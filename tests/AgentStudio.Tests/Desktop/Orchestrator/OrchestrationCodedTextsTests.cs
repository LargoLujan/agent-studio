using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The coded texts of Orchestration (docs/17 D-47, ADR-0054 addendum of 2026-11-12): every code has its text in Spanish of Spain and
/// in English with the same placeholders, and what the person reads is painted from the resources in the language in use, never
/// from a sentence of the layers below. What is anchored (the warning the approval carries, the first notice of a run) is not
/// painted, and a test of the engine fixes it.
/// </summary>
public sealed partial class OrchestrationCodedTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static IReadOnlyList<string> Markers(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var code in OrchestrationUserTexts.All)
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_code_has_its_text_in_both_languages_with_the_same_placeholders(string code)
    {
        var key = UserText.KeyOf(code);

        Assert.True(Spanish.Has(key), key + " (es)");
        Assert.True(English.Has(key), key + " (en)");
        Assert.Equal(Markers(Spanish.Get(key)), Markers(English.Get(key)));
        // Only a text that is nothing but pieces and punctuation (a list, «{0}: {1}.», «{0} ({1})») reads the same in both.
        Assert.True(Spanish.Get(key) != English.Get(key) || !Placeholder().Replace(Spanish.Get(key), string.Empty).Any(char.IsLetter), key);
    }

    [Fact]
    public void The_codes_are_all_different_valid_and_the_list_holds_every_constant_of_the_class()
    {
        var all = OrchestrationUserTexts.All;
        var constants = typeof(OrchestrationUserTexts)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, code => Assert.True(UserText.IsCode(code), code));
        Assert.Equal(constants.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_refusal_with_a_code_is_painted_in_the_language_in_use_and_any_other_failure_shows_its_technical_message()
    {
        var refusal = new UserTextException(UserText.Of(OrchestrationUserTexts.DeliveryNotChecked), "The plan is too big to check.");

        Assert.Equal("Atención: el plan es demasiado grande para comprobar si se podrá entregar a tu rama.", OrchestratorErrorText.Detail(refusal, Spanish));
        Assert.Equal("Warning: the plan is too big to check whether it can be delivered to your branch.", OrchestratorErrorText.Detail(refusal, English));
        Assert.Equal("boom", OrchestratorErrorText.Detail(new InvalidOperationException("boom"), English));
    }

    private static PlanWarning Warning(PlanWarningCode code, string task, IReadOnlyList<ConditionBranch>? when = null, bool timing = false) =>
        new(code, new TaskId(task), "message for logs") { When = when ?? [], DependsOnTiming = timing };

    [Fact]
    public void The_delivery_warning_names_the_same_tasks_in_both_languages_and_the_logged_notice_stays_in_spanish()
    {
        var warnings = new[]
        {
            Warning(PlanWarningCode.NotDeliverable, "T1"),
            Warning(PlanWarningCode.NotDeliverable, "T2"),
            Warning(PlanWarningCode.MayNotBeDeliverable, "T3", [new ConditionBranch(new TaskId("C1"), ConditionBranches.Yes)], timing: true),
        };
        var texts = PlanWarningText.Texts(warnings);

        var english = OrchestratorCodedText.Sentences(texts, English)!;
        var spanish = OrchestratorCodedText.Sentences(texts, Spanish)!;

        Assert.Contains("what T1 and T2 write cannot be delivered to your branch", english, StringComparison.Ordinal);
        Assert.Contains("what T3 writes may not be deliverable", english, StringComparison.Ordinal);
        Assert.Contains("(for example, if C1 takes the yes branch) and it depends on", english, StringComparison.Ordinal);
        Assert.Contains("lo que escriben T1 y T2 no se podrá entregar", spanish, StringComparison.Ordinal);
        Assert.Contains("si C1 toma la rama sí", spanish, StringComparison.Ordinal);
        Assert.DoesNotContain("Atención", english, StringComparison.Ordinal);

        // What goes to the log as the first notice of the run is the sentence as it was.
        Assert.StartsWith("Atención: lo que escriben T1 y T2 no se podrá entregar a tu rama", PlanWarningText.Notice(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_without_delivery_warnings_has_no_coded_text()
    {
        Assert.Empty(PlanWarningText.Texts([]));
        Assert.Null(OrchestratorCodedText.Sentences(PlanWarningText.Texts([]), English));
    }

    [Fact]
    public void The_warning_about_writers_names_the_same_tasks_agents_and_steps_in_both_languages_and_the_anchored_one_is_unchanged()
    {
        var writers = new[]
        {
            new MediumIntegrityWriter(new TaskId("T1"), MediumIntegrityReason.Shell) { Agent = new AgentId("ejecutorShell") },
            new MediumIntegrityWriter(new TaskId("T1"), MediumIntegrityReason.ToolsBeyondMeasuredProfile) { Agent = new AgentId("ejecutorShell"), Tools = ["Monitor", "Workflow"] },
            new MediumIntegrityWriter(new TaskId("T2"), MediumIntegrityReason.UnmeasuredCli) { Agent = new AgentId("ejecutorGemini"), Provider = ProviderId.GeminiCli },
            new MediumIntegrityWriter(new TaskId("L1"), MediumIntegrityReason.StepWithoutIsolation) { Step = "lint" },
        };
        var texts = VerificationStepText.PlanWarningTexts(writers);

        var english = OrchestratorCodedText.Sentences(texts, English)!;
        var spanish = OrchestratorCodedText.Sentences(texts, Spanish)!;

        Assert.Contains("T1: the agent \"ejecutorShell\" has Bash and the agent \"ejecutorShell\" has tools outside the measured profile (Monitor, Workflow).", english, StringComparison.Ordinal);
        Assert.Contains("T2: the agent \"ejecutorGemini\" writes with gemini-cli, whose confinement was not measured.", english, StringComparison.Ordinal);
        Assert.Contains("L1: the step \"lint\" runs without isolation.", english, StringComparison.Ordinal);
        Assert.Contains("T1: el agente «ejecutorShell» tiene Bash y el agente «ejecutorShell» tiene herramientas fuera del perfil medido (Monitor, Workflow).", spanish, StringComparison.Ordinal);
        Assert.Contains("L1: el paso «lint» se ejecuta sin aislamiento.", spanish, StringComparison.Ordinal);

        // The approval carries the sentence as it was anchored, in Spanish, whatever the language of the screen.
        Assert.StartsWith("Atención: este plan tiene escritores con integridad Medium. T1: el agente «ejecutorShell» tiene Bash", VerificationStepText.PlanWarning(writers), StringComparison.Ordinal);
        Assert.Empty(VerificationStepText.PlanWarningTexts([]));
    }

    [Fact]
    public void A_step_line_is_painted_in_the_language_in_use_and_keeps_its_plain_part_as_it_is()
    {
        var step = DotNetVerificationProfile.Propose().Find("test")! with { Arguments = ["test", "--no-build"], Isolation = ProcessIsolation.None, Requires = ["restore", "build"] };

        Assert.Equal(
            "test · Pruebas · dotnet test --no-build · requires restore and build · NO ISOLATION: the code the agents write runs with your own rights, in the working copy",
            OrchestratorCodedText.StepLine(step, English));
        Assert.StartsWith("test · Pruebas · dotnet test --no-build · requiere restore y build · SIN AISLAMIENTO", OrchestratorCodedText.StepLine(step, Spanish), StringComparison.Ordinal);
    }
    // ---- the rest of the screen texts of the orchestration (restos de WP-14A (e)) ----

    private static string Say(UserText? text, string anchored, IUiTextProvider provider) => OrchestratorCodedText.Painted(text, anchored, provider)!;

    [Fact]
    public void With_the_interface_in_english_the_reasons_of_the_workers_and_of_a_retry_come_out_in_english_and_in_spanish_as_they_were_written()
    {
        var failure = UserText.Of(OrchestrationUserTexts.WorkerVerificationFailed, "build, test");
        var retry = UserText.Of(OrchestrationUserTexts.TaskRetry, 2, 3, failure);

        Assert.Equal("Retry 2 of 3 after: The verification failed in: build, test", Say(retry, "Reintento 2 de 3 tras: La verificación falló en: build, test", English));
        Assert.Equal("Reintento 2 de 3 tras: La verificación falló en: build, test", Say(retry, "anchored", Spanish));
        // A reason that carries the words of an agent has no code: it is shown as it was anchored.
        Assert.Equal("Completada: terminé", Say(null, "Completada: terminé", English));
        Assert.Equal(
            "Retry 1 of 3 after: Completada a medias",
            Say(UserText.Of(OrchestrationUserTexts.TaskRetry, 1, 3, "Completada a medias"), "x", English));
    }

    [Fact]
    public void A_limit_of_use_is_told_with_its_instant_in_the_culture_of_the_language()
    {
        var at = new DateTimeOffset(2026, 11, 12, 18, 30, 0, TimeSpan.Zero);
        var spanish = Say(UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, at), "x", Spanish);
        var english = Say(UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, at), "x", English);

        Assert.StartsWith("Límite de uso del proveedor hasta ", spanish, StringComparison.Ordinal);
        Assert.StartsWith("Usage limit of the provider until ", english, StringComparison.Ordinal);
        Assert.DoesNotContain("T18:30", spanish, StringComparison.Ordinal);
        Assert.NotEqual(spanish[(spanish.IndexOf("hasta", StringComparison.Ordinal) + 6)..], english[(english.IndexOf("until", StringComparison.Ordinal) + 6)..]);
    }

    [Fact]
    public void The_delivery_says_why_it_does_not_deliver_in_the_language_in_use_with_the_pieces_nested()
    {
        var reason = UserText.Of(OrchestrationUserTexts.DeliveryVerdictNotApproved, UserText.Of(OrchestrationUserTexts.DeliveryDecisionRejected));
        Assert.StartsWith("The judge's verdict is \"rejected\".", Say(reason, "x", English), StringComparison.Ordinal);
        Assert.StartsWith("El veredicto del juez es «rechazado».", Say(reason, "x", Spanish), StringComparison.Ordinal);

        var inconsistent = UserText.Of(
            OrchestrationUserTexts.DeliveryRevisionInconsistent,
            "debate-1",
            "V1",
            UserText.Of(OrchestrationUserTexts.DeliveryInconsistentCopyOtherCommit, "aaaaaaaaaa", "bbbbbbbbbb"));
        var english = Say(inconsistent, "x", English);
        Assert.Contains("(its copy is of saved version (commit) aaaaaaaaaa, not of the integration bbbbbbbbbb of the run)", english, StringComparison.Ordinal);
        Assert.Contains("debate-1", english, StringComparison.Ordinal);

        var delivered = OrchestrationUserTexts.Sentences([
            UserText.Of(OrchestrationUserTexts.DeliveryDelivered, "main", "abc", "agentstudio/run-1"),
            UserText.Of(OrchestrationUserTexts.DeliveryUnrecorded, "disk full")]);
        Assert.Equal("Delivered: main is at abc and includes agentstudio/run-1. Warning: the delivery could not be recorded in the decision log (disk full).", Say(delivered, "x", English));
    }

    [Fact]
    public void The_notice_of_the_search_in_the_history_is_painted_in_the_language_in_use_from_the_sentence_it_was_saved_with()
    {
        var sentences = new[]
        {
            AgentStudio.Application.Semantic.ProjectHistoryTool.NotThePlannerReason,
            AgentStudio.Application.Semantic.ProjectHistoryTool.DisabledReason,
            AgentStudio.Application.Semantic.ProjectHistoryTool.NoMemoryReason,
            AgentStudio.Application.Semantic.ProjectHistoryTool.NotReadOnlyReason,
            AgentStudio.Application.Semantic.ProjectHistoryTool.EmptyReason,
            AgentStudio.Application.Semantic.ProjectHistoryTool.NoMcpReason(ProviderId.CodexCli),
            AgentStudio.Application.Semantic.ProjectHistoryTool.FailedReason("the disk is full."),
        };
        foreach (var sentence in sentences)
        {
            var text = LoggedTexts.HistoryToolNotice(sentence);
            Assert.NotNull(text);
            Assert.Contains("history", Say(text, sentence, English), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Say(text, sentence, English), Say(text, sentence, Spanish));
        }

        Assert.Equal(
            "The orchestrator did not have the search in the history. Codex does not take outside tools.",
            Say(LoggedTexts.HistoryToolNotice(AgentStudio.Application.Semantic.ProjectHistoryTool.NoMcpReason(ProviderId.CodexCli)), "x", English));
        Assert.Equal(
            UserText.Of(OrchestrationUserTexts.HistoryFailed, "the disk is full"),
            LoggedTexts.HistoryToolNotice(AgentStudio.Application.Semantic.ProjectHistoryTool.FailedReason("the disk is full.")));
        Assert.Null(LoggedTexts.HistoryToolNotice("otra cosa"));
    }

    [Fact]
    public void Why_a_run_is_not_resumed_by_itself_and_why_a_log_cannot_be_replayed_are_painted_in_the_language_in_use()
    {
        Assert.Equal("You paused it: it never resumes by itself.", Say(LoggedTexts.ResumeReason("La pausaste: nunca se reanuda sola.")!, "x", English));
        Assert.Equal("It could not be resumed: boom", Say(LoggedTexts.ResumeReason("No se pudo reanudar: boom")!, "x", English));

        var notice = UserText.Of(
            OrchestrationUserTexts.InspectLogNotReplayable,
            OrchestrationUserTexts.Sentences([UserText.Of(OrchestrationUserTexts.InspectProblemUnknownTasks), UserText.Of(OrchestrationUserTexts.InspectProblemDetail, "An entry repeats a sequence.")]));
        Assert.Equal(
            "The log of this run cannot be replayed in full (The log names tasks that are not in the plan of the run. An entry repeats a sequence.): it is shown up to that point.",
            Say(notice, "x", English));
    }

    [Fact]
    public void A_cancellation_without_a_reason_and_the_one_of_the_monitor_are_painted_as_pieces()
    {
        Assert.Equal("Cancelled by you: no reason given", Say(UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, UserText.Of(OrchestrationUserTexts.NoReason)), "x", English));
        Assert.Equal("Cancelada por ti: desde el monitor", Say(UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, UserText.Of(OrchestrationUserTexts.CancelledFromMonitor)), "x", Spanish));
        Assert.Equal("Cancelled by you: me aburrí", Say(UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, "me aburrí"), "x", English));
    }

    [Fact]
    public void The_phase_a_delivery_names_is_the_word_the_desktop_already_has_in_both_languages()
    {
        foreach (var phase in Enum.GetValues<AgentStudio.Application.Orchestration.RunPhase>())
        {
            var piece = OrchestrationUserTexts.Phase(phase);
            Assert.True(Spanish.Has(piece.ResourceKey), piece.Code + " (es)");
            Assert.True(English.Has(piece.ResourceKey), piece.Code + " (en)");
        }

        var reason = UserText.Of(OrchestrationUserTexts.DeliveryRunNotSucceeded, "R1", OrchestrationUserTexts.Phase(AgentStudio.Application.Orchestration.RunPhase.Failed));
        Assert.Equal("La ejecución R1 acabó como fallida. Solo se entrega una que acabó bien.", Say(reason, "x", Spanish));
        Assert.Equal("Run R1 ended as failed. Only a run that ended well is delivered.", Say(reason, "x", English));
        Assert.Equal(
            "El registro de decisiones no está íntegro: no se entrega nada.",
            Say(UserText.Of(OrchestrationUserTexts.DeliveryLogNotIntactUnknown), "x", Spanish));
    }

    [Fact]
    public void What_the_planner_could_not_do_says_what_is_missing_or_what_changed_in_both_languages()
    {
        var figures = UserText.Of(OrchestrationUserTexts.PlannerMemoryTooSmall, OrchestrationUserTexts.MemoryTooSmall(new AgentStudio.Application.Knowledge.ContextBudgetTooSmallException(5000, 4000)));
        var unknown = UserText.Of(OrchestrationUserTexts.PlannerMemoryTooSmall, OrchestrationUserTexts.MemoryTooSmall(new AgentStudio.Application.Knowledge.ContextBudgetTooSmallException("technical message")));

        Assert.Matches("its requirements take 5,?000 characters and the context only holds 4,?000", Say(figures, "x", English));
        Assert.Contains("caracteres", Say(figures, "x", Spanish), StringComparison.Ordinal);
        Assert.StartsWith("The orchestrator does not start: the memory and the requirements do not fit in its context.", Say(unknown, "x", English), StringComparison.Ordinal);
        Assert.DoesNotContain("technical message", Say(unknown, "x", English), StringComparison.Ordinal);
        Assert.EndsWith("HEAD moved.", Say(UserText.Of(OrchestrationUserTexts.PlannerRepositoryChanged, "HEAD moved."), "x", English), StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_that_changed_and_the_memory_that_does_not_fit_are_painted_with_their_pieces_nested()
    {
        var steps = UserText.Of(OrchestrationUserTexts.StepsMany, UserText.Of(OrchestrationUserTexts.ListAnd, "build", "test"));
        var changed = UserText.Of(OrchestrationUserTexts.WorkerRepositoryChanged, UserText.Of(OrchestrationUserTexts.RepositoryDuringStepsStopped, steps), "HEAD moved.");
        Assert.Equal("Your repository changed during steps build and test, which were stopped before they finished: HEAD moved.", Say(changed, "x", English));
        Assert.Equal("Tu repositorio cambió durante los pasos build y test, que se detuvieron antes de terminar: HEAD moved.", Say(changed, "x", Spanish));

        var memory = UserText.Of(OrchestrationUserTexts.WorkerMemoryUnverifiedEntry, "T1", 7L, "hash roto", "ejecutor");
        Assert.Contains("(entry 7: hash roto)", Say(memory, "x", English), StringComparison.Ordinal);
    }

    [Fact]
    public void The_failures_of_the_system_in_the_review_of_the_order_and_in_the_chat_come_out_in_english()
    {
        Assert.Equal("The orchestrator gave no answer.", Say(UserText.Of(OrchestrationUserTexts.ChatNoAnswer), "x", English));
        Assert.Equal("La respuesta del orquestador está vacía.", Say(UserText.Of(OrchestrationUserTexts.ChatAnswerNull), "x", Spanish));
        Assert.Equal("The orchestrator gave no notes of order.", Say(UserText.Of(OrchestrationUserTexts.OrderNoNotes), "x", English));
        Assert.Equal("The corrected plan could not be read: no tasks", Say(UserText.Of(OrchestrationUserTexts.PlannerCorrectedUnreadable, "no tasks"), "x", English));
    }
}
