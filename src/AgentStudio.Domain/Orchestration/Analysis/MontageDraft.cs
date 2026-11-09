using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;

namespace AgentStudio.Domain.Orchestration.Analysis;

/// <summary>
/// What the analyzer knows of the context of one task before it is launched (docs/17 D-13 and D-18), as the application
/// computes it with the packer, in tokens estimated by Agent Studio (characters divided by 4, docs/14 D-24).
/// </summary>
/// <param name="Task">The task.</param>
/// <param name="PreambleTokens">What goes in the prompt before the agent starts (protocol, requirements, brief, memory, upstream outputs, skills), estimated.</param>
/// <param name="Window">The window of the model of its agent, with its origin (the table of the tanda 2 or what the log taught).</param>
/// <param name="MandatoryCharacters">The characters of the mandatory requirements of the task.</param>
/// <param name="BudgetCharacters">The context budget of its agent, in characters.</param>
/// <param name="HasMemory">The agent gets project memory.</param>
/// <param name="HasBrief">The brief of the project exists and reaches the agent.</param>
/// <param name="HasUpstream">The task receives the output of earlier tasks.</param>
public sealed record TaskContextFacts(
    TaskId Task,
    long PreambleTokens,
    MetricValue<long> Window,
    int MandatoryCharacters,
    int BudgetCharacters,
    bool HasMemory,
    bool HasBrief,
    bool HasUpstream);

/// <summary>A window of quota of a provider as last seen (docs/14 D-14): its use from 0 to 1, when it resets, and its origin.</summary>
public sealed record QuotaWindowFacts(ProviderId Provider, string Window, MetricValue<double> Used, DateTimeOffset? ResetsAt);

/// <summary>The options of the run the draft would start with (docs/17 D-09): how many tasks at once, its budget and the concurrency of each provider.</summary>
/// <param name="MaxParallelTasks">Tasks at once (<c>RunOptions.MaxParallelTasks</c>, 3 by default).</param>
/// <param name="MaxAgentRuns">The budget of agent runs (<c>RunOptions.MaxAgentRuns</c>).</param>
/// <param name="ProviderConcurrency">How many runs of each provider may go at once (2 by default); a provider not listed has the default.</param>
public sealed record MontageRunFacts(int MaxParallelTasks, int MaxAgentRuns, IReadOnlyDictionary<ProviderId, int> ProviderConcurrency)
{
    /// <summary>The concurrency of a provider when none is given.</summary>
    public const int DefaultProviderConcurrency = 2;

    /// <summary>How many runs of <paramref name="provider"/> may go at once.</summary>
    public int ConcurrencyOf(ProviderId provider) => ProviderConcurrency.TryGetValue(provider, out var limit) ? limit : DefaultProviderConcurrency;
}

/// <summary>
/// What the analyzer learned of this project from the verified log (docs/17 D-22 to D-24): how each model did, and the agent
/// runs the estimator expects the plan to take (null when it does not know: fewer than three references).
/// </summary>
public sealed record MontageLearnedFacts(IReadOnlyList<ModelPerformance> Models, long? EstimatedAgentRuns)
{
    /// <summary>Nothing learned yet.</summary>
    public static MontageLearnedFacts None { get; } = new([], null);
}

/// <summary>
/// The draft the analyzer of the assembly reads (docs/17 D-18): an immutable copy of the team, the plan (null before there is
/// one), what the plan is checked against (<see cref="PlanContext"/>: requirements, roster, profile, tools, limits), the
/// options of the run, the context of each task, the windows of quota, the notes of order of the orchestrator, the catalog of
/// models and what was learned. The application builds it off the UI thread; <see cref="Generation"/> is the edit it was
/// copied at, so a result is applied only if nothing changed since (D-16).
/// </summary>
public sealed record MontageDraft(
    long Generation,
    TeamGraph Team,
    PlanDefinition? Plan,
    PlanContext Context,
    MontageRunFacts Run,
    IReadOnlyList<TaskContextFacts> TaskContexts,
    IReadOnlyList<QuotaWindowFacts> Quota,
    OrderNotes OrderNotes,
    ModelCatalog? Catalog,
    MontageLearnedFacts Learned,
    ContextHealthThresholds Thresholds)
{
    /// <summary>
    /// WP1 (docs/17 D-18, B6): the steps of the trusted verification profile, in its order, so the analyzer can tell a test
    /// step that gets a build before it (the task names one first, or the step requires one) from one that does not. Empty
    /// when the caller does not know the profile: then <see cref="FindingCodes.TestWithoutBuild"/> says nothing.
    /// </summary>
    public IReadOnlyList<VerificationStepFacts> Steps { get; init; } = [];
}

/// <summary>
/// One step of the trusted verification profile as the analyzer reads it (WP1, docs/17 D-18): its id, whether it builds
/// (its evidence is a build log), whether it runs tests, and the steps it requires, which the system runs before it.
/// </summary>
public sealed record VerificationStepFacts(string Id, bool Builds, bool RunsTests, IReadOnlyList<string> Requires);

/// <summary>
/// One rule of the analyzer (docs/17 D-18, WP1): deterministic, pure and free. It reads the draft and returns its findings; it
/// never spends quota, never reads a file and never changes the draft.
/// </summary>
public interface IMontageRule
{
    /// <summary>The codes this rule may return (<see cref="FindingCodes"/>), for its tests and the list of the band.</summary>
    IReadOnlyList<string> Codes { get; }

    /// <summary>The findings of this rule on <paramref name="draft"/>, in a stable order.</summary>
    IEnumerable<Finding> Check(MontageDraft draft);
}
