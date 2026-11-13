using System.Collections.ObjectModel;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Settings;

/// <summary>One answer of the list of intervals in «Ajustes»: how many minutes pass between two polls.</summary>
/// <param name="Minutes">One of <see cref="AppSettings.RemoteSyncIntervals"/>.</param>
/// <param name="Label">What the list shows, as «5 minutos».</param>
public sealed record RemoteSyncIntervalChoice(int Minutes, string Label)
{
    /// <summary>What a screen reader says of an item of the list: its label.</summary>
    public override string ToString() => Label;
}

/// <summary>One project of the list with the permission: its name for the list and the name of its «Quitar el permiso».</summary>
/// <param name="Grant">The project as the settings hold it.</param>
/// <param name="Label">The folder when it is known, else the id of the project.</param>
/// <param name="RemoveName">«Quitar el permiso de …», the name of its button for a screen reader.</param>
public sealed record RemoteSyncProjectRow(RemoteSyncGrant Grant, string Label, string RemoveName)
{
    /// <summary>What a screen reader says of an item of the list: its label.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// «Git remoto» in «Ajustes» (docs/21 §7.5, D-02 and D-23): the interval of the poll, the projects where «Sincronizar con GitHub» is on (each can be taken out
/// from here, never added: only the switch of the pull requests section of a project gives the permission) and a text that says what goes out and what does not.
/// It goes to the settings as a preference with the other sections and one «Guardar» (<c>SettingsDocumentViewModel</c>, through <see cref="Apply"/> and
/// <see cref="Accept"/>). A project taken out on screen is a change until it is saved, and is applied to the settings read at that moment (so a permission
/// given or withdrawn meanwhile, from the switch of a project, is not undone by a stale list).
/// </summary>
public sealed partial class RemoteSyncSettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;
    private readonly AppSettingsRemoteSyncPermission? _permission;
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    /// <param name="permission">The permission of the projects, told when «Guardar» takes one out and following the switch of the pull requests section; without it the list is read when the section is made.</param>
    public RemoteSyncSettingsViewModel(IAppSettingsStore store, AppSettingsRemoteSyncPermission? permission = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _permission = permission;
        Intervals = [.. AppSettings.RemoteSyncIntervals.Select(m => new RemoteSyncIntervalChoice(m, Texts.Format("RemoteSync_Interval_Item", m)))];
        _selectedInterval = Intervals[0];
        Show(store.Load());
        if (permission is not null)
        {
            permission.Changed += OnPermissionChanged;
        }
    }

    /// <summary>The title of the section in «Ajustes».</summary>
    public static string Title => Texts.Get("RemoteSync_Title");

    /// <summary>Raised after the preferences were saved.</summary>
    public event EventHandler? Saved;

    /// <summary>The intervals the person can choose: 2, 5, 15 and 60 minutes.</summary>
    public IReadOnlyList<RemoteSyncIntervalChoice> Intervals { get; }

    /// <summary>The interval chosen on screen; it is in force once saved.</summary>
    [ObservableProperty]
    private RemoteSyncIntervalChoice _selectedInterval;

    /// <summary>The projects with the permission that are not taken out on screen.</summary>
    public ObservableCollection<RemoteSyncProjectRow> Projects { get; } = [];

    /// <summary>Whether the list has any project.</summary>
    public bool HasProjects => Projects.Count > 0;

    /// <summary>Whether the list is empty, for the sentence that says how the permission is given.</summary>
    public bool HasNoProjects => Projects.Count == 0;

    /// <summary>Whether what is on screen differs from what is in force.</summary>
    public bool HasChanges => SelectedInterval.Minutes != _inForce.RemoteSyncIntervalMinutes || _removed.Count > 0;

    /// <summary>«Quitar el permiso»: the project leaves the list on screen; it is out of the settings when «Guardar» saves.</summary>
    [RelayCommand]
    private void Remove(RemoteSyncProjectRow? row)
    {
        if (row is null || !Projects.Remove(row))
        {
            return;
        }

        _removed.Add(row.Grant.ProjectId);
        NotifyList();
    }

    /// <summary>The interval on screen, and the projects taken out left out of the permission, both put into <paramref name="settings"/> (read just now).</summary>
    public AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            RemoteSyncIntervalMinutes = SelectedInterval.Minutes,
            RemoteSyncProjects = [.. settings.RemoteSyncProjects.Where(g => !_removed.Contains(g.ProjectId))],
        };
    }

    /// <summary>What was saved is in force now: it is shown, the projects that left are announced and <see cref="Saved"/> says so.</summary>
    public void Accept(AppSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var withdrawn = _removed.ToList();
        _removed.Clear();
        Show(saved);
        _permission?.AnnounceWithdrawn(withdrawn);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows again what is in force: what was changed on screen and not saved goes.</summary>
    public void Revert()
    {
        _removed.Clear();
        Show(_store.Load());
    }

    private void Show(AppSettings settings)
    {
        _inForce = settings;
        SelectedInterval = Intervals.FirstOrDefault(i => i.Minutes == settings.RemoteSyncIntervalMinutes) ?? Intervals.First(i => i.Minutes == AppSettings.DefaultRemoteSyncMinutes);
        ShowProjects(settings);
    }

    private void ShowProjects(AppSettings settings)
    {
        Projects.Clear();
        foreach (var grant in settings.RemoteSyncProjects.Where(g => !_removed.Contains(g.ProjectId)))
        {
            Projects.Add(new RemoteSyncProjectRow(grant, grant.ToString(), Texts.Format("RemoteSync_Projects_RemoveName", grant)));
        }

        NotifyList();
    }

    /// <summary>The switch of a project changed (its section, or the one «Guardar» just saved): the list follows what is stored, keeping what was taken out and not saved.</summary>
    private void OnPermissionChanged(object? sender, string projectId)
    {
        var settings = _store.Load();
        _inForce = _inForce with { RemoteSyncProjects = settings.RemoteSyncProjects };
        ShowProjects(settings);
    }

    private void NotifyList()
    {
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasNoProjects));
    }
}
