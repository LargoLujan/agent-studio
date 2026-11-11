using AgentStudio.Domain.Work;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>The budget of a milestone as stored in <c>milestones.budget_json</c>: a limit that is missing stays missing, never a zero.</summary>
internal sealed record MilestoneBudgetJson(long? MaxTokens, int? MaxAgentRuns, BudgetPeriod Period)
{
    public static MilestoneBudgetJson From(WorkBudget budget) => new(budget.MaxTokens, budget.MaxAgentRuns, budget.Period);

    public WorkBudget ToDomain() => new(MaxTokens, MaxAgentRuns, Period);
}
