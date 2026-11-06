using System.IO;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The settings of Phase 13 (docs/17 §9.3, P0): the language of the interface (D-49), «Reducir movimiento» (D-15) and the setting
/// of Windows Hello with its new name, «Pedir Windows Hello también en las aprobaciones importantes» (D-38). A file written
/// before Phase 13 is read well: the old name of the Hello setting is read under the new one, and the new settings take their
/// defaults.
/// </summary>
public sealed class AppSettingsPhase13Tests
{
    [Fact]
    public void A_file_of_before_phase_13_is_read_with_its_hello_setting_under_the_new_name_and_the_new_settings_at_their_defaults()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(
            paths.SettingsFile,
            """{"lastProjectPath":"C:\\p1","codexQuotaReadEnabled":true,"closeWindow":"KeepInTray","windowsHelloForCriticalApprovals":false,"idleLockEnabled":true,"idleLockMinutes":20}""");

        var loaded = new JsonAppSettingsStore(paths, null).Load();

        Assert.False(loaded.WindowsHelloForImportantApprovals);
        Assert.Equal(@"C:\p1", loaded.LastProjectPath);
        Assert.True(loaded.CodexQuotaReadEnabled);
        Assert.Equal(CloseWindowAction.KeepInTray, loaded.CloseWindow);
        Assert.Equal((true, 20), (loaded.IdleLockEnabled, loaded.IdleLockMinutes));
        Assert.Null(loaded.Language);
        Assert.False(loaded.ReduceMotion);
    }

    [Fact]
    public void Writing_a_file_of_before_phase_13_keeps_the_hello_setting_under_the_new_name_only()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, """{"WindowsHelloForCriticalApprovals":false}""");
        var store = new JsonAppSettingsStore(paths, null);

        store.SavePreferences(store.Load() with { ReduceMotion = true });

        var text = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"windowsHelloForImportantApprovals\": false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CriticalApprovals", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.Load().WindowsHelloForImportantApprovals);
    }

    [Fact]
    public void When_a_file_has_both_names_the_new_one_wins()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, """{"windowsHelloForCriticalApprovals":false,"windowsHelloForImportantApprovals":true}""");

        Assert.True(new JsonAppSettingsStore(paths, null).Load().WindowsHelloForImportantApprovals);
    }

    [Fact]
    public void The_defaults_ask_hello_follow_the_language_of_windows_and_keep_the_animations()
    {
        var settings = AppSettings.Default;

        Assert.True(settings.WindowsHelloForImportantApprovals);
        Assert.Null(settings.Language);
        Assert.False(settings.ReduceMotion);
    }

    [Fact]
    public void The_language_and_reduce_motion_are_preferences_that_survive_a_save_of_the_shell()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        var loadedAtStartup = store.Load();

        store.SavePreferences(store.Load() with { Language = "en", ReduceMotion = true });
        store.Save(loadedAtStartup.WithProjectOpened(a));

        var loaded = store.Load();
        Assert.Equal("en", loaded.Language);
        Assert.True(loaded.ReduceMotion);
        Assert.Equal([a], loaded.RecentProjects);
    }

    [Theory]
    [InlineData("\"es-ES\"", "es-ES")]
    [InlineData("\"EN\"", "en")]
    [InlineData("\"es-MX\"", "es-ES")]
    [InlineData("\"fr-FR\"", null)]
    [InlineData("\"klingon\"", null)]
    [InlineData("\"   \"", null)]
    [InlineData("null", null)]
    public void A_language_that_is_not_in_the_list_reads_as_the_one_of_windows(string stored, string? read)
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"language\":" + stored + ",\"reduceMotion\":true}");

        var loaded = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal(read, loaded.Language);
        Assert.True(loaded.ReduceMotion);
    }
}
