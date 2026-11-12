using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Memory.MemorySearchSamples;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The search of «Memoria» in Phase 12 (docs/17 D-05 and D-06, F12-6; ADR-0047) over fake ports: with words and the index,
/// a search by meaning and by words of every trust, one card per entry with its origin, its trust and why it came out, and
/// the texts of the log read-only; by words only when the model is not there, and the document says so; without the index,
/// or without words, the memory as before.
/// </summary>
public sealed class MemorySearchViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly ProjectContext Project = new("p-1", Root);

    private static (MemoryViewModel Memory, FakeProjectMemory Store, FakeSemanticIndex Index) Create(FakeSemanticIndex? index = null, params MemoryEntry[] entries) =>
        Create(index, null, null, entries);

    private static (MemoryViewModel Memory, FakeProjectMemory Store, FakeSemanticIndex Index) Create(
        FakeSemanticIndex? index, FakeJournalTrust? trust, FakeModelInstaller? installer, params MemoryEntry[] entries)
    {
        var store = new FakeProjectMemory();
        store.Entries.AddRange(entries);
        index ??= new FakeSemanticIndex();
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            store,
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(index, trust, installer));
        return (memory, store, index);
    }

    /// <summary>The recalls of the journal entries (the panel of the journal), as opposed to those of the list.</summary>
    private static int JournalRecalls(FakeProjectMemory store) => store.Recalls.Count(r => r.Query.Kinds is [MemoryKind.JournalEntry] && r.Query.MaxEntries == JournalTrustViewModel.JournalEntriesRead);

    [Fact]
    public async Task With_the_model_and_its_vectors_the_box_searches_by_meaning_and_by_words()
    {
        var (memory, _, _) = Create();

        await memory.SetProjectAsync(Root);

        Assert.Equal("Buscar por significado y por palabras", memory.SearchLabel);
        Assert.Equal("Buscar en la memoria por significado y por palabras", memory.SearchAutomationName);
    }

    [Fact]
    public async Task Without_the_model_or_without_vectors_the_box_searches_by_words()
    {
        var (missing, _, _) = Create(new FakeSemanticIndex { Status = Missing() });
        var (pending, _, _) = Create(new FakeSemanticIndex { Status = Ready(withVectors: 0) });
        var withoutIndex = new MemoryViewModel(new FakeProjectWorkspace(), new FakeProjectMemory(), null, null, new DailySummaryViewModel(null, new FakeClock(At)), new FakeClock(At));

        await missing.SetProjectAsync(Root);
        await pending.SetProjectAsync(Root);
        await withoutIndex.SetProjectAsync(Root);

        Assert.All(new[] { missing, pending, withoutIndex }, m =>
        {
            Assert.Equal("Buscar por palabras", m.SearchLabel);
            Assert.Equal("Buscar en la memoria por palabras", m.SearchAutomationName);
        });
    }

    [Fact]
    public async Task A_search_with_words_asks_the_index_for_every_trust_with_the_log_only_for_every_category()
    {
        var (memory, store, index) = Create();
        await memory.SetProjectAsync(Root);
        var recalls = store.Recalls.Count;

        memory.SearchText = "  pruebas que fallan  ";
        await memory.SearchCommand.ExecuteAsync(null);
        memory.SelectedKind = memory.Kinds.Single(k => k.Value == MemoryKind.KnownError);
        await memory.SearchCommand.ExecuteAsync(null);

        var (project, all) = index.Searches[0];
        Assert.Equal(Project, project);
        Assert.Equal(("pruebas que fallan", MemoryViewModel.MaxHits, false, true), (all.Text, all.Top, all.TrustedOnly, all.IncludeLog));
        Assert.Null(all.Kinds);
        var known = index.Searches[1].Query;
        Assert.Equal([MemoryKind.KnownError], known.Kinds);
        Assert.False(known.IncludeLog);
        Assert.False(known.TrustedOnly);
        Assert.Equal(recalls, store.Recalls.Count); // the list of the memory was not asked: the index answered.
    }

    [Fact]
    public async Task Each_result_keeps_its_origin_and_trust_and_says_why_it_came_out()
    {
        var trusted = Entry("m-1", MemoryTrust.Trusted);
        var proposed = Entry("m-2", MemoryTrust.Proposed, MemoryKind.KnownError, MemoryOrigin.Agent);
        var journal = JournalEntry("j-1", new DateOnly(2026, 10, 28), MemoryTrust.Quarantined);
        var index = new FakeSemanticIndex
        {
            Answer = _ => new SemanticSearchResult(
            [
                Hit(trusted, 1, 2),
                LogHit(57, "registro · veredicto", "Veredicto: R1 cumplido; la suite queda en verde.", null, 1),
                Hit(proposed, 2, null),
                Hit(journal, null, 3),
            ],
            SemanticSearchMode.Hybrid),
        };
        var (memory, _, _) = Create(index);
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas en rojo";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["m-1", "log:57#0", "m-2", "j-1"], memory.Results.Select(r => r is MemoryEntryViewModel e ? e.Id : ((LogFragmentViewModel)r).Id));
        Assert.Equal(["m-1", "m-2", "j-1"], memory.Entries.Select(e => e.Id));
        Assert.Equal(
            ["Coincide por palabras y por significado", "Coincide por palabras", "Coincide por significado"],
            memory.Entries.Select(e => e.Match));
        Assert.Equal(["Sistema", "Agente", "Autor sin comprobar"], memory.Entries.Select(e => e.OriginLabel));
        Assert.Equal(
            [
                "De confianza",
                "Propuesta de un agente: sin revisar",
                "En cuarentena: no se puede comprobar quién la escribió. Acéptala, o confía en su día o en toda la bitácora",
            ],
            memory.Entries.Select(e => e.TrustLabel));
        var log = Assert.Single(memory.Results.OfType<LogFragmentViewModel>());
        Assert.Equal(("Registro · veredicto", "Sistema", Tone.Success), (log.KindLabel, log.OriginLabel, log.TrustTone));
        Assert.Equal("Registro de decisiones, entrada 57", log.Source);
        Assert.Equal("Coincide por significado", log.Match);
        Assert.Equal("Por significado y por palabras: 4 resultados, del más cercano al menos.", memory.SearchNotice);
        Assert.Null(memory.StatusMessage);
    }

    [Fact]
    public async Task An_entry_with_several_fragments_is_one_card_ranked_by_its_best_fragment()
    {
        var split = Entry("m-7", MemoryTrust.Trusted, content: "Un texto largo partido en dos fragmentos.");
        var index = new FakeSemanticIndex
        {
            Answer = _ => new SemanticSearchResult([Hit(split, null, 1, part: 1), Hit(Entry("m-8", MemoryTrust.Trusted), 1, null), Hit(split, 2, null, part: 0)], SemanticSearchMode.Hybrid),
        };
        var (memory, _, _) = Create(index);
        await memory.SetProjectAsync(Root);

        memory.SearchText = "fragmentos";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["m-7", "m-8"], memory.Entries.Select(e => e.Id));
        Assert.Equal("Coincide por significado", memory.Entries[0].Match);
    }

    [Fact]
    public async Task Without_the_model_the_search_goes_by_words_and_says_why()
    {
        var index = new FakeSemanticIndex
        {
            Status = Missing(),
            Answer = _ => new SemanticSearchResult([Hit(Entry("m-1", MemoryTrust.Trusted), 1, null)], SemanticSearchMode.WordsOnly, WordsOnlyReason.ModelMissing),
        };
        var (memory, _, _) = Create(index);
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Solo por palabras: el modelo de búsqueda por significado no está descargado.", memory.SearchNotice);
        Assert.Equal("Coincide por palabras", Assert.Single(memory.Entries).Match);
    }

    [Fact]
    public async Task Without_words_the_memory_is_listed_as_before_and_the_index_is_not_asked()
    {
        var (memory, store, index) = Create(null, Entry("m-1", MemoryTrust.Trusted), Entry("m-2", MemoryTrust.Proposed));

        await memory.SetProjectAsync(Root);

        Assert.Empty(index.Searches);
        Assert.Null(store.Recalls[^1].Query.Text);
        Assert.Equal(["m-1", "m-2"], memory.Entries.Select(e => e.Id));
        Assert.Equal(memory.Entries, memory.Results.Cast<MemoryEntryViewModel>());
        Assert.All(memory.Entries, e => Assert.Null(e.Match));
        Assert.Null(memory.SearchNotice);
    }

    [Fact]
    public async Task While_the_index_of_the_project_has_no_fragments_the_memory_is_searched_by_words_and_says_so()
    {
        var (memory, store, index) = Create(new FakeSemanticIndex { Status = Ready(fragments: 0, withVectors: 0) }, Entry("m-1", MemoryTrust.Trusted));
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Empty(index.Searches);
        Assert.Equal("pruebas", store.Recalls[^1].Query.Text);
        Assert.Equal(MemorySearchTexts.EmptyIndex, memory.SearchNotice);
        Assert.Equal(["m-1"], memory.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Without_the_index_composed_the_memory_is_searched_by_words_and_says_so()
    {
        var store = new FakeProjectMemory();
        store.Entries.Add(Entry("m-1", MemoryTrust.Trusted));
        var memory = new MemoryViewModel(new FakeProjectWorkspace(), store, null, null, new DailySummaryViewModel(null, new FakeClock(At)), new FakeClock(At));
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Solo por palabras: la búsqueda por significado no está disponible aquí.", memory.SearchNotice);
        Assert.Equal("pruebas", store.Recalls[^1].Query.Text);
        Assert.False(memory.Model.IsIndexAvailable);
        Assert.Equal("La búsqueda por significado no está disponible aquí: la memoria se busca por palabras.", memory.Model.StatusText);
    }

    [Fact]
    public async Task A_search_of_the_index_that_fails_falls_back_to_the_words_of_the_memory_and_says_so()
    {
        var (memory, store, _) = Create(new FakeSemanticIndex { SearchFailure = new InvalidOperationException("El índice está ocupado.") }, Entry("m-1", MemoryTrust.Trusted));
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Solo por palabras: la búsqueda en el índice falló (El índice está ocupado).", memory.SearchNotice);
        Assert.Equal("pruebas", store.Recalls[^1].Query.Text);
        Assert.Equal(["m-1"], memory.Entries.Select(e => e.Id));
        Assert.Null(memory.ErrorMessage);
    }

    [Fact]
    public async Task When_the_state_of_the_index_cannot_be_read_the_panel_says_why_and_the_search_goes_by_words()
    {
        var (memory, store, index) = Create(new FakeSemanticIndex { StatusFailure = new IOException("semantic.db está bloqueado.") }, Entry("m-1", MemoryTrust.Trusted));
        await memory.SetProjectAsync(Root);

        memory.SearchText = "pruebas";
        await memory.SearchCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo leer el estado del índice de búsqueda: semantic.db está bloqueado.", memory.Model.StatusText);
        Assert.Equal(Tone.Error, memory.Model.StatusTone);
        Assert.Equal(MemorySearchTexts.UnreadableIndex, memory.SearchNotice);
        Assert.Empty(index.Searches);
        Assert.Equal("pruebas", store.Recalls[^1].Query.Text);
    }

    [Fact]
    public async Task A_result_waiting_for_the_person_can_be_accepted_and_the_search_runs_again()
    {
        var proposed = Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent);
        var index = new FakeSemanticIndex { Answer = _ => new SemanticSearchResult([Hit(proposed, 1, 1)], SemanticSearchMode.Hybrid) };
        var (memory, store, _) = Create(index, proposed);
        await memory.SetProjectAsync(Root);
        memory.SearchText = "worktrees";
        await memory.SearchCommand.ExecuteAsync(null);

        await memory.AcceptCommand.ExecuteAsync(memory.Entries[0]);

        Assert.Equal((Project, "m-2", MemoryReview.Accepted, "user"), (store.Reviews[0].Project, store.Reviews[0].Entry, store.Reviews[0].Decision, store.Reviews[0].DecidedBy));
        Assert.Equal(2, index.Searches.Count);
    }

    /// <summary>
    /// Reading the state of the index checks the files of the model (their SHA-256, some 123 MB) and the journal panel reads
    /// thousands of entries: neither is read on a search or on a review of an entry that is not of the journal.
    /// </summary>
    [Fact]
    public async Task A_search_or_a_review_reads_neither_the_state_of_the_index_nor_the_journal_again()
    {
        var proposed = Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent);
        var trust = new FakeJournalTrust { State = new JournalTrustState(false, [], 0) };
        var index = new FakeSemanticIndex { Answer = _ => new SemanticSearchResult([Hit(proposed, 1, 1)], SemanticSearchMode.Hybrid) };
        var (memory, store, _) = Create(index, trust, null, proposed);
        await memory.SetProjectAsync(Root);
        Assert.Equal((1, 1, 1), (index.StatusReads, trust.Reads, JournalRecalls(store)));

        memory.SearchText = "worktrees";
        await memory.SearchCommand.ExecuteAsync(null);
        await memory.SearchCommand.ExecuteAsync(null);
        await memory.AcceptCommand.ExecuteAsync(memory.Entries[0]);

        Assert.Equal(3, index.Searches.Count);
        Assert.Equal((1, 1, 1), (index.StatusReads, trust.Reads, JournalRecalls(store)));
    }

    [Fact]
    public async Task The_state_of_the_index_is_read_when_the_project_opens_after_a_download_and_after_a_refresh_of_the_index()
    {
        var installer = new FakeModelInstaller();
        var (memory, _, index) = Create(new FakeSemanticIndex { Status = Missing() }, null, installer);
        await memory.SetProjectAsync(Root);
        Assert.Equal(1, index.StatusReads);
        Assert.Equal("Buscar por palabras", memory.SearchLabel);

        index.Status = Ready(fragments: 120, withVectors: 0);
        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Equal(2, index.StatusReads);
        Assert.Equal("Buscar por palabras", memory.SearchLabel); // the model is there, the vectors not yet
        Assert.Equal(EmbeddingModelViewModel.DownloadedText, memory.Model.Outcome);

        index.Status = Ready();
        await memory.IndexRefreshedAsync();

        Assert.Equal(3, index.StatusReads);
        Assert.Equal("Buscar por significado y por palabras", memory.SearchLabel);
        Assert.Equal(Tone.Success, memory.Model.StatusTone);
        Assert.Null(memory.Model.Outcome); // V42: «hasta entonces, la búsqueda va por palabras» is no longer said
    }

    [Fact]
    public async Task A_review_of_a_journal_entry_reads_the_journal_panel_again()
    {
        var journal = JournalEntry("j-1", new DateOnly(2026, 10, 28), MemoryTrust.Quarantined);
        var trust = new FakeJournalTrust { State = new JournalTrustState(false, [], 1) };
        var (memory, store, index) = Create(new FakeSemanticIndex(), trust, null, journal);
        await memory.SetProjectAsync(Root);

        await memory.AcceptCommand.ExecuteAsync(memory.Entries.Single(e => e.Id == "j-1"));

        Assert.Equal(MemoryReview.Accepted, Assert.Single(store.Reviews).Decision);
        Assert.Equal((2, 2), (trust.Reads, JournalRecalls(store)));
        Assert.Equal(1, index.StatusReads);
    }

    /// <summary>
    /// A decision that waited in the queue may end in the middle of a search: the refreshes never interleave. What arrives
    /// while one runs is served by one more refresh after it, merged: a decision about the journal and a review that both
    /// end during a slow search cost one more refresh, not two.
    /// </summary>
    [Fact]
    public async Task Refreshes_never_interleave_and_what_arrives_during_one_is_served_by_one_more()
    {
        var proposed = Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent);
        var trust = new FakeJournalTrust { State = new JournalTrustState(false, [], 0) };
        var index = new FakeSemanticIndex { Answer = _ => new SemanticSearchResult([Hit(proposed, 1, 1)], SemanticSearchMode.Hybrid) };
        var (memory, store, _) = Create(index, trust, null, proposed);
        await memory.SetProjectAsync(Root);
        memory.SearchText = "worktrees";
        await memory.SearchCommand.ExecuteAsync(null);
        var entry = memory.Entries[0];
        var journalReads = trust.Reads;
        index.HoldSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var search = memory.SearchCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => index.Running == 1);
        var decision = memory.Journal.ToggleProjectCommand.ExecuteAsync(null);
        var review = memory.AcceptCommand.ExecuteAsync(entry);

        // Both are recorded, and both wait: nothing of their refresh runs while the search does.
        await WaitUntilAsync(() => trust.Decisions.Count == 1 && store.Reviews.Count == 1);
        Assert.Equal(journalReads, trust.Reads);
        Assert.Equal(2, index.Searches.Count);
        Assert.False(search.IsCompleted);

        index.HoldSearch.SetResult();
        await Task.WhenAll(search, decision, review).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(3, index.Searches.Count); // the slow search, then one more for the decision and the review together
        Assert.Equal(1, index.MostAtOnce);
        Assert.Equal(journalReads + 1, trust.Reads);
        Assert.False(memory.IsBusy);
    }

    [Fact]
    public async Task A_download_that_ends_with_the_model_ready_raises_ModelInstalled_once_with_the_open_project()
    {
        var installer = new FakeModelInstaller();
        var (memory, _, _) = Create(new FakeSemanticIndex { Status = Missing() }, null, installer);
        var installed = new List<ModelInstalledEventArgs>();
        memory.ModelInstalled += (_, e) => installed.Add(e);
        await memory.SetProjectAsync(Root);

        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);

        var only = Assert.Single(installed);
        Assert.Equal(Project, only.Project);
        Assert.Equal(EmbeddingModelState.Ready, only.Status.State);
    }

    [Fact]
    public async Task A_download_that_fails_does_not_verify_or_is_cancelled_never_raises_ModelInstalled()
    {
        var installer = new FakeModelInstaller();
        var (memory, _, _) = Create(new FakeSemanticIndex { Status = Missing() }, null, installer);
        var installed = 0;
        memory.ModelInstalled += (_, _) => installed++;
        await memory.SetProjectAsync(Root);

        installer.Install = (_, _) => Task.FromException<EmbeddingModelStatus>(new InvalidOperationException("No hay conexión."));
        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);
        installer.Install = (_, _) => Task.FromResult(new EmbeddingModelStatus(EmbeddingModelState.Mismatch, ModelFolder, "No da su SHA-256."));
        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);
        installer.Install = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new EmbeddingModelStatus(EmbeddingModelState.Ready, ModelFolder);
        };
        memory.Model.AskDownloadCommand.Execute(null);
        var cancelled = memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);
        memory.Model.CancelDownloadCommand.Execute(null);
        await cancelled;

        Assert.Equal(3, installer.Consents.Count);
        Assert.Equal("Descarga cancelada: no se ha guardado nada a medias.", memory.Model.Outcome);
        Assert.Equal(0, installed);
    }

    /// <summary>Waits, a bounded time, for what a refresh running on another thread does.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "The condition did not come true in 30 seconds.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Without_a_project_the_panels_ask_for_one()
    {
        var (memory, _, index) = Create();

        await memory.SetProjectAsync(null);

        Assert.Equal(0, index.StatusReads);
        Assert.Equal("Abre una carpeta de proyecto para ver el estado de la búsqueda por significado.", memory.Model.StatusText);
        Assert.False(memory.Model.CanDownload);
    }

    [Fact]
    public void The_items_of_the_lists_are_named_by_their_label()
    {
        var entry = new MemoryEntryViewModel(Entry("m-1", MemoryTrust.Trusted), TimeZoneInfo.Utc);
        var log = new LogFragmentViewModel(LogHit(57, "registro · veredicto", "Veredicto.", 1, null), TimeZoneInfo.Utc);
        var day = new JournalDayViewModel(new DateOnly(2026, 10, 28), 2, false);
        var file = new ModelFileLine("sentencepiece.bpe.model", "5,1 MB", "https://huggingface.co/x");

        Assert.Equal("Decisión · pruebas · De confianza", entry.ToString());
        Assert.Equal("Registro · veredicto · 18/10/2026 07:30", log.ToString());
        Assert.Equal("miércoles 28/10/2026 · 2 entradas en cuarentena", day.ToString());
        Assert.Equal("sentencepiece.bpe.model · 5,1 MB", file.ToString());
    }

    /// <summary>
    /// V40: an entry Agent Studio wrote before it left receipts (its format, «P-NNNN», with no receipt in the log) and one of
    /// the hook are both anchored as <see cref="MemoryOrigin.Agent"/>. The format of a line can be copied, so neither says who
    /// wrote it: both say it cannot be checked, never that someone else did.
    /// </summary>
    [Fact]
    public void A_journal_entry_in_quarantine_says_who_wrote_it_cannot_be_checked_whatever_its_format()
    {
        var day = new DateOnly(2026, 10, 28);
        var product = new MemoryEntryViewModel(
            JournalEntry("j-1", day, MemoryTrust.Quarantined, content: "- **04:22:31** · P-0001 · Orquestador · claude-code/- · #plan · «Planifica Calc»\n  - Resultado P-0001 · 04:22:40 · Succeeded · 9,3 s · tokens no disponibles"),
            TimeZoneInfo.Utc);
        var hook = new MemoryEntryViewModel(
            JournalEntry("j-2", day, MemoryTrust.Quarantined, content: "- **09:06:00** · sesión `eb1c6494` · #prompt · «Revisa el ADR-0023»"),
            TimeZoneInfo.Utc);
        var accepted = new MemoryEntryViewModel(JournalEntry("j-3", day, MemoryTrust.Trusted), TimeZoneInfo.Utc);

        foreach (var entry in new[] { product, hook })
        {
            Assert.Equal("En cuarentena: no se puede comprobar quién la escribió. Acéptala, o confía en su día o en toda la bitácora", entry.TrustLabel);
            Assert.Equal("Autor sin comprobar", entry.OriginLabel);
            Assert.Equal(Tone.Warning, entry.TrustTone);
            Assert.True(entry.CanBeAccepted);
        }

        Assert.Equal(("De confianza", "Autor sin comprobar"), (accepted.TrustLabel, accepted.OriginLabel));
        Assert.Equal("Agent Studio", new MemoryEntryViewModel(JournalEntry("j-4", day, MemoryTrust.Trusted, MemoryOrigin.System), TimeZoneInfo.Utc).OriginLabel);
        Assert.All(
            Enum.GetValues<MemoryTrust>().SelectMany(t => Enum.GetValues<MemoryOrigin>().Select(o => (MemoryTexts.Trust(MemoryKind.JournalEntry, o, t), MemoryTexts.Origin(MemoryKind.JournalEntry, o)))),
            texts =>
            {
                Assert.DoesNotContain("no la escribió", texts.Item1, StringComparison.Ordinal);
                Assert.NotEqual("Otro autor", texts.Item2);
            });
    }

    /// <summary>
    /// V41: an entry of the journal is Markdown, and «Memoria» showed it raw («**04:22:31**», «\(intento 1\)»). It reads
    /// clean now, its result as a sub-entry; the anchored text does not change, and any other category is shown as it is.
    /// </summary>
    [Fact]
    public void A_journal_entry_reads_without_the_marks_of_its_markdown_and_keeps_its_anchored_text()
    {
        const string Raw =
            "- **04:22:31** · P-0001 · Orquestador · claude-code/- · #plan · «Planifica Calc \\(intento 1\\) con \\*cuidado\\*» (ejecución 20261105-c4a1f2 · entrada 2026-11-05/P-0001)\n"
            + "  - Resultado P-0001 · 04:22:40 · Succeeded · 9,3 s · tokens no disponibles · ejecución `20261105-c4a1f2` · tarea `-` · intento 1 · entrada `2026-11-05/P-0001`";
        var journal = JournalEntry("j-1", new DateOnly(2026, 11, 5), MemoryTrust.Quarantined, content: Raw);
        var decision = Entry("m-1", MemoryTrust.Trusted, content: @"Ruta C:\(raíz)\*.cs y **negrita** literal.");

        var shown = new MemoryEntryViewModel(journal, TimeZoneInfo.Utc);
        var other = new MemoryEntryViewModel(decision, TimeZoneInfo.Utc);

        Assert.Equal(
            "04:22:31 · P-0001 · Orquestador · claude-code/- · #plan · «Planifica Calc (intento 1) con *cuidado*» (ejecución 20261105-c4a1f2 · entrada 2026-11-05/P-0001)\n"
            + "  • Resultado P-0001 · 04:22:40 · Succeeded · 9,3 s · tokens no disponibles · ejecución 20261105-c4a1f2 · tarea - · intento 1 · entrada 2026-11-05/P-0001",
            shown.Content);
        Assert.Equal(Raw, shown.Entry.Content);
        Assert.Equal(decision.Content, other.Content);
    }

    [Fact]
    public void The_day_of_a_journal_entry_comes_from_its_source()
    {
        Assert.Equal(new DateOnly(2026, 10, 28), JournalSources.Day(MemorySources.Journal(new DateOnly(2026, 10, 28), new string('e', 64))));
        Assert.Null(JournalSources.Day("registro:20261018-c4a1f2#42"));
        Assert.Null(JournalSources.Day("bitacora:28-10-2026#sha256:x"));
        Assert.Null(JournalSources.Day(null));
    }
}
