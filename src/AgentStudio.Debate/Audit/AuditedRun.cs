using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit;

/// <summary>An entry of the run with its typed payload: null for the kinds whose payload is free-form, or when it cannot be read (<see cref="Unreadable"/>).</summary>
internal sealed record RunItem(DecisionEntry Entry, DecisionPayload? Payload, bool Unreadable)
{
    public long Sequence => Entry.Sequence;

    public DecisionKind Kind => Entry.Kind;
}

/// <summary>
/// One run as the audit reads it: its entries of the verified log with their payloads, and the material it was started with
/// (plan, header, requirements) only when it is what <c>RunStarted</c> anchored. What is not trusted is never used: the
/// checks that need it are left out, and the reason is a finding (<see cref="PlanProblem"/>, <see cref="HeaderProblem"/>,
/// <see cref="RequirementsProblem"/>).
/// </summary>
internal sealed class AuditedRun
{
    private readonly IReadOnlyDictionary<EvidenceId, ReadOnlyMemory<byte>> _contents;
    private readonly Dictionary<TaskId, HashSet<TaskId>> _ancestors = [];

    private AuditedRun(OrchestratorAuditInput input, IReadOnlyList<DecisionEntry> entries, IReadOnlyList<RunItem> items, RunItem started)
    {
        Run = input.Run;
        Entries = entries;
        Items = items;
        StartedItem = started;
        Started = (RunStartedPayload)started.Payload!;
        _contents = input.Evidence;

        Roster = Started.Roster.GroupBy(m => m.Agent).ToDictionary(g => g.Key, g => g.First());
        foreach (var item in items)
        {
            if (item.Payload is TaskCreatedPayload created)
            {
                CreatedKinds.TryAdd(created.Task, created.Kind);
                CreatedTasks.Add(created.Task);
            }
        }

        TrustPlan(input.Plan);
        TrustHeader(input.Header);
        TrustRequirements(input.Requirements);
        CreatedAt = Header?.CreatedAt ?? started.Entry.RecordedAt;
    }

    public RunId Run { get; }

    /// <summary>The entries of the run in the verified log (<see cref="RunLog.EntriesOf"/>).</summary>
    public IReadOnlyList<DecisionEntry> Entries { get; }

    public IReadOnlyList<RunItem> Items { get; }

    public RunItem StartedItem { get; }

    public RunStartedPayload Started { get; }

    /// <summary>The roster <c>RunStarted</c> anchored: agent, role, provider and vendor, always trusted.</summary>
    public IReadOnlyDictionary<AgentId, RosterEntry> Roster { get; }

    /// <summary>The kind of each task as its <c>TaskCreated</c> recorded it.</summary>
    public Dictionary<TaskId, WorkTaskKind> CreatedKinds { get; } = [];

    /// <summary>The tasks the log created, in log order.</summary>
    public List<TaskId> CreatedTasks { get; } = [];

    /// <summary>The plan, when it is the one <c>RunStarted</c> anchored; null otherwise.</summary>
    public PlanDefinition? Plan { get; private set; }

    /// <summary>The tasks of the trusted plan by id; empty without one.</summary>
    public IReadOnlyDictionary<TaskId, TaskSpec> Specs { get; private set; } = new Dictionary<TaskId, TaskSpec>();

    public string? PlanProblem { get; private set; }

    /// <summary>The stored header, when it hashes to what <c>RunStarted</c> anchored; null otherwise.</summary>
    public RunRecord? Header { get; private set; }

    /// <summary>The definitions of the agents of the trusted header by id; empty without one.</summary>
    public IReadOnlyDictionary<AgentId, AgentDefinition> Definitions { get; private set; } = new Dictionary<AgentId, AgentDefinition>();

    public string? HeaderProblem { get; private set; }

    public Severity HeaderProblemSeverity { get; private set; } = Severity.High;

    /// <summary>Whether the stored header hashes to something else than what <c>RunStarted</c> anchored (version 2 on).</summary>
    public bool HeaderMismatch { get; private set; }

    /// <summary>
    /// The frozen verification profile of the run (ADR-0016), when the stored one hashes to the
    /// <c>VerificationProfileHash</c> that <c>RunStarted</c> anchored (every version anchors it); null otherwise. It says
    /// what each verification step of the plan captures, so what the run was required to verify.
    /// </summary>
    public VerificationProfile? Profile { get; private set; }

    /// <summary>Why the stored profile is not used, when the header problem does not say it already.</summary>
    public string? ProfileProblem { get; private set; }

    /// <summary>The tasks of the trusted plan in an order where every task comes after its prerequisites; empty without one.</summary>
    public IReadOnlyList<TaskId> TopologicalOrder { get; private set; } = [];

    /// <summary>The requirements, when they are the ones <c>RunStarted</c> anchored; null otherwise.</summary>
    public FrozenRequirementSet? Requirements { get; private set; }

    public string? RequirementsProblem { get; private set; }

    /// <summary>When the run started: the trusted header's time (the one a resume replays with), or that of <c>RunStarted</c>.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <exception cref="InvalidOperationException">The log has no <c>RunStarted</c> for the run: there is no run to audit.</exception>
    public static AuditedRun Read(OrchestratorAuditInput input)
    {
        var entries = RunLog.EntriesOf(input.Log.Entries, input.Run.Value);
        var items = entries.Select(Parse).ToList();
        var started = items.FirstOrDefault(i => i.Kind == DecisionKind.RunStarted && i.Payload is RunStartedPayload)
            ?? throw new InvalidOperationException($"Run '{input.Run}' has no readable RunStarted in the decision log: there is no run to audit.");
        return new AuditedRun(input, entries, items, started);
    }

    public static RunItem Parse(DecisionEntry entry)
    {
        try
        {
            return new RunItem(entry, DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson), Unreadable: false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new RunItem(entry, null, Unreadable: true);
        }
    }

    /// <summary>An agent run for a task: an executor (the planner and the judge carry no task; version 1 carries no purpose).</summary>
    public static bool IsExecutorLaunch(AgentRunStartedPayload started) =>
        started.Task is not null && started.Purpose is null or AgentRunPurpose.Executor;

    /// <summary>Whether a registered evidence may be an attempt record: system-captured document of a task titled as one (version 1 anchors no title).</summary>
    public static bool MayBeAttemptRecord(EvidenceRegisteredPayload evidence) =>
        evidence is { Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem, Task: not null }
        && (evidence.Title is null || evidence.Title.StartsWith(OrchestratorAudit.AttemptRecordTitlePrefix + " ", StringComparison.Ordinal));

    /// <summary>
    /// The task the engine dispatches at a step of the run, and how the log says it: its transition out of pending (to
    /// running, to awaiting approval, or to blocked when the dispatch could not go on), its instructions, or the launch of
    /// its agent (which <c>AgentRunner</c> records before the task is running). The replayer checks readiness at the same
    /// transitions.
    /// </summary>
    public static bool IsStart(RunItem item, out TaskId task, out string signal)
    {
        switch (item.Payload)
        {
            case TaskStatusChangedPayload { From: WorkTaskStatus.Pending, To: WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked } change:
                task = change.Task;
                signal = "TaskStatusChanged Pending → " + change.To;
                return true;
            case InstructionsIssuedPayload instructions:
                task = instructions.Task;
                signal = "InstructionsIssued";
                return true;
            case AgentRunStartedPayload { Task: { } launched }:
                task = launched;
                signal = "AgentRunStarted";
                return true;
            default:
                task = default;
                signal = string.Empty;
                return false;
        }
    }

    /// <summary>The task named in the free-form payload of an entry (<c>PermissionDenied</c>, <c>ReviewSkipped</c>), if it names one.</summary>
    public static IReadOnlyList<TaskId> FreeFormTasks(DecisionEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("task", out var task)
                && task.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(task.GetString())
                ? [new TaskId(task.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The kind of a task: from the trusted plan, else from its <c>TaskCreated</c>; null when neither knows it.</summary>
    public WorkTaskKind? KindOf(TaskId task) =>
        Specs.TryGetValue(task, out var spec) ? spec.Kind : CreatedKinds.TryGetValue(task, out var kind) ? kind : null;

    /// <summary>Whether <paramref name="ancestor"/> comes before <paramref name="task"/> in the trusted plan (through any chain of dependencies).</summary>
    public bool IsAncestor(TaskId ancestor, TaskId task) => AncestorsOf(task).Contains(ancestor);

    /// <summary>The tasks of the trusted plan that come after <paramref name="task"/>.</summary>
    public IReadOnlySet<TaskId> DescendantsOf(TaskId task) => Specs.Keys.Where(t => IsAncestor(task, t)).ToHashSet();

    /// <summary>
    /// The content of a piece of evidence as the store returned it, only when it hashes to <paramref name="sha256"/>, the
    /// SHA-256 the log anchored for it (R3-08); otherwise why it cannot be used, to follow «evidence X».
    /// </summary>
    public bool TryContent(EvidenceId evidence, string sha256, out ReadOnlyMemory<byte> content, out string problem)
    {
        if (!_contents.TryGetValue(evidence, out content))
        {
            problem = "cannot be read: the store did not open its content";
            return false;
        }

        var actual = ContentHash.Sha256Hex(content.Span);
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"does not hash to {sha256}, the SHA-256 the log anchored, but to {actual}: it was rewritten after it was captured";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>The text of a piece of evidence, read as <see cref="TryContent"/> reads it.</summary>
    public bool TryText(EvidenceId evidence, string sha256, out string text, out string problem)
    {
        if (TryContent(evidence, sha256, out var content, out problem))
        {
            text = Encoding.UTF8.GetString(content.Span);
            return true;
        }

        text = string.Empty;
        return false;
    }

    private HashSet<TaskId> AncestorsOf(TaskId task)
    {
        if (_ancestors.TryGetValue(task, out var known))
        {
            return known;
        }

        var found = new HashSet<TaskId>();
        var pending = new Stack<TaskId>(Prerequisites(task));
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (found.Add(next))
            {
                foreach (var further in Prerequisites(next))
                {
                    pending.Push(further);
                }
            }
        }

        _ancestors[task] = found;
        return found;
    }

    private IEnumerable<TaskId> Prerequisites(TaskId task) =>
        Specs.TryGetValue(task, out var spec) ? spec.DependsOn.Select(d => d.Prerequisite).Where(p => p != task) : [];

    private void TrustPlan(PlanDefinition? plan)
    {
        var anchored = Started.PlanHash;
        if (plan is null)
        {
            PlanProblem = FormattableString.Invariant(
                $"The plan {anchored} that RunStarted (entry {StartedItem.Sequence}) anchored is not in the store: the run cannot be replayed, nor checked against its plan.");
            return;
        }

        if (!string.Equals(plan.ContentHash, anchored, StringComparison.Ordinal))
        {
            PlanProblem = FormattableString.Invariant(
                $"The stored plan hashes to {plan.ContentHash}, but RunStarted (entry {StartedItem.Sequence}) anchored {anchored}: the run cannot be replayed, nor checked against its plan, and the stored one is not used.");
            return;
        }

        Plan = plan;
        Specs = plan.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        TopologicalOrder = Topological(plan.Tasks);
    }

    /// <summary>Kahn's algorithm, ties in the order of the plan (as <c>TaskGraph</c> does); a task in a cycle goes last.</summary>
    private static List<TaskId> Topological(IReadOnlyList<TaskSpec> tasks)
    {
        var known = tasks.Select(t => t.Id).ToHashSet();
        var waiting = tasks.ToDictionary(t => t.Id, t => t.DependsOn.Select(d => d.Prerequisite).Where(p => p != t.Id && known.Contains(p)).ToHashSet());
        var order = new List<TaskId>();
        while (order.Count < tasks.Count)
        {
            var next = tasks.Select(t => t.Id).FirstOrDefault(id => !order.Contains(id) && waiting[id].All(order.Contains));
            if (next == default)
            {
                order.AddRange(tasks.Select(t => t.Id).Where(id => !order.Contains(id)));
                break;
            }

            order.Add(next);
        }

        return order;
    }

    private void TrustHeader(RunRecord? header)
    {
        TrustProfile(header);
        if (Started.HeaderHash is not { } anchored)
        {
            HeaderProblemSeverity = Severity.Medium;
            HeaderProblem = FormattableString.Invariant(
                $"RunStarted (entry {StartedItem.Sequence}) is version {Started.V}, which anchors no hash of the run header: the definitions of its agents (review policies, permissions) come from the store alone and are not used; its verification profile is used only as RunStarted anchored it.");
            return;
        }

        if (header is null)
        {
            HeaderProblem = FormattableString.Invariant(
                $"The header of run {Run} is not in the store, so the definitions of its agents and its verification profile, which RunStarted (entry {StartedItem.Sequence}) anchored as {anchored}, cannot be checked.");
            return;
        }

        var stored = RunHeaderHash.Of(header);
        if (!string.Equals(stored, anchored, StringComparison.Ordinal))
        {
            HeaderMismatch = true;
            HeaderProblem = FormattableString.Invariant(
                $"The stored header of run {Run} hashes to {stored}, but RunStarted (entry {StartedItem.Sequence}) anchored {anchored}: its roster, review policies, options or verification profile changed after the start, and are not used.");
            return;
        }

        Header = header;
        Definitions = header.Roster.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
    }

    private void TrustProfile(RunRecord? header)
    {
        if (header is null)
        {
            return; // the problem of the header says that nothing of it can be checked
        }

        var anchored = Started.VerificationProfileHash;
        var stored = header.Verification.ContentHash;
        if (string.Equals(stored, anchored, StringComparison.Ordinal))
        {
            Profile = header.Verification;
            return;
        }

        ProfileProblem = FormattableString.Invariant(
            $"The stored verification profile of run {Run} hashes to {stored}, but RunStarted (entry {StartedItem.Sequence}) anchored {anchored}: what the run was required to verify is not known, and the stored profile is not used.");
    }

    private void TrustRequirements(FrozenRequirementSet? requirements)
    {
        var anchored = Started.RequirementSetHash;
        if (requirements is null)
        {
            RequirementsProblem = FormattableString.Invariant(
                $"The requirements {anchored} that RunStarted (entry {StartedItem.Sequence}) anchored are not in the store: the requirements quoted in the prompts of the executors cannot be checked.");
            return;
        }

        if (!string.Equals(requirements.ContentHash, anchored, StringComparison.Ordinal))
        {
            RequirementsProblem = FormattableString.Invariant(
                $"The stored requirements hash to {requirements.ContentHash}, but RunStarted (entry {StartedItem.Sequence}) anchored {anchored}: the requirements quoted in the prompts of the executors cannot be checked.");
            return;
        }

        Requirements = requirements;
    }
}

/// <summary>The status of every task as the log has said so far, and the branch each decided condition took.</summary>
internal sealed class TaskStatuses
{
    private readonly Dictionary<TaskId, WorkTaskStatus> _status = [];
    private readonly Dictionary<TaskId, string> _taken = [];

    public WorkTaskStatus Of(TaskId task) => _status.GetValueOrDefault(task, WorkTaskStatus.Pending);

    public string? TakenBranch(TaskId condition) => _taken.GetValueOrDefault(condition);

    public void Apply(RunItem item)
    {
        switch (item.Payload)
        {
            case TaskStatusChangedPayload change:
                _status[change.Task] = change.To;
                break;
            case ConditionEvaluatedPayload condition:
                _taken[condition.Task] = condition.Branch;
                break;
        }
    }
}

/// <summary>The findings of one pass, in the order they were found; the same code and message only once.</summary>
internal sealed class AuditFindings
{
    private readonly List<OrchestratorFinding> _found = [];
    private readonly HashSet<(string Code, string Message)> _seen = [];

    public IReadOnlyList<OrchestratorFinding> All => _found;

    public void Add(string code, Severity severity, IEnumerable<TaskId> tasks, IEnumerable<long> entries, string message)
    {
        if (_seen.Add((code, message)))
        {
            _found.Add(new OrchestratorFinding(code, severity, [.. tasks.Distinct()], [.. entries.Distinct().Order()], message));
        }
    }
}
