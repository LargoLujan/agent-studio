using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// «Contexto y límite de uso» of «Ajustes» (docs/14 D-11 and D-15): the context thresholds, validated by the rule of the evaluator
/// before anything is saved, and the opt-in that lets Agent Studio read the Codex quota. They are saved by the «Guardar» of
/// «Ajustes» as the preferences of the settings, never over the rest.
/// </summary>
public sealed class ObservabilitySettingsViewModelTests
{
    /// <summary>«Ajustes» with this section alone, as a composition without Phase 8 has it.</summary>
    private static SettingsDocumentViewModel Ajustes(IAppSettingsStore store, ObservabilitySettingsViewModel settings) => new(store, settings);

    [Fact]
    public void It_shows_the_stored_thresholds_and_opt_in()
    {
        var store = new RecordingSettingsStore(AppSettings.Default.WithContextThresholds(60, 80, 90) with { CodexQuotaReadEnabled = true });

        var settings = new ObservabilitySettingsViewModel(store);

        Assert.Equal(("60", "80", "90"), (settings.WarningText, settings.CompressText, settings.CriticalText));
        Assert.True(settings.CodexQuotaReadEnabled);
        Assert.Equal((0.60, 0.80, 0.90), (settings.Thresholds.Warning, settings.Thresholds.Compress, settings.Thresholds.Critical));
        Assert.True(settings.CodexOptedIn);
    }

    [Fact]
    public void By_default_it_shows_70_85_95_and_the_Codex_quota_off()
    {
        var settings = new ObservabilitySettingsViewModel(new RecordingSettingsStore());

        Assert.Equal(("70", "85", "95"), (settings.WarningText, settings.CompressText, settings.CriticalText));
        Assert.False(settings.CodexQuotaReadEnabled);
        Assert.False(settings.CodexOptedIn);
    }

    [Fact]
    public void Saving_valid_thresholds_and_the_opt_in_writes_them_as_preferences_and_says_so()
    {
        var store = new RecordingSettingsStore(AppSettings.Default with { RecentProjects = [@"C:\proyectos\calc"] });
        var settings = new ObservabilitySettingsViewModel(store);
        var ajustes = Ajustes(store, settings);
        var saved = 0;
        settings.Saved += (_, _) => saved++;

        settings.WarningText = " 65 ";
        settings.CompressText = "80";
        settings.CriticalText = "100";
        settings.CodexQuotaReadEnabled = true;
        ajustes.SaveCommand.Execute(null);

        Assert.Equal(["preferences"], store.Writes);
        Assert.Equal((65, 80, 100), (store.Current.ContextWarningPercent, store.Current.ContextCompressPercent, store.Current.ContextCriticalPercent));
        Assert.True(store.Current.CodexQuotaReadEnabled);
        Assert.Equal([@"C:\proyectos\calc"], store.Current.RecentProjects);
        Assert.Equal(1, saved);
        Assert.Equal((0.65, 0.80, 1.00), (settings.Thresholds.Warning, settings.Thresholds.Compress, settings.Thresholds.Critical));
        Assert.True(settings.CodexOptedIn);
        Assert.Null(settings.ErrorMessage);
        Assert.StartsWith("Ajustes guardados", ajustes.Notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("80", "70", "95")]
    [InlineData("70", "70", "95")]
    [InlineData("0", "85", "95")]
    [InlineData("70", "85", "101")]
    public void Thresholds_out_of_order_or_range_are_refused_and_nothing_is_saved(string warning, string compress, string critical)
    {
        var store = new RecordingSettingsStore();
        var settings = new ObservabilitySettingsViewModel(store);
        var saved = 0;
        settings.Saved += (_, _) => saved++;

        settings.WarningText = warning;
        settings.CompressText = compress;
        settings.CriticalText = critical;
        settings.CodexQuotaReadEnabled = true;
        Ajustes(store, settings).SaveCommand.Execute(null);

        Assert.Empty(store.Writes);
        Assert.Equal(0, saved);
        Assert.Equal("No se guardaron los umbrales. Cada uno debe ser mayor que el anterior, y el crítico no puede pasar de 100: 0 < aviso < compresión < crítico ≤ 100.", settings.ErrorMessage);
        Assert.Equal((0.70, 0.85, 0.95), (settings.Thresholds.Warning, settings.Thresholds.Compress, settings.Thresholds.Critical));
        Assert.False(settings.CodexOptedIn);
    }

    [Fact]
    public void A_threshold_that_is_not_a_whole_number_is_refused()
    {
        var store = new RecordingSettingsStore();
        var settings = new ObservabilitySettingsViewModel(store);

        settings.CompressText = "ochenta";
        Ajustes(store, settings).SaveCommand.Execute(null);

        Assert.Empty(store.Writes);
        Assert.Equal("No se guardaron los umbrales. «ochenta» no es un número entero de 1 a 100: corrígelo.", settings.ErrorMessage);
    }

    [Fact]
    public void A_store_that_fails_is_shown_and_the_previous_values_stay_in_force()
    {
        var store = new FailingStore();
        var settings = new ObservabilitySettingsViewModel(store);
        var ajustes = Ajustes(store, settings);

        settings.WarningText = "60";
        ajustes.SaveCommand.Execute(null);

        Assert.Equal("No se pudieron guardar los ajustes (Disco lleno.). Vuelve a intentarlo.", ajustes.ErrorMessage);
        Assert.Equal(0.70, settings.Thresholds.Warning);
    }

    private sealed class FailingStore : IAppSettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public void Save(AppSettings settings) => throw new System.IO.IOException("Disco lleno.");

        public void SavePreferences(AppSettings settings) => throw new System.IO.IOException("Disco lleno.");
    }
}
