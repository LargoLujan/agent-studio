using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Observability;

/// <summary>Which projects a usage report covers (docs/14 D-05).</summary>
public enum UsageScope
{
    /// <summary>The project open in the shell.</summary>
    ThisProject,

    /// <summary>The open project and every recent project of the settings, each read without writing anything.</summary>
    RecentProjects,
}

/// <summary>The period of a report, in the user's local days.</summary>
public enum UsagePeriod
{
    Today,
    Last7Days,
    ThisMonth,
    Last30Days,
}

/// <summary>One choice of a list on screen.</summary>
public sealed record Option<T>(T Value, string Label)
{
    /// <summary>What a screen reader says of the option (A1-bis): its label, never the members of the record.</summary>
    public override string ToString() => Label;
}

/// <summary>A dimension the report can group by, and whether the user picked it.</summary>
public sealed partial class DimensionOption(UsageDimension dimension, string label) : ObservableObject
{
    public UsageDimension Dimension { get; } = dimension;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What a screen reader says of a dimension of the report (V34): its label.</summary>
    public override string ToString() => Label;
}

/// <summary>One group of the report as a row: its key and every figure with its origin.</summary>
/// <param name="ContextPeak">The context peak in words; <see cref="ContextPeakBar"/> draws it.</param>
public sealed record UsageGroupRow(
    string Key,
    Figure Attempts,
    Figure Duration,
    Figure Input,
    Figure CacheRead,
    Figure CacheWrite,
    Figure Output,
    Figure Reasoning,
    Figure Cost,
    Figure RateLimited,
    Figure LimitReached,
    string ContextPeak,
    Figure Compactions)
{
    /// <summary>The context peak of the group as a bar (V39, docs/18 P3): its percentage, origin and level, or why there is none.</summary>
    public required MetricBarReading ContextPeakBar { get; init; }

    /// <summary>Phase 9 (docs/16 D-34): the time the attempts of the group waited in the queue of their provider, apart from their duration.</summary>
    public Figure QueueWait { get; init; } = new(null, string.Empty);

    /// <summary>Phase 9 (docs/16 D-34): the time the tasks waited for the usage limit of their plan after these attempts hit it, apart from their duration.</summary>
    public Figure QuotaWait { get; init; } = new(null, string.Empty);

    /// <summary>
    /// How many attempts of the group are also in another group of the same dimension (a milestone), said apart (F9-6); null when none is.
    /// The groups of that dimension add up to the total plus exactly these.
    /// </summary>
    public string? SharedNote { get; init; }

    /// <summary>What a screen reader says of a group of the report (V34): its key.</summary>
    public override string ToString() => Key;
}

/// <summary>
/// One axis of Phase 9 (docs/16 D-34, F9-6): the attempts of the report split by milestone or by one of the three axes of the activity,
/// each with its total, so its rows add up to it. <see cref="SharedNote"/> is the attempts of more than one milestone, said apart.
/// </summary>
public sealed record UsageAxis(string Title, string TotalText, IReadOnlyList<UsageGroupRow> Rows, string? SharedNote)
{
    /// <summary>What a screen reader says of an axis: its title.</summary>
    public override string ToString() => Title;
}

/// <summary>One attempt as a row: when it finished, who ran it, where and how it ended, its own figures, its context peak and its models.</summary>
/// <param name="ContextPeak">«Contexto máximo: …» in words; <see cref="ContextPeakBar"/> draws it.</param>
public sealed record UsageAttemptRow(string When, string Who, string Where, Figure Duration, Figure Input, Figure Output, Figure Cost, string ContextPeak, IReadOnlyList<string> PerModel)
{
    /// <summary>The context peak of the attempt as a bar (V39, docs/18 P3): its percentage, origin and level, or why there is none.</summary>
    public required MetricBarReading ContextPeakBar { get; init; }

    /// <summary>What a screen reader says of an attempt of the report (V34): when it finished and who ran it.</summary>
    public override string ToString() => $"{When} · {Who}";
}

/// <summary>
/// The «Consumo» document (docs/14 F4-1 and F4-2): the usage of the open project, or of every recent project, in the
/// period the user picks (local days), grouped by the dimensions the user picks, from the verified decision logs
/// (<see cref="IUsageReport"/>, D-01). Every figure says where it comes from (D-03); a sum that misses values says
/// «parcial»; a v1 attempt says it has no breakdown; the projects left out are listed with why; and the prompts of the
/// journal are reconciled with the attempts without being counted (D-08). The report on screen exports to CSV or JSON
/// (<see cref="IUsageExporter"/>, D-07), written atomically to the file chosen with «Guardar como». It also holds the
/// quota of each provider (<see cref="Quota"/>) and the preferences the report is judged with (<see cref="Preferences"/>),
/// which are changed in «Ajustes» (<c>SettingsDocumentViewModel</c>, «Contexto y cuota»), not here.
/// </summary>
public sealed partial class UsageDashboardViewModel : DocumentViewModel
{
    public const string DocumentId = "observability:usage";

    /// <summary>What the cost is, as the panel states it (docs/14 D-06), from the resources.</summary>
    public static string CostNote => Texts.Get("Usage_CostNote");

    /// <summary>Where the settings of the report and of the quota are, as the panel states it, from the resources.</summary>
    public static string SettingsNote => Texts.Get("Usage_SettingsNote");

    /// <summary>What the bar of a context peak is called, in a group and in an attempt (V39), from the resources.</summary>
    public static string PeakLabel => Texts.Get("Usage_Peak_Label");

    /// <summary>The model of an attempt that has none, in the language in use: the group of the aggregator says it the same way.</summary>
    private static string NoModel => Texts.Get("Usage_Value_NoModel");

    private readonly IUsageReport? _report;
    private readonly IUsageExporter? _exporter;
    private readonly ISaveFilePicker _picker;
    private readonly IAppSettingsStore _settings;
    private readonly TimeProvider _time;
    private string? _projectRoot;
    private UsageReport? _shown;

    /// <param name="report">The projection of the logs; null when the shell was composed without it (the panel says so).</param>
    /// <param name="exporter">Writes the CSV and the JSON; null when it was not composed (nothing is exported).</param>
    /// <param name="picker">«Guardar como».</param>
    /// <param name="settings">Where the recent projects are.</param>
    /// <param name="time">The local days of the periods and the times shown.</param>
    /// <param name="quota">The quota of each provider.</param>
    /// <param name="preferences">The thresholds the context peaks are judged with, and the Codex opt-in («Contexto y cuota» of «Ajustes»).</param>
    public UsageDashboardViewModel(
        IUsageReport? report,
        IUsageExporter? exporter,
        ISaveFilePicker picker,
        IAppSettingsStore settings,
        TimeProvider time,
        QuotaViewModel quota,
        ObservabilitySettingsViewModel preferences)
        : base(DocumentId, Texts.Get("Usage_Title"))
    {
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(preferences);
        _report = report;
        _exporter = exporter;
        _picker = picker;
        _settings = settings;
        _time = time;
        Quota = quota;
        Preferences = preferences;
        _selectedScope = Scopes[0];
        _selectedPeriod = Periods[0];
        foreach (var option in Dimensions.Where(d => d.Dimension is UsageDimension.Provider or UsageDimension.Model))
        {
            option.IsSelected = true;
        }

        Quota.CodexOptedIn = Preferences.CodexOptedIn;
        Preferences.Saved += OnPreferencesSaved;
    }

    public QuotaViewModel Quota { get; }

    /// <summary>The thresholds and the Codex opt-in in force; «Ajustes» changes them and this report follows (<see cref="ObservabilitySettingsViewModel.Saved"/>).</summary>
    public ObservabilitySettingsViewModel Preferences { get; }

    public IReadOnlyList<Option<UsageScope>> Scopes { get; } =
    [
        new(UsageScope.ThisProject, Texts.Get("Usage_Scope_This")),
        new(UsageScope.RecentProjects, Texts.Get("Usage_Scope_Recent")),
    ];

    public IReadOnlyList<Option<UsagePeriod>> Periods { get; } =
    [
        new(UsagePeriod.Today, Texts.Get("Usage_Period_Today")),
        new(UsagePeriod.Last7Days, Texts.Get("Usage_Period_Last7Days")),
        new(UsagePeriod.ThisMonth, Texts.Get("Usage_Period_ThisMonth")),
        new(UsagePeriod.Last30Days, Texts.Get("Usage_Period_Last30Days")),
    ];

    /// <summary>
    /// What the report can group by, in the order the query takes them (D-04): every dimension of <see cref="UsageDimension"/>,
    /// the milestone and the three axes of the activity of Phase 9 (docs/16 D-34) included, since the aggregator groups by them.
    /// </summary>
    public IReadOnlyList<DimensionOption> Dimensions { get; } = [.. Enum.GetValues<UsageDimension>().Select(d => new DimensionOption(d, DimensionLabel(d)))];

    [ObservableProperty]
    private Option<UsageScope> _selectedScope;

    [ObservableProperty]
    private Option<UsagePeriod> _selectedPeriod;

    public ObservableCollection<UsageGroupRow> Groups { get; } = [];

    public ObservableCollection<UsageAttemptRow> Attempts { get; } = [];

    /// <summary>
    /// Phase 9 (docs/16 D-34, F9-6): the attempts of the report split by milestone and by each of the three axes of the activity,
    /// apart from the groups the person picks. Each axis adds up to the total; what a milestone shares is said apart.
    /// </summary>
    public ObservableCollection<UsageAxis> Axes { get; } = [];

    /// <summary>The projects left out of the report, each with why (D-05).</summary>
    public ObservableCollection<string> Excluded { get; } = [];

    /// <summary>The prompts of the journal against the attempts of the logs (D-08); null without a report.</summary>
    [ObservableProperty]
    private string? _reconciliation;

    public ObservableCollection<string> ReconciliationWarnings { get; } = [];

    /// <summary>The period of the report on screen, in local days.</summary>
    [ObservableProperty]
    private string? _rangeText;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _exportStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportJsonCommand))]
    private bool _isBusy;

    /// <summary>The project open in the shell changed (null: none); the report is asked again.</summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        _projectRoot = projectRoot;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>«Actualizar»: asks the report again with what is chosen, and the quota.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        ExportStatus = null;
        await Quota.RefreshAsync().ConfigureAwait(true);
        if (_report is null)
        {
            Show(null);
            StatusMessage = Texts.Get("Usage_Status_Unavailable");
            return;
        }

        var roots = Roots();
        if (roots.Count == 0)
        {
            Show(null);
            StatusMessage = SelectedScope.Value == UsageScope.ThisProject
                ? Texts.Get("Usage_Status_OpenFolder")
                : Texts.Get("Usage_Status_NoRecent");
            return;
        }

        var (from, to) = Range(SelectedPeriod.Value);
        var query = new UsageQuery(from, to, _time.LocalTimeZone, [.. Dimensions.Where(d => d.IsSelected).Select(d => d.Dimension)]);
        IsBusy = true;
        StatusMessage = Texts.Get("Usage_Status_Calculating");
        try
        {
            var report = await _report.QueryAsync(roots, query, CancellationToken.None).ConfigureAwait(true);
            Show(report);
            RangeText = Texts.Format("Usage_Range", Day(from), Day(to.AddDays(-1)));
            StatusMessage = report.Attempts.Count == 0 && report.Groups.Count == 0 ? Texts.Get("Usage_Status_NoAttempts") : null;
        }
#pragma warning disable CA1031 // The projection is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Show(null);
            StatusMessage = null;
            ErrorMessage = Texts.Format("Usage_Error_Calculate", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsIdle() => !IsBusy;

    /// <summary>«Exportar CSV»: the report on screen, RFC 4180 with BOM, to the file chosen with «Guardar como».</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsvAsync() => ExportAsync("CSV", "csv", (exporter, report, stream) => exporter.WriteCsvAsync(report, stream, CancellationToken.None));

    /// <summary>«Exportar JSON»: the report on screen as <c>agentstudio-usage/1</c>, to the file chosen with «Guardar como».</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => ExportAsync("JSON", "json", (exporter, report, stream) => exporter.WriteJsonAsync(report, stream, CancellationToken.None));

    private bool CanExport() => _shown is not null && _exporter is not null && !IsBusy;

    private async Task ExportAsync(string format, string extension, Func<IUsageExporter, UsageReport, Stream, Task> write)
    {
        if (_shown is not { } report || _exporter is not { } exporter)
        {
            return;
        }

        var suggested = Texts.Format("Usage_Export_FileName", _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), extension);
        if (_picker.PickSaveFile(Texts.Format("Usage_Export_Title", format), suggested, $"{format} (*.{extension})|*.{extension}") is not { } path)
        {
            return;
        }

        ErrorMessage = null;
        ExportStatus = null;
        IsBusy = true;
        try
        {
            await ExportFile.WriteAtomicallyAsync(path, stream => write(exporter, report, stream)).ConfigureAwait(true);
            ExportStatus = Texts.Format("Usage_Export_Done", path);
        }
#pragma warning disable CA1031 // The exporter is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Usage_Export_Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPreferencesSaved(object? sender, EventArgs e)
    {
        Quota.CodexOptedIn = Preferences.CodexOptedIn;
        if (_shown is { } report)
        {
            Show(report);
        }
    }

    /// <summary>The projects of the scope: the open one first, then the recent ones of the settings, without repeats.</summary>
    private List<string> Roots()
    {
        var roots = new List<string>();
        if (_projectRoot is { } open)
        {
            roots.Add(open);
        }

        if (SelectedScope.Value == UsageScope.RecentProjects)
        {
            foreach (var recent in _settings.Load().RecentProjects.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                if (!roots.Contains(recent, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(recent);
                }
            }
        }

        return roots;
    }

    /// <summary>[first local midnight, last local midnight) of the period, each with the offset of the zone at that time.</summary>
    private (DateTimeOffset From, DateTimeOffset To) Range(UsagePeriod period)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var (first, end) = period switch
        {
            UsagePeriod.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            UsagePeriod.ThisMonth => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1)),
            UsagePeriod.Last30Days => (today.AddDays(-29), today.AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
        return (Midnight(first), Midnight(end));
    }

    private DateTimeOffset Midnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }

    private static string Day(DateTimeOffset at) => at.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private void Show(UsageReport? report)
    {
        _shown = report;
        var thresholds = Preferences.Thresholds;
        Replace(Groups, report?.Groups.Select(g => Row(g, thresholds)) ?? []);
        Replace(Attempts, report?.Attempts.Select(a => Row(a, thresholds)) ?? []);
        Replace(Axes, report is null ? [] : AxesOf(report, thresholds));
        Replace(Excluded, report?.Excluded.Select(e => $"{e.Root}: {UsageNotes.Translate(e.Reason)}") ?? []);
        Replace(ReconciliationWarnings, report?.Journal.Warnings.Select(UsageNotes.Translate) ?? []);
        Reconciliation = report is null ? null : ReconciliationText(report.Journal);
        if (report is null)
        {
            RangeText = null;
        }

        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
    }

    private static UsageGroupRow Row(UsageGroup group, ContextHealthThresholds thresholds)
    {
        var totals = group.Totals;
        var key = group.Key.Count == 0 ? Texts.Get("Usage_Total") : string.Join(" · ", group.Key.Select(k => $"{DimensionLabel(k.Dimension)}: {ValueLabel(k.Dimension, k.Value)}"));
        return Row(key, group, thresholds);
    }

    private static UsageGroupRow Row(string key, UsageGroup group, ContextHealthThresholds thresholds)
    {
        var totals = group.Totals;
        return new UsageGroupRow(
            key,
            MetricText.Measured(totals.Attempts),
            MetricText.Duration(totals.DurationMilliseconds),
            MetricText.Tokens(totals.InputTokens),
            MetricText.Tokens(totals.CacheReadInputTokens),
            MetricText.Tokens(totals.CacheWriteInputTokens),
            MetricText.Tokens(totals.OutputTokens),
            MetricText.Tokens(totals.ReasoningOutputTokens),
            MetricText.Cost(totals.CostUsd),
            MetricText.Measured(totals.RateLimited),
            MetricText.Measured(totals.LimitReached),
            Peak(totals.ContextPeak, thresholds),
            MetricText.Tokens(totals.Compactions))
        {
            ContextPeakBar = PeakBar(totals.ContextPeak, thresholds),
            QueueWait = totals.QueueWaitMilliseconds is { } queue ? MetricText.Duration(queue) : new Figure(null, MetricText.Origin(MetricOrigin.Missing)),
            QuotaWait = totals.QuotaWaitMilliseconds is { } quota ? MetricText.Duration(quota) : new Figure(null, MetricText.Origin(MetricOrigin.Missing)),
            SharedNote = group.SharedAttempts > 0 ? Texts.Format("Usage_Shared_Row", group.SharedAttempts.ToString("N0", MetricText.Culture)) : null,
        };
    }

    /// <summary>
    /// The four axes of Phase 9 (milestone, kind of work, first attempt or retry, what became of the work) over the attempts of the
    /// report, grouped by the aggregator of the projection itself, so the rows of an axis add up to its total (F9-6). Nothing is shown
    /// for a report without attempts.
    /// </summary>
    private IEnumerable<UsageAxis> AxesOf(UsageReport report, ContextHealthThresholds thresholds)
    {
        if (report.Attempts.Count == 0)
        {
            yield break;
        }

        var (from, to) = (report.Attempts.Min(a => a.FinishedAt), report.Attempts.Max(a => a.FinishedAt).AddTicks(1));
        foreach (var (dimension, titleKey) in new[]
        {
            (UsageDimension.Milestone, "Usage_Axis_Milestone"),
            (UsageDimension.Activity, "Usage_Axis_Activity"),
            (UsageDimension.ActivityAttempt, "Usage_Axis_Order"),
            (UsageDimension.ActivityDestination, "Usage_Axis_Fate"),
        })
        {
            var groups = UsageAggregator.Group(report.Attempts, new UsageQuery(from, to, _time.LocalTimeZone, [dimension]));
            var shared = dimension == UsageDimension.Milestone ? report.Attempts.Count(a => a.Milestones.Count > 1) : 0;
            yield return new UsageAxis(
                Texts.Get(titleKey),
                Texts.Format("Usage_Axis_Total", groups[0].Totals.Attempts.ToString("N0", MetricText.Culture)),
                [.. groups.Skip(1).Select(g => Row(ValueLabel(dimension, g.Key[0].Value), g, thresholds))],
                shared > 0 ? Texts.Format("Usage_Axis_Shared", shared.ToString("N0", MetricText.Culture)) : null);
        }
    }

    private UsageAttemptRow Row(UsageAttempt attempt, ContextHealthThresholds thresholds)
    {
        var finished = TimeZoneInfo.ConvertTime(attempt.FinishedAt, _time.LocalTimeZone);
        var who = string.Join(" · ", new[] { attempt.RunId, attempt.TaskId, Texts.Format("Usage_AttemptNumber", attempt.Attempt), PurposeLabel(attempt.Purpose), attempt.Agent.Value }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var where = string.Join(" · ", attempt.Provider.Value, attempt.ReportedModel ?? attempt.ConfiguredModel ?? NoModel, OutcomeLabel(attempt.Outcome));
        var duration = attempt.LaunchedAt is { } launched
            ? MetricValue<long>.Measured(Math.Max(0, (long)(attempt.FinishedAt - launched).TotalMilliseconds), "process start to finish")
            : MetricValue<long>.Missing("no launch time before version 2");
        IReadOnlyList<string> perModel = attempt.RecordedVersion < 2
            ? [Texts.Get("Usage_NoBreakdown")]
            : [.. attempt.PerModel.Select(m => Texts.Format(
                "Usage_PerModel",
                m.Model,
                MetricText.Tokens(m.Figures.InputTokens).Text,
                MetricText.Tokens(m.Figures.OutputTokens).Text,
                MetricText.Cost(m.Figures.CostUsd, m.CostBasis).Text))];
        return new UsageAttemptRow(
            finished.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            who,
            where,
            MetricText.Duration(duration),
            MetricText.Tokens(attempt.Own.InputTokens),
            MetricText.Tokens(attempt.Own.OutputTokens),
            MetricText.Cost(attempt.Own.CostUsd),
            Texts.Format("Usage_Peak_Of", Peak(attempt.ContextPeak, thresholds)),
            perModel)
        {
            ContextPeakBar = PeakBar(attempt.ContextPeak, thresholds),
        };
    }

    private static string Peak(ContextPeakFigures? peak, ContextHealthThresholds thresholds) =>
        peak is null ? Texts.Get("Usage_Peak_NoData") : ContextText.Read(peak.Model, peak.UsedTokens, peak.ContextWindow, thresholds).Text;

    /// <summary>
    /// The bar of a context peak (V39): read as every context bar is (<see cref="MetricBars"/>), judged with the thresholds of
    /// the settings; without a peak, an empty track that says «sin dato».
    /// </summary>
    private static MetricBarReading PeakBar(ContextPeakFigures? peak, ContextHealthThresholds thresholds) =>
        peak is null ? MetricBars.Empty(PeakLabel, Texts.Get("Usage_Peak_NoData")) : MetricBars.Context(PeakLabel, peak.Model, peak.UsedTokens, peak.ContextWindow, thresholds);

    private static string ReconciliationText(UsageReconciliation journal)
    {
        if (journal.JournalPrompts is not { } prompts)
        {
            return Texts.Get("Usage_Reconciliation_None");
        }

        var said = Texts.Format("Usage_Reconciliation_Said", prompts, journal.RecordedAttempts);
        return journal.Difference switch
        {
            0 => Texts.Format("Usage_Reconciliation_Equal", said),
            var difference => Texts.Format("Usage_Reconciliation_Difference", said, Math.Abs(difference ?? 0)),
        };
    }

    /// <summary>The name of a dimension, from the resources; one the resources do not know yet is shown as its name, never as <c>[key]</c>.</summary>
    internal static string DimensionLabel(UsageDimension dimension) => Named("Usage_Dim_", dimension) ?? dimension.ToString();

    /// <summary>
    /// The value of a group on a dimension. The aggregator gives the values of the milestone and of the three axes of the activity as
    /// names (<c>Implementation</c>, <c>First</c>, <c>Kept</c>) and as markers (<c>sin hito</c>, <c>sin sesión</c>, <c>sin tarea</c>, the model
    /// and the purpose of an attempt that has none) that the export keeps as they are, so the screen says them in the language in use. Every other value is data and is shown as it is.
    /// </summary>
    internal static string ValueLabel(UsageDimension dimension, string value) => dimension switch
    {
        UsageDimension.Model => value == UsageAttemptFacts.DefaultModel ? Texts.Get("Usage_Value_NoModel") : value,
        UsageDimension.Session => value == UsageAggregator.NoSession ? Texts.Get("Usage_Value_NoSession") : value,
        UsageDimension.Task => value == UsageAggregator.NoTask ? Texts.Get("Usage_Value_NoTask") : value,
        UsageDimension.Purpose => value == UsageAggregator.NoPurpose ? Texts.Get("Usage_Value_NoPurpose") : Texts.Current.Has("Usage_Purpose_" + value) ? Texts.Get("Usage_Purpose_" + value) : value,
        UsageDimension.Milestone => value == UsageAggregator.NoMilestone ? Texts.Get("Usage_Value_NoMilestone") : value,
        UsageDimension.Activity => value == UsageAggregator.NoActivity ? Texts.Get("Usage_Value_NoActivity") : Texts.Current.Has("Usage_Activity_" + value) ? Texts.Get("Usage_Activity_" + value) : value,
        UsageDimension.ActivityAttempt => value == UsageAggregator.NoActivity ? Texts.Get("Usage_Value_NoActivity") : Texts.Current.Has("Usage_Order_" + value) ? Texts.Get("Usage_Order_" + value) : value,
        UsageDimension.ActivityDestination => value == UsageAggregator.NoActivity ? Texts.Get("Usage_Value_NoActivity")
            : value == UsageAggregator.DiscardedWithCommit ? Texts.Get("Usage_Fate_DiscardedWithCommit")
            : Texts.Current.Has("Usage_Fate_" + value) ? Texts.Get("Usage_Fate_" + value) : value,
        _ => value,
    };

    private static string? PurposeLabel(AgentRunPurpose? purpose) => purpose is { } known ? Named("Usage_Purpose_", known) : null;

    private static string OutcomeLabel(AttemptOutcome outcome) => Named("Usage_Outcome_", outcome) ?? outcome.ToString();

    /// <summary>The text of <c>prefix + value</c>; null when the resources do not have it (a value appended later).</summary>
    private static string? Named(string prefix, Enum value) => Texts.Current.Has(prefix + value) ? Texts.Get(prefix + value) : null;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
