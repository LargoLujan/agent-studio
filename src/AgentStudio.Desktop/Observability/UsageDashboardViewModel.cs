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

    /// <summary>What a screen reader says of a group of the report (V34): its key.</summary>
    public override string ToString() => Key;
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

    /// <summary>What the cost is, as the panel states it (docs/14 D-06).</summary>
    public const string CostNote =
        "El coste es la estimación del CLI con su propia tabla de precios (precio de lista): con suscripción no es lo que pagas. Codex no lo expone.";

    /// <summary>Where the settings of the report and of the quota are, as the panel states it.</summary>
    public const string SettingsNote =
        "Los umbrales de salud del contexto y el permiso para leer la cuota de Codex se cambian en «Ajustes» (el engranaje de la barra de la izquierda).";

    /// <summary>What the bar of a context peak is called, in a group and in an attempt (V39).</summary>
    public const string PeakLabel = "Contexto máximo";

    private const string NoModel = "predeterminado del CLI (no expuesto)";

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
        : base(DocumentId, "Consumo")
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
        new(UsageScope.ThisProject, "Este proyecto"),
        new(UsageScope.RecentProjects, "Todos los proyectos recientes"),
    ];

    public IReadOnlyList<Option<UsagePeriod>> Periods { get; } =
    [
        new(UsagePeriod.Today, "Hoy"),
        new(UsagePeriod.Last7Days, "Últimos 7 días"),
        new(UsagePeriod.ThisMonth, "Este mes"),
        new(UsagePeriod.Last30Days, "Últimos 30 días"),
    ];

    /// <summary>What the report can group by, in the order the query takes them (D-04).</summary>
    public IReadOnlyList<DimensionOption> Dimensions { get; } = [.. Enum.GetValues<UsageDimension>().Select(d => new DimensionOption(d, DimensionLabel(d)))];

    [ObservableProperty]
    private Option<UsageScope> _selectedScope;

    [ObservableProperty]
    private Option<UsagePeriod> _selectedPeriod;

    public ObservableCollection<UsageGroupRow> Groups { get; } = [];

    public ObservableCollection<UsageAttemptRow> Attempts { get; } = [];

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
            StatusMessage = "El consumo no está disponible en esta composición: falta la proyección del registro.";
            return;
        }

        var roots = Roots();
        if (roots.Count == 0)
        {
            Show(null);
            StatusMessage = SelectedScope.Value == UsageScope.ThisProject
                ? "Abre una carpeta para ver su consumo, o elige «Todos los proyectos recientes»."
                : "No hay proyectos recientes en los ajustes.";
            return;
        }

        var (from, to) = Range(SelectedPeriod.Value);
        var query = new UsageQuery(from, to, _time.LocalTimeZone, [.. Dimensions.Where(d => d.IsSelected).Select(d => d.Dimension)]);
        IsBusy = true;
        StatusMessage = "Calculando el consumo…";
        try
        {
            var report = await _report.QueryAsync(roots, query, CancellationToken.None).ConfigureAwait(true);
            Show(report);
            RangeText = $"Del {Day(from)} al {Day(to.AddDays(-1))}, en tu hora local.";
            StatusMessage = report.Attempts.Count == 0 && report.Groups.Count == 0 ? "Sin intentos en el periodo." : null;
        }
#pragma warning disable CA1031 // The projection is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Show(null);
            StatusMessage = null;
            ErrorMessage = $"No se pudo calcular el consumo: {ex.Message}";
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

        var suggested = $"consumo-{_time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{extension}";
        if (_picker.PickSaveFile($"Exportar el consumo a {format}", suggested, $"{format} (*.{extension})|*.{extension}") is not { } path)
        {
            return;
        }

        ErrorMessage = null;
        ExportStatus = null;
        IsBusy = true;
        try
        {
            await ExportFile.WriteAtomicallyAsync(path, stream => write(exporter, report, stream)).ConfigureAwait(true);
            ExportStatus = $"Exportado a {path}";
        }
#pragma warning disable CA1031 // The exporter is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo exportar: {ex.Message}";
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
        Replace(Excluded, report?.Excluded.Select(e => $"{e.Root}: {e.Reason}") ?? []);
        Replace(ReconciliationWarnings, report?.Journal.Warnings ?? []);
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
        var key = group.Key.Count == 0 ? "Total" : string.Join(" · ", group.Key.Select(k => $"{DimensionLabel(k.Dimension)}: {k.Value}"));
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
        };
    }

    private UsageAttemptRow Row(UsageAttempt attempt, ContextHealthThresholds thresholds)
    {
        var finished = TimeZoneInfo.ConvertTime(attempt.FinishedAt, _time.LocalTimeZone);
        var who = string.Join(" · ", new[] { attempt.RunId, attempt.TaskId, $"intento {attempt.Attempt}", PurposeLabel(attempt.Purpose), attempt.Agent.Value }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var where = string.Join(" · ", attempt.Provider.Value, attempt.ReportedModel ?? attempt.ConfiguredModel ?? NoModel, OutcomeLabel(attempt.Outcome));
        var duration = attempt.LaunchedAt is { } launched
            ? MetricValue<long>.Measured(Math.Max(0, (long)(attempt.FinishedAt - launched).TotalMilliseconds), "process start to finish")
            : MetricValue<long>.Missing("no launch time before version 2");
        IReadOnlyList<string> perModel = attempt.RecordedVersion < 2
            ? ["sin desglose (registro anterior a la Fase 4)"]
            : [.. attempt.PerModel.Select(m =>
                $"{m.Model}: entrada {MetricText.Tokens(m.Figures.InputTokens).Text}; salida {MetricText.Tokens(m.Figures.OutputTokens).Text}; coste {MetricText.Cost(m.Figures.CostUsd, m.CostBasis).Text}")];
        return new UsageAttemptRow(
            finished.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            who,
            where,
            MetricText.Duration(duration),
            MetricText.Tokens(attempt.Own.InputTokens),
            MetricText.Tokens(attempt.Own.OutputTokens),
            MetricText.Cost(attempt.Own.CostUsd),
            "Contexto máximo: " + Peak(attempt.ContextPeak, thresholds),
            perModel)
        {
            ContextPeakBar = PeakBar(attempt.ContextPeak, thresholds),
        };
    }

    private static string Peak(ContextPeakFigures? peak, ContextHealthThresholds thresholds) =>
        peak is null ? "sin dato" : ContextText.Read(peak.Model, peak.UsedTokens, peak.ContextWindow, thresholds).Text;

    /// <summary>
    /// The bar of a context peak (V39): read as every context bar is (<see cref="MetricBars"/>), judged with the thresholds of
    /// the settings; without a peak, an empty track that says «sin dato».
    /// </summary>
    private static MetricBarReading PeakBar(ContextPeakFigures? peak, ContextHealthThresholds thresholds) =>
        peak is null ? MetricBars.Empty(PeakLabel, "sin dato") : MetricBars.Context(PeakLabel, peak.Model, peak.UsedTokens, peak.ContextWindow, thresholds);

    private static string ReconciliationText(UsageReconciliation journal)
    {
        if (journal.JournalPrompts is not { } prompts)
        {
            return "Sin bitácora que conciliar en el periodo.";
        }

        var said = string.Create(CultureInfo.InvariantCulture, $"La bitácora del periodo tiene {prompts} prompts y el registro, {journal.RecordedAttempts} intentos");
        return journal.Difference switch
        {
            0 => said + ": coinciden.",
            var difference => string.Create(CultureInfo.InvariantCulture, $"{said}: {Math.Abs(difference ?? 0)} de diferencia, que se muestran y no se cuentan."),
        };
    }

    internal static string DimensionLabel(UsageDimension dimension) => dimension switch
    {
        UsageDimension.Provider => "Proveedor",
        UsageDimension.Vendor => "Vendor",
        UsageDimension.Model => "Modelo",
        UsageDimension.Session => "Sesión",
        UsageDimension.Run => "Ejecución",
        UsageDimension.Task => "Tarea",
        UsageDimension.Agent => "Agente",
        UsageDimension.Purpose => "Propósito",
        UsageDimension.Project => "Proyecto",
        UsageDimension.Day => "Día",
        UsageDimension.Week => "Semana",
        UsageDimension.Month => "Mes",
        _ => dimension.ToString(),
    };

    private static string? PurposeLabel(AgentRunPurpose? purpose) => purpose switch
    {
        AgentRunPurpose.Planner => "Planificador",
        AgentRunPurpose.Executor => "Ejecutor",
        AgentRunPurpose.Judge => "Juez",
        AgentRunPurpose.SkillRecommendation => "Recomendación de skills",
        AgentRunPurpose.DailySummary => "Resumen diario",

        // Phase 5 (docs/15 D-44): the turns and the judge of a debate, and the judge of a whole-project audit.
        AgentRunPurpose.DebateParticipant => "Participante de debate",
        AgentRunPurpose.DebateJudge => "Juez de debate",
        AgentRunPurpose.ProjectAudit => "Auditoría del proyecto",

        // Phase 14 (docs/17 §8.2): the agent of the help and the draft of a document for clients, from the resources (ADR-0054).
        AgentRunPurpose.Help => Texts.Get("Usage_Purpose_Help"),
        AgentRunPurpose.ClientDocumentDraft => Texts.Get("Usage_Purpose_ClientDocumentDraft"),
        _ => null,
    };

    private static string OutcomeLabel(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Succeeded => "correcto",
        AttemptOutcome.Failed => "fallido",
        AttemptOutcome.Cancelled => "cancelado",
        AttemptOutcome.LimitReached => "límite alcanzado",
        AttemptOutcome.RateLimited => "límite de uso del proveedor",
        AttemptOutcome.Interrupted => "interrumpido",
        _ => outcome.ToString(),
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
