using System.IO;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Desktop.Localization;
using AgentStudio.Memory.Summary;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;

namespace AgentStudio.Tests.Memory.Summary;

/// <summary>
/// The daily summary as the screens say it (docs/17 D-47 and D-48, ADR-0054 addendum of Phase 14): the text anchored with the entry of a
/// closed day stays in Spanish and goes to the agents; its lines that the builder writes from a template are said in the language of
/// the interface, and what the journal said (prompts, tags, «Hecho» lines) goes through as it is.
/// </summary>
public sealed class DailySummaryTextsTests
{
    /// <summary>The words the screen says differently from the anchored text, because the guide of texts asks for «petición» and not «prompt» (D-52).</summary>
    private static readonly (string Anchored, string Screen)[] Glossary =
    [
        ("## Prompts: ", "## Peticiones: "),
        (" prompts del producto)", " peticiones de Agent Studio)"),
        ("- Sin prompts del producto: el hook de desarrollo no registra tokens.", "- Sin peticiones de Agent Studio: las de desarrollo no registran tokens."),
    ];

    private static string Expected =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Summary", "Fixtures", "resumen-ejemplo.md"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void In_Spanish_the_screen_says_what_is_anchored_but_for_the_words_of_the_glossary()
    {
        var anchored = Glossary.Aggregate(Expected, (text, word) => text.Replace(word.Anchored, word.Screen, StringComparison.Ordinal));

        Assert.Equal(anchored, Spanish.RenderNested(DailySummaryTexts.Coded(Expected)));
    }

    [Fact]
    public void In_English_the_lines_of_the_system_are_English_and_what_the_journal_said_is_as_it_was()
    {
        var english = English.RenderNested(DailySummaryTexts.Coded(Expected));

        Assert.StartsWith("# Summary of 2026-03-10\n\nDeterministic summary of the journal of the day.", english, StringComparison.Ordinal);
        Assert.Contains("\n## Requests: 9\n", english, StringComparison.Ordinal);
        Assert.Contains("\n## Results: 5\n", english, StringComparison.Ordinal);
        Assert.Contains("\n- Total time: 562.9 s\n", english, StringComparison.Ordinal);
        Assert.Contains("\n- No result in the journal: 1\n", english, StringComparison.Ordinal);
        Assert.Contains("\n- Input 20,000 → output 2,170 (partial: exact in 2 of 6 Agent Studio requests)\n", english, StringComparison.Ordinal);
        Assert.Contains("\n## Failures and limits: 3\n", english, StringComparison.Ordinal);
        Assert.Contains("\n## Most frequent tags\n", english, StringComparison.Ordinal);
        Assert.Contains("\n## “Done” lines: 4\n", english, StringComparison.Ordinal);
        Assert.Contains("\n## Lines not recognised: 3\n", english, StringComparison.Ordinal);

        // What the journal said, and the figures and labels it names, are not touched.
        foreach (var line in new[]
        {
            "- Backend · codex-cli/gpt-5: 2",
            "- Cancelled: 1",
            "- P-0002 · 09:09:47 · Failed · Backend",
            "- \\#fase-4: 3",
            "- Hecho: ADR-0023 revisado; faltaba el ejemplo del fallo cerrado.",
            "- Hecho \\(petición «memoria»\\): la memoria ya exporta su Markdown con \\`\\<b\\>\\` y \\#títulos escapados.",
        })
        {
            Assert.Contains("\n" + line + "\n", english, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("[Text_", english, StringComparison.Ordinal);
        Assert.Equal(Expected.Count(c => c == '\n'), english.Count(c => c == '\n'));
    }

    [Theory]
    [InlineData(true, "# Summary of 2026-10-16 (provisional)")]
    [InlineData(false, "# Summary of 2026-10-16")]
    public void The_summary_of_a_day_without_a_journal_and_the_provisional_one_are_said_in_English(bool provisional, string title)
    {
        var text = DailySummaryBuilder.Build(new DateOnly(2026, 10, 16), provisional, null).Text;

        var english = English.RenderNested(DailySummaryTexts.Coded(text));

        Assert.Equal(title + "\n\nDeterministic summary of the journal of the day. It only counts and quotes, it does not interpret.\n\nThere is no journal for this day.\n", english);
    }

    /// <summary>The summary of the day with more failures and «Hecho» lines than it lists says how many more are in the journal.</summary>
    [Fact]
    public void The_lines_that_say_how_many_more_there_are_are_said_in_English()
    {
        var english = English.RenderNested(DailySummaryTexts.Coded("- … y 4 más.\n- … y 7 líneas «Hecho» más en la bitácora.\n- Ninguna.\n- Entrada 1.200 → salida 340 (exactos)\n- Sin prompts del producto: el hook de desarrollo no registra tokens."));

        Assert.Equal(
            "- … and 4 more.\n- … and 7 more “Done” lines in the journal.\n- None.\n- Input 1,200 → output 340 (exact)\n- No Agent Studio requests: development requests do not record tokens.",
            english);
    }

    /// <summary>A text that is not one the builder wrote is never lost: every line goes through as it is.</summary>
    [Theory]
    [InlineData("Resumen")]
    [InlineData("## Prompts\n- 12 de user")]
    [InlineData("## Prompts: muchos")]
    [InlineData("- Duración total: 12,35 s")]
    [InlineData("línea con {0} y {llaves}")]
    [InlineData("")]
    public void A_text_that_is_not_a_summary_is_shown_as_it_is(string text)
    {
        Assert.Equal(text, Spanish.RenderNested(DailySummaryTexts.Coded(text)));
        Assert.Equal(text, English.RenderNested(DailySummaryTexts.Coded(text)));
    }

    [Fact]
    public void The_topic_of_the_summary_of_a_day_is_coded_only_when_it_is_exactly_that()
    {
        var code = DailySummaryTexts.TryTopic("Resumen del 2026-10-15");

        Assert.NotNull(code);
        Assert.Equal("Resumen del 2026-10-15", Spanish.RenderNested(code));
        Assert.Equal("Summary of 2026-10-15", English.RenderNested(code));
        Assert.Null(DailySummaryTexts.TryTopic("Resumen del 15/10/2026"));
        Assert.Null(DailySummaryTexts.TryTopic("Resumen del 2026-10-15 "));
        Assert.Null(DailySummaryTexts.TryTopic("Otro"));
    }

    /// <summary>The entry of a closed day, as the memory lists it, is coded in the same way: the list says it in the language in use.</summary>
    [Fact]
    public void The_entry_of_a_closed_day_is_coded_by_the_derivation_from_its_own_text()
    {
        var entry = new MemoryEntry("m1", MemoryKind.DailySummary, "Resumen del 2026-03-10", Expected, MemoryOrigin.System, "bitacora/2026-03-10.md#sha256:aa", DateTimeOffset.UnixEpoch, new string('a', 64), MemoryTrust.Trusted, null, null);
        var shown = new AgentStudio.Desktop.Memory.MemoryEntryViewModel(
            entry,
            TimeZoneInfo.Utc,
            coded: new MemoryEntryText(DailySummaryTexts.TryTopic(entry.Topic), DailySummaryTexts.Coded(entry.Content)),
            texts: English);

        Assert.Equal("Summary of 2026-03-10", shown.Topic);
        Assert.StartsWith("# Summary of 2026-03-10", shown.Content, StringComparison.Ordinal);
        Assert.Equal(Expected, shown.Entry.Content);
    }
}
