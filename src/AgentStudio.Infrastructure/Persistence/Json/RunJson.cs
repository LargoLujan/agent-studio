using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>The verification profile frozen in <c>orchestration_runs.verification_profile_json</c>.</summary>
internal sealed record VerificationProfileJson(string Name, IReadOnlyList<VerificationStepJson> Steps)
{
    public static VerificationProfileJson From(VerificationProfile profile) =>
        new(profile.Name, [.. profile.Steps.Select(VerificationStepJson.From)]);

    public VerificationProfile ToDomain() => new(Name, [.. (Steps ?? []).Select(s => s.ToDomain())]);
}

/// <param name="Isolation">
/// Only a step that turns isolation off carries it (ADR-0017): a profile that isolates every step is stored exactly as it
/// was before the field existed, and a stored step without it is isolated, the safe default. Keeping it is what lets a
/// step <c>none</c> resume as <c>none</c>, so the resumed profile hashes to what <c>RunStarted</c> recorded.
/// </param>
/// <param name="Requires">
/// The same for the steps a step requires (profile file version 2): only a step that requires some carries them, so a
/// profile without requirements is stored exactly as before they existed, and a stored step without them requires
/// nothing. Keeping them is what lets a resumed run build before it tests, as it did, with the hash <c>RunStarted</c>
/// recorded.
/// </param>
internal sealed record VerificationStepJson(
    string Id,
    string Title,
    string Executable,
    IReadOnlyList<string> Arguments,
    EvidenceKind Kind,
    TestReportFormat Report,
    string? ReportPath,
    TimeSpan Timeout,
    ProcessIsolation? Isolation = null,
    IReadOnlyList<string>? Requires = null)
{
    public static VerificationStepJson From(VerificationStep step) =>
        new(step.Id, step.Title, step.Executable, step.Arguments, step.Kind, step.Report, step.ReportPath, step.Timeout,
            step.Isolation == ProcessIsolation.LowIntegrity ? null : step.Isolation,
            step.Requires is { Count: > 0 } requires ? [.. requires] : null);

    public VerificationStep ToDomain() =>
        new(Id, Title, Executable, Arguments ?? [], Kind, Report, ReportPath, Timeout, Isolation ?? ProcessIsolation.LowIntegrity,
            Requires is { Count: > 0 } requires ? [.. requires] : null);
}

/// <summary>An agent definition, as kept in <c>agents.definition_json</c> and in the roster frozen with each run.</summary>
internal sealed record AgentJson(
    string Id,
    string Name,
    AgentRole Role,
    ModelJson Model,
    string SystemInstructions,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<PathGrantJson> PathGrants,
    BudgetJson Budget,
    int MaxRounds,
    MemoryPolicy Memory,
    ReviewJson Review)
{
    public static AgentJson From(AgentDefinition agent) => new(
        agent.Id.Value,
        agent.Name,
        agent.Role,
        ModelJson.From(agent.Model),
        agent.SystemInstructions,
        agent.Objectives,
        agent.AllowedTools,
        [.. agent.PathGrants.Select(g => new PathGrantJson(g.Root, g.Access))],
        new BudgetJson(agent.Budget.MaxTurns, agent.Budget.MaxDuration, agent.Budget.MaxTotalTokens, agent.Budget.MaxCostUsd),
        agent.MaxRounds,
        agent.Memory,
        new ReviewJson(agent.Review.RequiresIndependentReview, agent.Review.RequiresSystemCapturedTests));

    public AgentDefinition ToDomain() => new(
        new AgentId(Id),
        Name,
        Role,
        (Model ?? throw new InvalidOperationException($"Agent '{Id}' has no model.")).ToRequiredDomain(),
        SystemInstructions,
        Objectives ?? [],
        AllowedTools ?? [],
        [.. (PathGrants ?? []).Select(g => new PathGrant(g.Root, g.Access))],
        new ExecutionBudget(Budget.MaxTurns, Budget.MaxDuration, Budget.MaxTotalTokens, Budget.MaxCostUsd),
        MaxRounds,
        Memory,
        new ReviewPolicy(Review.RequiresIndependentReview, Review.RequiresSystemCapturedTests));
}

/// <summary>
/// A model binding. Provider and vendor are nullable only because a judge's verdict is stored as the judge gave it;
/// an agent of the roster always has both.
/// </summary>
internal sealed record ModelJson(string? Provider, string? Vendor, string? Model)
{
    public static ModelJson From(ModelBinding binding) => new(binding.Provider.Value, binding.Vendor.Value, binding.Model);

    public ModelBinding ToDomain() => new(
        Provider is null ? default : new ProviderId(Provider),
        Vendor is null ? default : new VendorId(Vendor),
        Model);

    public ModelBinding ToRequiredDomain() => new(
        new ProviderId(Provider ?? throw new InvalidOperationException("The model has no provider.")),
        new VendorId(Vendor ?? throw new InvalidOperationException("The model has no vendor.")),
        Model);
}

internal sealed record PathGrantJson(string Root, FileAccessLevel Access);

internal sealed record BudgetJson(int MaxTurns, TimeSpan MaxDuration, long? MaxTotalTokens, decimal? MaxCostUsd);

internal sealed record ReviewJson(bool RequiresIndependentReview, bool RequiresSystemCapturedTests);

/// <summary>The limits and timings of a run, in <c>orchestration_runs.options_json</c>.</summary>
/// <remarks>
/// The options of Phase 8 (docs/16 D-36, D-37 and D-39; ADR-0034) are stored only when they are not the defaults: a margin or
/// a cap only when given, the consent only when it was given (true). So options at their defaults are stored exactly as they
/// were before the fields existed, and a stored row without them reads with the defaults and hashes as it did.
/// </remarks>
internal sealed record RunOptionsJson(
    int MaxParallelTasks,
    int MaxAgentRuns,
    TimeSpan? StallThreshold,
    TimeSpan? HeartbeatInterval,
    TimeSpan? DefaultTaskTimeout,
    TimeSpan? MaxAutoWaitForQuota,
    TimeSpan? QuotaResetMargin = null,
    TimeSpan? MaxEstimatedQuotaWait = null,
    bool? AutoResumeConsent = null)
{
    public static RunOptionsJson From(RunOptions options) => new(
        options.MaxParallelTasks,
        options.MaxAgentRuns,
        options.StallThreshold,
        options.HeartbeatInterval,
        options.DefaultTaskTimeout,
        options.MaxAutoWaitForQuota,
        options.QuotaResetMargin,
        options.MaxEstimatedQuotaWait,
        options.AutoResumeConsent ? true : null);

    public RunOptions ToDomain() => new(
        MaxParallelTasks,
        MaxAgentRuns,
        StallThreshold,
        HeartbeatInterval,
        DefaultTaskTimeout,
        MaxAutoWaitForQuota,
        QuotaResetMargin,
        MaxEstimatedQuotaWait,
        AutoResumeConsent == true);
}
