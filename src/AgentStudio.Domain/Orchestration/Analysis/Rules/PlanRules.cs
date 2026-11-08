using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

// The rules of the analyzer about the plan itself (docs/17 D-18). Each says, in its summary, the arguments of its sentence in
// their order: that is the contract the band of the canvas paints with the resources (Text_<code>).

/// <summary>
/// The validator, wrapped (docs/17 D-18): every violation of <see cref="PlanValidator"/> is a finding with the code
/// <see cref="FindingCodes.ForViolation"/>, an error, on its task. The violations that have a code of D-18 of their own
/// (<see cref="Mapped"/>) are left to the rule of that code, which adds the fix: one problem, one finding. Nodes: the task, when
/// there is one. Arguments: the task id, when there is one.
/// </summary>
public sealed class ValidatorRule : IMontageRule
{
    /// <summary>The violations a rule of D-18 reports with its own code and fix, instead of this one.</summary>
    public static IReadOnlyDictionary<PlanViolationCode, string> Mapped { get; } = new Dictionary<PlanViolationCode, string>
    {
        [PlanViolationCode.JudgeVendorNotIndependent] = FindingCodes.JudgeSameVendor,
        [PlanViolationCode.OverlappingWriteScopes] = FindingCodes.ParallelWriteOverlap,
        [PlanViolationCode.WriteScopeOutsideAgentArea] = FindingCodes.WriteOutsideArea,
    };

    public IReadOnlyList<string> Codes { get; } =
        [.. Enum.GetValues<PlanViolationCode>().Where(c => !Mapped.ContainsKey(c)).Select(FindingCodes.ForViolation)];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return DraftFacts.Violations(draft)
            .Where(v => !Mapped.ContainsKey(v.Code))
            .Select(v => v.Task is { } task
                ? Finding.Of(FindingCodes.ForViolation(v.Code), FindingSeverity.Error, [task.Value], null, task.Value)
                : Finding.Of(FindingCodes.ForViolation(v.Code), FindingSeverity.Error, []))
            .ToList();
    }
}

/// <summary>
/// <see cref="FindingCodes.NoIntegration"/> (B10): tasks write code and no task integrates it, so nothing would reach the
/// person's branch. Error, with <see cref="AddIntegrationFix"/>. Nodes: the tasks that write. Arguments: none.
/// </summary>
public sealed class NoIntegrationRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.NoIntegration];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plan is not { } plan || plan.Tasks.Any(t => t.Kind == WorkTaskKind.Integration))
        {
            return [];
        }

        var writers = plan.Tasks.Where(DraftFacts.Writes).Select(t => t.Id.Value).Distinct(StringComparer.Ordinal).ToList();
        return writers.Count == 0 ? [] : [Finding.Of(FindingCodes.NoIntegration, FindingSeverity.Error, writers, new AddIntegrationFix())];
    }
}

/// <summary>
/// <see cref="FindingCodes.TestWithoutBuild"/> (B6): a task runs a test step and nothing builds before it on the same copy:
/// neither a step the task names earlier nor one the test step requires (<see cref="VerificationStepFacts.Requires"/>, which
/// the system runs first). Only with the profile known (<see cref="MontageDraft.Steps"/>) and when it has a build step: a
/// profile without one (pytest) tests without building. Error, with <see cref="AddBuildStepFix"/> and the first build step of
/// the profile. One per task, for its first such step. Nodes: the task. Arguments: the task id, the test step.
/// </summary>
public sealed class TestWithoutBuildRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.TestWithoutBuild];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var steps = draft.Steps.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var build = draft.Steps.FirstOrDefault(s => s.Builds);
        if (draft.Plan is not { } plan || build is null)
        {
            return [];
        }

        var findings = new List<Finding>();
        foreach (var task in plan.Tasks)
        {
            // A tool task runs its tool after the steps it requires (Workers), so only the steps a task names are read here.
            var named = task.VerificationSteps;
            for (var i = 0; i < named.Count; i++)
            {
                if (!steps.TryGetValue(named[i], out var step) || !step.RunsTests)
                {
                    continue;
                }

                var built = Closure(steps, named[i]).Any(s => s.Builds) || named.Take(i).Any(n => (steps.TryGetValue(n, out var earlier) && earlier.Builds) || Closure(steps, n).Any(s => s.Builds));
                if (!built)
                {
                    findings.Add(Finding.Of(FindingCodes.TestWithoutBuild, FindingSeverity.Error, [task.Id.Value], new AddBuildStepFix(task.Id, build.Id), task.Id.Value, named[i]));
                    break;
                }
            }
        }

        return findings;
    }

    /// <summary>The steps <paramref name="id"/> requires, through any path (not itself), as the system runs them before it.</summary>
    private static IEnumerable<VerificationStepFacts> Closure(IReadOnlyDictionary<string, VerificationStepFacts> steps, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var pending = new Stack<string>(steps.TryGetValue(id, out var first) ? first.Requires ?? [] : []);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!seen.Add(next) || !steps.TryGetValue(next, out var step))
            {
                continue;
            }

            yield return step;
            foreach (var required in step.Requires ?? [])
            {
                pending.Push(required);
            }
        }
    }
}

/// <summary>
/// <see cref="FindingCodes.ParallelWriteOverlap"/> (<c>OverlappingWriteScopes</c>): two tasks that write may run at once and
/// their scopes overlap. Error, with <see cref="AddDependencyFix"/>: the later of the two in the order of the plan after the
/// earlier («T4 después de T2»). Only on a sound graph. Nodes: both tasks. Arguments: the earlier task id, the later one.
/// </summary>
public sealed class ParallelWriteOverlapRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.ParallelWriteOverlap];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plan is not { } plan || DraftFacts.Ancestors(plan) is not { } ancestors)
        {
            return [];
        }

        var writers = plan.Tasks.Where(DraftFacts.Writes).ToList();
        var findings = new List<Finding>();
        for (var i = 0; i < writers.Count; i++)
        {
            for (var j = i + 1; j < writers.Count; j++)
            {
                var (a, b) = (writers[i], writers[j]);
                if (DraftFacts.Concurrent(ancestors, a.Id, b.Id) && WriteScope.Overlaps(a.WriteScope, b.WriteScope))
                {
                    findings.Add(Finding.Of(FindingCodes.ParallelWriteOverlap, FindingSeverity.Error, [a.Id.Value, b.Id.Value], DraftFacts.DependencyFix(plan, b.Id, a.Id), a.Id.Value, b.Id.Value));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// <see cref="FindingCodes.WriteOutsideArea"/> (D-11, <c>WriteScopeOutsideAgentArea</c>): an entry of the scope of a task is
/// outside the folders of its agent. Error, one per entry. Its fix gives the task to another agent that writes and whose
/// folders hold its whole scope (one of the same vendor first, so the judge stays independent), or else adds a folder to its
/// agent (<see cref="WidenAgentFoldersFix"/>, which asks Windows Hello): the main folder of the repository that holds the
/// entry, or the entry itself when it is a folder, or its parent folder. Nodes: the task and its agent. Arguments: the task
/// id, the agent id, the entry.
/// </summary>
public sealed class WriteOutsideAreaRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.WriteOutsideArea];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plan is not { } plan)
        {
            return [];
        }

        var agents = DraftFacts.Agents(draft);
        var findings = new List<Finding>();
        foreach (var task in plan.Tasks.Where(DraftFacts.Writes))
        {
            if (task.Agent is not { } id || !agents.TryGetValue(id, out var agent))
            {
                continue;
            }

            var outside = AgentWriteArea.Outside(agent, task.WriteScope);
            if (outside.Count == 0)
            {
                continue;
            }

            var other = (draft.Context.Roster ?? [])
                .Where(a => a.Id != agent.Id && TeamRoles.Writes(a.Role) && AgentWriteArea.Outside(a, task.WriteScope).Count == 0)
                .OrderBy(a => a.Model.Vendor == agent.Model.Vendor ? 0 : 1)
                .FirstOrDefault();
            foreach (var entry in outside)
            {
                Fix? fix = other is not null ? new ReassignTaskFix(task.Id, other.Id) : FolderFor(draft.Team.Repository, entry) is { } folder ? new WidenAgentFoldersFix(agent.Id, folder) : null;
                findings.Add(Finding.Of(FindingCodes.WriteOutsideArea, FindingSeverity.Error, [task.Id.Value, agent.Id.Value], fix, task.Id.Value, agent.Id.Value, entry));
            }
        }

        return findings;
    }

    /// <summary>The folder that would hold <paramref name="entry"/>: a main folder of the repository, the entry when it is a folder, or its parent; null for a file at the root.</summary>
    private static string? FolderFor(RepositoryNode? repository, string entry)
    {
        if (!WriteScope.TryNormalize(entry, out var normalized, out _))
        {
            return null;
        }

        foreach (var main in repository?.MainFolders ?? [])
        {
            if (WriteScope.TryNormalize(main, out var folder, out _))
            {
                var withSlash = folder.EndsWith('/') ? folder : folder + "/";
                if (normalized.StartsWith(withSlash, StringComparison.OrdinalIgnoreCase))
                {
                    return withSlash;
                }
            }
        }

        if (normalized.EndsWith('/'))
        {
            return normalized;
        }

        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? null : normalized[..(slash + 1)];
    }
}
