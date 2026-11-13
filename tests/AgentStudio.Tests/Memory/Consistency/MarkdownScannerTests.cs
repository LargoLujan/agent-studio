using System.Text;
using AgentStudio.Memory.Consistency;

namespace AgentStudio.Tests.Memory.Consistency;

/// <summary>
/// The reader of Markdown of the checks (docs/20 D-28): what is not a link is not one (blocks of code indented four spaces, HTML comments, also over several lines, code spans, also
/// over several lines), what is one still is, and the cost is held by a count of characters read, which does not depend on the clock.
/// </summary>
public sealed class MarkdownScannerTests
{
    private static string[] Destinations(string text) => [.. MarkdownScanner.Scan(text).Links.Select(l => l.Destination)];

    [Fact]
    public void A_block_of_code_indented_four_spaces_after_a_blank_line_holds_no_link()
    {
        var text = "Un párrafo.\n\n    [en código](a.md)\n\n    otra [línea](b.md)\n\t[con tabulador](c.md)\n\n[fuera](d.md)\n";

        Assert.Equal(["d.md"], Destinations(text));
    }

    [Fact]
    public void An_indented_line_that_goes_on_a_paragraph_or_a_list_is_not_a_block_of_code()
    {
        var text = "Un párrafo\n    sigue [aquí](a.md)\n\n- elemento\n    [continúa](b.md)\n";

        Assert.Equal(["a.md", "b.md"], Destinations(text));
    }

    [Fact]
    public void An_html_comment_holds_no_link_on_one_line_or_over_several()
    {
        var text = string.Join(
            '\n',
            "<!-- [uno](a.md) --> [visible](v1.md)",
            "antes <!-- [dos](b.md)",
            "[tres](c.md)",
            "--> [visible](v2.md)",
            "<!-- [cuatro](d.md) --> y <!-- [cinco](e.md) --> [visible](v3.md)");

        Assert.Equal(["v1.md", "v2.md", "v3.md"], Destinations(text));
    }

    [Fact]
    public void A_code_span_holds_no_link_also_when_it_goes_over_several_lines_of_a_paragraph()
    {
        var text = string.Join('\n', "Texto `código que sigue", "[uno](a.md) y más", "hasta aquí` y [visible](v1.md)", "", "Otro `sin cerrar", "", "[visible](v2.md)");

        Assert.Equal(["v1.md", "v2.md"], Destinations(text));
    }

    [Fact]
    public void A_backtick_that_is_never_closed_does_not_swallow_the_rest_of_the_file()
    {
        var text = "Una ` suelta\n" + string.Join('\n', Enumerable.Range(0, 30).Select(i => $"[l{i}](l{i}.md)"));

        // The span lasts at most MaxSpanLines lines of one paragraph; after that the backtick was only a backtick.
        Assert.Equal(30 - MarkdownScanner.MaxSpanLines, Destinations(text).Length);
    }

    [Fact]
    public void A_line_made_of_empty_links_cannot_make_every_candidate_read_to_the_end_of_the_line()
    {
        var line = string.Concat(Enumerable.Repeat("[](a", MarkdownScanner.MaxLineLength / 4));
        var text = string.Join('\n', Enumerable.Repeat(line, 200));

        _ = MarkdownScanner.Scan(text, out var steps);

        // Without a budget each of the 500 candidates of a line read up to 500 characters (250 000 a line); with it, a line reads at most its budget and one destination more.
        Assert.True(steps <= 200L * (MarkdownScanner.LineBudget + MarkdownScanner.MaxDestinationLength), steps.ToString());
        Assert.True(steps < text.Length * 3L, $"{steps} for {text.Length} characters");
    }

    [Fact]
    public void Ordinary_links_are_all_found_on_a_line_with_many()
    {
        var text = string.Concat(Enumerable.Range(0, 80).Select(i => $"[l{i}](f{i}.md) "));

        Assert.Equal(80, Destinations(text).Length);
    }

    [Fact]
    public void The_cost_grows_with_the_size_and_not_with_its_square()
    {
        long Cost(int lines)
        {
            _ = MarkdownScanner.Scan(string.Concat(Enumerable.Repeat(new StringBuilder().Insert(0, "[x](((((((", 150).ToString() + "\n", lines)), out var steps);
            return steps;
        }

        Assert.True(Cost(400) <= 4 * Cost(100) + 1);
    }
}
