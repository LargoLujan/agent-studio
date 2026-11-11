using System.IO;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// «Trabajo desatendido» in «Ajustes» (docs/16 D-13, D-14 and D-40; docs/04 §18.6): what closing the window does and keeping
/// the computer awake while there is work, saved by the «Guardar» of «Ajustes» as preferences through the store of the
/// settings, never over the rest; and the settings of Windows Hello and the lock, which come with it.
/// </summary>
public sealed class UnattendedSettingsViewModelTests
{
    /// <summary>«Ajustes» with this section and «Contexto y cuota», over the same store.</summary>
    private static SettingsDocumentViewModel Ajustes(IAppSettingsStore store, UnattendedSettingsViewModel settings) =>
        new(store, new ObservabilitySettingsViewModel(store), settings);

    [Fact]
    public void It_shows_what_is_stored_and_the_defaults_are_ask_and_no_keeping_awake()
    {
        var byDefault = new UnattendedSettingsViewModel(new InMemorySettingsStore());
        var stored = new UnattendedSettingsViewModel(new InMemorySettingsStore(AppSettings.Default with { CloseWindow = CloseWindowAction.Exit, PreventSleepWhileWorking = true }));

        Assert.Equal(CloseWindowAction.Ask, byDefault.CloseWindow);
        Assert.False(byDefault.PreventSleepWhileWorking);
        Assert.Equal([CloseWindowAction.Ask, CloseWindowAction.KeepInTray, CloseWindowAction.Exit], byDefault.CloseWindowChoices.Select(c => c.Value));
        Assert.Single(byDefault.CloseWindowChoices, c => c.IsSelected);
        Assert.Equal(CloseWindowAction.Exit, stored.CloseWindow);
        Assert.True(stored.PreventSleepWhileWorking);
    }

    [Fact]
    public void Saving_writes_both_preferences_keeps_the_rest_and_says_so()
    {
        var store = new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(@"C:\proyectos\tienda") with { IdleLockEnabled = true, IdleLockMinutes = 30 });
        var settings = new UnattendedSettingsViewModel(store);
        var ajustes = Ajustes(store, settings);
        var saved = 0;
        settings.Saved += (_, _) => saved++;

        settings.CloseWindowChoices[1].IsSelected = true;
        settings.CloseWindowChoices[0].IsSelected = false;
        settings.PreventSleepWhileWorking = true;
        ajustes.SaveCommand.Execute(null);

        Assert.Equal(CloseWindowAction.KeepInTray, store.Current.CloseWindow);
        Assert.True(store.Current.PreventSleepWhileWorking);
        Assert.Equal((true, 30), (store.Current.IdleLockEnabled, store.Current.IdleLockMinutes));
        Assert.Equal(@"C:\proyectos\tienda", store.Current.LastProjectPath);
        Assert.StartsWith("Ajustes guardados", ajustes.Notice, StringComparison.Ordinal);
        Assert.Null(ajustes.ErrorMessage);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void A_store_that_cannot_write_saves_nothing_and_says_why()
    {
        var store = new FailingStore();
        var settings = new UnattendedSettingsViewModel(store);
        var ajustes = Ajustes(store, settings);
        var saved = 0;
        settings.Saved += (_, _) => saved++;

        settings.Choose(CloseWindowAction.Exit);
        ajustes.SaveCommand.Execute(null);

        Assert.Equal("No se pudieron guardar los ajustes (The disk is full.). Vuelve a intentarlo.", ajustes.ErrorMessage);
        Assert.Null(ajustes.Notice);
        Assert.Equal(0, saved);
        Assert.Equal(CloseWindowAction.Exit, settings.CloseWindow);
    }

    [Fact]
    public void The_settings_of_windows_hello_and_the_lock_are_part_of_the_section()
    {
        var store = new InMemorySettingsStore();
        var lockSettings = new LockSettingsViewModel(store);

        Assert.Same(lockSettings, new UnattendedSettingsViewModel(store, lockSettings).Lock);
        Assert.Null(new UnattendedSettingsViewModel(store).Lock);
    }

    private sealed class FailingStore : IAppSettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public void Save(AppSettings settings) => throw new IOException("The disk is full.");
    }
}
