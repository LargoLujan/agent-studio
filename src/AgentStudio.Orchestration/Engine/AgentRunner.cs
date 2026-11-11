using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
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
/// none), which <c>AgentRunStarted</c> version 2 records (docs/14 D-08 and D-23). <paramref name="ContextParts"/> are the
/// parts of its prompt before the agent starts (docs/17 D-13), which version 3 records: null to measure them from the prompt
/// and the skills of the request (<see cref="PromptParts.Measure"/>), as every launch of the engine does unless its builder
/// gives them.
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
    ContextPackReference? ContextPack = null,
    IReadOnlyList<ContextPartSnapshot>? ContextParts = null);

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
/// The start is version 3 (docs/17 D-13, ADR-0052): what the run is for and the memory it was given (version 2, docs/14
/// section 5.2), and the parts of its prompt in characters (<see cref="PromptParts"/>); a start without parts is version 2. The end
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
        var parts = invocation.ContextParts ?? PromptParts.Measure(invocation.Request.Prompt, invocation.Request.SkillsDirectory, invocation.Request.Agent.SystemInstructions);
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
                invocation.ContextPack,
                parts.Count > 0 ? parts : null)
            {
                RunId = recorder.RunValue,
                V = parts.Count > 0 ? AgentRunStartedPayload.ContextPartsVersion : AgentRunStartedPayload.PurposeVersion,
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

    /// <summary>The receipt of the journal entry of the attempt (docs/17 D-02), once it ended; null when the journal gave none.</summary>
    public JournalReceipt? Journal { get; private set; }

    /// <summary>
    /// Keeps the class of the end of <paramref name="outcome"/> (the adapter's, or the one its status implies) and the receipt
    /// of its journal entry.
    /// </summary>
    public void Ended(AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        End = outcome.Classified();
        Journal = outcome.Journal;
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
            Journal = Journal,
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

/// <summary>
/// The parts of a prompt before the agent starts (docs/17 D-13, ADR-0052), in characters: measured on the exact text the agent
/// receives, so the parts of the text add up to its length, plus what presenting its skills costs, which the CLI adds on its
/// own. No text is kept: only sizes.
/// <list type="bullet">
/// <item>A block of data (<c>&lt;project_brief&gt;</c>, <c>&lt;agent_instructions&gt;</c>, <c>&lt;project_memory&gt;</c>,
/// <c>&lt;upstream_output&gt;</c>, <c>&lt;previous_attempt&gt;</c>, <c>&lt;handoff&gt;</c> and <c>&lt;human_answer&gt;</c>) is its
/// part, from the line that opens it to the line that closes it. What goes inside is quoted by <c>PromptData</c>, which escapes
/// <c>&lt;</c>, so no line of data can open or close a block or start a section.</item>
/// <item>A section (a line that starts with <c>## </c>) goes with the first block it holds: its heading and the line that says
/// its blocks are data are part of it. A section whose heading starts with <see cref="RequirementsHeading"/> is the
/// requirements (the frozen ones a task covers, or the whole set the planner gets).</item>
/// <item>Everything else of the system (the title, the rules, the objective and the instructions of the task, the write scope,
/// the verification, the report asked for) is the protocol.</item>
/// <item>The skills are the characters of the name and the description of each <c>SKILL.md</c> materialized for the launch, which
/// is what the CLI presents of them; a folder or a file that cannot be read adds nothing, and links are not followed.</item>
/// <item>The instructions of the agent of its template are part of <see cref="ContextPartKind.AgentInstructions"/>: they are in
/// the context, and would otherwise be counted as conversation. An executor gets them only in its prompt, in the section with
/// the <c>&lt;agent_instructions&gt;</c> block (docs/17 D-42), which counts whole as that part; the planner gets them from the
/// adapter outside the prompt (<c>SystemInstructions</c>: Claude Code with <c>--append-system-prompt-file</c>, Codex before the
/// task), counted apart.</item>
/// </list>
/// A part without characters is left out, and the parts go in the order of <see cref="ContextPartKind"/>.
/// </summary>
internal static class PromptParts
{
    /// <summary>How the heading of a section of requirements starts.</summary>
    public const string RequirementsHeading = "## Requisitos";

    /// <summary>The most <c>SKILL.md</c> files read for one launch.</summary>
    public const int MaxSkills = 256;

    /// <summary>How much of the start of a <c>SKILL.md</c> is read to find its frontmatter.</summary>
    private const int FrontmatterWindow = 16 * 1024;

    private static readonly (string Tag, ContextPartKind Kind)[] Blocks =
    [
        ("project_brief", ContextPartKind.ProjectBrief),
        ("agent_instructions", ContextPartKind.AgentInstructions),
        ("project_memory", ContextPartKind.Memory),
        ("upstream_output", ContextPartKind.Upstream),
        ("previous_attempt", ContextPartKind.PreviousAttempt),
        ("handoff", ContextPartKind.PreviousAttempt),
        ("human_answer", ContextPartKind.HumanAnswer),
    ];

    private static readonly ContextPartKind[] Kinds = Enum.GetValues<ContextPartKind>();

    /// <summary>
    /// The parts of <paramref name="prompt"/>, of the skills materialized in <paramref name="skillsDirectory"/> (null for none)
    /// and of the instructions of the agent the adapter adds outside the prompt (<paramref name="agentInstructions"/>).
    /// </summary>
    public static IReadOnlyList<ContextPartSnapshot> Measure(string prompt, string? skillsDirectory, string? agentInstructions = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var sizes = new Dictionary<ContextPartKind, int>();
        void Add(ContextPartKind kind, int characters) => sizes[kind] = sizes.GetValueOrDefault(kind) + characters;

        foreach (var section in Sections(prompt))
        {
            var sectionKind = KindOf(section);
            string? open = null;
            var blockKind = ContextPartKind.Protocol;
            foreach (var line in section)
            {
                if (open is null && Opens(line) is { } opened)
                {
                    (open, blockKind) = opened;
                }

                if (open is null)
                {
                    Add(sectionKind, line.Length);
                    continue;
                }

                Add(blockKind, line.Length);
                if (line.Contains("</" + open + ">", StringComparison.Ordinal))
                {
                    open = null;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(agentInstructions))
        {
            Add(ContextPartKind.AgentInstructions, agentInstructions.Length);
        }

        if (SkillCharacters(skillsDirectory) is > 0 and var skills)
        {
            Add(ContextPartKind.Skills, skills);
        }

        return [.. Kinds.Where(k => sizes.GetValueOrDefault(k) > 0).Select(k => new ContextPartSnapshot(k, sizes[k]))];
    }

    /// <summary>The lines of the prompt, each with its end of line, cut where a section starts.</summary>
    private static List<List<string>> Sections(string prompt)
    {
        var sections = new List<List<string>>();
        var current = new List<string>();
        var start = 0;
        while (start < prompt.Length)
        {
            var end = prompt.IndexOf('\n', start);
            var line = end < 0 ? prompt[start..] : prompt[start..(end + 1)];
            start += line.Length;
            if (line.StartsWith("## ", StringComparison.Ordinal) && current.Count > 0)
            {
                sections.Add(current);
                current = [];
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            sections.Add(current);
        }

        return sections;
    }

    /// <summary>The part of a section: the requirements by their heading, else the part of its first block, else the protocol.</summary>
    private static ContextPartKind KindOf(List<string> section)
    {
        if (section[0].StartsWith(RequirementsHeading, StringComparison.Ordinal))
        {
            return ContextPartKind.Requirements;
        }

        return section.Select(Opens).FirstOrDefault(o => o is not null)?.Kind ?? ContextPartKind.Protocol;
    }

    /// <summary>The block a line opens (<c>&lt;tag</c> at its start, then a space, <c>&gt;</c> or its end), or null.</summary>
    private static (string Tag, ContextPartKind Kind)? Opens(string line)
    {
        if (line.Length < 2 || line[0] != '<')
        {
            return null;
        }

        foreach (var (tag, kind) in Blocks)
        {
            if (line.Length > tag.Length && string.CompareOrdinal(line, 1, tag, 0, tag.Length) == 0)
            {
                var next = line.Length > tag.Length + 1 ? line[tag.Length + 1] : '\n';
                if (next is ' ' or '>' or '\r' or '\n')
                {
                    return (tag, kind);
                }
            }
        }

        return null;
    }

    /// <summary>The characters of the names and descriptions of the skills under <paramref name="directory"/>; 0 when there are none or it cannot be read.</summary>
    private static int SkillCharacters(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 6,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        var total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "SKILL.md", options).Take(MaxSkills))
            {
                total += Presentation(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could be read counts; the folder is the materializer's and is not the evidence of anything.
        }

        return total;
    }

    /// <summary>The characters of the name and the description in the frontmatter of one <c>SKILL.md</c>.</summary>
    internal static int Presentation(string file)
    {
        string head;
        try
        {
            using var reader = new StreamReader(file);
            var buffer = new char[FrontmatterWindow];
            head = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var lines = head.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return 0;
        }

        var total = 0;
        var inValue = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                inValue = false;
                foreach (var key in (string[])["name:", "description:"])
                {
                    if (line.StartsWith(key, StringComparison.Ordinal))
                    {
                        var value = line[key.Length..].Trim().Trim('"', '\'');
                        total += value is ">" or "|" or ">-" or "|-" ? 0 : value.Length;
                        inValue = true;
                    }
                }
            }
            else if (inValue && line.Trim().Length > 0)
            {
                total += line.Trim().Length + 1;
            }
        }

        return total;
    }
}
