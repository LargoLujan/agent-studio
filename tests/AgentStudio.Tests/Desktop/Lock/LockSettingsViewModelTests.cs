using System.IO;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// «Windows Hello y bloqueo» of «Ajustes» (docs/16 D-13, docs/04 §18.6): Hello in the critical approvals, on by default and it
/// can be turned off; the idle lock, off by default, from 1 to 1 440 minutes. Saved by the «Guardar» of «Ajustes» as
/// preferences, never over the rest.
/// </summary>
public sealed class LockSettingsViewModelTests
{
    private sealed class FailingStore : IAppSettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public void Save(AppSettings settings) => throw new IOException("Disco lleno.");

        public void SavePreferences(AppSettings settings) => throw new IOException("Disco lleno.");
    }

    /// <summary>«Ajustes» with this section and «Contexto y límite de uso», over the same store.</summary>
    private static SettingsDocumentViewModel Ajustes(IAppSettingsStore store, LockSettingsViewModel settings) =>
        new(store, new ObservabilitySettingsViewModel(store), lockSettings: settings);

    [Fact]
    public void It_shows_what_is_in_force_hello_on_and_the_idle_lock_off_by_default()
    {
        var settings = new LockSettingsViewModel(new InMemorySettingsStore());

        Assert.True(settings.HelloForCriticalApprovals);
        Assert.False(settings.IdleLockEnabled);
        Assert.Equal("15", settings.IdleLockMinutesText);
        Assert.Null(settings.ErrorMessage);
    }

    [Fact]
    public void Saving_keeps_the_rest_of_the_settings_and_says_so()
    {
        var store = new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(@"C:\proyectos\calc") with { CodexQuotaReadEnabled = true });
        var settings = new LockSettingsViewModel(store);
        var ajustes = Ajustes(store, settings);
        var saved = 0;
        settings.Saved += (_, _) => saved++;

        settings.HelloForCriticalApprovals = false;
        settings.IdleLockEnabled = true;
        settings.IdleLockMinutesText = " 5 ";
        ajustes.SaveCommand.Execute(null);

        Assert.Equal((false, true, 5), (store.Current.WindowsHelloForImportantApprovals, store.Current.IdleLockEnabled, store.Current.IdleLockMinutes));
        Assert.True(store.Current.CodexQuotaReadEnabled);
        Assert.Equal(@"C:\proyectos\calc", store.Current.LastProjectPath);
        Assert.StartsWith("Ajustes guardados", ajustes.Notice, StringComparison.Ordinal);
        Assert.Equal("5", settings.IdleLockMinutesText);
        Assert.Equal(1, saved);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("quince")]
    [InlineData("")]
    [InlineData("-3")]
    public void A_time_out_of_range_is_not_saved_and_says_why(string minutes)
    {
        var store = new InMemorySettingsStore();
        var settings = new LockSettingsViewModel(store) { IdleLockEnabled = true, IdleLockMinutesText = minutes };
        var ajustes = Ajustes(store, settings);

        ajustes.SaveCommand.Execute(null);

        Assert.Equal(0, store.Saves);
        Assert.Equal($"No se guardó el bloqueo: «{minutes.Trim()}» no es un número entero de minutos entre 1 y 1440. Escribe un número válido.", settings.ErrorMessage);
        Assert.Null(ajustes.Notice);
    }

    [Fact]
    public void A_store_that_cannot_write_says_so_and_nothing_changes()
    {
        var store = new FailingStore();
        var settings = new LockSettingsViewModel(store) { IdleLockEnabled = true };
        var ajustes = Ajustes(store, settings);

        ajustes.SaveCommand.Execute(null);

        Assert.Equal("No se pudieron guardar los ajustes (Disco lleno.). Vuelve a intentarlo.", ajustes.ErrorMessage);
        Assert.Null(ajustes.Notice);
        Assert.True(settings.IdleLockEnabled);
    }
}
