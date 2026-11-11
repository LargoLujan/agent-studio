using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// The decision of the budget gate for one attempt about to launch (docs/16 D-05, F9-8). <see cref="Reason"/> is the sentence the
/// person reads (Spanish) and <see cref="Code"/> a stable one for the alert in the log; both are null when it may launch.
/// </summary>
/// <param name="Allowed">The attempt may launch.</param>
/// <param name="Scope">Whose budget decided: <c>project</c> or the id of the milestone; null when allowed.</param>
/// <param name="Reached">The limit the projection passes; null when allowed or when the gate could not tell (<see cref="Code"/> says why).</param>
/// <param name="ProjectionUnknown">The attempt had no estimate, so the check was only against what was already used.</param>
public sealed record BudgetGateVerdict(bool Allowed, string? Scope, BudgetLimit? Reached, string? Code, string? Reason, bool ProjectionUnknown = false)
{
    public static readonly BudgetGateVerdict Free = new(true, null, null, null, null);
}

/// <summary>
/// The budget of the project and of the milestone, applied before an agent attempt launches (docs/16 D-05, F9-8, ADR-0040). The patterns
/// of the other gates of the engine: it asks only through ports, it launches nothing and it spends no quota (the estimate and the usage
/// are read from the verified log), and it <b>fails closed</b>. With no budget there is nothing to check and it lets everything through
/// without reading anything else; with a budget, anything it cannot tell (the budget cannot be read, the log of the project is not
/// trusted, the usage cannot be projected) refuses, and says so.
/// </summary>
/// <remarks>
/// <para>What an attempt would add is its estimated total (the 75th percentile of the tokens of similar attempts, the same figure the
/// window of a question shows) and one run, plus the same for each attempt already in flight, which the log does not count until it ends.
/// An attempt without an estimate (fewer than three references, or an estimator that cannot answer) is <i>unknown</i>: it is never taken
/// for zero without saying so. It may launch only while what has been used is still under the limit of tokens, and the verdict says that
/// the projection was unknown; a budget that refused every attempt of a project with no history would be one nobody could use.</para>
/// <para>What has been used is read with <see cref="IUsageReport"/>, as the panel counts it (input and output tokens of the attempts that
/// ended, and their number): the budget of a project over its period, that of a milestone over all its attempts (those of the runs linked
/// to it and their follow-ups, <see cref="MilestoneMembership"/>). An attempt whose tokens the log does not give counts for what it
/// does give, and the verdict says how many were left out.</para>
/// <para>The judge is not gated: like the judgment of a debate, it cannot run out of budget, or a run could never end.</para>
/// </remarks>
public sealed class ProjectBudgetGate(
    IProjectBudgetStore? budgets,
    IUsageReport? usage,
    IUsageEstimator? estimator,
    IMilestoneRegistry? milestones,
    IProjectLogReader? logs,
    TimeProvider time)
{
    /// <summary>The code of the alert and of the sentence when a limit would be passed.</summary>
    public const string ExceededCode = "project.budget-exceeded";

    /// <summary>The code when the gate could not read what it needs, so it refuses (fails closed).</summary>
    public const string UnreadableCode = "project.budget-unreadable";

    /// <summary>
    /// Whether the attempt described by <paramref name="request"/> may launch in <paramref name="run"/>, with <paramref name="inFlightAgentRuns"/>
    /// attempts of the run already started and not yet in the log.
    /// </summary>
    public async Task<BudgetGateVerdict> CheckAsync(ProjectContext project, string run, UsageEstimateRequest request, int inFlightAgentRuns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var limits = await LimitsAsync(project, run, ct).ConfigureAwait(false);
            if (limits.Count == 0)
            {
                return BudgetGateVerdict.Free;
            }

            if (usage is null)
            {
                return Unreadable("no hay con qué leer lo gastado");
            }

            var report = await usage.QueryAsync([project.RootPath], new UsageQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, time.LocalTimeZone, []), ct).ConfigureAwait(false);
            if (report.Excluded.Count > 0)
            {
                return Unreadable("el registro del proyecto no es de confianza: " + report.Excluded[0].Reason);
            }

            var estimated = await EstimateAsync(request, ct).ConfigureAwait(false);
            var pending = Math.Max(0, inFlightAgentRuns) + 1;
            var projected = new BudgetUsage(estimated is { } tokens ? checked(tokens * pending) : 0, pending);
            var now = time.GetLocalNow();
            foreach (var (scope, budget, members) in limits)
            {
                var attempts = report.Attempts.Where(a => members(a) && InPeriod(a, budget.Period, now)).ToList();
                var used = new BudgetUsage(attempts.Sum(a => TokensOf(a) ?? 0), attempts.Count);
                var check = budget.Check(used, projected);
                if (check.Allowed && estimated is null && budget.MaxTokens is { } max && used.Tokens >= max)
                {
                    check = new BudgetCheck(false, BudgetLimit.Tokens);
                }

                if (!check.Allowed)
                {
                    return Exceeded(scope, budget, check.Reached!.Value, used, projected, estimated is null, attempts.Count(a => TokensOf(a) is null));
                }
            }

            return estimated is null && limits.Any(l => l.Budget.MaxTokens is not null) ? BudgetGateVerdict.Free with { ProjectionUnknown = true } : BudgetGateVerdict.Free;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(ex.Message);
        }
    }

    /// <summary>The budgets that apply to the run: the project's and those of the milestones it belongs to, each with how to tell its attempts.</summary>
    private async Task<List<(string Scope, WorkBudget Budget, Func<UsageAttempt, bool> Members)>> LimitsAsync(ProjectContext project, string run, CancellationToken ct)
    {
        var limits = new List<(string, WorkBudget, Func<UsageAttempt, bool>)>();
        if (budgets is not null && await budgets.LoadAsync(project, ct).ConfigureAwait(false) is { } own)
        {
            own.EnsureValid();
            limits.Add(("project", own, _ => true));
        }

        if (milestones is not null && logs is not null)
        {
            var snapshot = await logs.ReadAsync(project.RootPath, ct).ConfigureAwait(false);
            if (!snapshot.IsTrusted)
            {
                throw new InvalidOperationException("The log of the project is not trusted.");
            }

            foreach (var id in MilestoneMembership.Of(snapshot.Entries).GetValueOrDefault(run) ?? [])
            {
                if (await milestones.FindAsync(project.ProjectId, new MilestoneId(id), ct).ConfigureAwait(false) is { Budget: { } budget })
                {
                    budget.EnsureValid();
                    var milestone = id;
                    limits.Add((milestone, budget with { Period = BudgetPeriod.Total }, a => a.Milestones.Contains(milestone, StringComparer.Ordinal)));
                }
            }
        }

        return limits;
    }

    /// <summary>The estimated total tokens of the attempt (the 75th percentile); null when the estimate is unknown or cannot be asked.</summary>
    private async Task<long?> EstimateAsync(UsageEstimateRequest request, CancellationToken ct)
    {
        if (estimator is null)
        {
            return null;
        }

        try
        {
            var estimate = await estimator.EstimateAsync(request, ct).ConfigureAwait(false);
            return estimate is { IsKnown: true, TotalTokens: { IsKnown: true } total } ? total.High.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // the estimator could not answer: the attempt has no estimate, which the verdict says
        }
    }

    private static long? TokensOf(UsageAttempt attempt) =>
        attempt.Own.InputTokens.Value is { } input && attempt.Own.OutputTokens.Value is { } output ? input + output : null;

    /// <summary>Whether the attempt ended in the period that contains <paramref name="now"/> (the day, the ISO week from Monday or the month, in the local zone).</summary>
    private static bool InPeriod(UsageAttempt attempt, BudgetPeriod period, DateTimeOffset now)
    {
        if (period == BudgetPeriod.Total)
        {
            return true;
        }

        var finished = attempt.FinishedAt.ToOffset(now.Offset).DateTime;
        var today = now.DateTime;
        return period switch
        {
            BudgetPeriod.Day => finished.Date == today.Date,
            BudgetPeriod.Week => System.Globalization.ISOWeek.GetYear(finished) == System.Globalization.ISOWeek.GetYear(today)
                && System.Globalization.ISOWeek.GetWeekOfYear(finished) == System.Globalization.ISOWeek.GetWeekOfYear(today),
            BudgetPeriod.Month => finished.Year == today.Year && finished.Month == today.Month,
            _ => false,
        };
    }

    private static BudgetGateVerdict Unreadable(string why) =>
        new(false, null, null, UnreadableCode, "No se lanza el intento: no se pudo comprobar el presupuesto (" + why + "), y sin saberlo no se arriesga la cuota.");

    private static BudgetGateVerdict Exceeded(string scope, WorkBudget budget, BudgetLimit reached, BudgetUsage used, BudgetUsage projected, bool unknown, int withoutTokens)
    {
        var whose = scope == "project" ? "del proyecto" : $"del hito {scope}";
        var period = budget.Period switch
        {
            BudgetPeriod.Day => " de hoy",
            BudgetPeriod.Week => " de esta semana",
            BudgetPeriod.Month => " de este mes",
            _ => string.Empty,
        };
        var text = reached == BudgetLimit.Tokens
            ? string.Create(CultureInfo.InvariantCulture, $"el presupuesto{period} {whose} es de {budget.MaxTokens} tokens y ya van {used.Tokens}{(unknown ? " (sin estimación de este intento)" : $"; con este intento serían {used.Tokens + projected.Tokens}")}")
            : string.Create(CultureInfo.InvariantCulture, $"el presupuesto{period} {whose} es de {budget.MaxAgentRuns} ejecuciones de agente y ya van {used.AgentRuns}; con este intento serían {used.AgentRuns + projected.AgentRuns}");
        var partial = withoutTokens > 0 && reached == BudgetLimit.Tokens ? string.Create(CultureInfo.InvariantCulture, $" ({withoutTokens} intentos sin cifra de tokens no cuentan)") : string.Empty;
        return new BudgetGateVerdict(false, scope, reached, ExceededCode, $"No se lanza el intento: {text}{partial}. Sube el presupuesto o reanuda cuando quieras.", unknown);
    }
}
