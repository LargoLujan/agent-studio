using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;

namespace AgentStudio.Application.Analysis;

// Phase 13 contracts of the analysis of consumption (docs/17 D-34 to D-36). The deterministic analysis is always there and is
// free: it runs when each run ends, like the auditor, and when the person asks, over the verified log. The analysis with an
// agent is optional: an auxiliary read-only run (purpose ConsumptionAnalysis) of the vendor that did not produce the work,
// with the person's OK each time. It is never part of a verdict (D-36). WP3 implements both (Observability/Analysis).

/// <summary>The codes of the deterministic analysis (docs/17 D-34), which the desktop says with its resources, each with what it suggests.</summary>
public static class ConsumptionFindingCodes
{
    /// <summary>The pack of context left entries out: thin context (<c>AgentRunStarted</c>, its <c>ContextPack</c>).</summary>
    public const string PackOmitted = "ctx.pack-omitted";

    /// <summary>A peak over the threshold of compression (<c>AgentRunFinished</c>, its peak).</summary>
    public const string HighPeak = "ctx.high-peak";

    /// <summary>The CLI compacted the context: something may have been lost.</summary>
    public const string Compacted = "ctx.compacted";

    /// <summary>Large parts before starting (skills or memory) in tasks that ended soon (the parts of D-13).</summary>
    public const string HeavyPreamble = "ctx.heavy-preamble";

    /// <summary>Retries that end with the same class of end (F8-1).</summary>
    public const string RetrySameClass = "retry.same-class";

    /// <summary>Retries with the same tests failing (the TRX of the verification).</summary>
    public const string RetrySameFailingTests = "retry.same-failing-tests";

    /// <summary>The plan had to be corrected (<c>PlanRevised</c>).</summary>
    public const string PlanCorrected = "plan.corrected";

    /// <summary>The verdict had to be corrected.</summary>
    public const string VerdictCorrected = "verdict.corrected";

    /// <summary>Level «máximo» for a small change (3 files and 50 lines at most).</summary>
    public const string ModelOversized = "model.oversized";

    /// <summary>Little cache read between the retries of one session.</summary>
    public const string CacheLowReuse = "cache.low-reuse";

    /// <summary>Attempts of tasks that failed or were cancelled, or of a run that was not delivered (docs/16 D-34). It informs.</summary>
    public const string WorkDiscarded = "work.discarded";

    /// <summary>Long waits in the queue of the provider (from <c>AgentRunStarted</c> to <c>LaunchedAt</c>).</summary>
    public const string WaitQueue = "wait.queue";

    /// <summary>Every code, in the order of D-34.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PackOmitted, HighPeak, Compacted, HeavyPreamble, RetrySameClass, RetrySameFailingTests, PlanCorrected, VerdictCorrected,
        ModelOversized, CacheLowReuse, WorkDiscarded, WaitQueue,
    ];
}

/// <summary>What a finding or a suggestion is about (docs/17 D-34 and D-35).</summary>
public enum ConsumptionTargetKind
{
    Run,
    Agent,
    Task,
    Attempt,

    /// <summary>A setting of the run or of the app (the context budget, the tasks at once).</summary>
    Setting,
}

/// <summary>What a finding is about: the run, an agent, a task or an attempt by its id (<c>T3#2</c> for an attempt), or a setting by its name.</summary>
public sealed record ConsumptionTarget(ConsumptionTargetKind Kind, string? Id = null);

/// <summary>
/// One finding of the deterministic analysis (docs/17 D-34): its code, what it is about, what it cost with its origin
/// («~42.000 tokens de entrada en 2 reintentos · exacto · informado por el CLI») and the arguments of its sentence. Never a
/// saving made up: only what was spent.
/// </summary>
/// <param name="Code">One of <see cref="ConsumptionFindingCodes"/>.</param>
/// <param name="Target">What it is about.</param>
/// <param name="Tokens">The tokens it cost, with their origin; a figure without a value when the CLI does not expose them.</param>
/// <param name="Attempts">The attempts it is about.</param>
/// <param name="Arguments">The arguments of its sentence (<see cref="TextArguments"/>).</param>
public sealed record ConsumptionFinding(string Code, ConsumptionTarget Target, MetricValue<long> Tokens, int Attempts, IReadOnlyList<object> Arguments)
{
    /// <summary>Builds a finding with its arguments checked.</summary>
    /// <exception cref="ArgumentException">A blank code, negative attempts, or an argument a coded text does not carry.</exception>
    public static ConsumptionFinding Of(string code, ConsumptionTarget target, MetricValue<long> tokens, int attempts, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        return new ConsumptionFinding(code, target, tokens, attempts, TextArguments.Checked(arguments, nameof(arguments)));
    }
}

/// <summary>
/// The deterministic analysis of the consumption of one run (docs/17 D-34): the head of the verified log it read, when, and
/// its findings in a stable order.
/// </summary>
public sealed record ConsumptionReport(RunId Run, string LogHeadHash, DateTimeOffset At, IReadOnlyList<ConsumptionFinding> Findings);

/// <summary>
/// The deterministic analysis of consumption (docs/17 D-34): free and instant, over the verified log only. A log that does not
/// verify fails closed (<c>DecisionLogIntegrityException</c>), as the memory does.
/// </summary>
public interface IConsumptionAnalyzer
{
    Task<ConsumptionReport> AnalyzeAsync(ProjectContext project, RunId run, CancellationToken ct);
}

/// <summary>The limits of the answer of the agent that analyses consumption (docs/17 D-35), which its strict schema cannot say.</summary>
public static class AssistedConsumptionLimits
{
    /// <summary>The longest text of a suggestion.</summary>
    public const int MaxSuggestionText = 300;

    /// <summary>The most suggestions one analysis gives.</summary>
    public const int MaxSuggestions = 10;
}

/// <summary>
/// One suggestion of the agent that analysed consumption (docs/17 D-35): what it is about, its text (data of the agent, at most
/// <see cref="AssistedConsumptionLimits.MaxSuggestionText"/> characters, shown framed as written by an agent), a typed fix or
/// none, and the saving the agent estimates. The saving is «lo dice el agente», never a measurement of the system. Applying a
/// fix goes through the usual gates.
/// </summary>
public sealed record AssistedConsumptionSuggestion(ConsumptionTarget Target, string Text, Fix? Fix, long? SavingClaimedByAgent);

/// <summary>
/// The analysis of consumption by an agent (docs/17 D-35): its suggestions, the evidence of its answer as it came, and the
/// agent run that produced it (its usage goes to «Consumo» under the purpose <c>ConsumptionAnalysis</c>).
/// </summary>
public sealed record AssistedConsumptionAnalysis(RunId Run, EvidenceId AnswerEvidence, IReadOnlyList<AssistedConsumptionSuggestion> Suggestions);

/// <summary>
/// What the agent receives (docs/17 D-35): the deterministic report, the table of usage per attempt and per part, and the size
/// of the prompts, but never their text; and the person's OK to spend quota on it, with the estimate in front.
/// </summary>
public sealed record AssistedConsumptionRequest(ProjectContext Project, RunId Run, ConsumptionReport Deterministic, QuotaConsent Consent);

/// <summary>
/// The analysis of consumption with an agent (docs/17 D-35): an auxiliary read-only run (<c>AuxiliaryAgentRunner</c>) of the
/// vendor that did not produce the work (the judge's), without memory and in an empty folder. Without the consent nothing is
/// launched.
/// </summary>
public interface IAssistedConsumptionAnalysis
{
    Task<AssistedConsumptionAnalysis> AnalyzeAsync(AssistedConsumptionRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);
}
