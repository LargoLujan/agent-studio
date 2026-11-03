using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;

namespace AgentStudio.Memory.Derivation;

/// <summary>
/// The texts of the derived memory, in Spanish (docs/14 D-18 and D-20, docs/15 D-46). A system entry is built only from
/// structured fields of the log: identifiers, enums, counters, hashes, commits, refs and paths the system measured, who
/// decided (a person or the system, never the id the app records a person with), and the messages the system itself wrote
/// (alerts, the vendor note of a verdict). Never from free text an agent may have written into a payload: the reason of a
/// transition (it quotes the agent's summary), the title of a task, the reason of the close of a debate (it may be the
/// summary of the judge), the message of a request to replan that a judge made, or the comment of a person. The only text it
/// quotes is the question of a debate the person wrote, read as the log anchored it (<see cref="WithQuestion"/>).
/// </summary>
/// <remarks>
/// A time is said in UTC, explicitly, in plain Spanish: «el 29/10/2026 a las 22:40 UTC» (V27). The text is anchored by its
/// hash and packed into the prompts of the agents, so it never depends on the zone or the culture of the machine (ADR-0022,
/// docs/14 D-19): the same log gives the same text and the same entry anywhere, and the hour repeated when the clocks go
/// back is two different times. The screens show the local time of an entry beside it. A unit of the log is named as what
/// it is: a run, a debate (<c>deb-…</c>) or an audit (<c>aud-…</c>).
/// <para>
/// Compatibility: an entry is derived once per source (<c>MemoryDerivation</c> skips a source the log already announced with
/// its row), and its content is anchored by its hash when it is announced, so the entries recorded before (with the id of
/// the person and the ISO date) keep their text and their anchor; only the new ones use these words.
/// </para>
/// </remarks>
internal static partial class DerivationTexts
{
    /// <summary>The most paths an entry lists; the rest are counted.</summary>
    public const int MaxListed = 40;

    public const string BusyReason = "Hay una actividad con agentes en curso en el proyecto (una ejecución, una planificación, un debate o una auditoría): la memoria se deriva cuando termine.";

    public const string StoppedReason = "Empezó una actividad con agentes en el proyecto (una ejecución, una planificación, un debate o una auditoría): la derivación se detuvo y la próxima completa lo que falta.";

    public const string UnanchoredReason = "El historial del registro de decisiones no tiene un ancla que lo reconozca en esta ubicación: no se deriva memoria hasta que lo aceptes como nuevo origen.";

    public const string BrokenMidwayReason = "El registro de decisiones dejó de verificar durante la derivación: no se escribió nada más.";

    public const string AlertSummary = "Alerta de integridad de la memoria: una evidencia no es la que ancló el registro";

    public static string NotVerifiedReason(DecisionLogVerification verification) =>
        verification.FirstInvalidSequence is { } sequence
            ? string.Create(CultureInfo.InvariantCulture, $"El registro de decisiones no verifica (entrada {sequence}): no se deriva memoria.")
            : "El registro de decisiones no verifica: no se deriva memoria.";

    // ---- Decision ----

    public static string PlanTopic(string run) => $"Plan de la ejecución {run}";

    public static string PlanSummaryTopic(string run) => $"Resumen del plan aprobado de la ejecución {run}";

    public static string PlanDecision(Actor actor, bool granted, string? planHash, int tasks, string run, DateTimeOffset at)
    {
        var plan = planHash is { Length: >= 12 } ? "el plan " + planHash[..12] : "el plan";
        var size = granted && tasks > 0 ? $" ({Count(tasks, "tarea", "tareas")})" : string.Empty;
        return $"{Who(actor)} {(granted ? "aprobó" : "denegó")} {plan} de la ejecución {run}{size} el {When(at)}.";
    }

    public static string DeliveryTopic(string run) => $"Entrega de la ejecución {run}";

    public static string DeliveryDecision(Actor actor, bool granted, int? changedPaths, string run, DateTimeOffset at) =>
        granted
            ? $"{Who(actor)} aprobó entregar la ejecución {run} a su rama el {When(at)}{(changedPaths is { } paths ? ": " + Changes(paths) : string.Empty)}."
            : $"{Who(actor)} denegó entregar la ejecución {run} a su rama el {When(at)}.";

    // ---- PendingTask ----

    public static string PendingTopic(string run) => $"Tareas pendientes de la ejecución {run}";

    public static string Pending(string run, RunPhase phase, IReadOnlyList<(TaskCreatedPayload Task, TaskStatusChangedPayload? Last)> tasks) =>
        $"Al terminar la ejecución {run} ({Phase(phase)}) quedaron sin completar {Count(tasks.Count, "tarea", "tareas")}: {string.Join("; ", tasks.Select(t => Unfinished(t.Task, t.Last)))}.";

    public static string OpenIssuesTopic(TaskId task, string run) => $"Problemas abiertos que dejó el informe de {task.Value} en la ejecución {run}";

    // ---- Decision (Phase 5): challenges, the acceptance of a revision and the dismissal of a request to replan ----

    public static string ChallengeTopic(string verdict, string unit) => $"Impugnación del veredicto {verdict} {OfUnit(unit)}";

    /// <summary>Who challenged which verdict, when, and the revision it opened; the reasons are named by their evidence, never quoted.</summary>
    public static string Challenge(Actor actor, VerdictChallengedPayload challenge, string unit, DateTimeOffset at) =>
        $"{Person(actor)} impugnó el veredicto {challenge.Verdict} {OfUnit(unit)} el {When(at)}; "
        + $"la revisión es el debate {challenge.RevisionDebate} y sus razones están en la evidencia {challenge.Reasons.Value}.";

    public static string RevisionAcceptanceTopic(string unit) => $"Aceptación de una revisión {InUnit(unit)}";

    public static string RevisionAcceptance(Actor actor, bool granted, string unit, DateTimeOffset at) =>
        $"{Who(actor)} {(granted ? "aceptó" : "no aceptó")} la revisión de un veredicto impugnado {InUnit(unit)} el {When(at)}.";

    public static string ReplanTopic(string unit) => $"Petición de replanificar {Unit(unit)}";

    public static string ReplanDismissal(Actor actor, bool granted, string unit, DateTimeOffset at) =>
        $"{Who(actor)} {(granted ? "descartó" : "no descartó")} la petición de replanificar {Unit(unit)} el {When(at)}.";

    // ---- DebateSummary ----

    public static string DebateTopic(string debate, DebateMode mode) => $"Debate {debate} ({Mode(mode)})";

    /// <summary>
    /// The summary of a closed debate (D-46) from the structured fields of its opening and its close: mode, object, the
    /// verdict a revision challenges, when, outcome and rule, the proposal chosen (a label) and each verdict with its
    /// decision. The question goes last (<see cref="WithQuestion"/>), so a long one never pushes the rest past the limit.
    /// </summary>
    public static string DebateSummary(
        string debate,
        DebateOpenedPayload opened,
        DebateClosedPayload closed,
        IReadOnlyList<(string Id, VerdictIssuedPayload? Verdict)> verdicts,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt)
    {
        var text = new StringBuilder("Debate ").Append(debate).Append(" (").Append(Mode(opened.Mode)).Append(')');
        text.Append(opened.Challenges is { } challenged && Label().IsMatch(challenged)
            ? ": revisión de la impugnación del veredicto " + challenged
            : Subject(opened));

        text.Append(", abierto el ").Append(When(openedAt)).Append(" y cerrado el ").Append(When(closedAt)).Append('.');
        text.Append(" Resultado: ").Append(Outcome(closed.Outcome)).Append(" (").Append(Rule(closed.Rule)).Append(')');
        if (closed.ChosenProposal is { } chosen)
        {
            text.Append("; propuesta elegida: ").Append(Label().IsMatch(chosen) ? chosen : "no citada");
        }

        text.Append('.');
        text.Append(verdicts.Count == 0 ? " Sin veredictos." : $" Veredictos: {string.Join("; ", verdicts.Select(VerdictLine))}.");
        return text.ToString();
    }

    /// <summary>The summary with its question: quoted as the person wrote it, or why it is not.</summary>
    public static string WithQuestion(string summary, QuestionState state, string? question, EvidenceId evidence) => state switch
    {
        QuestionState.Quoted => $"{summary} Pregunta: «{question}»",
        QuestionState.Empty => $"{summary} La pregunta está vacía.",
        QuestionState.NotAnchored => $"{summary} La pregunta (evidencia {evidence.Value}) no está anclada en el registro de decisiones: no se cita.",
        QuestionState.NotByAPerson => $"{summary} La pregunta (evidencia {evidence.Value}) no la escribió una persona: no se cita.",
        _ => $"{summary} La pregunta (evidencia {evidence.Value}) no es la que ancló el registro de decisiones: no se cita.",
    };

    /// <summary>What the derivation says, in Spanish, of the question of a debate it did not quote because it does not match its anchor.</summary>
    public static string QuestionProblem(DebateSummaryFact summary) =>
        $"La evidencia {summary.QuestionEvidence.Value} del debate {summary.Debate} (su pregunta) no es la que ancló el registro de decisiones: no se cita en la memoria.";

    /// <summary>The message of its integrity alert, in English; the same problem always gives the same message.</summary>
    public static string QuestionTamperedAlert(DebateSummaryFact summary, string problem) =>
        $"Evidence '{summary.QuestionEvidence.Value}' of debate '{summary.Debate}' (its question), which memory would quote, is not the content the decision log anchored for it (SHA-256 {summary.QuestionSha256}): {problem} Nothing is quoted from it.";

    // ---- KnownError ----

    public static string FailedTestsTopic(TaskId? task, string run) =>
        task is { } id ? $"Pruebas fallidas en {id.Value} {InUnit(run)}" : $"Pruebas fallidas {InUnit(run)}";

    public static string FailedTests(EvidenceRegisteredPayload test, TestRunSnapshot tests, string run)
    {
        var title = string.IsNullOrWhiteSpace(test.Title) ? string.Empty : $" «{test.Title.Trim()}»";
        var where = test.Task is { } task
            ? "de " + task.Value + (test.Attempt is { } attempt ? string.Create(CultureInfo.InvariantCulture, $" (intento {attempt})") : string.Empty)
            : OfUnit(run);
        var commit = test.SubjectCommit is { } subject ? " sobre el commit " + subject : string.Empty;
        var detail = tests.Failed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(tests.Failed == 1 ? "falló 1 prueba" : $"fallaron {tests.Failed} pruebas")} ({tests.Passed} correctas, {tests.Skipped} omitidas, código de salida {tests.ExitCode})")
            : tests.Passed == 0
                ? string.Create(CultureInfo.InvariantCulture, $"no pasó ninguna prueba ({tests.Skipped} omitidas, código de salida {tests.ExitCode})")
                : string.Create(CultureInfo.InvariantCulture, $"el proceso de las pruebas terminó con el código de salida {tests.ExitCode} ({tests.Passed} correctas, {tests.Skipped} omitidas)");
        return $"La verificación{title} {where} falló{commit}: {detail}.";
    }

    public static string ConflictTopic(TaskId? task, string run) =>
        task is { } id ? $"Conflicto de integración en {id.Value} en la ejecución {run}" : $"Conflicto de integración en la ejecución {run}";

    public static string Conflict(IntegrationPerformedPayload merge, string run) =>
        $"La integración {Named(merge.Task)}de la ejecución {run} tuvo conflictos en {Count(merge.ConflictPaths.Count, "fichero", "ficheros")} y no se integró: {List(merge.ConflictPaths)}.";

    public static string AlertTopic(string code, string run) => $"Alerta {code} {InUnit(run)}";

    public static string Alert(AuditAlertPayload alert, string run)
    {
        var tasks = alert.Tasks.Count > 0 ? $" (tareas: {string.Join(", ", alert.Tasks.Select(t => t.Value))})" : string.Empty;
        var detail = string.IsNullOrWhiteSpace(alert.Message) ? string.Empty : " Detalle del registro: " + alert.Message.Trim();
        return $"Alerta de auditoría «{alert.Code}» {InUnit(run)}{tasks}.{detail}";
    }

    /// <summary>
    /// A request to replan a run (D-34 to D-36): who asked, the findings, the tasks and the verdict behind it. The codes and
    /// the message are quoted only from the auditor (or the system), whose texts are made of the log; a request a judge made
    /// may carry its corrective actions, which are text of an agent, so only how many findings it has is said.
    /// </summary>
    public static string Replan(Actor actor, ReplanRequestedPayload request, string unit, DateTimeOffset at)
    {
        var bySystem = actor.Kind is ActorKind.Auditor or ActorKind.System;
        var who = actor.Kind switch
        {
            ActorKind.Auditor => "El auditor del orquestador",
            ActorKind.Judge => $"El juez {actor.Id} de la revisión del orquestador",
            _ => "Agent Studio",
        };
        var findings = request.Findings.Count == 0
            ? "sin hallazgos"
            : bySystem ? "hallazgos: " + List(request.Findings) : Count(request.Findings.Count, "hallazgo", "hallazgos");
        var tasks = request.Tasks.Count > 0 ? $"; tareas: {string.Join(", ", request.Tasks.Select(t => t.Value))}" : string.Empty;
        var verdict = request.Verdict is { } id ? $"; veredicto {id}" : string.Empty;
        var detail = bySystem && !string.IsNullOrWhiteSpace(request.Message) ? " Detalle del registro: " + request.Message.Trim() : string.Empty;
        return $"{who} pidió replanificar {Unit(unit)} el {When(at)} ({findings}{tasks}{verdict}).{detail}";
    }

    // ---- Change ----

    public static string IntegrationTopic(TaskId? task, string run) =>
        task is { } id ? $"Integración {id.Value} en la ejecución {run}" : $"Integración en la ejecución {run}";

    public static string Integration(IntegrationPerformedPayload merge, string commit, string run) =>
        $"La integración {Named(merge.Task)}de la ejecución {run} dejó {merge.TargetRef} en el commit {commit}{Sources(merge.Sources)}.";

    public static string Delivery(IntegrationPerformedPayload delivery, string commit, IReadOnlyList<string>? changedPaths, string run)
    {
        var text = $"La entrega de la ejecución {run} dejó {delivery.TargetRef} en el commit {commit}{Sources(delivery.Sources)}.";
        return changedPaths switch
        {
            null => text,
            { Count: 0 } => text + " No cambia ningún fichero.",
            _ => $"{text} {Capitalized(Changes(changedPaths.Count))}: {List(changedPaths)}.",
        };
    }

    // ---- Verdict ----

    /// <param name="revises">The challenged verdict this one revises (D-40), or null for a verdict of a review, a debate or an audit.</param>
    public static string VerdictTopic(TaskId? task, string run, string? revises = null) =>
        !IsRun(run)
            ? task is { } judge ? $"Veredicto de {judge.Value} {InUnit(run)}" : $"Veredicto {OfUnit(run)}"
            : revises is { } challenged ? $"Veredicto de la revisión de la impugnación del veredicto {challenged} en la ejecución {run}"
            : task is { } id ? $"Veredicto de la revisión {id.Value} en la ejecución {run}" : $"Veredicto de la ejecución {run}";

    /// <param name="revises">The challenged verdict this one revises (D-40), or null.</param>
    public static string Verdict(VerdictIssuedPayload verdict, string judge, string run, string? revises = null)
    {
        var target = !IsRun(run) ? "para " + Unit(run)
            : revises is { } challenged ? "para la revisión de la impugnación del veredicto " + challenged
            : verdict.Task is { } task ? "para la revisión " + task.Value : "para la ejecución " + run;
        var validity = verdict.Valid ? "válido"
            : verdict.Violations.Count == 0 ? "no válido"
            : $"no válido (incidencias: {string.Join(", ", verdict.Violations.Select(v => v.ToString()))})";
        var correction = verdict.Supersedes is not { } superseded ? string.Empty
            : revises is not null ? $"; revisa el veredicto {superseded} tras su impugnación"
            : "; corrige al veredicto " + superseded;
        var note = string.IsNullOrWhiteSpace(verdict.VendorNote) ? string.Empty : " " + verdict.VendorNote.Trim();
        return $"El juez {judge} emitió el veredicto {verdict.VerdictId} {target}: {Decision(verdict.Decision)}, {validity}{correction}.{note}";
    }

    // ---- an agent's text that is not quoted ----

    /// <summary>What the derivation says, in Spanish, of a text of an agent it did not quote.</summary>
    public static string Problem(AgentQuote quote) =>
        $"La evidencia {quote.Anchor.Evidence.Value} de la ejecución {quote.Run} ({What(quote, spanish: true)}) no es la que ancló el registro de decisiones: no se cita en la memoria.";

    /// <summary>The message of its integrity alert, in English like every message the system leaves in the log; the same problem always gives the same message.</summary>
    public static string TamperedAlert(AgentQuote quote, string problem) =>
        $"Evidence '{quote.Anchor.Evidence.Value}' of run '{quote.Run}' ({What(quote, spanish: false)}), which memory would quote, is not the content the decision log anchored for it (SHA-256 {quote.Anchor.Sha256}): {problem} Nothing is quoted from it.";

    private static string What(AgentQuote quote, bool spanish) => quote.What switch
    {
        QuotedText.PlanSummary => spanish ? "el resumen del plan aprobado" : "the summary of the approved plan",
        QuotedText.OpenIssues => spanish ? $"los problemas abiertos del informe de {quote.Task}" : $"the open issues of the report of task '{quote.Task}'",
        _ => throw new ArgumentOutOfRangeException(nameof(quote), quote.What, "Unknown quoted text."),
    };

    // ---- pieces ----

    /// <summary>
    /// Who decided: «La persona» when a person answered, or the system when no person's answer counted (the gate denied). Never
    /// the id the app records a person with (<c>user</c>, V27): the text is read by the person and packed into the prompts of
    /// the agents, where «Aprobaste…» would read as if the agent had approved.
    /// </summary>
    private static string Who(Actor actor) =>
        actor.Kind == ActorKind.User ? PersonSubject : "Agent Studio, sin una respuesta válida de una persona,";

    /// <summary>The person when a person signed the entry; otherwise the system.</summary>
    private static string Person(Actor actor) => actor.Kind == ActorKind.User ? PersonSubject : "Agent Studio";

    /// <summary>A person, as the subject of a sentence of the memory.</summary>
    private const string PersonSubject = "La persona";

    /// <summary>Whether a unit of the log is an orchestration run (not a debate nor an audit).</summary>
    private static bool IsRun(string unit) =>
        !unit.StartsWith(JournalingAgentProvider.DebatePrefix, StringComparison.Ordinal)
        && !unit.StartsWith(JournalingAgentProvider.AuditPrefix, StringComparison.Ordinal);

    /// <summary>A unit of the log as what it is: «la ejecución …», «el debate …» or «la auditoría …».</summary>
    private static string Unit(string unit) =>
        unit.StartsWith(JournalingAgentProvider.DebatePrefix, StringComparison.Ordinal) ? "el debate " + unit
        : unit.StartsWith(JournalingAgentProvider.AuditPrefix, StringComparison.Ordinal) ? "la auditoría " + unit
        : "la ejecución " + unit;

    /// <summary>«de la ejecución …», «del debate …» or «de la auditoría …».</summary>
    private static string OfUnit(string unit) =>
        unit.StartsWith(JournalingAgentProvider.DebatePrefix, StringComparison.Ordinal) ? "del debate " + unit : "de " + Unit(unit);

    /// <summary>«en la ejecución …», «en el debate …» or «en la auditoría …».</summary>
    private static string InUnit(string unit) => "en " + Unit(unit);

    private static string Mode(DebateMode mode) => mode switch
    {
        DebateMode.Free => "libre",
        DebateMode.Adversarial => "adversarial",
        DebateMode.ArchitectureReview => "revisión de arquitectura",
        DebateMode.ImplementationAudit => "auditoría de implementación",
        DebateMode.OrchestratorReview => "revisión del orquestador",
        DebateMode.Tribunal => "tribunal",
        _ => mode.ToString(),
    };

    /// <summary>What the debate was about, by the identifiers the system wrote (a commit, a run or a verdict); nothing for no object.</summary>
    private static string Subject(DebateOpenedPayload opened) => (opened.Subject, opened.SubjectRef) switch
    {
        (DebateSubjectKind.Commit, { } commit) when Label().IsMatch(commit) => ", sobre el commit " + commit,
        (DebateSubjectKind.Run, { } run) when Label().IsMatch(run) => ", sobre la ejecución " + run,
        (DebateSubjectKind.Verdict, { } verdict) when Label().IsMatch(verdict) => ", sobre el veredicto " + verdict,
        (DebateSubjectKind.None, _) => string.Empty,
        _ => ", con un objeto que el registro no nombra",
    };

    private static string Outcome(DebateOutcome outcome) => outcome switch
    {
        DebateOutcome.Open => "abierto",
        DebateOutcome.Resolved => "resuelto",
        DebateOutcome.Unresolved => "sin resolver",
        DebateOutcome.EscalatedToUser => "lo deja en manos de una persona",
        DebateOutcome.Aborted => "abortado",
        _ => outcome.ToString(),
    };

    /// <summary>The rule a debate closed with (<c>JudgingPanel.Rules</c> and <c>aborted</c>); anything else is not quoted.</summary>
    private static string Rule(string rule) => rule switch
    {
        JudgingPanel.Single => "juez único",
        JudgingPanel.Agree => "el jurado coincide",
        JudgingPanel.Disagree => "el jurado discrepa",
        JudgingPanel.Invalid => "veredictos no válidos o incompletos",
        JudgingPanel.User => "decisión del usuario («Decidir yo»)",
        DebateClosedPayload.AbortedRule => "abortado",
        _ => "regla desconocida",
    };

    private static string VerdictLine((string Id, VerdictIssuedPayload? Verdict) verdict) =>
        verdict.Verdict is { } issued
            ? $"{verdict.Id} ({Decision(issued.Decision)}, {(issued.Valid ? "válido" : "no válido")})"
            : $"{verdict.Id} (no está en el registro)";

    /// <summary>An identifier the system writes (a label, a commit, a run or a verdict id): never free text.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}_.:-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    /// <summary>A moment in UTC, said so: «29/10/2026 a las 22:40 UTC». Never in the zone of the machine (the text is anchored).</summary>
    private static string When(DateTimeOffset at) =>
        at.UtcDateTime.ToString("dd/MM/yyyy 'a las' HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Unfinished(TaskCreatedPayload task, TaskStatusChangedPayload? last)
    {
        var text = new StringBuilder(task.Task.Value).Append(" (").Append(Kind(task.Kind)).Append("): ").Append(Status(last));
        if (last is { Attempt: > 0 } moved)
        {
            text.Append(", tras ").Append(Count(moved.Attempt, "intento", "intentos"));
        }

        if (last is { To: WorkTaskStatus.Blocked } blocked && Block(blocked.BlockReason) is { } why)
        {
            text.Append(", motivo: ").Append(why);
        }

        return text.ToString();
    }

    /// <summary>Where a task stood; one that never moved from where it was created never started.</summary>
    private static string Status(TaskStatusChangedPayload? last) => last is null
        ? "no llegó a empezar"
        : last.To switch
        {
            WorkTaskStatus.Pending => "pendiente",
            WorkTaskStatus.Running => "en curso",
            WorkTaskStatus.Blocked => "bloqueada",
            WorkTaskStatus.AwaitingApproval => "esperando una aprobación",
            WorkTaskStatus.Completed => "completada",
            WorkTaskStatus.Failed => "fallida",
            WorkTaskStatus.Cancelled => "cancelada",
            WorkTaskStatus.Skipped => "omitida",
            _ => last.To.ToString(),
        };

    /// <summary>
    /// The structured reason of a block as the engine writes it (<c>Interrupted</c>, <c>ProviderUnavailable</c>,
    /// <c>AwaitingHuman</c>, <c>RateLimitedUntil(&lt;ISO 8601&gt;)</c>, or <c>RateLimitedUntilEstimated(&lt;ISO 8601&gt;)</c> for a
    /// wait the system estimated because the provider did not say when its limit resets, docs/16 D-37); anything else is not
    /// quoted.
    /// </summary>
    private static string? Block(string? reason)
    {
        foreach (var (limited, note) in new[] { ("RateLimitedUntil(", string.Empty), ("RateLimitedUntilEstimated(", " (espera estimada)") })
        {
            if (reason is not null
                && reason.StartsWith(limited, StringComparison.Ordinal)
                && reason.EndsWith(')')
                && DateTimeOffset.TryParseExact(reason[limited.Length..^1], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
            {
                return "límite de uso del proveedor hasta el " + When(until) + note;
            }
        }

        return reason switch
        {
            "Interrupted" => "la aplicación se cerró mientras trabajaba",
            "ProviderUnavailable" => "no se pudo lanzar el agente o la herramienta",
            "AwaitingHuman" => "espera a una persona",
            _ => null,
        };
    }

    private static string Kind(WorkTaskKind kind) => kind switch
    {
        WorkTaskKind.AgentWork => "trabajo de agente",
        WorkTaskKind.Condition => "condición",
        WorkTaskKind.ToolRun => "herramienta",
        WorkTaskKind.Review => "revisión",
        WorkTaskKind.Debate => "debate",
        WorkTaskKind.HumanApproval => "aprobación de una persona",
        WorkTaskKind.Integration => "integración",
        _ => kind.ToString(),
    };

    private static string Phase(RunPhase phase) => phase switch
    {
        RunPhase.Running => "en curso",
        RunPhase.Paused => "en pausa",
        RunPhase.WaitingForQuota => "esperando cuota",
        RunPhase.AwaitingHuman => "esperando a una persona",
        RunPhase.Interrupted => "interrumpida",
        RunPhase.Succeeded => "completada",
        RunPhase.Failed => "fallida",
        RunPhase.Blocked => "bloqueada",
        RunPhase.Cancelled => "cancelada",
        _ => phase.ToString(),
    };

    private static string Decision(VerdictDecision decision) => decision switch
    {
        VerdictDecision.Approved => "aprobado",
        VerdictDecision.ChangesRequested => "pide cambios",
        VerdictDecision.Rejected => "rechazado",
        VerdictDecision.EscalateToHuman => "lo deja en manos de una persona",
        VerdictDecision.Unresolved => "sin resolver",
        _ => decision.ToString(),
    };

    private static string Named(TaskId? task) => task is { } id ? id.Value + " " : string.Empty;

    private static string Sources(IReadOnlyList<string> sources) =>
        sources.Count == 0 ? string.Empty : $"; integra {Count(sources.Count, "commit", "commits")}: {List(sources)}";

    private static string Changes(int paths) => paths switch
    {
        0 => "no cambia ningún fichero",
        1 => "cambia 1 fichero",
        _ => string.Create(CultureInfo.InvariantCulture, $"cambian {paths} ficheros"),
    };

    private static string List(IReadOnlyList<string> items)
    {
        var listed = string.Join(", ", items.Take(MaxListed));
        return items.Count > MaxListed ? string.Create(CultureInfo.InvariantCulture, $"{listed} y {items.Count - MaxListed} más") : listed;
    }

    private static string Count(int count, string one, string many) =>
        count == 1 ? "1 " + one : string.Create(CultureInfo.InvariantCulture, $"{count} {many}");

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
