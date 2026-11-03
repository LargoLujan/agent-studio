using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration;

/// <summary>Limits the validator enforces. Debate nodes are not available until Phase 5.</summary>
public sealed record PlanLimits(int MaxTasks = 20, int MaxRetriesPerTask = 3, bool DebateNodesAvailable = false);

/// <summary>Everything a plan is checked against: the frozen requirements, the agents, and the trusted verification profile.</summary>
/// <param name="TestRunSteps">
/// The steps of the profile whose evidence is a test run (<c>EvidenceKind.TestRun</c>). With it, a task that writes code
/// for an agent whose policy asks for system-captured tests must run at least one of them
/// (<see cref="PlanViolationCode.TestStepRequiredByPolicy"/>): a build alone is not a test. Null when the caller does not
/// know the kinds of the steps; then only <see cref="PlanViolationCode.VerificationRequiredByPolicy"/> is checked, and the
/// engine, which always knows them, checks the rest before anything runs.
/// </param>
public sealed record PlanContext(
    FrozenRequirementSet Requirements,
    IReadOnlyList<AgentDefinition> Roster,
    IReadOnlySet<string> VerificationSteps,
    IReadOnlySet<string> ToolIds,
    PlanLimits Limits,
    IReadOnlySet<string>? TestRunSteps = null);

/// <summary>Stable codes of the plan rules. Tests, logs and the UI refer to the code, never to the message.</summary>
public enum PlanViolationCode
{
    EmptyPlan,
    TooManyTasks,
    InvalidTaskId,
    DuplicateTaskId,
    TitleRequired,
    ObjectiveRequired,
    UnknownDependency,
    DuplicateDependency,
    DependencyCycle,
    UnknownAgent,
    AgentRequired,
    OrchestratorHasTask,
    JudgeOutsideReview,
    ReviewerNotJudge,

    /// <summary>A judge on a review task runs on the vendor of the orchestrator or of an executor of the plan (ADR-0006, Phase 3 decision 7).</summary>
    JudgeVendorNotIndependent,
    ConditionWithoutPredicate,
    PredicateOnNonCondition,
    PredicateNotAncestor,
    BranchRequired,
    BranchOnNonCondition,
    UnknownBranch,

    /// <summary>A task builds on the code of two or more tasks that were not merged by an Integration task.</summary>
    FanInWithoutIntegration,
    UnsafeWriteScope,
    WriteScopeRequired,
    OverlappingWriteScopes,
    UnknownRequirement,
    MandatoryRequirementUncovered,
    UnknownVerificationStep,
    ToolRequired,
    UnknownTool,
    RetriesNotAllowed,
    RetriesOutOfRange,
    InvalidTimeout,
    DebateNotAvailable,

    /// <summary>An Integration task names no verification step: merged code would never be built or tested.</summary>
    IntegrationWithoutVerification,

    /// <summary>
    /// Two Integration tasks could run at the same time: neither is an ancestor of the other. Every integration of a run
    /// merges into the one run branch and verifies in the one <c>_integration</c> worktree, so they must form a chain.
    /// </summary>
    IntegrationsNotOrdered,

    /// <summary>
    /// A task writes code for an agent whose review policy asks for tests captured by the system
    /// (<see cref="ReviewPolicy.RequiresSystemCapturedTests"/>), and it names no verification step.
    /// </summary>
    VerificationRequiredByPolicy,

    /// <summary>
    /// A task writes code for an agent whose review policy asks for an independent review
    /// (<see cref="ReviewPolicy.RequiresIndependentReview"/>), and no Review task of the plan comes after every task
    /// with that policy.
    /// </summary>
    ReviewRequiredByPolicy,

    /// <summary>
    /// A task writes code for an agent whose review policy asks for tests captured by the system, and it names
    /// verification steps but none of them runs tests (<see cref="PlanContext.TestRunSteps"/>): a build is not a test.
    /// </summary>
    TestStepRequiredByPolicy,
}

public sealed record PlanViolation(PlanViolationCode Code, TaskId? Task, string Message);

/// <summary>
/// Stable codes of what the validator warns about (<see cref="PlanValidator.Warnings"/>): the plan may run, but it will not
/// do what the user expects of it. Tests, logs and the UI refer to the code, never to the message.
/// </summary>
public enum PlanWarningCode
{
    /// <summary>
    /// A task writes code that, on every path of the plan where it runs, the plan keeps from being delivered. A run is
    /// delivered to the user's branch only as the integration it ended with, and only if the review whose verdict it ended
    /// with (the last one to finish) was shown exactly that integration and worked on it, with no integration after the
    /// verdict (<c>DeliveryService</c>): an Integration that runs after the writer, and every Review that can finish last
    /// after the last Integration, with no other task that writes code in between. Without them what the task writes can
    /// never be delivered, whatever the judge says.
    /// </summary>
    NotDeliverable,

    /// <summary>
    /// The same, but not on every path, or not whatever order the tasks finish in: on one path where it is not sure to be
    /// delivered (<see cref="PlanWarning.When"/> names its branches), either the plan keeps it from being delivered, or
    /// whether the review that finishes last started from the integration that finishes last depends on which tasks
    /// finish first (<see cref="PlanWarning.DependsOnTiming"/>).
    /// </summary>
    MayNotBeDeliverable,

    /// <summary>
    /// The plan is too large to check every path (<see cref="PlanValidator.MaxDeliveryCheckWork"/>): whether what its tasks
    /// write can be delivered is not known. One warning for the plan, about no task.
    /// </summary>
    DeliveryNotChecked,
}

/// <summary>The branch (<see cref="ConditionBranches"/>) a condition takes on one path of a plan.</summary>
public sealed record ConditionBranch(TaskId Condition, string Branch);

/// <summary>Something the user should know before approving a plan that may run. <see cref="Task"/> is the task it is about.</summary>
public sealed record PlanWarning(PlanWarningCode Code, TaskId? Task, string Message)
{
    /// <summary>
    /// For <see cref="PlanWarningCode.MayNotBeDeliverable"/>: the branches of the conditions that run on one path where what
    /// the task writes is not sure to be delivered, in the order of the plan. Empty otherwise, and when the plan has no
    /// condition that runs there.
    /// </summary>
    public IReadOnlyList<ConditionBranch> When { get; init; } = [];

    /// <summary>
    /// For <see cref="PlanWarningCode.MayNotBeDeliverable"/>: on that path the run is delivered only if its tasks finish in a
    /// lucky order. The run ends with the verdict of the Review that finishes last, and only some of the Reviews that can
    /// finish last start from the Integration that finishes last (or more than one Integration can). False otherwise.
    /// </summary>
    public bool DependsOnTiming { get; init; }
}

/// <summary>
/// The authority on whether a plan may run. The orchestrator agent proposes plans, so nothing it says is trusted: this
/// check is deterministic and never calls an agent. A plan without violations can always be turned into a graph
/// (<see cref="PlanDefinition.BuildGraph"/>) and run by the engine.
/// </summary>
/// <remarks>
/// Violations come in a stable order (plan size and ids, then each task, then dependencies, requirements, the rules
/// that need the graph, and judge independence). Rules that need a sound graph (predicate ancestry, fan-in, write-scope
/// overlap) are skipped while the plan has duplicate ids, unknown dependencies or cycles, so one structural mistake
/// does not bury the user under consequences.
/// </remarks>
public static class PlanValidator
{
    public static IReadOnlyList<PlanViolation> Validate(PlanDefinition plan, PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        return new Check(plan, context).Run();
    }

    /// <summary>
    /// The most work <see cref="Warnings"/> does on a plan, counted before any is done: the paths (2 to the number of
    /// conditions) times the tasks and dependencies walked on each, times the tasks each path is judged for. A plan the
    /// orchestrator wrote is not trusted (ADR-0018), so a larger one gets <see cref="PlanWarningCode.DeliveryNotChecked"/>
    /// at once instead. The plans a run accepts (<see cref="PlanLimits.MaxTasks"/>) stay well within it.
    /// </summary>
    public const long MaxDeliveryCheckWork = 1L << 25;

    /// <summary>
    /// What the user should know before approving <paramref name="plan"/>, a plan without violations (<see cref="Validate"/>
    /// says whether it may run, and callers ask this only once it may): whether what each task that writes code
    /// (<c>AgentWork</c> with workspace <c>Write</c>) can be delivered, as <c>DeliveryService</c> requires it, on every path
    /// of the plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is one choice of branch for every condition, run as the graph runs it (<see cref="TaskGraph"/>): a task with
    /// <c>AllCompleted</c> runs when every prerequisite runs (and, from a condition, the branch it waits for is taken), one
    /// with <c>AnyCompleted</c> when at least one does, and a task that runs waits for every prerequisite that runs.
    /// </para>
    /// <para>
    /// A run is delivered as the integration it ended with, the last one to finish, and only when the review whose verdict it
    /// ended with, the last one to finish (the engine keeps the verdict of each review that ends), was shown exactly that
    /// integration (its dossier, built when the review starts, names it) and worked on it (its worktree starts on its code
    /// frontier, the closest tasks before it that produce code, which must be exactly that Integration), with no integration
    /// after the verdict. So on a path where a writer runs, it is delivered when an Integration that runs comes after it, one
    /// Integration that runs comes after every other one, and every Review that can finish last (no Review that runs comes
    /// after it) starts from that last Integration: after it, with no other task that writes code in between. When only
    /// some of them do, or more than one Integration can finish last, it depends on which tasks finish first
    /// (<see cref="PlanWarning.DependsOnTiming"/>); otherwise it is not delivered on that path.
    /// </para>
    /// <para>
    /// A writer delivered on no path where it runs is <see cref="PlanWarningCode.NotDeliverable"/>; one delivered on some
    /// paths only, or by the order of the tasks, <see cref="PlanWarningCode.MayNotBeDeliverable"/>, with the first path
    /// where it is not sure to be. The check is static: it takes every task to succeed, as a run that can be delivered did.
    /// Deterministic and in the order of the tasks. A plan too large to check (<see cref="MaxDeliveryCheckWork"/>) gets one
    /// <see cref="PlanWarningCode.DeliveryNotChecked"/>; one whose graph is not sound (duplicate ids, unknown dependencies,
    /// cycles) has no warnings, since its violations come first.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PlanWarning> Warnings(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var writers = plan.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork && t.Workspace == WorkspaceMode.Write).ToList();
        if (writers.Count == 0)
        {
            return [];
        }

        var conditionCount = plan.Tasks.Count(t => t.Kind == WorkTaskKind.Condition);
        var perPath = (plan.Tasks.Count + plan.Tasks.Sum(t => (long)t.DependsOn.Count)) * plan.Tasks.Count;
        if (conditionCount >= 31 || perPath > MaxDeliveryCheckWork || (1L << conditionCount) > MaxDeliveryCheckWork / perPath)
        {
            return [new PlanWarning(
                PlanWarningCode.DeliveryNotChecked,
                null,
                $"The plan has {plan.Tasks.Count} tasks and {conditionCount} conditions: checking every branch would take more than the {MaxDeliveryCheckWork} steps the check allows, so whether what its tasks write can be delivered is not known.")];
        }

        if (TopologicalOrder(plan) is not { } order)
        {
            return [];
        }

        var byId = plan.Tasks.ToDictionary(t => t.Id);
        var producers = plan.Tasks.Where(ProducesCode).Select(t => t.Id).ToHashSet();
        var conditions = order.Where(id => byId[id].Kind == WorkTaskKind.Condition).ToList();
        var anyIntegration = plan.Tasks.Any(t => t.Kind == WorkTaskKind.Integration);
        var delivered = new HashSet<TaskId>();
        var byTiming = new HashSet<TaskId>();
        var first = new Dictionary<TaskId, (IReadOnlyList<ConditionBranch> When, Delivery Outcome, string Why)>();
        for (var choice = 0; choice < 1 << conditions.Count; choice++)
        {
            var path = Walk(order, byId, conditions, choice);
            var running = writers.Where(w => path.Runs.Contains(w.Id)).ToList();
            if (running.Count == 0)
            {
                continue;
            }

            var integrations = order.Where(id => path.Runs.Contains(id) && byId[id].Kind == WorkTaskKind.Integration).ToList();
            var reviews = order.Where(id => path.Runs.Contains(id) && byId[id].Kind == WorkTaskKind.Review).ToList();
            var ending = Ending(path, integrations, reviews, producers);
            foreach (var writer in running)
            {
                var (outcome, why) = Judge(writer.Id, path, integrations, ending, anyIntegration);
                if (outcome == Delivery.Delivered)
                {
                    delivered.Add(writer.Id);
                    continue;
                }

                if (outcome == Delivery.Timing)
                {
                    byTiming.Add(writer.Id);
                }

                first.TryAdd(writer.Id, (path.Branches, outcome, why));
            }
        }

        var warnings = new List<PlanWarning>();
        foreach (var writer in writers.Where(w => first.ContainsKey(w.Id)))
        {
            var (when, outcome, why) = first[writer.Id];
            // Never: on no path where it runs is it delivered, nor left to the order of the tasks.
            warnings.Add(!delivered.Contains(writer.Id) && !byTiming.Contains(writer.Id)
                ? new PlanWarning(
                    PlanWarningCode.NotDeliverable,
                    writer.Id,
                    $"Task '{writer.Id}' writes code, but {why}: a run is delivered to the user's branch only as the integration a judge reviewed, so what '{writer.Id}' writes could never be delivered.")
                : new PlanWarning(
                    PlanWarningCode.MayNotBeDeliverable,
                    writer.Id,
                    $"Task '{writer.Id}' writes code that is not sure to be delivered: {(when.Count == 0 ? string.Empty : $"when {string.Join(", ", when.Select(b => $"'{b.Condition}' takes '{b.Branch}'"))}, ")}{why}. A run is delivered to the user's branch only as its last integration, when the review it ends with was shown that integration and started from it.")
                {
                    When = when,
                    DependsOnTiming = outcome == Delivery.Timing,
                });
        }

        return warnings;
    }

    /// <summary>How a writer fares on one path: delivered, delivered only if its tasks finish in a lucky order, or never.</summary>
    private enum Delivery
    {
        Delivered,
        Timing,
        Never,
    }

    /// <summary>
    /// The fate of <paramref name="writer"/> on <paramref name="path"/>, with why when it is not delivered: an Integration
    /// that runs must merge it, and then it fares as the run does (<paramref name="ending"/>, from <see cref="Ending"/>).
    /// </summary>
    private static (Delivery Outcome, string Why) Judge(TaskId writer, PlanPath path, IReadOnlyList<TaskId> integrations, (Delivery Outcome, string Why) ending, bool anyIntegration)
    {
        if (integrations.Count == 0)
        {
            return (Delivery.Never, anyIntegration ? "no Integration runs" : "the plan has no Integration");
        }

        return integrations.Any(i => path.After[i].Contains(writer)) ? ending : (Delivery.Never, "no Integration that runs comes after it");
    }

    /// <summary>
    /// Whether a run on <paramref name="path"/> is delivered, whatever it integrated. The run ends with the verdict of the
    /// Review that finishes last, one that no Review that runs comes after; it is delivered only as the Integration that
    /// finishes last, one that no Integration that runs comes after, and only when that Review starts from it
    /// (<see cref="StartsFrom"/>). Sure when there is one such Integration and every such Review starts from it; a matter of
    /// which finishes first when only some pairing works; never otherwise.
    /// </summary>
    private static (Delivery Outcome, string Why) Ending(PlanPath path, IReadOnlyList<TaskId> integrations, IReadOnlyList<TaskId> reviews, IReadOnlySet<TaskId> producers)
    {
        if (reviews.Count == 0)
        {
            return (Delivery.Never, "no Review runs");
        }

        var lastIntegrations = integrations.Where(i => !integrations.Any(j => j != i && path.After[j].Contains(i))).ToList();
        var lastReviews = reviews.Where(r => !reviews.Any(s => s != r && path.After[s].Contains(r))).ToList();

        // A Review after two of those Integrations starts from neither (its frontier holds both), so only one can do.
        var starting = lastReviews.Count(r => lastIntegrations.Where(path.After[r].Contains).Take(2).ToList() is [var only] && StartsFrom(r, only, path, producers));
        if (starting == lastReviews.Count && lastIntegrations.Count == 1)
        {
            return (Delivery.Delivered, string.Empty);
        }

        if (starting > 0)
        {
            return (Delivery.Timing, lastIntegrations.Count > 1
                ? "more than one Integration that runs can finish last, so whether the Review that ends the run started from the last one depends on which finishes first"
                : "the run ends with the verdict of the Review that finishes last, and not every Review that can finish last starts from the last Integration (after it, with no other task that writes code in between), so it depends on which finishes first");
        }

        return (Delivery.Never, "no Review that can end the run starts from the last Integration that runs (after it, with no other task that writes code in between), so the verdict the run ends with is never about what would be delivered");
    }

    /// <summary>
    /// Whether <paramref name="review"/> starts from <paramref name="integration"/> on <paramref name="path"/>: its worktree
    /// starts on its code frontier (the tasks that produce code it comes after that no other such task comes after), and
    /// that frontier is exactly the Integration. Every other task that produces code before the review is before the
    /// Integration too.
    /// </summary>
    private static bool StartsFrom(TaskId review, TaskId integration, PlanPath path, IReadOnlySet<TaskId> producers)
    {
        var before = path.After[review];
        return before.Contains(integration) && before.All(t => t == integration || !producers.Contains(t) || path.After[integration].Contains(t));
    }

    /// <summary>Tasks whose result is code the tasks after them build on (<c>RunContext.ProducesCode</c> in the engine).</summary>
    private static bool ProducesCode(TaskSpec task) =>
        task.Kind == WorkTaskKind.Integration || (task.Kind == WorkTaskKind.AgentWork && task.Workspace == WorkspaceMode.Write);

    /// <summary>The tasks that run on one path of a plan, what each of them runs after, and the branch of each condition that runs.</summary>
    private sealed record PlanPath(HashSet<TaskId> Runs, Dictionary<TaskId, HashSet<TaskId>> After, IReadOnlyList<ConditionBranch> Branches);

    /// <summary>
    /// The path where the conditions take the branches of <paramref name="choice"/> (bit <c>i</c> set: <c>no</c> for the
    /// <c>i</c>-th condition), run as <see cref="TaskGraph"/> runs it: an edge is satisfied when its prerequisite runs
    /// and, when it leaves a condition with a branch, that branch is taken; <c>AllCompleted</c> runs when every edge is
    /// satisfied, <c>AnyCompleted</c> when one is, a task without prerequisites always. A task that runs waits for every
    /// prerequisite that runs, also through an edge whose branch was not taken (<c>AnyCompleted</c> waits until none is pending).
    /// </summary>
    private static PlanPath Walk(IReadOnlyList<TaskId> order, Dictionary<TaskId, TaskSpec> byId, IReadOnlyList<TaskId> conditions, int choice)
    {
        var taken = new Dictionary<TaskId, string>();
        for (var i = 0; i < conditions.Count; i++)
        {
            taken[conditions[i]] = (choice & (1 << i)) == 0 ? ConditionBranches.Yes : ConditionBranches.No;
        }

        var runs = new HashSet<TaskId>();
        var after = new Dictionary<TaskId, HashSet<TaskId>>();
        foreach (var id in order)
        {
            var task = byId[id];
            var satisfied = task.DependsOn
                .Select(d => runs.Contains(d.Prerequisite)
                    && (d.Branch is null || byId[d.Prerequisite].Kind != WorkTaskKind.Condition || string.Equals(taken[d.Prerequisite], d.Branch, StringComparison.Ordinal)))
                .ToList();
            var ready = satisfied.Count == 0 || (task.Join == JoinPolicy.AnyCompleted ? satisfied.Any(s => s) : satisfied.All(s => s));
            if (!ready)
            {
                continue;
            }

            runs.Add(id);
            var before = new HashSet<TaskId>();
            foreach (var prerequisite in task.DependsOn.Select(d => d.Prerequisite).Where(runs.Contains))
            {
                before.Add(prerequisite);
                before.UnionWith(after[prerequisite]);
            }

            after[id] = before;
        }

        return new PlanPath(runs, after, [.. conditions.Where(runs.Contains).Select(c => new ConditionBranch(c, taken[c]))]);
    }

    /// <summary>
    /// The tasks in an order where each comes after all of its prerequisites (Kahn's, ties by the order of the plan), in
    /// time linear in the tasks and dependencies, or null when the graph is not sound: a duplicate id, an unknown or self
    /// dependency, a cycle.
    /// </summary>
    private static List<TaskId>? TopologicalOrder(PlanDefinition plan)
    {
        var byId = new Dictionary<TaskId, TaskSpec>();
        foreach (var task in plan.Tasks)
        {
            if (!byId.TryAdd(task.Id, task))
            {
                return null;
            }
        }

        var waiting = new Dictionary<TaskId, int>();
        var dependents = plan.Tasks.ToDictionary(t => t.Id, _ => new List<TaskId>());
        foreach (var task in plan.Tasks)
        {
            var prerequisites = task.DependsOn.Select(d => d.Prerequisite).Distinct().ToList();
            if (prerequisites.Any(p => p == task.Id || !byId.ContainsKey(p)))
            {
                return null;
            }

            waiting[task.Id] = prerequisites.Count;
            foreach (var prerequisite in prerequisites)
            {
                dependents[prerequisite].Add(task.Id);
            }
        }

        var ready = new Queue<TaskId>(plan.Tasks.Where(t => waiting[t.Id] == 0).Select(t => t.Id));
        var order = new List<TaskId>();
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            order.Add(id);
            foreach (var next in dependents[id])
            {
                if (--waiting[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        return order.Count == plan.Tasks.Count ? order : null;
    }

    private sealed class Check
    {
        private readonly PlanDefinition _plan;
        private readonly PlanContext _context;
        private readonly List<PlanViolation> _violations = [];
        private readonly Dictionary<TaskId, TaskSpec> _byId = [];
        private readonly Dictionary<AgentId, AgentDefinition> _roster = [];
        private readonly Dictionary<TaskId, HashSet<TaskId>> _ancestors = [];
        private bool _graphIsSound = true;

        public Check(PlanDefinition plan, PlanContext context)
        {
            _plan = plan;
            _context = context;
            foreach (var task in plan.Tasks)
            {
                _byId.TryAdd(task.Id, task);
            }

            foreach (var agent in context.Roster)
            {
                _roster.TryAdd(agent.Id, agent);
            }
        }

        public IReadOnlyList<PlanViolation> Run()
        {
            if (_plan.Tasks.Count == 0)
            {
                Add(PlanViolationCode.EmptyPlan, null, "The plan has no tasks.");
                return _violations;
            }

            CheckSize();
            CheckIds();
            foreach (var task in _plan.Tasks)
            {
                CheckTask(task);
            }

            CheckDependencies();
            CheckRequirementCoverage();
            if (_graphIsSound)
            {
                CheckPredicateAncestry();
                CheckFanIn();
                CheckWriteScopeOverlaps();
                CheckIntegrationOrder();
            }

            CheckJudgeIndependence();
            CheckReviewPolicy();
            return _violations;
        }

        private void Add(PlanViolationCode code, TaskId? task, string message) => _violations.Add(new PlanViolation(code, task, message));

        // ---- plan level ----

        private void CheckSize()
        {
            if (_plan.Tasks.Count > _context.Limits.MaxTasks)
            {
                Add(PlanViolationCode.TooManyTasks, null, $"The plan has {_plan.Tasks.Count} tasks; the limit is {_context.Limits.MaxTasks}.");
            }
        }

        private void CheckIds()
        {
            foreach (var group in _plan.Tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1))
            {
                _graphIsSound = false;
                Add(PlanViolationCode.DuplicateTaskId, group.Key, $"Task id '{group.Key}' appears {group.Count()} times.");
            }

            foreach (var task in _plan.Tasks.Where(t => !IsValidTaskId(t.Id.Value)))
            {
                Add(
                    PlanViolationCode.InvalidTaskId,
                    task.Id,
                    $"Task id '{task.Id}' is not valid: it starts with a letter and has at most 32 letters, digits, '_' or '-'.");
            }
        }

        private static bool IsValidTaskId(string id) =>
            id.Length is >= 1 and <= 32
            && char.IsAsciiLetter(id[0])
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

        // ---- one task ----

        private void CheckTask(TaskSpec task)
        {
            var id = task.Id;
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                Add(PlanViolationCode.TitleRequired, id, $"Task '{id}' has no title.");
            }

            if (task.Kind == WorkTaskKind.AgentWork && string.IsNullOrWhiteSpace(task.Objective))
            {
                Add(PlanViolationCode.ObjectiveRequired, id, $"Agent task '{id}' has no objective.");
            }

            CheckAgent(task);
            CheckRetriesAndTimeout(task);
            CheckKindRules(task);

            foreach (var requirement in task.Covers.Where(r => _context.Requirements.Find(r) is null))
            {
                Add(PlanViolationCode.UnknownRequirement, id, $"Task '{id}' covers requirement '{requirement}', which is not in the frozen set.");
            }

            foreach (var step in task.VerificationSteps.Where(s => !_context.VerificationSteps.Contains(s)))
            {
                Add(PlanViolationCode.UnknownVerificationStep, id, $"Task '{id}' names verification step '{step}', which is not in the verification profile.");
            }

            foreach (var entry in task.WriteScope)
            {
                if (!WriteScope.TryNormalize(entry, out _, out var problem))
                {
                    Add(PlanViolationCode.UnsafeWriteScope, id, $"Write scope '{entry}' of task '{id}' is not allowed: {problem}");
                }
            }

            if (task.Kind == WorkTaskKind.AgentWork && task.Workspace == WorkspaceMode.Write && task.WriteScope.Count == 0)
            {
                Add(PlanViolationCode.WriteScopeRequired, id, $"Task '{id}' writes to its worktree but declares no write scope.");
            }
        }

        private void CheckAgent(TaskSpec task)
        {
            var id = task.Id;
            if (task.Agent is not { } agentId)
            {
                if (task.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review)
                {
                    Add(PlanViolationCode.AgentRequired, id, $"{task.Kind} task '{id}' needs an agent.");
                }

                return;
            }

            if (!_roster.TryGetValue(agentId, out var agent))
            {
                Add(PlanViolationCode.UnknownAgent, id, $"Task '{id}' is assigned to '{agentId}', who is not in the roster.");
                return;
            }

            if (agent.Role == AgentRole.Orchestrator)
            {
                Add(PlanViolationCode.OrchestratorHasTask, id, $"Task '{id}' is assigned to the orchestrator '{agentId}', which only plans.");
            }
            else if (agent.Role is AgentRole.Judge or AgentRole.Auditor && task.Kind != WorkTaskKind.Review)
            {
                Add(PlanViolationCode.JudgeOutsideReview, id, $"{agent.Role} '{agentId}' can only take review tasks, not '{id}' ({task.Kind}).");
            }
            else if (task.Kind == WorkTaskKind.Review && agent.Role is not (AgentRole.Judge or AgentRole.Auditor))
            {
                Add(PlanViolationCode.ReviewerNotJudge, id, $"Review task '{id}' is assigned to '{agentId}' ({agent.Role}); only a judge or an auditor can review.");
            }
        }

        private void CheckRetriesAndTimeout(TaskSpec task)
        {
            var id = task.Id;
            if (task.MaxRetries < 0 || task.MaxRetries > _context.Limits.MaxRetriesPerTask)
            {
                Add(PlanViolationCode.RetriesOutOfRange, id, $"Task '{id}' allows {task.MaxRetries} retries; the limit is {_context.Limits.MaxRetriesPerTask}.");
            }

            if (task.Kind is WorkTaskKind.Review or WorkTaskKind.HumanApproval && task.MaxRetries != 0)
            {
                Add(PlanViolationCode.RetriesNotAllowed, id, $"{task.Kind} task '{id}' cannot be retried (a retry would spend quota or ask the user again).");
            }

            if (task.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            {
                Add(PlanViolationCode.InvalidTimeout, id, $"Task '{id}' has a timeout that is not positive.");
            }
        }

        private void CheckKindRules(TaskSpec task)
        {
            var id = task.Id;
            switch (task.Kind)
            {
                case WorkTaskKind.Debate when !_context.Limits.DebateNodesAvailable:
                    Add(PlanViolationCode.DebateNotAvailable, id, $"Task '{id}' is a debate node, which is available from Phase 5.");
                    break;
                case WorkTaskKind.ToolRun when string.IsNullOrWhiteSpace(task.ToolId):
                    Add(PlanViolationCode.ToolRequired, id, $"Tool task '{id}' does not name the tool to run.");
                    break;
                case WorkTaskKind.ToolRun when !_context.ToolIds.Contains(task.ToolId!):
                    Add(PlanViolationCode.UnknownTool, id, $"Tool task '{id}' names '{task.ToolId}', which is not in the verification profile.");
                    break;
                case WorkTaskKind.Condition when task.Condition is null:
                    Add(PlanViolationCode.ConditionWithoutPredicate, id, $"Condition '{id}' has no predicate.");
                    break;
                case WorkTaskKind.Integration when task.VerificationSteps.Count == 0:
                    Add(PlanViolationCode.IntegrationWithoutVerification, id, $"Integration task '{id}' names no verification step, so the merged code would never be built or tested.");
                    break;
            }

            if (task.Kind != WorkTaskKind.Condition && task.Condition is not null)
            {
                Add(PlanViolationCode.PredicateOnNonCondition, id, $"Task '{id}' is a {task.Kind} but carries a condition predicate.");
            }
        }

        // ---- dependencies ----

        private void CheckDependencies()
        {
            foreach (var task in _plan.Tasks)
            {
                var seen = new HashSet<TaskId>();
                foreach (var dependency in task.DependsOn)
                {
                    if (!seen.Add(dependency.Prerequisite))
                    {
                        Add(PlanViolationCode.DuplicateDependency, task.Id, $"Task '{task.Id}' lists '{dependency.Prerequisite}' as a prerequisite more than once.");
                    }

                    if (dependency.Prerequisite == task.Id)
                    {
                        _graphIsSound = false;
                        Add(PlanViolationCode.DependencyCycle, task.Id, $"Dependency cycle: {task.Id} -> {task.Id}.");
                        continue;
                    }

                    if (!_byId.TryGetValue(dependency.Prerequisite, out var prerequisite))
                    {
                        _graphIsSound = false;
                        Add(PlanViolationCode.UnknownDependency, task.Id, $"Task '{task.Id}' depends on '{dependency.Prerequisite}', which is not in the plan.");
                        continue;
                    }

                    CheckBranch(task, dependency, prerequisite);
                }
            }

            CheckCycles();
        }

        private void CheckBranch(TaskSpec task, DependencySpec dependency, TaskSpec prerequisite)
        {
            if (prerequisite.Kind == WorkTaskKind.Condition)
            {
                if (string.IsNullOrWhiteSpace(dependency.Branch))
                {
                    Add(PlanViolationCode.BranchRequired, task.Id, $"Task '{task.Id}' depends on condition '{prerequisite.Id}' without naming a branch (yes or no).");
                }
                else if (!ConditionBranches.All.Contains(dependency.Branch))
                {
                    Add(PlanViolationCode.UnknownBranch, task.Id, $"Task '{task.Id}' waits for branch '{dependency.Branch}' of '{prerequisite.Id}'; a condition has the branches yes and no.");
                }
            }
            else if (dependency.Branch is not null)
            {
                Add(PlanViolationCode.BranchOnNonCondition, task.Id, $"Task '{task.Id}' names branch '{dependency.Branch}' of '{prerequisite.Id}', which is not a condition.");
            }
        }

        private void CheckCycles()
        {
            // Depth-first search along "depends on" edges; an edge back to a task on the current path closes a cycle.
            var state = new Dictionary<TaskId, int>(); // 1 = on the current path, 2 = finished
            var path = new List<TaskId>();

            void Visit(TaskId id)
            {
                state[id] = 1;
                path.Add(id);
                foreach (var next in KnownPrerequisites(id))
                {
                    if (!state.TryGetValue(next, out var seen))
                    {
                        Visit(next);
                    }
                    else if (seen == 1)
                    {
                        var cycle = path.Skip(path.IndexOf(next)).Append(next).ToList();
                        _graphIsSound = false;
                        Add(PlanViolationCode.DependencyCycle, next, $"Dependency cycle: {string.Join(" -> ", cycle)}.");
                    }
                }

                path.RemoveAt(path.Count - 1);
                state[id] = 2;
            }

            foreach (var task in _plan.Tasks)
            {
                if (!state.ContainsKey(task.Id))
                {
                    Visit(task.Id);
                }
            }
        }

        /// <summary>The distinct, known prerequisites of a task other than itself, in a stable order.</summary>
        private IEnumerable<TaskId> KnownPrerequisites(TaskId id) => _byId[id].DependsOn
            .Select(d => d.Prerequisite)
            .Where(p => p != id && _byId.ContainsKey(p))
            .Distinct()
            .OrderBy(p => p.Value, StringComparer.Ordinal);

        // ---- requirements ----

        private void CheckRequirementCoverage()
        {
            var covered = _plan.Tasks.SelectMany(t => t.Covers).ToHashSet();
            foreach (var requirement in _context.Requirements.Mandatory.Where(r => !covered.Contains(r.Id)))
            {
                Add(PlanViolationCode.MandatoryRequirementUncovered, null, $"Mandatory requirement '{requirement.Id}' is not covered by any task.");
            }
        }

        // ---- rules that need a sound graph ----

        private HashSet<TaskId> AncestorsOf(TaskId id)
        {
            if (_ancestors.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var found = new HashSet<TaskId>();
            var stack = new Stack<TaskId>(KnownPrerequisites(id));
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (found.Add(current))
                {
                    foreach (var next in KnownPrerequisites(current))
                    {
                        stack.Push(next);
                    }
                }
            }

            _ancestors[id] = found;
            return found;
        }

        private void CheckPredicateAncestry()
        {
            foreach (var task in _plan.Tasks.Where(t => t.Condition is not null))
            {
                var referenced = task.Condition!.ReferencedTask;
                if (!AncestorsOf(task.Id).Contains(referenced))
                {
                    Add(PlanViolationCode.PredicateNotAncestor, task.Id, $"Condition '{task.Id}' reads task '{referenced}', which is not one of its ancestors.");
                }
            }
        }

        private static bool NeedsCodeBase(TaskSpec task) =>
            task.Kind is WorkTaskKind.Review or WorkTaskKind.ToolRun || (task.Kind == WorkTaskKind.AgentWork && task.Workspace != WorkspaceMode.None);

        private void CheckFanIn()
        {
            foreach (var task in _plan.Tasks.Where(NeedsCodeBase))
            {
                // The code frontier: the closest ancestors that produce code, looking through the nodes that do not.
                var frontier = new HashSet<TaskId>();
                var visited = new HashSet<TaskId>();
                var stack = new Stack<TaskId>(KnownPrerequisites(task.Id));
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!visited.Add(current))
                    {
                        continue;
                    }

                    if (ProducesCode(_byId[current]))
                    {
                        frontier.Add(current);
                    }
                    else
                    {
                        foreach (var next in KnownPrerequisites(current))
                        {
                            stack.Push(next);
                        }
                    }
                }

                // A frontier task that is an ancestor of another one is already contained in it.
                var maximal = frontier
                    .Where(x => !frontier.Any(y => y != x && AncestorsOf(y).Contains(x)))
                    .OrderBy(x => x.Value, StringComparer.Ordinal)
                    .ToList();
                if (maximal.Count >= 2)
                {
                    Add(
                        PlanViolationCode.FanInWithoutIntegration,
                        task.Id,
                        $"Task '{task.Id}' builds on the code of {string.Join(", ", maximal.Select(m => $"'{m}'"))}; merge them in an Integration task first.");
                }
            }
        }

        private void CheckWriteScopeOverlaps()
        {
            var writers = _plan.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork && t.Workspace == WorkspaceMode.Write).ToList();
            for (var i = 0; i < writers.Count; i++)
            {
                for (var j = i + 1; j < writers.Count; j++)
                {
                    var a = writers[i];
                    var b = writers[j];
                    var concurrent = !AncestorsOf(a.Id).Contains(b.Id) && !AncestorsOf(b.Id).Contains(a.Id);
                    if (concurrent && WriteScope.Overlaps(a.WriteScope, b.WriteScope))
                    {
                        Add(
                            PlanViolationCode.OverlappingWriteScopes,
                            a.Id,
                            $"Tasks '{a.Id}' and '{b.Id}' can run at the same time and their write scopes overlap; they would change the same files.");
                    }
                }
            }
        }

        /// <summary>
        /// Every pair of integrations must be ordered by ancestry. The pair is reported once, on the later task of the
        /// canonical order, naming the earlier one.
        /// </summary>
        private void CheckIntegrationOrder()
        {
            var integrations = _plan.Tasks.Where(t => t.Kind == WorkTaskKind.Integration).ToList();
            for (var j = 1; j < integrations.Count; j++)
            {
                for (var i = 0; i < j; i++)
                {
                    var (a, b) = (integrations[i], integrations[j]);
                    if (!AncestorsOf(a.Id).Contains(b.Id) && !AncestorsOf(b.Id).Contains(a.Id))
                    {
                        Add(
                            PlanViolationCode.IntegrationsNotOrdered,
                            b.Id,
                            $"Integrations '{a.Id}' and '{b.Id}' could run at the same time; every integration merges into the run's branch and verifies in its one integration worktree, so one must depend on the other.");
                    }
                }
            }
        }

        // ---- the review policy of whoever writes the code ----

        /// <summary>
        /// The agent of each task that writes code brings its review policy: system-captured tests mean the task names
        /// verification steps, and at least one of them runs tests when the context knows which do; an independent review
        /// means one Review task comes after every task of such agents. When none does, the tasks the best-placed review
        /// (the one after most of them, the first by id on a tie) misses are reported. Ancestry needs a sound graph, so the
        /// review rule waits for one.
        /// </summary>
        private void CheckReviewPolicy()
        {
            var writers = _plan.Tasks
                .Where(t => t.Kind == WorkTaskKind.AgentWork && t.Workspace == WorkspaceMode.Write && t.Agent is { } a && _roster.ContainsKey(a))
                .ToList();
            foreach (var task in writers.Where(t => _roster[t.Agent!.Value].Review.RequiresSystemCapturedTests))
            {
                if (task.VerificationSteps.Count == 0)
                {
                    Add(
                        PlanViolationCode.VerificationRequiredByPolicy,
                        task.Id,
                        $"Task '{task.Id}' writes code for '{task.Agent}', whose review policy asks for tests captured by the system, but it names no verification step.");
                }
                else if (_context.TestRunSteps is { } tests && !task.VerificationSteps.Any(tests.Contains))
                {
                    Add(
                        PlanViolationCode.TestStepRequiredByPolicy,
                        task.Id,
                        $"Task '{task.Id}' writes code for '{task.Agent}', whose review policy asks for tests captured by the system, but none of its verification steps ({string.Join(", ", task.VerificationSteps)}) runs tests.");
                }
            }

            var reviewed = writers.Where(t => _roster[t.Agent!.Value].Review.RequiresIndependentReview).ToList();
            if (!_graphIsSound || reviewed.Count == 0)
            {
                return;
            }

            var best = _plan.Tasks
                .Where(t => t.Kind == WorkTaskKind.Review)
                .Select(review => (Review: review, Covered: reviewed.Count(w => AncestorsOf(review.Id).Contains(w.Id))))
                .OrderByDescending(x => x.Covered)
                .ThenBy(x => x.Review.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            var covered = best.Review is null ? [] : AncestorsOf(best.Review.Id);
            foreach (var task in reviewed.Where(w => !covered.Contains(w.Id)))
            {
                var nearest = best.Review is null ? "the plan has no Review task" : $"'{best.Review.Id}' does not come after it";
                Add(
                    PlanViolationCode.ReviewRequiredByPolicy,
                    task.Id,
                    $"Task '{task.Id}' writes code for '{task.Agent}', whose review policy asks for an independent review, but no Review task comes after every task with that policy ({nearest}).");
            }
        }

        // ---- judge independence ----

        private void CheckJudgeIndependence()
        {
            var reviews = _plan.Tasks
                .Where(t => t.Kind == WorkTaskKind.Review && t.Agent is { } a && _roster.ContainsKey(a))
                .ToList();
            if (reviews.Count == 0)
            {
                return;
            }

            // Everyone who produced what is reviewed: the orchestrator (who made the plan) and the executors in it.
            var orchestrators = _roster.Values.Where(a => a.Role == AgentRole.Orchestrator).ToList();
            if (_plan.Author is { } author && _roster.TryGetValue(author, out var planner) && !orchestrators.Contains(planner))
            {
                orchestrators.Add(planner);
            }

            var executors = _plan.Tasks
                .Where(t => t.Kind == WorkTaskKind.AgentWork && t.Agent is { } a && _roster.ContainsKey(a))
                .Select(t => _roster[t.Agent!.Value])
                .DistinctBy(a => a.Id)
                .ToList();

            foreach (var review in reviews)
            {
                var judge = _roster[review.Agent!.Value];
                var vendor = judge.Model.Vendor;
                var shared = orchestrators.Where(a => a.Model.Vendor == vendor).Select(a => $"the orchestrator '{a.Id}'")
                    .Concat(executors.Where(a => a.Model.Vendor == vendor).Select(a => $"the executor '{a.Id}'"))
                    .ToList();
                if (shared.Count > 0)
                {
                    Add(
                        PlanViolationCode.JudgeVendorNotIndependent,
                        review.Id,
                        $"'{judge.Id}' reviews task '{review.Id}' on vendor '{vendor}', the same vendor as {string.Join(" and ", shared)}; the judge must run on a vendor that produced none of the work.");
                }
            }
        }
    }
}
