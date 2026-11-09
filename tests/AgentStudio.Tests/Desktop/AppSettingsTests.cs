using System.IO;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Tests.Desktop;

public sealed class AppSettingsTests
{
    [Fact]
    public void WithProjectOpened_PutsTheProjectFirstAndRemembersItAsLast()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        var settings = AppSettings.Default.WithProjectOpened(a).WithProjectOpened(b);

        Assert.Equal([b, a], settings.RecentProjects);
        Assert.Equal(b, settings.LastProjectPath);
    }

    [Fact]
    public void WithProjectOpened_MovesAnExistingProjectToTheFrontWithoutDuplicates()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        var settings = AppSettings.Default.WithProjectOpened(a).WithProjectOpened(b)
            .WithProjectOpened(a.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(2, settings.RecentProjects.Count);
        Assert.Equal(a, settings.RecentProjects[0], ignoreCase: true);
        Assert.Equal(b, settings.RecentProjects[1]);
    }

    [Fact]
    public void WithProjectOpened_KeepsAtMostTenProjectsDroppingTheOldest()
    {
        using var temp = new TempDirectory();
        var settings = AppSettings.Default;
        var paths = Enumerable.Range(1, 12).Select(i => temp.CreateDirectory($"p{i:00}")).ToList();
        foreach (var path in paths)
        {
            settings = settings.WithProjectOpened(path);
        }

        Assert.Equal(AppSettings.MaxRecentProjects, settings.RecentProjects.Count);
        Assert.Equal(paths[^1], settings.RecentProjects[0]);
        Assert.Equal(paths[2], settings.RecentProjects[^1]);
        Assert.DoesNotContain(paths[0], settings.RecentProjects);
    }

    [Fact]
    public void WithProjectRemoved_ForgetsItAndClearsTheLastProject()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");

        var settings = AppSettings.Default.WithProjectOpened(a).WithProjectRemoved(a);

        Assert.Empty(settings.RecentProjects);
        Assert.Null(settings.LastProjectPath);
    }

    // ---- Phase 4 preferences (docs/14 D-11 and D-15) -----------------------------------------------------------

    [Fact]
    public void By_default_the_context_thresholds_are_70_85_95_and_the_Codex_quota_is_not_read()
    {
        var thresholds = AppSettings.Default.ContextThresholds();

        Assert.Equal((0.70, 0.85, 0.95), (thresholds.Warning, thresholds.Compress, thresholds.Critical));
        Assert.False(AppSettings.Default.CodexQuotaReadEnabled);
    }

    [Theory]
    [InlineData(85, 70, 95)]
    [InlineData(70, 70, 95)]
    [InlineData(0, 85, 95)]
    [InlineData(70, 85, 101)]
    [InlineData(-5, 85, 95)]
    public void WithContextThresholds_refuses_thresholds_out_of_order_or_out_of_range(int warning, int compress, int critical)
    {
        Assert.False(AppSettings.AreValidContextThresholds(warning, compress, critical));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Default.WithContextThresholds(warning, compress, critical));
    }

    [Fact]
    public void WithContextThresholds_keeps_valid_thresholds_and_the_rest()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");

        var settings = AppSettings.Default.WithProjectOpened(a).WithContextThresholds(60, 80, 100);

        Assert.True(AppSettings.AreValidContextThresholds(60, 80, 100));
        Assert.Equal((0.60, 0.80, 1.00), (settings.ContextThresholds().Warning, settings.ContextThresholds().Compress, settings.ContextThresholds().Critical));
        Assert.Equal([a], settings.RecentProjects);
    }

    // ---- Phase 8 settings (docs/16 D-13, D-14 and D-40) -----------------------------------------------------------

    /// <summary>
    /// The defaults docs/16 fixes: closing the window asks when there is work going on (D-14), critical approvals ask Windows
    /// Hello and it can be turned off (D-13), nothing locks by itself (D-13) and the computer may sleep as always (D-40).
    /// </summary>
    [Fact]
    public void By_default_closing_asks_hello_confirms_critical_approvals_nothing_locks_and_the_computer_may_sleep()
    {
        var settings = AppSettings.Default;

        Assert.Equal(CloseWindowAction.Ask, settings.CloseWindow);
        Assert.True(settings.WindowsHelloForImportantApprovals);
        Assert.False(settings.IdleLockEnabled);
        Assert.Equal(AppSettings.DefaultIdleLockMinutes, settings.IdleLockMinutes);
        Assert.Equal(15, AppSettings.DefaultIdleLockMinutes);
        Assert.False(settings.PreventSleepWhileWorking);
        Assert.Equal(["Ask", "KeepInTray", "Exit"], Enum.GetNames<CloseWindowAction>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(AppSettings.MaxIdleLockMinutes + 1)]
    public void WithIdleLock_refuses_a_time_out_of_range(int minutes)
    {
        Assert.False(AppSettings.IsValidIdleLockMinutes(minutes));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Default.WithIdleLock(true, minutes));
    }

    [Fact]
    public void WithIdleLock_keeps_a_valid_time_and_the_rest()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");

        var settings = AppSettings.Default.WithProjectOpened(a).WithIdleLock(true, 5);

        Assert.True(AppSettings.IsValidIdleLockMinutes(1));
        Assert.True(AppSettings.IsValidIdleLockMinutes(AppSettings.MaxIdleLockMinutes));
        Assert.Equal((true, 5), (settings.IdleLockEnabled, settings.IdleLockMinutes));
        Assert.Equal([a], settings.RecentProjects);
    }
}

public sealed class JsonAppSettingsStoreTests
{
    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();

        var settings = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data"))).Load();

        Assert.Null(settings.LastProjectPath);
        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAndCreatesTheFolder()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths);
        var project = temp.CreateDirectory("proj");

        store.Save(AppSettings.Default.WithProjectOpened(project));
        var loaded = new JsonAppSettingsStore(paths).Load();

        Assert.Equal(project, loaded.LastProjectPath);
        Assert.Equal([project], loaded.RecentProjects);
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTemporaryFilesAndReplacesPreviousContent()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths);
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        store.Save(AppSettings.Default.WithProjectOpened(a));
        store.Save(AppSettings.Default.WithProjectOpened(b));

        var files = Directory.GetFileSystemEntries(paths.BaseDirectory).Select(Path.GetFileName).ToList();
        Assert.Equal(["settings.json"], files);
        Assert.Equal(b, store.Load().LastProjectPath);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Load_CorruptJson_ReturnsDefaults(string content)
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, content);

        var settings = new JsonAppSettingsStore(paths).Load();

        Assert.Null(settings.LastProjectPath);
        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public void Load_HandEditedFile_DropsBlanksAndTrimsToTheLimit()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        var many = string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"C:\\\\p{i}\""));
        File.WriteAllText(paths.SettingsFile, $"{{\"lastProjectPath\":\"  \",\"recentProjects\":[\"\",{many}]}}");

        var settings = new JsonAppSettingsStore(paths).Load();

        Assert.Null(settings.LastProjectPath);
        Assert.Equal(AppSettings.MaxRecentProjects, settings.RecentProjects.Count);
        Assert.DoesNotContain(string.Empty, settings.RecentProjects);
    }

    [Fact]
    public void Load_WithoutASkillsIndex_UsesTheDefaultFolderWhenItExists()
    {
        using var temp = new TempDirectory();
        var index = temp.CreateDirectory("skills-index");

        var settings = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), index).Load();

        Assert.Equal(index, settings.SkillsIndexPath);
    }

    [Fact]
    public void Load_WithoutASkillsIndex_LeavesItUnsetWhenTheDefaultFolderDoesNotExist()
    {
        using var temp = new TempDirectory();

        var settings = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), temp.Combine("no-existe")).Load();

        Assert.Null(settings.SkillsIndexPath);
    }

    [Fact]
    public void Load_KeepsAConfiguredSkillsIndex_EvenWhenTheDefaultFolderExists()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var mine = temp.Combine("mi-indice");
        var store = new JsonAppSettingsStore(paths, temp.CreateDirectory("skills-index"));

        store.Save(AppSettings.Default with { SkillsIndexPath = mine });

        Assert.Equal(mine, store.Load().SkillsIndexPath);
    }

    [Fact]
    public void Load_HandEditedBlankSkillsIndex_CountsAsUnset()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"skillsIndexPath\":\"   \"}");

        var settings = new JsonAppSettingsStore(paths, temp.Combine("no-existe")).Load();

        Assert.Null(settings.SkillsIndexPath);
    }

    // ---- Phase 4 preferences (docs/14 D-11 and D-15) -----------------------------------------------------------

    [Fact]
    public void SavePreferences_ThenLoad_RoundTripsTheThresholdsAndTheCodexOptIn()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);

        store.SavePreferences(AppSettings.Default.WithContextThresholds(60, 80, 90) with { CodexQuotaReadEnabled = true });
        var loaded = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal((60, 80, 90), (loaded.ContextWarningPercent, loaded.ContextCompressPercent, loaded.ContextCriticalPercent));
        Assert.True(loaded.CodexQuotaReadEnabled);
    }

    /// <summary>
    /// The shell keeps the settings it loaded at startup and saves them again when a project is opened: that copy must not
    /// undo the preferences saved since (the panel of thresholds and the Codex opt-in write them).
    /// </summary>
    [Fact]
    public void Save_KeepsThePreferencesOnDisk_SoACopyLoadedEarlierDoesNotUndoThem()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        store.Save(AppSettings.Default.WithProjectOpened(a));
        var loadedAtStartup = store.Load();

        store.SavePreferences(store.Load().WithContextThresholds(50, 75, 90) with { CodexQuotaReadEnabled = true });
        store.Save(loadedAtStartup.WithProjectOpened(b));

        var loaded = store.Load();
        Assert.Equal([b, a], loaded.RecentProjects);
        Assert.Equal((50, 75, 90), (loaded.ContextWarningPercent, loaded.ContextCompressPercent, loaded.ContextCriticalPercent));
        Assert.True(loaded.CodexQuotaReadEnabled);
    }

    [Fact]
    public void SavePreferences_KeepsTheProjectsOnDisk()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        store.Save(AppSettings.Default.WithProjectOpened(a));

        store.SavePreferences(AppSettings.Default with { CodexQuotaReadEnabled = true });

        var loaded = store.Load();
        Assert.Equal([a], loaded.RecentProjects);
        Assert.Equal(a, loaded.LastProjectPath);
        Assert.True(loaded.CodexQuotaReadEnabled);
    }

    [Theory]
    [InlineData("{\"contextWarningPercent\":90,\"contextCompressPercent\":80,\"contextCriticalPercent\":95}")]
    [InlineData("{\"contextWarningPercent\":0,\"contextCompressPercent\":85,\"contextCriticalPercent\":95}")]
    [InlineData("{\"contextWarningPercent\":70,\"contextCompressPercent\":85,\"contextCriticalPercent\":150}")]
    public void Load_HandEditedThresholdsThatBreakTheRule_FallBackToTheDefaults(string content)
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, content);

        var settings = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal((70, 85, 95), (settings.ContextWarningPercent, settings.ContextCompressPercent, settings.ContextCriticalPercent));
    }

    [Fact]
    public void Load_AFileFromBeforePhase4_GetsTheDefaultPreferences()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\",\"recentProjects\":[\"C:\\\\p1\"]}");

        var settings = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal((70, 85, 95), (settings.ContextWarningPercent, settings.ContextCompressPercent, settings.ContextCriticalPercent));
        Assert.False(settings.CodexQuotaReadEnabled);
        Assert.Equal(@"C:\p1", settings.LastProjectPath);
    }

    // ---- Phase 8 settings (docs/16 D-13, D-14 and D-40): preferences, written by SavePreferences -------------------

    private static AppSettings Phase8Preferences(AppSettings settings) =>
        settings.WithIdleLock(true, 20) with
        {
            CloseWindow = CloseWindowAction.KeepInTray,
            WindowsHelloForImportantApprovals = false,
            PreventSleepWhileWorking = true,
        };

    [Fact]
    public void SavePreferences_ThenLoad_RoundTripsTheSettingsOfPhase8_WithTheClosingActionByName()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);

        store.SavePreferences(Phase8Preferences(AppSettings.Default));
        var loaded = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal(CloseWindowAction.KeepInTray, loaded.CloseWindow);
        Assert.Equal((true, 20), (loaded.IdleLockEnabled, loaded.IdleLockMinutes));
        Assert.False(loaded.WindowsHelloForImportantApprovals);
        Assert.True(loaded.PreventSleepWhileWorking);
        Assert.Contains("\"closeWindow\": \"KeepInTray\"", File.ReadAllText(paths.SettingsFile), StringComparison.Ordinal);
    }

    /// <summary>The shell saves the copy it loaded at startup when a project is opened: it must not undo the settings of Phase 8 saved since.</summary>
    [Fact]
    public void Save_KeepsTheSettingsOfPhase8OnDisk_SoACopyLoadedEarlierDoesNotUndoThem()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        var loadedAtStartup = store.Load();

        store.SavePreferences(Phase8Preferences(store.Load()));
        store.Save(loadedAtStartup.WithProjectOpened(a));

        var loaded = store.Load();
        Assert.Equal([a], loaded.RecentProjects);
        Assert.Equal(CloseWindowAction.KeepInTray, loaded.CloseWindow);
        Assert.Equal((true, 20), (loaded.IdleLockEnabled, loaded.IdleLockMinutes));
        Assert.False(loaded.WindowsHelloForImportantApprovals);
        Assert.True(loaded.PreventSleepWhileWorking);
    }

    [Fact]
    public void Load_AFileFromBeforePhase8_GetsTheDefaultsOfPhase8()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\",\"contextWarningPercent\":60,\"contextCompressPercent\":80,\"contextCriticalPercent\":90,\"codexQuotaReadEnabled\":true}");

        var settings = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal(CloseWindowAction.Ask, settings.CloseWindow);
        Assert.True(settings.WindowsHelloForImportantApprovals);
        Assert.Equal((false, AppSettings.DefaultIdleLockMinutes), (settings.IdleLockEnabled, settings.IdleLockMinutes));
        Assert.False(settings.PreventSleepWhileWorking);
        Assert.Equal((60, 80, 90), (settings.ContextWarningPercent, settings.ContextCompressPercent, settings.ContextCriticalPercent));
        Assert.True(settings.CodexQuotaReadEnabled);
    }

    /// <summary>A hand-edited value that makes no sense falls back to its default, and only that one: the rest of the file stays.</summary>
    [Theory]
    [InlineData("\"closeWindow\":\"Bandeja\"")]
    [InlineData("\"closeWindow\":7")]
    [InlineData("\"closeWindow\":null")]
    [InlineData("\"closeWindow\":{\"x\":1}")]
    [InlineData("\"idleLockMinutes\":0")]
    [InlineData("\"idleLockMinutes\":99999")]
    public void Load_HandEditedSettingsOfPhase8ThatMakeNoSense_FallBackToTheirDefaultsAndKeepTheRest(string field)
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\"," + field + ",\"idleLockEnabled\":true}");

        var settings = new JsonAppSettingsStore(paths, null).Load();

        Assert.Equal(CloseWindowAction.Ask, settings.CloseWindow);
        Assert.Equal(AppSettings.DefaultIdleLockMinutes, settings.IdleLockMinutes);
        Assert.True(settings.IdleLockEnabled);
        Assert.Equal(@"C:\p1", settings.LastProjectPath);
    }

    [Fact]
    public void Load_TheClosingActionIsReadByName_WhateverTheCase_OrByItsNumber()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        var store = new JsonAppSettingsStore(paths, null);

        File.WriteAllText(paths.SettingsFile, "{\"closeWindow\":\"exit\"}");
        var byName = store.Load().CloseWindow;
        File.WriteAllText(paths.SettingsFile, "{\"closeWindow\":1}");
        var byNumber = store.Load().CloseWindow;

        Assert.Equal((CloseWindowAction.Exit, CloseWindowAction.KeepInTray), (byName, byNumber));
    }

    [Fact]
    public void SavePreferences_RefusesAnIdleLockTimeOutOfRange_AndSavesNothing()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SavePreferences(AppSettings.Default with { IdleLockMinutes = 0 }));
        Assert.False(File.Exists(paths.SettingsFile));
    }

    // ---- Phase 12 (docs/17 D-06, F12-9): the history search of the orchestrator ----------------------------------------

    [Fact]
    public void The_history_search_of_the_orchestrator_is_on_by_default_also_in_a_file_of_before_and_off_survives_a_save_of_the_shell()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\",\"codexQuotaReadEnabled\":true}");
        var store = new JsonAppSettingsStore(paths, null);
        var loadedAtStartup = store.Load();

        Assert.True(AppSettings.Default.PlannerHistoryToolEnabled);
        Assert.True(loadedAtStartup.PlannerHistoryToolEnabled);

        store.SavePreferences(store.Load() with { PlannerHistoryToolEnabled = false });
        store.Save(loadedAtStartup.WithProjectOpened(temp.CreateDirectory("a")));

        Assert.False(new JsonAppSettingsStore(paths, null).Load().PlannerHistoryToolEnabled);
        Assert.Contains("\"plannerHistoryToolEnabled\": false", File.ReadAllText(paths.SettingsFile), StringComparison.Ordinal);
    }
}
