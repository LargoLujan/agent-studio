using System.Collections.Concurrent;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Orchestration.Publication;

namespace AgentStudio.Tests.Desktop.Services;

/// <summary>A folder notice comes from the pool; its listeners must neither mutate a bound list there nor prevent attaching the mailbox.</summary>
public sealed class RemoteSyncThreadingTests
{
    [Fact]
    public async Task Permission_changes_update_the_settings_list_only_on_its_captured_context()
    {
        var store = new InMemorySettingsStore();
        var permission = new AppSettingsRemoteSyncPermission(store);
        var ui = new QueuedContext();
        RemoteSyncSettingsViewModel section;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            section = new RemoteSyncSettingsViewModel(store, permission, _ => true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => permission.SetAsync("p1", true, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Empty(section.Projects);
        ui.Drain();
        Assert.Equal("p1", Assert.Single(section.Projects).Grant.ProjectId);
    }

    [Fact]
    public async Task A_failing_folder_notice_does_not_prevent_opening_the_mailbox()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".agentstudio/agentstudio.db");
        var project = new ProjectContext("p1", temp.Path);
        await using var rig = new PublicationRig(RemoteSyncState.Empty with { LastOutcome = HostingOutcome.Unreachable });
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("p1")] });
        var permission = new AppSettingsRemoteSyncPermission(store);
        var notified = false;
        permission.Changed += (_, _) =>
        {
            notified = true;
            throw new InvalidOperationException("A listener failed.");
        };
        var attachment = new RemoteSyncAttachment(new Workspace(project), rig.Sync, permission);

        await attachment.OnProjectOpenedAsync(project.RootPath, TestContext.Current.CancellationToken);

        Assert.True(notified);
        // Health is restored only when the mailbox was attached; asking for health never creates it.
        Assert.Equal(HostingOutcome.Unreachable, rig.Sync.Health(project.ProjectId).LastOutcome);
    }

    [Fact]
    public async Task A_failing_listener_does_not_hide_saved_permissions_or_folder_names_from_other_screens()
    {
        var store = new InMemorySettingsStore();
        var permission = new AppSettingsRemoteSyncPermission(store);
        permission.Changed += (_, _) => throw new InvalidOperationException("A listener failed.");
        var notified = new List<string>();
        permission.Changed += (_, id) => notified.Add(id);

        await permission.SetAsync("p1", true, TestContext.Current.CancellationToken);
        permission.NoteProject("p1", @"C:\work\one");
        await permission.SetAsync("p1", false, TestContext.Current.CancellationToken);
        await permission.SetAsync("p1", true, TestContext.Current.CancellationToken);

        Assert.Equal(["p1", "p1", "p1", "p1"], notified);
        Assert.Equal(@"C:\work\one", Assert.Single(store.Load().RemoteSyncProjects).Folder);

        // Saving a withdrawal announces it even when another screen's listener fails first.
        var section = new RemoteSyncSettingsViewModel(store, permission, _ => true);
        section.RemoveCommand.Execute(Assert.Single(section.Projects));
        var saved = section.Apply(store.Load());
        store.Current = saved;
        section.Accept(saved);
        Assert.Equal(5, notified.Count);
        Assert.False(permission.IsAllowed("p1"));
        Assert.False(section.HasChanges);
    }

    [Fact]
    public async Task Queued_permission_notices_preserve_the_unsaved_interval_and_withdrawal()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { RemoteSyncProjects = [new RemoteSyncGrant("p1")] });
        var permission = new AppSettingsRemoteSyncPermission(store);
        var ui = new QueuedContext();
        RemoteSyncSettingsViewModel section;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            section = new RemoteSyncSettingsViewModel(store, permission, _ => true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        section.SelectedInterval = section.Intervals.Single(i => i.Minutes == 60);
        section.RemoveCommand.Execute(Assert.Single(section.Projects));
        await Task.Run(() => permission.SetAsync("p2", true, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        ui.Drain();

        Assert.Equal("p2", Assert.Single(section.Projects).Grant.ProjectId);
        Assert.Equal(60, section.SelectedInterval.Minutes);
        Assert.True(section.HasChanges);
        Assert.Equal(["p2"], section.Apply(store.Load()).RemoteSyncProjects.Select(g => g.ProjectId));
    }

    private sealed class Workspace(ProjectContext project) : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(project);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queued = new();

        public override void Post(SendOrPostCallback d, object? state) => _queued.Enqueue((d, state));

        public void Drain()
        {
            while (_queued.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
