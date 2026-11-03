using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// The fixed, versioned requirements of a mode (docs/15 D-36: <c>orq-1</c> to <c>orq-7</c> for «Revisión del orquestador»),
/// as the debate module defines them; null for a mode whose requirements the user writes or that come from its subject.
/// </summary>
public delegate FrozenRequirementSet? DebateModeRequirements(DebateMode mode);

/// <summary>
/// The services the «Debates» and «Auditoría» documents talk to (docs/15 D-41 to D-43), all through Application ports.
/// Any of them may be missing (a shell composed without them): each screen then says what is missing instead of
/// pretending, and offers nothing that would need it.
/// </summary>
/// <param name="Coordinator">Prepares, opens, resumes, lists and reads debates.</param>
/// <param name="Challenges">Challenges a stored verdict with a revision debate.</param>
/// <param name="Audits">«Auditar proyecto completo».</param>
/// <param name="Workspace">Opens the project, for its id.</param>
/// <param name="Roster">The agents of the project's template: participants and judges.</param>
/// <param name="Requirements">The frozen requirement sets: the latest one for an audit, a run's and a verdict's.</param>
/// <param name="Runs">The runs of the project, as the subject of a debate.</param>
/// <param name="Verdicts">The stored verdicts, for the card and the challenge.</param>
/// <param name="Evidence">Opens the evidence a contribution cites.</param>
/// <param name="FixedRequirements">The fixed requirements of the modes that have them.</param>
public sealed record DebateScreenBackend(
    IDebateCoordinator? Coordinator = null,
    IVerdictChallenges? Challenges = null,
    IProjectAuditService? Audits = null,
    IProjectWorkspace? Workspace = null,
    IAgentRoster? Roster = null,
    IRequirementStore? Requirements = null,
    IRunRegistry? Runs = null,
    IVerdictStore? Verdicts = null,
    IEvidenceStore? Evidence = null,
    DebateModeRequirements? FixedRequirements = null)
{
    public static DebateScreenBackend None { get; } = new();
}
