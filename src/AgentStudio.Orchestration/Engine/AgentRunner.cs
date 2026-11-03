using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// One agent launch: who, through which (composed) provider, with which request, for which task and attempt, what it is
/// for (<paramref name="Purpose"/>) and the project memory its prompt was given (<paramref name="ContextPack"/>, null for
/// none), which <c>AgentRunStarted</c> version 2 records (docs/14 D-08 and D-23).
/// </summary>
internal sealed record AgentInvocation(
    AgentDefinition Agent,
    IAgentProvider Provider,
    AgentRunRequest Request,
    TaskId? Task,
    int Attempt,
    EvidenceRecord Prompt,
    IReadOnlyList<SkillId> Skills,
    string ReportTitle,
    AgentRunPurpose Purpose,
    ContextPackReference? ContextPack = null);

/// <summary>
/// How a launch went. <see cref="Launched"/> is false when the user's repository had already changed
/// (<see cref="RepositoryDifferences"/>, nothing was launched or recorded) or when <c>StartAsync</c> failed
/// (<see cref="LaunchError"/>).
/// </summary>
internal sealed record AgentInvocationResult(
    bool Launched,
    Exception? LaunchError,
    AgentRunOutcome? Outcome,
    EvidenceRecord? Report,
    IReadOnlyList<string> RepositoryDifferences,
    string? SessionId,
    DateTimeOffset? RateLimitResetsAt);

/// <summary>
/// Runs one agent the way every agent run in Phase 3 must go (docs/13 sections 4.2 and 4.7, ADR-0013):
/// <list type="number">
/// <item>the user's repository is checked against the baseline, and nothing is launched if it already changed;</item>
/// <item><c>AgentRunStarted</c> is recorded before the launch (so a launch can never be repeated silently after a restart);</item>
/// <item>the composed provider starts the run, waiting in its queue if needed;</item>
/// <item>the events are read to the end, and the run is cancelled when the caller asks (stall, timeout, user);</item>
/// <item>the user's repository is checked against the baseline again;</item>
/// <item>the structured output is captured as evidence reported by the agent, and <c>AgentRunFinished</c> is recorded with it.</item>
/// </list>
/// The start is version 2 (docs/14 section 5.2, ADR-0019): it says what the run is for and the memory it was given. The end
/// is version 3 (docs/16 D-41): the usage with origins and breakdown, the time the process was launched (after the
/// provider's queue), the model of the steps (never the one the session announced, D-13), the session it resumed (D-02),
/// the step with the largest context with the window of its model, the compactions (null when the provider does not report
/// context: not exposed, never zero) and the class of the end, as the adapter recognised it in its CLI or, when it did not,
/// the one the status implies.
/// </summary>
internal static class AgentRunner
{
    /// <param name="invocation">What to run.</param>
    /// <param name="recorder">The run's log and evidence.</param>
    /// <param name="guard">Checks the user's repository against the baseline of the run.</param>
    /// <param name="onRecorded">Called once <c>AgentRunStarted</c> is in the log (the attempt now counts against the budget).</param>
    /// <param name="onStarted">Called once <c>StartAsync</c> returned (the task may become Running only now).</param>
    /// <param name="onEvent">Called for every event (the heartbeat of the abandonment monitor).</param>
    /// <param name="stop">Cancelled when the caller wants the run stopped; the run is cancelled and its outcome still recorded.</param>
    /// <param name="abort">Cancelled when the app closes: the run is disposed (its process tree killed) and nothing more is recorded.</param>
    /// <param name="time">The clock of the launch time.</param>
    public static async Task<AgentInvocationResult> RunAsync(
        AgentInvocation invocation,
        RunRecorder recorder,
        UserRepositoryGuard guard,
        Action? onRecorded,
        Action? onStarted,
        Action<AgentEvent>? onEvent,
        CancellationToken stop,
        CancellationToken abort,
        TimeProvider time)
    {
        var before = await guard.CheckAsync(abort).ConfigureAwait(false);
        if (before.Count > 0)
        {
            return new AgentInvocationResult(false, null, null, null, before, null, null);
        }

        var agent = invocation.Agent;
        await recorder.RecordAsync(
            DecisionKind.AgentRunStarted,
            $"Arranca {agent.Id} ({invocation.Provider.Provider}) para {invocation.Task?.Value ?? "el plan"}, intento {invocation.Attempt}",
            new AgentRunStartedPayload(
                invocation.Task,
                invocation.Attempt,
                agent.Id,
                invocation.Provider.Provider,
                invocation.Provider.Vendor,
                agent.Model.Model,
                invocation.Request.WorkingDirectory,
                invocation.Prompt.Id,
                invocation.Prompt.ContentSha256,
                invocation.Skills,
                invocation.Purpose,
                invocation.ContextPack)
            {
                RunId = recorder.RunValue,
                V = AgentRunStartedPayload.PurposeVersion,
            },
            abort).ConfigureAwait(false);
        onRecorded?.Invoke();

        IAgentRun run;
        using (var launch = CancellationTokenSource.CreateLinkedTokenSource(stop, abort))
        {
            try
            {
                // The wait in the provider's queue honours both tokens: a run stopped while queued never starts. The request
                // carries the start just recorded, so the journal can name it (docs/17 D-02).
                var anchored = invocation.Request with { Anchor = new AgentRunAnchor(recorder.RunValue, invocation.Task?.Value, invocation.Attempt) };
                run = await invocation.Provider.StartAsync(anchored, launch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (stop.IsCancellationRequested)
            {
                await RecordFinishedAsync(recorder, invocation, AttemptOutcome.Cancelled, null, null, "Se canceló antes de arrancar.", null, new RunObservations(invocation, null), abort).ConfigureAwait(false);
                return new AgentInvocationResult(false, ex, null, null, [], null, null);
            }
            catch (Exception ex)
            {
                await RecordFinishedAsync(recorder, invocation, AttemptOutcome.Failed, null, null, "No se pudo lanzar el agente: " + ex.Message, null, new RunObservations(invocation, null), abort).ConfigureAwait(false);
                return new AgentInvocationResult(false, ex, null, null, [], null, null);
            }
        }

        // After the provider's queue: the duration of the attempt runs from here (docs/14 D-04).
        var seen = new RunObservations(invocation, time.GetUtcNow());
        await using (run.ConfigureAwait(false))
        {
            onStarted?.Invoke();
            using var cancelOnStop = stop.Register(() => _ = run.CancelAsync());

            try
            {
                await foreach (var agentEvent in run.Events.WithCancellation(abort).ConfigureAwait(false))
                {
                    seen.Observe(agentEvent);
                    onEvent?.Invoke(agentEvent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !abort.IsCancellationRequested)
            {
                // A broken event stream does not decide the outcome: the completion does.
            }

            var outcome = await run.Completion.WaitAsync(abort).ConfigureAwait(false);
            seen.Ended(outcome);
            var sessionId = outcome.SessionId ?? seen.SessionId;
            var differences = await guard.CheckAsync(abort).ConfigureAwait(false);

            EvidenceRecord? report = null;
            if (!string.IsNullOrWhiteSpace(outcome.StructuredOutputJson))
            {
                report = await recorder.CaptureAsync(
                    EvidenceKind.AgentStatement,
                    EvidenceOrigin.ReportedByAgent,
                    invocation.ReportTitle,
                    agent.Id.Value,
                    outcome.StructuredOutputJson,
                    "application/json",
                    invocation.Task,
                    invocation.Attempt,
                    abort).ConfigureAwait(false);
            }

            await RecordFinishedAsync(
                recorder,
                invocation,
                AttemptOutcomes.From(outcome.Status),
                outcome.ExitCode,
                sessionId,
                outcome.Error,
                report?.Id,
                seen,
                abort).ConfigureAwait(false);
            return new AgentInvocationResult(true, null, outcome, report, differences, sessionId, seen.RateLimitResetsAt);
        }
    }

    /// <summary>Records <c>AgentRunFinished</c> version 3 with what the events and the outcome of the attempt said (<paramref name="seen"/>).</summary>
    public static Task<DecisionEntry> RecordFinishedAsync(
        RunRecorder recorder,
        AgentInvocation invocation,
        AttemptOutcome status,
        int? exitCode,
        string? sessionId,
        string? error,
        EvidenceId? report,
        RunObservations seen,
        CancellationToken cancellationToken) =>
        recorder.RecordAsync(
            DecisionKind.AgentRunFinished,
            $"Termina {invocation.Agent.Id} para {invocation.Task?.Value ?? "el plan"}, intento {invocation.Attempt}: {status}",
            seen.Finished(recorder.RunValue, invocation.Task, invocation.Attempt, status, exitCode, sessionId, error, report),
            cancellationToken);

    /// <summary>Records the audit alert of a step that found the user's repository changed, with every difference.</summary>
    public static Task<DecisionEntry> RecordRepositoryAlertAsync(RunRecorder recorder, TaskId? task, IReadOnlyList<string> differences, CancellationToken cancellationToken) =>
        recorder.RecordAsync(
            DecisionKind.AuditAlertRaised,
            "El repositorio del usuario cambió durante la ejecución",
            new AuditAlertPayload(
                "user-repository.changed",
                task is { } t ? [t] : [],
                "The user's repository changed while the run was going on: " + string.Join(" ", differences)) { RunId = recorder.RunValue },
            cancellationToken);

    /// <summary>The read-only permissions of the planner (and of any agent that must not change files).</summary>
    public static PermissionProfile ReadOnly(IReadOnlyList<string> allowedTools) =>
        new(false, false, false, allowedTools, [], []);
}

/// <summary>
/// What the events of one attempt said about it, as <c>AgentRunFinished</c> version 3 records it (docs/14 section 5.2 and
/// docs/16 D-41): the session, the last usage, when a rate limit resets, the model of the last step, the step with the
/// largest context, the compactions and, once the attempt ended, the class of its end.
/// </summary>
/// <param name="launchedAt">When the process was launched, after the provider's queue; null when it never was.</param>
internal sealed class RunObservations(AgentInvocation invocation, DateTimeOffset? launchedAt)
{
    private readonly bool _reportsContext = invocation.Provider.Capabilities.ReportsContextOccupancy;
    private int _compactions;
    private string? _lastStepModel;

    public string? SessionId { get; private set; }

    public TokenUsage? Usage { get; private set; }

    public DateTimeOffset? RateLimitResetsAt { get; private set; }

    /// <summary>
    /// The class of the end the outcome of the attempt carries (<see cref="AgentRunOutcome.Classified"/>); null until the
    /// attempt ended, and for an attempt that never ran, whose end is the one its status implies.
    /// </summary>
    public AgentRunEnd? End { get; private set; }

    /// <summary>Keeps the class of the end of <paramref name="outcome"/>: the adapter's, or the one its status implies.</summary>
    public void Ended(AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        End = outcome.Classified();
    }

    /// <summary>The step with the largest prompt (the first of equals).</summary>
    public ContextObserved? Peak { get; private set; }

    /// <summary>The model of the last step or, without steps, the only model of the breakdown; never the one of <see cref="SessionStarted"/> (D-13).</summary>
    public string? ReportedModel => _lastStepModel ?? (Usage?.PerModel is [var only] ? only.Model : null);

    public void Observe(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case SessionStarted started:
                SessionId = started.SessionId;
                break;
            case UsageReported reported:
                Usage = reported.Usage;
                break;
            case RateLimitSignaled limited:
                RateLimitResetsAt = limited.ResetsAt ?? RateLimitResetsAt;
                break;
            case ContextObserved step:
                _lastStepModel = step.Model ?? _lastStepModel;
                if (step.PromptTokens.Value is { } tokens && (Peak?.PromptTokens.Value is not { } largest || tokens > largest))
                {
                    Peak = step;
                }

                break;
            case CompactionObserved:
                _compactions++;
                break;
        }
    }

    /// <summary>
    /// The end of the attempt, version 3: with the class of its outcome (<see cref="Ended"/>) or, for an attempt that never
    /// ran (cancelled before it started, a launch that failed), the one <paramref name="status"/> implies.
    /// </summary>
    public AgentRunFinishedPayload Finished(string runId, TaskId? task, int attempt, AttemptOutcome status, int? exitCode, string? sessionId, string? error, EvidenceId? report) =>
        new(
            task,
            attempt,
            status,
            exitCode,
            sessionId,
            error,
            Usage is null ? null : UsageSnapshot.WithBreakdown(Usage),
            report,
            launchedAt,
            ReportedModel,
            string.IsNullOrWhiteSpace(invocation.Request.ResumeSessionId) ? null : invocation.Request.ResumeSessionId,
            PeakSnapshot(),
            _reportsContext ? _compactions : null,
            Usage?.Turns,
            End is { } end ? AttemptEndSnapshot.From(end) : AttemptEndSnapshot.ImpliedBy(status))
        {
            RunId = runId,
            V = AgentRunFinishedPayload.EndClassVersion,
        };

    /// <summary>
    /// The largest step with the window of its model. A CLI reports the window when the run ends (Claude's
    /// <c>modelUsage</c>), after the step was observed without it: the window of the breakdown, when it has one for that
    /// model, is the one recorded.
    /// </summary>
    private ContextPeakSnapshot? PeakSnapshot()
    {
        if (Peak is not { } peak)
        {
            return null;
        }

        var window = WindowOf(peak.Model) ?? peak.ContextWindow;
        return new ContextPeakSnapshot(peak.Model, MetricSnapshot.WithOrigin(peak.PromptTokens), MetricSnapshot.WithOrigin(window));
    }

    private MetricValue<long>? WindowOf(string? model)
    {
        if (model is null || Usage is not { } usage)
        {
            return null;
        }

        var match = usage.PerModel.FirstOrDefault(m => m.Model == model)
            ?? usage.PerModel.FirstOrDefault(m => m.Model.StartsWith(model, StringComparison.Ordinal) || model.StartsWith(m.Model, StringComparison.Ordinal));
        return match is { ContextWindow.HasValue: true } ? match.ContextWindow : null;
    }
}
