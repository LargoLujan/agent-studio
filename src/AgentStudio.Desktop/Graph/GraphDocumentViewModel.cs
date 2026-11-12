using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Graph;

/// <summary>
/// The «Auto Graph» document (docs/20 section 6.4): one tab for the session that follows the open project, with the sections the composition gives it
/// («Grafo», «Diagramas», «Discrepancias»). It is the host only: it knows no concrete section, tolerates none, and tells each one when the project changes.
/// It has no view of its own yet: its template is added with the first section, by the lead (<c>Themes/DocumentTemplates.xaml</c>).
/// </summary>
public sealed partial class GraphDocumentViewModel : DocumentViewModel
{
    public const string DocumentId = "graph:project";

    private readonly List<GraphSection> _sections;

    private readonly IProjectWorkspace? _workspace;

    /// <param name="workspace">Opens the project the shell follows as a <see cref="ProjectContext"/> (<see cref="SetProjectRootAsync"/>); without it only <see cref="SetProjectAsync"/> sets one.</param>
    public GraphDocumentViewModel(IEnumerable<GraphSection> sections, IProjectWorkspace? workspace = null)
        : base(DocumentId, Texts.Get("Graph_Title"))
    {
        ArgumentNullException.ThrowIfNull(sections);
        _workspace = workspace;
        _sections = [.. sections];
        if (_sections.Any(s => s is null))
        {
            throw new ArgumentException("A section is not null.", nameof(sections));
        }

        _selectedSection = _sections.FirstOrDefault();
    }

    /// <summary>The sections in the order the list shows them; empty while no package has put one.</summary>
    public IReadOnlyList<GraphSection> Sections => _sections;

    [ObservableProperty]
    private GraphSection? _selectedSection;

    /// <summary>The project the document shows; null when none is open.</summary>
    public ProjectContext? Project { get; private set; }

    public bool HasProject => Project is not null;

    /// <summary>The project open in the shell changed (null: none): every section drops what it held of the other one and reads again.</summary>
    public async Task SetProjectAsync(ProjectContext? project, CancellationToken ct)
    {
        Project = project;
        foreach (var section in _sections)
        {
            section.SetProject(project);
        }

        OnPropertyChanged(nameof(HasProject));
        await ReloadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>The shell follows a project by its folder (null: none): the document opens it as the project the sections read. Without a workspace nothing is shown.</summary>
    public async Task SetProjectRootAsync(string? projectRoot, CancellationToken ct)
    {
        ProjectContext? project = null;
        if (projectRoot is { Length: > 0 } && _workspace is { } workspace)
        {
            try
            {
                project = await workspace.OpenAsync(projectRoot, ct).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // The store of projects is behind a port: a project that cannot be opened is shown as none, never thrown into the UI.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                project = null;
            }
        }

        await SetProjectAsync(project, ct).ConfigureAwait(true);
    }

    /// <summary>Reads every section again, one after the other.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        foreach (var section in _sections)
        {
            ct.ThrowIfCancellationRequested();
            await section.ReloadAsync(ct).ConfigureAwait(true);
        }
    }
}
