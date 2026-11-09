using System.Globalization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// The part of the draft a fix of one click edits (docs/17 D-18): the plan (null before there is one), the template of agents
/// and the budget of the run in agent runs. Immutable: applying a fix gives a new state, so undoing is going back to the one
/// before (<see cref="EditHistory{T}"/>).
/// </summary>
public sealed record MontageEditState(PlanDefinition? Plan, IReadOnlyList<AgentDefinition> Roster, int MaxAgentRuns);

/// <summary>
/// What applying a fix needs beyond the state: the title of a new Integration task (a text of the interface, from the resources
/// of the desktop: the domain writes none), the steps of the trusted profile to verify it, and the template of a judge to copy
/// when the team has none (docs/17 D-09: adding copies the template of the role).
/// </summary>
public sealed record FixEnvironment(string IntegrationTitle, IReadOnlyList<VerificationStepFacts> Steps, AgentDefinition? JudgeTemplate = null);

/// <summary>
/// How applying a fix went: the new state when it was applied, or the same state and why not, as a stable code
/// (<see cref="FixRefusals"/>). A fix that is not applied changes nothing.
/// </summary>
public sealed record FixOutcome(MontageEditState State, bool Applied, string? Refusal)
{
    public static FixOutcome Done(MontageEditState state) => new(state, true, null);

    public static FixOutcome Refused(MontageEditState state, string refusal) => new(state, false, refusal);
}

/// <summary>Why a fix was not applied, as stable codes.</summary>
public static class FixRefusals
{
    /// <summary>The fix edits the plan and there is none.</summary>
    public const string NoPlan = "fix.refused.no-plan";

    /// <summary>The fix names a task that is not in the plan.</summary>
    public const string UnknownTask = "fix.refused.unknown-task";

    /// <summary>The fix names an agent that is not in the template.</summary>
    public const string UnknownAgent = "fix.refused.unknown-agent";

    /// <summary>The dependency would make a cycle, waits for a condition (which needs a branch) or is already kept.</summary>
    public const string Dependency = "fix.refused.dependency";

    /// <summary>The graph of the plan is not sound (the validator says why): the fix waits for it.</summary>
    public const string GraphNotSound = "fix.refused.graph-not-sound";

    /// <summary>There is no judge to copy: neither in the team nor in the environment.</summary>
    public const string NoJudgeTemplate = "fix.refused.no-judge-template";

    /// <summary>The template has no field for it (the context budget is the one of the prompt of the executor).</summary>
    public const string NotInTemplate = "fix.refused.not-in-template";

    /// <summary>The fix goes through another store: the activations of skills are the person's, through theirs.</summary>
    public const string ThroughSkillStore = "fix.refused.through-skill-store";

    /// <summary>The fix asks for nothing that is not there already.</summary>
    public const string NothingToChange = "fix.refused.nothing-to-change";

    /// <summary>The fix would leave the graph of the plan unsound (a cycle, by the criterion of the validator): it is not offered.</summary>
    public const string BreaksGraph = "fix.refused.breaks-graph";
}

/// <summary>
/// Applies a fix of one click to the draft (docs/17 D-18): a typed edit, pure, that gives a new state and can be undone by
/// going back to the old one. It never decides whether the result may be approved: the analyzer and the validator look at it
/// again. Whether the fix changes the template (<see cref="Fix.ChangesRoster"/>, which withdraws the approval of the plan) or
/// grants a capability of high risk (<see cref="Fix.GrantsHighRiskCapability"/>, which asks Windows Hello) is for the caller to
/// say and ask before it applies it. The plan keeps its source and author: the canvas marks the edits of the person as it does
/// for any other.
/// </summary>
public static class FixApplier
{
    /// <exception cref="ArgumentException">A kind of fix this applier does not know.</exception>
    public static FixOutcome Apply(MontageEditState state, Fix fix, FixEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(environment);
        var outcome = Edit(state, fix, environment);

        // The net under every fix: one that would leave the graph of a sound plan unsound (a cycle, a repeated id, an unknown
        // dependency), by the same criterion as the validator, is not applied and so not offered (MontageAnalyzer).
        return outcome.Applied && state.Plan is { } before && outcome.State.Plan is { } after && DraftFacts.Ancestors(before) is not null && DraftFacts.Ancestors(after) is null
            ? FixOutcome.Refused(state, FixRefusals.BreaksGraph)
            : outcome;
    }

    private static FixOutcome Edit(MontageEditState state, Fix fix, FixEnvironment environment)
    {
        return fix switch
        {
            AddIntegrationFix => AddIntegration(state, environment),
            AddBuildStepFix f => EditTask(state, f.Task, t => t.VerificationSteps.Contains(f.BuildStep, StringComparer.Ordinal)
                ? null
                : t with { VerificationSteps = Inserted(t.VerificationSteps, f.BuildStep, environment) }),
            AddDependencyFix f => AddDependency(state, f.Task, f.DependsOn),
            ReassignTaskFix f => Known(state, f.Agent) ? EditTask(state, f.Task, t => t.Agent == f.Agent ? null : t with { Agent = f.Agent }) : FixOutcome.Refused(state, FixRefusals.UnknownAgent),
            SwitchJudgeFix f => Known(state, f.Judge) ? EditTask(state, f.ReviewTask, t => t.Agent == f.Judge ? null : t with { Agent = f.Judge }) : FixOutcome.Refused(state, FixRefusals.UnknownAgent),
            AddJudgeFix f => AddJudge(state, f, environment),
            WidenAgentFoldersFix f => EditAgent(state, f.Agent, a => a.PathGrants.Any(g => g.Access == FileAccessLevel.ReadWrite && string.Equals(g.Root, f.Folder, StringComparison.OrdinalIgnoreCase))
                ? null
                : a with { PathGrants = [.. a.PathGrants, new PathGrant(f.Folder, FileAccessLevel.ReadWrite)] }),
            RaiseContextBudgetFix => FixOutcome.Refused(state, FixRefusals.NotInTemplate),
            RaiseRunBudgetFix f => f.MaxAgentRuns > state.MaxAgentRuns ? FixOutcome.Done(state with { MaxAgentRuns = f.MaxAgentRuns }) : FixOutcome.Refused(state, FixRefusals.NothingToChange),
            ChangeModelFix f => EditAgent(state, f.Agent, a => string.Equals(a.Model.Model, f.Model, StringComparison.Ordinal) && (f.Effort is null || a.Effort == f.Effort)
                ? null
                : a with { Model = a.Model with { Model = f.Model }, Effort = f.Effort ?? a.Effort }),
            SetMemoryFix f => EditAgent(state, f.Agent, a => a.Memory == f.Memory ? null : a with { Memory = f.Memory }),
            RemoveSkillsFix => FixOutcome.Refused(state, FixRefusals.ThroughSkillStore),
            RemoveCapabilityFix f => EditAgent(state, f.Agent, a => a.AllowedTools.Contains(f.Tool, StringComparer.Ordinal)
                ? a with { AllowedTools = [.. a.AllowedTools.Where(t => !string.Equals(t, f.Tool, StringComparison.Ordinal))] }
                : null),
            _ => throw new ArgumentException($"Unknown fix {fix.GetType().Name}.", nameof(fix)),
        };
    }

    /// <summary>
    /// An Integration task behind the tasks that write and that no other task that writes comes after (or behind the leaves of
    /// the graph when none writes), verified with the test steps of the profile and a build first when a test step does not
    /// require one; and every Review after a task that writes waits for it too, so the judge reviews what is integrated and
    /// the run can be delivered (B10).
    /// </summary>
    private static FixOutcome AddIntegration(MontageEditState state, FixEnvironment environment)
    {
        if (state.Plan is not { } plan)
        {
            return FixOutcome.Refused(state, FixRefusals.NoPlan);
        }

        if (DraftFacts.Ancestors(plan) is not { } ancestors)
        {
            return FixOutcome.Refused(state, FixRefusals.GraphNotSound);
        }

        var writers = plan.Tasks.Where(DraftFacts.Writes).Select(t => t.Id).ToList();
        var behind = writers.Count > 0
            ? writers.Where(w => !writers.Any(o => o != w && ancestors[o].Contains(w))).ToList()
            : plan.Tasks.Where(t => !plan.Tasks.Any(o => ancestors[o.Id].Contains(t.Id))).Select(t => t.Id).ToList();
        var taken = plan.Tasks.Select(t => t.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = new TaskId(Enumerable.Range(1, 99).Select(n => "I" + n.ToString(CultureInfo.InvariantCulture)).First(candidate => !taken.Contains(candidate)));
        var integration = new TaskSpec(
            id,
            environment.IntegrationTitle,
            WorkTaskKind.Integration,
            null,
            string.Empty,
            string.Empty,
            [.. behind.Select(w => new DependencySpec(w))],
            JoinPolicy.AllCompleted,
            [],
            [],
            IntegrationSteps(environment.Steps),
            null,
            null,
            1,
            null,
            WorkspaceMode.None);
        // A review that comes before what the integration merges (W1 → R1 → W2) cannot wait for it: that would be a cycle.
        var beforeIntegration = behind.SelectMany(b => ancestors[b].Append(b)).ToHashSet();
        var tasks = plan.Tasks
            .Select(t => t.Kind == WorkTaskKind.Review && !beforeIntegration.Contains(t.Id) && writers.Any(ancestors[t.Id].Contains)
                ? t with { DependsOn = [.. t.DependsOn, new DependencySpec(id)] }
                : t)
            .Append(integration)
            .ToList();
        return FixOutcome.Done(state with { Plan = Rebuilt(plan, tasks) });
    }

    /// <summary>The steps that verify a new integration: the test steps of the profile, with the first build before them unless each requires one.</summary>
    private static IReadOnlyList<string> IntegrationSteps(IReadOnlyList<VerificationStepFacts> steps)
    {
        var build = steps.FirstOrDefault(s => s.Builds);
        var tests = steps.Where(s => s.RunsTests).ToList();
        if (tests.Count == 0)
        {
            return build is null ? (steps.Count > 0 ? [steps[0].Id] : []) : [build.Id];
        }

        var builtByRequirement = build is not null && tests.All(t => (t.Requires ?? []).Contains(build.Id, StringComparer.Ordinal));
        return build is null || builtByRequirement ? [.. tests.Select(t => t.Id)] : [build.Id, .. tests.Select(t => t.Id)];
    }

    /// <summary>The build step put before the first test step of the task (or at the start), so it builds on the copy it tests.</summary>
    private static IReadOnlyList<string> Inserted(IReadOnlyList<string> steps, string build, FixEnvironment environment)
    {
        var tests = environment.Steps.Where(s => s.RunsTests).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var at = steps.ToList().FindIndex(tests.Contains);
        var list = steps.ToList();
        list.Insert(at < 0 ? 0 : at, build);
        return list;
    }

    private static FixOutcome AddDependency(MontageEditState state, TaskId task, TaskId dependsOn)
    {
        if (state.Plan is not { } plan)
        {
            return FixOutcome.Refused(state, FixRefusals.NoPlan);
        }

        if (!plan.Tasks.Any(t => t.Id == task) || !plan.Tasks.Any(t => t.Id == dependsOn))
        {
            return FixOutcome.Refused(state, FixRefusals.UnknownTask);
        }

        return DraftFacts.DependencyFix(plan, task, dependsOn) is null
            ? FixOutcome.Refused(state, FixRefusals.Dependency)
            : EditTask(state, task, t => t with { DependsOn = [.. t.DependsOn, new DependencySpec(dependsOn)] });
    }

    private static FixOutcome AddJudge(MontageEditState state, AddJudgeFix fix, FixEnvironment environment)
    {
        var template = state.Roster.FirstOrDefault(a => a.Role == AgentRole.Judge) ?? environment.JudgeTemplate;
        if (template is null)
        {
            return FixOutcome.Refused(state, FixRefusals.NoJudgeTemplate);
        }

        var taken = state.Roster.Select(a => a.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = Enumerable.Range(2, 98).First(n => !taken.Contains($"{template.Id.Value}-{n.ToString(CultureInfo.InvariantCulture)}"));
        var suffix = number.ToString(CultureInfo.InvariantCulture);
        var judge = template with
        {
            Id = new AgentId($"{template.Id.Value}-{suffix}"),
            Name = $"{template.Name} {suffix}",
            Role = AgentRole.Judge,
            Model = new ModelBinding(fix.Provider, fix.Vendor),
            PathGrants = [],
            Memory = MemoryPolicy.None,
            Effort = null,
        };
        return FixOutcome.Done(state with { Roster = [.. state.Roster, judge] });
    }

    private static bool Known(MontageEditState state, AgentId agent) => state.Roster.Any(a => a.Id == agent);

    /// <summary>The state with <paramref name="task"/> changed by <paramref name="change"/>; null from it means there is nothing to change.</summary>
    private static FixOutcome EditTask(MontageEditState state, TaskId task, Func<TaskSpec, TaskSpec?> change)
    {
        if (state.Plan is not { } plan)
        {
            return FixOutcome.Refused(state, FixRefusals.NoPlan);
        }

        if (plan.Tasks.FirstOrDefault(t => t.Id == task) is not { } spec)
        {
            return FixOutcome.Refused(state, FixRefusals.UnknownTask);
        }

        if (change(spec) is not { } changed)
        {
            return FixOutcome.Refused(state, FixRefusals.NothingToChange);
        }

        return FixOutcome.Done(state with { Plan = Rebuilt(plan, [.. plan.Tasks.Select(t => ReferenceEquals(t, spec) ? changed : t)]) });
    }

    /// <summary>The state with <paramref name="agent"/> changed by <paramref name="change"/>; null from it means there is nothing to change.</summary>
    private static FixOutcome EditAgent(MontageEditState state, AgentId agent, Func<AgentDefinition, AgentDefinition?> change)
    {
        if (state.Roster.FirstOrDefault(a => a.Id == agent) is not { } definition)
        {
            return FixOutcome.Refused(state, FixRefusals.UnknownAgent);
        }

        if (change(definition) is not { } changed)
        {
            return FixOutcome.Refused(state, FixRefusals.NothingToChange);
        }

        return FixOutcome.Done(state with { Roster = [.. state.Roster.Select(a => ReferenceEquals(a, definition) ? changed : a)] });
    }

    private static PlanDefinition Rebuilt(PlanDefinition plan, IReadOnlyList<TaskSpec> tasks) =>
        PlanDefinition.Create(plan.Summary, tasks, plan.Risks, plan.Source, plan.Author);
}

/// <summary>
/// The undo and redo of the edits of a draft (docs/17 D-18: Ctrl+Z and Ctrl+Y; there was none before): a history of immutable
/// states. Doing an edit keeps the state before it and forgets what was undone; undoing goes back one, redoing forward one.
/// Pure and in memory, so the fixes of one click and any edit of the canvas undo the same way. Not thread-safe: the canvas
/// uses it from its own thread.
/// </summary>
/// <typeparam name="T">The state of the draft: an immutable value.</typeparam>
public sealed class EditHistory<T>
    where T : class
{
    private readonly Stack<T> _undo = new();
    private readonly Stack<T> _redo = new();

    /// <param name="initial">The state the history starts from.</param>
    /// <param name="capacity">How many edits it keeps to undo (100 by default); the oldest are forgotten.</param>
    public EditHistory(T initial, int capacity = 100)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Current = initial;
        Capacity = capacity;
    }

    public T Current { get; private set; }

    public int Capacity { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Makes <paramref name="next"/> the current state; what was undone can no longer be redone.</summary>
    public void Do(T next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _undo.Push(Current);
        if (_undo.Count > Capacity)
        {
            var kept = _undo.Reverse().Skip(_undo.Count - Capacity).ToList();
            _undo.Clear();
            foreach (var state in kept)
            {
                _undo.Push(state);
            }
        }

        _redo.Clear();
        Current = next;
    }

    /// <summary>Goes back to the state before the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (!_undo.TryPop(out var previous))
        {
            return false;
        }

        _redo.Push(Current);
        Current = previous;
        return true;
    }

    /// <summary>Does again the last edit undone; false when there is none.</summary>
    public bool Redo()
    {
        if (!_redo.TryPop(out var next))
        {
            return false;
        }

        _undo.Push(Current);
        Current = next;
        return true;
    }
}
