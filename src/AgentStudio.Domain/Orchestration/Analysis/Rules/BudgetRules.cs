using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

// The rules of the analyzer about what the run would spend (docs/17 D-18 and D-23): the estimate of the plan against its
// budget, and the windows of quota of the CLIs of the team. Tokens are never turned into a share of quota.

/// <summary>
/// <see cref="FindingCodes.CostOverBudget"/>: the estimator expects the plan to take more agent runs than the budget of the run
/// (<c>MaxAgentRuns</c>). Only with a plan and an estimate (at least three references). Warning. Its fix raises the budget to
/// the estimate (<see cref="RaiseRunBudgetFix"/>). Nodes: none. Arguments: the agent runs estimated, the budget.
/// </summary>
public sealed class CostOverBudgetRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.CostOverBudget];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plan is null || draft.Learned?.EstimatedAgentRuns is not { } estimated || estimated <= draft.Run.MaxAgentRuns)
        {
            return [];
        }

        return [Finding.Of(FindingCodes.CostOverBudget, FindingSeverity.Warning, [], new RaiseRunBudgetFix((int)Math.Min(estimated, int.MaxValue)), estimated, draft.Run.MaxAgentRuns)];
    }
}

/// <summary>
/// <see cref="FindingCodes.QuotaWindowNear"/>: a window of quota of a CLI the team uses is over the warning threshold of the
/// bars (<see cref="Metrics.ContextHealthThresholds.Warning"/>, 70 % by default). Only windows with a value. Warning, no fix: it
/// informs («puede esperar hasta las HH:MM»). One per window, in the order given. Nodes: the agents on that CLI. Arguments:
/// the CLI id, the window as it was read (<c>5h</c>, <c>7d</c>), the use in whole percent and, when the CLI said it, the
/// instant it resets; <see cref="MontageTexts.SentenceOf"/> says which sentence fits.
/// </summary>
public sealed class QuotaWindowNearRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.QuotaWindowNear];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var byProvider = (draft.Context.Roster ?? []).DistinctBy(a => a.Id).GroupBy(a => a.Model.Provider).ToDictionary(g => g.Key, g => g.Select(a => a.Id.Value).ToList());
        var findings = new List<Finding>();
        foreach (var window in draft.Quota ?? [])
        {
            if (!byProvider.TryGetValue(window.Provider, out var agents) || window.Used.Value is not { } used || used < draft.Thresholds.Warning)
            {
                continue;
            }

            var percent = (int)Math.Round(used * 100, MidpointRounding.AwayFromZero);
            findings.Add(window.ResetsAt is { } resets
                ? Finding.Of(FindingCodes.QuotaWindowNear, FindingSeverity.Warning, agents, null, window.Provider.Value, window.Window, percent, resets)
                : Finding.Of(FindingCodes.QuotaWindowNear, FindingSeverity.Warning, agents, null, window.Provider.Value, window.Window, percent));
        }

        return findings;
    }
}
