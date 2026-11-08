using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// The deterministic recommendation of a model and an effort (docs/17 D-24, ADR-0050): pure, without an agent and without
/// quota, from the role, the project and its history. It always says why (<see cref="ModelRecommendationReasons"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Candidates.</b> The entries of the catalog for the provider of the agent that accept at least one effort. Within a
/// level, the order of the catalog is the preference: its first entry wins (the catalog puts <c>claude-opus-5-5</c> before
/// <c>claude-fable-5-1</c>, so «máximo» is Opus). A level without candidates falls back to the nearest one, the higher first.
/// No candidate at all: no recommendation (null).</item>
/// <item><b>Orchestrator.</b> «Máximo» with at least <see cref="ModelRecommendationRules.OrchestratorMaximumFromMandatoryRequirements"/>
/// mandatory requirements, or with more than <see cref="ModelRecommendationRules.OrchestratorMaximumFromFiles"/> files;
/// otherwise «equilibrado». Effort <see cref="ModelRecommendationRules.OrchestratorEffort"/>.</item>
/// <item><b>Judge and auditor.</b> The highest level of their provider, with <see cref="ModelRecommendationRules.JudgeEffort"/>.
/// That the provider is of a vendor that did not produce is the rule of the team (ADR-0014; <c>team.judge-same-vendor</c>), not
/// of this recommendation, which never moves an agent to another CLI.</item>
/// <item><b>The others</b> (executor, architect, tester, documenter, critic). «Equilibrado», with
/// <see cref="ModelRecommendationRules.ExecutorEffort"/>, unless the history decides.</item>
/// <item><b>The history decides</b> for those when there is data: among the known performances (<see cref="ModelPerformance.IsKnown"/>,
/// three tasks or more) of models the catalog classifies for the same provider, the best one (most first-pass approvals by
/// share of its tasks, then fewer rejections, fewer retries, more tasks and the order of the catalog) is recommended when
/// more than half of its tasks passed at the first attempt (<see cref="HistoryMinimumFirstPassShare"/>).</item>
/// <item><b>Effort.</b> The one of the rule if the model accepts it; otherwise the nearest one it accepts, the lower on a tie.</item>
/// <item><b>A second executor</b> «ligero» (<see cref="RecommendSecondExecutor"/>) only when the plans of the project tend to have
/// tasks that only touch documentation or tests.</item>
/// </list>
/// </remarks>
public static class ModelRecommender
{
    /// <summary>The share of first-pass approvals a model must pass (strictly) for its history to decide.</summary>
    public const double HistoryMinimumFirstPassShare = 0.5;

    /// <summary>The recommendation for the agent <paramref name="input"/> describes; null when the catalog has no candidate for its provider.</summary>
    public static ModelRecommendation? Recommend(ModelCatalog catalog, ModelRecommendationInput input)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(input);
        var rules = catalog.Rules ?? ModelRecommendationRules.Default;
        var candidates = Candidates(catalog, input.Provider);
        if (candidates.Count == 0)
        {
            return null;
        }

        switch (input.Role)
        {
            case AgentRole.Orchestrator:
                if (input.MandatoryRequirements >= rules.OrchestratorMaximumFromMandatoryRequirements)
                {
                    return Of(candidates, ModelTier.Maximum, rules.OrchestratorEffort, ModelRecommendationReasons.ManyMandatoryRequirements, input.MandatoryRequirements);
                }

                if (input.ProjectFiles is { } files && files > rules.OrchestratorMaximumFromFiles)
                {
                    return Of(candidates, ModelTier.Maximum, rules.OrchestratorEffort, ModelRecommendationReasons.LargeProject, files);
                }

                return Of(candidates, ModelTier.Balanced, rules.OrchestratorEffort, ModelRecommendationReasons.RoleDefault);

            case AgentRole.Judge or AgentRole.Auditor:
                return Of(candidates, candidates.Max(c => c.Tier), rules.JudgeEffort, ModelRecommendationReasons.JudgeHighestOtherVendor);

            default:
                return FromHistory(catalog, candidates, input, rules.ExecutorEffort)
                    ?? Of(candidates, ModelTier.Balanced, rules.ExecutorEffort, ModelRecommendationReasons.RoleDefault);
        }
    }

    /// <summary>
    /// The second executor, «ligero», when the plans of the project tend to have tasks that only touch documentation or tests
    /// (<see cref="ModelRecommendationInput.PlansHaveDocsOrTestsOnlyTasks"/>); null otherwise, or without a candidate.
    /// </summary>
    public static ModelRecommendation? RecommendSecondExecutor(ModelCatalog catalog, ModelRecommendationInput input)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.PlansHaveDocsOrTestsOnlyTasks)
        {
            return null;
        }

        var candidates = Candidates(catalog, input.Provider);
        return candidates.Count == 0
            ? null
            : Of(candidates, ModelTier.Light, (catalog.Rules ?? ModelRecommendationRules.Default).ExecutorEffort, ModelRecommendationReasons.LightSecondExecutor);
    }

    /// <summary>
    /// Whether <paramref name="agent"/> already follows <paramref name="recommendation"/>: the same CLI, a model the catalog
    /// takes for the same one (an alias and its full name are the same model) and the same effort. Applying it then changes
    /// nothing, so it must not change the template (nor its hash).
    /// </summary>
    public static bool IsFollowedBy(ModelCatalog catalog, ModelRecommendation recommendation, AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Model.Provider != recommendation.Provider || agent.Effort != recommendation.Effort || string.IsNullOrWhiteSpace(agent.Model.Model))
        {
            return false;
        }

        if (string.Equals(agent.Model.Model.Trim(), recommendation.Model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = catalog.Find(agent.Model.Provider, agent.Model.Model);
        return current is not null && ReferenceEquals(current, catalog.Find(recommendation.Provider, recommendation.Model));
    }

    /// <summary>The effort of <paramref name="entry"/> nearest to <paramref name="wanted"/> (the lower on a tie); null when it accepts none.</summary>
    public static ReasoningEffort? NearestEffort(ModelCatalogEntry entry, ReasoningEffort wanted)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var accepted = (entry.Efforts ?? []).Where(e => ReasoningEfforts.Accepts(entry.Provider, e)).ToList();
        if (accepted.Count == 0)
        {
            return null;
        }

        return accepted.Contains(wanted)
            ? wanted
            : accepted.OrderBy(e => Math.Abs((int)e - (int)wanted)).ThenBy(e => (int)e).First();
    }

    private static List<ModelCatalogEntry> Candidates(ModelCatalog catalog, ProviderId provider) =>
        [.. (catalog.Entries ?? []).Where(e => e.Provider == provider && e.Names is { Count: > 0 } && NearestEffort(e, ReasoningEffort.Medium) is not null)];

    private static ModelRecommendation? FromHistory(ModelCatalog catalog, List<ModelCatalogEntry> candidates, ModelRecommendationInput input, ReasoningEffort effort)
    {
        var known = (input.History ?? [])
            .Where(p => p.IsKnown && p.Provider == input.Provider && p.Tasks > 0)
            .Select(p => (Performance: p, Entry: catalog.Find(p.Provider, p.Model)))
            .Where(p => p.Entry is not null && candidates.Contains(p.Entry))
            .OrderByDescending(p => (double)p.Performance.FirstPassApprovals / p.Performance.Tasks)
            .ThenBy(p => p.Performance.Rejections)
            .ThenBy(p => p.Performance.Retries)
            .ThenByDescending(p => p.Performance.Tasks)
            .ThenBy(p => candidates.IndexOf(p.Entry!))
            .ThenBy(p => p.Performance.Model, StringComparer.Ordinal)
            .ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var (best, entry) = known[0];
        if ((double)best.FirstPassApprovals / best.Tasks <= HistoryMinimumFirstPassShare)
        {
            return null;
        }

        return ModelRecommendation.Of(
            entry!.Provider, entry.Name, entry.Tier, NearestEffort(entry, effort)!.Value, ModelRecommendationReasons.History, best.Model, best.Tasks, best.FirstPassApprovals);
    }

    private static ModelRecommendation Of(List<ModelCatalogEntry> candidates, ModelTier tier, ReasoningEffort effort, string reason, params object[] arguments)
    {
        var entry = TierOrder(tier).Select(t => candidates.FirstOrDefault(c => c.Tier == t)).First(c => c is not null)!;
        return ModelRecommendation.Of(entry.Provider, entry.Name, entry.Tier, NearestEffort(entry, effort)!.Value, reason, arguments);
    }

    /// <summary>The levels to look in for <paramref name="tier"/>: itself, then the nearest ones, the higher first.</summary>
    private static IEnumerable<ModelTier> TierOrder(ModelTier tier) =>
        Enum.GetValues<ModelTier>().OrderBy(t => Math.Abs((int)t - (int)tier)).ThenByDescending(t => (int)t);
}
