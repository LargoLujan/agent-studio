using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// What the review screens ask of the shell (the integration implements it over <c>MainViewModel</c>): show one of their
/// documents, and open the orchestrator of a project with a request («Crear tareas correctivas»). Without it, the actions
/// that leave a screen are not offered.
/// </summary>
public interface IReviewNavigation
{
    /// <summary>Adds the document to the dock if it is not there and activates it.</summary>
    void Show(DocumentViewModel document);

    /// <summary>Opens the «Orquestador» of the project with <paramref name="request"/> as its request; nothing is planned or run.</summary>
    Task CreateCorrectiveTasksAsync(string projectRoot, string request);
}

/// <summary>
/// The «Debates» and «Auditoría» documents of the session, one of each per project, created the first time on the calling
/// (UI) thread and kept, so closing a tab loses neither a debate being followed nor the report on screen; and the moves
/// between them: «Impugnar» from an audit or from the run monitor opens the challenge in the «Debates» of the project.
/// </summary>
public sealed class DebateScreens
{
    private readonly Dictionary<string, DebatesDocumentViewModel> _debates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectAuditDocumentViewModel> _audits = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly Func<IReviewNavigation?>? _navigation;

    /// <param name="backend">The ports of the screens; any may be missing.</param>
    /// <param name="time">The clock and the local zone of the dates shown.</param>
    /// <param name="navigation">The shell, resolved when it is needed (it is built after this).</param>
    public DebateScreens(DebateScreenBackend backend, TimeProvider time, Func<IReviewNavigation?>? navigation = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        Backend = backend;
        _time = time;
        _navigation = navigation;
    }

    public DebateScreenBackend Backend { get; }

    /// <summary>The shell can show a document and open the orchestrator: the actions that leave a screen are offered.</summary>
    public bool CanNavigate => _navigation?.Invoke() is not null;

    /// <summary>The «Debates» document of the project, created the first time.</summary>
    public DebatesDocumentViewModel Debates(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!_debates.TryGetValue(projectRoot, out var document))
        {
            document = new DebatesDocumentViewModel(projectRoot, Backend, _time, this);
            _debates[projectRoot] = document;
        }

        return document;
    }

    /// <summary>The «Auditoría» document of the project, created the first time.</summary>
    public ProjectAuditDocumentViewModel Audit(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!_audits.TryGetValue(projectRoot, out var document))
        {
            document = new ProjectAuditDocumentViewModel(projectRoot, Backend, _time, this);
            _audits[projectRoot] = document;
        }

        return document;
    }

    /// <summary>«Impugnar» from outside «Debates» (an audit, the run monitor): shows the «Debates» of the project on the challenge of that verdict.</summary>
    public async Task ChallengeAsync(string projectRoot, string verdictId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verdictId);
        var document = Debates(projectRoot);
        _navigation?.Invoke()?.Show(document);
        await document.OpenAsync().ConfigureAwait(true);
        await document.StartChallengeAsync(verdictId).ConfigureAwait(true);
    }

    /// <summary>«Crear tareas correctivas»: the orchestrator of the project with the request; nothing is planned without you.</summary>
    public Task CreateCorrectiveTasksAsync(string projectRoot, string request) =>
        _navigation?.Invoke() is { } navigation ? navigation.CreateCorrectiveTasksAsync(projectRoot, request) : Task.CompletedTask;
}
