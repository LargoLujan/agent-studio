using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Models;

// Phase 13 (docs/17 D-22 to D-24, ADR-0050), WP2: the catalog of models as the app ships it, what the verified log teaches of
// each model, and the comparison of the options of a model with the estimator. The recommendation itself is pure and lives in
// the domain (ModelRecommender); these are what the configurator (WP7), the inspector (WP6) and the analyzer (WP1, through the
// draft) read. Nothing here launches an agent or spends quota.

/// <summary>A source the catalog names for its data: what it is, where it is (a command or an address) and when it was checked.</summary>
/// <param name="Id">How the entries of the catalog refer to it, such as <c>claude-help</c>.</param>
public sealed record ModelCatalogSource(string Id, string Description, string Location, DateOnly CheckedOn);

/// <summary>
/// The local catalog of models as the app ships it (docs/17 D-23): the catalog, the day it was checked, its sources and the
/// sources of each entry (<see cref="SourcesOf"/>). When it could not be read, <see cref="Problems"/> says why with stable codes
/// and the catalog is empty: nothing is classified («sin clasificar») and nothing breaks.
/// </summary>
/// <param name="EntrySources">The ids of the sources of each entry, in the order of <see cref="ModelCatalog.Entries"/>.</param>
public sealed record ModelCatalogDocument(
    ModelCatalog Catalog,
    DateOnly CheckedOn,
    IReadOnlyList<ModelCatalogSource> Sources,
    IReadOnlyList<IReadOnlyList<string>> EntrySources,
    IReadOnlyList<string> Problems)
{
    /// <summary>Whether the catalog was read without problems.</summary>
    public bool IsValid => Problems.Count == 0;

    /// <summary>An empty catalog, with the problems that made it so.</summary>
    public static ModelCatalogDocument Unreadable(IReadOnlyList<string> problems) =>
        new(new ModelCatalog(1, [], ModelRecommendationRules.Default), DateOnly.MinValue, [], [], problems);

    /// <summary>The sources of <paramref name="entry"/> (an entry of <see cref="Catalog"/>); none for an entry it does not hold.</summary>
    public IReadOnlyList<ModelCatalogSource> SourcesOf(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var index = -1;
        for (var i = 0; i < Catalog.Entries.Count; i++)
        {
            if (ReferenceEquals(Catalog.Entries[i], entry))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index >= EntrySources.Count)
        {
            return [];
        }

        var ids = EntrySources[index];
        return [.. Sources.Where(s => ids.Contains(s.Id, StringComparer.Ordinal))];
    }
}

/// <summary>The catalog of models of this version of the app (docs/17 D-23). Read once; it never goes to the network.</summary>
public interface IModelCatalogSource
{
    /// <summary>The catalog, or an empty one with its problems when it could not be read.</summary>
    ModelCatalogDocument Current { get; }
}

/// <summary>
/// What the verified log of a project teaches of one model (docs/17 D-22 and D-23), every figure with its origin and its level:
/// how it did in the tasks of the project (estimated by Agent Studio; «desconocido» with fewer than three), the last context
/// window its CLI reported and what a task cost at the CLI's list price («no expuesto» in Codex, never zero).
/// </summary>
/// <param name="Model">The name of the catalog entry when it classifies the model (an alias and its full name are one model), else the id the CLI reported, without its date.</param>
/// <param name="ReportedAs">The ids the CLI reported for it (or the configured name, when the CLI reported none), in order.</param>
/// <param name="Performance">Its tasks as executor: first-pass approvals, retries and rejections of the judge.</param>
/// <param name="ContextWindow">The last window the CLI reported for it, as an estimate of Agent Studio for what comes next; missing when none was.</param>
/// <param name="CostPerTask">The median cost of its tasks, with its origin; missing with fewer than three, not exposed in Codex.</param>
/// <param name="Attempts">Its attempts in the log, of any purpose.</param>
public sealed record LearnedModel(
    ProviderId Provider,
    string Model,
    IReadOnlyList<string> ReportedAs,
    ModelPerformance Performance,
    MetricValue<long> ContextWindow,
    MetricValue<decimal> CostPerTask,
    int Attempts)
{
    /// <summary>
    /// The reasoning efforts its attempts were set with (docs/18 P47, ADR-0050), each once, from the lightest to the heaviest;
    /// empty when none of them recorded one. The performance above is the model's, never split by effort (a split would leave it
    /// without its three references); this says which efforts it rests on, so an analysis can say «con esfuerzo alto» when it knows.
    /// </summary>
    public IReadOnlyList<ReasoningEffort> Efforts { get; init; } = [];
}

/// <summary>
/// Every model the verified log of a project has seen (docs/17 D-23), in a stable order (provider, then model). A log that does
/// not verify, or that no anchor recognises, teaches nothing (<see cref="Empty"/>).
/// </summary>
public sealed record LearnedModels(IReadOnlyList<LearnedModel> Models)
{
    /// <summary>
    /// The catalog the models were learned against, so <see cref="Find"/> resolves a name by the same rule
    /// (<see cref="ModelKeys.Of"/>); null when there was none (then a name is its id without the date).
    /// </summary>
    public ModelCatalog? Catalog { get; init; }

    /// <summary>Nothing learned.</summary>
    public static LearnedModels Empty { get; } = new([]);

    /// <summary>The performances, as the recommendation and the analyzer read them (<see cref="ModelRecommendationInput.History"/>).</summary>
    public IReadOnlyList<ModelPerformance> Performances => [.. Models.Select(m => m.Performance)];

    /// <summary>
    /// The model learned under <paramref name="model"/>, by the one rule of which names are the same model
    /// (<see cref="ModelKeys.Of"/> with <see cref="Catalog"/>): an alias finds what was learned under its full name and the other
    /// way round, with or without the date and in any case; null when none.
    /// </summary>
    public LearnedModel? Find(ProviderId provider, string? model) =>
        ModelKeys.Of(Catalog, provider, model) is { } key
            ? Models.FirstOrDefault(m => m.Provider == provider && string.Equals(m.Model, key, StringComparison.Ordinal))
            : null;
}

/// <summary>What the verified log of a project teaches of its models (docs/17 D-23). It reads only; it launches nothing.</summary>
public interface IModelLearning
{
    /// <summary>The models of the log of the project at <paramref name="projectRoot"/>, against the catalog of the app; none when the log is not trusted.</summary>
    Task<LearnedModels> LearnAsync(string projectRoot, CancellationToken ct);
}

/// <summary>What one option of a model is expected to spend (docs/17 D-24), from the estimator: «desconocida» with fewer than three references.</summary>
/// <param name="Model">The model of the option; null for the default of the CLI.</param>
public sealed record ModelOptionEstimate(string? Model, UsageEstimate Estimate);

/// <summary>
/// The expected consumption of each option of a model (docs/17 D-24): «si el estimador tiene referencias, la diferencia de
/// consumo esperada con las otras opciones». It asks the estimator of the system (<see cref="IUsageEstimator"/>, docs/16 D-20
/// brought forward), which reads only the verified log: no agent, no quota.
/// </summary>
public static class ModelOptionEstimates
{
    /// <summary>
    /// The estimate of each of <paramref name="models"/> (in their order, once each, without case) for the attempt
    /// <paramref name="request"/> describes, with only its model changed.
    /// </summary>
    public static async Task<IReadOnlyList<ModelOptionEstimate>> CompareAsync(
        IUsageEstimator estimator,
        UsageEstimateRequest request,
        IReadOnlyList<string?> models,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(models);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimates = new List<ModelOptionEstimate>();
        foreach (var model in models)
        {
            var name = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            if (!seen.Add(name ?? string.Empty))
            {
                continue;
            }

            var estimate = await estimator.EstimateAsync(request with { Model = name }, ct).ConfigureAwait(false);
            estimates.Add(new ModelOptionEstimate(name, estimate));
        }

        return estimates;
    }
}
