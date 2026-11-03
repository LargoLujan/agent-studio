using AgentStudio.Desktop.Markdown;

namespace AgentStudio.Tests.Desktop.Markdown;

/// <summary>
/// V31: the part of Markdown the journal uses, read as a person reads it: titles, paragraphs, lists with their sub-entries,
/// bold, code, and the escapes the journal writes resolved (the live journal showed «\(intento 1\)» and «**00:01:27**»).
/// </summary>
public sealed class MarkdownLiteTests
{
    [Fact]
    public void An_entry_of_the_journal_reads_with_its_time_in_bold_and_no_escapes()
    {
        var item = Assert.IsType<MarkdownListItem>(Assert.Single(MarkdownLite.Parse(
            @"- **00:01:27** · P-0003 · Ejecutor · claude-code/haiku · #prompt · «Arreglar Calc \(intento 1\) con \*cuidado\* y \[T-1\]» (tarea T1)")));

        Assert.Equal(0, item.Depth);
        Assert.Equal(
            [
                new MarkdownSpan(MarkdownSpanKind.Bold, "00:01:27"),
                new MarkdownSpan(MarkdownSpanKind.Text, " · P-0003 · Ejecutor · claude-code/haiku · #prompt · «Arreglar Calc (intento 1) con *cuidado* y [T-1]» (tarea T1)"),
            ],
            item.Spans);
    }

    [Fact]
    public void A_journal_has_its_title_its_paragraph_its_entries_and_their_sub_entries()
    {
        const string journal =
            "# Bitácora · 2026-10-30\r\n\r\n"
            + "Registro automático de Agent Studio.\r\nCuando el agente termina, se añade una línea «Resultado».\r\n\r\n"
            + "- **09:06:00** · sesión `eb1c6494` · #prompt #docs · «Revisa el ADR-0023»\r\n"
            + "  - Hecho: revisado.\r\n"
            + "  - Resultado P-0002 · 09:09:47 · Failed · 280,4 s\r\n"
            + "## Registro retroactivo\r\n"
            + "- **mañana** · #prompt · «sin hora»\r\n";

        var blocks = MarkdownLite.Parse(journal);

        Assert.Equal(new MarkdownHeading(1, [new MarkdownSpan(MarkdownSpanKind.Text, "Bitácora · 2026-10-30")]), blocks[0], MarkdownComparer.Instance);
        var paragraph = Assert.IsType<MarkdownParagraph>(blocks[1]);
        Assert.Equal(2, paragraph.Lines.Count);
        var entry = Assert.IsType<MarkdownListItem>(blocks[2]);
        Assert.Equal(
            [
                new MarkdownSpan(MarkdownSpanKind.Bold, "09:06:00"),
                new MarkdownSpan(MarkdownSpanKind.Text, " · sesión "),
                new MarkdownSpan(MarkdownSpanKind.Code, "eb1c6494"),
                new MarkdownSpan(MarkdownSpanKind.Text, " · #prompt #docs · «Revisa el ADR-0023»"),
            ],
            entry.Spans);
        Assert.Equal([1, 1], blocks.Skip(3).Take(2).Cast<MarkdownListItem>().Select(i => i.Depth));
        Assert.Equal(2, Assert.IsType<MarkdownHeading>(blocks[5]).Level);
        Assert.Equal(0, Assert.IsType<MarkdownListItem>(blocks[6]).Depth);
        Assert.Equal(7, blocks.Count);
    }

    [Theory]
    [InlineData(@"un \*asterisco\* escapado", "un *asterisco* escapado")]
    [InlineData(@"\*\*no es negrita\*\*", "**no es negrita**")]
    [InlineData("una ` sola comilla", "una ` sola comilla")]
    [InlineData("**sin cerrar", "**sin cerrar")]
    [InlineData(@"una barra \ sin escape y \d tampoco", @"una barra \ sin escape y \d tampoco")]
    public void What_is_not_bold_or_code_stays_text(string line, string text) =>
        Assert.Equal([new MarkdownSpan(MarkdownSpanKind.Text, text)], MarkdownLite.ParseSpans(line));

    [Fact]
    public void Code_keeps_its_content_as_it_is_and_a_fence_keeps_its_lines()
    {
        Assert.Equal([new MarkdownSpan(MarkdownSpanKind.Code, @"<b> y \(nada\)")], MarkdownLite.ParseSpans(@"`<b> y \(nada\)`"));

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownLite.Parse("```\n- no es lista\n# ni título\n```")));
        Assert.Equal(["- no es lista", "# ni título"], code.Lines);
    }

    [Fact]
    public void Nothing_comes_of_an_empty_text() => Assert.Empty(MarkdownLite.Parse(string.Empty));

    /// <summary>Records with lists compare their lists by reference; the blocks are compared by their content.</summary>
    private sealed class MarkdownComparer : IEqualityComparer<MarkdownBlock>
    {
        public static readonly MarkdownComparer Instance = new();

        public bool Equals(MarkdownBlock? x, MarkdownBlock? y) => (x, y) switch
        {
            (MarkdownHeading a, MarkdownHeading b) => a.Level == b.Level && a.Spans.SequenceEqual(b.Spans),
            _ => object.Equals(x, y),
        };

        public int GetHashCode(MarkdownBlock obj) => obj.GetType().GetHashCode();
    }
}
