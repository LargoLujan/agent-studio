using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Infrastructure.Persistence;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>Creates the «Orquestador» document of a project.</summary>
public interface IOrchestratorDocumentFactory
{
    OrchestratorDocumentViewModel Create(string projectRoot);
}

/// <summary>
/// Creates the documents on the calling thread (the UI thread: the activity bar opens them), so their monitors apply the
/// run's events on that thread's context.
/// </summary>
public sealed class OrchestratorDocumentFactory : IOrchestratorDocumentFactory
{
    private readonly OrchestrationSession _session;
    private readonly ICanvasLayoutStore _layout;
    private readonly TimeProvider _time;
    private readonly ReinforcedConfirmation? _confirmation;

    /// <param name="confirmation">Windows Hello for «Aprobar plan» (docs/16 D-13); without it the button alone approves.</param>
    public OrchestratorDocumentFactory(OrchestrationSession session, ICanvasLayoutStore layout, TimeProvider time, ReinforcedConfirmation? confirmation = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _layout = layout;
        _time = time;
        _confirmation = confirmation;
    }

    /// <summary>
    /// No backend at all: the canvas works, and the screen says «Motor no disponible». Without a workspace the project's
    /// storage is never opened, so the positions stay in memory. What the shell uses when nothing else was composed.
    /// </summary>
    public static OrchestratorDocumentFactory WithoutEngine(TimeProvider? time = null) =>
        new(new OrchestrationSession(OrchestrationBackend.None), new FileCanvasLayoutStore(new ProjectStorageFiles()), time ?? TimeProvider.System);

    /// <summary>The draft of each project is kept where its canvas positions are, when the layout store keeps drafts too.</summary>
    public OrchestratorDocumentViewModel Create(string projectRoot) =>
        new(projectRoot, _session, _layout, _time, confirmation: _confirmation, drafts: _layout as IPlanDraftStore);
}
