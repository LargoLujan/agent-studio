using AgentStudio.Application.Coordination;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// The ports the «Trabajo» document talks to (docs/16 §4.6, F9-1 to F9-10), all Application contracts. Any of them may be missing (a
/// shell composed without them): each section then says what is missing instead of pretending, and offers nothing that would need it.
/// </summary>
/// <param name="Workspace">Opens the project, for its id.</param>
/// <param name="Milestones">Opens, links and reopens milestones and replays their state.</param>
/// <param name="Closer">Closes a milestone by auditing it (approval and consent are the audit's).</param>
/// <param name="Backlog">The backlog of requirements in draft.</param>
/// <param name="Board">The board, projected from the verified log.</param>
/// <param name="Links">Explicit trace links.</param>
/// <param name="Suggestions">Links the system infers; they count only when confirmed.</param>
/// <param name="Chain">The chain from a requirement to its delivery.</param>
/// <param name="Participation">Who took each of the six roles.</param>
/// <param name="Budgets">The budget of the project.</param>
/// <param name="Roster">The agents of the template: the judges that may close a milestone.</param>
/// <param name="Runs">The runs of the project, to link them to a milestone.</param>
/// <param name="Debates">The debates of the project, to link them to a milestone.</param>
/// <param name="Evidence">Opens the saved proof a node of the chain names (a diff, a report).</param>
/// <param name="Verdicts">Opens the verdict a node of the chain names.</param>
public sealed record WorkBackend(
    IProjectWorkspace? Workspace = null,
    IMilestoneService? Milestones = null,
    IMilestoneCloser? Closer = null,
    IBacklogService? Backlog = null,
    IBoardProjector? Board = null,
    ITraceLinkService? Links = null,
    ITraceSuggester? Suggestions = null,
    ITraceChainReader? Chain = null,
    IParticipationProjector? Participation = null,
    IProjectBudgetStore? Budgets = null,
    IAgentRoster? Roster = null,
    IRunRegistry? Runs = null,
    IDebateCoordinator? Debates = null,
    IEvidenceStore? Evidence = null,
    IVerdictStore? Verdicts = null)
{
    public static WorkBackend None { get; } = new();
}

/// <summary>
/// What the sections of the document share: the ports, the clock, the project open, the milestone selected and the node the chain,
/// the participation and the links look at (<see cref="Focus"/>). A section that writes calls <see cref="NotifyWritten"/>, and the
/// document reloads them all from the verified log, so no section shows what it remembers.
/// </summary>
public sealed class WorkContext(WorkBackend backend, TimeProvider time)
{
    public WorkBackend Backend { get; } = backend;

    public TimeProvider Time { get; } = time;

    /// <summary>The person, as the entries of the log name them (the same name every approval uses).</summary>
    public string Person => ApprovalPromptService.DecidedBy;

    public ProjectContext? Project { get; set; }

    public MilestoneId? SelectedMilestone { get; private set; }

    public TraceRef? Focus { get; private set; }

    /// <summary>The milestone selected changed (or none): the board and the participation read it.</summary>
    public event EventHandler? MilestoneSelected;

    /// <summary>The node to look at changed: the chain, the participation and the links read it.</summary>
    public event EventHandler? FocusChanged;

    /// <summary>Something was written to the log: every section reloads.</summary>
    public event EventHandler? Written;

    public void SelectMilestone(MilestoneId? milestone)
    {
        if (SelectedMilestone?.Value == milestone?.Value)
        {
            return;
        }

        SelectedMilestone = milestone;
        MilestoneSelected?.Invoke(this, EventArgs.Empty);
    }

    public void LookAt(TraceRef? node)
    {
        Focus = node;
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyWritten() => Written?.Invoke(this, EventArgs.Empty);

    public string LocalTime(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
