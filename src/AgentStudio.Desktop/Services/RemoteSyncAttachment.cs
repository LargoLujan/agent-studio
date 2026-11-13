using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Orchestration.Publication;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// What the shell does about the remote when a project is opened (docs/21 §10.9): it tells the permission which folder the project has (so «Ajustes» can name it)
/// and opens its mailbox (<see cref="RemoteSyncService.AttachAsync"/>), so the pull requests the project was following are watched again. It does so only for a
/// project that already has its storage (<c>.agentstudio/agentstudio.db</c>, like the upkeep of the memory): opening a folder to look at it never creates one. It
/// never throws into the interface, and it never waits for the network: the mailbox reads its local state and, only with the permission of the project, starts
/// its poll.
/// </summary>
public sealed class RemoteSyncAttachment(
    IProjectWorkspace? workspace,
    RemoteSyncService? sync,
    AppSettingsRemoteSyncPermission? permission,
    ILogger<RemoteSyncAttachment>? log = null)
{
    /// <summary>Whether there is a mailbox to open.</summary>
    public bool IsAvailable => workspace is not null && sync is not null;

    /// <summary>
    /// The app is leaving: each mailbox ends the stretch it is in (a <c>push</c> is not cut in half) and stops, waiting at most <paramref name="wait"/>. Nothing is left
    /// running behind; a stretch that does not end in time is left to the end of the process, and the log says so.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan wait)
    {
        if (sync is null)
        {
            return;
        }

        try
        {
            await sync.DisposeAsync().AsTask().WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log?.LogWarning("The remote mailbox did not end its stretch before the exit.");
        }
#pragma warning disable CA1031 // The app is leaving: nothing useful can be done with a mailbox that fails to close.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log?.LogWarning(ex, "The remote mailbox could not be closed in order.");
        }
    }

    /// <summary>
    /// The same, for the exit of the app, where nobody waits for a task: WPF does not wait for an <c>async void</c> <c>OnExit</c>, so with an <c>await</c> there the
    /// process would end with a <c>push</c> halfway (docs/21 D-25). This blocks the caller until the mailboxes stopped or <paramref name="wait"/> went by. The work
    /// runs on the thread pool and nothing in the closing of a mailbox needs the thread of the interface (the service never captures a context, and what it
    /// tells the screen it posts), so the thread of the interface can block on it without a deadlock.
    /// </summary>
    public void ShutdownBlocking(TimeSpan wait) => Task.Run(() => ShutdownAsync(wait)).GetAwaiter().GetResult();

    /// <summary>A project was opened. Returns when the mailbox is open, or at once when there is nothing to open.</summary>
    public async Task OnProjectOpenedAsync(string projectRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!IsAvailable || !File.Exists(Path.Combine(projectRoot, ".agentstudio", "agentstudio.db")))
        {
            return;
        }

        try
        {
            var project = await workspace!.OpenAsync(projectRoot, ct).ConfigureAwait(false);
            permission?.NoteProject(project.ProjectId, project.RootPath);
            await sync!.AttachAsync(project, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The mailbox is a convenience of the screen: a project that does not open it still opens, and the log says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log?.LogWarning(ex, "The remote mailbox of the project could not be opened.");
        }
    }
}
