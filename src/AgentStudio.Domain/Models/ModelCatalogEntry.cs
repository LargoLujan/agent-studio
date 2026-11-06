using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// One model of the local catalog (docs/17 D-23): for one CLI and the version of it the catalog was checked against, the names
/// the model goes by (its full name and its aliases, as <c>--model</c> or <c>-m</c> take them), its level, the efforts it
/// accepts and a note. There are no prices (docs/14 D-06): the cost comes from the log, with its origin. The catalog is a JSON
/// versioned inside the app (<c>models.catalog.json</c>, read by WP2 of Phase 13).
/// </summary>
/// <param name="Provider">The CLI the names are for: the same model may be reached by other names from another CLI.</param>
/// <param name="TestedCliVersion">The version of the CLI the entry was checked against, such as <c>2.1.290</c>.</param>
/// <param name="Names">The full name first, then the aliases; compared without case.</param>
/// <param name="Tier">The level, in words.</param>
/// <param name="Efforts">The efforts the model accepts from that CLI; empty when the CLI takes none for it.</param>
/// <param name="Note">What the person should know (a preview, a model only for some plans); null when nothing.</param>
public sealed record ModelCatalogEntry(
    ProviderId Provider,
    string TestedCliVersion,
    IReadOnlyList<string> Names,
    ModelTier Tier,
    IReadOnlyList<ReasoningEffort> Efforts,
    string? Note = null)
{
    /// <summary>The name the entry is known by: the first of <see cref="Names"/>.</summary>
    public string Name => Names.Count > 0 ? Names[0] : string.Empty;

    /// <summary>Whether <paramref name="model"/> is one of the names of this entry (without case).</summary>
    public bool IsNamed(string? model) =>
        !string.IsNullOrWhiteSpace(model) && Names.Any(n => string.Equals(n, model.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the model accepts <paramref name="effort"/> from its CLI.</summary>
    public bool Accepts(ReasoningEffort effort) => Efforts.Contains(effort);

    /// <summary>Returns every rule this entry breaks, as stable codes; an empty list means it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(TestedCliVersion))
        {
            problems.Add("model-catalog.cli-version-required");
        }

        var names = Names ?? [];
        if (names.Count == 0)
        {
            problems.Add("model-catalog.name-required");
        }

        if (names.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("model-catalog.name-blank");
        }

        if (names.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            problems.Add("model-catalog.name-duplicate");
        }

        if (!Enum.IsDefined(Tier))
        {
            problems.Add("model-catalog.tier-unknown");
        }

        var efforts = Efforts ?? [];
        if (efforts.Any(e => !Enum.IsDefined(e)))
        {
            problems.Add("model-catalog.effort-unknown");
        }

        if (efforts.Distinct().Count() != efforts.Count)
        {
            problems.Add("model-catalog.effort-duplicate");
        }

        return problems;
    }
}

/// <summary>
/// The starting rules of the deterministic recommendation (docs/17 D-24), which live in the catalog so they can be adjusted
/// without a new version of the app. The defaults are the ones D-24 states.
/// </summary>
/// <param name="OrchestratorMaximumFromMandatoryRequirements">With at least this many mandatory requirements the orchestrator is recommended «máximo».</param>
/// <param name="OrchestratorMaximumFromFiles">With more files than this in the project the orchestrator is recommended «máximo».</param>
/// <param name="OrchestratorEffort">The effort recommended to the orchestrator.</param>
/// <param name="ExecutorEffort">The effort recommended to an executor.</param>
/// <param name="JudgeEffort">The effort recommended to the judge: one run per review, on which everything else rests.</param>
public sealed record ModelRecommendationRules(
    int OrchestratorMaximumFromMandatoryRequirements = 5,
    int OrchestratorMaximumFromFiles = 2000,
    ReasoningEffort OrchestratorEffort = ReasoningEffort.High,
    ReasoningEffort ExecutorEffort = ReasoningEffort.Medium,
    ReasoningEffort JudgeEffort = ReasoningEffort.High)
{
    /// <summary>The rules of D-24.</summary>
    public static ModelRecommendationRules Default { get; } = new();
}

/// <summary>
/// The local catalog of models (docs/17 D-23): its version, its entries and the rules of the recommendation. What the log
/// teaches (the windows the CLIs reported, how each model did in this project) is added to it by the reader, never written
/// into it.
/// </summary>
public sealed record ModelCatalog(int Version, IReadOnlyList<ModelCatalogEntry> Entries, ModelRecommendationRules Rules)
{
    /// <summary>The entry of <paramref name="model"/> for <paramref name="provider"/>, or null when the catalog does not classify it («sin clasificar»).</summary>
    public ModelCatalogEntry? Find(ProviderId provider, string? model) =>
        Entries.FirstOrDefault(e => e.Provider == provider && e.IsNamed(model));

    /// <summary>Returns every rule the catalog breaks, as stable codes, each entry's too; an empty list means it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Version < 1)
        {
            problems.Add("model-catalog.version-required");
        }

        foreach (var entry in Entries ?? [])
        {
            problems.AddRange(entry.Validate());
        }

        var clash = (Entries ?? [])
            .SelectMany(e => (e.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (e.Provider, Name: n.Trim().ToUpperInvariant())))
            .GroupBy(p => p)
            .Any(g => g.Count() > 1);
        if (clash)
        {
            problems.Add("model-catalog.name-in-two-entries");
        }

        return [.. problems.Distinct(StringComparer.Ordinal)];
    }
}
