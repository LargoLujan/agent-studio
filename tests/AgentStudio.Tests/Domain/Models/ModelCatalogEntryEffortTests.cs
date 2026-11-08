using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Domain.Models;

/// <summary>
/// An entry of the catalog offers only levels of effort its CLI takes (<see cref="ReasoningEfforts.For"/>): otherwise the
/// selector would offer one that the template then refuses (<c>agent.effort-not-for-provider</c>, docs/17 D-25).
/// </summary>
public sealed class ModelCatalogEntryEffortTests
{
    private static ModelCatalogEntry Entry(ProviderId provider, params ReasoningEffort[] efforts) =>
        new(provider, "1.0.0", ["modelo"], ModelTier.Balanced, efforts);

    [Theory]
    [InlineData("claude-code", ReasoningEffort.Minimal)]
    [InlineData("codex-cli", ReasoningEffort.Max)]
    [InlineData("gemini-cli", ReasoningEffort.Low)]
    public void An_entry_with_a_level_its_cli_does_not_take_is_invalid(string provider, ReasoningEffort effort)
    {
        var entry = Entry(new ProviderId(provider), ReasoningEffort.Medium, effort);

        Assert.Contains("model-catalog.effort-not-for-cli", entry.Validate());
        Assert.Contains("model-catalog.effort-not-for-cli", new ModelCatalog(1, [entry], ModelRecommendationRules.Default).Validate());
    }

    [Fact]
    public void An_entry_with_the_levels_of_its_cli_or_none_is_valid()
    {
        Assert.Empty(Entry(ProviderId.ClaudeCode, [.. ReasoningEfforts.For(ProviderId.ClaudeCode)]).Validate());
        Assert.Empty(Entry(ProviderId.CodexCli, [.. ReasoningEfforts.For(ProviderId.CodexCli)]).Validate());
        Assert.Empty(Entry(ProviderId.ClaudeCode).Validate());
    }

    /// <summary>A value outside the list is said once, as unknown: it is not also a level of the wrong CLI.</summary>
    [Fact]
    public void A_value_outside_the_list_is_unknown_and_nothing_more()
    {
        Assert.Equal(["model-catalog.effort-unknown"], Entry(ProviderId.ClaudeCode, ReasoningEffort.Low, (ReasoningEffort)9).Validate());
    }
}
