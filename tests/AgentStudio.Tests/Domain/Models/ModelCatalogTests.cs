using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Models;

/// <summary>
/// The types of models and power (docs/17 D-22 to D-25): the closed list of efforts each CLI receives, the local catalog with
/// its invariants and no prices, the inputs and the result of the recommendation, and the effort of an agent.
/// </summary>
public sealed class ModelCatalogTests
{
    private static readonly ModelCatalogEntry Sonnet = new(ProviderId.ClaudeCode, "2.1.290", ["claude-sonnet-5-5", "sonnet"], ModelTier.Balanced, [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High]);

    private static readonly ModelCatalogEntry Haiku = new(ProviderId.ClaudeCode, "2.1.290", ["claude-haiku-4-5", "haiku"], ModelTier.Light, [ReasoningEffort.Low]);

    [Fact]
    public void The_efforts_are_the_closed_list_of_both_clis_and_each_has_the_token_they_take()
    {
        Assert.Equal(["Minimal", "Low", "Medium", "High", "XHigh", "Max"], Enum.GetNames<ReasoningEffort>());
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh", "max"], Enum.GetValues<ReasoningEffort>().Select(ReasoningEfforts.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReasoningEfforts.Token((ReasoningEffort)42));
        Assert.Equal(["Light", "Balanced", "Maximum"], Enum.GetNames<ModelTier>());
    }

    [Fact]
    public void The_catalog_finds_a_model_by_any_of_its_names_and_classifies_nothing_else()
    {
        var catalog = new ModelCatalog(1, [Sonnet, Haiku], ModelRecommendationRules.Default);

        Assert.Empty(catalog.Validate());
        Assert.Same(Sonnet, catalog.Find(ProviderId.ClaudeCode, "SONNET"));
        Assert.Same(Haiku, catalog.Find(ProviderId.ClaudeCode, "claude-haiku-4-5"));
        Assert.Null(catalog.Find(ProviderId.CodexCli, "sonnet"));
        Assert.Null(catalog.Find(ProviderId.ClaudeCode, "fable"));
        Assert.True(Sonnet.Accepts(ReasoningEffort.High));
        Assert.False(Haiku.Accepts(ReasoningEffort.Max));
        Assert.Equal("claude-sonnet-5-5", Sonnet.Name);
    }

    [Fact]
    public void An_entry_and_a_catalog_say_what_they_break()
    {
        var broken = new ModelCatalogEntry(ProviderId.ClaudeCode, " ", ["opus", "OPUS", " "], (ModelTier)7, [ReasoningEffort.Low, ReasoningEffort.Low, (ReasoningEffort)9]);

        Assert.Equal(
            ["model-catalog.cli-version-required", "model-catalog.name-blank", "model-catalog.name-duplicate", "model-catalog.tier-unknown", "model-catalog.effort-unknown", "model-catalog.effort-duplicate"],
            broken.Validate());
        Assert.Contains("model-catalog.name-in-two-entries", new ModelCatalog(1, [Sonnet, Sonnet with { Names = ["sonnet"] }], ModelRecommendationRules.Default).Validate());
        Assert.Contains("model-catalog.version-required", new ModelCatalog(0, [], ModelRecommendationRules.Default).Validate());
    }

    [Fact]
    public void The_rules_of_the_recommendation_start_with_the_ones_of_the_design()
    {
        var rules = ModelRecommendationRules.Default;

        Assert.Equal((5, 2000), (rules.OrchestratorMaximumFromMandatoryRequirements, rules.OrchestratorMaximumFromFiles));
        Assert.Equal((ReasoningEffort.High, ReasoningEffort.Medium, ReasoningEffort.High), (rules.OrchestratorEffort, rules.ExecutorEffort, rules.JudgeEffort));
    }

    [Fact]
    public void A_performance_is_known_from_three_references_and_a_recommendation_says_why_with_a_code()
    {
        Assert.False(new ModelPerformance(ProviderId.ClaudeCode, "sonnet", 2, 2, 0, 0).IsKnown);
        Assert.True(new ModelPerformance(ProviderId.ClaudeCode, "sonnet", 3, 2, 1, 0).IsKnown);

        var recommendation = ModelRecommendation.Of(ProviderId.ClaudeCode, "sonnet", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.History, "sonnet", 9, 7);
        Assert.Equal([(object)"sonnet", 9, 7], recommendation.ReasonArguments);
        Assert.Throws<ArgumentException>(() => ModelRecommendation.Of(ProviderId.ClaudeCode, "sonnet", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.History, new object()));
    }

    /// <summary>D-25: the effort of an agent is optional, and only a value of the list is valid.</summary>
    [Fact]
    public void The_effort_of_an_agent_is_optional_and_one_of_the_list()
    {
        var agent = new AgentDefinition(
            Aid("ejecutor"), "Ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"), string.Empty, [], ["Read"], [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

        Assert.Null(agent.Effort);
        Assert.Empty(agent.Validate());
        Assert.Empty((agent with { Effort = ReasoningEffort.Max }).Validate());
        Assert.Equal(["agent.effort-unknown"], (agent with { Effort = (ReasoningEffort)9 }).Validate());
    }

    /// <summary>
    /// Each CLI takes its own levels (docs/17 D-22): Claude Code from «low» to «max», Codex from «minimal» to «xhigh», any other
    /// provider none. An agent with a level its CLI does not take is invalid (WP2 builds the arguments from the same table).
    /// </summary>
    [Fact]
    public void The_effort_of_an_agent_is_one_its_cli_takes()
    {
        var claude = new AgentDefinition(
            Aid("ejecutor"), "Ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"), string.Empty, [], ["Read"], [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));
        var codex = claude with { Model = new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5.5") };
        var gemini = claude with { Model = new ModelBinding(ProviderId.GeminiCli, VendorId.Google, null) };

        Assert.Equal(["agent.effort-not-for-provider"], (claude with { Effort = ReasoningEffort.Minimal }).Validate());
        Assert.Equal(["agent.effort-not-for-provider"], (codex with { Effort = ReasoningEffort.Max }).Validate());
        Assert.Equal(["agent.effort-not-for-provider"], (gemini with { Effort = ReasoningEffort.Low }).Validate());
        Assert.Empty((codex with { Effort = ReasoningEffort.Minimal }).Validate());
        Assert.Empty((claude with { Effort = ReasoningEffort.XHigh }).Validate());
        Assert.Equal([ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max], ReasoningEfforts.For(ProviderId.ClaudeCode));
        Assert.Equal([ReasoningEffort.Minimal, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh], ReasoningEfforts.For(ProviderId.CodexCli));
        Assert.Empty(ReasoningEfforts.For(ProviderId.GeminiCli));
        Assert.Empty(gemini.Validate());
    }
}
