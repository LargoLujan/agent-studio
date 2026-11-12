using System.Globalization;
using AgentStudio.Application.Localization;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The walk over every sentence the engine anchors with a code of <see cref="OrchestrationUserTexts"/> (ADR-0054 addendum of 2026-11-12):
/// each is given as the log keeps it and must derive the coded text the engine gives live. A code that is born with an anchored
/// sentence and that no case reaches fails the test, unless it is on <see cref="NotAnchored"/> with its reason, and so does a
/// reason on that list that a case does reach. The live side of each sentence is in the tests of the engine, which compare what it
/// told with the derivation (<c>CodedTransitionsAsync</c>, the summaries, the gate of the budget).
/// </summary>
public sealed class LoggedTextsCoverageTests
{
    private enum Kind
    {
        Task,
        Resume,
        Summary,
        History,
    }

    private sealed record Case(Kind Kind, string Sentence, UserText Expected);

    private static UserText Of(string code, params object[] arguments) => UserText.Of(code, arguments);

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static string BudgetSentence(string period, string whose, string what) =>
        $"No se lanza el intento: el presupuesto{period} {whose} es de {what}. Sube el presupuesto y reintenta la tarea.";

    private static UserText Budget(UserText exceeded, params UserText[] more) =>
        OrchestrationUserTexts.Sentences([exceeded, .. more, Of(OrchestrationUserTexts.BudgetRaise)]);

    private static IReadOnlyList<Case> Cases()
    {
        var at = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(1));
        var passed = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var u = Iso(at);
        var p = Iso(passed);
        UserText Piece(string code) => Of(code);
        UserText RepositoryChanged(UserText when, string differences) => Of(OrchestrationUserTexts.WorkerRepositoryChanged, when, differences);
        UserText Project(string code) => Piece(code);
        var cases = new List<Case>();

        void Task(string sentence, UserText expected) => cases.Add(new Case(Kind.Task, sentence, expected));
        void TaskCode(string sentence, string code) => cases.Add(new Case(Kind.Task, sentence, Piece(code)));
        void Resume(string sentence, UserText expected) => cases.Add(new Case(Kind.Resume, sentence, expected));
        void Summary(string sentence, UserText expected) => cases.Add(new Case(Kind.Summary, sentence, expected));
        void History(string sentence, UserText expected) => cases.Add(new Case(Kind.History, sentence, expected));

        // ---- the transitions of a task ----
        Task("Arranca el intento 2: el proveedor lanzó al agente.", Of(OrchestrationUserTexts.TaskAttemptLaunched, 2));
        Task("Arranca el intento 3.", Of(OrchestrationUserTexts.TaskAttemptStarted, 3));
        Task("El juez aprobó (veredicto V1); se concluye al reanudar con el veredicto que ya estaba en el registro.", Of(OrchestrationUserTexts.TaskJudgeApprovedResumed, "V1"));
        Task("El veredicto V1 necesita a una persona; se concluye al reanudar con el veredicto que ya estaba en el registro.", Of(OrchestrationUserTexts.TaskVerdictNeedsPersonResumed, "V1"));
        Task("Cancelada por el usuario: sin motivo", Of(OrchestrationUserTexts.TaskCancelledByUser, Piece(OrchestrationUserTexts.NoReason)));
        Task("Cancelada por el usuario: Cancelada por el usuario desde el monitor.", Of(OrchestrationUserTexts.TaskCancelledByUser, Piece(OrchestrationUserTexts.CancelledFromMonitor)));
        Task("Los nodos Condition no están disponibles en la Fase 3.", Of(OrchestrationUserTexts.TaskNodeUnavailable, "Condition"));
        Task("No hay ningún proveedor «gemini-cli» para ejecutor.", Of(OrchestrationUserTexts.TaskNoProvider, "gemini-cli", "ejecutor"));
        Task("La tarea T3 parte del código de varias tareas (T1, T2) sin una integración.", Of(OrchestrationUserTexts.TaskNoIntegration, "T3", "T1, T2"));
        Task("Reintento 2 de 3 tras: El paso test falló.", Of(OrchestrationUserTexts.TaskRetry, 2, 3, Of(OrchestrationUserTexts.WorkerStepFailed, "test")));
        TaskCode("Espera tu respuesta: el agente hace la pregunta Q1.", UserTextCodes.QuestionAwaitingAnswer);
        TaskCode("El agente de T1 quiso hacer una pregunta más de las 3 que admite una tarea: demasiadas preguntas, revisa los requisitos.", UserTextCodes.QuestionTooMany);
        TaskCode("El usuario pidió reintentarla.", OrchestrationUserTexts.TaskRetryByUser);
        TaskCode("Terminó la espera de cuota del proveedor.", OrchestrationUserTexts.TaskQuotaWaitEnded);
        TaskCode("Esperando la aprobación del usuario.", OrchestrationUserTexts.TaskAwaitingApproval);
        TaskCode("Evaluando la condición.", OrchestrationUserTexts.TaskEvaluatingCondition);
        TaskCode("Interrumpida: la aplicación se cerró mientras se trabajaba en ella.", OrchestrationUserTexts.TaskInterrupted);
        TaskCode("Su pregunta ya tenía respuesta cuando se cerró la aplicación: sigue con un intento nuevo.", OrchestrationUserTexts.TaskQuestionAnsweredBeforeClose);
        TaskCode("Reanudada a petición del usuario.", OrchestrationUserTexts.TaskResumedByUser);
        TaskCode("Reanudada sola por el consentimiento de la cabecera de su ejecución.", OrchestrationUserTexts.TaskResumedByConsent);

        // ---- what the workers write ----
        TaskCode("Se canceló antes de arrancar.", OrchestrationUserTexts.WorkerCancelledBeforeStart);
        TaskCode("El proveedor canceló la ejecución.", OrchestrationUserTexts.WorkerProviderCancelled);
        TaskCode("El agente hace una pregunta a una persona.", OrchestrationUserTexts.WorkerAgentAsks);
        TaskCode("Cancelada por el usuario durante la verificación.", OrchestrationUserTexts.WorkerVerificationCancelled);
        TaskCode("La verificación no terminó a tiempo.", OrchestrationUserTexts.WorkerVerificationTimeout);
        TaskCode("Cancelada por el usuario.", OrchestrationUserTexts.WorkerCancelledByUser);
        TaskCode("Límite de uso del proveedor, sin hora de reinicio.", OrchestrationUserTexts.WorkerRateLimitedNoReset);
        TaskCode("Abandonada: la herramienta no dio señales de vida.", OrchestrationUserTexts.WorkerToolSilent);
        TaskCode("Tiempo agotado.", OrchestrationUserTexts.WorkerToolTimeout);
        TaskCode("La integración no terminó a tiempo.", OrchestrationUserTexts.WorkerIntegrationTimeout);
        TaskCode("La verificación de la integración falló.", OrchestrationUserTexts.WorkerIntegrationVerificationFailed);
        TaskCode("La revisión no terminó a tiempo.", OrchestrationUserTexts.WorkerReviewTimeout);
        TaskCode("El juez alcanzó el límite de uso de su proveedor, sin hora de reinicio.", OrchestrationUserTexts.WorkerJudgeLimitNoReset);
        Task("No se pudo lanzar el agente: boom", Of(OrchestrationUserTexts.WorkerLaunchFailed, "boom"));
        Task("El agente terminó con estado Failed.", Of(OrchestrationUserTexts.WorkerAgentEndedStatus, "Failed"));
        Task("El informe del agente no es válido: falta el resumen", Of(OrchestrationUserTexts.WorkerReportInvalid, "falta el resumen"));
        Task("Cambios fuera del alcance de escritura: a.cs", Of(OrchestrationUserTexts.WorkerOutsideScope, "a.cs"));
        Task("La verificación falló en: build, test", Of(OrchestrationUserTexts.WorkerVerificationFailed, "build, test"));
        Task("Abandonada: sin actividad durante más de 2.5 minutos.", Of(OrchestrationUserTexts.WorkerAbandoned, 2.5));
        Task("Tiempo agotado: más de 30 minutos.", Of(OrchestrationUserTexts.WorkerTimedOut, 30d));
        Task("La sesión de claude-code caducó o no está iniciada: inicia sesión con «claude auth login» y reintenta la tarea.", Of(OrchestrationUserTexts.WorkerSignedOut, "claude-code", "claude auth login"));
        Task("La sesión de Claude Code no está iniciada: inicia sesión con «claude auth login» y reintenta la tarea.", Of(OrchestrationUserTexts.WorkerNotSignedIn, "Claude Code", "claude auth login"));
        Task($"Límite de uso del proveedor hasta {u}.", Of(OrchestrationUserTexts.WorkerRateLimitedUntil, at));
        Task($"La cuota de codex-cli sigue agotada según su app-server, hasta {u}.", Of(OrchestrationUserTexts.WorkerLimitStillOnUntil, "codex-cli", at));
        Task("La cuota de codex-cli sigue agotada según su app-server, sin hora de reinicio.", Of(OrchestrationUserTexts.WorkerLimitStillOnNoReset, "codex-cli"));
        Task("El paso build terminó bien.", Of(OrchestrationUserTexts.WorkerStepSucceeded, "build"));
        Task("El paso test falló.", Of(OrchestrationUserTexts.WorkerStepFailed, "test"));
        Task("El paso build, que test requiere, falló: test no llegó a ejecutarse.", Of(OrchestrationUserTexts.WorkerStepSkipped, "build", "test"));
        Task("No se integra nada: la ref de T1, T2 ya no apunta al commit que se verificó.", Of(OrchestrationUserTexts.WorkerSourcesMoved, "T1, T2"));
        Task("Conflicto de integración en: a.cs, b.cs. Se resuelve a mano o con una ejecución de seguimiento.", Of(OrchestrationUserTexts.WorkerIntegrationConflict, "a.cs, b.cs"));
        Task("Integrada en abc123.", Of(OrchestrationUserTexts.WorkerIntegrated, "abc123"));
        Task("El juez aprobó (veredicto V1).", Of(OrchestrationUserTexts.WorkerJudgeApproved, "V1"));
        Task("El juez pidió cambios (veredicto V1); se propone una ejecución de seguimiento.", Of(OrchestrationUserTexts.WorkerJudgeChangesRequested, "V1"));
        Task("El juez rechazó el trabajo (veredicto V1).", Of(OrchestrationUserTexts.WorkerJudgeRejected, "V1"));
        Task("El veredicto V1 necesita a una persona.", Of(OrchestrationUserTexts.WorkerJudgeNeedsPerson, "V1"));
        Task($"El juez alcanzó el límite de uso de su proveedor hasta {u}.", Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, at));
        Task("No se pudo obtener la aprobación (equivale a denegarla): boom", Of(OrchestrationUserTexts.WorkerApprovalFailed, "boom"));
        Task("No se lanza T1: el registro de decisiones no verifica, así que la ficha del proyecto no se puede comprobar. Decide una persona: revisa el registro.", Of(OrchestrationUserTexts.WorkerBriefLogUnverified, "T1"));
        Task("No se lanza T2: el informe de T1 (evidencia E1) no es el que ancló el registro de decisiones; alguien lo reescribió en el almacén. Decide una persona.", Of(OrchestrationUserTexts.WorkerUpstreamTampered, "T2", "T1", "E1"));
        Task("Error del sistema: boom", Of(OrchestrationUserTexts.WorkerSystemError, "boom"));
        Task("Aprobado por Ana.", Of(OrchestrationUserTexts.WorkerApproved, "Ana"));
        Task("Denegado por Ana.", Of(OrchestrationUserTexts.WorkerDenied, "Ana"));
        Task("Denegado por Ana: no me fío", Of(OrchestrationUserTexts.WorkerDeniedWithComment, "Ana", "no me fío"));

        // ---- the memory that does not fit or does not verify, the repository that changed, the evidence of an integration ----
        Task(
            "No se lanza T1: sus requisitos obligatorios necesitan 5000 caracteres y el presupuesto de contexto es de 4000, así que no caben junto con la memoria del proyecto. Decide una persona: reduce los requisitos de la tarea o quita la memoria de ejecutor en la plantilla.",
            Of(OrchestrationUserTexts.WorkerMemoryTooSmall, "T1", Of(OrchestrationUserTexts.MemoryDetailFigures, 5000, 4000), "ejecutor"));
        Task(
            "No se lanza T1: The context is too small. Decide una persona: reduce los requisitos de la tarea o quita la memoria de ejecutor en la plantilla.",
            Of(OrchestrationUserTexts.WorkerMemoryTooSmall, "T1", Piece(OrchestrationUserTexts.MemoryDetailUnknown), "ejecutor"));
        Task(
            "No se lanza T1: el registro de decisiones no verifica, así que la memoria del proyecto no se puede comprobar. Decide una persona: revisa el registro o quita la memoria de ejecutor en la plantilla.",
            Of(OrchestrationUserTexts.WorkerMemoryUnverified, "T1", "ejecutor"));
        Task(
            "No se lanza T1: el registro de decisiones no verifica (entrada 7: hash roto), así que la memoria del proyecto no se puede comprobar. Decide una persona: revisa el registro o quita la memoria de ejecutor en la plantilla.",
            Of(OrchestrationUserTexts.WorkerMemoryUnverifiedEntry, "T1", 7L, "hash roto", "ejecutor"));
        Task("El repositorio del usuario había cambiado antes de lanzar el agente: HEAD moved.", Of(OrchestrationUserTexts.WorkerRepositoryBeforeLaunch, "HEAD moved."));
        Task("El repositorio del usuario cambió mientras trabajaba el agente: HEAD moved. Stash changed.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryWhileAgent), "HEAD moved. Stash changed."));
        Task("El repositorio del usuario cambió durante la verificación: HEAD moved.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryDuringVerification), "HEAD moved."));
        Task("El repositorio del usuario cambió durante la integración: HEAD moved.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryDuringIntegration), "HEAD moved."));
        Task("El repositorio del usuario cambió antes de la integración: HEAD moved.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryBeforeIntegration), "HEAD moved."));
        Task("El repositorio del usuario cambió durante la revisión: HEAD moved.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryDuringReview), "HEAD moved."));
        Task("El repositorio del usuario cambió antes de la revisión: HEAD moved.", RepositoryChanged(Piece(OrchestrationUserTexts.RepositoryBeforeReview), "HEAD moved."));
        Task("El repositorio del usuario cambió durante el paso lint: HEAD moved.", RepositoryChanged(Of(OrchestrationUserTexts.RepositoryDuringSteps, Of(OrchestrationUserTexts.StepsOne, "lint")), "HEAD moved."));
        Task(
            "El repositorio del usuario cambió durante los pasos restore, build y test: HEAD moved.",
            RepositoryChanged(Of(OrchestrationUserTexts.RepositoryDuringSteps, Of(OrchestrationUserTexts.StepsMany, Of(OrchestrationUserTexts.ListAnd, "restore, build", "test"))), "HEAD moved."));
        Task(
            "El repositorio del usuario cambió durante los pasos build y test, que se detuvieron antes de terminar: HEAD moved.",
            RepositoryChanged(Of(OrchestrationUserTexts.RepositoryDuringStepsStopped, Of(OrchestrationUserTexts.StepsMany, Of(OrchestrationUserTexts.ListAnd, "build", "test"))), "HEAD moved."));
        Task(
            "El repositorio del usuario cambió antes de los pasos build y test: HEAD moved.",
            RepositoryChanged(Of(OrchestrationUserTexts.RepositoryBeforeSteps, Of(OrchestrationUserTexts.StepsMany, Of(OrchestrationUserTexts.ListAnd, "build", "test"))), "HEAD moved."));
        Task(
            "Una evidencia de la integración cambió en el almacén entre su captura y su lectura: alguien la reescribió, así que la integración no se da por buena ni se reintenta. E1 changed.",
            Of(OrchestrationUserTexts.WorkerEvidenceTampered, "E1 changed."));
        Task(
            "La evidencia de la integración no es la que capturó el integrador y no se da por buena: E2 has no record.",
            Of(OrchestrationUserTexts.WorkerEvidenceUnanchored, "E2 has no record."));

        // ---- the wait for the usage limit ----
        Task($"El proveedor está sobrecargado o limita la velocidad hasta {u}.", Of(OrchestrationUserTexts.QuotaUntil, Piece(OrchestrationUserTexts.QuotaWhoOverload), at));
        Task(
            $"Límite de uso del proveedor sin hora de reinicio: espera estimada hasta {u}.",
            Of(OrchestrationUserTexts.QuotaNoReset, Piece(OrchestrationUserTexts.QuotaWhoPlain), Piece(OrchestrationUserTexts.QuotaWaitEstimated), at));
        Task(
            $"El juez alcanzó el límite de uso de su proveedor, con una hora de reinicio que ya pasó ({p}): espera hasta {u}.",
            Of(OrchestrationUserTexts.QuotaPassedReset, Piece(OrchestrationUserTexts.QuotaWhoJudge), passed, Piece(OrchestrationUserTexts.QuotaWait), at));

        // ---- the budget of the project and of the milestone ----
        var tokens = "1000 tokens y ya van 801; con este intento serían 1001";
        var tokensExceeded = Of(OrchestrationUserTexts.BudgetExceededTokens, Project(OrchestrationUserTexts.BudgetNameProjectTotal), 1000L, 801L, 1001L);
        Task(BudgetSentence(string.Empty, "del proyecto", tokens), Budget(tokensExceeded));
        Task(BudgetSentence(" de hoy", "del proyecto", tokens), Budget(Of(OrchestrationUserTexts.BudgetExceededTokens, Project(OrchestrationUserTexts.BudgetNameProjectDay), 1000L, 801L, 1001L)));
        Task(BudgetSentence(" de esta semana", "del proyecto", tokens), Budget(Of(OrchestrationUserTexts.BudgetExceededTokens, Project(OrchestrationUserTexts.BudgetNameProjectWeek), 1000L, 801L, 1001L)));
        Task(BudgetSentence(" de este mes", "del proyecto", tokens), Budget(Of(OrchestrationUserTexts.BudgetExceededTokens, Project(OrchestrationUserTexts.BudgetNameProjectMonth), 1000L, 801L, 1001L)));
        Task(
            BudgetSentence(string.Empty, "del hito mil-1", tokens),
            Budget(Of(OrchestrationUserTexts.BudgetExceededTokens, Of(OrchestrationUserTexts.BudgetNameMilestoneTotal, "mil-1"), 1000L, 801L, 1001L)));
        Task(
            BudgetSentence(string.Empty, "del proyecto", "1000 tokens y ya van 1000 (sin estimación de este intento)"),
            Budget(Of(OrchestrationUserTexts.BudgetExceededTokensUnknown, Project(OrchestrationUserTexts.BudgetNameProjectTotal), 1000L, 1000L)));
        Task(
            BudgetSentence(string.Empty, "del proyecto", "500 tokens y ya van 500; con este intento serían 510 (1 intentos sin cifra de tokens no cuentan)"),
            Budget(Of(OrchestrationUserTexts.BudgetExceededTokens, Project(OrchestrationUserTexts.BudgetNameProjectTotal), 500L, 500L, 510L), Of(OrchestrationUserTexts.BudgetPartial, 1)));
        Task(
            BudgetSentence(string.Empty, "del proyecto", "2 ejecuciones de agente y ya van 2; con este intento serían 3"),
            Budget(Of(OrchestrationUserTexts.BudgetExceededRuns, Project(OrchestrationUserTexts.BudgetNameProjectTotal), 2, 2, 3)));
        Task(
            "No se lanza el intento: no se pudo comprobar el presupuesto (no hay con qué leer lo gastado), y sin saberlo no se arriesga la cuota.",
            Of(OrchestrationUserTexts.BudgetUnreadable, Piece(OrchestrationUserTexts.BudgetWhyNoUsage)));
        Task(
            "No se lanza el intento: no se pudo comprobar el presupuesto (el registro del proyecto no es de confianza: hash roto), y sin saberlo no se arriesga la cuota.",
            Of(OrchestrationUserTexts.BudgetUnreadable, Piece(OrchestrationUserTexts.BudgetWhyUntrusted)));
        Task(
            "No se lanza el intento: no se pudo comprobar el presupuesto (disk full), y sin saberlo no se arriesga la cuota.",
            Of(OrchestrationUserTexts.BudgetUnreadable, Piece(OrchestrationUserTexts.BudgetWhyFailed)));

        // ---- why a run is not resumed by itself ----
        Resume("Su cabecera no tiene el consentimiento para reanudarse sola: la reanudas tú.", Piece(OrchestrationUserTexts.ResumeNoConsent));
        Resume("No está a medias.", Piece(OrchestrationUserTexts.ResumeNotHalfway));
        Resume("La pausaste: nunca se reanuda sola.", Piece(OrchestrationUserTexts.ResumePaused));
        Resume("La dejaste al salir: nunca se reanuda sola, aunque Windows cerrara la sesión después.", Piece(OrchestrationUserTexts.ResumeLeftOnExit));
        Resume("Windows cerró la sesión con la ejecución en marcha.", Piece(OrchestrationUserTexts.ResumeSessionEnded));
        Resume("Esperaba cuota cuando se cerró la aplicación.", Piece(OrchestrationUserTexts.ResumeWaitingLimit));
        Resume("La cortó un cierre que no pediste.", Piece(OrchestrationUserTexts.ResumeUnrequestedClose));
        Resume("La reanudación sola está suspendida mientras la aplicación sale.", Piece(OrchestrationUserTexts.ResumeSuspended));
        Resume("No está registrada.", Piece(OrchestrationUserTexts.ResumeNotRegistered));
        Resume("No se pudo reanudar: boom", Of(OrchestrationUserTexts.ResumeFailed, "boom"));
        Resume("En esta sesión de la aplicación ya no se intenta reanudarla sola: el intento anterior falló (boom).", Of(OrchestrationUserTexts.ResumeFailedBefore, "boom"));
        Resume("Se detuvo en esta sesión de la aplicación por un error del sistema (boom): la reanudas tú.", Of(OrchestrationUserTexts.ResumeStoppedByError, "boom"));

        // ---- how a run ended ----
        UserText Untested(string task, string? commit) => commit is null
            ? Of(OrchestrationUserTexts.VerdictUntestedNoCommit, task)
            : Of(OrchestrationUserTexts.VerdictUntestedCommit, task, commit);
        const string testsRequired = "Falló: la política de pruebas exige que el commit final de la ejecución pase pruebas capturadas por el sistema, y no hay ninguna ejecución de pruebas que pase sobre ";
        Summary("Terminó bien: 3 tareas completadas y 1 omitidas.", Of(OrchestrationUserTexts.VerdictSucceeded, 3, 1));
        Summary("Cancelada por el usuario: me aburrí", Of(OrchestrationUserTexts.VerdictCancelledByUser, "me aburrí"));
        Summary("Cancelada por el usuario: sin motivo", Of(OrchestrationUserTexts.VerdictCancelledByUser, Piece(OrchestrationUserTexts.NoReason)));
        Summary("Falló: la política de revisión exige una revisión independiente aprobada de T1, T2, y no la hubo.", Of(OrchestrationUserTexts.VerdictReviewRequired, OrchestrationUserTexts.List(["T1", "T2"])));
        Summary(testsRequired + "T3 (abc123).", Of(OrchestrationUserTexts.VerdictTestsRequired, Untested("T3", "abc123")));
        Summary(testsRequired + "T3 (sin commit).", Of(OrchestrationUserTexts.VerdictTestsRequired, Untested("T3", null)));
        Summary(
            testsRequired + "T1 (abc), T2 (sin commit), T3 (def).",
            Of(OrchestrationUserTexts.VerdictTestsRequired, OrchestrationUserTexts.List([Untested("T1", "abc"), Untested("T2", null), Untested("T3", "def")])));
        Summary(
            "Falló. Sin reintentos: T1. Inalcanzables: T2. Se agotó el presupuesto de ejecuciones de agente (5).",
            Of(
                OrchestrationUserTexts.VerdictFailed,
                OrchestrationUserTexts.Sentences(
                [
                    Of(OrchestrationUserTexts.VerdictNoRetries, "T1"),
                    Of(OrchestrationUserTexts.VerdictUnreachable, "T2"),
                    Of(OrchestrationUserTexts.VerdictBudgetSpent, 5),
                ])));
        Summary("Cancelada: la cancelación se había aplicado a todas las tareas antes de la interrupción.", Piece(OrchestrationUserTexts.VerdictCancelledAfterStop));
        Summary("Se detuvo: el repositorio del usuario cambió durante la ejecución.", Piece(OrchestrationUserTexts.VerdictStoppedRepository));
        Summary("Se detuvo: la ref de una tarea ya no apunta al commit que se verificó, y no se integró nada de ella.", Piece(OrchestrationUserTexts.VerdictStoppedSourceMoved));
        Summary("Se detuvo: una evidencia de la integración cambió en el almacén entre su captura y su lectura; esa integración no se da por buena, ni se revisa ni se entrega.", Piece(OrchestrationUserTexts.VerdictStoppedEvidence));
        Summary("Interrumpida: no terminó y no hay ningún proceso detrás. Se puede reanudar.", Piece(OrchestrationUserTexts.VerdictInterruptedInLog));

        // ---- why the orchestrator planned without the search in the history ----
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.NotThePlannerReason, Piece(OrchestrationUserTexts.HistoryNotThePlanner));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.DisabledReason, Piece(OrchestrationUserTexts.HistoryDisabled));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.NoMemoryReason, Piece(OrchestrationUserTexts.HistoryNoMemory));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.NotReadOnlyReason, Piece(OrchestrationUserTexts.HistoryNotReadOnly));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.EmptyReason, Piece(OrchestrationUserTexts.HistoryEmpty));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.NoMcpReason(AgentStudio.Domain.Providers.ProviderId.CodexCli), Of(OrchestrationUserTexts.HistoryNoMcp, "Codex"));
        History(AgentStudio.Application.Semantic.ProjectHistoryTool.FailedReason("the disk is full."), Of(OrchestrationUserTexts.HistoryFailed, "the disk is full"));

        return cases;
    }

    private static UserText? Derive(Case c) => c.Kind switch
    {
        Kind.Task => LoggedTexts.TaskReason(c.Sentence),
        Kind.Resume => LoggedTexts.ResumeReason(c.Sentence),
        Kind.Summary => LoggedTexts.RunSummary(c.Sentence),
        _ => LoggedTexts.HistoryToolNotice(c.Sentence),
    };

    public static TheoryData<int> Indexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Cases().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public void Each_sentence_the_engine_anchors_gives_its_code_and_its_pieces(int index)
    {
        var c = Cases()[index];

        Assert.Equal(c.Expected, Derive(c));
    }

    private static void CollectCodes(UserText text, ISet<string> codes)
    {
        codes.Add(text.Code);
        foreach (var piece in text.Arguments.OfType<UserText>())
        {
            CollectCodes(piece, codes);
        }
    }

    /// <summary>The codes that are not born with an anchored sentence, or that no sentence can reach, each with the reason.</summary>
    private static readonly Dictionary<string, string> NotAnchored = new(StringComparer.Ordinal)
    {
        [OrchestrationUserTexts.VerdictInterrupted] = "The detail of a phase the monitor shows (RunPhaseChangedEvent.DetailText): the sentence is not anchored, only a final summary is.",
        [OrchestrationUserTexts.VerdictAwaitingHuman] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictBlockedLaunch] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictWaitingQuota] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictWaitingQuotaEstimated] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictPausedChecks] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictPausedLong] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictPaused] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.VerdictDeadlock] = "The detail of a phase the monitor shows; it is not anchored.",
        [OrchestrationUserTexts.BudgetNameMilestoneDay] = "The budget of a milestone is always a total (ProjectBudgetGate.LimitsAsync): the sentence with a period of a milestone is never written.",
        [OrchestrationUserTexts.BudgetNameMilestoneWeek] = "The budget of a milestone is always a total (ProjectBudgetGate.LimitsAsync): the sentence with a period of a milestone is never written.",
        [OrchestrationUserTexts.BudgetNameMilestoneMonth] = "The budget of a milestone is always a total (ProjectBudgetGate.LimitsAsync): the sentence with a period of a milestone is never written.",
    };

    private static bool BornWithAnAnchoredSentence(string code) =>
        code.StartsWith("task.", StringComparison.Ordinal)
        || code.StartsWith("worker.", StringComparison.Ordinal)
        || code.StartsWith("quota.", StringComparison.Ordinal)
        || code.StartsWith("resume.", StringComparison.Ordinal)
        || code.StartsWith("history.", StringComparison.Ordinal)
        || code.StartsWith("project.budget-", StringComparison.Ordinal)
        || code.StartsWith("run.verdict.", StringComparison.Ordinal)
        || code.StartsWith("plan.list.", StringComparison.Ordinal)
        || code is OrchestrationUserTexts.NoReason or OrchestrationUserTexts.CancelledFromMonitor;

    [Fact]
    public void Every_code_that_is_born_with_an_anchored_sentence_is_reached_by_a_case_or_is_listed_with_its_reason()
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Cases())
        {
            CollectCodes(c.Expected, reached);
        }

        var anchored = OrchestrationUserTexts.All.Where(BornWithAnAnchoredSentence).ToList();

        var unreached = anchored.Where(code => !reached.Contains(code) && !NotAnchored.ContainsKey(code)).ToList();
        Assert.Empty(unreached);

        // A reason on the list is true: no case reaches it, and it is a code of the class.
        var wronglyListed = NotAnchored.Keys.Where(reached.Contains).ToList();
        Assert.Empty(wronglyListed);
        var unknown = NotAnchored.Keys.Where(code => !OrchestrationUserTexts.All.Contains(code)).ToList();
        Assert.Empty(unknown);        Assert.All(NotAnchored.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    /// <summary>
    /// What is left without a code on purpose: the words of an agent or of a provider, and the sentence of an older version. The screen
    /// shows them as they were anchored. A provider error written as one of the sentences above would get its code: it is shown
    /// translated although it is not the system's, which is the cost of deriving the code from the sentence (D-48).
    /// </summary>
    [Theory]
    [InlineData("Completada: terminé lo que me pedías.")]
    [InlineData("El agente declaró que no pudo terminar: no encuentro el fichero.")]
    [InlineData("El agente necesita a una persona: falta una clave. Problemas: ninguno.")]
    [InlineData("El proveedor devolvió un error 500.")]
    [InlineData("Una frase que ninguna versión del motor escribió.")]
    public void The_words_of_an_agent_or_of_a_provider_and_unknown_sentences_have_no_code(string sentence)
    {
        Assert.Null(LoggedTexts.TaskReason(sentence));
        Assert.Null(LoggedTexts.RunSummary(sentence));
        Assert.Null(LoggedTexts.ResumeReason(sentence));
    }

    [Fact]
    public void Nothing_that_derives_throws_whatever_the_sentence_is()
    {
        var filler = new string('x', 5_000);
        foreach (var sentence in new[] { string.Empty, " ", "\0", "Arranca el intento " + filler, "Terminó bien: " + filler, "No se lanza " + filler, "El paso " + filler })
        {
            _ = LoggedTexts.TaskReason(sentence);
            _ = LoggedTexts.RunSummary(sentence);
            _ = LoggedTexts.ResumeReason(sentence);
            _ = LoggedTexts.HistoryToolNotice(sentence);
        }

        Assert.Null(LoggedTexts.TaskReason(null));
    }
}
