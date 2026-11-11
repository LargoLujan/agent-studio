using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// One day of the journal in the trust panel: how many of its entries whose author cannot be checked wait in quarantine, or
/// that the person trusts it, and what can be decided about it.
/// </summary>
public sealed class JournalDayViewModel
{
    public JournalDayViewModel(DateOnly day, int quarantined, bool trusted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quarantined);
        Day = day;
        Quarantined = quarantined;
        Trusted = trusted;
    }

    public DateOnly Day { get; }

    /// <summary>The entries of the day whose author cannot be checked and wait in quarantine.</summary>
    public int Quarantined { get; }

    /// <summary>Whether the person trusts the journal of this day.</summary>
    public bool Trusted { get; }

    /// <summary>«martes 28/10/2026».</summary>
    public string DayText => Day.ToString(Texts.Get("Memory_Day_Format"), Texts.Current.Culture);

    public string Summary => Trusted ? Texts.Get("Memory_Journal_DayTrusted")
        : Quarantined == 1 ? Texts.Get("Memory_Journal_DayQuarantine_One")
        : Texts.Format("Memory_Journal_DayQuarantine_Other", Quarantined.ToString(Texts.Current.Culture));

    public Tone Tone => Trusted ? Tone.Success : Tone.Warning;

    public string ActionLabel => Trusted ? Texts.Get("Memory_Journal_DayRetract") : Texts.Get("Memory_Journal_DayTrust");

    /// <summary>The accessible name of the button: what it does, and to which day.</summary>
    public string ActionName => Texts.Format("Memory_Journal_DayActionName", ActionLabel, DayText);

    /// <summary>How a list and UI Automation name it.</summary>
    public override string ToString() => $"{DayText} · {Summary}";
}

/// <summary>
/// «Confiar en la bitácora de este proyecto», and the same by days (docs/17 D-02; ADR-0047, decision 8), in plain Spanish.
/// The journal is text of the repository: what Agent Studio wrote with its receipt matches the run that wrote it and is
/// trusted; the rest (the development hook, an entry that came with the repository, what Agent Studio wrote before it left
/// receipts: who wrote it cannot be checked, V40) waits in quarantine until the person accepts it, one by one (a review, in
/// the list), by days or for the whole project. Each decision goes to the decision log through
/// <see cref="IJournalTrust"/>, by the person, and can be withdrawn; while a run or a planning goes on it waits in the queue
/// and says so. The days on show are those with entries in quarantine and those the person trusts.
/// </summary>
public sealed partial class JournalTrustViewModel : ObservableObject
{
    /// <summary>The journal entries read to know which days have entries in quarantine (the newest).</summary>
    public const int JournalEntriesRead = 2000;

    /// <summary>What the panel explains first.</summary>
    public static string Explanation => Texts.Get("Memory_Journal_Explanation");

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
                return Texts.Get("Memory_Journal_Unavailable");
            }

            if (_project is null)
            {
                return Texts.Get("Memory_Journal_OpenFolder");
            }

            if (State is not { } state)
            {
                return Texts.Get("Memory_Journal_Unknown");
            }

            var quarantined = state.QuarantinedEntries switch
            {
                0 => Texts.Get("Memory_Journal_QuarantineNone"),
                1 => Texts.Get("Memory_Journal_QuarantineOne"),
                var n => Texts.Format("Memory_Journal_QuarantineMany", n.ToString(Texts.Current.Culture)),
            };
            if (state.ProjectTrusted)
            {
                return state.QuarantinedEntries == 0
                    ? Texts.Get("Memory_Journal_ProjectTrustedNone")
                    : Texts.Format("Memory_Journal_ProjectTrustedSome", quarantined);
            }

            var days = state.TrustedDays.Count switch
            {
                0 => string.Empty,
                1 => Texts.Get("Memory_Journal_TrustedDays_One"),
                var n => Texts.Format("Memory_Journal_TrustedDays_Other", n.ToString(Texts.Current.Culture)),
            };
            return quarantined + days;
        }
    }

    public string ProjectActionLabel => ProjectTrusted ? Texts.Get("Memory_Journal_ProjectRetract") : Texts.Get("Memory_Journal_ProjectTrust");

    /// <summary>What the button does, before it is pressed.</summary>
    public string ProjectActionHelp => ProjectTrusted
        ? Texts.Get("Memory_Journal_ProjectRetractHelp")
        : Texts.Get("Memory_Journal_ProjectTrustHelp");

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
            ErrorMessage = Texts.Format("Memory_Journal_ReadError", ex.Message);
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
            ? Texts.Get("Memory_Decision_Queued")
            : Texts.Get("Memory_Decision_Recording");
        try
        {
            await trust.SetAsync(project, scope, day, trusted, ApprovalPromptService.DecidedBy, null, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The decision is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Pending = null;
            ErrorMessage = Texts.Format("Memory_Journal_DecideError", ex.Message);
            return;
        }

        Pending = null;

        // The trust of the entries comes from the log: the document reads the memory, and this panel, again.
        await _changed().ConfigureAwait(true);
    }

    /// <summary>The days with entries in quarantine whose author cannot be checked, and the days the person trusts.</summary>
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
