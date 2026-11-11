using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// Landing tab. Without a project: "Abrir carpeta" and the recent projects. With one open: the ways into it (the orchestrator,
/// the files, the debates, the audit, the usage, the memory and the terminal, with their shortcuts), then the recent projects.
/// </summary>
public sealed partial class WelcomeDocumentViewModel : DocumentViewModel
{
    public const string Id = "welcome";

    public WelcomeDocumentViewModel(
        ObservableCollection<RecentProjectItem> recentProjects,
        ICommand openFolderCommand,
        ICommand openRecentCommand,
        IReadOnlyList<WelcomeActionViewModel>? projectActions = null)
        : base(Id, Texts.Get("Welcome_Title"))
    {
        RecentProjects = recentProjects;
        OpenFolderCommand = openFolderCommand;
        OpenRecentCommand = openRecentCommand;
        ProjectActions = projectActions ?? [];
    }

    public ObservableCollection<RecentProjectItem> RecentProjects { get; }

    public ICommand OpenFolderCommand { get; }

    /// <summary>Parameter: the project path.</summary>
    public ICommand OpenRecentCommand { get; }

    /// <summary>What can be opened in the open project; shown only while there is one.</summary>
    public IReadOnlyList<WelcomeActionViewModel> ProjectActions { get; }

    /// <summary>The name of the open project; null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    private string? _projectName;

    /// <summary>Where the open project is, as the shell shows paths (the profile folder collapsed); null without one.</summary>
    [ObservableProperty]
    private string? _projectPathDisplay;

    public bool HasProject => ProjectName is not null;

    /// <summary>
    /// The text that suggests setting the project up, the first time it is opened (docs/04 section 21.9); null when there is nothing to
    /// suggest. It is only said: the configurator never opens by itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetupSuggestion))]
    private string? _setupSuggestion;

    public bool HasSetupSuggestion => SetupSuggestion is not null;

    /// <summary>The shell opened another project, or none (both null).</summary>
    public void ShowProject(string? name, string? pathDisplay)
    {
        ProjectName = name;
        ProjectPathDisplay = pathDisplay;
    }
}
