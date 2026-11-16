using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>The step «Inicio» proposes for the open project (docs/24 §3.2), from what the shell can read of it.</summary>
public enum NextStepKind
{
    /// <summary>The configurator was never applied to the project.</summary>
    SetUp,

    /// <summary>The project is set up and has no frozen requirements yet.</summary>
    FirstRequest,

    /// <summary>A run of the project is going on (paused or waiting included).</summary>
    RunGoingOn,

    /// <summary>Anything else, including what could not be read: a new request, which claims nothing about the project.</summary>
    NewRequest,
}

/// <summary>The card «Siguiente paso»: why this is the next step, and one button that takes it.</summary>
public sealed class NextStepViewModel(NextStepKind kind, string reason, string action, ICommand command)
{
    public NextStepKind Kind { get; } = kind;

    /// <summary>One sentence: why this is the next step.</summary>
    public string Reason { get; } = reason;

    /// <summary>The text of the button.</summary>
    public string Action { get; } = action;

    public ICommand Command { get; } = command;

    /// <summary>What a screen reader says of the step: the text of its button.</summary>
    public override string ToString() => Action;
}

/// <summary>
/// Landing tab, «Inicio». Without a project: «Abrir carpeta» and the recent projects. With one open: its name, the next step
/// with one button (docs/24 §3.2), «Abrir otra carpeta» and the recent projects. The ways into each screen are in the
/// activity bar, with their names; this page does not repeat them.
/// </summary>
public sealed partial class WelcomeDocumentViewModel : DocumentViewModel
{
    public const string Id = "welcome";

    public WelcomeDocumentViewModel(
        ObservableCollection<RecentProjectItem> recentProjects,
        ICommand openFolderCommand,
        ICommand openRecentCommand)
        : base(Id, Texts.Get("Welcome_Title"))
    {
        RecentProjects = recentProjects;
        OpenFolderCommand = openFolderCommand;
        OpenRecentCommand = openRecentCommand;
    }

    public ObservableCollection<RecentProjectItem> RecentProjects { get; }

    public ICommand OpenFolderCommand { get; }

    /// <summary>Parameter: the project path.</summary>
    public ICommand OpenRecentCommand { get; }

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

    /// <summary>The card «Siguiente paso» of the open project; null without a project, or until the shell has read it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextStep))]
    private NextStepViewModel? _nextStep;

    public bool HasNextStep => NextStep is not null;

    /// <summary>The shell opened another project, or none (both null).</summary>
    public void ShowProject(string? name, string? pathDisplay)
    {
        ProjectName = name;
        ProjectPathDisplay = pathDisplay;
    }
}
