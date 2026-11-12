using System.IO;
using System.Text;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The «Consumo» panel (docs/14 F4-1 and F4-2, D-03 to D-07) over a fake <see cref="IUsageReport"/>: the query of the
/// period in the local zone, grouped by the dimensions the user picks, for the open project or every recent one; every
/// figure with its origin; what is left out and the reconciliation with the journal; and the export to CSV and JSON
/// written atomically to the file chosen with «Guardar como».
/// </summary>
public sealed class UsageDashboardViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    private static UsageDashboardViewModel Dashboard(
        FakeUsageReport? report = null,
        FakeUsageExporter? exporter = null,
        FakeSaveFilePicker? picker = null,
        RecordingSettingsStore? settings = null,
        FakeQuotaReader? codex = null,
        bool composeReport = true)
    {
        var store = settings ?? new RecordingSettingsStore();
        var clock = new FakeClock(Now);
        return new UsageDashboardViewModel(
            composeReport ? report ?? new FakeUsageReport(() => UsageSamples.Day1) : null,
            exporter ?? new FakeUsageExporter(),
            picker ?? new FakeSaveFilePicker(null),
            store,
            clock,
            new QuotaViewModel(new FakeQuotaObservations(), codex, clock),
            new ObservabilitySettingsViewModel(store));
    }

    [Fact]
    public async Task Today_in_the_local_zone_grouped_by_provider_and_model_is_what_it_asks_first()
    {
        var report = new FakeUsageReport();
        var dashboard = Dashboard(report);

        await dashboard.SetProjectAsync(Root);

        var (roots, query) = Assert.Single(report.Queries);
        Assert.Equal([Root], roots);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.FromHours(2)), query.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), query.To);
        Assert.Equal(TimeSpan.FromHours(2), query.Zone.BaseUtcOffset);
        Assert.Equal([UsageDimension.Provider, UsageDimension.Model], query.GroupBy);
        Assert.Null(query.Filter);
    }

    [Fact]
    public void Every_dimension_the_panel_offers_can_be_grouped_by_the_aggregator()
    {
        var dashboard = Dashboard();

        foreach (var option in dashboard.Dimensions)
        {
            var query = new UsageQuery(Now.AddDays(-1), Now.AddDays(1), TimeZoneInfo.Utc, [option.Dimension]);
            var groups = UsageAggregator.Group([UsageSamples.ClaudeAttempt], query);
            Assert.True(groups.Count >= 1, option.Dimension.ToString());
        }
    }

    [Fact]
    public async Task The_chosen_dimensions_and_period_go_to_the_query_in_a_fixed_order()
    {
        var report = new FakeUsageReport();
        var dashboard = Dashboard(report);
        await dashboard.SetProjectAsync(Root);

        dashboard.Dimensions.Single(d => d.Dimension == UsageDimension.Day).IsSelected = true;
        dashboard.Dimensions.Single(d => d.Dimension == UsageDimension.Session).IsSelected = true;
        dashboard.Dimensions.Single(d => d.Dimension == UsageDimension.Project).IsSelected = true;
        dashboard.SelectedPeriod = dashboard.Periods.Single(p => p.Value == UsagePeriod.Last7Days);
        await dashboard.RefreshCommand.ExecuteAsync(null);

        var query = report.Queries[^1].Query;
        Assert.Equal([UsageDimension.Provider, UsageDimension.Model, UsageDimension.Session, UsageDimension.Project, UsageDimension.Day], query.GroupBy);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(2)), query.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), query.To);
    }

    [Fact]
    public async Task This_month_runs_from_its_first_day_to_the_first_of_the_next()
    {
        var report = new FakeUsageReport();
        var dashboard = Dashboard(report);
        await dashboard.SetProjectAsync(Root);

        dashboard.SelectedPeriod = dashboard.Periods.Single(p => p.Value == UsagePeriod.ThisMonth);
        await dashboard.RefreshCommand.ExecuteAsync(null);

        var query = report.Queries[^1].Query;
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), query.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(2)), query.To);
    }

    [Fact]
    public async Task Every_recent_project_can_be_asked_with_the_open_one_first()
    {
        var report = new FakeUsageReport();
        var settings = new RecordingSettingsStore(AppSettings.Default with { RecentProjects = [@"C:\proyectos\web", Root, @"C:\proyectos\datos"] });
        var dashboard = Dashboard(report, settings: settings);
        await dashboard.SetProjectAsync(Root);

        dashboard.SelectedScope = dashboard.Scopes.Single(s => s.Value == UsageScope.RecentProjects);
        await dashboard.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([Root, @"C:\proyectos\web", @"C:\proyectos\datos"], report.Queries[^1].Roots);
    }

    [Fact]
    public async Task Every_figure_of_a_group_carries_its_origin()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var claude = dashboard.Groups[0];
        Assert.Equal("Proveedor: claude-code · Modelo: claude-sonnet-5-5", claude.Key);
        Assert.Equal("14 · exacto · medido por Agent Studio", claude.Attempts.Text);
        Assert.Equal("41 min · exacto · medido por Agent Studio", claude.Duration.Text);
        Assert.Equal("1.200.000 · exacto · informado por el programa del agente", claude.Input.Text);
        Assert.Equal("900.000 · exacto · informado por el programa del agente", claude.CacheRead.Text);
        Assert.Equal("250.000 · exacto · informado por el programa del agente", claude.CacheWrite.Text);
        Assert.Equal("96.000 · exacto · informado por el programa del agente", claude.Output.Text);
        Assert.Equal("no expuesto", claude.Reasoning.Text);
        Assert.Equal("~3,25 USD · estimado por el programa del agente (precio de lista)", claude.Cost.Text);
        Assert.Equal("0 · exacto · medido por Agent Studio", claude.RateLimited.Text);
        Assert.Equal("30 % · exacto · informado por el programa del agente · 300.000 de 1.000.000 tokens · claude-sonnet-5-5 · sano", claude.ContextPeak);
        Assert.Equal("0 · exacto · informado por el programa del agente", claude.Compactions.Text);

        var codex = dashboard.Groups[1];
        Assert.Equal("Proveedor: codex-cli · Modelo: predeterminado del programa del agente (no expuesto)", codex.Key);
        Assert.Equal("640.000 · parcial (8 de 9) · exacto · informado por el programa del agente", codex.Input.Text);
        Assert.Equal("no expuesto", codex.Cost.Text);
        Assert.Equal("1 · exacto · medido por Agent Studio", codex.RateLimited.Text);
        Assert.Equal("no expuesto", codex.ContextPeak);
        Assert.Equal("no expuesto", codex.Compactions.Text);
    }

    [Fact]
    public async Task A_group_without_a_key_is_the_total()
    {
        var dashboard = Dashboard(new FakeUsageReport(() => UsageSamples.Empty with { Groups = [UsageSamples.Group(UsageSamples.ClaudeTotals)] }));

        await dashboard.SetProjectAsync(Root);

        Assert.Equal("Total", Assert.Single(dashboard.Groups).Key);
    }

    [Fact]
    public async Task The_context_peak_of_an_attempt_is_never_shown_as_exact_when_its_window_is_an_estimate()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var attempt = dashboard.Attempts[0];
        Assert.Equal("Contexto máximo: ~30 % · estimado por Agent Studio · 300.000 de ~1.000.000 tokens · claude-sonnet-5-5 · sano", attempt.ContextPeak);
        Assert.DoesNotContain("exacto", attempt.ContextPeak, StringComparison.Ordinal);
    }

    /// <summary>
    /// V39: every percentage is a bar (docs/18 P3), the context peak of a group and of an attempt too: the common bar, with its
    /// origin and its level, the estimate with «~», and an empty track that says why when there is no figure.
    /// </summary>
    [Fact]
    public async Task The_context_peak_of_a_group_and_of_an_attempt_is_a_bar_with_its_origin_and_level()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var claude = dashboard.Groups[0].ContextPeakBar;
        Assert.Equal(("Contexto máximo", "30 %", "exacto · informado por el programa del agente", "sano"), (claude.Label, claude.ValueText, claude.Origin, claude.LevelText));
        Assert.Equal(30, claude.Value!.Value, 6);
        Assert.Equal(MetricBarLevel.Healthy, claude.Level);
        Assert.Contains("300.000 de 1.000.000 tokens", claude.Detail, StringComparison.Ordinal);

        var codex = dashboard.Groups[1].ContextPeakBar;
        Assert.False(codex.HasValue);
        Assert.Equal("no expuesto", codex.ValueText);

        var estimated = dashboard.Attempts[0].ContextPeakBar;
        Assert.Equal("~30 %", estimated.ValueText);
        Assert.True(estimated.IsEstimate);
        Assert.Equal("estimado por Agent Studio", estimated.Origin);

        var none = dashboard.Attempts[1].ContextPeakBar;
        Assert.False(none.HasValue);
        Assert.Equal("sin dato", none.ValueText);
    }

    [Fact]
    public async Task An_attempt_lists_who_when_and_how_it_ended_with_its_models()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var attempt = dashboard.Attempts[0];
        Assert.Equal("18/10/2026 09:33:05", attempt.When);
        Assert.Equal("20261018-c4a1f2 · T1 · intento 1 · Ejecutor · ejecutor", attempt.Who);
        Assert.Equal("claude-code · claude-sonnet-5-5 · correcto", attempt.Where);
        Assert.Equal("3 min · exacto · medido por Agent Studio", attempt.Duration.Text);
        Assert.Equal("31.000 · exacto · informado por el programa del agente", attempt.Input.Text);
        Assert.Equal("~0,12 USD · estimado por el programa del agente (precio de lista)", attempt.Cost.Text);
        Assert.Equal(
            [
                "claude-sonnet-5-5: entrada 30.000 · exacto · informado por el programa del agente; salida 1.100 · exacto · informado por el programa del agente; coste ~0,11 USD · estimado por el programa del agente (precio de lista)",
                "claude-haiku-4-5-20251001: entrada 1.000 · exacto · informado por el programa del agente; salida 100 · exacto · informado por el programa del agente; coste ~0,01 USD · estimado por el programa del agente (precio de lista)",
            ],
            attempt.PerModel);
    }

    [Fact]
    public async Task An_attempt_recorded_before_phase_4_shows_no_breakdown_no_model_and_no_duration()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var attempt = dashboard.Attempts[1];
        Assert.Equal(["sin desglose (registro anterior a la Fase 4)"], attempt.PerModel);
        Assert.Equal("codex-cli · predeterminado del programa del agente (no expuesto) · límite de uso del proveedor", attempt.Where);
        Assert.Equal("sin dato", attempt.Duration.Text);
        Assert.Equal("no expuesto", attempt.Cost.Text);
        Assert.Equal("Contexto máximo: sin dato", attempt.ContextPeak);
    }

    [Fact]
    public async Task The_projects_left_out_and_the_reconciliation_with_the_journal_are_shown()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        Assert.Equal([@"C:\otros\viejo: su registro no verifica: la cadena se rompe en la entrada 12"], dashboard.Excluded);
        Assert.Equal("La bitácora del periodo tiene 25 prompts y el registro, 23 intentos: 2 de diferencia, que se muestran y no se cuentan.", dashboard.Reconciliation);
        Assert.Equal(["2 prompts de la bitácora no tienen intento en el registro: pruebas en vivo o una versión anterior."], dashboard.ReconciliationWarnings);
    }

    [Fact]
    public async Task Export_to_CSV_writes_what_the_exporter_wrote_to_the_chosen_file_and_nothing_else()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("consumo.csv");
        var exporter = new FakeUsageExporter();
        var picker = new FakeSaveFilePicker(target);
        var dashboard = Dashboard(exporter: exporter, picker: picker);
        await dashboard.SetProjectAsync(Root);

        await dashboard.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal(("Exportar el consumo a CSV", "consumo-2026-10-18.csv", "CSV (*.csv)|*.csv"), Assert.Single(picker.Asked));
        var (format, report) = Assert.Single(exporter.Calls);
        Assert.Equal("csv", format);
        Assert.Same(UsageSamples.Day1, report);
        Assert.Equal(new UTF8Encoding(false).GetBytes("\uFEFFattempt,input\r\n1,1200\r\n"), File.ReadAllBytes(target));
        Assert.Equal(["consumo.csv"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
        Assert.Equal($"Exportado a {target}", dashboard.ExportStatus);
    }

    [Fact]
    public async Task Export_to_JSON_writes_the_json_of_the_exporter()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("consumo.json");
        var exporter = new FakeUsageExporter();
        var picker = new FakeSaveFilePicker(target);
        var dashboard = Dashboard(exporter: exporter, picker: picker);
        await dashboard.SetProjectAsync(Root);

        await dashboard.ExportJsonCommand.ExecuteAsync(null);

        Assert.Equal(("Exportar el consumo a JSON", "consumo-2026-10-18.json", "JSON (*.json)|*.json"), Assert.Single(picker.Asked));
        Assert.Equal("json", Assert.Single(exporter.Calls).Format);
        Assert.Equal("{\"schema\":\"agentstudio-usage/1\"}", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_cancelled_save_as_writes_nothing()
    {
        var exporter = new FakeUsageExporter();
        var dashboard = Dashboard(exporter: exporter, picker: new FakeSaveFilePicker(null));
        await dashboard.SetProjectAsync(Root);

        await dashboard.ExportCsvCommand.ExecuteAsync(null);

        Assert.Empty(exporter.Calls);
        Assert.Null(dashboard.ExportStatus);
    }

    [Fact]
    public async Task A_failed_export_leaves_the_existing_file_as_it_was_and_no_temporary_file()
    {
        using var temp = new TempDirectory();
        var target = temp.WriteFile("consumo.csv", "lo de antes");
        var exporter = new FakeUsageExporter { Failure = new IOException("Disco lleno.") };
        var dashboard = Dashboard(exporter: exporter, picker: new FakeSaveFilePicker(target));
        await dashboard.SetProjectAsync(Root);

        await dashboard.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal("lo de antes", File.ReadAllText(target));
        Assert.Equal(["consumo.csv"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
        Assert.Equal("No se pudo exportar: Disco lleno.", dashboard.ErrorMessage);
    }

    [Fact]
    public async Task Nothing_is_exported_before_a_report_is_shown()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(null);

        Assert.False(dashboard.ExportCsvCommand.CanExecute(null));
        Assert.False(dashboard.ExportJsonCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_a_project_the_open_project_scope_asks_for_one_and_asks_nothing()
    {
        var report = new FakeUsageReport();
        var dashboard = Dashboard(report);

        await dashboard.SetProjectAsync(null);

        Assert.Empty(report.Queries);
        Assert.Equal("Abre una carpeta para ver su consumo, o elige «Todos los proyectos recientes».", dashboard.StatusMessage);
    }

    [Fact]
    public async Task Without_the_usage_report_composed_the_panel_says_so()
    {
        var dashboard = Dashboard(composeReport: false);

        await dashboard.SetProjectAsync(Root);

        Assert.Empty(dashboard.Groups);
        Assert.Equal("El consumo no está disponible en esta composición: falta la proyección del registro.", dashboard.StatusMessage);
    }

    [Fact]
    public async Task A_report_that_fails_is_shown_and_not_thrown()
    {
        var dashboard = Dashboard(new FakeUsageReport(() => throw new InvalidOperationException("El registro no se puede leer.")));

        await dashboard.SetProjectAsync(Root);

        Assert.Equal("No se pudo calcular el consumo: El registro no se puede leer.", dashboard.ErrorMessage);
        Assert.Empty(dashboard.Groups);
    }

    [Fact]
    public async Task Saving_the_Codex_opt_in_in_ajustes_enables_the_Codex_read()
    {
        var store = new RecordingSettingsStore();
        var reader = new FakeQuotaReader();
        var dashboard = Dashboard(settings: store, codex: reader);
        await dashboard.SetProjectAsync(Root);
        Assert.False(dashboard.Quota.ReadCodexCommand.CanExecute(null));

        dashboard.Preferences.CodexQuotaReadEnabled = true;
        SaveInAjustes(store, dashboard.Preferences);

        Assert.True(dashboard.Quota.ReadCodexCommand.CanExecute(null));
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Saving_other_thresholds_in_ajustes_judges_the_context_peaks_again()
    {
        var store = new RecordingSettingsStore();
        var dashboard = Dashboard(settings: store);
        await dashboard.SetProjectAsync(Root);
        Assert.EndsWith("· sano", dashboard.Groups[0].ContextPeak, StringComparison.Ordinal);

        dashboard.Preferences.WarningText = "20";
        dashboard.Preferences.CompressText = "25";
        SaveInAjustes(store, dashboard.Preferences);

        Assert.EndsWith("· compresión recomendada", dashboard.Groups[0].ContextPeak, StringComparison.Ordinal);
        Assert.Equal("compresión recomendada", dashboard.Groups[0].ContextPeakBar.LevelText);
    }

    /// <summary>The «Guardar» of «Ajustes», over the preferences «Consumo» judges its report with.</summary>
    private static void SaveInAjustes(RecordingSettingsStore store, ObservabilitySettingsViewModel preferences) =>
        new AgentStudio.Desktop.Settings.SettingsDocumentViewModel(store, preferences).SaveCommand.Execute(null);
}
