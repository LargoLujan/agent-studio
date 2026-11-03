using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// What each mode asks of its participants (docs/15 D-07) and the requirements a mode fixes: those of an orchestrator review
/// are <c>orq-1</c> to <c>orq-7</c>, one per check of the auditor of the orchestrator (D-33 and D-36), mandatory and judged
/// by the judge. The texts for the agents are in English, like every prompt; the requirements are the user's, in Spanish.
/// </summary>
public static class DebateModes
{
    /// <summary>The criterion every requirement of an orchestrator review has: a valuation of the judge (D-36).</summary>
    public const string OrchestratorCriterion = "c1";

    private static readonly (string Id, string Statement, string Criterion)[] OrchestratorChecks =
    [
        ("orq-1", "Las tareas están asignadas a agentes con el rol y los permisos adecuados.", "El registro muestra cada tarea asignada a un agente cuyo rol y permisos la permiten."),
        ("orq-2", "Las dependencias del plan se respetan.", "Ninguna tarea arranca antes de que terminen sus dependencias y de que se evalúen sus condiciones."),
        ("orq-3", "Las instrucciones de cada ejecutor son completas.", "Cada lanzamiento de un ejecutor lleva antes sus instrucciones, con el objetivo y el texto literal de los requisitos que cubre."),
        ("orq-4", "No se omite ninguna revisión obligatoria.", "Toda tarea cuyo ejecutor exige revisión independiente tiene su revisión pedida y su veredicto."),
        ("orq-5", "Los permisos se respetan.", "Cada tarea cambia solo lo que permite su alcance de escritura, y ninguna tarea de solo lectura deja cambios."),
        ("orq-6", "Se piden las aprobaciones necesarias.", "El plan, la entrega y todo borrado con cambios tienen su aprobación atada antes de hacerse."),
        ("orq-7", "Cada integración se verifica antes de usarse.", "Cada integración va seguida del build y los tests del commit resultante, capturados por el sistema y en verde, antes de que algo dependa de ella."),
    ];

    /// <summary>
    /// The requirements of an orchestrator review (D-36), frozen at <paramref name="frozenAt"/>: always the same content, so
    /// always the same <see cref="FrozenRequirementSet.ContentHash"/>.
    /// </summary>
    public static FrozenRequirementSet OrchestratorReview(DateTimeOffset frozenAt) => FrozenRequirementSet.Freeze(
        OrchestratorChecks.Select(c => new Requirement(
            new RequirementId(c.Id),
            c.Statement,
            RequirementPriority.Mandatory,
            [new AcceptanceCriterion(OrchestratorCriterion, c.Criterion, VerificationMethod.JudgeAssessment)])),
        frozenAt);

    /// <summary>Whether the mode chooses among its contributions or reviews work others produced (D-07), as the prompt says it.</summary>
    public static string Purpose(DebateMode mode) => mode switch
    {
        DebateMode.Free => "a free debate: the participants propose answers to the question and the judge chooses the best one, or none.",
        DebateMode.Adversarial => "an adversarial debate: the proponents defend a solution and the critics look for its errors, contradictions, risks and unmet requirements; the judge decides whether it holds.",
        DebateMode.ArchitectureReview => "an architecture review: the participants propose designs and compare them on maintainability, security, performance, cost and complexity, besides the requirements; the judge recommends one.",
        DebateMode.ImplementationAudit => "an audit of an implementation: the proponents defend that a finished run meets its frozen requirements and the critics question it, with the evidence of the run; the judge decides whether it complies.",
        DebateMode.OrchestratorReview => "a review of the orchestrator: the reviewers check the decision log of a run and the report of its auditor against the requirements orq-1 to orq-7; the judge decides whether the orchestrator kept its protocol.",
        DebateMode.Tribunal => "a tribunal: each panelist assesses the subject on its own and there is no deliberation; the judge synthesises the assessments.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown debate mode."),
    };

    /// <summary>What the participant with <paramref name="role"/> does in <paramref name="round"/> of <paramref name="mode"/> (D-07).</summary>
    public static string Task(DebateMode mode, DebateRole role, int round)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);
        if (round == 1)
        {
            return (mode, role) switch
            {
                (DebateMode.Tribunal, _) => "Write your independent assessment of the subject against the requirements (kind Assessment). You see nothing of the other panelists.",
                (DebateMode.Adversarial, DebateRole.Critic) => "Write your independent critique (kind Proposal): what an answer to the question must get right and where it usually fails. You do not see the proposal yet.",
                (DebateMode.ImplementationAudit, DebateRole.Proponent) => "Write your independent defence (kind Proposal): why the run meets each frozen requirement, citing the evidence of its dossier.",
                (DebateMode.ImplementationAudit, _) => "Write your independent questioning (kind Proposal): which frozen requirements the run may not meet and why, citing the evidence of its dossier.",
                (DebateMode.OrchestratorReview, _) => "Write your independent review (kind Proposal) of the decision log of the run and the report of its auditor against orq-1 to orq-7, one finding per claim.",
                (DebateMode.ArchitectureReview, _) => "Propose your design independently (kind Proposal), with its trade-offs.",
                _ => "Propose your answer to the question independently (kind Proposal). You see nothing of the other participants.",
            };
        }

        return (mode, role) switch
        {
            (DebateMode.Adversarial or DebateMode.ImplementationAudit, DebateRole.Critic) =>
                "Object (kind Objection) to the errors, contradictions, risks and unmet requirements you find in what the proponents published, with evidence; or bring evidence (kind Evidence).",
            (DebateMode.Adversarial or DebateMode.ImplementationAudit, DebateRole.Proponent) =>
                "Rebut (kind Rebuttal) the objections to your position with evidence, or concede what is right; or bring evidence (kind Evidence).",
            (DebateMode.ArchitectureReview, _) =>
                "Compare the published designs (kind Objection, Rebuttal or Evidence) on maintainability, security, performance, cost and complexity, besides the requirements.",
            (DebateMode.OrchestratorReview, _) =>
                "Contrast the findings published by the other reviewers (kind Objection, Rebuttal or Evidence): confirm them with evidence of the log, or show where they are wrong.",
            _ => "Object (kind Objection) to what you find wrong in the published contributions, rebut (kind Rebuttal) the objections to yours, or bring evidence (kind Evidence).",
        };
    }
}
