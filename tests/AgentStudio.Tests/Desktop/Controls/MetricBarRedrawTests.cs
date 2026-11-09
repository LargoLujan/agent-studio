using System.Windows.Controls;
using AgentStudio.Desktop.Controls;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Controls;

/// <summary>
/// F13-7 (docs/17 D-16): the common bar draws only what changed. A screen with dozens of bars sets the same reading again at every
/// grouped change; a bar that says the same draws nothing, and its marks and segments are drawn again only when they change.
/// </summary>
public sealed class MetricBarRedrawTests
{
    private static MetricBarReading Reading(double value, params double[] marks) => new()
    {
        Label = "Contexto",
        Value = value,
        ValueText = $"{value} %",
        Origin = "exacto",
        Marks = marks,
    };

    [Fact]
    public void A_reading_that_says_the_same_leaves_the_marks_as_they_were_and_another_set_of_marks_draws_them_again()
    {
        var (same, changed) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var bar = new MetricBar { Reading = Reading(40, 0.7, 0.85, 0.95) };
            var marks = (Grid)bar.FindName("MarksLayer");
            var first = marks.Children.Cast<object>().ToList();

            // The value moves but the thresholds stay: the marks are the same visuals.
            bar.Reading = Reading(52, 0.7, 0.85, 0.95);
            var afterValue = marks.Children.Cast<object>().ToList();

            // Other thresholds: drawn again.
            bar.Reading = Reading(52, 0.5);
            var afterMarks = marks.Children.Cast<object>().ToList();
            return (first.SequenceEqual(afterValue), afterValue.SequenceEqual(afterMarks));
        });

        Assert.True(same);
        Assert.False(changed);
    }

    [Fact]
    public void The_same_reading_again_changes_nothing_and_the_words_follow_a_new_one()
    {
        var (before, after, again) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var bar = new MetricBar { Reading = Reading(40, 0.7) };
            var fill = (System.Windows.Shapes.Rectangle)bar.FindName("Fill");
            var brush = fill.Fill;
            var valueRun = (System.Windows.Documents.Run)bar.FindName("ValueRun");
            var first = valueRun.Text;

            bar.Reading = Reading(40, 0.7);
            var same = ReferenceEquals(fill.Fill, brush) && valueRun.Text == first;

            bar.Reading = Reading(75, 0.7);
            return (first, valueRun.Text, same);
        });

        Assert.Equal("40 %", before);
        Assert.Equal("75 %", after);
        Assert.True(again);
    }

    [Fact]
    public void A_bar_that_goes_from_marks_to_none_clears_them()
    {
        var count = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var bar = new MetricBar { Reading = Reading(40, 0.7, 0.9) };
            bar.Reading = Reading(40);
            return ((Grid)bar.FindName("MarksLayer")).Children.Count;
        });

        Assert.Equal(0, count);
    }
}
