using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// Spanish names of what a verdict says (docs/03 §6 and docs/15 D-42), shared by the card of a run, of a debate and of an
/// audit: decisions with their tone, confidence, the status of each requirement, findings and their severity, the rules of
/// the validator, where a challenge stands and the disposition of a review.
/// </summary>
public static class VerdictTexts
{
    public static string Decision(VerdictDecision decision) => decision switch
    {
        VerdictDecision.Approved => "Aprobado",
        VerdictDecision.ChangesRequested => "Cambios solicitados",
        VerdictDecision.Rejected => "Rechazado",
        VerdictDecision.EscalateToHuman => "Escalado a una persona",
        VerdictDecision.Unresolved => "Sin resolver",
        _ => decision.ToString(),
    };

    public static Tone ToneOf(VerdictDecision decision) => decision switch
    {
        VerdictDecision.Approved => Tone.Success,
        VerdictDecision.ChangesRequested or VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved => Tone.Warning,
        VerdictDecision.Rejected => Tone.Error,
        _ => Tone.Neutral,
    };

    public static string Confidence(ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.Low => "baja",
        ConfidenceLevel.Medium => "media",
        ConfidenceLevel.High => "alta",
        _ => level.ToString(),
    };

    public static string Status(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Satisfied => "Satisfecho",
        AssessmentStatus.NotSatisfied => "Incumplido",
        AssessmentStatus.NotVerifiable => "No verificable",
        _ => status.ToString(),
    };

    /// <summary>The glyph of a requirement's status; the word goes next to it, so the glyph is never the only cue.</summary>
    public static string Glyph(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Satisfied => "✔",
        AssessmentStatus.NotSatisfied => "✖",
        _ => "?",
    };

    public static Tone ToneOf(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Satisfied => Tone.Success,
        AssessmentStatus.NotSatisfied => Tone.Error,
        _ => Tone.Warning,
    };

    public static string Category(FindingCategory category) => category switch
    {
        FindingCategory.IgnoredRequirement => "Requisito ignorado",
        FindingCategory.OmittedTask => "Tarea omitida",
        FindingCategory.FabricatedResult => "Resultado inventado",
        FindingCategory.TestsNotRun => "Pruebas sin ejecutar",
        FindingCategory.ContradictoryDecision => "Decisión contradictoria",
        FindingCategory.OutOfScopeChange => "Cambio fuera de alcance",
        FindingCategory.IntegrationError => "Error de integración",
        FindingCategory.SecurityIssue => "Problema de seguridad",
        FindingCategory.PrematureCompletion => "Finalización prematura",
        FindingCategory.InstructionViolation => "Instrucción incumplida",
        FindingCategory.Other => "Otro",
        _ => category.ToString(),
    };

    public static string Severity(Severity severity) => severity switch
    {
        Domain.Judging.Severity.Low => "Baja",
        Domain.Judging.Severity.Medium => "Media",
        Domain.Judging.Severity.High => "Alta",
        Domain.Judging.Severity.Critical => "Crítica",
        _ => severity.ToString(),
    };

    public static Tone ToneOf(Severity severity) => severity switch
    {
        Domain.Judging.Severity.Low => Tone.Neutral,
        Domain.Judging.Severity.Medium => Tone.Warning,
        _ => Tone.Error,
    };

    /// <summary>What a rule of the validator found, in Spanish (the validator's own message, in English, goes with it as detail).</summary>
    public static string Violation(VerdictViolationCode code) => code switch
    {
        VerdictViolationCode.RequirementSetMismatch => "Cita otro conjunto de requisitos",
        VerdictViolationCode.DecisionLogMismatch => "Cita otro estado del registro de decisiones",
        VerdictViolationCode.RequirementNotAssessed => "Deja un requisito sin evaluar",
        VerdictViolationCode.UnknownRequirementAssessed => "Evalúa un requisito que no existe",
        VerdictViolationCode.DuplicateAssessment => "Evalúa un requisito dos veces",
        VerdictViolationCode.EvidenceNotInDossier => "Cita una evidencia que no está en el dossier",
        VerdictViolationCode.EvidenceCitedButNotExamined => "Cita una evidencia que no dice haber examinado",
        VerdictViolationCode.SatisfiedWithoutEvidence => "Da por satisfecho un requisito sin evidencias",
        VerdictViolationCode.ApprovedWithUnmetMandatoryRequirement => "Aprueba con un requisito obligatorio incumplido",
        VerdictViolationCode.ApprovedOnAgentClaimsOnly => "Aprueba solo con afirmaciones de agentes",
        VerdictViolationCode.ApprovedWithFailingTests => "Aprueba con pruebas que fallan",
        VerdictViolationCode.ApprovedWithoutTestRun => "Aprueba sin una ejecución de pruebas",
        VerdictViolationCode.ApprovedWithCriticalFinding => "Aprueba con un hallazgo crítico",
        VerdictViolationCode.MissingConfidenceJustification => "No justifica su confianza",
        VerdictViolationCode.HumanInterventionNotFlagged => "No marca que necesita a una persona",
        VerdictViolationCode.MissingCorrectiveActions => "Pide cambios o rechaza sin acciones correctivas",
        VerdictViolationCode.JudgeNotIndependent => "El juez no es independiente",
        VerdictViolationCode.SatisfiedByFailingTestRun => "Da por satisfecho un requisito con pruebas que fallan",
        VerdictViolationCode.ApprovedWithoutEvidenceForCriterion => "Aprueba sin evidencia de un criterio",
        VerdictViolationCode.MalformedVerdict => "Veredicto mal formado",
        VerdictViolationCode.ApprovedPendingHumanIntervention => "Aprueba aunque necesita a una persona",
        VerdictViolationCode.ApprovedWithIntegrityProblems => "Aprueba con problemas de integridad del material",
        VerdictViolationCode.ApprovedWithoutTestRunForCommit => "Aprueba sin pruebas del commit revisado",
        VerdictViolationCode.DebateResolutionMissing => "Aprueba o pide cambios sin elegir propuesta",
        VerdictViolationCode.DebateResolutionUnknown => "Elige o compara una propuesta que no es de este debate",
        _ => code.ToString(),
    };

    public static string Challenge(ChallengeStatus status) => status switch
    {
        ChallengeStatus.None => "Sin impugnar.",
        ChallengeStatus.Pending => "Impugnado: la revisión no se ha cerrado y la entrega espera.",
        ChallengeStatus.Confirmed => "Impugnado: la revisión confirmó lo que decía el veredicto.",
        ChallengeStatus.Revoked => "Impugnado: la revisión retiró la aprobación y la entrega queda bloqueada.",
        ChallengeStatus.AwaitingAcceptance => "Impugnado: la revisión dio la vuelta hacia una aprobación, que solo cuenta si la aceptas.",
        ChallengeStatus.Accepted => "Impugnado: aceptaste la revisión que da la vuelta hacia una aprobación.",
        ChallengeStatus.DecidedByUser => "Impugnado: la revisión necesitó a una persona y decidiste tú.",
        _ => status.ToString(),
    };

    public static Tone ToneOf(ChallengeStatus status) => status switch
    {
        ChallengeStatus.None => Tone.Neutral,
        ChallengeStatus.Confirmed or ChallengeStatus.Accepted or ChallengeStatus.DecidedByUser => Tone.Success,
        ChallengeStatus.Revoked => Tone.Error,
        _ => Tone.Warning,
    };

    public static string Disposition(ReviewDisposition disposition) => disposition switch
    {
        ReviewDisposition.Approved => "Aprobado",
        ReviewDisposition.ChangesRequested => "Cambios solicitados",
        ReviewDisposition.Rejected => "Rechazado",
        ReviewDisposition.NeedsHuman => "Necesita una persona",
        ReviewDisposition.RateLimited => "Sin veredicto: el proveedor del juez alcanzó su límite de uso",
        _ => disposition.ToString(),
    };

    public static Tone ToneOf(ReviewDisposition disposition) => disposition switch
    {
        ReviewDisposition.Approved => Tone.Success,
        ReviewDisposition.Rejected => Tone.Error,
        _ => Tone.Warning,
    };
}
