using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// The coded text of a reason or a summary the decision log anchored (docs/17 D-47, D-48, ADR-0054 addendum of 2026-11-12). The log
/// keeps the Spanish sentence byte for byte (it is chained by SHA-256), so a run read back from it, or resumed, has no code stored: the
/// code is <b>derived at read time</b> from the exact sentence the engine writes, as the memory does. A sentence that is not one of
/// those (the words of an agent or of its program, the differences of the repository, a sentence of an older version) gives null, and the screen shows it as it was
/// anchored. The sentences are the ones <see cref="OrchestrationRun"/> and <see cref="RunResumer"/> write; a test walks each of them.
/// </summary>
public static partial class LoggedTexts
{
    /// <summary>
    /// The longest a sentence may take to match. The sentences come from the log and carry the words of providers and of agents, so
    /// a long one built to make a pattern backtrack must give up (the derivation then gives null) instead of holding the engine.
    /// </summary>
    private const int MatchTimeoutMs = 250;

    /// <summary>How many «Reintento N de M tras: » one inside another are derived; a longer chain (only a provider error written to look like one makes it) keeps the rest as text, so the derivation cannot run out of stack.</summary>
    private const int MaxRetryNesting = 4;

    [GeneratedRegex(@"^Arranca el intento (\d+): el proveedor lanzó al agente\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex AttemptLaunched();

    [GeneratedRegex(@"^Arranca el intento (\d+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex AttemptStarted();

    [GeneratedRegex(@"^El juez aprobó \(veredicto (.+)\); se concluye al reanudar con el veredicto que ya estaba en el registro\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeApprovedResumed();

    [GeneratedRegex(@"^El veredicto (.+) necesita a una persona; se concluye al reanudar con el veredicto que ya estaba en el registro\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NeedsPersonResumed();

    [GeneratedRegex(@"^Cancelada por el usuario: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex CancelledByUser();

    [GeneratedRegex(@"^Espera tu respuesta: el agente hace la pregunta .+\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex AwaitingAnswer();

    [GeneratedRegex(@"^El agente de .+ quiso hacer una pregunta más de .+: demasiadas preguntas, revisa los requisitos\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TooManyQuestions();

    [GeneratedRegex(@"^Los nodos (\w+) no están disponibles en la Fase 3\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NodeUnavailable();

    [GeneratedRegex(@"^No hay ningún proveedor «(.+)» para (.+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NoProvider();

    [GeneratedRegex(@"^Terminó bien: (\d+) tareas completadas y (\d+) omitidas\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Succeeded();

    [GeneratedRegex(@"^Falló: la política de revisión exige una revisión independiente aprobada de (.+), y no la hubo\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex ReviewRequired();

    [GeneratedRegex(@"^Falló\.(?: Sin reintentos: (.+?)\.)?(?: Inalcanzables: (.+?)\.)?(?: Se agotó el presupuesto de ejecuciones de agente \((\d+)\)\.)?$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Failed();

    [GeneratedRegex(@"^El agente terminó con estado (\w+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex AgentEndedStatus();

    [GeneratedRegex(@"^No se pudo lanzar el agente: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex LaunchFailed();

    [GeneratedRegex(@"^El informe del agente no es válido: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex ReportInvalid();

    [GeneratedRegex(@"^Cambios fuera del alcance de escritura: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex OutsideScope();

    [GeneratedRegex(@"^La verificación falló en: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex VerificationFailed();

    [GeneratedRegex(@"^Abandonada: sin actividad durante más de ([\d.]+) minutos\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Abandoned();

    [GeneratedRegex(@"^Tiempo agotado: más de ([\d.]+) minutos\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TimedOut();

    [GeneratedRegex(@"^La sesión de (.+) caducó o no está iniciada: inicia sesión con «(.+)» y reintenta la tarea\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex SignedOut();

    [GeneratedRegex(@"^Límite de uso del proveedor hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex RateLimitedUntil();

    [GeneratedRegex(@"^El paso (.+), que (.+) requiere, falló: (.+) no llegó a ejecutarse\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex StepSkipped();

    [GeneratedRegex(@"^El paso (.+) terminó bien\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex StepSucceeded();

    [GeneratedRegex(@"^El paso (.+) falló\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex StepFailed();

    [GeneratedRegex(@"^No se integra nada: la ref de (.+) ya no apunta al commit que se verificó\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex SourcesMoved();

    [GeneratedRegex(@"^Conflicto de integración en: (.+)\. Se resuelve a mano o con una ejecución de seguimiento\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex IntegrationConflict();

    [GeneratedRegex(@"^Integrada en (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Integrated();

    [GeneratedRegex(@"^El juez aprobó \(veredicto (.+)\)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeApproved();

    [GeneratedRegex(@"^El juez pidió cambios \(veredicto (.+)\); se propone una ejecución de seguimiento\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeChangesRequested();

    [GeneratedRegex(@"^El juez rechazó el trabajo \(veredicto (.+)\)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeRejected();

    [GeneratedRegex(@"^El veredicto (.+) necesita a una persona\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeNeedsPerson();

    [GeneratedRegex(@"^El juez alcanzó el límite de uso de su proveedor hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JudgeLimitUntil();

    [GeneratedRegex(@"^No se pudo obtener la aprobación \(equivale a denegarla\): (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex ApprovalFailed();

    [GeneratedRegex(@"^No se lanza (.+): el registro de decisiones no verifica, así que la ficha del proyecto no se puede comprobar\. Decide una persona: revisa el registro\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex BriefLogUnverified();

    [GeneratedRegex(@"^No se lanza (.+): el informe de (.+) \(evidencia (.+)\) no es el que ancló el registro de decisiones; alguien lo reescribió en el almacén\. Decide una persona\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex UpstreamTampered();

    [GeneratedRegex(@"^Error del sistema: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex SystemError();

    [GeneratedRegex(@"^Aprobado por (.+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Approved();

    [GeneratedRegex(@"^Denegado por (.+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Denied();

    [GeneratedRegex(@"^Denegado por ([^:]+): (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex DeniedWithComment();

    [GeneratedRegex(@"^Reintento (\d+) de (\d+) tras: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex Retry();

    [GeneratedRegex(@"^La sesión de (.+) no está iniciada: inicia sesión con «(.+)» y reintenta la tarea\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NotSignedIn();

    [GeneratedRegex(@"^La cuota de (.+) sigue agotada según su app-server, hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex LimitStillOnUntil();

    [GeneratedRegex(@"^La cuota de (.+) sigue agotada según su app-server, sin hora de reinicio\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex LimitStillOnNoReset();

    [GeneratedRegex(@"^(El juez alcanzó el límite de uso de su proveedor|El proveedor está sobrecargado o limita la velocidad|Límite de uso del proveedor) sin hora de reinicio: (espera estimada|espera) hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex WaitNoReset();

    [GeneratedRegex(@"^(El juez alcanzó el límite de uso de su proveedor|El proveedor está sobrecargado o limita la velocidad|Límite de uso del proveedor), con una hora de reinicio que ya pasó \((\S+)\): (espera estimada|espera) hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex WaitPassedReset();

    [GeneratedRegex(@"^El proveedor está sobrecargado o limita la velocidad hasta (\S+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex OverloadUntil();

    [GeneratedRegex(@"^En esta sesión de la aplicación ya no se intenta reanudarla sola: el intento anterior falló \((.*)\)\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex ResumeFailedBefore();

    [GeneratedRegex(@"^No se pudo reanudar: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex ResumeFailed();

    [GeneratedRegex(@"^Se detuvo en esta sesión de la aplicación por un error del sistema \((.*)\): la reanudas tú\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex ResumeStoppedByError();

    [GeneratedRegex(@"^El orquestador no tuvo la búsqueda en el historial: (.+) no admite herramientas externas\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex HistoryNoMcp();

    [GeneratedRegex(@"^El orquestador no tuvo la búsqueda en el historial: no se pudo preparar \((.*)\)\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex HistoryFailed();

    [GeneratedRegex(@"^Falló: la política de pruebas exige que el commit final de la ejecución pase pruebas capturadas por el sistema, y no hay ninguna ejecución de pruebas que pase sobre (.+)\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TestsRequired();

    [GeneratedRegex(@"^(.+) \(([^()]+)\)$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TaskAndCommit();

    [GeneratedRegex(@"^No se lanza el intento: el presupuesto( de hoy| de esta semana| de este mes)? (?:del proyecto|del hito (.+?)) es de (\d+) (tokens|ejecuciones de agente) y ya van (\d+)(?:; con este intento serían (\d+)| \(sin estimación de este intento\))(?: \((\d+) intentos sin cifra de tokens no cuentan\))?\. Sube el presupuesto y reintenta la tarea\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex BudgetExceeded();

    [GeneratedRegex(@"^No se lanza el intento: no se pudo comprobar el presupuesto \((.*)\), y sin saberlo no se arriesga la cuota\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex BudgetUnreadable();

    [GeneratedRegex(@"^La tarea (.+) parte del código de varias tareas \((.+)\) sin una integración\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TaskNoIntegration();

    [GeneratedRegex(@"^No se lanza (.+): sus requisitos obligatorios necesitan (\d+) caracteres y el presupuesto de contexto es de (\d+), así que no caben junto con la memoria del proyecto\. Decide una persona: reduce los requisitos de la tarea o quita la memoria de (.+) en la plantilla\.$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex MemoryTooSmallFigures();

    [GeneratedRegex(@"^No se lanza (.+): (.*)\. Decide una persona: reduce los requisitos de la tarea o quita la memoria de (.+) en la plantilla\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex MemoryTooSmallOther();

    [GeneratedRegex(@"^No se lanza (.+): el registro de decisiones no verifica(?: \(entrada (\d+): (.*)\))?, así que la memoria del proyecto no se puede comprobar\. Decide una persona: revisa el registro o quita la memoria de (.+) en la plantilla\.$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex MemoryUnverified();

    [GeneratedRegex(@"^El repositorio del usuario había cambiado antes de lanzar el agente: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex RepositoryBeforeLaunch();

    [GeneratedRegex(@"^El repositorio del usuario cambió (.+?): (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex RepositoryChanged();

    [GeneratedRegex(@"^Una evidencia de la integración cambió en el almacén entre su captura y su lectura: alguien la reescribió, así que la integración no se da por buena ni se reintenta\. (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex EvidenceTampered();

    [GeneratedRegex(@"^La evidencia de la integración no es la que capturó el integrador y no se da por buena: (.*)$", RegexOptions.Singleline, MatchTimeoutMs)]
    private static partial Regex EvidenceUnanchored();
    /// <summary>
    /// The coded text of the notice that tells why the orchestrator planned without the search in the history
    /// (<c>PlanProposal.HistoryToolNotice</c>, the sentences of <c>ProjectHistoryTool</c>), derived from the sentence so that a draft
    /// saved with it gets the same one; null when it is not one of them.
    /// </summary>
    public static UserText? HistoryToolNotice(string? notice) => Derive(() => DeriveHistoryToolNotice(notice));

    private static UserText? DeriveHistoryToolNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return null;
        }

        if (notice == AgentStudio.Application.Semantic.ProjectHistoryTool.NotThePlannerReason)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryNotThePlanner);
        }

        if (notice == AgentStudio.Application.Semantic.ProjectHistoryTool.DisabledReason)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryDisabled);
        }

        if (notice == AgentStudio.Application.Semantic.ProjectHistoryTool.NoMemoryReason)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryNoMemory);
        }

        if (notice == AgentStudio.Application.Semantic.ProjectHistoryTool.NotReadOnlyReason)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryNotReadOnly);
        }

        if (notice == AgentStudio.Application.Semantic.ProjectHistoryTool.EmptyReason)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryEmpty);
        }

        if (HistoryNoMcp().Match(notice) is { Success: true } noMcp)
        {
            return UserText.Of(OrchestrationUserTexts.HistoryNoMcp, noMcp.Groups[1].Value);
        }

        return HistoryFailed().Match(notice) is { Success: true } failed
            ? UserText.Of(OrchestrationUserTexts.HistoryFailed, failed.Groups[1].Value)
            : null;
    }

    /// <summary>
    /// Runs a derivation so that none can throw: the sentences come from a log and from providers (an error of a provider reaches a
    /// reason whole), so a number that does not fit, a pattern that gives up or anything else unexpected gives null, and the screen
    /// paints the sentence as it was anchored. Called while the engine runs and when a log is read back, where an exception would
    /// stop a run or leave it closed.
    /// </summary>
    private static UserText? Derive(Func<UserText?> derivation)
    {
        try
        {
            return derivation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
    /// <summary>The minutes of a threshold as the sentences of the engine write them («0.#», invariant), as the number a coded text carries.</summary>
    public static double Minutes(TimeSpan span) =>
        double.Parse(span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>The reason the engine takes when a cancellation gives none (anchored as it is).</summary>
    public const string NoReasonGiven = "sin motivo";

    /// <summary>The reason the monitor gives when the person cancels from it (anchored as it is).</summary>
    public const string CancelledFromMonitorReason = "Cancelada por el usuario desde el monitor.";

    /// <summary>
    /// The reason of a cancellation as an argument: a piece for the two the system writes, and the person's own words as they are.
    /// The live engine and the derivation from the log both go through here, so they agree.
    /// </summary>
    public static object CancelReason(string reason) => reason switch
    {
        NoReasonGiven => UserText.Of(OrchestrationUserTexts.NoReason),
        CancelledFromMonitorReason => UserText.Of(OrchestrationUserTexts.CancelledFromMonitor),
        _ => reason,
    };

    /// <summary>The coded text of a reason of a transition of a task, or null when it is not one the engine writes with a template.</summary>
    public static UserText? TaskReason(string? reason) => Derive(() => DeriveTaskReason(reason, 0));

    private static UserText? DeriveTaskReason(string? reason, int depth)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        if (Fixed.TryGetValue(reason, out var code))
        {
            return UserText.Of(code);
        }

        if (AttemptLaunched().Match(reason) is { Success: true } launched && Int(launched, 1) is { } launchedAttempt)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptLaunched, launchedAttempt);
        }

        if (AttemptStarted().Match(reason) is { Success: true } started && Int(started, 1) is { } startedAttempt)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptStarted, startedAttempt);
        }

        if (JudgeApprovedResumed().Match(reason) is { Success: true } approved)
        {
            return UserText.Of(OrchestrationUserTexts.TaskJudgeApprovedResumed, approved.Groups[1].Value);
        }

        if (NeedsPersonResumed().Match(reason) is { Success: true } needs)
        {
            return UserText.Of(OrchestrationUserTexts.TaskVerdictNeedsPersonResumed, needs.Groups[1].Value);
        }

        if (CancelledByUser().Match(reason) is { Success: true } cancelled)
        {
            return UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, CancelReason(cancelled.Groups[1].Value));
        }

        if (NodeUnavailable().Match(reason) is { Success: true } node)
        {
            return UserText.Of(OrchestrationUserTexts.TaskNodeUnavailable, node.Groups[1].Value);
        }

        if (AwaitingAnswer().IsMatch(reason))
        {
            return UserText.Of(UserTextCodes.QuestionAwaitingAnswer);
        }

        if (TooManyQuestions().IsMatch(reason))
        {
            return UserText.Of(UserTextCodes.QuestionTooMany);
        }

        if (NoProvider().Match(reason) is { Success: true } provider)
        {
            return UserText.Of(OrchestrationUserTexts.TaskNoProvider, provider.Groups[1].Value, provider.Groups[2].Value);
        }

        if (TaskNoIntegration().Match(reason) is { Success: true } noIntegration)
        {
            return UserText.Of(OrchestrationUserTexts.TaskNoIntegration, noIntegration.Groups[1].Value, noIntegration.Groups[2].Value);
        }

        return BudgetReason(reason) ?? WorkerReason(reason, depth);
    }

    /// <summary>The reasons the workers write (<c>Workers</c>), with the same arguments the engine gives them live; null for the rest (an agent's own words, the differences of the repository).</summary>
    private static UserText? WorkerReason(string reason, int depth)
    {
        if (Retry().Match(reason) is { Success: true } retry && Int(retry, 1) is { } retryNumber && Int(retry, 2) is { } retryOf)
        {
            var tail = retry.Groups[3].Value;
            return UserText.Of(
                OrchestrationUserTexts.TaskRetry,
                retryNumber,
                retryOf,
                (object?)(depth < MaxRetryNesting ? DeriveTaskReason(tail, depth + 1) : null) ?? tail);
        }

        if (AgentEndedStatus().Match(reason) is { Success: true } status)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerAgentEndedStatus, status.Groups[1].Value);
        }

        if (LaunchFailed().Match(reason) is { Success: true } launch)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerLaunchFailed, launch.Groups[1].Value);
        }

        if (ReportInvalid().Match(reason) is { Success: true } report)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerReportInvalid, report.Groups[1].Value);
        }

        if (OutsideScope().Match(reason) is { Success: true } scope)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerOutsideScope, scope.Groups[1].Value);
        }

        if (VerificationFailed().Match(reason) is { Success: true } verification)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerVerificationFailed, verification.Groups[1].Value);
        }

        if (Abandoned().Match(reason) is { Success: true } abandoned && Number(abandoned, 1) is { } silent)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerAbandoned, silent);
        }

        if (TimedOut().Match(reason) is { Success: true } timedOut && Number(timedOut, 1) is { } minutes)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerTimedOut, minutes);
        }

        if (SignedOut().Match(reason) is { Success: true } signedOut)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerSignedOut, signedOut.Groups[1].Value, signedOut.Groups[2].Value);
        }

        if (RateLimitedUntil().Match(reason) is { Success: true } limit && Instant(limit, 1) is { } resets)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, resets);
        }

        if (StepSkipped().Match(reason) is { Success: true } skipped)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerStepSkipped, skipped.Groups[1].Value, skipped.Groups[2].Value);
        }

        if (StepSucceeded().Match(reason) is { Success: true } stepDone)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerStepSucceeded, stepDone.Groups[1].Value);
        }

        if (StepFailed().Match(reason) is { Success: true } stepFailed)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerStepFailed, stepFailed.Groups[1].Value);
        }

        if (SourcesMoved().Match(reason) is { Success: true } moved)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerSourcesMoved, moved.Groups[1].Value);
        }

        if (IntegrationConflict().Match(reason) is { Success: true } conflict)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerIntegrationConflict, conflict.Groups[1].Value);
        }

        if (Integrated().Match(reason) is { Success: true } integrated)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerIntegrated, integrated.Groups[1].Value);
        }

        if (JudgeApproved().Match(reason) is { Success: true } approved)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerJudgeApproved, approved.Groups[1].Value);
        }

        if (JudgeChangesRequested().Match(reason) is { Success: true } changes)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerJudgeChangesRequested, changes.Groups[1].Value);
        }

        if (JudgeRejected().Match(reason) is { Success: true } rejected)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerJudgeRejected, rejected.Groups[1].Value);
        }

        if (JudgeNeedsPerson().Match(reason) is { Success: true } needs)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerJudgeNeedsPerson, needs.Groups[1].Value);
        }

        if (JudgeLimitUntil().Match(reason) is { Success: true } judgeLimit && Instant(judgeLimit, 1) is { } judgeResets)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, judgeResets);
        }

        if (ApprovalFailed().Match(reason) is { Success: true } failedApproval)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerApprovalFailed, failedApproval.Groups[1].Value);
        }

        if (BriefLogUnverified().Match(reason) is { Success: true } brief)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerBriefLogUnverified, brief.Groups[1].Value);
        }

        if (UpstreamTampered().Match(reason) is { Success: true } upstream)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerUpstreamTampered, upstream.Groups[1].Value, upstream.Groups[2].Value, upstream.Groups[3].Value);
        }

        if (NotSignedIn().Match(reason) is { Success: true } notSignedIn)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerNotSignedIn, notSignedIn.Groups[1].Value, notSignedIn.Groups[2].Value);
        }

        if (LimitStillOnUntil().Match(reason) is { Success: true } still && Instant(still, 2) is { } stillUntil)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnUntil, still.Groups[1].Value, stillUntil);
        }

        if (LimitStillOnNoReset().Match(reason) is { Success: true } stillNoReset)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnNoReset, stillNoReset.Groups[1].Value);
        }

        if (WaitNoReset().Match(reason) is { Success: true } noReset && Instant(noReset, 3) is { } noResetUntil)
        {
            return UserText.Of(OrchestrationUserTexts.QuotaNoReset, Who(noReset.Groups[1].Value), Wait(noReset.Groups[2].Value), noResetUntil);
        }

        if (WaitPassedReset().Match(reason) is { Success: true } passed && Instant(passed, 2) is { } passedReset && Instant(passed, 4) is { } passedUntil)
        {
            return UserText.Of(OrchestrationUserTexts.QuotaPassedReset, Who(passed.Groups[1].Value), passedReset, Wait(passed.Groups[3].Value), passedUntil);
        }

        if (OverloadUntil().Match(reason) is { Success: true } overload && Instant(overload, 1) is { } overloadUntil)
        {
            return UserText.Of(OrchestrationUserTexts.QuotaUntil, Who(OverloadSentence), overloadUntil);
        }

        if (MemoryTooSmallFigures().Match(reason) is { Success: true } figures && Int(figures, 2) is { } required && Int(figures, 3) is { } budget)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerMemoryTooSmall, figures.Groups[1].Value, UserText.Of(OrchestrationUserTexts.MemoryDetailFigures, required, budget), figures.Groups[4].Value);
        }

        if (MemoryTooSmallOther().Match(reason) is { Success: true } tooSmall)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerMemoryTooSmall, tooSmall.Groups[1].Value, UserText.Of(OrchestrationUserTexts.MemoryDetailUnknown), tooSmall.Groups[3].Value);
        }

        if (MemoryUnverified().Match(reason) is { Success: true } unverified)
        {
            if (!unverified.Groups[2].Success)
            {
                return UserText.Of(OrchestrationUserTexts.WorkerMemoryUnverified, unverified.Groups[1].Value, unverified.Groups[4].Value);
            }

            return Long(unverified, 2) is { } entry
                ? UserText.Of(OrchestrationUserTexts.WorkerMemoryUnverifiedEntry, unverified.Groups[1].Value, entry, unverified.Groups[3].Value, unverified.Groups[4].Value)
                : null;
        }

        if (RepositoryBeforeLaunch().Match(reason) is { Success: true } beforeLaunch)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerRepositoryBeforeLaunch, beforeLaunch.Groups[1].Value);
        }

        if (RepositoryChanged().Match(reason) is { Success: true } repository)
        {
            return RepositoryWhen(repository.Groups[1].Value) is { } moment
                ? UserText.Of(OrchestrationUserTexts.WorkerRepositoryChanged, moment, repository.Groups[2].Value)
                : null;
        }

        if (EvidenceTampered().Match(reason) is { Success: true } tampered)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerEvidenceTampered, tampered.Groups[1].Value);
        }

        if (EvidenceUnanchored().Match(reason) is { Success: true } unanchored)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerEvidenceUnanchored, unanchored.Groups[1].Value);
        }
        if (SystemError().Match(reason) is { Success: true } system)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerSystemError, system.Groups[1].Value);
        }

        if (Approved().Match(reason) is { Success: true } by)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerApproved, by.Groups[1].Value);
        }

        // With a comment first: the comment of a person may end in a full stop, and the sentence without one ends in it too.
        if (DeniedWithComment().Match(reason) is { Success: true } commented)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerDeniedWithComment, commented.Groups[1].Value, commented.Groups[2].Value);
        }

        return Denied().Match(reason) is { Success: true } deniedBy
            ? UserText.Of(OrchestrationUserTexts.WorkerDenied, deniedBy.Groups[1].Value)
            : null;
    }

    /// <summary>When the user's repository changed, as the piece the live engine gives (<c>Workers</c>); null when it is not one of the moments it names.</summary>
    private static UserText? RepositoryWhen(string when)
    {
        var fixedCode = when switch
        {
            "mientras trabajaba el agente" => OrchestrationUserTexts.RepositoryWhileAgent,
            "durante la verificación" => OrchestrationUserTexts.RepositoryDuringVerification,
            "durante la integración" => OrchestrationUserTexts.RepositoryDuringIntegration,
            "antes de la integración" => OrchestrationUserTexts.RepositoryBeforeIntegration,
            "durante la revisión" => OrchestrationUserTexts.RepositoryDuringReview,
            "antes de la revisión" => OrchestrationUserTexts.RepositoryBeforeReview,
            _ => null,
        };
        if (fixedCode is not null)
        {
            return UserText.Of(fixedCode);
        }

        const string stoppedTail = ", que se detuvieron antes de terminar";
        if (when.StartsWith("durante ", StringComparison.Ordinal) && when.EndsWith(stoppedTail, StringComparison.Ordinal))
        {
            return StepsPiece(when["durante ".Length..^stoppedTail.Length]) is { } stopped ? UserText.Of(OrchestrationUserTexts.RepositoryDuringStepsStopped, stopped) : null;
        }

        if (when.StartsWith("durante ", StringComparison.Ordinal))
        {
            return StepsPiece(when["durante ".Length..]) is { } during ? UserText.Of(OrchestrationUserTexts.RepositoryDuringSteps, during) : null;
        }

        return when.StartsWith("antes de ", StringComparison.Ordinal) && StepsPiece(when["antes de ".Length..]) is { } before
            ? UserText.Of(OrchestrationUserTexts.RepositoryBeforeSteps, before)
            : null;
    }

    /// <summary>«el paso build» or «los pasos build, lint y test» as the piece <see cref="OrchestrationUserTexts.Steps"/> gives; null for anything else.</summary>
    private static UserText? StepsPiece(string steps)
    {
        const string one = "el paso ";
        const string many = "los pasos ";
        if (steps.StartsWith(one, StringComparison.Ordinal) && steps.Length > one.Length)
        {
            return UserText.Of(OrchestrationUserTexts.StepsOne, steps[one.Length..]);
        }

        if (steps.StartsWith(many, StringComparison.Ordinal))
        {
            var list = steps[many.Length..];
            var and = list.LastIndexOf(" y ", StringComparison.Ordinal);
            return and > 0 ? UserText.Of(OrchestrationUserTexts.StepsMany, UserText.Of(OrchestrationUserTexts.ListAnd, list[..and], list[(and + 3)..])) : null;
        }

        return null;
    }

    /// <summary>
    /// The reason the budget gate gives for not launching an attempt (<c>ProjectBudgetGate</c>) as the coded text it gives next to it:
    /// the name of the budget and the figures, or why the budget could not be read. The free text the report gives for a log it does not
    /// trust, and the message of an exception, are not in the coded text (the sentence anchored them, the screen does not need them).
    /// </summary>
    private static UserText? BudgetReason(string reason)
    {
        if (BudgetUnreadable().Match(reason) is { Success: true } unreadable)
        {
            var why = unreadable.Groups[1].Value;
            return UserText.Of(
                OrchestrationUserTexts.BudgetUnreadable,
                UserText.Of(why switch
                {
                    "no hay con qué leer lo gastado" => OrchestrationUserTexts.BudgetWhyNoUsage,
                    _ when why.StartsWith("el registro del proyecto no es de confianza: ", StringComparison.Ordinal) => OrchestrationUserTexts.BudgetWhyUntrusted,
                    _ => OrchestrationUserTexts.BudgetWhyFailed,
                }));
        }

        if (BudgetExceeded().Match(reason) is not { Success: true } exceeded)
        {
            return null;
        }

        var period = exceeded.Groups[1].Value switch
        {
            " de hoy" => BudgetPeriod.Day,
            " de esta semana" => BudgetPeriod.Week,
            " de este mes" => BudgetPeriod.Month,
            _ => BudgetPeriod.Total,
        };
        var name = ProjectBudgetGate.NameOf(exceeded.Groups[2].Success ? exceeded.Groups[2].Value : "project", period);
        var sentences = new List<UserText>();
        if (exceeded.Groups[4].Value == "tokens")
        {
            if (Long(exceeded, 3) is not { } max || Long(exceeded, 5) is not { } used)
            {
                return null;
            }

            if (!exceeded.Groups[6].Success)
            {
                sentences.Add(UserText.Of(OrchestrationUserTexts.BudgetExceededTokensUnknown, name, max, used));
            }
            else if (Long(exceeded, 6) is { } projected)
            {
                sentences.Add(UserText.Of(OrchestrationUserTexts.BudgetExceededTokens, name, max, used, projected));
            }
            else
            {
                return null;
            }

            if (exceeded.Groups[7].Success)
            {
                if (Int(exceeded, 7) is not { } withoutTokens)
                {
                    return null;
                }

                sentences.Add(UserText.Of(OrchestrationUserTexts.BudgetPartial, withoutTokens));
            }
        }
        else
        {
            // The attempts without a figure of tokens only count for a budget of tokens, and a budget of runs always says what it would reach.
            if (exceeded.Groups[7].Success || !exceeded.Groups[6].Success || Int(exceeded, 3) is not { } maxRuns || Int(exceeded, 5) is not { } usedRuns || Int(exceeded, 6) is not { } projectedRuns)
            {
                return null;
            }

            sentences.Add(UserText.Of(OrchestrationUserTexts.BudgetExceededRuns, name, maxRuns, usedRuns, projectedRuns));
        }

        sentences.Add(UserText.Of(OrchestrationUserTexts.BudgetRaise));
        return OrchestrationUserTexts.Sentences(sentences);
    }

    /// <summary>The tasks of a final summary of the test policy, each with its commit or without one, as the pieces the engine gives.</summary>
    private static UserText? TestsRequiredSummary(Match required)
    {
        var items = new List<UserText>();
        foreach (var item in required.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (TaskAndCommit().Match(item) is not { Success: true } part)
            {
                return null;
            }

            items.Add(part.Groups[2].Value == "sin commit"
                ? UserText.Of(OrchestrationUserTexts.VerdictUntestedNoCommit, part.Groups[1].Value)
                : UserText.Of(OrchestrationUserTexts.VerdictUntestedCommit, part.Groups[1].Value, part.Groups[2].Value));
        }

        return items.Count == 0 ? null : UserText.Of(OrchestrationUserTexts.VerdictTestsRequired, OrchestrationUserTexts.List(items));
    }
    private const string OverloadSentence = "El proveedor está sobrecargado o limita la velocidad";

    private static UserText Who(string sentence) => UserText.Of(sentence switch
    {
        "El juez alcanzó el límite de uso de su proveedor" => OrchestrationUserTexts.QuotaWhoJudge,
        OverloadSentence => OrchestrationUserTexts.QuotaWhoOverload,
        _ => OrchestrationUserTexts.QuotaWhoPlain,
    });

    private static UserText Wait(string sentence) =>
        UserText.Of(sentence == "espera estimada" ? OrchestrationUserTexts.QuotaWaitEstimated : OrchestrationUserTexts.QuotaWait);

    /// <summary>
    /// The coded text of the reason a pass of the scheduler or a resume by consent gives for not resuming a run or for how it did
    /// (<c>ScheduledResume.Reason</c>), or null when it is not one of the sentences of <c>ResumeScheduler</c>.
    /// </summary>
    public static UserText? ResumeReason(string? reason) => Derive(() => DeriveResumeReason(reason));

    private static UserText? DeriveResumeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        if (ResumeFixed.TryGetValue(reason, out var code))
        {
            return UserText.Of(code);
        }

        if (ResumeFailedBefore().Match(reason) is { Success: true } before)
        {
            return UserText.Of(OrchestrationUserTexts.ResumeFailedBefore, before.Groups[1].Value);
        }

        if (ResumeFailed().Match(reason) is { Success: true } failed)
        {
            return UserText.Of(OrchestrationUserTexts.ResumeFailed, failed.Groups[1].Value);
        }

        return ResumeStoppedByError().Match(reason) is { Success: true } stopped
            ? UserText.Of(OrchestrationUserTexts.ResumeStoppedByError, stopped.Groups[1].Value)
            : null;
    }

    private static readonly Dictionary<string, string> ResumeFixed = new(StringComparer.Ordinal)
    {
        ["Su cabecera no tiene el consentimiento para reanudarse sola: la reanudas tú."] = OrchestrationUserTexts.ResumeNoConsent,
        ["No está a medias."] = OrchestrationUserTexts.ResumeNotHalfway,
        ["La pausaste: nunca se reanuda sola."] = OrchestrationUserTexts.ResumePaused,
        ["La dejaste al salir: nunca se reanuda sola, aunque Windows cerrara la sesión después."] = OrchestrationUserTexts.ResumeLeftOnExit,
        ["Windows cerró la sesión con la ejecución en marcha."] = OrchestrationUserTexts.ResumeSessionEnded,
        ["Esperaba cuota cuando se cerró la aplicación."] = OrchestrationUserTexts.ResumeWaitingLimit,
        ["La cortó un cierre que no pediste."] = OrchestrationUserTexts.ResumeUnrequestedClose,
        ["La reanudación sola está suspendida mientras la aplicación sale."] = OrchestrationUserTexts.ResumeSuspended,
        ["No está registrada."] = OrchestrationUserTexts.ResumeNotRegistered,
    };

    private static double? Number(Match match, int group) =>
        double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset? Instant(Match match, int group) =>
        DateTimeOffset.TryParseExact(match.Groups[group].Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    /// <summary>The coded text of the summary a run finished with (<c>RunFinished</c>), or null when it is not one the engine writes with a template.</summary>
    public static UserText? RunSummary(string? summary) => Derive(() => DeriveRunSummary(summary));

    private static UserText? DeriveRunSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        if (FixedSummaries.TryGetValue(summary, out var code))
        {
            return UserText.Of(code);
        }

        if (Succeeded().Match(summary) is { Success: true } done && Int(done, 1) is { } completed && Int(done, 2) is { } skipped)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictSucceeded, completed, skipped);
        }

        if (CancelledByUser().Match(summary) is { Success: true } cancelled)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, CancelReason(cancelled.Groups[1].Value));
        }

        if (ReviewRequired().Match(summary) is { Success: true } review)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictReviewRequired, OrchestrationUserTexts.List(Split(review.Groups[1].Value)));
        }

        if (TestsRequired().Match(summary) is { Success: true } testsRequired)
        {
            return TestsRequiredSummary(testsRequired);
        }

        if (Failed().Match(summary) is { Success: true } failed && (failed.Groups[1].Success || failed.Groups[2].Success || failed.Groups[3].Success) && (!failed.Groups[3].Success || Int(failed, 3) is not null))
        {
            var parts = new List<UserText>();
            if (failed.Groups[1].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictNoRetries, OrchestrationUserTexts.List(Split(failed.Groups[1].Value))));
            }

            if (failed.Groups[2].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictUnreachable, OrchestrationUserTexts.List(Split(failed.Groups[2].Value))));
            }

            if (failed.Groups[3].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictBudgetSpent, Int(failed, 3)!.Value));
            }

            return UserText.Of(OrchestrationUserTexts.VerdictFailed, OrchestrationUserTexts.Sentences(parts));
        }

        return null;
    }

    /// <summary>The reasons the engine writes without arguments, as it anchors them.</summary>
    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["El usuario pidió reintentarla."] = OrchestrationUserTexts.TaskRetryByUser,
        ["Terminó la espera de cuota del proveedor."] = OrchestrationUserTexts.TaskQuotaWaitEnded,
        ["Esperando la aprobación del usuario."] = OrchestrationUserTexts.TaskAwaitingApproval,
        ["Evaluando la condición."] = OrchestrationUserTexts.TaskEvaluatingCondition,
        [RunInspector.InterruptedReason] = OrchestrationUserTexts.TaskInterrupted,
        ["Su pregunta ya tenía respuesta cuando se cerró la aplicación: sigue con un intento nuevo."] = OrchestrationUserTexts.TaskQuestionAnsweredBeforeClose,
        [RunResumer.ReopenedByUser] = OrchestrationUserTexts.TaskResumedByUser,
        [RunResumer.ReopenedByConsent] = OrchestrationUserTexts.TaskResumedByConsent,
        ["Se canceló antes de arrancar."] = OrchestrationUserTexts.WorkerCancelledBeforeStart,
        ["El proveedor canceló la ejecución."] = OrchestrationUserTexts.WorkerProviderCancelled,
        ["El agente hace una pregunta a una persona."] = OrchestrationUserTexts.WorkerAgentAsks,
        ["Cancelada por el usuario durante la verificación."] = OrchestrationUserTexts.WorkerVerificationCancelled,
        ["La verificación no terminó a tiempo."] = OrchestrationUserTexts.WorkerVerificationTimeout,
        ["Cancelada por el usuario."] = OrchestrationUserTexts.WorkerCancelledByUser,
        ["Límite de uso del proveedor, sin hora de reinicio."] = OrchestrationUserTexts.WorkerRateLimitedNoReset,
        ["Abandonada: la herramienta no dio señales de vida."] = OrchestrationUserTexts.WorkerToolSilent,
        ["Tiempo agotado."] = OrchestrationUserTexts.WorkerToolTimeout,
        ["La integración no terminó a tiempo."] = OrchestrationUserTexts.WorkerIntegrationTimeout,
        ["La verificación de la integración falló."] = OrchestrationUserTexts.WorkerIntegrationVerificationFailed,
        ["La revisión no terminó a tiempo."] = OrchestrationUserTexts.WorkerReviewTimeout,
        ["El juez alcanzó el límite de uso de su proveedor, sin hora de reinicio."] = OrchestrationUserTexts.WorkerJudgeLimitNoReset,
    };

    /// <summary>The summaries the engine writes without arguments, as it anchors them.</summary>
    private static readonly Dictionary<string, string> FixedSummaries = new(StringComparer.Ordinal)
    {
        ["Cancelada: la cancelación se había aplicado a todas las tareas antes de la interrupción."] = OrchestrationUserTexts.VerdictCancelledAfterStop,
        ["Se detuvo: el repositorio del usuario cambió durante la ejecución."] = OrchestrationUserTexts.VerdictStoppedRepository,
        ["Se detuvo: la ref de una tarea ya no apunta al commit que se verificó, y no se integró nada de ella."] = OrchestrationUserTexts.VerdictStoppedSourceMoved,
        ["Se detuvo: una evidencia de la integración cambió en el almacén entre su captura y su lectura; esa integración no se da por buena, ni se revisa ni se entrega."] = OrchestrationUserTexts.VerdictStoppedEvidence,
        [RunInspector.InterruptedSummary] = OrchestrationUserTexts.VerdictInterruptedInLog,
    };

    /// <summary>A pause or a resume of the dispatch, as the inspector shows it: the own of the person, or of whoever else decided.</summary>
    public static UserText Dispatch(bool paused, string decidedBy, bool byUser) => byUser
        ? UserText.Of(paused ? OrchestrationUserTexts.RunPausedByYou : OrchestrationUserTexts.RunResumedByYou)
        : UserText.Of(paused ? OrchestrationUserTexts.RunPausedBy : OrchestrationUserTexts.RunResumedBy, decidedBy);

    private static List<string> Split(string list) => [.. list.Split(", ", StringSplitOptions.RemoveEmptyEntries)];

    private static long? Long(Match match, int group) =>
        long.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? Int(Match match, int group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
