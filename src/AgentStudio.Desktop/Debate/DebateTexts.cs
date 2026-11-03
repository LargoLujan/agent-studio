using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// Spanish names of what a debate shows (docs/15 D-07, D-13, D-20 and D-41): modes and the roles each one needs, roles,
/// phases, outcomes, kinds of contribution, limits, why it went to judgment, dispositions of the judgment with their
/// explanation, subjects, what it waits for and the rule that closed it.
/// </summary>
public static class DebateTexts
{
    public static string Mode(DebateMode mode) => mode switch
    {
        DebateMode.Free => "Libre",
        DebateMode.Adversarial => "Adversarial",
        DebateMode.ArchitectureReview => "Revisión de arquitectura",
        DebateMode.ImplementationAudit => "Auditoría de implementación",
        DebateMode.OrchestratorReview => "Revisión del orquestador",
        DebateMode.Tribunal => "Tribunal",
        _ => mode.ToString(),
    };

    /// <summary>The participants each mode needs and what it is about (D-07).</summary>
    public static string ModeHelp(DebateMode mode) => mode switch
    {
        DebateMode.Free => "Dos participantes o más, con cualquier papel. Propuestas en la ronda 1; objeciones, réplicas y evidencias después. El juez elige una propuesta o «no resuelto».",
        DebateMode.Adversarial => "Un proponente y un crítico como mínimo. El crítico busca errores, contradicciones, riesgos e incumplimientos; el proponente replica.",
        DebateMode.ArchitectureReview => "Dos participantes o más con diseños independientes. Se comparan por mantenibilidad, seguridad, rendimiento, coste y complejidad, más tus criterios.",
        DebateMode.ImplementationAudit => "Un proponente que defiende la entrega y un crítico como mínimo, sobre una ejecución terminada con commit candidato y sus requisitos congelados.",
        DebateMode.OrchestratorReview => "Dos revisores o más (críticos o panelistas) sobre el registro de una ejecución y el informe del auditor, con los requisitos fijos orq-1 a orq-7.",
        DebateMode.Tribunal => "Dos panelistas o más, y solo panelistas: evaluaciones independientes, sin deliberación, y el juez sintetiza.",
        _ => string.Empty,
    };

    public static string Role(DebateRole role) => role switch
    {
        DebateRole.Proponent => "Proponente",
        DebateRole.Critic => "Crítico",
        DebateRole.Panelist => "Panelista",
        _ => role.ToString(),
    };

    public static string Phase(DebatePhase phase) => phase switch
    {
        DebatePhase.Proposals => "Propuestas",
        DebatePhase.Deliberation => "En deliberación",
        DebatePhase.AwaitingJudgment => "Esperando el juicio",
        DebatePhase.Closed => "Cerrado",
        _ => phase.ToString(),
    };

    public static string Outcome(DebateOutcome outcome) => outcome switch
    {
        DebateOutcome.Open => "Abierto",
        DebateOutcome.Resolved => "Resuelto",
        DebateOutcome.Unresolved => "Sin resolver",
        DebateOutcome.EscalatedToUser => "Necesita una persona",
        DebateOutcome.Aborted => "Abortado",
        _ => outcome.ToString(),
    };

    /// <summary>The tone of a debate's state dot: running is active, waiting for a person is a warning, an abort is neutral.</summary>
    public static Tone ToneOf(DebatePhase phase, DebateOutcome outcome, bool interrupted = false) => (phase, outcome) switch
    {
        _ when interrupted => Tone.Warning,
        (_, DebateOutcome.Resolved) => Tone.Success,
        (_, DebateOutcome.Unresolved or DebateOutcome.EscalatedToUser) => Tone.Warning,
        (_, DebateOutcome.Aborted) => Tone.Neutral,
        (DebatePhase.Closed, _) => Tone.Neutral,
        _ => Tone.Active,
    };

    public static string Kind(ContributionKind kind) => kind switch
    {
        ContributionKind.Proposal => "Propuesta",
        ContributionKind.Assessment => "Evaluación",
        ContributionKind.Objection => "Objeción",
        ContributionKind.Rebuttal => "Réplica",
        ContributionKind.Evidence => "Evidencia",
        _ => kind.ToString(),
    };

    /// <summary>The limit that cut the debate; null when none did.</summary>
    public static string? Limit(DebateLimitReached limit) => limit switch
    {
        DebateLimitReached.Rounds => "límite de rondas",
        DebateLimitReached.Duration => "límite de tiempo",
        DebateLimitReached.Tokens => "límite de tokens",
        _ => null,
    };

    public static string Judgment(DebateJudgmentReason reason, DebateLimitReached limit = DebateLimitReached.None) => reason switch
    {
        DebateJudgmentReason.Requested => "lo pediste tú",
        DebateJudgmentReason.Converged => "las posiciones convergieron (todos marcaron su contribución como terminada)",
        DebateJudgmentReason.Limit => Limit(limit) is { } cut ? $"se alcanzó el {cut}" : "se alcanzó un límite",
        DebateJudgmentReason.TurnMissing => "un turno falló dos veces y pediste juicio con lo que hay",
        DebateJudgmentReason.Tribunal => "un tribunal no delibera: el juez sintetiza tras las evaluaciones",
        _ => reason.ToString(),
    };

    public static string Subject(DebateSubjectKind subject, string? reference = null) => subject switch
    {
        DebateSubjectKind.None => "Sin objeto",
        DebateSubjectKind.Commit => reference is { Length: > 0 } commit ? $"Commit {Short(commit)}" : "Commit HEAD del repositorio",
        DebateSubjectKind.Run => reference is { Length: > 0 } run ? $"Ejecución {run}" : "Una ejecución",
        DebateSubjectKind.Verdict => reference is { Length: > 0 } verdict ? $"Veredicto {verdict}" : "Un veredicto (impugnación)",
        _ => subject.ToString(),
    };

    public static string Judging(DebateJudgingKind kind) => kind switch
    {
        DebateJudgingKind.IndependentJudge => "Juez independiente",
        DebateJudgingKind.CrossVendorPanel => "Jurado cruzado",
        DebateJudgingKind.NonProducerJudge => "Juez no productor",
        _ => kind.ToString(),
    };

    /// <summary>What each disposition means (D-13), in the words the form explains it with.</summary>
    public static string JudgingHelp(DebateJudgingKind kind) => kind switch
    {
        DebateJudgingKind.IndependentJudge => "Un juez de un vendor que ni produjo el objeto ni participa: la regla de ADR-0006 tal cual, sin dispensa.",
        DebateJudgingKind.CrossVendorPanel => "Un juez de cada vendor de los participantes: es una dispensa de la regla de los participantes, no independencia. Si los dos coinciden, queda su decisión; si discrepan, decides tú.",
        DebateJudgingKind.NonProducerJudge => "Un juez del vendor que no produjo el trabajo, aunque comparta vendor con un participante: dispensa de la regla de los participantes para ese vendor.",
        _ => string.Empty,
    };

    /// <summary>The waiver a disposition carries, said plainly; null when there is none (D-12: rule P is never waived).</summary>
    public static string? Waiver(IReadOnlyCollection<VendorId> waived) => waived.Count == 0
        ? null
        : $"Dispensa registrada de la regla de los participantes (ADR-0006) para {string.Join(" y ", waived.Select(Vendor).Order(StringComparer.Ordinal))}: el juez comparte vendor con un participante. La regla de los productores no se dispensa nunca.";

    public static string Vendor(VendorId vendor) => vendor.Value switch
    {
        "anthropic" => "Anthropic",
        "openai" => "OpenAI",
        "google" => "Google",
        var other => other,
    };

    public static string Provider(ProviderId provider) => provider.Value switch
    {
        "claude-code" => "Claude Code",
        "codex-cli" => "Codex CLI",
        "gemini-cli" => "Gemini CLI",
        var other => other,
    };

    public static string Await(DebateAwaitReason reason) => reason switch
    {
        DebateAwaitReason.FailedTurn => "Un turno falló dos veces: reintenta el turno, pide juicio con lo que hay o aborta.",
        DebateAwaitReason.ProviderLimit => "Límite de uso del proveedor: espera sola si el CLI dijo cuándo se reinicia; si no, se detiene.",
        DebateAwaitReason.HumanResolution => "El juicio necesita a una persona: «Decidir yo».",
        _ => reason.ToString(),
    };

    /// <summary>The rule that closed a debate (<see cref="JudgingPanel"/> and <c>DebateClosedPayload.AbortedRule</c>).</summary>
    public static string Rule(string? rule) => rule switch
    {
        JudgingPanel.Single => "juez único",
        JudgingPanel.Agree => "el jurado coincide",
        JudgingPanel.Disagree => "el jurado discrepa: decides tú",
        JudgingPanel.Invalid => "veredictos que no se pueden combinar: decides tú",
        JudgingPanel.User => "decisión tuya",
        DebateClosedPayload.AbortedRule => "abortado",
        null or "" => "sin regla",
        var other => other,
    };

    public static string Turn(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Succeeded => "terminó",
        AttemptOutcome.Failed => "falló",
        AttemptOutcome.Cancelled => "cancelado",
        AttemptOutcome.LimitReached => "alcanzó su límite",
        AttemptOutcome.RateLimited => "límite de uso del proveedor",
        AttemptOutcome.Interrupted => "interrumpido",
        _ => outcome.ToString(),
    };

    /// <summary>«1 turno», «3 turnos»: a count with its noun.</summary>
    public static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    public static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
