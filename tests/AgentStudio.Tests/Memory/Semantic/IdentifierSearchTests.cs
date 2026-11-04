using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Memory.Semantic;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// What the index does with the metadata of a search (ADR-0047, second addendum of 2026-11-04): the fragments that contain
/// exactly one of its identifiers go first, the words of the template of the journal do not search, the vector of an entry of
/// the journal carries the version of its view, and a model without a cut says so.
/// </summary>
public sealed class IdentifierSearchTests
{
    private static readonly DateTimeOffset October15 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An entry of the journal as the product writes it, anchored at <paramref name="at"/> (the clock goes forward only, so
    /// the entries of a test are written in order).
    /// </summary>
    private static async Task<MemoryEntry> JournalAsync(IndexWorld world, DateTimeOffset at, int number, string summary, string run, string task = "T1", MemoryOrigin origin = MemoryOrigin.System)
    {
        world.World.Clock.SetUtcNow(at.AddMinutes(-1));
        var p = "P-" + number.ToString("D4", CultureInfo.InvariantCulture);
        var day = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return await world.RecordAsync(
            MemoryKind.JournalEntry,
            $"Bitácora del {day} · {p}",
            string.Create(CultureInfo.InvariantCulture, $"- **{at:HH:mm:ss}** · {p} · Backend · codex-cli/gpt-5 · #prompt · «{summary}» (tarea {task})\n  - Resultado {p} · {at.AddMinutes(5):HH:mm:ss} · Succeeded · 300,0 s · tokens no disponibles · ejecución `{run}` · tarea `{task}` · intento 1"),
            origin);
    }

    /// <summary>Six known errors close to «el lector de pruebas» in words and in meaning: in both lists, they lead any fusion.</summary>
    private static async Task ReadersAsync(IndexWorld world)
    {
        for (var i = 0; i < 6; i++)
        {
            await world.RecordAsync(MemoryKind.KnownError, $"Lector de pruebas {i}", $"El lector de pruebas {i} pasó por alto las pruebas omitidas.");
        }
    }

    private static Task<SemanticSearchResult> SearchAsync(SemanticIndex index, IndexWorld world, string text, IReadOnlyList<MemoryKind>? kinds = null) =>
        index.SearchAsync(world.Project, new SemanticQuery(text) { Kinds = kinds }, Ct);

    [Fact]
    public async Task The_entry_with_the_number_of_the_search_goes_first_where_the_fusion_would_leave_it_out()
    {
        using var world = await IndexWorld.CreateAsync();
        var planning = await JournalAsync(world, October15, 101, "Planificar la importación de requisitos", "20261015-a1b2c3");
        await ReadersAsync(world);
        await world.RefreshAsync();
        const string Question = "¿Qué pasó con el lector de pruebas en la P-0101?";

        // Its number is its only word in common with the search, and its meaning is far from it: by the fusion alone it is
        // behind the six known errors, which both lists bring.
        var fused = await SearchAsync(world.With(TestModels.Tuning with { MaxIdentifierHits = 0 }), world, Question);
        Assert.DoesNotContain(fused.Hits.Take(5), h => h.Entry!.Id == planning.Id);
        Assert.All(fused.Hits, h => Assert.Null(h.Identifier));

        var result = await world.SearchAsync(Question);

        Assert.Equal(SemanticSearchMode.Hybrid, result.Mode);
        Assert.Equal(planning.Id, result.Hits[0].Entry!.Id);
        Assert.Equal("P-0101", result.Hits[0].Identifier);
        Assert.Null(result.Hits[0].Rank.SemanticRank);
        Assert.All(result.Hits.Skip(1), h => Assert.Null(h.Identifier));
        Assert.Equal(result.Hits.Count, result.Hits.Select(h => h.Entry!.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_entries_of_a_run_go_first_in_the_order_of_their_words()
    {
        using var world = await IndexWorld.CreateAsync();
        var planning = await JournalAsync(world, October15, 101, "Planificar el lector de informes", "20261015-a1b2c3");
        var fixing = await JournalAsync(world, October15.AddHours(1), 102, "Arreglar el lector de informes", "20261015-a1b2c3", "T2");
        var other = await JournalAsync(world, October15.AddHours(2), 103, "Arreglar el lector de informes", "20261015-ffffff", "T2");
        await ReadersAsync(world);
        await world.RefreshAsync();

        var byWords = await world.SearchAsync("¿Qué se hizo para planificar en la ejecución 20261015-a1b2c3?");
        var fixingFirst = await world.SearchAsync("¿Qué se tuvo que arreglar en la ejecución 20261015-a1b2c3?");

        Assert.Equal([planning.Id, fixing.Id], byWords.Hits.Take(2).Select(h => h.Entry!.Id));
        Assert.Equal([fixing.Id, planning.Id], fixingFirst.Hits.Take(2).Select(h => h.Entry!.Id));
        Assert.All(byWords.Hits.Take(2), h => Assert.Equal("20261015-a1b2c3", h.Identifier));
        Assert.DoesNotContain(fixingFirst.Hits, h => h.Entry!.Id == other.Id && h.Identifier is not null);
    }

    [Fact]
    public async Task A_day_brings_first_what_writes_it_and_what_was_anchored_that_day_at_most_three()
    {
        using var world = await IndexWorld.CreateAsync();
        var first = await JournalAsync(world, October15, 101, "Planificar la importación", "20261015-a1b2c3");
        var second = await JournalAsync(world, October15.AddHours(1), 102, "Implementar la importación", "20261015-a1b2c3", "T2");
        var third = await JournalAsync(world, October15.AddHours(2), 103, "Probar la importación", "20261015-a1b2c3", "T3");
        world.World.Clock.SetUtcNow(October15.AddHours(3));
        var decision = await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        var nextDay = await JournalAsync(world, October15.AddDays(1), 104, "Desplegar la importación", "20261016-b2c3d4");
        await world.RefreshAsync();

        var result = await world.SearchAsync("¿Qué se decidió el 15/10 sobre el despliegue?");

        // The decision of that day has the word of the search: it goes first; then the newest of the day, three in all.
        Assert.Equal([decision.Id, third.Id, second.Id], result.Hits.Take(3).Select(h => h.Entry!.Id));
        Assert.All(result.Hits.Take(3), h => Assert.Equal("15/10", h.Identifier));
        Assert.All(result.Hits.Skip(3), h => Assert.Null(h.Identifier));
        Assert.DoesNotContain(result.Hits.Take(3), h => h.Entry!.Id == first.Id || h.Entry!.Id == nextDay.Id);

        var once = await world.With(TestModels.Tuning with { MaxIdentifierHits = 1 }).SearchAsync(world.Project, new SemanticQuery("¿Qué se decidió el 15 de octubre sobre el despliegue?"), Ct);
        Assert.Equal(decision.Id, once.Hits[0].Entry!.Id);
        Assert.Single(once.Hits, h => h.Identifier is not null);
    }

    [Fact]
    public async Task A_number_with_its_day_is_only_the_entry_of_that_day_and_the_number_alone_is_any_day()
    {
        using var world = await IndexWorld.CreateAsync();
        var fifteenth = await JournalAsync(world, October15, 101, "Planificar la importación", "20261015-a1b2c3");
        var sixteenth = await JournalAsync(world, October15.AddDays(1), 101, "Planificar la exportación", "20261016-b2c3d4");
        await ReadersAsync(world);
        await world.RefreshAsync();

        var entry = await world.SearchAsync("¿Qué dice la entrada 2026-10-16/P-0101?");
        var number = await world.SearchAsync("¿Qué dice la P-0101?");

        Assert.Equal(sixteenth.Id, entry.Hits[0].Entry!.Id);
        Assert.Single(entry.Hits, h => h.Identifier is not null);
        Assert.Equal([sixteenth.Id, fifteenth.Id], number.Hits.Take(2).Select(h => h.Entry!.Id));
    }

    [Fact]
    public async Task A_task_alone_opens_nothing_and_with_a_day_it_orders_what_the_day_found()
    {
        using var world = await IndexWorld.CreateAsync();
        var plan = await JournalAsync(world, October15, 101, "Planificar la importación", "20261015-a1b2c3", "T1");
        var task = await JournalAsync(world, October15.AddHours(1), 102, "Implementar la importación", "20261015-a1b2c3", "T2");
        await JournalAsync(world, October15.AddDays(1), 103, "Implementar la exportación", "20261016-b2c3d4", "T2");
        await world.RefreshAsync();

        var alone = await world.SearchAsync("¿Qué pasó con la T2?");
        var withDay = await world.SearchAsync("¿Qué pasó con la T2 del 15/10?");

        Assert.All(alone.Hits, h => Assert.Null(h.Identifier));
        Assert.Equal([task.Id, plan.Id], withDay.Hits.Take(2).Select(h => h.Entry!.Id));
        Assert.Equal("15/10", withDay.Hits[0].Identifier);
    }

    [Fact]
    public async Task A_memory_id_brings_its_entry_first_by_its_data_and_only_what_the_search_admits()
    {
        using var world = await IndexWorld.CreateAsync();
        var decision = await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        var proposal = await world.RecordAsync(MemoryKind.Decision, "Base de datos", "Propuesta: cambiar a Postgres.", MemoryOrigin.Agent);
        await ReadersAsync(world);
        await world.RefreshAsync();

        var result = await world.SearchAsync($"¿Sigue vigente {decision.Id}? ¿y el lector de pruebas?");
        var proposed = await world.SearchAsync($"¿Sigue vigente {proposal.Id}?");
        var otherKind = await world.SearchAsync($"¿Sigue vigente {decision.Id}?", kinds: [MemoryKind.KnownError]);

        Assert.Equal(decision.Id, result.Hits[0].Entry!.Id);
        Assert.Equal(decision.Id, result.Hits[0].Identifier);
        Assert.DoesNotContain(proposed.Hits, h => h.Entry!.Id == proposal.Id);
        Assert.DoesNotContain(otherKind.Hits, h => h.Entry!.Id == decision.Id);
    }

    [Fact]
    public async Task Without_the_model_the_identifiers_still_go_first()
    {
        using var world = await IndexWorld.CreateAsync(EmbeddingModelState.Missing);
        var planning = await JournalAsync(world, October15, 101, "Planificar la importación de requisitos", "20261015-a1b2c3");
        await ReadersAsync(world);
        await world.RefreshAsync();

        var result = await world.SearchAsync("¿Qué pasó con el lector de pruebas en la P-0101?");

        Assert.Equal(SemanticSearchMode.WordsOnly, result.Mode);
        Assert.Equal((planning.Id, "P-0101"), (result.Hits[0].Entry!.Id, result.Hits[0].Identifier));
    }

    [Fact]
    public async Task The_day_of_an_entry_of_the_journal_is_the_day_of_its_file_and_not_the_day_it_was_derived()
    {
        using var world = await IndexWorld.CreateAsync();
        world.World.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        const string Content = "- **11:40:22** · P-0102 · Backend · codex-cli/gpt-5 · #prompt · «Implementar el almacén»";
        var old = await world.World.Memory.RecordAsync(
            world.Project,
            new MemoryDraft(MemoryKind.JournalEntry, "Bitácora del 2026-10-15 · P-0102", Content, MemoryOrigin.System, MemorySources.Journal(new DateOnly(2026, 10, 15), AgentStudio.Domain.Common.ContentHash.Sha256Hex(Content))),
            Ct);
        var anchoredToday = await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        await world.RefreshAsync();

        // Derived on the 5th of November, the entry is of the 15th of October, the day of its file.
        Assert.Equal([anchoredToday.Id], (await world.SearchAsync("¿Qué hicimos el 5/11?")).Hits.Where(h => h.Identifier is not null).Select(h => h.Entry!.Id));
        Assert.Equal([old.Id], (await world.SearchAsync("¿Qué hicimos el 15/10?")).Hits.Where(h => h.Identifier is not null).Select(h => h.Entry!.Id));
    }

    [Fact]
    public async Task The_day_of_any_other_entry_is_the_day_it_was_anchored_in_the_time_zone_of_the_person()
    {
        using var world = await IndexWorld.CreateAsync();
        world.World.Clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2"));
        world.World.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 23, 29, 0, TimeSpan.Zero));
        var late = await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        await world.RefreshAsync();

        // 23:30 in UTC is 01:30 of the 6th for the person.
        Assert.Equal([late.Id], (await world.SearchAsync("¿Qué se decidió el 6/11?")).Hits.Where(h => h.Identifier is not null).Select(h => h.Entry!.Id));
        Assert.DoesNotContain((await world.SearchAsync("¿Qué se decidió el 5/11?")).Hits, h => h.Identifier is not null);
    }

    [Fact]
    public async Task A_search_that_asks_nothing_but_its_identifiers_does_not_search_by_meaning()
    {
        using var world = await IndexWorld.CreateAsync(new TestEmbedder(TestModels.Hashing, IndexWorld.Concepts), TestModels.Uncut);
        var planning = await JournalAsync(world, October15, 127, "Planificar el índice semántico", "20261015-a1b2c3");
        await ReadersAsync(world);
        await world.RefreshAsync();
        int Queries() => world.Embedder.Calls.Count(c => c.Role == EmbeddingRole.Query);
        var before = Queries();

        // Without a cut every neighbour by meaning would come: none does, and the question is not even embedded.
        var only = await world.SearchAsync("Dame lo de la P-0127");
        Assert.Equal([planning.Id], only.Hits.Select(h => h.Entry!.Id));
        Assert.Equal(SemanticSearchMode.Hybrid, only.Mode);
        Assert.Equal(before, Queries());

        // With the cut of the model the search still says it has one.
        var withCut = await world.With(TestModels.Tuning).SearchAsync(world.Project, new SemanticQuery("¿Qué salió de la ejecución 20261015-a1b2c3?"), Ct);
        Assert.Equal((new MeaningOnlyCut(0.2f), (string?)null), (withCut.Cut, withCut.CutNotice));
        Assert.Equal(before, Queries());

        // A word to search by brings the meaning back.
        var more = await world.SearchAsync("Dame lo de la P-0127 y el lector de pruebas");
        Assert.Equal(planning.Id, more.Hits[0].Entry!.Id);
        Assert.True(more.Hits.Count > 1, $"{more.Hits.Count} hits");
        Assert.Equal(before + 1, Queries());
    }

    // ---- the words of the template ----

    [Fact]
    public async Task The_lexical_list_leaves_out_the_words_of_the_template_unless_the_search_has_no_other()
    {
        using var world = await IndexWorld.CreateAsync(EmbeddingModelState.Missing);
        for (var i = 0; i < 4; i++)
        {
            await JournalAsync(world, October15.AddHours(i), 101 + i, "Revisar la pantalla " + i, "20261015-a1b2c" + i);
        }

        var invoices = await world.RecordAsync(MemoryKind.KnownError, "Facturas", "El módulo de facturas redondea mal el IVA.");
        await world.RefreshAsync();

        // «ejecución», «tarea», «#prompt» and «codex-cli» are in every entry of the journal: only «facturas» searches.
        Assert.Equal([invoices.Id], await world.FoundAsync("ejecución tarea #prompt codex-cli facturas"));
        Assert.Equal(["facturas"], world.Store.WordSearches[^1].Terms);

        // A search made only of them still searches by them; a word that only looks like one is not one.
        Assert.Equal(4, (await world.FoundAsync("ejecución tarea")).Length);
        Assert.Equal(["tareas", "facturas"], (await SearchTermsAsync(world, "tareas facturas")));
    }

    private static async Task<IReadOnlyList<string>> SearchTermsAsync(IndexWorld world, string text)
    {
        await world.SearchAsync(text);
        return world.Store.WordSearches[^1].Terms;
    }

    // ---- the fingerprints of the vectors ----

    [Fact]
    public async Task A_vector_carries_the_fingerprint_of_its_model_and_one_of_the_journal_also_the_version_of_its_view()
    {
        using var world = await IndexWorld.CreateAsync();
        var decision = await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        var journal = await JournalAsync(world, October15, 101, "Planificar la importación", "20261015-a1b2c3");
        await world.RefreshAsync();

        var states = (await world.Store.ReadStateAsync(world.Project, Ct)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var model = TestModels.Hashing.Fingerprint();
        Assert.Equal(model, states[FragmentOf(decision)].ModelFingerprint);
        Assert.Equal(SemanticIndex.VectorFingerprint(TestModels.Hashing, MemoryKind.JournalEntry), states[FragmentOf(journal)].ModelFingerprint);
        Assert.NotEqual(model, states[FragmentOf(journal)].ModelFingerprint);
        Assert.All(Enum.GetValues<MemoryKind>().Where(k => k != MemoryKind.JournalEntry), k => Assert.Equal(model, SemanticIndex.VectorFingerprint(TestModels.Hashing, k)));
        Assert.Equal((2, 2), ((await world.Index.StatusAsync(world.Project, Ct)).Fragments, (await world.Index.StatusAsync(world.Project, Ct)).WithVectors));

        // A vector of the journal of another version of the view is computed again; nothing else is.
        var held = Assert.Single(await world.Store.ReadIndexedAsync(world.Project, [FragmentOf(journal)], Ct));
        world.Store.Plant(world.Project, held with { ModelFingerprint = model });
        var embedded = world.Embedder.Calls.Count;

        Assert.Equal(new SemanticIndexRefresh(0, 0, 1, 1), await world.RefreshAsync());
        Assert.Equal(["Planificar la importación"], world.Embedder.Calls.Skip(embedded).Single().Texts);

        // The snapshot names the model, and carries the vector of the view of the journal.
        var snapshot = await world.Index.SnapshotAsync(world.Project, 10, Ct);
        Assert.Equal(model, snapshot.ModelFingerprint);
        Assert.All(snapshot.Fragments, f => Assert.NotNull(f.Vector));
    }

    // ---- a model without a cut ----

    [Fact]
    public async Task A_search_and_the_status_say_when_the_model_has_no_cut()
    {
        using var world = await IndexWorld.CreateAsync();
        await world.RecordAsync(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.");
        await world.RefreshAsync();
        var uncut = world.With(TestModels.Uncut);

        var withCut = await world.SearchAsync("pruebas que fallan");
        var without = await uncut.SearchAsync(world.Project, new SemanticQuery("pruebas que fallan"), Ct);

        Assert.Null(withCut.CutNotice);
        Assert.Equal(SemanticSearchTexts.NoCut, without.CutNotice);
        Assert.StartsWith("Sin corte de relevancia para este modelo", SemanticSearchTexts.NoCut, StringComparison.Ordinal);
        Assert.Null((await world.Index.StatusAsync(world.Project, Ct)).CutNotice);
        Assert.Equal(SemanticSearchTexts.NoCut, (await uncut.StatusAsync(world.Project, Ct)).CutNotice);

        world.Model.State = EmbeddingModelState.Missing;
        Assert.Null((await uncut.SearchAsync(world.Project, new SemanticQuery("pruebas que fallan"), Ct)).CutNotice);
        Assert.Null((await uncut.StatusAsync(world.Project, Ct)).CutNotice);
    }

    private static string FragmentOf(MemoryEntry entry) => AgentStudio.Domain.Semantic.SemanticFragment.IdOf(AgentStudio.Domain.Semantic.FragmentSource.Memory(entry.Id, entry.ContentSha256), 0);
}
