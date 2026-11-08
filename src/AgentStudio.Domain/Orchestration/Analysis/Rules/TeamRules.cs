using System.Runtime.CompilerServices;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

// The rules of the analyzer about the team (docs/17 D-09, D-11 and D-18). They work with or without a plan.

/// <summary>
/// <see cref="FindingCodes.JudgeSameVendor"/> (<c>JudgeVendorNotIndependent</c>, ADR-0014): a judge of the vendor of someone
/// who produced the work. With a plan, each Review task the validator finds so, on the vendors of the orchestrator and of the
/// executors of the plan. Without one, while the team is assembled (D-09), each judge when no judge of the team is of a vendor
/// other than the orchestrator's and every executor's. Error. Its fix has the review done by a judge of the team of another
/// vendor (<see cref="SwitchJudgeFix"/>, only with a plan) or adds one (<see cref="AddJudgeFix"/>) of the vendor nobody
/// produced with; none when both vendors produce. Nodes: the review task (with a plan) and the judge. Arguments: the judge id,
/// its vendor.
/// </summary>
public sealed class JudgeSameVendorRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.JudgeSameVendor];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var agents = DraftFacts.Agents(draft);
        var roster = draft.Context.Roster ?? [];
        var findings = new List<Finding>();
        if (draft.Plan is { } plan)
        {
            var producers = roster.Where(a => a.Role == AgentRole.Orchestrator).Select(a => a.Model.Vendor)
                .Concat(plan.Author is { } author && agents.TryGetValue(author, out var planner) ? [planner.Model.Vendor] : [])
                .Concat(plan.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork && t.Agent is { } a && agents.ContainsKey(a)).Select(t => agents[t.Agent!.Value].Model.Vendor))
                .ToHashSet();
            var independent = roster.FirstOrDefault(a => TeamRoles.Judges(a.Role) && !producers.Contains(a.Model.Vendor));
            foreach (var violation in DraftFacts.Violations(draft).Where(v => v.Code == PlanViolationCode.JudgeVendorNotIndependent && v.Task is not null))
            {
                var review = plan.Tasks.First(t => t.Id == violation.Task);
                var judge = agents[review.Agent!.Value];
                Fix? fix = independent is not null ? new SwitchJudgeFix(review.Id, independent.Id) : AddJudge(producers);
                findings.Add(Finding.Of(FindingCodes.JudgeSameVendor, FindingSeverity.Error, [review.Id.Value, judge.Id.Value], fix, judge.Id.Value, judge.Model.Vendor.Value));
            }

            return findings;
        }

        var teamProducers = roster.Where(a => a.Role == AgentRole.Orchestrator || TeamRoles.Writes(a.Role)).Select(a => a.Model.Vendor).ToHashSet();
        var judges = roster.Where(a => a.Role == AgentRole.Judge).ToList();
        if (judges.Count == 0 || judges.Any(j => !teamProducers.Contains(j.Model.Vendor)))
        {
            return [];
        }

        var add = AddJudge(teamProducers);
        return [.. judges.Select(j => Finding.Of(FindingCodes.JudgeSameVendor, FindingSeverity.Error, [j.Id.Value], add, j.Id.Value, j.Model.Vendor.Value))];
    }

    private static AddJudgeFix? AddJudge(IReadOnlySet<VendorId> producers) =>
        DraftFacts.JudgeVendors.Where(v => !producers.Contains(v.Vendor)).Select(v => new AddJudgeFix(v.Provider, v.Vendor)).FirstOrDefault();
}

/// <summary>
/// <see cref="FindingCodes.ParallelCapacity"/> (D-09): more agents that write on one CLI than can work at once, the smaller of
/// the tasks at once of the run and the concurrency of the CLI: «con 4 ejecutores de Claude trabajan 2 a la vez; el resto
/// espera turno». Warning, no fix: it informs. One per CLI, in the order the CLIs first appear in the team. Nodes: the agents
/// that write on it. Arguments: how many there are, the CLI id, how many work at once.
/// </summary>
public sealed class ParallelCapacityRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.ParallelCapacity];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var findings = new List<Finding>();
        foreach (var group in (draft.Context.Roster ?? []).Where(a => TeamRoles.Writes(a.Role)).DistinctBy(a => a.Id).GroupBy(a => a.Model.Provider))
        {
            var capacity = Math.Max(1, Math.Min(draft.Run.ConcurrencyOf(group.Key), draft.Run.MaxParallelTasks));
            var count = group.Count();
            if (count > capacity)
            {
                findings.Add(Finding.Of(FindingCodes.ParallelCapacity, FindingSeverity.Warning, group.Select(a => a.Id.Value), null, count, group.Key.Value, capacity));
            }
        }

        return findings;
    }
}

/// <summary>
/// <see cref="FindingCodes.HighRiskCapability"/> (ADR-0018, point 3): an agent that writes with a shell (<c>Bash</c>), with the
/// network (<c>WebFetch</c>, <c>WebSearch</c>) or on Codex, whose sandbox always has a shell. With a plan, only the agents
/// that have a task in it. Warning: if it stays, approving the plan asks Windows Hello (D-37). For a tool the fix takes it away
/// (<see cref="RemoveCapabilityFix"/>, which never asks Hello); Codex has none. Nodes: the agent. Arguments: the agent id, the
/// capability (the tool as the template names it without its pattern, or the CLI id <c>codex-cli</c>).
/// </summary>
public sealed class HighRiskCapabilityRule : IMontageRule
{
    /// <summary>The tools that change the threat model (ADR-0018, point 3): the shell and the network.</summary>
    public static IReadOnlyList<string> HighRiskTools { get; } = ["Bash", "WebFetch", "WebSearch"];

    public IReadOnlyList<string> Codes { get; } = [FindingCodes.HighRiskCapability];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var inPlan = draft.Plan?.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork).Select(t => t.Agent).OfType<AgentId>().ToHashSet();
        var findings = new List<Finding>();
        foreach (var agent in (draft.Context.Roster ?? []).Where(a => TeamRoles.Writes(a.Role)).DistinctBy(a => a.Id))
        {
            if (inPlan is not null && !inPlan.Contains(agent.Id))
            {
                continue;
            }

            foreach (var tool in agent.AllowedTools ?? [])
            {
                var name = DraftFacts.ToolName(tool);
                if (HighRiskTools.Contains(name, StringComparer.Ordinal))
                {
                    findings.Add(Finding.Of(FindingCodes.HighRiskCapability, FindingSeverity.Warning, [agent.Id.Value], new RemoveCapabilityFix(agent.Id, tool), agent.Id.Value, name));
                }
            }

            var writesOnCodex = agent.Model.Provider == ProviderId.CodexCli
                && (draft.Plan is null || draft.Plan.Tasks.Any(t => DraftFacts.Writes(t) && t.Agent == agent.Id));
            if (writesOnCodex)
            {
                findings.Add(Finding.Of(FindingCodes.HighRiskCapability, FindingSeverity.Warning, [agent.Id.Value], null, agent.Id.Value, agent.Model.Provider.Value));
            }
        }

        return findings;
    }
}

/// <summary>The violations of the validator on a draft, computed once per draft for every rule that reads them.</summary>
internal static class DraftViolations
{
    private static readonly ConditionalWeakTable<MontageDraft, IReadOnlyList<PlanViolation>> Cache = [];

    public static IReadOnlyList<PlanViolation> Of(MontageDraft draft) =>
        draft.Plan is not { } plan ? [] : Cache.GetValue(draft, d => PlanValidator.Validate(plan, d.Context));
}
