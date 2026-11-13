using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Application.Hosting;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>«Git remoto» in the settings (docs/21 §6, §7.5 and D-02): the permission per project is off by default, the interval is one of four, and one value answers both ports.</summary>
public sealed class RemoteSyncSettingsTests
{
    [Fact]
    public void By_default_no_project_is_allowed_and_the_interval_is_five_minutes()
    {
        var permission = new AppSettingsRemoteSyncPermission(new InMemorySettingsStore());

        Assert.False(permission.IsAllowed("p1"));
        Assert.False(permission.PeriodicSyncAllowed("p1"));
        Assert.Equal(TimeSpan.FromMinutes(5), permission.Interval);
    }

    [Fact]
    public async Task Giving_the_permission_is_read_by_both_ports_and_raises_changed_once()
    {
        var store = new InMemorySettingsStore();
        var permission = new AppSettingsRemoteSyncPermission(store);
        permission.NoteProject("p1", @"C:\work\one");
        var raised = new List<string>();
        permission.Changed += (_, id) => raised.Add(id);

        await permission.SetAsync("p1", true, CancellationToken.None);
        await permission.SetAsync("p1", true, CancellationToken.None);

        Assert.True(permission.IsAllowed("p1"));
        Assert.True(((IRemoteSyncPolicy)permission).PeriodicSyncAllowed("p1"));
        Assert.False(permission.IsAllowed("p2"));
        Assert.Equal(["p1"], raised);
        Assert.Equal(@"C:\work\one", Assert.Single(store.Load().RemoteSyncProjects).Folder);

        await permission.SetAsync("p1", false, CancellationToken.None);
        Assert.False(permission.IsAllowed("p1"));
        Assert.Equal(["p1", "p1"], raised);
    }

    [Fact]
    public void The_permission_and_the_interval_survive_the_file_and_the_other_writers()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths);
        ((IAppSettingsStore)store).SavePreferences(store.Load() with { RemoteSyncProjects = [new RemoteSyncGrant("p1", @"C:\x")], RemoteSyncIntervalMinutes = 15 });

        // The shell saves the projects it knows with a copy loaded before: the preferences stay as they are stored.
        store.Save(AppSettings.Default.WithProjectOpened(temp.CreateDirectory("proj")));

        var loaded = new JsonAppSettingsStore(paths).Load();
        Assert.Equal("p1", Assert.Single(loaded.RemoteSyncProjects).ProjectId);
        Assert.Equal(15, loaded.RemoteSyncIntervalMinutes);
    }

    [Fact]
    public void A_hand_edited_file_reads_with_a_valid_interval_and_a_clean_list()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        Directory.CreateDirectory(paths.BaseDirectory);
        File.WriteAllText(paths.SettingsFile, """{"remoteSyncIntervalMinutes": 7, "remoteSyncProjects": [null, {"projectId": " "}, {"projectId": "a"}, {"projectId": "a", "folder": "C:\\x"}, {"projectId": "b"}]}""");

        var loaded = new JsonAppSettingsStore(paths).Load();

        Assert.Equal(AppSettings.DefaultRemoteSyncMinutes, loaded.RemoteSyncIntervalMinutes);
        Assert.Equal(["a", "b"], loaded.RemoteSyncProjects.Select(g => g.ProjectId));
    }

    [Fact]
    public void An_interval_outside_the_four_is_refused_when_saving()
    {
        var store = new InMemorySettingsStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => ((IAppSettingsStore)store).SavePreferences(AppSettings.Default with { RemoteSyncIntervalMinutes = 7 }));
        foreach (var minutes in new[] { 2, 5, 15, 60 })
        {
            ((IAppSettingsStore)store).SavePreferences(AppSettings.Default with { RemoteSyncIntervalMinutes = minutes });
            Assert.Equal(minutes, store.Load().RemoteSyncIntervalMinutes);
        }
    }

    [Fact]
    public void The_section_lists_the_projects_and_taking_one_out_is_applied_to_the_settings_read_at_saving()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("a", @"C:\a"), new RemoteSyncGrant("b")] });
        var permission = new AppSettingsRemoteSyncPermission(store);
        var section = new RemoteSyncSettingsViewModel(store, permission);
        var raised = new List<string>();
        permission.Changed += (_, id) => raised.Add(id);

        Assert.Equal([@"C:\a", "b"], section.Projects.Select(p => p.ToString()));
        Assert.False(section.HasChanges);

        section.RemoveCommand.Execute(section.Projects[0]);
        Assert.True(section.HasChanges);

        // A permission given from another project's switch while «Ajustes» was open is not undone by the stale list.
        store.Current = store.Current with { RemoteSyncProjects = [.. store.Current.RemoteSyncProjects, new RemoteSyncGrant("c")] };
        var saved = section.Apply(store.Load());
        ((IAppSettingsStore)store).SavePreferences(saved);
        section.Accept(saved);

        Assert.Equal(["b", "c"], store.Load().RemoteSyncProjects.Select(g => g.ProjectId));
        Assert.Contains("a", raised);
        Assert.False(section.HasChanges);
        Assert.False(permission.IsAllowed("a"));
    }

    [Fact]
    public void Reverting_the_section_brings_back_what_was_taken_out_and_the_interval()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("a")] });
        var section = new RemoteSyncSettingsViewModel(store);

        section.RemoveCommand.Execute(section.Projects[0]);
        section.SelectedInterval = section.Intervals.First(i => i.Minutes == 60);
        Assert.True(section.HasChanges);
        Assert.Equal("a", Assert.Single(store.Load().RemoteSyncProjects).ProjectId);

        section.Revert();

        Assert.False(section.HasChanges);
        Assert.Single(section.Projects);
        Assert.Equal(5, section.SelectedInterval.Minutes);
    }
}
