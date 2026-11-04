using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Semantic;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The identifiers of a search (ADR-0047, second addendum of 2026-11-04): which it recognises, as the search wrote them, and
/// when a fragment contains one exactly, in its text or in its data.
/// </summary>
public sealed class SearchIdentifiersTests
{
    [Theory]
    [InlineData("¿Qué se pidió en la P-0113?", SearchIdentifierKind.Prompt, "P-0113")]
    [InlineData("¿Qué salió de la ejecución 20261023-b4c5d6?", SearchIdentifierKind.Run, "20261023-b4c5d6")]
    [InlineData("el debate deb-20260310-a1b2c3", SearchIdentifierKind.Run, "deb-20260310-a1b2c3")]
    [InlineData("¿Qué dice la entrada 2026-10-18/P-0108?", SearchIdentifierKind.Entry, "2026-10-18/P-0108")]
    [InlineData("¿Qué revisó el Documentador el 2026-10-29?", SearchIdentifierKind.Date, "2026-10-29")]
    [InlineData("¿Qué probó el Tester el 24/10?", SearchIdentifierKind.Date, "24/10")]
    [InlineData("lo del 29/10/2026", SearchIdentifierKind.Date, "29/10/2026")]
    [InlineData("lo del 29-10-2026", SearchIdentifierKind.Date, "29-10-2026")]
    [InlineData("¿Qué hizo el Backend el 21 de octubre?", SearchIdentifierKind.Date, "21 de octubre")]
    [InlineData("¿sigue vigente mem-0123456789abcdef?", SearchIdentifierKind.Memory, "mem-0123456789abcdef")]
    [InlineData("el fragmento mem:mem-0123456789abcdef#2", SearchIdentifierKind.Memory, "mem:mem-0123456789abcdef#2")]
    [InlineData("¿En qué quedó la T5?", SearchIdentifierKind.Task, "T5")]
    public void A_search_names_what_it_writes_exactly(string text, SearchIdentifierKind kind, string written)
    {
        var identifier = Assert.Single(SearchIdentifiers.In(text));

        Assert.Equal((kind, written), (identifier.Kind, identifier.Text));
        Assert.Equal(kind != SearchIdentifierKind.Task, identifier.OpensTheHead);
    }

    [Theory]
    [InlineData("¿Qué decía el ADR-0016?")]
    [InlineData("la P-12 y la P-12345678901")]
    [InlineData("32/13 y 31/02")]
    [InlineData("el 31 de febrero")]
    [InlineData("XP-0102 y P-0102x")]
    [InlineData("¿por qué el diario se ve con formato?")]
    [InlineData("20261023-b4c5dz")]
    [InlineData("")]
    public void What_only_looks_like_an_identifier_is_none(string text)
    {
        Assert.Empty(SearchIdentifiers.In(text));
    }

    [Theory]
    [InlineData("¿Qué pasó el 3/5?", true)]
    [InlineData("lo del 3/5", true)]
    [InlineData("El día 3/5 se cerró la fase", true)]
    [InlineData("revisar hasta el  3/5", true)]
    [InlineData("3/5", false)]
    [InlineData("Que pasen 3/5 pruebas del lector", false)]
    [InlineData("la fase 2/3 del plan", false)]
    [InlineData("el hotel 3/5", false)]
    [InlineData("el plan 3/5", false)]
    [InlineData("3/5/2026", true)]
    [InlineData("3 de mayo", true)]
    public void A_day_without_its_year_is_one_only_after_el_del_or_dia_or_with_the_name_of_its_month(string text, bool isDay)
    {
        var days = SearchIdentifiers.In(text).Where(i => i.Kind == SearchIdentifierKind.Date).ToList();

        Assert.Equal(isDay ? 1 : 0, days.Count);
        if (isDay)
        {
            Assert.Equal((3, 5), (days[0].Day, days[0].Month));
        }
    }

    [Fact]
    public void A_share_in_an_objective_opens_nothing()
    {
        Assert.DoesNotContain(SearchIdentifiers.In("Implementar el lector de TRX hasta que pasen 3/5 pruebas y la fase 2/3"), i => i.OpensTheHead);
    }

    [Theory]
    [InlineData("el 24/10 de las pruebas fallan")]
    [InlineData("del 3/5 de los tests en rojo")]
    [InlineData("el 3/5 12 pruebas en verde")]
    [InlineData("el 31/2")]
    [InlineData("el 3/13")]
    public void A_share_after_el_is_no_day_when_de_las_de_los_or_a_number_follows_it_and_never_one_that_is_not_in_the_calendar(string text)
    {
        Assert.Empty(Days(text));
    }

    [Theory]
    [InlineData("el 24/10 se cerró la fase")]
    [InlineData("¿qué pasó el 24/10?")]
    [InlineData("el 24/10.")]
    [InlineData("el 24/10 de 2026")]
    public void The_same_day_with_anything_else_after_it_is_still_one(string text)
    {
        Assert.Equal([(10, 24)], Days(text));
    }

    [Theory]
    [InlineData("lo del 29.10.2026", 2026, 10, 29)]
    [InlineData("la entrega del 2026/10/18", 2026, 10, 18)]
    [InlineData("el 20 de oct.", null, 10, 20)]
    [InlineData("el 20 de oct", null, 10, 20)]
    [InlineData("¿qué pasó el 3 de sept. de 2026?", 2026, 9, 3)]
    [InlineData("el 1 de ene", null, 1, 1)]
    [InlineData("hasta el 7 de dic.", null, 12, 7)]
    [InlineData("al 7/5", null, 5, 7)]
    public void The_other_ways_of_writing_a_day_are_days_too(string text, int? year, int month, int day)
    {
        var identifier = Assert.Single(SearchIdentifiers.In(text));

        Assert.Equal(SearchIdentifierKind.Date, identifier.Kind);
        Assert.Equal((year, month, day), (identifier.Year, identifier.Month, identifier.Day));
    }

    [Fact]
    public void Every_day_of_a_list_is_one()
    {
        Assert.Equal([(5, 3), (5, 7)], Days("los días 3/5 y 7/5"));
        Assert.Equal([(5, 3), (5, 7)], Days("del 3/5 al 7/5"));
        Assert.Equal([(5, 3), (5, 4), (5, 7)], Days("el 3/5, 4/5 y 7/5"));
        Assert.Empty(Days("3/5 y 7/5"));
        Assert.Equal([(5, 3)], Days("el 3/5 y 7 pruebas"));
    }

    [Fact]
    public void A_fragment_contains_a_day_written_short_and_the_day_of_its_source_is_what_its_time_is_compared_with()
    {
        var written = Fragment("Decisión: se revisa el 20 de oct. con el equipo.", T0);
        Assert.True(Contains("el 20/10", FoldedWords.Of(written.Text), written));

        // The index gives the day of the source: the time of the fragment says nothing by itself.
        var late = Fragment("Cambio: sin fecha en el texto.", new DateTimeOffset(2026, 11, 5, 23, 30, 0, TimeSpan.Zero));
        var words = FoldedWords.Of(late.Text);
        var sixth = Assert.Single(SearchIdentifiers.In("el 6/11"));
        Assert.True(SearchIdentifiers.Contains(sixth, words, new DateOnly(2026, 11, 6), EntryId, 0));
        Assert.False(SearchIdentifiers.Contains(sixth, words, new DateOnly(2026, 11, 5), EntryId, 0));
        Assert.False(SearchIdentifiers.Contains(sixth, words, null, EntryId, 0));
    }

    [Fact]
    public void Without_its_identifiers_a_text_keeps_what_it_asks_besides_them()
    {
        const string Asked = "¿Qué pasó el 3/5 y el 7/5 en la P-0127?";

        Assert.Equal(["¿Qué", "pasó", "el", "y", "el", "en", "la", "?"], SearchIdentifiers.Without(Asked).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Asked.Length, SearchIdentifiers.Without(Asked).Length);
        Assert.Equal("Dame lo de la", SearchIdentifiers.Without("Dame lo de la P-0127").TrimEnd());
        Assert.Equal("pasan 3/5 pruebas", SearchIdentifiers.Without("pasan 3/5 pruebas"));
        Assert.Equal(string.Empty, SearchIdentifiers.Without(null));
    }

    [Fact]
    public void An_entry_with_its_day_is_one_identifier_and_the_days_keep_their_year_or_any()
    {
        var identifiers = SearchIdentifiers.In("la 2026-10-18/P-0108, el 21 de octubre de 2025 y el 24/10");

        Assert.Equal([SearchIdentifierKind.Entry, SearchIdentifierKind.Date, SearchIdentifierKind.Date], identifiers.Select(i => i.Kind));
        Assert.Equal((2026, 10, 18), (identifiers[0].Year!.Value, identifiers[0].Month, identifiers[0].Day));
        Assert.Equal(["p", "0108"], identifiers[0].Words);
        Assert.Equal((2025, 10, 21), (identifiers[1].Year!.Value, identifiers[1].Month, identifiers[1].Day));
        Assert.Equal(((int?)null, 10, 24), (identifiers[2].Year, identifiers[2].Month, identifiers[2].Day));
        Assert.Single(SearchIdentifiers.In("P-0108 y otra vez P-0108"));
    }

    [Fact]
    public void A_fragment_contains_a_run_a_number_or_a_task_only_as_whole_words()
    {
        var fragment = Fragment("Bitácora del 2026-10-15 · P-0102: - **11:40:22** · P-0102 · Backend · «Implementar» (tarea T2)\n  - Resultado P-0102 · 12:05:10 · Succeeded · ejecución `20261015-a1b2c3` · tarea `T2` · intento 1", T0);
        var words = FoldedWords.Of(fragment.Text);

        Assert.True(Contains("P-0102", words, fragment));
        Assert.False(Contains("P-0010", words, fragment));
        Assert.True(Contains("20261015-a1b2c3", words, fragment));
        Assert.False(Contains("20261015-a1b2c4", words, fragment));
        Assert.True(Contains("T2", words, fragment));
        Assert.False(Contains("T-2", words, fragment));
        Assert.False(Contains("P-0102", FoldedWords.Of("la P-01023 y la XP-0102"), fragment));
    }

    [Fact]
    public void A_fragment_contains_a_day_it_writes_or_the_day_of_its_time_and_an_entry_with_its_day_only_of_that_day()
    {
        var journal = Fragment("Bitácora del 2026-10-15 · P-0102: - **11:40:22** · P-0102 · Backend · «Implementar»", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        var words = FoldedWords.Of(journal.Text);

        Assert.True(Contains("el 15/10", words, journal));
        Assert.True(Contains("el 15 de octubre de 2026", words, journal));
        Assert.True(Contains("2026-10-15", words, journal));
        Assert.False(Contains("2025-10-15", words, journal));
        Assert.True(Contains("el 1 de enero", words, journal));
        Assert.True(Contains("2026-10-15/P-0102", words, journal));
        Assert.False(Contains("2026-10-16/P-0102", words, journal));

        // The day of a run, and a day written out in the content.
        var run = Fragment("Cambio: la ejecución `20261029-f4a5b6` lo dejó listo; se revisa el 3 de noviembre.", T0);
        var runWords = FoldedWords.Of(run.Text);
        Assert.True(Contains("el 29/10", runWords, run));
        Assert.True(Contains("3 de noviembre", runWords, run));
        Assert.False(Contains("el 28/10", runWords, run));

        // A time is not a day, nor a number of the journal before a time: «P-0004 · 10:15» is not the 15th of October.
        var times = Fragment("Resultado P-0004 · 10:15:00 · Failed · 09:10:15", T0);
        Assert.False(Contains("el 15/10", FoldedWords.Of(times.Text), times));
    }

    [Fact]
    public void A_memory_id_is_in_every_fragment_of_its_entry_and_one_with_its_part_only_in_that_one()
    {
        var first = Fragment("Tema: una parte", T0, part: 0);
        var second = Fragment("Tema: otra parte", T0, part: 1);

        Assert.True(Contains("mem-0123456789abcdef", FoldedWords.Of(first.Text), first));
        Assert.True(Contains("MEM-0123456789ABCDEF", FoldedWords.Of(second.Text), second));
        Assert.False(Contains("mem:mem-0123456789abcdef#1", FoldedWords.Of(first.Text), first));
        Assert.True(Contains("mem:mem-0123456789abcdef#1", FoldedWords.Of(second.Text), second));
        Assert.False(Contains("mem-fedcba9876543210", FoldedWords.Of(first.Text), first));

        var citing = Fragment("Decisión: sustituye a mem-fedcba9876543210.", T0);
        Assert.True(Contains("mem-fedcba9876543210", FoldedWords.Of(citing.Text), citing));
    }

    private const string EntryId = "mem-0123456789abcdef";

    private static SemanticFragment Fragment(string text, DateTimeOffset at, int part = 0) =>
        new(FragmentSource.Memory(EntryId, HashA), part, text, at);

    /// <summary>Whether <paramref name=fragment/> contains the one identifier of <paramref name=search/>, its time the day of its source.</summary>
    private static bool Contains(string search, IReadOnlyList<string> words, SemanticFragment fragment) =>
        SearchIdentifiers.Contains(Assert.Single(SearchIdentifiers.In(search)), words, DateOnly.FromDateTime(fragment.At.UtcDateTime), EntryId, fragment.Part);

    private static (int Month, int Day)[] Days(string text) =>
        [.. SearchIdentifiers.In(text).Where(i => i.Kind == SearchIdentifierKind.Date).Select(i => (i.Month, i.Day))];
}
