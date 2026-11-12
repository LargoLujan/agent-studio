using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Memory.Summary;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// «Resumen del día» (docs/14 F4-9, D-31): the deterministic summary of one local day of the project's journal, made by
/// <see cref="IDailySummaryService"/> (no agent, no quota). Today's summary is provisional and is not kept; a closed day
/// goes to the memory as a daily summary once, and summarizing it again keeps nothing new.
/// </summary>
public sealed partial class DailySummaryViewModel : ObservableObject
{
    private readonly IDailySummaryService? _service;
    private readonly TimeProvider _time;

    /// <param name="service">Summarizes a day of the journal; null when the shell was composed without it.</param>
    /// <param name="time">Its local day is «hoy».</param>
    public DailySummaryViewModel(IDailySummaryService? service, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _time = time;
        _day = Today;
        Status = Unavailable;
    }

    /// <summary>The project whose journal is summarized; null without one.</summary>
    public ProjectContext? Project { get; private set; }

    /// <summary>The local day on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayText))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyCanExecuteChangedFor(nameof(NextDayCommand))]
    private DateOnly _day;

    /// <summary>«viernes 16/10/2026 (hoy)».</summary>
    public string DayText => Day.ToString(Texts.Get("Memory_Day_Format"), Texts.Current.Culture) + (Day == Today ? Texts.Get("Memory_Day_Today") : string.Empty);

    /// <summary>The summary as the service wrote it; null until one is made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private string? _summary;

    /// <summary>
    /// What the box of the summary says while it is empty: what to do to fill it, or why it cannot be filled; null once there
    /// is a summary.
    /// </summary>
    public string? EmptyHint => Summary is not null ? null
        : _service is null ? Texts.Get("Memory_Daily_Unavailable")
        : Project is null ? Texts.Get("Memory_Daily_OpenFolderHint")
        : IsBusy ? Texts.Get("Memory_Daily_Busy")
        : Texts.Format("Memory_Daily_Hint", DayText);

    /// <summary>Whether it was kept in the memory, or why not; or why nothing can be summarized.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>«12 prompts · 2 líneas sin reconocer (solo se cuentan)».</summary>
    [ObservableProperty]
    private string? _counts;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private bool _isBusy;

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private string? Unavailable => _service is null
        ? Texts.Get("Memory_Daily_Unavailable")
        : Project is null ? Texts.Get("Memory_Daily_OpenFolder") : null;

    /// <summary>The project open in the shell changed (null: none).</summary>
    public void SetProject(ProjectContext? project)
    {
        Project = project;
        Clear();
        SummarizeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EmptyHint));
    }

    [RelayCommand]
    private void PreviousDay() => Day = Day.AddDays(-1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextDay() => Day = Day.AddDays(1);

    private bool CanGoForward() => Day < Today;

    partial void OnDayChanged(DateOnly value) => Clear();

    /// <summary>«Resumir el día»: the deterministic summary of the day on screen.</summary>
    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private async Task SummarizeAsync()
    {
        if (_service is not { } service || Project is not { } project)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await service.SummarizeAsync(project, Day, CancellationToken.None).ConfigureAwait(true);
            // The text is anchored in Spanish when the day closes; the screen says its lines in the language in use (D-47, D-48).
            Summary = Texts.Current.RenderNested(DailySummaryTexts.Coded(result.Summary));
            Counts = Texts.Format(result.Prompts == 1 ? "Memory_Daily_Requests_One" : "Memory_Daily_Requests_Other", result.Prompts)
                + " · " + Texts.Format(result.UnrecognizedLines == 1 ? "Memory_Daily_Unrecognized_One" : "Memory_Daily_Unrecognized_Other", result.UnrecognizedLines);
            Status = result switch
            {
                { Provisional: true } => Texts.Get("Memory_Daily_Provisional"),
                { Recorded: { } entry } => Texts.Format("Memory_Daily_Recorded", entry.Id),
                _ => Texts.Get("Memory_Daily_AlreadyDone"),
            };
        }
#pragma warning disable CA1031 // The service is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Clear();
            ErrorMessage = Texts.Format("Memory_Daily_Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSummarize() => _service is not null && Project is not null && !IsBusy;

    private void Clear()
    {
        Summary = null;
        Counts = null;
        ErrorMessage = null;
        Status = Unavailable;
    }
}
