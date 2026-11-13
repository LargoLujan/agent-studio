using System.Collections.Concurrent;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Publication;

/// <summary>
/// The mailboxes of the projects and the poll (docs/21 D-23 to D-26): one <see cref="ProjectRemoteMailbox"/> per project, made on the first call with the local state read, and the clock of
/// the poll that each one keeps (every <see cref="IRemoteSyncPolicy.Interval"/>, only while the person gave the permission; without a policy in the composition the network is never
/// used in the background). <see cref="SyncNowAsync"/> is the person's click and works with the periodic poll off.
/// </summary>
/// <remarks>The poll starts for a project when something asks for its mailbox; the composition calls <see cref="AttachAsync"/> when it opens a project, so the pull requests it was following are watched again.</remarks>
public sealed class RemoteSyncService : IRemoteSync, IRemoteMailboxes, IAsyncDisposable, IDisposable
{
    private readonly MailboxServices _services;
    private readonly ConcurrentDictionary<string, Lazy<Task<ProjectRemoteMailbox>>> _boxes = new(StringComparer.Ordinal);

    internal RemoteSyncService(
        IDecisionRecorder recorder, IProjectActivity activity, IHostingProviders providers, IRemoteSyncStore store, IRemoteSyncPolicy? policy, TimeProvider time)
    {
        _services = new MailboxServices(recorder, activity, providers, store, policy, time, id => Changed?.Invoke(this, id));
    }

    public event EventHandler<string>? Changed;

    /// <summary>Opens the mailbox of the project (and with it the poll), reading its local state.</summary>
    public async Task AttachAsync(ProjectContext project, CancellationToken ct) => _ = await OfAsync(project, ct).ConfigureAwait(false);

    async Task<ProjectRemoteMailbox> IRemoteMailboxes.OfAsync(ProjectContext project, CancellationToken ct) => await OfAsync(project, ct).ConfigureAwait(false);

    internal async Task<ProjectRemoteMailbox> OfAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var lazy = _boxes.GetOrAdd(project.ProjectId, _ => new Lazy<Task<ProjectRemoteMailbox>>(() => CreateAsync(project)));
        var made = lazy.Value;
        try
        {
            return await made.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (made.IsFaulted)
        {
            // A mailbox that failed to open is not kept as failed for ever: the next use tries again.
            _boxes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectRemoteMailbox>>>(project.ProjectId, lazy));
            throw;
        }
    }

    private async Task<ProjectRemoteMailbox> CreateAsync(ProjectContext project)
    {
        RemoteSyncState state;
        try
        {
            state = await _services.Store.LoadAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state = RemoteSyncState.Empty;
        }

        return new ProjectRemoteMailbox(project, _services, state);
    }

    public async Task SyncNowAsync(ProjectContext project, CancellationToken ct) =>
        await (await OfAsync(project, ct).ConfigureAwait(false)).SyncNowAsync().ConfigureAwait(false);

    public async Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        await (await OfAsync(project, ct).ConfigureAwait(false)).FollowAsync(pullRequest, run?.Value).ConfigureAwait(false);
    }

    public async Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        await (await OfAsync(project, ct).ConfigureAwait(false)).UnfollowAsync(pullRequest).ConfigureAwait(false);
    }

    public async Task CancelAsync(string projectId, string ticketId, CancellationToken ct)
    {
        if (Existing(projectId) is { } box)
        {
            await box.CancelAsync(ticketId).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<RemoteWorkStatus> Pending(string projectId) => Existing(projectId)?.Pending() ?? [];

    public RemoteSyncHealth Health(string projectId) => Existing(projectId)?.Health ?? new RemoteSyncHealth(null, null, null);

    private ProjectRemoteMailbox? Existing(string projectId) =>
        _boxes.TryGetValue(projectId, out var lazy) && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>
    /// The same ordered release for a container that is disposed with <c>Dispose()</c>: a service that is only <see cref="IAsyncDisposable"/> makes
    /// <c>ServiceProvider.Dispose()</c> throw, and every test or host that builds the composition and disposes it that way would fail. The app releases it
    /// with <see cref="DisposeAsync"/> first (the exit), so this finds nothing left to wait for. (Added by the integration of Phase 10.)
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>When the app closes: each mailbox ends the stretch it is in (a <c>push</c> is not cut) and stops. No task is left behind.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _boxes.Values.ToList())
        {
            if (lazy.IsValueCreated)
            {
                try
                {
                    await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // the mailbox never opened: nothing to close
                }
            }
        }

        _boxes.Clear();
    }
}
