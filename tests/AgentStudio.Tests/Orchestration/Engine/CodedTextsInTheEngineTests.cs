using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// What the engine says of a run next to the Spanish sentence it anchors (docs/17 D-47, D-48, ADR-0054 addendum of 2026-11-12): the
/// sentence in the log is the one it always was, the coded text goes alongside in the live events and the result, and a run read back
/// from the log (which stores no code) gets the same code derived from the sentence.
/// </summary>
public sealed class CodedTextsInTheEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<OrchestrationEvent>> ReadAllAsync(IOrchestrationRun run)
    {
        var events = new List<OrchestrationEvent>();
        await foreach (var item in run.Events.WithCancellation(Ct))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public async Task A_run_that_succeeds_gives_its_summary_as_a_code_and_a_run_read_back_gets_the_same_one()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "v1")));
        RunId id;
        RunResult finished;
        List<OrchestrationEvent> live;
        await using (var run = await harness.StartAsync(Plan(Work("T1").Verified("build"))))
        {
            finished = await run.CompleteAsync(harness);
            live = await ReadAllAsync(run);
            id = run.Id;
        }

        Assert.StartsWith("Terminó bien: ", finished.Summary, StringComparison.Ordinal);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.VerdictSucceeded, 1, 0), finished.SummaryText);

        // What every live transition says as a code is what the same sentence gives when it is read back from the log.
        var withText = live.OfType<TaskStatusChangedEvent>().Where(t => t.ReasonText is not null).ToList();
        Assert.NotEmpty(withText);
        Assert.All(withText, t => Assert.Equal(t.ReasonText, DerivedReason(t.Reason)));

        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        var history = await ReadAllAsync(inspected);
        var result = await inspected.Completion;
        Assert.Equal(finished.Summary, result.Summary);
        Assert.Equal(finished.SummaryText, result.SummaryText);
        Assert.Equal(
            live.OfType<TaskStatusChangedEvent>().Select(t => t.ReasonText),
            history.OfType<TaskStatusChangedEvent>().Select(t => t.ReasonText));
    }

    [Fact]
    public async Task A_run_that_fails_for_its_retries_names_the_tasks_as_pieces_of_a_code()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (request, _) => request.TaskId == "T1" ? AgentScript.Failing() : AgentScript.Completed();
        RunResult finished;
        RunId id;
        await using (var run = await harness.StartAsync(Plan(Work("T1") with { MaxRetries = 1 }, Work("T2").After(On("T1")))))
        {
            finished = await run.CompleteAsync(harness);
            id = run.Id;
        }

        var expected = UserText.Of(
            OrchestrationUserTexts.VerdictFailed,
            UserText.Of(
                OrchestrationUserTexts.ListSpace,
                UserText.Of(OrchestrationUserTexts.VerdictNoRetries, "T1"),
                UserText.Of(OrchestrationUserTexts.VerdictUnreachable, "T2")));
        Assert.Equal("Falló. Sin reintentos: T1. Inalcanzables: T2.", finished.Summary);
        Assert.Equal(expected, finished.SummaryText);

        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        Assert.Equal(expected, (await inspected.Completion).SummaryText);
    }

    [Fact]
    public async Task A_cancelled_run_and_its_tasks_say_who_cancelled_it_as_a_code_with_the_reason_as_the_person_wrote_it()
    {
        using var harness = new EngineHarness();
        var started = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true, Started = started, Files = new Dictionary<string, string> { ["src/T1/x.cs"] = "x" } });

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.CancelAsync("el usuario cambió de idea");
        var result = await run.CompleteAsync(harness);
        var events = await ReadAllAsync(run);

        Assert.Equal("Cancelada por el usuario: el usuario cambió de idea", result.Summary);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, "el usuario cambió de idea"), result.SummaryText);
        Assert.Contains(events.OfType<TaskStatusChangedEvent>(), t => t.To == WorkTaskStatus.Cancelled && t.ReasonText == UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, "el usuario cambió de idea"));
    }

    [Theory]
    [InlineData("Interrumpida: la aplicación se cerró mientras se trabajaba en ella.", OrchestrationUserTexts.TaskInterrupted)]
    [InlineData("Reanudada a petición del usuario.", OrchestrationUserTexts.TaskResumedByUser)]
    [InlineData("Reanudada sola por el consentimiento de la cabecera de su ejecución.", OrchestrationUserTexts.TaskResumedByConsent)]
    [InlineData("Su pregunta ya tenía respuesta cuando se cerró la aplicación: sigue con un intento nuevo.", OrchestrationUserTexts.TaskQuestionAnsweredBeforeClose)]
    [InlineData("Terminó la espera de cuota del proveedor.", OrchestrationUserTexts.TaskQuotaWaitEnded)]
    [InlineData("Evaluando la condición.", OrchestrationUserTexts.TaskEvaluatingCondition)]
    [InlineData("Esperando la aprobación del usuario.", OrchestrationUserTexts.TaskAwaitingApproval)]
    [InlineData("El usuario pidió reintentarla.", OrchestrationUserTexts.TaskRetryByUser)]
    [InlineData("Espera tu respuesta: el agente hace la pregunta Q1.", UserTextCodes.QuestionAwaitingAnswer)]
    [InlineData("El agente de T1 quiso hacer una pregunta más de las 3 que admite una tarea: demasiadas preguntas, revisa los requisitos.", UserTextCodes.QuestionTooMany)]
    public void The_reasons_without_arguments_the_engine_anchors_have_a_code(string reason, string code)
    {
        Assert.Equal(UserText.Of(code), DerivedReason(reason));
    }

    [Fact]
    public void The_reasons_with_arguments_give_them_as_pieces_and_a_sentence_of_a_worker_gives_no_code()
    {
        Assert.Equal(UserText.Of(OrchestrationUserTexts.TaskAttemptLaunched, 2), DerivedReason("Arranca el intento 2: el proveedor lanzó al agente."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.TaskAttemptStarted, 3), DerivedReason("Arranca el intento 3."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.TaskJudgeApprovedResumed, "V1"),
            DerivedReason("El juez aprobó (veredicto V1); se concluye al reanudar con el veredicto que ya estaba en el registro."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.TaskNoProvider, "gemini-cli", "ejecutor"), DerivedReason("No hay ningún proveedor «gemini-cli» para ejecutor."));
        Assert.Null(DerivedReason("Completada: terminé lo que me pedías."));
        Assert.Null(DerivedReason("El agente declaró que no pudo terminar: no encuentro el fichero."));
        Assert.Null(DerivedReason(null));
    }

    [Fact]
    public async Task A_failed_verification_and_its_retry_give_their_reasons_as_codes_and_a_run_read_back_derives_the_same_ones()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "v1")));
        harness.Claude.Script("ejecutor", "T1", 2, AgentScript.Completed());
        harness.Verification.Script = (scope, step) => scope.Attempt == 1 && step.Id == "test" ? StepResult.Fail : null;
        RunId id;
        List<OrchestrationEvent> live;
        await using (var run = await harness.StartAsync(Plan(Work("T1").Verified("build", "test"))))
        {
            await run.CompleteAsync(harness);
            live = await ReadAllAsync(run);
            id = run.Id;
        }

        var transitions = live.OfType<TaskStatusChangedEvent>().ToList();
        var failed = transitions.Single(t => t.To == WorkTaskStatus.Failed);
        Assert.Equal("La verificación falló en: test", failed.Reason);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerVerificationFailed, "test"), failed.ReasonText);

        // The retry carries the reason of the failure as a piece of its own, and not as a sentence.
        var retry = transitions.Single(t => t.Reason.StartsWith("Reintento ", StringComparison.Ordinal));
        Assert.EndsWith("tras: La verificación falló en: test", retry.Reason, StringComparison.Ordinal);
        var expectedRetry = Assert.IsType<UserText>(retry.ReasonText);
        Assert.Equal(OrchestrationUserTexts.TaskRetry, expectedRetry.Code);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerVerificationFailed, "test"), expectedRetry.Arguments[2]);

        // Every transition says the same as a code live and read back from the log.
        Assert.All(transitions, t => Assert.Equal(t.ReasonText, DerivedReason(t.Reason)));
        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        var history = (await ReadAllAsync(inspected)).OfType<TaskStatusChangedEvent>().ToList();
        Assert.Equal(transitions.Select(t => t.ReasonText), history.Select(t => t.ReasonText));
    }

    [Theory]
    [InlineData("Se canceló antes de arrancar.", OrchestrationUserTexts.WorkerCancelledBeforeStart)]
    [InlineData("El proveedor canceló la ejecución.", OrchestrationUserTexts.WorkerProviderCancelled)]
    [InlineData("Cancelada por el usuario durante la verificación.", OrchestrationUserTexts.WorkerVerificationCancelled)]
    [InlineData("La verificación no terminó a tiempo.", OrchestrationUserTexts.WorkerVerificationTimeout)]
    [InlineData("Cancelada por el usuario.", OrchestrationUserTexts.WorkerCancelledByUser)]
    [InlineData("Límite de uso del proveedor, sin hora de reinicio.", OrchestrationUserTexts.WorkerRateLimitedNoReset)]
    [InlineData("Abandonada: la herramienta no dio señales de vida.", OrchestrationUserTexts.WorkerToolSilent)]
    [InlineData("Tiempo agotado.", OrchestrationUserTexts.WorkerToolTimeout)]
    [InlineData("La integración no terminó a tiempo.", OrchestrationUserTexts.WorkerIntegrationTimeout)]
    [InlineData("La verificación de la integración falló.", OrchestrationUserTexts.WorkerIntegrationVerificationFailed)]
    [InlineData("La revisión no terminó a tiempo.", OrchestrationUserTexts.WorkerReviewTimeout)]
    [InlineData("El juez alcanzó el límite de uso de su proveedor, sin hora de reinicio.", OrchestrationUserTexts.WorkerJudgeLimitNoReset)]
    public void The_reasons_of_the_workers_without_arguments_have_a_code(string reason, string code)
    {
        Assert.Equal(UserText.Of(code), DerivedReason(reason));
    }

    [Fact]
    public void The_reasons_of_the_workers_with_arguments_give_them_as_pieces()
    {
        var at = new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.FromHours(1));
        var iso = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerStepSucceeded, "build"), DerivedReason("El paso build terminó bien."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerStepFailed, "test"), DerivedReason("El paso test falló."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerStepSkipped, "build", "test"), DerivedReason("El paso build, que test requiere, falló: test no llegó a ejecutarse."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerAbandoned, 2.5), DerivedReason("Abandonada: sin actividad durante más de 2.5 minutos."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerTimedOut, 30d), DerivedReason("Tiempo agotado: más de 30 minutos."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, at), DerivedReason($"Límite de uso del proveedor hasta {iso}."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, at), DerivedReason($"El juez alcanzó el límite de uso de su proveedor hasta {iso}."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeApproved, "V1"), DerivedReason("El juez aprobó (veredicto V1)."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeChangesRequested, "V1"), DerivedReason("El juez pidió cambios (veredicto V1); se propone una ejecución de seguimiento."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeRejected, "V1"), DerivedReason("El juez rechazó el trabajo (veredicto V1)."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeNeedsPerson, "V1"), DerivedReason("El veredicto V1 necesita a una persona."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerSignedOut, "claude-code", "claude auth login"), DerivedReason("La sesión de claude-code caducó o no está iniciada: inicia sesión con «claude auth login» y reintenta la tarea."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerNotSignedIn, "Claude Code", "claude auth login"), DerivedReason("La sesión de Claude Code no está iniciada: inicia sesión con «claude auth login» y reintenta la tarea."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnUntil, "codex-cli", at), DerivedReason($"La cuota de codex-cli sigue agotada según su app-server, hasta {iso}."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnNoReset, "codex-cli"), DerivedReason("La cuota de codex-cli sigue agotada según su app-server, sin hora de reinicio."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerIntegrationConflict, "a.cs, b.cs"), DerivedReason("Conflicto de integración en: a.cs, b.cs. Se resuelve a mano o con una ejecución de seguimiento."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerSourcesMoved, "T1, T2"), DerivedReason("No se integra nada: la ref de T1, T2 ya no apunta al commit que se verificó."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerOutsideScope, "a.cs"), DerivedReason("Cambios fuera del alcance de escritura: a.cs"));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerDenied, "Ana"), DerivedReason("Denegado por Ana."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerDeniedWithComment, "Ana", "no me fío"), DerivedReason("Denegado por Ana: no me fío"));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerDeniedWithComment, "Ana", "no me fío."), DerivedReason("Denegado por Ana: no me fío."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerApproved, "Ana"), DerivedReason("Aprobado por Ana."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.TaskRetry, 2, 3, UserText.Of(OrchestrationUserTexts.WorkerStepFailed, "test")), DerivedReason("Reintento 2 de 3 tras: El paso test falló."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.TaskRetry, 1, 3, "Completada a medias"), DerivedReason("Reintento 1 de 3 tras: Completada a medias"));
    }

    [Fact]
    public void A_cancellation_without_a_reason_and_the_one_of_the_monitor_give_their_reason_as_a_piece()
    {
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, UserText.Of(OrchestrationUserTexts.NoReason)),
            DerivedReason("Cancelada por el usuario: sin motivo"));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, UserText.Of(OrchestrationUserTexts.CancelledFromMonitor)),
            LoggedTexts.RunSummary("Cancelada por el usuario: Cancelada por el usuario desde el monitor."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, "me aburrí"),
            LoggedTexts.RunSummary("Cancelada por el usuario: me aburrí"));
    }

    [Fact]
    public async Task A_run_cancelled_without_a_reason_names_it_as_a_piece_live_and_read_back()
    {
        using var harness = new EngineHarness();
        var started = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true, Started = started, Files = new Dictionary<string, string> { ["src/T1/x.cs"] = "x" } });
        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.CancelAsync(" ");
        var result = await run.CompleteAsync(harness);

        Assert.Equal("Cancelada por el usuario: sin motivo", result.Summary);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, UserText.Of(OrchestrationUserTexts.NoReason)), result.SummaryText);
        Assert.Equal(result.SummaryText, LoggedTexts.RunSummary(result.Summary));
    }

    [Theory]
    [InlineData("Su cabecera no tiene el consentimiento para reanudarse sola: la reanudas tú.", OrchestrationUserTexts.ResumeNoConsent)]
    [InlineData("No está a medias.", OrchestrationUserTexts.ResumeNotHalfway)]
    [InlineData("La pausaste: nunca se reanuda sola.", OrchestrationUserTexts.ResumePaused)]
    [InlineData("La dejaste al salir: nunca se reanuda sola, aunque Windows cerrara la sesión después.", OrchestrationUserTexts.ResumeLeftOnExit)]
    [InlineData("Windows cerró la sesión con la ejecución en marcha.", OrchestrationUserTexts.ResumeSessionEnded)]
    [InlineData("Esperaba cuota cuando se cerró la aplicación.", OrchestrationUserTexts.ResumeWaitingLimit)]
    [InlineData("La cortó un cierre que no pediste.", OrchestrationUserTexts.ResumeUnrequestedClose)]
    [InlineData("La reanudación sola está suspendida mientras la aplicación sale.", OrchestrationUserTexts.ResumeSuspended)]
    [InlineData("No está registrada.", OrchestrationUserTexts.ResumeNotRegistered)]
    public void The_reasons_the_scheduler_gives_for_a_run_it_does_not_resume_have_a_code(string reason, string code)
    {
        Assert.Equal(UserText.Of(code), LoggedTexts.ResumeReason(reason));
    }

    [Fact]
    public void The_reasons_of_a_failed_resume_give_their_detail_as_a_piece()
    {
        Assert.Equal(UserText.Of(OrchestrationUserTexts.ResumeFailed, "boom"), LoggedTexts.ResumeReason("No se pudo reanudar: boom"));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.ResumeFailedBefore, "boom"), LoggedTexts.ResumeReason("En esta sesión de la aplicación ya no se intenta reanudarla sola: el intento anterior falló (boom)."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.ResumeStoppedByError, "boom"), LoggedTexts.ResumeReason("Se detuvo en esta sesión de la aplicación por un error del sistema (boom): la reanudas tú."));
        Assert.Null(LoggedTexts.ResumeReason("otra cosa"));
    }

    [Fact]
    public void The_wait_for_a_limit_the_provider_did_not_announce_gives_each_form_of_its_sentence_a_code()
    {
        var until = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);
        var passed = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var u = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var p = passed.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        UserText Piece(string code) => UserText.Of(code);

        // The same sentence as the one a worker writes when the provider did say when: the same code.
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, until), DerivedReason($"Límite de uso del proveedor hasta {u}."));
        Assert.Equal(UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, until), DerivedReason($"El juez alcanzó el límite de uso de su proveedor hasta {u}."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.QuotaUntil, Piece(OrchestrationUserTexts.QuotaWhoOverload), until),
            DerivedReason($"El proveedor está sobrecargado o limita la velocidad hasta {u}."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.QuotaNoReset, Piece(OrchestrationUserTexts.QuotaWhoPlain), Piece(OrchestrationUserTexts.QuotaWaitEstimated), until),
            DerivedReason($"Límite de uso del proveedor sin hora de reinicio: espera estimada hasta {u}."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.QuotaNoReset, Piece(OrchestrationUserTexts.QuotaWhoJudge), Piece(OrchestrationUserTexts.QuotaWaitEstimated), until),
            DerivedReason($"El juez alcanzó el límite de uso de su proveedor sin hora de reinicio: espera estimada hasta {u}."));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.QuotaPassedReset, Piece(OrchestrationUserTexts.QuotaWhoOverload), passed, Piece(OrchestrationUserTexts.QuotaWait), until),
            DerivedReason($"El proveedor está sobrecargado o limita la velocidad, con una hora de reinicio que ya pasó ({p}): espera hasta {u}."));
    }
    private static UserText? DerivedReason(string? reason) => LoggedTexts.TaskReason(reason);
}
