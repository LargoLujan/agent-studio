using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Where the launches of one judge are recorded (docs/15 D-23 and D-44): the project and the unit of the log (a run, a
/// debate or an audit), the task of the entries <c>AgentRunStarted</c> and <c>AgentRunFinished</c> (null for the review of a
/// run, whose judge works for the review and not for a task attempt, docs/04 section 11; <c>juez-&lt;agent&gt;</c> for a
/// debate and an audit, so the two judges of a panel and their corrections each have their own key), the task their
/// evidence belongs to, and the purpose the usage panel groups them by.
/// </summary>
public sealed record JudgeRunScope(string ProjectId, string Run, TaskId? EntryTask, string? EvidenceTask, AgentRunPurpose Purpose)
{
    /// <summary>The prefix of the task of the judge of a debate or an audit (D-44).</summary>
    public const string JudgeTaskPrefix = "juez-";

    /// <summary>The task of the judge <paramref name="judge"/> in a debate or an audit: <c>juez-&lt;agent&gt;</c>.</summary>
    public static TaskId JudgeTask(AgentId judge) => new(JudgeTaskPrefix + judge.Value);

    /// <summary>The review of an orchestration run, as Phase 3 records it: no task in the entries, the review task in the evidence.</summary>
    public static JudgeRunScope ForRunReview(string projectId, string run, TaskId reviewTask) =>
        new(projectId, run, null, reviewTask.Value, AgentRunPurpose.Judge);

    /// <summary>The judge <paramref name="judge"/> of a debate (<c>deb-…</c>), with its own task.</summary>
    public static JudgeRunScope ForDebate(string projectId, string debate, AgentId judge) =>
        new(projectId, debate, JudgeTask(judge), JudgeTask(judge).Value, AgentRunPurpose.DebateJudge);

    /// <summary>The judge <paramref name="judge"/> of a whole-project audit (<c>aud-…</c>), with its own task.</summary>
    public static JudgeRunScope ForAudit(string projectId, string audit, AgentId judge) =>
        new(projectId, audit, JudgeTask(judge), JudgeTask(judge).Value, AgentRunPurpose.ProjectAudit);
}

/// <summary>
/// Records each launch of a judge in the decision log: the prompt is captured as evidence first, then
/// <c>AgentRunStarted</c> version 2 (so a launch that cannot be recorded does not happen), and <c>AgentRunFinished</c>
/// version 3 with the raw answer as agent-reported evidence and the class of the end (docs/16 D-41: the adapter's, or the
/// one the status implies). It moved out of <see cref="IndependentReview"/> (docs/15 D-23) without changing what the review
/// records, so the judgment of a debate and the audit record their judges the same way, each with its
/// <see cref="JudgeRunScope"/>.
/// </summary>
/// <remarks>
/// The judge reports every event of its run to its progress, which is this recorder (it passes them on to
/// <paramref name="next"/>): from them it takes what version 3 records besides the usage and the outcome the judge hands back
/// (<see cref="AgentRunObservations"/>). <c>LaunchedAt</c> is when the first event of the run came:
/// <see cref="AgentJudge"/> says nothing when its provider's queue ends, so it is the moment the run was seen running, at
/// most the start-up of the CLI later than the launch. The reported model is that of the last step, never the model of
/// <see cref="SessionStarted"/> that <see cref="JudgeRunEnd.Model"/> carries (docs/14 D-13).
/// </remarks>
public sealed class JudgeRunRecorder(
    IDecisionRecorder decisions,
    IEvidenceStore evidence,
    JudgeRunScope scope,
    AgentDefinition agent,
    TimeProvider time,
    bool reportsContext,
    IProgress<AgentEvent>? next = null) : IJudgeRunObserver, IProgress<AgentEvent>
{
    /// <summary>The actor and producer the system records itself as.</summary>
    public const string SystemId = "agent-studio";

    private static readonly Actor SystemActor = new(ActorKind.System, SystemId);

    private readonly Lock _gate = new();
    private AgentRunObservations _current = new(reportsContext);

    public JudgeRunScope Scope => scope;

    public void Report(AgentEvent value)
    {
        lock (_gate)
        {
            _current.Observe(value, time.GetUtcNow());
        }

        next?.Report(value);
    }

    public async Task RunStartingAsync(JudgeRunStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        var resumed = start.Request.ResumeSessionId;
        lock (_gate)
        {
            _current = new AgentRunObservations(reportsContext, string.IsNullOrWhiteSpace(resumed) ? null : resumed);
        }

        var prompt = await CaptureAsync(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Prompt del juez (intento {start.Attempt})",
            start.Request.Prompt, start.Attempt, "text/markdown", cancellationToken).ConfigureAwait(false);
        await decisions.RecordAsync(
            scope.ProjectId,
            SystemActor,
            DecisionKind.AgentRunStarted,
            $"Juez '{agent.Id}' lanzado (intento {start.Attempt})",
            new AgentRunStartedPayload(scope.EntryTask, start.Attempt, agent.Id, start.Provider, start.Vendor, agent.Model.Model, start.Request.WorkingDirectory, prompt.Id, prompt.ContentSha256, [], scope.Purpose, Effort: agent.Effort)
            {
                RunId = scope.Run,
                V = agent.Effort is null ? AgentRunStartedPayload.PurposeVersion : AgentRunStartedPayload.ContextPartsVersion,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The start <see cref="RunStartingAsync"/> records: the run of the scope, its entry task and the attempt (docs/17 D-02).</summary>
    public AgentRunAnchor? AnchorOf(JudgeRunStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return new AgentRunAnchor(scope.Run, scope.EntryTask?.Value, start.Attempt);
    }

    public async Task RunFinishedAsync(JudgeRunEnd end, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(end);
        EvidenceId? answer = null;
        var text = end.Outcome.StructuredOutputJson ?? end.Outcome.FinalText;
        if (!string.IsNullOrEmpty(text))
        {
            answer = (await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Respuesta del juez (intento {end.Attempt})",
                text, end.Attempt, "application/json", cancellationToken).ConfigureAwait(false)).Id;
        }

        AgentRunObservations seen;
        lock (_gate)
        {
            seen = _current;
        }

        await decisions.RecordAsync(
            scope.ProjectId,
            SystemActor,
            DecisionKind.AgentRunFinished,
            $"Juez '{agent.Id}' terminado: {end.Outcome.Status} (intento {end.Attempt})",
            new AgentRunFinishedPayload(
                scope.EntryTask,
                end.Attempt,
                AttemptOutcomes.From(end.Outcome.Status),
                end.Outcome.ExitCode,
                end.Outcome.SessionId,
                end.Outcome.Error,
                end.Usage is null ? null : UsageSnapshot.WithBreakdown(end.Usage),
                answer,
                seen.LaunchedAt,
                seen.ReportedModel(end.Usage),
                seen.ResumedSession,
                seen.PeakSnapshot(end.Usage),
                seen.Compactions,
                end.Usage?.Turns,
                AttemptEndSnapshot.From(end.Outcome.Classified()))
            {
                RunId = scope.Run,
                V = AgentRunFinishedPayload.EndClassVersion,
                Journal = end.Outcome.Journal,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<EvidenceRecord> CaptureAsync(EvidenceKind kind, EvidenceOrigin origin, string title, string content, int attempt, string mediaType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await evidence.CaptureAsync(
            scope.ProjectId,
            new EvidenceDraft(kind, origin, title, origin == EvidenceOrigin.CapturedBySystem ? SystemId : agent.Id.Value,
                RunId: scope.Run, TaskId: scope.EvidenceTask, Attempt: attempt, MediaType: mediaType),
            stream,
            cancellationToken).ConfigureAwait(false);
    }
}
