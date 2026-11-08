using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// What the system estimates going on with each option of a question would cost (docs/17 D-32): the estimator
/// (<see cref="EnginePorts.Estimator"/>) over the similar attempts of the project, by the effort the agent declares. Without an
/// estimator nothing is estimated: the question carries no estimates, never a number made up.
/// </summary>
internal static class QuestionEstimator
{
    public static Task<IReadOnlyList<OptionEstimateSnapshot>> EstimateAsync(WorkerContext w, AgentAttemptPlan plan, AgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(question);
        return Task.FromResult<IReadOnlyList<OptionEstimateSnapshot>>([]);
    }
}
