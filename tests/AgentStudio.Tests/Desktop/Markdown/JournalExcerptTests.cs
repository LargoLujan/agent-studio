using AgentStudio.Desktop.Markdown;

namespace AgentStudio.Tests.Desktop.Markdown;

/// <summary>
/// What the readable view of a long day draws (the review of V31): its title and the lines under it, then its latest entries,
/// whole, within a budget of lines and characters. Nothing of the file changes: this is only what is drawn.
/// </summary>
public sealed class JournalExcerptTests
{
    private const string Head = "# Bitácora · 2026-10-30\n\nRegistro automático de Agent Studio.\n\n";

    [Fact]
    public void A_short_day_is_drawn_whole()
    {
        var day = Head + "- **09:00:00** · P-0001 · «una»\n  - Resultado P-0001\n";

        var excerpt = JournalExcerpt.Of(day, maxLines: 50, maxCharacters: 10_000);

        Assert.Equal(new JournalExcerpt(day, false, 1, 1, 7, 7), excerpt);
    }

    [Fact]
    public void A_long_day_keeps_its_title_and_its_latest_entries_whole()
    {
        var day = Head + string.Concat(Enumerable.Range(1, 100).Select(i => $"- **09:00:00** · P-{i:0000} · «petición {i}»\n  - Resultado P-{i:0000}\n  - Hecho: hecha {i}.\n"));

        var excerpt = JournalExcerpt.Of(day, maxLines: 20, maxCharacters: 100_000);

        var lines = excerpt.Text.Split('\n');
        Assert.True(excerpt.IsTrimmed);
        Assert.True(lines.Length <= 20, $"{lines.Length} lines drawn.");
        Assert.Equal(["# Bitácora · 2026-10-30", string.Empty, "Registro automático de Agent Studio.", string.Empty], lines[..4]);
        Assert.StartsWith("- **09:00:00** · P-", lines[4], StringComparison.Ordinal);
        Assert.Contains("«petición 100»", excerpt.Text, StringComparison.Ordinal);
        Assert.Equal((5, 100), (excerpt.ShownEntries, excerpt.TotalEntries));
    }

    [Fact]
    public void A_day_of_few_but_very_long_lines_is_kept_within_its_characters()
    {
        var day = Head + string.Concat(Enumerable.Range(1, 10).Select(i => $"- **09:00:00** · P-{i:0000} · «{new string('x', 3000)}»\n"));

        var excerpt = JournalExcerpt.Of(day, maxLines: 200, maxCharacters: 7_000);

        Assert.True(excerpt.IsTrimmed);
        Assert.True(excerpt.Text.Length <= 7_000, $"{excerpt.Text.Length} characters drawn.");
        Assert.Equal(2, excerpt.ShownEntries);
        Assert.Contains("P-0010", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_entry_longer_than_the_budget_is_cut_at_its_start()
    {
        var day = Head + "- **09:00:00** · P-0001 · «corta»\n- **09:00:01** · P-0002 · «" + new string('y', 20_000) + "»";

        var excerpt = JournalExcerpt.Of(day, maxLines: 200, maxCharacters: 5_000);

        Assert.True(excerpt.IsTrimmed);
        Assert.True(excerpt.Text.Length <= 5_000, $"{excerpt.Text.Length} characters drawn.");
        Assert.StartsWith(Head.TrimEnd('\n'), excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("\n…", excerpt.Text, StringComparison.Ordinal);
        Assert.EndsWith("»", excerpt.Text, StringComparison.Ordinal);
    }
}
