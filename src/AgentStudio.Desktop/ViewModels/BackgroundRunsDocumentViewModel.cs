using System.Collections.ObjectModel;
using System.IO;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>A view of the shell's existing monitors; opening or closing it never owns a run.</summary>
public sealed partial class BackgroundRunsDocumentViewModel : DocumentViewModel
{
    public BackgroundRunsDocumentViewModel()
        : base("document:background-runs", Texts.Get("BackgroundRuns_Title"))
    {
    }

    public ObservableCollection<BackgroundRunItemViewModel> Runs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMonitor))]
    private BackgroundRunItemViewModel? _selectedRun;

    public RunMonitorViewModel? SelectedMonitor => SelectedRun?.Monitor;

    /// <summary>Keeps finished monitors reachable so their review and publication commands remain available.</summary>
    internal void Add(string projectRoot, RunMonitorViewModel monitor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(monitor);
        if (Runs.Any(item => ReferenceEquals(item.Monitor, monitor)))
        {
            return;
        }

        var item = new BackgroundRunItemViewModel(projectRoot, monitor);
        Runs.Add(item);
        SelectedRun ??= item;
    }
}

/// <summary>Project identity and the original monitor, with no second event reader or work registration.</summary>
public sealed class BackgroundRunItemViewModel(string projectRoot, RunMonitorViewModel monitor)
{
    public string ProjectRoot { get; } = projectRoot;

    public RunMonitorViewModel Monitor { get; } = monitor;

    public string ProjectName => Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectRoot)) is { Length: > 0 } name ? name : ProjectRoot;

    public string AccessibleName => Texts.Format("BackgroundRuns_RunName", ProjectRoot, Monitor.RunId);

    public override string ToString() => AccessibleName;
}
