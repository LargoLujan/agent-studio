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

    /// <summary>Some mailbox is in the middle of something (a stretch of git, a call of the platform): the exit tells the person that it waits for it.</summary>
    public bool IsBusy => sync?.IsBusy == true;

    /// <summary>The one wait of the exit: the managed exit starts it and <c>App.OnExit</c> finds it done (or timed out) and does not wait again.</summary>
    private Task? _shutdown;

    /// <summary>
    /// The app is leaving: each mailbox ends the stretch it is in (a <c>push</c> is not cut in half) and stops, waiting at most <paramref name="wait"/>. Nothing is left
    /// running behind; a stretch that does not end in time is left to the end of the process, and the log says so. It is done once: whoever asks again (the managed exit
    /// first, <c>App.OnExit</c> as the safety net) gets the same wait, with the same limit, and not another one. It never needs the thread of the interface.
    /// </summary>
    public Task ShutdownAsync(TimeSpan wait)
    {
        lock (_shutdownLock)
        {
            // On the pool: the first steps of the close (the cancellations of the mailboxes) never run on the thread that asked.
            return _shutdown ??= Task.Run(() => CloseAsync(wait));
        }
    }

    private readonly object _shutdownLock = new();

    private async Task CloseAsync(TimeSpan wait)
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
    /// The same, as the safety net of <c>App.OnExit</c>, for what does not go through the managed exit (the end of the session of Windows, an exit before the tray exists), where
    /// nobody waits for a task: WPF does not wait for an <c>async void</c> <c>OnExit</c>, so with an <c>await</c> there the process would end with a <c>push</c> halfway (docs/21 D-25).
    /// This blocks the caller until the mailboxes stopped or <paramref name="wait"/> went by; when the managed exit already waited, it returns at once. The work runs on the thread
    /// pool and nothing in the closing of a mailbox needs the thread of the interface (the service never captures a context, and what it tells the screen it posts), so the thread
    /// of the interface can block on it without a deadlock.
    /// </summary>
    public void ShutdownBlocking(TimeSpan wait) => ShutdownAsync(wait).GetAwaiter().GetResult();

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
            NoteFolder(project);
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

    /// <summary>Noting the folder is only a name for the list of «Ajustes»: a failure of it (or of a listener of the permission) is logged and never keeps the mailbox from opening.</summary>
    private void NoteFolder(ProjectContext project)
    {
        try
        {
            permission?.NoteProject(project.ProjectId, project.RootPath);
        }
#pragma warning disable CA1031 // Only a name for a list: whatever fails here, the mailbox opens.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log?.LogWarning(ex, "The folder of the project could not be noted for the permission.");
        }
    }
}
