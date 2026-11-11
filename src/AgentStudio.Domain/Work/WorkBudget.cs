using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work;

/// <summary>The span a budget covers (docs/16 D-05). A milestone budget is for the whole milestone: <see cref="Total"/>.</summary>
public enum BudgetPeriod
{
    /// <summary>All the work, without a period: the only one a milestone has.</summary>
    Total,
    Day,
    Week,
    Month,
}

/// <summary>Which limit of a budget a projection reaches (<see cref="WorkBudget.Check"/>).</summary>
public enum BudgetLimit
{
    Tokens,
    AgentRuns,
}

/// <summary>What has been used, or what an attempt about to launch is projected to use, against a budget.</summary>
public readonly record struct BudgetUsage(long Tokens, int AgentRuns);

/// <summary>The verdict of <see cref="WorkBudget.Check"/>: the attempt may launch, or the limit its projection passes.</summary>
public readonly record struct BudgetCheck(bool Allowed, BudgetLimit? Reached)
{
    public static readonly BudgetCheck Within = new(true, null);
}

/// <summary>
/// An optional budget of a project or a milestone (docs/16 D-05, F9-8): tokens, agent runs or both, per period. It
/// shares out the person's own use of their subscription and never widens it: the quota is the account's. Before an
/// attempt launches, the engine projects what has been used with what the attempt is estimated to take (an attempt
/// without an estimate counts as unknown and never as less than it will take, as <c>DebateBudget</c> does) and does not
/// launch it when that passes a limit (<see cref="Check"/>), and says why.
/// </summary>
/// <param name="MaxTokens">The most tokens, input and output together as the usage panel counts them; null for no limit of tokens.</param>
/// <param name="MaxAgentRuns">The most launches of an agent; null for no limit of launches.</param>
/// <param name="Period">The span it covers; a milestone's is <see cref="BudgetPeriod.Total"/>.</param>
public sealed record WorkBudget(long? MaxTokens, int? MaxAgentRuns, BudgetPeriod Period = BudgetPeriod.Total)
{
    /// <summary>The codes of what is wrong with the budget; empty means valid. A budget with no limit is not a budget.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxTokens is null && MaxAgentRuns is null)
        {
            problems.Add("budget.no-limit");
        }

        if (MaxTokens is <= 0)
        {
            problems.Add("budget.tokens-not-positive");
        }

        if (MaxAgentRuns is <= 0)
        {
            problems.Add("budget.agent-runs-not-positive");
        }

        if (!Enum.IsDefined(Period))
        {
            problems.Add("budget.period-unknown");
        }

        return problems;
    }

    /// <summary>Refuses a budget that does not validate, with the first code as the rule.</summary>
    /// <exception cref="DomainRuleViolationException">The budget has a problem.</exception>
    public void EnsureValid()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new DomainRuleViolationException(problems[0], $"The budget is not valid: {string.Join(", ", problems)}.");
        }
    }

    /// <summary>
    /// Whether an attempt may launch: what has been <paramref name="used"/> plus what the attempt is <paramref name="projected"/>
    /// to take must not pass a limit of the budget. A limit the budget does not have lets everything through. Reaching a limit
    /// exactly is within it. The limit passed is the first of tokens and then runs.
    /// </summary>
    public BudgetCheck Check(BudgetUsage used, BudgetUsage projected)
    {
        if (MaxTokens is { } tokens && used.Tokens + projected.Tokens > tokens)
        {
            return new BudgetCheck(false, BudgetLimit.Tokens);
        }

        if (MaxAgentRuns is { } runs && used.AgentRuns + projected.AgentRuns > runs)
        {
            return new BudgetCheck(false, BudgetLimit.AgentRuns);
        }

        return BudgetCheck.Within;
    }
}
