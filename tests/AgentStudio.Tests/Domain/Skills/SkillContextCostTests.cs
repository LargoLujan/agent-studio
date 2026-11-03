using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Domain.Skills;

/// <summary>
/// docs/14 D-26 and docs/10 section 1: every active skill costs context in every run, because the CLI shows the model its
/// name and description. Agent Studio estimates it as those characters divided by 4, and says it is an estimate of its own.
/// </summary>
public sealed class SkillContextCostTests
{
    private static SkillEntry Entry(string name, string summary) =>
        new(
            new SkillId(name),
            name,
            summary,
            SkillSectors.IngenieriaSoftware,
            [],
            new SkillSource("owner/repo", "skills/" + name, "main", null, "MIT", SkillDistribution.Vendored));

    [Fact]
    public void The_cost_of_a_skill_is_its_name_and_description_divided_by_four_rounded_up()
    {
        // 4 + 5 characters: 9 / 4 is 2.25, so 3 tokens (never below what the model really reads).
        var cost = SkillContextCost.Of(Entry("abcd", "efghi"));

        Assert.Equal(3, cost.Value);
    }

    [Fact]
    public void A_multiple_of_four_characters_is_not_rounded()
    {
        Assert.Equal(2, SkillContextCost.Of(Entry("abcd", "efgh")).Value);
    }

    [Fact]
    public void The_cost_is_an_estimate_of_agent_studio_never_an_exact_figure()
    {
        var cost = SkillContextCost.Of(Entry("spark-optimization", "Optimiza trabajos de Spark."));

        Assert.Equal(MetricOrigin.EstimatedBySystem, cost.Origin);
        Assert.Equal(MetricProvenance.Estimated, cost.Provenance);
        Assert.Contains("4", cost.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Accented_text_counts_its_characters()
    {
        // «Diseño» has six characters, as the model sees them, whatever their bytes in UTF-8.
        Assert.Equal(2, SkillContextCost.Of(Entry("ux", "Diseño")).Value);
    }

    [Fact]
    public void The_total_of_what_an_agent_has_active_adds_the_costs_and_stays_an_estimate()
    {
        var total = SkillContextCost.Total([Entry("abcd", "efghi"), Entry("abcd", "efgh")]);

        Assert.Equal(5, total.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, total.Origin);
    }

    [Fact]
    public void An_agent_with_no_active_skill_costs_nothing_and_it_is_still_labelled_as_an_estimate()
    {
        var total = SkillContextCost.Total([]);

        Assert.Equal(0, total.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, total.Origin);
    }

    [Fact]
    public void Nothing_is_estimated_from_a_missing_skill()
    {
        Assert.Throws<ArgumentNullException>(() => SkillContextCost.Of(null!));
        Assert.Throws<ArgumentNullException>(() => SkillContextCost.Total(null!));
    }
}
