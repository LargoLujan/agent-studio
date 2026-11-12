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
        Assert.Null(DerivedReason("La verificación falló en: build, test"));
        Assert.Null(DerivedReason(null));
    }

    private static UserText? DerivedReason(string? reason) => LoggedTexts.TaskReason(reason);
}
