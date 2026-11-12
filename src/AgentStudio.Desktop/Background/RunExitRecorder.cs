using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// Writes what was chosen when the app was left with a piece of work going (docs/16 D-16, F8-9) in the log of that work: a
/// <c>RunExitChosen</c> entry of its run, debate or audit. The managed exit writes through it and never through the engine.
/// </summary>
public interface IRunExitRecorder
{
    /// <summary>Records <paramref name="choice"/> in the log of <paramref name="project"/>, for the work <paramref name="workId"/>.</summary>
    /// <exception cref="DecisionLogIntegrityException">The log of the project does not verify: nothing is written.</exception>
    Task RecordAsync(ProjectContext project, string workId, RunExitChoice choice, CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="IRunExitRecorder"/> of the app, over the only writer of each project's log (<see cref="IDecisionRecorder"/>):
/// one <c>RunExitChosen</c> (version 1) per piece of work, as docs/04 §18.2 fixes it. The person's choices are signed by
/// the user who answered the dialog (<see cref="ApprovalPromptService.DecidedBy"/>, as the approvals); a session that
/// Windows ended is signed by the system, and its <see cref="RunExitChosenPayload.DecidedBy"/> says it was Windows.
/// </summary>
public sealed class DecisionLogRunExitRecorder : IRunExitRecorder
{
    /// <summary>Who decided a <see cref="RunExitChoice.SessionEnded"/>: Windows ended the session, nobody chose it.</summary>
    public const string WindowsDecided = "windows";

    /// <summary>The system actor of the log, the one the recorder itself signs with.</summary>
    public static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly IDecisionRecorder _decisions;

    public DecisionLogRunExitRecorder(IDecisionRecorder decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        _decisions = decisions;
    }

    public async Task RecordAsync(ProjectContext project, string workId, RunExitChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        if (!Enum.IsDefined(choice))
        {
            throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown exit choice.");
        }

        var person = choice != RunExitChoice.SessionEnded;
        var decidedBy = person ? ApprovalPromptService.DecidedBy : WindowsDecided;
        var actor = person ? new Actor(ActorKind.User, decidedBy) : SystemActor;
        await _decisions.RecordAsync(
            project.ProjectId,
            actor,
            DecisionKind.RunExitChosen,
            Summary(choice),
            new RunExitChosenPayload(choice, decidedBy) { RunId = workId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The summary of the entry, in Spanish: the choice as the dialog said it when the entry was first written. It is history of
    /// the log, signed with the rest of the entry and never translated (docs/17 D-48), so it does not follow the language of the
    /// interface, nor the wording of the dialog, which can be clearer.
    /// </summary>
    public static string Summary(RunExitChoice choice) => choice switch
    {
        RunExitChoice.PauseAndExitWhenIdle => "Pausar y salir cuando terminen los intentos en curso",
        RunExitChoice.ExitNow => "Salir ahora y dejarla interrumpida",
        RunExitChoice.SessionEnded => "Windows cerró la sesión: cuenta como salir ahora",
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown exit choice."),
    };
}
