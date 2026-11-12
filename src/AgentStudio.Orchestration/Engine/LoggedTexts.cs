using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// The coded text of a reason or a summary the decision log anchored (docs/17 D-47, D-48, ADR-0054 addendum of 2026-11-12). The log
/// keeps the Spanish sentence byte for byte (it is chained by SHA-256), so a run read back from it, or resumed, has no code stored: the
/// code is <b>derived at read time</b> from the exact sentence the engine writes, as the memory does. A sentence that is not one of
/// those (a reason of a worker, an agent's own words, a sentence of an older version) gives null, and the screen shows it as it was
/// anchored. The sentences are the ones <see cref="OrchestrationRun"/> and <see cref="RunResumer"/> write; a test walks each of them.
/// </summary>
public static partial class LoggedTexts
{
    [GeneratedRegex(@"^Arranca el intento (\d+): el proveedor lanzó al agente\.$")]
    private static partial Regex AttemptLaunched();

    [GeneratedRegex(@"^Arranca el intento (\d+)\.$")]
    private static partial Regex AttemptStarted();

    [GeneratedRegex(@"^El juez aprobó \(veredicto (.+)\); se concluye al reanudar con el veredicto que ya estaba en el registro\.$")]
    private static partial Regex JudgeApprovedResumed();

    [GeneratedRegex(@"^El veredicto (.+) necesita a una persona; se concluye al reanudar con el veredicto que ya estaba en el registro\.$")]
    private static partial Regex NeedsPersonResumed();

    [GeneratedRegex(@"^Cancelada por el usuario: (.*)$", RegexOptions.Singleline)]
    private static partial Regex CancelledByUser();

    [GeneratedRegex(@"^Espera tu respuesta: el agente hace la pregunta .+\.$")]
    private static partial Regex AwaitingAnswer();

    [GeneratedRegex(@"^El agente de .+ quiso hacer una pregunta más de .+: demasiadas preguntas, revisa los requisitos\.$")]
    private static partial Regex TooManyQuestions();

    [GeneratedRegex(@"^Los nodos (\w+) no están disponibles en la Fase 3\.$")]
    private static partial Regex NodeUnavailable();

    [GeneratedRegex(@"^No hay ningún proveedor «(.+)» para (.+)\.$")]
    private static partial Regex NoProvider();

    [GeneratedRegex(@"^Terminó bien: (\d+) tareas completadas y (\d+) omitidas\.$")]
    private static partial Regex Succeeded();

    [GeneratedRegex(@"^Falló: la política de revisión exige una revisión independiente aprobada de (.+), y no la hubo\.$")]
    private static partial Regex ReviewRequired();

    [GeneratedRegex(@"^Falló\.(?: Sin reintentos: (.+?)\.)?(?: Inalcanzables: (.+?)\.)?(?: Se agotó el presupuesto de ejecuciones de agente \((\d+)\)\.)?$")]
    private static partial Regex Failed();

    [GeneratedRegex(@"^El agente terminó con estado (\w+)\.$")]
    private static partial Regex AgentEndedStatus();

    [GeneratedRegex(@"^No se pudo lanzar el agente: (.*)$", RegexOptions.Singleline)]
    private static partial Regex LaunchFailed();

    [GeneratedRegex(@"^El informe del agente no es válido: (.*)$", RegexOptions.Singleline)]
    private static partial Regex ReportInvalid();

    [GeneratedRegex(@"^Cambios fuera del alcance de escritura: (.*)$", RegexOptions.Singleline)]
    private static partial Regex OutsideScope();

    [GeneratedRegex(@"^La verificación falló en: (.*)$", RegexOptions.Singleline)]
    private static partial Regex VerificationFailed();

    [GeneratedRegex(@"^Abandonada: sin actividad durante más de ([\d.]+) minutos\.$")]
    private static partial Regex Abandoned();

    [GeneratedRegex(@"^Tiempo agotado: más de ([\d.]+) minutos\.$")]
    private static partial Regex TimedOut();

    [GeneratedRegex(@"^La sesión de (.+) caducó o no está iniciada: inicia sesión con «(.+)» y reintenta la tarea\.$")]
    private static partial Regex SignedOut();

    [GeneratedRegex(@"^Límite de uso del proveedor hasta (\S+)\.$")]
    private static partial Regex RateLimitedUntil();

    [GeneratedRegex(@"^El paso (.+), que (.+) requiere, falló: (.+) no llegó a ejecutarse\.$")]
    private static partial Regex StepSkipped();

    [GeneratedRegex(@"^El paso (.+) terminó bien\.$")]
    private static partial Regex StepSucceeded();

    [GeneratedRegex(@"^El paso (.+) falló\.$")]
    private static partial Regex StepFailed();

    [GeneratedRegex(@"^No se integra nada: la ref de (.+) ya no apunta al commit que se verificó\.$")]
    private static partial Regex SourcesMoved();

    [GeneratedRegex(@"^Conflicto de integración en: (.+)\. Se resuelve a mano o con una ejecución de seguimiento\.$")]
    private static partial Regex IntegrationConflict();

    [GeneratedRegex(@"^Integrada en (\S+)\.$")]
    private static partial Regex Integrated();

    [GeneratedRegex(@"^El juez aprobó \(veredicto (.+)\)\.$")]
    private static partial Regex JudgeApproved();

    [GeneratedRegex(@"^El juez pidió cambios \(veredicto (.+)\); se propone una ejecución de seguimiento\.$")]
    private static partial Regex JudgeChangesRequested();

    [GeneratedRegex(@"^El juez rechazó el trabajo \(veredicto (.+)\)\.$")]
    private static partial Regex JudgeRejected();

    [GeneratedRegex(@"^El veredicto (.+) necesita a una persona\.$")]
    private static partial Regex JudgeNeedsPerson();

    [GeneratedRegex(@"^El juez alcanzó el límite de uso de su proveedor hasta (\S+)\.$")]
    private static partial Regex JudgeLimitUntil();

    [GeneratedRegex(@"^No se pudo obtener la aprobación \(equivale a denegarla\): (.*)$", RegexOptions.Singleline)]
    private static partial Regex ApprovalFailed();

    [GeneratedRegex(@"^No se lanza (.+): el registro de decisiones no verifica, así que la ficha del proyecto no se puede comprobar\. Decide una persona: revisa el registro\.$")]
    private static partial Regex BriefLogUnverified();

    [GeneratedRegex(@"^No se lanza (.+): el informe de (.+) \(evidencia (.+)\) no es el que ancló el registro de decisiones; alguien lo reescribió en el almacén\. Decide una persona\.$")]
    private static partial Regex UpstreamTampered();

    [GeneratedRegex(@"^Error del sistema: (.*)$", RegexOptions.Singleline)]
    private static partial Regex SystemError();

    [GeneratedRegex(@"^Aprobado por (.+)\.$")]
    private static partial Regex Approved();

    [GeneratedRegex(@"^Denegado por (.+)\.$")]
    private static partial Regex Denied();

    [GeneratedRegex(@"^Denegado por ([^:]+): (.*)$", RegexOptions.Singleline)]
    private static partial Regex DeniedWithComment();

    [GeneratedRegex(@"^Reintento (\d+) de (\d+) tras: (.*)$", RegexOptions.Singleline)]
    private static partial Regex Retry();

    [GeneratedRegex(@"^La sesión de (.+) no está iniciada: inicia sesión con «(.+)» y reintenta la tarea\.$")]
    private static partial Regex NotSignedIn();

    [GeneratedRegex(@"^La cuota de (.+) sigue agotada según su app-server, hasta (\S+)\.$")]
    private static partial Regex LimitStillOnUntil();

    [GeneratedRegex(@"^La cuota de (.+) sigue agotada según su app-server, sin hora de reinicio\.$")]
    private static partial Regex LimitStillOnNoReset();

    [GeneratedRegex(@"^(El juez alcanzó el límite de uso de su proveedor|El proveedor está sobrecargado o limita la velocidad|Límite de uso del proveedor) sin hora de reinicio: (espera estimada|espera) hasta (\S+)\.$")]
    private static partial Regex WaitNoReset();

    [GeneratedRegex(@"^(El juez alcanzó el límite de uso de su proveedor|El proveedor está sobrecargado o limita la velocidad|Límite de uso del proveedor), con una hora de reinicio que ya pasó \((\S+)\): (espera estimada|espera) hasta (\S+)\.$")]
    private static partial Regex WaitPassedReset();

    [GeneratedRegex(@"^El proveedor está sobrecargado o limita la velocidad hasta (\S+)\.$")]
    private static partial Regex OverloadUntil();

    [GeneratedRegex(@"^En esta sesión de la aplicación ya no se intenta reanudarla sola: el intento anterior falló \((.*)\)\.$", RegexOptions.Singleline)]
    private static partial Regex ResumeFailedBefore();

    [GeneratedRegex(@"^No se pudo reanudar: (.*)$", RegexOptions.Singleline)]
    private static partial Regex ResumeFailed();

    [GeneratedRegex(@"^Se detuvo en esta sesión de la aplicación por un error del sistema \((.*)\): la reanudas tú\.$", RegexOptions.Singleline)]
    private static partial Regex ResumeStoppedByError();

    [GeneratedRegex(@"^El orquestador no tuvo la búsqueda en el historial: (.+) no admite herramientas externas\.$")]
    private static partial Regex HistoryNoMcp();

    [GeneratedRegex(@"^El orquestador no tuvo la búsqueda en el historial: no se pudo preparar \((.*)\)\.$", RegexOptions.Singleline)]
    private static partial Regex HistoryFailed();

    /// <summary>
    /// The coded text of the notice that tells why the orchestrator planned without the search in the history
    /// (<c>PlanProposal.HistoryToolNotice</c>, the sentences of <c>ProjectHistoryTool</c>), derived from the sentence so that a draft
    /// saved with it gets the same one; null when it is not one of them.
    /// </summary>
    public static UserText? HistoryToolNotice(string? notice)
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
    public static UserText? TaskReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        if (Fixed.TryGetValue(reason, out var code))
        {
            return UserText.Of(code);
        }

        if (AttemptLaunched().Match(reason) is { Success: true } launched)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptLaunched, Int(launched, 1));
        }

        if (AttemptStarted().Match(reason) is { Success: true } started)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptStarted, Int(started, 1));
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

        return WorkerReason(reason);
    }

    /// <summary>The reasons the workers write (<c>Workers</c>), with the same arguments the engine gives them live; null for the rest (an agent's own words, the differences of the repository).</summary>
    private static UserText? WorkerReason(string reason)
    {
        if (Retry().Match(reason) is { Success: true } retry)
        {
            var tail = retry.Groups[3].Value;
            return UserText.Of(
                OrchestrationUserTexts.TaskRetry,
                Int(retry, 1),
                Int(retry, 2),
                (object?)TaskReason(tail) ?? tail);
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

        if (SystemError().Match(reason) is { Success: true } system)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerSystemError, system.Groups[1].Value);
        }

        if (Approved().Match(reason) is { Success: true } by)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerApproved, by.Groups[1].Value);
        }

        if (Denied().Match(reason) is { Success: true } deniedBy)
        {
            return UserText.Of(OrchestrationUserTexts.WorkerDenied, deniedBy.Groups[1].Value);
        }

        return DeniedWithComment().Match(reason) is { Success: true } commented
            ? UserText.Of(OrchestrationUserTexts.WorkerDeniedWithComment, commented.Groups[1].Value, commented.Groups[2].Value)
            : null;
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
    public static UserText? ResumeReason(string? reason)
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
    public static UserText? RunSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        if (FixedSummaries.TryGetValue(summary, out var code))
        {
            return UserText.Of(code);
        }

        if (Succeeded().Match(summary) is { Success: true } done)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictSucceeded, Int(done, 1), Int(done, 2));
        }

        if (CancelledByUser().Match(summary) is { Success: true } cancelled)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, CancelReason(cancelled.Groups[1].Value));
        }

        if (ReviewRequired().Match(summary) is { Success: true } review)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictReviewRequired, OrchestrationUserTexts.List(Split(review.Groups[1].Value)));
        }

        if (Failed().Match(summary) is { Success: true } failed && (failed.Groups[1].Success || failed.Groups[2].Success || failed.Groups[3].Success))
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
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictBudgetSpent, Int(failed, 3)));
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

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
}
