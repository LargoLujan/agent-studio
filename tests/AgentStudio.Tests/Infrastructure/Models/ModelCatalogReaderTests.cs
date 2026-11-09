using System.Text.Json.Nodes;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Models;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Providers.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Models;

/// <summary>
/// The local catalog of models (docs/17 D-23, F13-11): a versioned data file with its sources and no prices, read and checked
/// by its reader, which fails closed (an empty catalog with the problems, never an exception and never half a catalog).
/// </summary>
public sealed class ModelCatalogReaderTests
{
    private static ModelCatalogDocument Shipped => BuiltInModelCatalog.Shipped;

    /// <summary>The shipped JSON with one change, to see what the reader does with it.</summary>
    private static string Changed(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(ModelCatalogData.Json)!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static JsonObject FirstModel(JsonObject root) => root["models"]!.AsArray()[0]!.AsObject();

    [Fact]
    public void The_shipped_catalog_is_valid_and_every_entry_names_its_sources()
    {
        Assert.True(Shipped.IsValid, string.Join(", ", Shipped.Problems));
        Assert.Empty(Shipped.Catalog.Validate());
        Assert.Equal(new DateOnly(2026, 11, 8), Shipped.CheckedOn);
        Assert.Equal(Shipped.Catalog.Entries.Count, Shipped.EntrySources.Count);
        Assert.All(Shipped.Catalog.Entries, entry => Assert.NotEmpty(Shipped.SourcesOf(entry)));
        Assert.All(Shipped.Sources, source => Assert.False(string.IsNullOrWhiteSpace(source.Location)));
        Assert.Equal(ModelRecommendationRules.Default, Shipped.Catalog.Rules);
    }

    /// <summary>D-06 of docs/14: there is no table of prices, and the catalog never becomes one.</summary>
    [Fact]
    public void The_catalog_has_no_prices()
    {
        Assert.DoesNotContain("price", ModelCatalogData.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usd", ModelCatalogData.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$", ModelCatalogData.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_entry_was_checked_against_a_tested_version_of_its_cli_and_offers_only_efforts_its_cli_takes()
    {
        foreach (var entry in Shipped.Catalog.Entries)
        {
            Assert.Contains(entry.TestedCliVersion, SupportedVersions.For(entry.Provider));
            Assert.All(entry.Efforts, effort => Assert.True(ReasoningEfforts.Accepts(entry.Provider, effort), $"{entry.Name}: {effort}"));
            Assert.True(entry.Note is null || UserText.IsCode(entry.Note), entry.Name);
        }
    }

    /// <summary>The aliases of <c>claude --model</c> and the models of Codex, each with its level, as D-22 says them.</summary>
    [Theory]
    [InlineData("claude-code", "opus", ModelTier.Maximum)]
    [InlineData("claude-code", "fable", ModelTier.Maximum)]
    [InlineData("claude-code", "sonnet", ModelTier.Balanced)]
    [InlineData("claude-code", "haiku", ModelTier.Light)]
    [InlineData("claude-code", "claude-haiku-4-5", ModelTier.Light)]
    [InlineData("codex-cli", "gpt-6-astra", ModelTier.Maximum)]
    [InlineData("codex-cli", "gpt-6.1-sol", ModelTier.Balanced)]
    [InlineData("codex-cli", "gpt-6-luna", ModelTier.Light)]
    public void The_catalog_classifies_the_known_names_of_each_cli(string provider, string model, ModelTier tier)
    {
        Assert.Equal(tier, Shipped.Catalog.Find(new ProviderId(provider), model)?.Tier);
    }

    [Fact]
    public void A_name_the_catalog_does_not_know_is_unclassified_and_so_is_a_model_of_another_cli()
    {
        Assert.Null(Shipped.Catalog.Find(ProviderId.ClaudeCode, "opusplan"));
        Assert.Null(Shipped.Catalog.Find(ProviderId.ClaudeCode, "claude-mythos-5-1"));
        Assert.Null(Shipped.Catalog.Find(ProviderId.CodexCli, "sonnet"));
        Assert.Null(Shipped.Catalog.Find(ProviderId.GeminiCli, "gpt-6-luna"));
    }

    /// <summary>
    /// The order is the preference (ADR-0050): «máximo» is Opus before Fable; the alias haiku, whose model is not verified and
    /// takes no effort, is never recommended; GPT-6 Luna, which the account accepted, before Terra.
    /// </summary>
    [Fact]
    public void The_order_of_the_shipped_catalog_gives_the_recommendations_of_the_design()
    {
        var catalog = Shipped.Catalog;
        ModelRecommendationInput Input(AgentRole role, ProviderId provider, int mandatory = 0, bool docsOrTests = false) =>
            new(role, provider, [VendorId.Anthropic], 120, 4, mandatory, "dotnet", 12, docsOrTests, []);

        Assert.Equal(("claude-opus-5-5", ReasoningEffort.High), Pick(ModelRecommender.Recommend(catalog, Input(AgentRole.Orchestrator, ProviderId.ClaudeCode, mandatory: 5))));
        Assert.Equal(("claude-sonnet-5-5", ReasoningEffort.High), Pick(ModelRecommender.Recommend(catalog, Input(AgentRole.Orchestrator, ProviderId.ClaudeCode))));
        Assert.Equal(("claude-sonnet-5-5", ReasoningEffort.Medium), Pick(ModelRecommender.Recommend(catalog, Input(AgentRole.Executor, ProviderId.ClaudeCode))));
        Assert.Equal(("claude-haiku-5-5", ReasoningEffort.Medium), Pick(ModelRecommender.RecommendSecondExecutor(catalog, Input(AgentRole.Executor, ProviderId.ClaudeCode, docsOrTests: true))));
        Assert.Equal(("gpt-6-astra", ReasoningEffort.High), Pick(ModelRecommender.Recommend(catalog, Input(AgentRole.Judge, ProviderId.CodexCli))));
        Assert.Equal(("gpt-6-luna", ReasoningEffort.Medium), Pick(ModelRecommender.RecommendSecondExecutor(catalog, Input(AgentRole.Executor, ProviderId.CodexCli, docsOrTests: true))));

        static (string, ReasoningEffort) Pick(ModelRecommendation? recommendation) => (recommendation!.Model, recommendation.Effort);
    }

    [Fact]
    public void The_registered_source_is_the_shipped_catalog_and_registering_twice_registers_it_once()
    {
        var services = new ServiceCollection().AddAgentStudioModelCatalog().AddAgentStudioModelCatalog();

        Assert.Single(services, d => d.ServiceType == typeof(IModelCatalogSource));
        using var provider = services.BuildServiceProvider();
        Assert.Same(Shipped, provider.GetRequiredService<IModelCatalogSource>().Current);
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "{", "model-catalog.json-invalid" },
        { "[]", "model-catalog.shape-invalid" },
        { Changed(r => r["extra"] = 1), "model-catalog.shape-invalid" },
        { Changed(r => r["version"] = 2), "model-catalog.version-unsupported" },
        { Changed(r => r.Remove("version")), "model-catalog.version-required" },
        { Changed(r => r["version"] = 0), "model-catalog.version-required" },
        { Changed(r => r["checkedOn"] = "08/11/2026"), "model-catalog.checked-on-invalid" },
        { Changed(r => r["sources"] = new JsonArray()), "model-catalog.sources-required" },
        { Changed(r => r["sources"]!.AsArray().Add(r["sources"]![0]!.DeepClone())), "model-catalog.source-duplicate" },
        { Changed(r => r["rules"]!["judgeEffort"] = "ultra"), "model-catalog.rules-invalid" },
        { Changed(r => r["rules"]!["orchestratorMaximumFromFiles"] = 0), "model-catalog.rules-invalid" },
        { Changed(r => r.Remove("models")), "model-catalog.models-required" },
        { Changed(r => FirstModel(r)["provider"] = "gemini-pro"), "model-catalog.provider-unknown" },
        { Changed(r => FirstModel(r)["tier"] = "huge"), "model-catalog.tier-unknown" },
        { Changed(r => FirstModel(r)["efforts"] = new JsonArray("low", "ultra")), "model-catalog.effort-unknown" },
        { Changed(r => FirstModel(r)["efforts"] = new JsonArray("minimal")), "model-catalog.effort-not-for-cli" },
        { Changed(r => FirstModel(r)["efforts"] = new JsonArray("low", "low")), "model-catalog.effort-duplicate" },
        { Changed(r => FirstModel(r)["note"] = "Una nota escrita a mano"), "model-catalog.note-not-a-code" },
        { Changed(r => FirstModel(r)["sources"] = new JsonArray()), "model-catalog.entry-sources-required" },
        { Changed(r => FirstModel(r)["sources"] = new JsonArray("nowhere")), "model-catalog.entry-source-unknown" },
        { Changed(r => FirstModel(r)["names"] = new JsonArray()), "model-catalog.name-required" },
        { Changed(r => FirstModel(r)["names"] = new JsonArray("opus", " ")), "model-catalog.name-blank" },
        { Changed(r => FirstModel(r)["names"] = new JsonArray("opus", "sonnet")), "model-catalog.name-in-two-entries" },
        { Changed(r => FirstModel(r).Remove("testedCliVersion")), "model-catalog.cli-version-required" },
        { Changed(r => FirstModel(r)["price"] = 4), "model-catalog.entry-invalid" },
    };

    /// <summary>A broken catalog is not used at all: nothing is classified, nothing breaks, and the problem is said by its code.</summary>
    [Theory]
    [MemberData(nameof(Broken))]
    public void A_broken_catalog_gives_an_empty_one_with_its_problem(string json, string problem)
    {
        var document = ModelCatalogReader.Read(json);

        Assert.False(document.IsValid);
        Assert.Contains(problem, document.Problems);
        Assert.Empty(document.Catalog.Entries);
        Assert.Empty(document.Sources);
        Assert.Null(document.Catalog.Find(ProviderId.ClaudeCode, "opus"));
        Assert.Equal(ModelRecommendationRules.Default, document.Catalog.Rules);
    }

    [Fact]
    public void A_note_may_be_absent_and_the_rules_may_be_left_to_their_defaults()
    {
        var json = Changed(r =>
        {
            r.Remove("rules");
            FirstModel(r)["note"] = null;
        });

        var document = ModelCatalogReader.Read(json);

        Assert.True(document.IsValid, string.Join(", ", document.Problems));
        Assert.Null(document.Catalog.Entries[0].Note);
        Assert.Equal(ModelRecommendationRules.Default, document.Catalog.Rules);
    }

    [Fact]
    public void Every_effort_token_is_read_back_as_its_effort()
    {
        foreach (var effort in Enum.GetValues<ReasoningEffort>())
        {
            Assert.Equal(effort, ModelCatalogReader.EffortOf(ReasoningEfforts.Token(effort)));
        }

        Assert.Null(ModelCatalogReader.EffortOf("Low"));
        Assert.Null(ModelCatalogReader.EffortOf(null));
    }
}
