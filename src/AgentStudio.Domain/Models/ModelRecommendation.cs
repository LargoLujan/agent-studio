using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

// Phase 13 (docs/17 D-24): the inputs and the result of the deterministic recommendation of a model. The rules themselves
// (ModelRecommender) are WP2's; these types are what the configurator, the inspector and the analyzer share.

/// <summary>
/// How a model did in this project, from the verified log (docs/17 D-22 and D-24): its tasks, how many were approved at the
/// first attempt, its retries and the rejections of the judge. Estimated by Agent Studio; with fewer than
/// <see cref="MinimumReferences"/> tasks it is «desconocido» (<see cref="IsKnown"/>).
/// </summary>
public sealed record ModelPerformance(ProviderId Provider, string Model, int Tasks, int FirstPassApprovals, int Retries, int Rejections)
{
    /// <summary>The fewest tasks that make a performance known: with fewer, nothing is said of it (docs/16 D-20).</summary>
    public const int MinimumReferences = 3;

    /// <summary>Whether there are enough tasks to say anything about the model.</summary>
    public bool IsKnown => Tasks >= MinimumReferences;
}

/// <summary>
/// What the recommendation of one agent is computed from (docs/17 D-24): its role, the size of the project (files of the index
/// of the explorer), the requirements, the stack, the budget and the history of the project. Nothing here is free text of an
/// agent.
/// </summary>
/// <param name="Role">The role of the agent the recommendation is for.</param>
/// <param name="Provider">The CLI the agent runs on.</param>
/// <param name="ProducerVendors">For a judge, the vendors of the orchestrator and the executors: the judge is recommended from another one (ADR-0014).</param>
/// <param name="ProjectFiles">The files of the project, or null when unknown.</param>
/// <param name="Requirements">How many requirements there are.</param>
/// <param name="MandatoryRequirements">How many of them are mandatory.</param>
/// <param name="Stack">The stack detected (<c>ProjectStackDetector</c>), or null.</param>
/// <param name="MaxAgentRuns">The budget of a run (<c>RunOptions.MaxAgentRuns</c>), or null.</param>
/// <param name="PlansHaveDocsOrTestsOnlyTasks">The plans of the project tend to have tasks that only touch documentation or tests: a second, lighter executor pays off.</param>
/// <param name="History">How each model did in this project.</param>
public sealed record ModelRecommendationInput(
    AgentRole Role,
    ProviderId Provider,
    IReadOnlyList<VendorId> ProducerVendors,
    int? ProjectFiles,
    int Requirements,
    int MandatoryRequirements,
    string? Stack,
    int? MaxAgentRuns,
    bool PlansHaveDocsOrTestsOnlyTasks,
    IReadOnlyList<ModelPerformance> History);

/// <summary>Why a model is recommended (docs/17 D-24), as a stable code the desktop says in one sentence of its resources.</summary>
public static class ModelRecommendationReasons
{
    /// <summary>The orchestrator, «máximo»: many mandatory requirements. Argument: how many.</summary>
    public const string ManyMandatoryRequirements = "model.reason.many-mandatory-requirements";

    /// <summary>The orchestrator, «máximo»: a large project. Argument: its files.</summary>
    public const string LargeProject = "model.reason.large-project";

    /// <summary>The default of the role (the orchestrator «equilibrado», an executor «equilibrado»).</summary>
    public const string RoleDefault = "model.reason.role-default";

    /// <summary>A second executor, «ligero»: the plans of the project have tasks of documentation or tests.</summary>
    public const string LightSecondExecutor = "model.reason.light-second-executor";

    /// <summary>The judge: the highest level of the vendor that did not produce the work.</summary>
    public const string JudgeHighestOtherVendor = "model.reason.judge-highest-other-vendor";

    /// <summary>The history decides. Arguments: the model, its tasks and those approved at the first attempt.</summary>
    public const string History = "model.reason.history";
}

/// <summary>
/// A recommendation (docs/17 D-24): the model, its level and effort, why in one coded sentence and, when the estimator has
/// references, what it is expected to spend against the other options (that is the estimator's, in Application).
/// </summary>
/// <param name="ReasonCode">One of <see cref="ModelRecommendationReasons"/>.</param>
/// <param name="ReasonArguments">The arguments of the reason (<see cref="TextArguments"/>).</param>
public sealed record ModelRecommendation(
    ProviderId Provider,
    string Model,
    ModelTier Tier,
    ReasoningEffort Effort,
    string ReasonCode,
    IReadOnlyList<object> ReasonArguments)
{
    /// <summary>Builds a recommendation with its arguments checked.</summary>
    /// <exception cref="ArgumentException">The model or the reason is blank, or an argument is not one a coded text carries.</exception>
    public static ModelRecommendation Of(ProviderId provider, string model, ModelTier tier, ReasoningEffort effort, string reasonCode, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new ModelRecommendation(provider, model, tier, effort, reasonCode, TextArguments.Checked(arguments, nameof(arguments)));
    }
}
