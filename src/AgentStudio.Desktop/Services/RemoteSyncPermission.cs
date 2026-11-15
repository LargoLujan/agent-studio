using System.Collections.Concurrent;
using System.IO;
using AgentStudio.Application.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// «Sincronizar con GitHub» of each project and the interval of the poll (docs/21 D-02 and §7.5), over the settings: the same value answers
/// <see cref="IRemoteSyncPermission"/> (what the switch of the pull requests section reads and writes) and <see cref="IRemoteSyncPolicy"/> (what the
/// mailbox asks before it uses the network in the background), so there is one truth and no copy that can fall out of step. Every call reads the stored
/// settings, as <c>AppSettingsSkillsDoctorSetting</c> does: a change in «Ajustes» applies to the next poll. Nothing is on by default.
/// <see cref="SetAsync"/> is the one way to give the permission, and only a click of the person calls it.
/// </summary>
public sealed class AppSettingsRemoteSyncPermission(IAppSettingsStore store, ILogger<AppSettingsRemoteSyncPermission>? log = null) : IRemoteSyncPermission, IRemoteSyncPolicy
{
    private readonly IAppSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler<string>? Changed;

    /// <inheritdoc />
    public TimeSpan Interval => TimeSpan.FromMinutes(_store.Load().RemoteSyncIntervalMinutes);

    /// <inheritdoc />
    public bool IsAllowed(string projectId) =>
        !string.IsNullOrWhiteSpace(projectId) && _store.Load().RemoteSyncProjects.Any(g => string.Equals(g.ProjectId, projectId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool PeriodicSyncAllowed(string projectId) => IsAllowed(projectId);

    /// <summary>
    /// Remembers the folder of an open project, so the list of «Ajustes» can name it (the port carries the id alone): in memory for the permission given
    /// from now on, and in the stored grant when the project already has the permission without a folder, or with another one (P76), so a project is named
    /// from the first time the app opens it after the permission was given, in any session.
    /// </summary>
    public void NoteProject(string projectId, string folder)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _folders[projectId] = folder;
        var changed = false;
        try
        {
            _store.UpdatePreferences(settings =>
            {
                var grant = settings.RemoteSyncProjects.FirstOrDefault(g => string.Equals(g.ProjectId, projectId, StringComparison.Ordinal));
                if (grant is null || string.Equals(grant.Folder, folder, StringComparison.Ordinal))
                {
                    return settings;
                }

                changed = true;
                return settings with { RemoteSyncProjects = [.. settings.RemoteSyncProjects.Select(g => ReferenceEquals(g, grant) ? grant with { Folder = folder } : g)] };
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a name for the list: the project opens and syncs the same without it.
            changed = false;
        }

        if (changed)
        {
            Announce(projectId);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string projectId, bool allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ct.ThrowIfCancellationRequested();
        var changed = false;
        _store.UpdatePreferences(settings =>
        {
            var present = settings.RemoteSyncProjects.Any(g => string.Equals(g.ProjectId, projectId, StringComparison.Ordinal));
            if (allowed && !present)
            {
                _folders.TryGetValue(projectId, out var folder);
                changed = true;
                return settings with { RemoteSyncProjects = [.. settings.RemoteSyncProjects, new RemoteSyncGrant(projectId, folder)] };
            }

            if (!allowed && present)
            {
                changed = true;
                return settings with { RemoteSyncProjects = [.. settings.RemoteSyncProjects.Where(g => !string.Equals(g.ProjectId, projectId, StringComparison.Ordinal))] };
            }

            return settings;
        });

        if (changed)
        {
            Announce(projectId);
        }

        return Task.CompletedTask;
    }

    // A stored permission is already in force: one failing screen must not hide it from the other listeners.
    private void Announce(string projectId)
    {
        if (Changed is not { } listeners)
        {
            return;
        }

        foreach (EventHandler<string> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(this, projectId);
            }
#pragma warning disable CA1031 // Notification failures must not turn an already saved permission into a failed operation.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log?.LogWarning(ex, "A remote permission listener failed.");
            }
        }
    }

    /// <summary>
    /// «Ajustes» took these projects off the list and saved: the permission is already out of the stored settings, and whoever shows the switch of each is
    /// told. It only announces; it writes nothing.
    /// </summary>
    internal void AnnounceWithdrawn(IEnumerable<string> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        foreach (var id in projectIds)
        {
            Announce(id);
        }
    }
}
