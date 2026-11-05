using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// One day of the journal in the trust panel: how many of its entries Agent Studio did not write wait in quarantine, or
/// that the person trusts it, and what can be decided about it.
/// </summary>
public sealed class JournalDayViewModel
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public JournalDayViewModel(DateOnly day, int quarantined, bool trusted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quarantined);
        Day = day;
        Quarantined = quarantined;
        Trusted = trusted;
    }

    public DateOnly Day { get; }

    /// <summary>The entries of the day that Agent Studio did not write and wait in quarantine.</summary>
    public int Quarantined { get; }

    /// <summary>Whether the person trusts the journal of this day.</summary>
    public bool Trusted { get; }

    /// <summary>«martes 28/10/2026».</summary>
    public string DayText => Day.ToString("dddd dd/MM/yyyy", Spanish);

    public string Summary => Trusted ? "De confianza: confías en este día"
        : Quarantined == 1 ? "1 entrada en cuarentena"
        : Quarantined.ToString(Spanish) + " entradas en cuarentena";

    public Tone Tone => Trusted ? Tone.Success : Tone.Warning;

    public string ActionLabel => Trusted ? "Retirar la confianza" : "Confiar en este día";

    /// <summary>The accessible name of the button: what it does, and to which day.</summary>
    public string ActionName => $"{ActionLabel}: {DayText}";

    /// <summary>How a list and UI Automation name it.</summary>
    public override string ToString() => $"{DayText} · {Summary}";
}

/// <summary>
/// «Confiar en la bitácora de este proyecto», and the same by days (docs/17 D-02; ADR-0047, decision 8), in plain Spanish.
/// The journal is text of the repository: what Agent Studio wrote matches the run that wrote it and is trusted; the rest
/// (the development hook, an entry that came with the repository) waits in quarantine until the person accepts it, one by
/// one (a review, in the list), by days or for the whole project. Each decision goes to the decision log through
/// <see cref="IJournalTrust"/>, by the person, and can be withdrawn; while a run or a planning goes on it waits in the queue
/// and says so. The days on show are those with entries in quarantine and those the person trusts.
/// </summary>
public sealed partial class JournalTrustViewModel : ObservableObject
{
    /// <summary>The journal entries read to know which days have entries in quarantine (the newest).</summary>
    public const int JournalEntriesRead = 2000;

    /// <summary>What the panel explains first.</summary>
    public const string Explanation =
        "La bitácora del proyecto (.agentstudio/bitacora/) también es memoria. Lo que anotó Agent Studio en sus ejecuciones se comprueba "
        + "con el registro y es de confianza. Lo demás, lo que anotan tus sesiones de desarrollo o lo que llega con el repositorio, espera "
        + "en cuarentena: ningún agente lo recibe hasta que lo aceptes, entrada a entrada, por días o para todo el proyecto. Cada decisión "
        + "queda en el registro y la puedes retirar. El juez nunca recibe la bitácora.";

    private readonly IJournalTrust? _trust;
    private readonly IProjectMemory? _memory;
    private readonly IRunActivity? _activity;
    private readonly Func<Task> _changed;
    private ProjectContext? _project;

    /// <param name="trust">The decision; null when it was not composed (the panel says so).</param>
    /// <param name="memory">The memory, to know which days have entries in quarantine.</param>
    /// <param name="activity">Whether a run or a planning goes on (a decision then waits in the queue).</param>
    /// <param name="changed">What the document does after a decision is recorded: it reads the memory again.</param>
    public JournalTrustViewModel(IJournalTrust? trust, IProjectMemory? memory, IRunActivity? activity, Func<Task> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _trust = trust;
        _memory = memory;
        _activity = activity;
        _changed = changed;
    }

    /// <summary>Whether the decision was composed.</summary>
    public bool IsAvailable => _trust is not null && _memory is not null;

    /// <summary>How far the journal of the project is trusted now; null without a project or when it could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ProjectTrusted))]
    [NotifyPropertyChangedFor(nameof(ProjectActionLabel))]
    [NotifyPropertyChangedFor(nameof(ProjectActionHelp))]
    [NotifyPropertyChangedFor(nameof(CanDecide))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDayCommand))]
    private JournalTrustState? _state;

    /// <summary>A decision that is being recorded or waits in the queue; null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDecide))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDayCommand))]
    private string? _pending;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The days with entries in quarantine and the days the person trusts, the newest first.</summary>
    public ObservableCollection<JournalDayViewModel> Days { get; } = [];

    public bool HasDays => Days.Count > 0;

    public bool ProjectTrusted => State?.ProjectTrusted == true;

    /// <summary>How far the journal is trusted now, in plain Spanish.</summary>
    public string StateText
    {
        get
        {
            if (!IsAvailable)
            {
                return "La decisión de confiar en la bitácora no está disponible en esta composición.";
            }

            if (_project is null)
            {
                return "Abre una carpeta de proyecto para ver su bitácora.";
            }

            if (State is not { } state)
            {
                return "No se sabe aún cuánto confías en la bitácora de este proyecto.";
            }

            var quarantined = state.QuarantinedEntries switch
            {
                0 => "Ninguna entrada de la bitácora espera en cuarentena.",
                1 => "1 entrada de la bitácora espera en cuarentena: no llega a ningún agente.",
                var n => $"{n.ToString(CultureInfo.InvariantCulture)} entradas de la bitácora esperan en cuarentena: no llegan a ningún agente.",
            };
            if (state.ProjectTrusted)
            {
                return state.QuarantinedEntries == 0
                    ? "Confías en toda la bitácora de este proyecto, también en los días que vengan."
                    : $"Confías en toda la bitácora de este proyecto. {quarantined} Son heredadas de un historial aceptado como nuevo origen, y esas se revisan una a una.";
            }

            var days = state.TrustedDays.Count switch
            {
                0 => string.Empty,
                1 => " Confías en 1 día.",
                var n => $" Confías en {n.ToString(CultureInfo.InvariantCulture)} días.",
            };
            return quarantined + days;
        }
    }

    public string ProjectActionLabel => ProjectTrusted ? "Dejar de confiar en la bitácora de este proyecto" : "Confiar en la bitácora de este proyecto";

    /// <summary>What the button does, before it is pressed.</summary>
    public string ProjectActionHelp => ProjectTrusted
        ? "Las entradas que no escribió Agent Studio vuelven a cuarentena, salvo las de los días en que confías y las que aceptaste una a una."
        : "Las entradas que no escribió Agent Studio, también las de los días que vengan, podrán llegar a los agentes con «Memoria del proyecto». "
          + "Úsalo si la bitácora es tuya (tus sesiones de desarrollo) y no te llega de terceros.";

    /// <summary>Whether a decision can be taken now.</summary>
    public bool CanDecide => IsAvailable && _project is not null && State is not null && Pending is null;

    private bool RunIsActive => _project is { } project && _activity?.IsBusy(project.ProjectId) == true;

    /// <summary>The project changed (null: none), or the memory was read again: reads the trust and the days.</summary>
    public async Task RefreshAsync(ProjectContext? project)
    {
        _project = project;
        ErrorMessage = null;
        if (project is null || _trust is not { } trust || _memory is not { } memory)
        {
            State = null;
            Days.Clear();
            OnPropertyChanged(nameof(HasDays));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanDecide));
            ToggleProjectCommand.NotifyCanExecuteChanged();
            ToggleDayCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            var state = await trust.ReadAsync(project, CancellationToken.None).ConfigureAwait(true);
            var entries = await memory.RecallAsync(
                project,
                new MemoryQuery(Kinds: [MemoryKind.JournalEntry], MaxEntries: JournalEntriesRead, TrustedOnly: false),
                CancellationToken.None).ConfigureAwait(true);
            State = state;
            ShowDays(state, entries);
        }
#pragma warning disable CA1031 // The decision and the memory are behind ports: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            State = null;
            Days.Clear();
            OnPropertyChanged(nameof(HasDays));
            ErrorMessage = $"No se pudo leer la confianza de la bitácora: {ex.Message}";
        }

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanDecide));
        ToggleProjectCommand.NotifyCanExecuteChanged();
        ToggleDayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Confiar en la bitácora de este proyecto», or «Dejar de confiar»: a decision of the person, in the log.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task ToggleProjectAsync() => DecideAsync(JournalTrustScope.Project, null, !ProjectTrusted);

    /// <summary>«Confiar en este día», or «Retirar la confianza» of a day the person trusts.</summary>
    [RelayCommand(CanExecute = nameof(CanDecideDay))]
    private Task ToggleDayAsync(JournalDayViewModel? day) =>
        day is null ? Task.CompletedTask : DecideAsync(JournalTrustScope.Day, day.Day, !day.Trusted);

    private bool CanDecideDay(JournalDayViewModel? day) => CanDecide && day is not null;

    private async Task DecideAsync(JournalTrustScope scope, DateOnly? day, bool trusted)
    {
        if (!CanDecide || _project is not { } project || _trust is not { } trust)
        {
            return;
        }

        ErrorMessage = null;
        Pending = RunIsActive
            ? "En cola: se registrará cuando termine la ejecución o la planificación en curso."
            : "Registrando tu decisión…";
        try
        {
            await trust.SetAsync(project, scope, day, trusted, ApprovalPromptService.DecidedBy, null, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The decision is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Pending = null;
            ErrorMessage = $"No se pudo registrar tu decisión sobre la bitácora: {ex.Message}";
            return;
        }

        Pending = null;

        // The trust of the entries comes from the log: the document reads the memory, and this panel, again.
        await _changed().ConfigureAwait(true);
    }

    /// <summary>The days with entries Agent Studio did not write in quarantine, and the days the person trusts.</summary>
    private void ShowDays(JournalTrustState state, IReadOnlyList<MemoryEntry> entries)
    {
        var quarantined = entries
            .Where(e => e is { Kind: MemoryKind.JournalEntry, Origin: MemoryOrigin.Agent, Trust: MemoryTrust.Quarantined })
            .Select(e => JournalSources.Day(e.Source))
            .OfType<DateOnly>()
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());
        var trusted = state.TrustedDays.ToHashSet();
        Days.Clear();
        foreach (var day in quarantined.Keys.Union(trusted).OrderDescending())
        {
            Days.Add(new JournalDayViewModel(day, quarantined.GetValueOrDefault(day), trusted.Contains(day)));
        }

        OnPropertyChanged(nameof(HasDays));
    }
}
