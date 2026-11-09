using System.Windows;
using AgentStudio.Domain.Team;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// The node of the repository of the Team layer (docs/17 D-10, F13-3): the folder, the branch and its short HEAD, the remotes
/// with their URL clean, the changes not committed (in the colour of a warning, with what happens), the stack and the main
/// folders, each one a connector of the node. It holds a <see cref="RepositoryNode"/>, whose remotes never carry a user nor a
/// password: no text of this node can show one.
/// </summary>
public sealed partial class RepositoryNodeViewModel : ObservableObject
{
    /// <summary>The id the node has in the layout file and for Nodify: the Team layer has only one repository.</summary>
    public const string LayoutId = "repository";

    private readonly CanvasText _text;

    public RepositoryNodeViewModel(RepositoryNode? node, string? problem, Point location, CanvasText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        _location = location;
        WholeProject = new TeamConnectorViewModel(this, TeamConnectorFlow.Input, text.Get("Canvas_Repo_WholeProject"), isWholeProject: true);
        Node = node;
        Problem = problem;
        Folders = [.. (node?.MainFolders ?? []).Select(f => new TeamConnectorViewModel(this, TeamConnectorFlow.Input, f, f))];
    }

    public RepositoryNode? Node { get; }

    /// <summary>Why the repository could not be read (a code of <see cref="RepositoryProblems"/> or the message of the git layer); null when it was.</summary>
    public string? Problem { get; }

    /// <summary>The connector of the whole project: an agent without folders works there, as it always did.</summary>
    public TeamConnectorViewModel WholeProject { get; }

    /// <summary>One connector per main folder; the cable «Trabaja en» goes to one of them.</summary>
    public IReadOnlyList<TeamConnectorViewModel> Folders { get; }

    /// <summary>Every connector of the node, as Nodify draws them: the whole project first and then each main folder.</summary>
    public IReadOnlyList<TeamConnectorViewModel> Connectors => [WholeProject, .. Folders];

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private Size _size;

    /// <summary>The run is integrating or delivering to this repository now (the cable «Entrega» of the Plan layer, docs/17 D-14).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntegratingText))]
    private bool _isIntegrating;

    /// <summary>The halo of the node moves while it integrates; off with «Reducir movimiento».</summary>
    [ObservableProperty]
    private bool _isAnimated;

    public string IntegratingText => IsIntegrating ? _text.Get("Canvas_Repo_Integrating") : string.Empty;

    public string Title => _text.Format("Canvas_Repo_Title", Node is null ? _text.Get("Canvas_Repo_Unknown") : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Node.Folder)));

    public string BranchText => Node is null ? string.Empty
        : Node.Branch is { Length: > 0 } branch ? _text.Format("Canvas_Repo_Branch", branch) : _text.Get("Canvas_Repo_DetachedHead");

    public string HeadText => Node is null ? string.Empty
        : Node.HeadShort is { Length: > 0 } head ? _text.Format("Canvas_Repo_Head", head) : _text.Get("Canvas_Repo_NoCommits");

    /// <summary>One line per remote, «origin: https://github.com/org/repo.git», always with the clean URL.</summary>
    public IReadOnlyList<string> RemoteLines => Node is null ? [] : [.. Node.Remotes.Select(r => $"{r.Name}: {r.Url}")];

    public string RemotesText => Node is null ? string.Empty
        : Node.Remotes.Count == 0 ? _text.Get("Canvas_Repo_NoRemotes") : string.Join(" · ", RemoteLines);

    /// <summary>There are changes not committed: it says what that means for a run (ADR-0013, point 7).</summary>
    public bool HasUncommittedChanges => Node?.HasUncommittedChanges == true;

    public string ChangesText => Node is null ? string.Empty
        : Node.HasUncommittedChanges ? _text.Get("Canvas_Repo_Dirty") : _text.Get("Canvas_Repo_Clean");

    public string StackText => Node?.Stack is { Length: > 0 } stack ? _text.Format("Canvas_Repo_Stack", stack) : string.Empty;

    /// <summary>Why the repository is not shown, in words; empty when it was read.</summary>
    public string ProblemText => Node is not null ? string.Empty
        : Problem == RepositoryProblems.Reading ? _text.Get("Canvas_Repo_Reading")
        : Problem == RepositoryProblems.NotComposed ? _text.Get("Canvas_Repo_NotComposed")
        : _text.Format("Canvas_Repo_NotRead", Problem ?? string.Empty);

    /// <summary>What a screen reader says of the node.</summary>
    public string AutomationName => Node is null
        ? $"{Title} · {ProblemText}"
        : string.Join(" · ", new[] { Title, BranchText, HeadText, RemotesText, ChangesText, StackText }.Where(p => p.Length > 0));

    /// <summary>The connector of a folder or of the whole project, by the folder a cable names (null: the whole project).</summary>
    public TeamConnectorViewModel? ConnectorOf(string? folder) =>
        folder is null ? WholeProject : Folders.FirstOrDefault(f => string.Equals(f.Folder, folder, StringComparison.Ordinal));

    public override string ToString() => AutomationName;
}
