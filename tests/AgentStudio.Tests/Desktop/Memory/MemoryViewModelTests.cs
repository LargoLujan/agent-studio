using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The «Memoria» document (docs/14 F4-5, D-18 to D-22) over fake ports: every entry with its category, origin and trust;
/// what a person can decide about each one (accept or reject a proposal or an entry in quarantine, withdraw a trusted
/// one), recorded through <see cref="IProjectMemory.ReviewAsync"/> and queued while a run goes on; and the Markdown
/// export, which is never offered while a run or a planning goes on and says why the exporter refused.
/// </summary>
public sealed class MemoryViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    private static MemoryEntry Entry(string id, MemoryTrust trust, MemoryKind kind = MemoryKind.Decision, MemoryOrigin origin = MemoryOrigin.System, string? supersededBy = null) =>
        new(id, kind, "worktrees", $"Contenido de {id}", origin, "run 20261018-c4a1f2 · entrada 42", At, new string('b', 64), trust, null, supersededBy);

    private static (MemoryViewModel Memory, FakeProjectMemory Store, FakeMemoryExporter Exporter, FakeRunActivity Activity, FakeDailySummaryService Summaries) Create(params MemoryEntry[] entries)
    {
        var store = new FakeProjectMemory();
        store.Entries.AddRange(entries);
        var exporter = new FakeMemoryExporter();
        var activity = new FakeRunActivity();
        var summaries = new FakeDailySummaryService();
        var memory = new MemoryViewModel(new FakeProjectWorkspace(), store, exporter, activity, new DailySummaryViewModel(summaries, new FakeClock(At)), new FakeClock(At));
        return (memory, store, exporter, activity, summaries);
    }

    [Fact]
    public async Task Every_entry_shows_its_category_topic_content_origin_and_trust()
    {
        var (memory, _, _, _, _) = Create(
            Entry("m-1", MemoryTrust.Trusted),
            Entry("m-2", MemoryTrust.Proposed, MemoryKind.PendingTask, MemoryOrigin.Agent),
            Entry("m-3", MemoryTrust.Quarantined, MemoryKind.KnownError),
            Entry("m-4", MemoryTrust.Rejected, MemoryKind.Verdict, MemoryOrigin.Agent),
            Entry("m-5", MemoryTrust.Retracted, MemoryKind.Convention, MemoryOrigin.User, supersededBy: "m-9"));

        await memory.SetProjectAsync(Root);

        Assert.Equal(["Decisión", "Tarea pendiente", "Error conocido", "Veredicto", "Convención"], memory.Entries.Select(e => e.KindLabel));
        Assert.Equal(["Sistema", "Agente", "Sistema", "Agente", "Tú"], memory.Entries.Select(e => e.OriginLabel));
        Assert.Equal(
            [
                "De confianza",
                "Propuesta de un agente: sin revisar",
                "En cuarentena: heredada de un historial aceptado como nuevo origen; revísala",
                "Rechazada",
                "Retirada",
            ],
            memory.Entries.Select(e => e.TrustLabel));
        Assert.Equal([Tone.Success, Tone.Warning, Tone.Warning, Tone.Neutral, Tone.Neutral], memory.Entries.Select(e => e.TrustTone));
        var first = memory.Entries[0];
        Assert.Equal(("worktrees", "Contenido de m-1", "18/10/2026 09:30", "run 20261018-c4a1f2 · entrada 42"), (first.Topic, first.Content, first.Created, first.Source));
        Assert.Equal("Sustituida por m-9", memory.Entries[4].Superseded);
    }

    [Fact]
    public async Task Recall_asks_for_every_trust_with_the_search_and_the_category()
    {
        var (memory, store, _, _, _) = Create();
        await memory.SetProjectAsync(Root);

        memory.SearchText = "  worktree  ";
        memory.SelectedKind = memory.Kinds.Single(k => k.Value == MemoryKind.Decision);
        await memory.RefreshCommand.ExecuteAsync(null);

        var (project, query) = store.Recalls[^1];
        Assert.Equal(new ProjectContext("p-1", Root), project);
        Assert.Equal("worktree", query.Text);
        Assert.Equal([MemoryKind.Decision], query.Kinds);
        Assert.False(query.TrustedOnly);
        Assert.Equal(500, query.MaxEntries);
    }

    [Fact]
    public async Task Without_a_search_or_a_category_every_entry_is_asked()
    {
        var (memory, store, _, _, _) = Create();

        await memory.SetProjectAsync(Root);

        var query = Assert.Single(store.Recalls).Query;
        Assert.Null(query.Text);
        Assert.Null(query.Kinds);
        Assert.False(query.TrustedOnly);
    }

    [Fact]
    public async Task A_proposal_can_be_accepted_or_rejected_and_the_decision_is_recorded_as_the_users()
    {
        var (memory, store, _, _, _) = Create(Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent));
        await memory.SetProjectAsync(Root);
        var proposal = memory.Entries[0];

        Assert.True(memory.AcceptCommand.CanExecute(proposal));
        Assert.True(memory.RejectCommand.CanExecute(proposal));
        Assert.False(memory.RetractCommand.CanExecute(proposal));

        await memory.AcceptCommand.ExecuteAsync(proposal);

        Assert.Equal([(new ProjectContext("p-1", Root), "m-2", MemoryReview.Accepted, "user", (string?)null)], store.Reviews);
        Assert.Equal(2, store.Recalls.Count);
    }

    [Fact]
    public async Task Rejecting_records_the_rejection()
    {
        var (memory, store, _, _, _) = Create(Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent));
        await memory.SetProjectAsync(Root);

        await memory.RejectCommand.ExecuteAsync(memory.Entries[0]);

        Assert.Equal(MemoryReview.Rejected, Assert.Single(store.Reviews).Decision);
    }

    [Fact]
    public async Task An_entry_in_quarantine_can_be_accepted_or_rejected_once_reviewed()
    {
        var (memory, store, _, _, _) = Create(Entry("m-3", MemoryTrust.Quarantined));
        await memory.SetProjectAsync(Root);
        var inherited = memory.Entries[0];

        Assert.True(memory.AcceptCommand.CanExecute(inherited));
        Assert.True(memory.RejectCommand.CanExecute(inherited));
        Assert.False(memory.RetractCommand.CanExecute(inherited));

        await memory.AcceptCommand.ExecuteAsync(inherited);

        Assert.Equal(("m-3", MemoryReview.Accepted), (Assert.Single(store.Reviews).Entry, store.Reviews[0].Decision));
    }

    [Fact]
    public async Task A_trusted_entry_can_only_be_withdrawn()
    {
        var (memory, store, _, _, _) = Create(Entry("m-1", MemoryTrust.Trusted));
        await memory.SetProjectAsync(Root);
        var trusted = memory.Entries[0];

        Assert.False(memory.AcceptCommand.CanExecute(trusted));
        Assert.False(memory.RejectCommand.CanExecute(trusted));
        Assert.True(memory.RetractCommand.CanExecute(trusted));

        await memory.RetractCommand.ExecuteAsync(trusted);

        Assert.Equal(("m-1", MemoryReview.Retracted), (Assert.Single(store.Reviews).Entry, store.Reviews[0].Decision));
    }

    [Fact]
    public async Task Rejected_and_withdrawn_entries_offer_no_decision()
    {
        var (memory, _, _, _, _) = Create(Entry("m-4", MemoryTrust.Rejected), Entry("m-5", MemoryTrust.Retracted));
        await memory.SetProjectAsync(Root);

        Assert.All(memory.Entries, entry =>
        {
            Assert.False(memory.AcceptCommand.CanExecute(entry));
            Assert.False(memory.RejectCommand.CanExecute(entry));
            Assert.False(memory.RetractCommand.CanExecute(entry));
        });
    }

    [Fact]
    public async Task A_review_asked_during_a_run_waits_in_the_queue_and_says_so()
    {
        var (memory, store, _, activity, _) = Create(Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent));
        await memory.SetProjectAsync(Root);
        var proposal = memory.Entries[0];
        activity.Busy = true;
        store.HoldReviews = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var review = memory.AcceptCommand.ExecuteAsync(proposal);

        Assert.Equal("En cola: se registrará cuando termine la ejecución o la planificación en curso.", proposal.Pending);
        Assert.False(memory.AcceptCommand.CanExecute(proposal));
        Assert.False(memory.RejectCommand.CanExecute(proposal));
        Assert.Empty(store.Reviews);

        activity.Busy = false;
        store.HoldReviews.SetResult();
        await review;

        Assert.Single(store.Reviews);
        Assert.Null(memory.Entries[0].Pending);
    }

    [Fact]
    public async Task A_review_that_fails_is_shown_and_not_thrown()
    {
        var (memory, store, _, _, _) = Create(Entry("m-2", MemoryTrust.Proposed, origin: MemoryOrigin.Agent));
        await memory.SetProjectAsync(Root);
        store.ReviewFailure = new InvalidOperationException("El registro de decisiones no verifica.");

        await memory.AcceptCommand.ExecuteAsync(memory.Entries[0]);

        Assert.Equal("No se pudo registrar la revisión: El registro de decisiones no verifica.", memory.ErrorMessage);
        Assert.Null(memory.Entries[0].Pending);
        Assert.True(memory.AcceptCommand.CanExecute(memory.Entries[0]));
    }

    [Fact]
    public async Task The_export_is_not_offered_while_a_run_or_a_planning_goes_on()
    {
        var (memory, _, exporter, activity, _) = Create();
        await memory.SetProjectAsync(Root);
        Assert.True(memory.ExportCommand.CanExecute(null));
        Assert.Null(memory.ExportBlocked);

        activity.Busy = true;
        memory.RefreshRunActivity();

        Assert.False(memory.ExportCommand.CanExecute(null));
        Assert.Equal("Hay una ejecución, una planificación, un debate o una auditoría en curso: la exportación espera a que termine.", memory.ExportBlocked);

        await memory.ExportCommand.ExecuteAsync(null);
        Assert.Empty(exporter.Exports);

        activity.Busy = false;
        memory.RefreshRunActivity();
        Assert.True(memory.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_export_says_what_it_wrote()
    {
        var (memory, _, exporter, _, _) = Create();
        await memory.SetProjectAsync(Root);

        await memory.ExportCommand.ExecuteAsync(null);

        Assert.Equal([new ProjectContext("p-1", Root)], exporter.Exports);
        Assert.Equal("Exportadas 7 entradas a .agentstudio/memory/ (2 ficheros). El Markdown nunca se lee de vuelta.", memory.ExportStatus);
        Assert.Null(memory.ExportWarning);
    }

    [Fact]
    public async Task An_export_the_exporter_refuses_before_the_first_file_says_why()
    {
        var (memory, _, exporter, _, _) = Create();
        exporter.Result = MemoryExportResult.Refused("Hay una ejecución en curso.");
        await memory.SetProjectAsync(Root);

        await memory.ExportCommand.ExecuteAsync(null);

        Assert.Equal("No se exportó: Hay una ejecución en curso.", memory.ExportWarning);
        Assert.Null(memory.ExportStatus);
    }

    /// <summary>
    /// A run that starts while the export writes stops it before the next file (WP3's exporter): what was rewritten is
    /// said, and that the rest stays as it was until the next export.
    /// </summary>
    [Fact]
    public async Task An_export_a_run_stopped_midway_says_which_files_it_rewrote_and_that_the_rest_waits()
    {
        var (memory, _, exporter, _, _) = Create();
        exporter.Result = new MemoryExportResult(
            false,
            ["memory/cronologico-2026-10.md", "memory/tema-decisiones.md"],
            0,
            "Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine.");
        await memory.SetProjectAsync(Root);

        await memory.ExportCommand.ExecuteAsync(null);

        Assert.Equal(
            "Exportación incompleta: Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine. "
            + "Se reescribieron 2 ficheros (memory/cronologico-2026-10.md, memory/tema-decisiones.md) antes de parar; el resto sigue como "
            + "estaba hasta la próxima exportación, que lo reescribe todo.",
            memory.ExportWarning);
        Assert.Null(memory.ExportStatus);
    }

    [Fact]
    public async Task An_export_stopped_after_one_file_says_so_in_the_singular()
    {
        var (memory, _, exporter, _, _) = Create();
        exporter.Result = new MemoryExportResult(false, ["memory/cronologico-2026-10.md"], 0, "Hay una ejecución en curso.");
        await memory.SetProjectAsync(Root);

        await memory.ExportCommand.ExecuteAsync(null);

        Assert.Contains("Se reescribió 1 fichero (memory/cronologico-2026-10.md) antes de parar", memory.ExportWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_daily_summary_follows_the_project()
    {
        var (memory, _, _, _, _) = Create();

        await memory.SetProjectAsync(Root);

        Assert.Equal(new ProjectContext("p-1", Root), memory.Summary.Project);
    }

    [Fact]
    public async Task Without_a_project_it_asks_for_one_and_offers_nothing()
    {
        var (memory, store, _, _, _) = Create(Entry("m-1", MemoryTrust.Trusted));

        await memory.SetProjectAsync(null);

        Assert.Empty(store.Recalls);
        Assert.Empty(memory.Entries);
        Assert.Equal("Abre una carpeta para ver su memoria.", memory.StatusMessage);
        Assert.False(memory.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_the_memory_composed_it_says_so()
    {
        var memory = new MemoryViewModel(new FakeProjectWorkspace(), null, null, null, new DailySummaryViewModel(null, new FakeClock(At)), new FakeClock(At));

        await memory.SetProjectAsync(Root);

        Assert.Equal("La memoria del proyecto no está disponible en esta composición.", memory.StatusMessage);
        Assert.False(memory.ExportCommand.CanExecute(null));
    }
}
