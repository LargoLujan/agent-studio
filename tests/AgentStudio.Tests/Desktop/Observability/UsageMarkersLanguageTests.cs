using System.Globalization;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// Review of WP5 (docs/16 §4.6): the markers of the aggregator («sin sesión», «sin tarea», the model and the purpose that an attempt does not
/// have), the reasons a project was left out and the warnings of the reconciliation are written in the language of the source, and the panel
/// says them in the language in use. The raw values stay as they are: the export keeps them.
/// </summary>
public sealed class UsageMarkersLanguageTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    private static readonly UsageAttempt[] Bare =
    [
        UsageSamples.ClaudeAttempt with { SessionId = null, TaskId = null, Purpose = null, ReportedModel = null, ConfiguredModel = null },
    ];

    private static UsageReport Report() =>
        UsageSamples.Empty with
        {
            Attempts = Bare,
            Groups = UsageAggregator.Group(Bare, new UsageQuery(Now.AddDays(-30), Now.AddDays(1), TimeZoneInfo.Utc, [UsageDimension.Session, UsageDimension.Task, UsageDimension.Purpose, UsageDimension.Model])),
            Excluded =
            [
                new ExcludedProject(@"C:\otros\viejo", "p-9", "El registro no verifica (entrada 12): la cadena se rompe"),
                new ExcludedProject(@"C:\otros\movido", "p-8", "El ancla de esta ubicación es de otro proyecto: no se cuenta."),
                new ExcludedProject(@"C:\otros\raro", null, "un motivo que el proyector todavía no conoce"),
            ],
            Journal = new UsageReconciliation(
                25,
                23,
                [
                    JournalReconciler.NoJournal,
                    @"C:\proyectos\calc: La entrada 7 cierra el intento 2 de la ejecución (20261018-a1b2c3) sin su AgentRunStarted: no se cuenta.",
                    "Bitácora: 25 prompts; registro: 1 intento. Diferencia de 24 prompts, que no se suma (pruebas en vivo, versiones anteriores o intentos que no llegaron al proveedor).",
                ]),
        };

    private static UsageDashboardViewModel Dashboard()
    {
        var store = new RecordingSettingsStore();
        var clock = new FakeClock(Now);
        return new UsageDashboardViewModel(
            new FakeUsageReport(Report),
            new FakeUsageExporter(),
            new FakeSaveFilePicker(null),
            store,
            clock,
            new QuotaViewModel(new FakeQuotaObservations(), null, clock),
            new ObservabilitySettingsViewModel(store));
    }

    [Fact]
    public async Task The_markers_and_the_notes_are_said_in_english_when_the_language_is_english()
    {
        using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo("en")));
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        var key = dashboard.Groups[1].Key;
        Assert.Contains("no session", key, StringComparison.Ordinal);
        Assert.Contains("no task", key, StringComparison.Ordinal);
        Assert.Contains("no purpose", key, StringComparison.Ordinal);
        Assert.Contains("CLI default (not exposed)", key, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageAggregator.NoSession, key, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageAggregator.NoTask, key, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageAttemptFacts.DefaultModel, key, StringComparison.Ordinal);

        Assert.Equal(
            [
                @"C:\otros\viejo: The log does not verify (entry 12): la cadena se rompe",
                @"C:\otros\movido: The anchor of this location belongs to another project: it is not counted.",
                @"C:\otros\raro: un motivo que el proyector todavía no conoce",
            ],
            dashboard.Excluded);
        Assert.Equal(
            [
                "No journal for the period.",
                @"C:\proyectos\calc: Entry 7 closes attempt 2 of the run (20261018-a1b2c3) without its AgentRunStarted: it is not counted.",
                "Journal: 25 prompts; log: 1 attempt. Difference of 24 prompts, which is not added up (live tests, earlier versions or attempts that never reached the provider).",
            ],
            dashboard.ReconciliationWarnings);
    }

    [Fact]
    public async Task In_castilian_the_notes_stay_as_the_projection_wrote_them_and_the_raw_values_are_untouched()
    {
        var dashboard = Dashboard();

        await dashboard.SetProjectAsync(Root);

        Assert.Equal(Report().Journal.Warnings, dashboard.ReconciliationWarnings);
        Assert.Equal(Report().Excluded.Select(e => $"{e.Root}: {e.Reason}"), dashboard.Excluded);
        Assert.Contains(UsageAggregator.NoSession, dashboard.Groups[1].Key, StringComparison.Ordinal);
        Assert.Contains(UsageAttemptFacts.DefaultModel, dashboard.Groups[1].Key, StringComparison.Ordinal);

        // The aggregator keeps its raw values for the export.
        Assert.Equal(UsageAggregator.NoSession, Report().Groups[1].Key[0].Value);
    }
}
