using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Models;

/// <summary>
/// The deterministic recommendation of a model (docs/17 D-24, F13-11): the rules of each role, the history when there is
/// data, the effort the model accepts, the second light executor, and that it never names a model the catalog does not offer
/// for the CLI of the agent. Nothing here launches an agent or spends quota.
/// </summary>
public sealed class ModelRecommenderTests
{
    private static readonly ReasoningEffort[] AllClaude = [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max];

    private static readonly ReasoningEffort[] AllCodex = [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh];

    /// <summary>A catalog in the shape of the shipped one: within a level, the first entry of a CLI is the preferred one.</summary>
    private static ModelCatalog Catalog(ModelRecommendationRules? rules = null) => new(
        1,
        [
            Claude(["claude-opus-5-5", "opus"], ModelTier.Maximum, AllClaude),
            Claude(["claude-fable-5-1", "fable"], ModelTier.Maximum, AllClaude),
            Claude(["claude-sonnet-5-5", "sonnet"], ModelTier.Balanced, AllClaude),
            Claude(["claude-haiku-5-5"], ModelTier.Light, AllClaude),
            Claude(["haiku"], ModelTier.Light, []),
            Codex(["gpt-6-astra"], ModelTier.Maximum),
            Codex(["gpt-6.1-sol"], ModelTier.Balanced),
            Codex(["gpt-6-luna"], ModelTier.Light),
        ],
        rules ?? ModelRecommendationRules.Default);

    private static ModelCatalogEntry Claude(string[] names, ModelTier tier, ReasoningEffort[] efforts) => new(ProviderId.ClaudeCode, "2.1.290", names, tier, efforts);

    private static ModelCatalogEntry Codex(string[] names, ModelTier tier) => new(ProviderId.CodexCli, "0.160.0", names, tier, AllCodex);

    private static ModelRecommendationInput Input(
        AgentRole role,
        ProviderId? provider = null,
        int? files = 300,
        int requirements = 3,
        int mandatory = 1,
        bool docsOrTests = false,
        params ModelPerformance[] history) =>
        new(role, provider ?? ProviderId.ClaudeCode, [VendorId.Anthropic], files, requirements, mandatory, "dotnet", 12, docsOrTests, history);

    private static void AssertRecommends(ModelRecommendation? actual, string model, ModelTier tier, ReasoningEffort effort, string reason, params object[] arguments)
    {
        Assert.NotNull(actual);
        Assert.Equal((model, tier, effort, reason), (actual.Model, actual.Tier, actual.Effort, actual.ReasonCode));
        Assert.Equal(arguments, actual.ReasonArguments);
    }

    [Fact]
    public void The_orchestrator_is_balanced_with_a_high_effort_by_default()
    {
        AssertRecommends(ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator)), "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.High, ModelRecommendationReasons.RoleDefault);
    }

    [Fact]
    public void The_orchestrator_is_maximum_from_five_mandatory_requirements_or_more_than_two_thousand_files()
    {
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator, mandatory: 5, requirements: 8)),
            "claude-opus-5-5", ModelTier.Maximum, ReasoningEffort.High, ModelRecommendationReasons.ManyMandatoryRequirements, 5);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator, files: 2001)),
            "claude-opus-5-5", ModelTier.Maximum, ReasoningEffort.High, ModelRecommendationReasons.LargeProject, 2001);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator, mandatory: 4, files: 2000)),
            "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.High, ModelRecommendationReasons.RoleDefault);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator, files: null)),
            "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.High, ModelRecommendationReasons.RoleDefault);
    }

    /// <summary>The rules live in the catalog, so they can be adjusted without a new version of the app (D-24).</summary>
    [Fact]
    public void The_rules_of_the_catalog_are_the_ones_applied()
    {
        var rules = new ModelRecommendationRules(OrchestratorMaximumFromMandatoryRequirements: 2, OrchestratorEffort: ReasoningEffort.XHigh, ExecutorEffort: ReasoningEffort.Low);

        AssertRecommends(
            ModelRecommender.Recommend(Catalog(rules), Input(AgentRole.Orchestrator, mandatory: 2)),
            "claude-opus-5-5", ModelTier.Maximum, ReasoningEffort.XHigh, ModelRecommendationReasons.ManyMandatoryRequirements, 2);
        AssertRecommends(ModelRecommender.Recommend(Catalog(rules), Input(AgentRole.Executor)), "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.Low, ModelRecommendationReasons.RoleDefault);
    }

    [Fact]
    public void An_executor_is_balanced_with_a_medium_effort_and_the_judge_the_highest_level_of_its_cli_with_a_high_one()
    {
        AssertRecommends(ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor)), "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.RoleDefault);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Judge, ProviderId.CodexCli)),
            "gpt-6-astra", ModelTier.Maximum, ReasoningEffort.High, ModelRecommendationReasons.JudgeHighestOtherVendor);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Auditor, ProviderId.CodexCli)),
            "gpt-6-astra", ModelTier.Maximum, ReasoningEffort.High, ModelRecommendationReasons.JudgeHighestOtherVendor);
    }

    /// <summary>D-24: «tus 9 tareas con sonnet se aprobaron a la primera en 7». The name the project used is the one said.</summary>
    [Fact]
    public void The_history_decides_when_a_known_model_passed_more_than_half_of_its_tasks_at_the_first_attempt()
    {
        var sonnet = new ModelPerformance(ProviderId.ClaudeCode, "sonnet", 9, 7, 2, 0);
        var opus = new ModelPerformance(ProviderId.ClaudeCode, "claude-opus-5-5", 4, 4, 0, 0);

        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, history: [sonnet])),
            "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.History, "sonnet", 9, 7);
        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, history: [sonnet, opus])),
            "claude-opus-5-5", ModelTier.Maximum, ReasoningEffort.Medium, ModelRecommendationReasons.History, "claude-opus-5-5", 4, 4);
    }

    [Fact]
    public void A_history_that_is_unknown_weak_of_another_cli_or_of_a_model_the_catalog_does_not_classify_does_not_decide()
    {
        ModelPerformance[] history =
        [
            new(ProviderId.ClaudeCode, "claude-haiku-5-5", 2, 2, 0, 0),
            new(ProviderId.ClaudeCode, "claude-opus-5-5", 6, 3, 3, 1),
            new(ProviderId.CodexCli, "gpt-6-luna", 9, 9, 0, 0),
            new(ProviderId.ClaudeCode, "claude-mythos-5-1", 9, 9, 0, 0),
        ];

        AssertRecommends(
            ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, history: history)),
            "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.RoleDefault);
    }

    /// <summary>The history counts tasks, which only producers do: the orchestrator and the judge keep their rules.</summary>
    [Fact]
    public void The_history_never_changes_the_orchestrator_nor_the_judge()
    {
        var haiku = new ModelPerformance(ProviderId.ClaudeCode, "claude-haiku-5-5", 10, 10, 0, 0);

        Assert.Equal(ModelRecommendationReasons.RoleDefault, ModelRecommender.Recommend(Catalog(), Input(AgentRole.Orchestrator, history: [haiku]))!.ReasonCode);
        Assert.Equal(ModelRecommendationReasons.JudgeHighestOtherVendor, ModelRecommender.Recommend(Catalog(), Input(AgentRole.Judge, history: [haiku]))!.ReasonCode);
    }

    [Fact]
    public void The_history_breaks_ties_by_rejections_then_retries_then_tasks_then_the_order_of_the_catalog()
    {
        var sonnet = new ModelPerformance(ProviderId.ClaudeCode, "claude-sonnet-5-5", 4, 4, 0, 1);
        var opus = new ModelPerformance(ProviderId.ClaudeCode, "claude-opus-5-5", 4, 4, 0, 0);
        var fable = new ModelPerformance(ProviderId.ClaudeCode, "fable", 4, 4, 0, 0);

        Assert.Equal("claude-opus-5-5", ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, history: [sonnet, fable, opus]))!.Model);
        Assert.Equal("claude-fable-5-1", ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, history: [sonnet, fable]))!.Model);
    }

    [Fact]
    public void A_second_light_executor_is_recommended_only_when_the_plans_have_tasks_of_documentation_or_tests()
    {
        Assert.Null(ModelRecommender.RecommendSecondExecutor(Catalog(), Input(AgentRole.Executor)));

        // The alias haiku takes no effort in the catalog, so the light model recommended is the first one that does.
        AssertRecommends(
            ModelRecommender.RecommendSecondExecutor(Catalog(), Input(AgentRole.Executor, docsOrTests: true)),
            "claude-haiku-5-5", ModelTier.Light, ReasoningEffort.Medium, ModelRecommendationReasons.LightSecondExecutor);
        AssertRecommends(
            ModelRecommender.RecommendSecondExecutor(Catalog(), Input(AgentRole.Executor, ProviderId.CodexCli, docsOrTests: true)),
            "gpt-6-luna", ModelTier.Light, ReasoningEffort.Medium, ModelRecommendationReasons.LightSecondExecutor);
    }

    [Fact]
    public void The_effort_is_the_one_of_the_rule_when_the_model_takes_it_else_the_nearest_the_lower_on_a_tie()
    {
        var gapped = Claude(["claude-sonnet-x"], ModelTier.Balanced, [ReasoningEffort.Low, ReasoningEffort.High]);
        var upper = Claude(["claude-sonnet-y"], ModelTier.Balanced, [ReasoningEffort.High, ReasoningEffort.Max]);

        Assert.Equal(ReasoningEffort.Low, ModelRecommender.NearestEffort(gapped, ReasoningEffort.Medium));
        Assert.Equal(ReasoningEffort.High, ModelRecommender.NearestEffort(upper, ReasoningEffort.Medium));
        Assert.Equal(ReasoningEffort.High, ModelRecommender.NearestEffort(gapped, ReasoningEffort.High));
        Assert.Null(ModelRecommender.NearestEffort(Claude(["haiku"], ModelTier.Light, []), ReasoningEffort.Medium));

        // An effort the CLI itself does not take is never chosen, even if an entry listed it (the reader refuses such a catalog).
        Assert.Equal(ReasoningEffort.Low, ModelRecommender.NearestEffort(Claude(["claude-x"], ModelTier.Light, [ReasoningEffort.Minimal, ReasoningEffort.Low]), ReasoningEffort.Minimal));

        var catalog = new ModelCatalog(1, [upper], ModelRecommendationRules.Default);
        AssertRecommends(ModelRecommender.Recommend(catalog, Input(AgentRole.Executor)), "claude-sonnet-y", ModelTier.Balanced, ReasoningEffort.High, ModelRecommendationReasons.RoleDefault);
    }

    [Fact]
    public void A_level_without_candidates_falls_back_to_the_nearest_one_the_higher_first_and_a_cli_without_any_gets_none()
    {
        var onlyMaximum = new ModelCatalog(1, [Claude(["claude-opus-5-5"], ModelTier.Maximum, AllClaude)], ModelRecommendationRules.Default);
        var onlyLight = new ModelCatalog(1, [Claude(["claude-haiku-5-5"], ModelTier.Light, AllClaude)], ModelRecommendationRules.Default);
        var both = new ModelCatalog(1, [Claude(["claude-haiku-5-5"], ModelTier.Light, AllClaude), Claude(["claude-opus-5-5"], ModelTier.Maximum, AllClaude)], ModelRecommendationRules.Default);

        Assert.Equal("claude-opus-5-5", ModelRecommender.Recommend(onlyMaximum, Input(AgentRole.Executor))!.Model);
        Assert.Equal("claude-haiku-5-5", ModelRecommender.Recommend(onlyLight, Input(AgentRole.Orchestrator))!.Model);
        Assert.Equal("claude-opus-5-5", ModelRecommender.Recommend(both, Input(AgentRole.Executor))!.Model);
        Assert.Equal("claude-haiku-5-5", ModelRecommender.Recommend(onlyLight, Input(AgentRole.Judge))!.Model);
        Assert.Null(ModelRecommender.Recommend(Catalog(), Input(AgentRole.Executor, ProviderId.GeminiCli)));
        Assert.Null(ModelRecommender.Recommend(new ModelCatalog(1, [Claude(["haiku"], ModelTier.Light, [])], ModelRecommendationRules.Default), Input(AgentRole.Executor)));
        Assert.Null(ModelRecommender.RecommendSecondExecutor(Catalog(), Input(AgentRole.Executor, ProviderId.GeminiCli, docsOrTests: true)));
    }

    [Fact]
    public void The_same_input_always_gives_the_same_recommendation()
    {
        var input = Input(AgentRole.Executor, history: [new ModelPerformance(ProviderId.ClaudeCode, "sonnet", 9, 7, 2, 0)]);
        var first = ModelRecommender.Recommend(Catalog(), input)!;
        var second = ModelRecommender.Recommend(Catalog(), input)!;

        Assert.Equal((first.Provider, first.Model, first.Tier, first.Effort, first.ReasonCode), (second.Provider, second.Model, second.Tier, second.Effort, second.ReasonCode));
        Assert.Equal(first.ReasonArguments, second.ReasonArguments);
    }

    /// <summary>
    /// Applying a recommendation an agent already follows must change nothing, so the hash of its template does not change
    /// either (D-25): an alias and its full name are the same model.
    /// </summary>
    [Fact]
    public void An_agent_with_the_alias_of_the_recommended_model_and_its_effort_already_follows_it()
    {
        var catalog = Catalog();
        var recommendation = ModelRecommender.Recommend(catalog, Input(AgentRole.Executor))!;
        var agent = new AgentDefinition(
            Aid("ejecutor"), "Ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"), string.Empty, [], ["Read"], [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false), ReasoningEffort.Medium);

        Assert.True(ModelRecommender.IsFollowedBy(catalog, recommendation, agent));
        Assert.True(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Model = agent.Model with { Model = "CLAUDE-SONNET-5-5" } }));
        Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Effort = null }));
        Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Model = agent.Model with { Model = null } }));
        Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Model = agent.Model with { Model = "opus" } }));
        Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Model = new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "sonnet") }));
        Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent with { Model = agent.Model with { Model = "otro-modelo" } }));
    }
}
