using System.IO;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.Skills;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// The writers of the settings file (docs/18 P80, docs/21 §10.9): the permission of each project, the one of the skills doctor, the preferences of «Ajustes», the shell and the window all read the file, change their own
/// fields and write it back; the store makes those three steps one, so none of them loses what another saved in between. And the list of «Git remoto» in «Ajustes» (P76 and P81): it names each project, says when its folder
/// is gone and takes a project out all the same.
/// </summary>
public sealed class SettingsWritersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writers_that_change_the_file_at_the_same_time_lose_nothing()
    {
        using var temp = new TempDirectory();
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        var permission = new AppSettingsRemoteSyncPermission(store);
        var doctor = new AppSettingsSkillsDoctorSetting(store);
        var projects = Enumerable.Range(0, 24).Select(i => "p" + i).ToList();
        using var go = new ManualResetEventSlim();
        var placement = new WindowPlacement(10, 20, 800, 600, false);

        // Everybody waits at the same door and goes in at once; the door is a signal, not a delay.
        var work = new List<Task>();
        work.AddRange(projects.Select(id => Task.Run(() => { go.Wait(Ct); return permission.SetAsync(id, true, Ct); }, Ct)));
        work.Add(Task.Run(() => { go.Wait(Ct); for (var i = 0; i < 12; i++) { doctor.Set(true); } }, Ct));
        work.Add(Task.Run(() => { go.Wait(Ct); for (var i = 0; i < 12; i++) { store.SaveWindowPlacement(placement); } }, Ct));
        work.Add(Task.Run(() => { go.Wait(Ct); for (var i = 0; i < 12; i++) { store.Save(AppSettings.Default.WithProjectOpened(temp.CreateDirectory("proj" + i))); } }, Ct));
        work.Add(Task.Run(() => { go.Wait(Ct); for (var i = 0; i < 12; i++) { store.UpdatePreferences(s => s with { RemoteSyncIntervalMinutes = 15 }); } }, Ct));
        go.Set();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(120), Ct);

        var loaded = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null).Load();
        Assert.Equal(projects.Order(StringComparer.Ordinal), loaded.RemoteSyncProjects.Select(g => g.ProjectId).Order(StringComparer.Ordinal));
        Assert.True(loaded.SkillsDoctorNetworkEnabled);
        Assert.Equal(placement, loaded.MainWindowPlacement);
        Assert.Equal(15, loaded.RemoteSyncIntervalMinutes);
        Assert.Single(loaded.RecentProjects);
    }

    [Fact]
    public async Task A_permission_withdrawn_is_not_brought_back_by_a_change_made_at_the_same_time()
    {
        using var temp = new TempDirectory();
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        var permission = new AppSettingsRemoteSyncPermission(store);
        await permission.SetAsync("gone", true, Ct);
        using var go = new ManualResetEventSlim();

        var work = new List<Task>
        {
            Task.Run(() => { go.Wait(Ct); return permission.SetAsync("gone", false, Ct); }, Ct),
            Task.Run(() => { go.Wait(Ct); for (var i = 0; i < 20; i++) { store.UpdatePreferences(s => s with { SkillsDoctorNetworkEnabled = i % 2 == 0 }); } }, Ct),
        };
        go.Set();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(120), Ct);

        Assert.False(permission.IsAllowed("gone"));
    }

    [Fact]
    public void A_change_that_changes_nothing_writes_nothing_and_one_that_breaks_the_rules_is_refused()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);

        store.UpdatePreferences(s => s);
        Assert.False(File.Exists(paths.SettingsFile));

        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdatePreferences(s => s with { RemoteSyncIntervalMinutes = 7 }));
        Assert.False(File.Exists(paths.SettingsFile));

        var saved = store.UpdatePreferences(s => s with { RemoteSyncIntervalMinutes = 60 });
        Assert.Equal(60, saved.RemoteSyncIntervalMinutes);
        Assert.Equal(60, store.Load().RemoteSyncIntervalMinutes);
    }

    [Fact]
    public async Task The_folder_of_a_project_is_stored_when_the_app_opens_it_after_the_permission_was_given()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("p1")] });
        var permission = new AppSettingsRemoteSyncPermission(store);
        var raised = new List<string>();
        permission.Changed += (_, id) => raised.Add(id);

        permission.NoteProject("p2", @"C:\work\two");
        Assert.Equal(0, store.Saves);

        permission.NoteProject("p1", @"C:\work\one");
        Assert.Equal(@"C:\work\one", Assert.Single(store.Load().RemoteSyncProjects).Folder);
        Assert.Equal(["p1"], raised);

        // The same folder again is not a change; and a permission given later to a project the app noted keeps its folder.
        var saves = store.Saves;
        permission.NoteProject("p1", @"C:\work\one");
        Assert.Equal(saves, store.Saves);
        await permission.SetAsync("p2", true, Ct);
        Assert.Equal([@"C:\work\one", @"C:\work\two"], store.Load().RemoteSyncProjects.Select(g => g.Folder));
    }

    [Fact]
    public void The_list_names_each_project_says_when_its_folder_is_gone_and_takes_it_out_all_the_same()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with
        {
            RemoteSyncProjects = [new RemoteSyncGrant("a", @"C:\here"), new RemoteSyncGrant("b", @"C:\gone"), new RemoteSyncGrant("c")],
        });
        var section = new RemoteSyncSettingsViewModel(store, folderExists: folder => folder == @"C:\here");

        Assert.Equal(@"C:\here", section.Projects[0].Label);
        Assert.Equal(Texts.Format("RemoteSync_Projects_Missing", new RemoteSyncGrant("b", @"C:\gone")), section.Projects[1].Label);
        Assert.Contains(@"C:\gone", section.Projects[1].Label, StringComparison.Ordinal);
        Assert.NotEqual(@"C:\gone", section.Projects[1].Label);
        Assert.Equal("c", section.Projects[2].Label);

        section.RemoveCommand.Execute(section.Projects[1]);
        var saved = section.Apply(store.Load());

        Assert.Equal(["a", "c"], saved.RemoteSyncProjects.Select(g => g.ProjectId));
    }

    [Fact]
    public void An_interval_that_is_not_in_the_list_read_from_a_hand_edited_file_is_shown_as_five_minutes()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Combine("data"));
        Directory.CreateDirectory(paths.BaseDirectory);
        File.WriteAllText(paths.SettingsFile, """{"remoteSyncIntervalMinutes": 9999, "remoteSyncProjects": [{"projectId": "a", "folder": "C:\\x"}]}""");

        var section = new RemoteSyncSettingsViewModel(new JsonAppSettingsStore(paths, null), folderExists: _ => true);

        Assert.Equal(AppSettings.DefaultRemoteSyncMinutes, section.SelectedInterval.Minutes);
        Assert.Contains(section.SelectedInterval, section.Intervals);
        Assert.False(section.HasChanges);
        Assert.Equal(@"C:\x", Assert.Single(section.Projects).Label);
    }

    [Fact]
    public void An_empty_list_says_so_and_has_nothing_to_take_out()
    {
        var section = new RemoteSyncSettingsViewModel(new InMemorySettingsStore());

        Assert.Empty(section.Projects);
        Assert.True(section.HasNoProjects);
        Assert.False(section.HasProjects);
        Assert.False(section.HasChanges);

        section.RemoveCommand.Execute(null);
        Assert.False(section.HasChanges);
    }

    [Fact]
    public void Taking_a_permission_out_is_a_change_that_saving_applies_and_the_list_then_shows_what_is_in_force()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("a", @"C:\a"), new RemoteSyncGrant("b", @"C:\b")] });
        var permission = new AppSettingsRemoteSyncPermission(store);
        var section = new RemoteSyncSettingsViewModel(store, permission, _ => true);
        var raised = new List<string>();
        permission.Changed += (_, id) => raised.Add(id);

        section.RemoveCommand.Execute(section.Projects.Single(p => p.Grant.ProjectId == "a"));
        Assert.True(section.HasChanges);
        Assert.Equal(["b"], section.Projects.Select(p => p.Grant.ProjectId));
        Assert.True(permission.IsAllowed("a"));

        var saved = ((IAppSettingsStore)store).UpdatePreferences(section.Apply);
        section.Accept(saved);

        Assert.False(permission.IsAllowed("a"));
        Assert.True(permission.IsAllowed("b"));
        Assert.Equal(["a"], raised);
        Assert.False(section.HasChanges);
        Assert.Equal(["b"], section.Projects.Select(p => p.Grant.ProjectId));
    }
}
