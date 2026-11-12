using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// The «Trabajo» document (docs/16 §4.6, docs/03): milestones, backlog, board, chain, participants, links and budgets of the project
/// open, one tab for the session that follows the project like «Memoria» does. Every section is a projection of the verified log
/// (or, for the budget of the project, of a file of the person on this computer): after anything is written, they all read again, and
/// none shows what it remembers. A port that was not composed is said in its section and offers nothing that would need it.
/// </summary>
public sealed partial class WorkDocumentViewModel : DocumentViewModel
{
    public const string DocumentId = "work:project";

    private readonly List<WorkSection> _sections;
    private int _generation;

    public WorkDocumentViewModel(WorkBackend backend, TimeProvider time)
        : base(DocumentId, Texts.Get("Work_Title"))
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        Backend = backend;
        Context = new WorkContext(backend, time);
        Milestones = new MilestonesSection(Context);
        Backlog = new BacklogSection(Context);
        Board = new BoardSection(Context);
        Chain = new ChainSection(Context);
        Participation = new ParticipationSection(Context);
        Links = new LinksSection(Context);
        Budgets = new BudgetsSection(Context);
        _sections = [Milestones, Backlog, Board, Chain, Participation, Links, Budgets];
        _selectedSection = Milestones;
        StatusMessage = Texts.Get("Work_Status_OpenFolder");

        // Anything written to the log makes every section read it again; «Ver cadena» takes the person to the chain.
        Context.Written += (_, _) => _ = ReloadAsync();
        Context.FocusChanged += (_, _) =>
        {
            if (Context.Focus is not null)
            {
                SelectedSection = Chain;
            }
        };
    }

    public WorkBackend Backend { get; }

    public WorkContext Context { get; }

    public MilestonesSection Milestones { get; }

    public BacklogSection Backlog { get; }

    public BoardSection Board { get; }

    public ChainSection Chain { get; }

    public ParticipationSection Participation { get; }

    public LinksSection Links { get; }

    public BudgetsSection Budgets { get; }

    /// <summary>The sections in the order the list shows them.</summary>
    public IReadOnlyList<WorkSection> Sections => _sections;

    [ObservableProperty]
    private WorkSection _selectedSection;

    /// <summary>The project the document shows; null when none is open.</summary>
    public string? ProjectRoot { get; private set; }

    public bool HasProject => Context.Project is not null;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The project open in the shell changed (null: none): the sections show it, or say there is none.</summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        ProjectRoot = projectRoot;
        Context.Project = null;
        Context.SelectMilestone(null);
        Context.LookAt(null);
        ErrorMessage = null;
        foreach (var section in _sections)
        {
            section.OnProjectChanged();
        }

        OnPropertyChanged(nameof(HasProject));
        if (projectRoot is null)
        {
            StatusMessage = Texts.Get("Work_Status_OpenFolder");
            return;
        }

        if (Backend.Workspace is not { } workspace)
        {
            StatusMessage = Texts.Get("Work_Status_Unavailable");
            return;
        }

        try
        {
            Context.Project = await workspace.OpenAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = null;
        }
#pragma warning disable CA1031 // The workspace is behind a port: a failure is said on the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Work_Error", ex.Message);
            return;
        }

        OnPropertyChanged(nameof(HasProject));
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Reads every section again from the verified log.</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        foreach (var section in _sections)
        {
            if (generation != _generation)
            {
                return;
            }

            await section.ReloadAsync().ConfigureAwait(true);
            section.RaiseHasProject();
        }
    }
}
