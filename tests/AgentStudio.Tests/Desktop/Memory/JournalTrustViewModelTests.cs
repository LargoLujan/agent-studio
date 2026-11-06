using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Memory.MemorySearchSamples;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The panel «Bitácora» of «Memoria» (docs/17 D-02; ADR-0047, decision 8) over fake ports: what the trust covers, in plain
/// Spanish; «Confiar en la bitácora de este proyecto» and the same by days, each a decision of the person through
/// <see cref="IJournalTrust"/> that can be withdrawn and waits in the queue while a run goes on; and the days with entries
/// in quarantine, which are only those Agent Studio did not write.
/// </summary>
public sealed class JournalTrustViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly ProjectContext Project = new("p-1", Root);

    private static readonly DateOnly Tuesday = new(2026, 10, 27);
    private static readonly DateOnly Wednesday = new(2026, 10, 28);
    private static readonly DateOnly Thursday = new(2026, 10, 29);

    private static (JournalTrustViewModel Journal, FakeJournalTrust Trust, FakeProjectMemory Store, FakeRunActivity Activity, Func<int> Changes) Create(JournalTrustState state, params MemoryEntry[] entries)
    {
        var trust = new FakeJournalTrust { State = state };
        var store = new FakeProjectMemory();
        store.Entries.AddRange(entries);
        var activity = new FakeRunActivity();
        var changes = 0;
        JournalTrustViewModel? journal = null;
        journal = new JournalTrustViewModel(trust, store, activity, async () =>
        {
            changes++;
            await journal!.RefreshAsync(Project);
        });
        return (journal, trust, store, activity, () => changes);
    }

    private static MemoryEntry[] Quarantined() =>
    [
        JournalEntry("j-1", Wednesday, MemoryTrust.Quarantined),
        JournalEntry("j-2", Wednesday, MemoryTrust.Quarantined),
        JournalEntry("j-3", Thursday, MemoryTrust.Quarantined),
        JournalEntry("j-4", Thursday, MemoryTrust.Trusted, MemoryOrigin.System),
        JournalEntry("j-5", Thursday, MemoryTrust.Quarantined, MemoryOrigin.System),
    ];

    [Fact]
    public async Task The_panel_explains_what_the_trust_covers_and_how_many_entries_wait()
    {
        var (journal, _, store, _, _) = Create(new JournalTrustState(false, [], 4), Quarantined());

        await journal.RefreshAsync(Project);

        Assert.Contains("Lo que Agent Studio anota en sus ejecuciones deja un recibo en el registro: con él se comprueba, y es de confianza", JournalTrustViewModel.Explanation, StringComparison.Ordinal);
        Assert.Contains("El juez nunca recibe la bitácora", JournalTrustViewModel.Explanation, StringComparison.Ordinal);
        Assert.Equal("4 entradas de la bitácora esperan en cuarentena: no llegan a ningún agente.", journal.StateText);
        Assert.Equal("Confiar en la bitácora de este proyecto", journal.ProjectActionLabel);
        Assert.StartsWith("Las entradas cuyo autor no se puede comprobar, también las de los días que vengan", journal.ProjectActionHelp, StringComparison.Ordinal);
        var recall = Assert.Single(store.Recalls).Query;
        Assert.Equal([MemoryKind.JournalEntry], recall.Kinds);
        Assert.False(recall.TrustedOnly);
    }

    /// <summary>
    /// V40: what Agent Studio wrote before it left receipts has none, like an entry of the hook, so the panel never says
    /// that an entry in quarantine was not written by Agent Studio: it says that who wrote it cannot be checked.
    /// </summary>
    [Fact]
    public async Task The_panel_says_who_wrote_an_entry_cannot_be_checked_and_never_that_agent_studio_did_not()
    {
        var (journal, _, _, _, _) = Create(new JournalTrustState(false, [], 4), Quarantined());
        await journal.RefreshAsync(Project);
        var untrusted = journal.ProjectActionHelp;
        await journal.ToggleProjectCommand.ExecuteAsync(null);
        var trusted = journal.ProjectActionHelp;

        Assert.True(journal.ProjectTrusted);
        Assert.Contains("lo que Agent Studio anotó antes de dejar recibos", JournalTrustViewModel.Explanation, StringComparison.Ordinal);
        Assert.Contains("Lo que no se puede comprobar espera en cuarentena", JournalTrustViewModel.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Las entradas cuyo autor no se puede comprobar vuelven a cuarentena", trusted, StringComparison.Ordinal);
        Assert.All(
            new[] { JournalTrustViewModel.Explanation, untrusted, trusted },
            text => Assert.DoesNotContain("no escribió", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_days_are_those_with_entries_agent_studio_did_not_write_in_quarantine_and_those_trusted()
    {
        var (journal, _, _, _, _) = Create(new JournalTrustState(false, [Tuesday], 4), Quarantined());

        await journal.RefreshAsync(Project);

        // Thursday's entry of the system in quarantine was inherited: trusting the day does not cover it, so it does not count.
        Assert.Equal([Thursday, Wednesday, Tuesday], journal.Days.Select(d => d.Day));
        Assert.Equal(["1 entrada en cuarentena", "2 entradas en cuarentena", "De confianza: confías en este día"], journal.Days.Select(d => d.Summary));
        Assert.Equal(["Confiar en este día", "Confiar en este día", "Retirar la confianza"], journal.Days.Select(d => d.ActionLabel));
        Assert.Equal([Tone.Warning, Tone.Warning, Tone.Success], journal.Days.Select(d => d.Tone));
        Assert.Equal("Retirar la confianza: martes 27/10/2026", journal.Days[2].ActionName);
        Assert.True(journal.HasDays);
        Assert.EndsWith("Confías en 1 día.", journal.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusting_the_journal_of_the_project_is_the_persons_decision_and_can_be_withdrawn()
    {
        var (journal, trust, _, _, changes) = Create(new JournalTrustState(false, [], 3), Quarantined());
        await journal.RefreshAsync(Project);

        await journal.ToggleProjectCommand.ExecuteAsync(null);

        Assert.Equal([(Project, JournalTrustScope.Project, (DateOnly?)null, true, "user", (string?)null)], trust.Decisions);
        Assert.Equal(1, changes());
        Assert.True(journal.ProjectTrusted);
        Assert.Equal("Dejar de confiar en la bitácora de este proyecto", journal.ProjectActionLabel);
        Assert.StartsWith("Confías en toda la bitácora de este proyecto.", journal.StateText, StringComparison.Ordinal);

        await journal.ToggleProjectCommand.ExecuteAsync(null);

        Assert.Equal((JournalTrustScope.Project, false), (trust.Decisions[1].Scope, trust.Decisions[1].Trusted));
        Assert.False(journal.ProjectTrusted);
    }

    [Fact]
    public async Task A_day_can_be_trusted_and_its_trust_withdrawn()
    {
        var (journal, trust, _, _, _) = Create(new JournalTrustState(false, [Tuesday], 3), Quarantined());
        await journal.RefreshAsync(Project);

        await journal.ToggleDayCommand.ExecuteAsync(journal.Days.Single(d => d.Day == Wednesday));
        await journal.ToggleDayCommand.ExecuteAsync(journal.Days.Single(d => d.Day == Tuesday));

        Assert.Equal(
            [(JournalTrustScope.Day, (DateOnly?)Wednesday, true), (JournalTrustScope.Day, (DateOnly?)Tuesday, false)],
            trust.Decisions.Select(d => (d.Scope, d.Day, d.Trusted)));
        Assert.All(trust.Decisions, d => Assert.Equal("user", d.DecidedBy));
    }

    [Fact]
    public async Task A_decision_asked_during_a_run_waits_in_the_queue_and_says_so()
    {
        var (journal, trust, _, activity, _) = Create(new JournalTrustState(false, [], 3), Quarantined());
        await journal.RefreshAsync(Project);
        activity.Busy = true;
        trust.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var decision = journal.ToggleProjectCommand.ExecuteAsync(null);

        Assert.Equal("En cola: se registrará cuando termine la ejecución o la planificación en curso.", journal.Pending);
        Assert.False(journal.CanDecide);
        Assert.False(journal.ToggleDayCommand.CanExecute(journal.Days[0]));
        Assert.Empty(trust.Decisions);

        activity.Busy = false;
        trust.Hold.SetResult();
        await decision;

        Assert.Single(trust.Decisions);
        Assert.Null(journal.Pending);
        Assert.True(journal.CanDecide);
    }

    [Fact]
    public async Task A_decision_that_fails_is_shown_and_not_thrown()
    {
        var (journal, trust, _, _, changes) = Create(new JournalTrustState(false, [], 3), Quarantined());
        await journal.RefreshAsync(Project);
        trust.Failure = new InvalidOperationException("El registro de decisiones no verifica.");

        await journal.ToggleProjectCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo registrar tu decisión sobre la bitácora: El registro de decisiones no verifica.", journal.ErrorMessage);
        Assert.Null(journal.Pending);
        Assert.Equal(0, changes());
        Assert.True(journal.ToggleProjectCommand.CanExecute(null));
    }

    [Fact]
    public async Task When_the_trust_cannot_be_read_the_panel_says_why_and_offers_nothing()
    {
        var (journal, trust, _, _, _) = Create(new JournalTrustState(false, [], 3), Quarantined());
        trust.ReadFailure = new InvalidOperationException("El registro de decisiones no verifica.");

        await journal.RefreshAsync(Project);

        Assert.Equal("No se pudo leer la confianza de la bitácora: El registro de decisiones no verifica.", journal.ErrorMessage);
        Assert.False(journal.CanDecide);
        Assert.Empty(journal.Days);
    }

    [Fact]
    public async Task Without_the_decision_composed_or_without_a_project_the_panel_says_so()
    {
        var unavailable = new JournalTrustViewModel(null, new FakeProjectMemory(), null, () => Task.CompletedTask);
        var (withoutProject, _, _, _, _) = Create(new JournalTrustState(false, [], 0));

        await unavailable.RefreshAsync(Project);
        await withoutProject.RefreshAsync(null);

        Assert.False(unavailable.IsAvailable);
        Assert.Equal("La decisión de confiar en la bitácora no está disponible en esta composición.", unavailable.StateText);
        Assert.False(unavailable.ToggleProjectCommand.CanExecute(null));
        Assert.Equal("Abre una carpeta de proyecto para ver su bitácora.", withoutProject.StateText);
        Assert.False(withoutProject.ToggleProjectCommand.CanExecute(null));
    }

    [Fact]
    public async Task With_nothing_in_quarantine_the_panel_says_so()
    {
        var (journal, _, _, _, _) = Create(new JournalTrustState(false, [], 0));

        await journal.RefreshAsync(Project);

        Assert.Equal("Ninguna entrada de la bitácora espera en cuarentena.", journal.StateText);
        Assert.False(journal.HasDays);
    }

    [Fact]
    public async Task In_the_document_a_decision_reads_the_memory_and_the_panel_again()
    {
        var trust = new FakeJournalTrust { State = new JournalTrustState(false, [], 3) };
        var store = new FakeProjectMemory();
        store.Entries.AddRange(Quarantined());
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            store,
            null,
            new FakeRunActivity(),
            new DailySummaryViewModel(null, new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(JournalTrust: trust));
        await memory.SetProjectAsync(Root);
        var recalls = store.Recalls.Count;

        await memory.Journal.ToggleProjectCommand.ExecuteAsync(null);

        Assert.Single(trust.Decisions);
        Assert.Equal(recalls + 2, store.Recalls.Count); // the journal entries of the panel, then the list
        Assert.Null(store.Recalls[^1].Query.Kinds);
        Assert.True(memory.Journal.ProjectTrusted);
    }
}
