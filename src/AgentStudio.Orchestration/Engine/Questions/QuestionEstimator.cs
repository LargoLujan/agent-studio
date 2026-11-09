using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// What the system estimates going on with each option of a question would cost (docs/17 D-32): the estimator
/// (<see cref="EnginePorts.Estimator"/>) over the similar attempts of the project (the same agent, its model, an executor's
/// attempt), by the size of the work the agent declares for the option. Each estimate is recorded as <c>QuestionAsked</c> keeps
/// it (<see cref="OptionEstimateSnapshot"/>): the interval of total tokens, from the 25th to the 75th percentile, each end with
/// its origin, and the references it rests on. Never a number made up:
/// <list type="bullet">
/// <item>without an estimator, or when it cannot answer (a log that does not verify, say), nothing is estimated and the
/// question carries no estimates;</item>
/// <item>with fewer than three references both ends are missing, with the reason of the estimator;</item>
/// <item>the total is never the sum of the percentiles of the input and of the output, which is not the percentile of the
/// total: the ends are those of <see cref="UsageEstimate.TotalTokens"/>, the interval the estimator computes over the totals of
/// its references (<see cref="UsageEstimate.ToSnapshot"/>), and without it both ends are missing with the references and the
/// reason (<see cref="UsageEstimate.NoTotalReason"/>).</item>
/// </list>
/// </summary>
internal static class QuestionEstimator
{
    public static async Task<IReadOnlyList<OptionEstimateSnapshot>> EstimateAsync(WorkerContext w, AgentAttemptPlan plan, AgentQuestion question)
    {
        if (w.Ports.Estimator is not { } estimator)
        {
            return [];
        }

        var snapshots = new List<OptionEstimateSnapshot>();
        try
        {
            foreach (var option in question.Options)
            {
                var request = new UsageEstimateRequest(w.Run.Project, plan.Agent.Id, plan.Provider.Provider, plan.Agent.Model.Model, AgentRunPurpose.Executor, SizeOf(option.Effort));
                var estimate = await estimator.EstimateAsync(request, w.Abort).ConfigureAwait(false);
                snapshots.Add(estimate.ToSnapshot(option.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // the estimator could not answer: nothing is estimated, which the window says as it is
        }

        return snapshots;
    }

    /// <summary>The size of the work an effort the agent declares stands for (docs/17 D-32).</summary>
    public static WorkSize SizeOf(QuestionEffort effort) => effort switch
    {
        QuestionEffort.Small => WorkSize.Small,
        QuestionEffort.Medium => WorkSize.Medium,
        _ => WorkSize.Large,
    };
}
