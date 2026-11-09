using System.Windows;
using System.Windows.Controls;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Desktop.Controls;

/// <summary>
/// What the common bar gains for Phase 13 (docs/17 D-12, D-13 and F13-6): the bar of context by parts. Each part is a segment of
/// the fill with a gap where it ends, and the words say each part with its value, from the resources; a bar without parts is
/// drawn and named exactly as before.
/// </summary>
public sealed class MetricBarSegmentsTests
{
    private static MetricBarReading ByParts() => new()
    {
        Label = "Contexto",
        Value = 6000,
        Maximum = 10000,
        ValueText = "60 %",
        Origin = "exacto · informado por el CLI",
        OriginKind = MetricOrigin.ReportedByProvider,
        Level = MetricBarLevel.Healthy,
        LevelText = "sano",
        Marks = [0.70, 0.85, 0.95],
        Segments =
        [
            new MetricBarSegment("Instrucciones del protocolo", 1500, "~1.500 tokens", true),
            new MetricBarSegment("Requisitos", 1000, "~1.000 tokens", true),
            new MetricBarSegment("Memoria", 500, "~500 tokens", true),
        ],
    };

    [Fact]
    public void Each_part_ends_where_its_sum_reaches_and_a_part_beyond_the_value_draws_no_gap()
    {
        Assert.Equal([0.15, 0.25, 0.30], ByParts().SegmentEnds);
        Assert.Equal([0.15], (ByParts() with { Value = 2000 }).SegmentEnds);
        Assert.Empty((ByParts() with { Value = null, ValueText = "no expuesto" }).SegmentEnds);
        Assert.Empty((ByParts() with { Segments = [] }).SegmentEnds);
    }

    [Fact]
    public void The_words_say_each_part_with_its_value_from_the_resources()
    {
        var reading = ByParts();

        Assert.EndsWith(Texts.Format("MetricBar_Parts", "Instrucciones del protocolo ~1.500 tokens; Requisitos ~1.000 tokens; Memoria ~500 tokens"), reading.AccessibleName, StringComparison.Ordinal);
        Assert.StartsWith("Contexto: 60 % · exacto · informado por el CLI · sano", reading.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("Contexto: 60 % · exacto · informado por el CLI · sano", (reading with { Segments = [] }).AccessibleName);
        Assert.NotEqual(reading, reading with { Segments = [.. reading.Segments.Take(2)] });
        Assert.Equal(reading, ByParts());
    }

    [Fact]
    public void The_bar_draws_a_gap_where_each_part_ends()
    {
        var (withParts, withoutParts) = WpfHost.Run(() =>
        {
            var parts = new MetricBar { Reading = ByParts() };
            var plain = new MetricBar { Reading = ByParts() with { Segments = [] } };
            return (((Panel)parts.FindName("SegmentsLayer")).Children.Count, ((Panel)plain.FindName("SegmentsLayer")).Children.Count);
        });

        Assert.Equal(3, withParts);
        Assert.Equal(0, withoutParts);
    }
}
