using System.Collections.Concurrent;
using AgentStudio.Application.Hosting;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// «Sincronizar con GitHub» of each project and the interval of the poll (docs/21 D-02 and §7.5), over the settings: the same value answers
/// <see cref="IRemoteSyncPermission"/> (what the switch of the pull requests section reads and writes) and <see cref="IRemoteSyncPolicy"/> (what the
/// mailbox asks before it uses the network in the background), so there is one truth and no copy that can fall out of step. Every call reads the stored
/// settings, as <c>AppSettingsSkillsDoctorSetting</c> does: a change in «Ajustes» applies to the next poll. Nothing is on by default.
/// <see cref="SetAsync"/> is the one way to give the permission, and only a click of the person calls it.
/// </summary>
public sealed class AppSettingsRemoteSyncPermission(IAppSettingsStore store) : IRemoteSyncPermission, IRemoteSyncPolicy
{
    private readonly IAppSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);
    private readonly object _write = new();

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
    /// Remembers the folder of an open project, so the list of «Ajustes» can name it when the permission is given (the port carries the id alone). Kept in
    /// memory only; a project the app never opened in this session is listed by its id.
    /// </summary>
    public void NoteProject(string projectId, string folder)
    {
        if (!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(folder))
        {
            _folders[projectId] = folder;
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string projectId, bool allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ct.ThrowIfCancellationRequested();
        var changed = false;
        lock (_write)
        {
            var settings = _store.Load();
            var present = settings.RemoteSyncProjects.Any(g => string.Equals(g.ProjectId, projectId, StringComparison.Ordinal));
            if (allowed && !present)
            {
                _folders.TryGetValue(projectId, out var folder);
                _store.SavePreferences(settings with { RemoteSyncProjects = [.. settings.RemoteSyncProjects, new RemoteSyncGrant(projectId, folder)] });
                changed = true;
            }
            else if (!allowed && present)
            {
                _store.SavePreferences(settings with { RemoteSyncProjects = [.. settings.RemoteSyncProjects.Where(g => !string.Equals(g.ProjectId, projectId, StringComparison.Ordinal))] });
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, projectId);
        }

        return Task.CompletedTask;
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
            Changed?.Invoke(this, id);
        }
    }
}
