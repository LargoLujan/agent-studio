using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Models;

/// <summary>
/// A model or an effort that is not known never breaks a launch (docs/17 D-23 and D-25): the CLI gets its own default or the
/// name as written, and a coded notice says so. The efforts each CLI takes at all are a closed list, from its help.
/// </summary>
public sealed class ModelNoticesTests
{
    private static readonly ModelCatalog Catalog = new(
        1,
        [
            new(ProviderId.ClaudeCode, "2.1.290", ["claude-sonnet-5-5", "sonnet"], ModelTier.Balanced, [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High]),
            new(ProviderId.CodexCli, "0.160.0", ["gpt-6-luna"], ModelTier.Light, [ReasoningEffort.Low, ReasoningEffort.Medium]),
        ],
        ModelRecommendationRules.Default);

    [Fact]
    public void Each_cli_takes_its_own_closed_list_of_efforts()
    {
        Assert.Equal([ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max], CliReasoningEfforts.Of(ProviderId.ClaudeCode));
        Assert.Equal([ReasoningEffort.Minimal, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh], CliReasoningEfforts.Of(ProviderId.CodexCli));
        Assert.Empty(CliReasoningEfforts.Of(ProviderId.GeminiCli));

        Assert.False(CliReasoningEfforts.Accepts(ProviderId.ClaudeCode, ReasoningEffort.Minimal));
        Assert.False(CliReasoningEfforts.Accepts(ProviderId.CodexCli, ReasoningEffort.Max));
        Assert.False(CliReasoningEfforts.Accepts(ProviderId.ClaudeCode, (ReasoningEffort)42));
        Assert.Equal(ReasoningEffort.XHigh, CliReasoningEfforts.ForLaunch(ProviderId.CodexCli, ReasoningEffort.XHigh));
        Assert.Null(CliReasoningEfforts.ForLaunch(ProviderId.CodexCli, ReasoningEffort.Max));
        Assert.Null(CliReasoningEfforts.ForLaunch(ProviderId.ClaudeCode, null));
    }

    [Fact]
    public void A_known_model_with_an_effort_it_takes_says_nothing_and_neither_does_the_default_of_the_cli_without_an_effort()
    {
        Assert.Empty(ModelNotices.Of(Catalog, ProviderId.ClaudeCode, "sonnet", ReasoningEffort.Medium));
        Assert.Empty(ModelNotices.Of(Catalog, ProviderId.ClaudeCode, " claude-sonnet-5-5 ", null));
        Assert.Empty(ModelNotices.Of(Catalog, ProviderId.ClaudeCode, null, null));
    }

    [Fact]
    public void A_model_the_catalog_does_not_know_is_unclassified_and_goes_as_written()
    {
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.Unclassified, "claude-mythos-5-1")], ModelNotices.Of(Catalog, ProviderId.ClaudeCode, "claude-mythos-5-1", null));
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.Unclassified, "sonnet")], ModelNotices.Of(Catalog, ProviderId.CodexCli, "sonnet", null));
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.Unclassified, "sonnet")], ModelNotices.Of(null, ProviderId.ClaudeCode, "sonnet", null));
    }

    [Fact]
    public void An_effort_the_cli_does_not_take_goes_with_the_default_of_the_cli_and_is_said()
    {
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.EffortCliDefault, "minimal")], ModelNotices.Of(Catalog, ProviderId.ClaudeCode, "sonnet", ReasoningEffort.Minimal));
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.EffortCliDefault, "max")], ModelNotices.Of(Catalog, ProviderId.CodexCli, "gpt-6-luna", ReasoningEffort.Max));

        // A value outside the list (a template edited by hand) never throws: it is said by its number.
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.EffortCliDefault, "42")], ModelNotices.Of(Catalog, ProviderId.ClaudeCode, "sonnet", (ReasoningEffort)42));
    }

    [Fact]
    public void An_effort_the_catalog_does_not_list_for_the_model_or_one_for_the_default_model_is_said_and_still_passed()
    {
        Assert.Equal(
            [ModelNotice.Of(ModelNoticeCodes.EffortNotInCatalog, "sonnet", "xhigh")],
            ModelNotices.Of(Catalog, ProviderId.ClaudeCode, "sonnet", ReasoningEffort.XHigh));
        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.EffortForDefaultModel, "high")], ModelNotices.Of(Catalog, ProviderId.ClaudeCode, null, ReasoningEffort.High));
        Assert.Equal(
            [ModelNotice.Of(ModelNoticeCodes.Unclassified, "otro"), ModelNotice.Of(ModelNoticeCodes.EffortCliDefault, "max")],
            ModelNotices.Of(Catalog, ProviderId.CodexCli, "otro", ReasoningEffort.Max));
    }

    [Fact]
    public void The_notices_of_an_agent_are_those_of_its_model_and_its_effort()
    {
        var agent = new AgentDefinition(
            Aid("juez"), "Juez", AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-6-luna"), string.Empty, [], [], [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false), ReasoningEffort.High);

        Assert.Equal([ModelNotice.Of(ModelNoticeCodes.EffortNotInCatalog, "gpt-6-luna", "high")], ModelNotices.Of(Catalog, agent));
        Assert.All(ModelNoticeCodes.All, code => Assert.Matches("^model\\.[a-z0-9-]+$", code));
    }
}
