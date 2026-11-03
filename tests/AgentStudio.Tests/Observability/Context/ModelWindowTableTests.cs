using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>
/// The base table of context windows (docs/18 P3 and P36): the window Agent Studio estimates for a model the project has not
/// seen, so the first run of a new project has a percentage from its first step. Its windows are estimates that name the
/// table and its version, never exact; Codex has none («no expuesto»); a model it does not know has no window.
/// </summary>
public sealed class ModelWindowTableTests
{
    private static readonly ModelWindowTable Table = ModelWindowTable.Current;

    [Theory]
    [InlineData("claude-sonnet-5-5", 1_000_000)]
    [InlineData("claude-opus-5-5", 1_000_000)]
    [InlineData("claude-fable-5-1", 1_000_000)]
    [InlineData("claude-haiku-5-5", 1_000_000)]
    [InlineData("claude-haiku-4-5", 200_000)]
    public void A_known_model_has_its_window_as_an_estimate_of_agent_studio(string model, long tokens)
    {
        var window = Table.WindowFor(model, ProviderId.ClaudeCode);

        Assert.Equal(tokens, window.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, window.Origin);
        Assert.Contains("tabla base de ventanas de Agent Studio (" + ModelWindowTable.Version + ")", window.Source, StringComparison.Ordinal);
        Assert.Contains(model, window.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_date_the_cli_appends_to_a_model_is_not_part_of_its_id()
    {
        // The capture of Claude Code 2.1.290 names haiku with its date, and reports 200 000 for it.
        Assert.Equal(200_000L, Table.WindowFor("claude-haiku-4-5-20251001").Value);
        Assert.Equal(200_000L, Table.WindowFor("claude-haiku-4-5@20251001").Value);
        Assert.Equal(1_000_000L, Table.WindowFor("Claude-Sonnet-5-5 ").Value);
        Assert.Equal("claude-haiku-4-5", ModelWindowTable.Normalize("claude-haiku-4-5-20251001"));
        Assert.Equal("claude-sonnet-4-5[1m]", ModelWindowTable.Normalize("claude-sonnet-4-5-20250929[1m]"));
    }

    [Fact]
    public void An_alias_takes_the_window_of_its_family_and_haiku_the_smallest_so_it_never_under_reports()
    {
        Assert.Equal(1_000_000L, Table.WindowFor("opus").Value);
        Assert.Equal(1_000_000L, Table.WindowFor("sonnet").Value);
        Assert.Equal(1_000_000L, Table.WindowFor("opusplan").Value);
        Assert.Equal(200_000L, Table.WindowFor("haiku").Value);
    }

    [Fact]
    public void The_suffix_1m_is_the_variant_with_a_window_of_one_million()
    {
        var window = Table.WindowFor("claude-sonnet-4-5[1m]");

        Assert.Equal(1_000_000L, window.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, window.Origin);
    }

    [Fact]
    public void A_model_the_table_does_not_know_has_no_window_and_says_which()
    {
        var window = Table.WindowFor("claude-unknown-9");

        Assert.Equal(MetricOrigin.Missing, window.Origin);
        Assert.False(window.HasValue);
        Assert.Contains("claude-unknown-9", window.Source, StringComparison.Ordinal);
        Assert.Equal(MetricOrigin.Missing, Table.WindowFor(null).Origin);
        Assert.Equal(MetricOrigin.Missing, Table.WindowFor("  ").Origin);
    }

    [Fact]
    public void Codex_has_no_window_in_the_table_because_codex_exec_does_not_expose_it()
    {
        Assert.Equal(MetricOrigin.NotExposed, Table.WindowFor("gpt-6-luna").Origin);
        Assert.Equal(MetricOrigin.NotExposed, Table.WindowFor("gpt-5.1-codex-max").Origin);
        Assert.Equal(MetricOrigin.NotExposed, Table.WindowFor("claude-sonnet-5-5", ProviderId.CodexCli).Origin);
        Assert.Equal(MetricOrigin.NotExposed, Table.WindowFor(null, ProviderId.GeminiCli).Origin);
        Assert.DoesNotContain(Table.Windows.Keys, model => model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_window_is_positive_and_the_version_is_a_date()
    {
        Assert.NotEmpty(Table.Windows);
        Assert.All(Table.Windows.Values, tokens => Assert.True(tokens > 0));
        Assert.True(DateOnly.TryParseExact(ModelWindowTable.Version, "yyyy-MM-dd", out _), ModelWindowTable.Version);
    }

    [Fact]
    public void A_table_refuses_a_window_that_is_not_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelWindowTable(new Dictionary<string, long> { ["m"] = 0 }));
    }
}
