using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator.Monitor;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Questions;

/// <summary>
/// F13-14 (docs/17 D-28 to D-33): the question of an agent as the window and the card show it. The text of the agent is framed as
/// said by the agent; each option has its cost with its origin, or «desconocido» or «no disponible» with the reason; answering
/// sends one option, or the person's own text when the agent allows it; and «Decidir más tarde» never cancels anything.
/// </summary>
public sealed class QuestionViewModelTests
{
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static QuestionViewModel New(PendingQuestion? question = null, Func<QuestionAnswer, Task>? answer = null, IUiTextProvider? texts = null) =>
        new(question ?? Pending(estimates: [Estimate("a", 9_000, 15_000, 6), Estimate("b", null, null, 2)]), answer ?? (_ => Task.CompletedTask), "user", texts);

    // ---- the cost of each option (D-32) ----

    [Fact]
    public void An_option_with_an_estimate_says_the_interval_of_total_tokens_its_origin_and_its_references()
    {
        var model = New();

        Assert.Equal("~9.000 a 15.000 tokens · estimado por Agent Studio · 6 referencias", model.Options[0].Cost.Text);
        Assert.Equal(QuestionCostKind.Estimated, model.Options[0].Cost.Kind);
        Assert.Equal("p25 of 6 similar attempts", model.Options[0].Cost.Detail);
    }

    [Fact]
    public void An_option_with_fewer_than_three_references_is_unknown_and_never_gets_a_number()
    {
        var model = New();

        Assert.Equal("Coste desconocido: hay menos de tres intentos parecidos (2)", model.Options[1].Cost.Text);
        Assert.Equal(QuestionCostKind.Unknown, model.Options[1].Cost.Kind);
        Assert.DoesNotContain("~", model.Options[1].Cost.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tokens", model.Options[1].Cost.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enough_references_without_a_total_are_not_available_with_the_reason()
    {
        var cost = QuestionCosts.Of(Estimate("a", null, null, 5));

        Assert.Equal(QuestionCostKind.Unavailable, cost.Kind);
        Assert.Equal("Coste no disponible: los intentos parecidos no dan un total de tokens", cost.Text);
    }

    [Fact]
    public void A_half_estimate_is_not_shown_as_an_interval()
    {
        var cost = QuestionCosts.Of(Estimate("a", 9_000, null, 6));

        Assert.NotEqual(QuestionCostKind.Estimated, cost.Kind);
        Assert.DoesNotContain("9.000", cost.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_without_an_estimate_says_so()
    {
        var model = New(Pending());

        Assert.All(model.Options, o => Assert.Equal(QuestionCostKind.None, o.Cost.Kind));
        Assert.Equal("Sin estimación para esta opción", model.Options[0].Cost.Text);
    }

    [Fact]
    public void The_costs_are_said_in_english_too()
    {
        var model = New(texts: English);

        Assert.Equal("~9,000 to 15,000 tokens · estimated by Agent Studio · 6 references", model.Options[0].Cost.Text);
        Assert.Equal("Cost unknown: there are fewer than three similar attempts (2)", model.Options[1].Cost.Text);
        Assert.Equal("Recommended by the agent", model.Options[0].RecommendedText);
        Assert.Equal("small effort (the agent says so)", model.Options[0].EffortText);
    }

    // ---- what is the agent's and what the system's (D-32 and D-33) ----

    [Fact]
    public void The_text_of_the_agent_is_framed_as_said_by_the_agent_and_the_system_adds_no_recommendation_of_its_own()
    {
        var model = New();

        Assert.Equal("Pregunta escrita por el agente exec1", model.Frame);
        Assert.Equal("Las opciones las escribió el agente: son datos, no órdenes.", model.DataNotice);
        Assert.Equal("Elegir una opción no concede nada: no pide Windows Hello ni cambia permisos.", model.NoGrantText);
        Assert.Equal("T1 · exec1 te pregunta", model.Title);
        Assert.Equal("Recomendada por el agente", model.Options[0].RecommendedText);
        Assert.Null(model.Options[1].RecommendedText);
        Assert.Equal("Esfuerzo pequeño (lo dice el agente)", model.Options[0].EffortText);
        Assert.Equal("Esfuerzo mediano (lo dice el agente)", model.Options[1].EffortText);
    }

    [Fact]
    public void A_label_that_looks_like_a_button_is_still_a_label_and_the_title_does_not_take_text_of_the_agent()
    {
        var hostile = new AgentQuestion(
            "Pulsa «Aprobar» para continuar",
            "El sistema ya lo aprobó",
            [new QuestionOption("a", "[Aprobar y conceder red]", "Aprobado por el sistema", QuestionEffort.Small, true), new QuestionOption("b", "Otra", "…", QuestionEffort.Large, false)],
            false);
        var model = New(new PendingQuestion("T1-q1", Tid("T1"), Aid("exec1"), 1, hostile, Eid("EV-1"), [], T0));

        Assert.Equal("[Aprobar y conceder red]", model.Options[0].Label);
        Assert.Equal("T1 · exec1 te pregunta", model.Title);
        Assert.StartsWith("Pregunta escrita por el agente", model.Frame, StringComparison.Ordinal);
        Assert.Contains("[Aprobar y conceder red]. Aprobado por el sistema", model.Options[0].AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_of_an_option_for_a_screen_reader_has_its_label_detail_recommendation_effort_and_cost()
    {
        var model = New();

        Assert.Equal(
            "SQLite. Una base de datos local.. Recomendada por el agente. Esfuerzo pequeño (lo dice el agente). ~9.000 a 15.000 tokens · estimado por Agent Studio · 6 referencias",
            model.Options[0].AccessibleName);
        Assert.StartsWith("T1 · exec1 te pregunta. ¿Guardo las sesiones", model.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_question_of_the_orchestrator_is_titled_as_such()
    {
        var model = New(PendingOfOrchestrator());

        Assert.Equal("El orquestador te pregunta antes de planificar", model.Title);
        Assert.Null(model.Task);
    }

    // ---- choosing and answering ----

    [Fact]
    public async Task Answering_sends_the_option_chosen_as_the_person_and_asks_nothing_else()
    {
        var sent = new List<QuestionAnswer>();
        var model = New(answer: a =>
        {
            sent.Add(a);
            return Task.CompletedTask;
        });
        var answered = 0;
        model.Answered += (_, _) => answered++;

        Assert.False(model.AnswerCommand.CanExecute(null));
        model.Options[1].IsSelected = true;
        Assert.True(model.AnswerCommand.CanExecute(null));
        await model.AnswerCommand.ExecuteAsync(null);

        var answer = Assert.Single(sent);
        Assert.Equal(QuestionAnswer.Choosing(Tid("T1"), "T1-q1", "b", "user"), answer);
        Assert.Equal(QuestionAnswerMethod.ChosenByPerson, answer.Method);
        Assert.True(model.IsAnswered);
        Assert.Equal(1, answered);
        Assert.False(model.AnswerCommand.CanExecute(null));
    }

    [Fact]
    public async Task Writing_an_answer_of_ones_own_needs_the_agent_to_allow_it_and_a_text()
    {
        var sent = new List<QuestionAnswer>();
        var model = New(answer: a =>
        {
            sent.Add(a);
            return Task.CompletedTask;
        });

        model.IsOtherSelected = true;
        Assert.False(model.AnswerCommand.CanExecute(null));
        model.OtherText = "   ";
        Assert.False(model.AnswerCommand.CanExecute(null));
        model.OtherText = new string('x', QuestionAnswer.MaxOtherTextLength + 1);
        Assert.False(model.AnswerCommand.CanExecute(null));
        model.OtherText = "  Mejor un fichero SQLite por proyecto.  ";
        Assert.True(model.AnswerCommand.CanExecute(null));
        await model.AnswerCommand.ExecuteAsync(null);

        Assert.Equal(QuestionAnswer.Writing(Tid("T1"), "T1-q1", "Mejor un fichero SQLite por proyecto.", "user"), Assert.Single(sent));
    }

    [Fact]
    public void A_question_that_does_not_allow_other_answers_never_sends_the_text_of_the_person()
    {
        var model = New(Pending(allowOther: false));

        Assert.False(model.AllowOther);
        model.IsOtherSelected = true;
        model.OtherText = "Otra cosa";

        Assert.False(model.AnswerCommand.CanExecute(null));
    }

    [Fact]
    public void Choosing_one_leaves_the_other_and_choosing_the_own_text_leaves_the_options()
    {
        var model = New();

        model.Options[0].IsSelected = true;
        model.Options[1].IsSelected = true;
        Assert.False(model.Options[0].IsSelected);
        Assert.Same(model.Options[1], model.SelectedOption);

        model.IsOtherSelected = true;
        Assert.Null(model.SelectedOption);
        Assert.All(model.Options, o => Assert.False(o.IsSelected));

        model.Options[0].IsSelected = true;
        Assert.False(model.IsOtherSelected);
    }

    [Fact]
    public async Task Nothing_is_chosen_for_the_person_so_without_a_choice_it_cannot_be_answered_even_if_the_agent_recommends_one()
    {
        var sent = 0;
        var model = New(answer: _ =>
        {
            sent++;
            return Task.CompletedTask;
        });

        Assert.True(model.Options[0].IsRecommended);
        Assert.All(model.Options, o => Assert.False(o.IsSelected));
        Assert.Null(model.SelectedOption);
        Assert.False(model.IsOtherSelected);
        Assert.False(model.AnswerCommand.CanExecute(null));
        await model.AnswerCommand.ExecuteAsync(null);

        Assert.Equal(0, sent);
        Assert.False(model.IsAnswered);
    }

    [Fact]
    public void The_window_that_opens_by_itself_does_not_take_the_focus_and_the_one_the_person_asks_for_does()
    {
        var (_, host, _, _, monitor, ui, run) = Prompting();
        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Equal([false], host.Activations);

        monitor.OpenQuestionCommand.Execute(monitor.PendingQuestions[0]);

        Assert.Equal([false, true], host.Activations);
    }

    [Fact]
    public async Task When_the_engine_refuses_the_answer_the_window_says_why_and_the_person_can_try_again()
    {
        var calls = 0;
        var model = New(answer: _ => ++calls == 1 ? Task.FromException(new InvalidOperationException("La tarea ya no espera esa pregunta.")) : Task.CompletedTask);
        model.Options[0].IsSelected = true;

        await model.AnswerCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo enviar tu respuesta: La tarea ya no espera esa pregunta.", model.ErrorMessage);
        Assert.False(model.IsAnswered);
        Assert.True(model.AnswerCommand.CanExecute(null));

        await model.AnswerCommand.ExecuteAsync(null);

        Assert.Null(model.ErrorMessage);
        Assert.True(model.IsAnswered);
    }

    [Fact]
    public void Deciding_later_only_asks_to_close_the_window_and_sends_nothing()
    {
        var sent = 0;
        var model = New(answer: _ =>
        {
            sent++;
            return Task.CompletedTask;
        });
        var deferred = 0;
        model.DeferRequested += (_, _) => deferred++;

        model.DeferCommand.Execute(null);

        Assert.Equal(1, deferred);
        Assert.Equal(0, sent);
        Assert.False(model.IsAnswered);
    }

    // ---- the questions of the orchestrator before it plans ----

    [Fact]
    public async Task The_questions_of_the_orchestrator_are_answered_in_one_round_and_come_back_in_order()
    {
        var run = new RunId("20260101-abc123");
        var set = new PlannerQuestionSet(run, [PendingOfOrchestrator(1), PendingOfOrchestrator(2)]);

        Assert.Equal(2, set.Remaining);
        set.Questions[1].Options[1].IsSelected = true;
        await set.Questions[1].AnswerCommand.ExecuteAsync(null);
        Assert.False(set.Answers.IsCompleted);
        Assert.Equal(1, set.Remaining);

        set.Questions[0].IsOtherSelected = true;
        set.Questions[0].OtherText = "Sin base de datos.";
        await set.Questions[0].AnswerCommand.ExecuteAsync(null);

        var answers = await set.Answers;
        Assert.Equal(["plan-q1", "plan-q2"], answers.Select(a => a.Question.Id));
        Assert.All(answers, a => Assert.Equal(run, a.Run));
        Assert.All(answers, a => Assert.Null(a.Answer.Task));
        Assert.Equal("Sin base de datos.", answers[0].Answer.OtherText);
        Assert.Equal("b", answers[1].Answer.OptionId);
    }

    [Fact]
    public void A_set_without_questions_is_not_a_set()
    {
        Assert.Throws<ArgumentException>(() => new PlannerQuestionSet(new RunId("20260101-abc123"), []));
    }

    // ---- the monitor, the cards and the run ----

    [Fact]
    public async Task A_question_is_a_card_of_the_monitor_and_answering_it_goes_to_the_run()
    {
        var inner = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, View("T1", "exec1", WorkTaskStatus.Running)));
        var run = new AnsweringRun(inner);
        var ui = new QueueSynchronizationContext();
        var monitor = new RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), ui);
        monitor.Attach(run);
        var raised = new List<QuestionViewModel>();
        monitor.QuestionRaised += (_, q) => raised.Add(q);
        var question = Pending(estimates: [Estimate("a", 9_000, 15_000, 6)]);

        inner.Emit(new QuestionPendingEvent(T0, question));
        Pump(ui, 1);

        var card = Assert.Single(monitor.PendingQuestions);
        Assert.Same(card, Assert.Single(raised));
        Assert.True(monitor.HasPendingQuestions);
        card.Options[0].IsSelected = true;
        await card.AnswerCommand.ExecuteAsync(null);

        Assert.Equal(QuestionAnswer.Choosing(Tid("T1"), "T1-q1", "a", "user"), Assert.Single(run.Answers));
        Assert.Equal("user", RunMonitorViewModel.QuestionDecidedBy);

        // The card goes when the run says the question was answered.
        var resolved = new List<QuestionViewModel>();
        monitor.QuestionResolved += (_, q) => resolved.Add(q);
        inner.Emit(new QuestionAnsweredEvent(T0, question.Id, question.Task, QuestionAnswerMethod.ChosenByPerson));
        Pump(ui, 1);
        Assert.Empty(monitor.PendingQuestions);
        Assert.Same(card, Assert.Single(resolved));
        Assert.Contains(monitor.Activity, a => a.Text.Contains("Respondiste", StringComparison.Ordinal));
    }

    [Fact]
    public void A_question_that_was_waiting_when_the_run_is_attached_is_shown_again_after_a_restart()
    {
        var waiting = Pending();
        var ui = new QueueSynchronizationContext();
        var monitor = new RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), ui);
        var raised = 0;
        monitor.QuestionRaised += (_, _) => raised++;

        monitor.Attach(new FakeRun(FakeRun.SnapshotOf(RunPhase.AwaitingHuman, View("T1", "exec1", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason, waiting))));

        Assert.Single(monitor.PendingQuestions);
        Assert.Equal(1, raised);
        Assert.True(monitor.Tasks[0].HasQuestion);
        Assert.Equal("Espera tu respuesta", monitor.Tasks[0].StatusLabel);
        Assert.False(monitor.Tasks[0].IsBlocked);
    }

    [Fact]
    public void A_service_built_after_the_run_was_attached_still_opens_the_window_of_a_question_that_was_waiting()
    {
        var monitor = new RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), new QueueSynchronizationContext());
        monitor.Attach(new FakeRun(FakeRun.SnapshotOf(RunPhase.AwaitingHuman, View("T1", "exec1", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason, Pending()))));
        var host = new RecordingHost();

        using var service = new QuestionPromptService(monitor, host);
        var attention = new List<QuestionAttentionEventArgs>();
        service.AttentionRequested += (_, e) => attention.Add(e);

        Assert.Equal(["T1-q1"], host.Shown);

        // It is wired like any other: deciding later closes the window and keeps the card.
        monitor.PendingQuestions[0].DeferCommand.Execute(null);
        Assert.Equal(["T1-q1"], host.Closed);
        Assert.Single(monitor.PendingQuestions);
    }

    [Fact]
    public void The_task_that_waits_for_an_answer_is_not_a_block_to_retry_in_any_language_of_its_label()
    {
        var (monitor, _, _, _) = Open(View("T1", "exec1", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason));

        Assert.True(monitor.Tasks[0].AwaitingAnswer);
        Assert.False(monitor.Tasks[0].IsBlocked);
        Assert.Equal("Espera tu respuesta", BlockReasonText.Label(BlockReasonText.AwaitingAnswerReason, TimeZoneInfo.Utc));
        Assert.Equal("Waiting for your answer", English.Get("Text_question_awaiting_answer"));
    }

    [Fact]
    public void A_run_read_back_from_its_log_takes_no_answers()
    {
        var run = new AnsweringRun(new FakeRun(FakeRun.SnapshotOf(RunPhase.AwaitingHuman, View("T1", "exec1", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason, Pending()))));
        var monitor = new RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), new QueueSynchronizationContext());

        monitor.Inspect(run, new AgentStudio.Application.Projects.ProjectContext("p1", "C:/p"));

        Assert.Empty(monitor.PendingQuestions);
    }

    // ---- the windows and the tray ----

    [Fact]
    public void The_window_opens_by_itself_when_the_app_is_in_view_and_the_tray_is_not_asked()
    {
        var (service, host, _, attention, monitor, ui, run) = Prompting();

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Equal(["T1-q1"], host.Shown);
        Assert.Empty(attention);
        service.Dispose();
        _ = monitor;
    }

    [Fact]
    public void Out_of_view_the_tray_says_an_agent_asks_something_with_no_data_of_the_project_and_the_window_waits()
    {
        var (_, host, _, attention, _, ui, run) = Prompting(inView: false);

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Empty(host.Shown);
        var notice = Assert.Single(attention);
        Assert.Equal("Un agente te pregunta algo", notice.Notice);
        Assert.DoesNotContain("SQLite", notice.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("T1", notice.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("exec1", notice.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Deciding_later_closes_the_window_keeps_the_card_and_the_card_opens_it_again()
    {
        var (_, host, _, _, monitor, ui, run) = Prompting();
        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);
        var card = monitor.PendingQuestions[0];

        card.DeferCommand.Execute(null);

        Assert.Equal(["T1-q1"], host.Closed);
        Assert.Single(monitor.PendingQuestions);
        Assert.False(card.IsAnswered);

        monitor.OpenQuestionCommand.Execute(card);

        Assert.Equal(["T1-q1", "T1-q1"], host.Shown);
    }

    [Fact]
    public void A_question_resolved_by_the_run_closes_its_window()
    {
        var (_, host, _, _, monitor, ui, run) = Prompting();
        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        run.Emit(new QuestionAnsweredEvent(T0, "T1-q1", Tid("T1"), QuestionAnswerMethod.RecommendedByConsent));
        Pump(ui, 1);

        Assert.Equal(["T1-q1"], host.Closed);
        Assert.Empty(monitor.PendingQuestions);
    }

    [Fact]
    public void With_the_interface_locked_the_window_is_withdrawn_and_comes_back_when_it_unlocks()
    {
        var (_, host, interfaceLock, attention, monitor, ui, run) = Prompting();
        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);
        Assert.Equal(["T1-q1"], host.Shown);

        interfaceLock.Lock();

        Assert.Equal(1, host.Withdrawals);

        // A question that comes while it is locked does not show: the tray is told, and the window waits for the unlock.
        run.Emit(new QuestionPendingEvent(T0, Pending("T2", "exec2")));
        Pump(ui, 1);
        Assert.Equal(["T1-q1"], host.Shown);
        Assert.Single(attention);

        interfaceLock.Unlock();

        Assert.Equal(1, host.Restorations);
        Assert.Equal(["T1-q1", "T1-q1", "T2-q1"], host.Shown);
        Assert.Equal(2, monitor.PendingQuestions.Count);
    }

    [Fact]
    public void A_deferred_question_does_not_reopen_by_itself_when_the_interface_unlocks()
    {
        var (_, host, interfaceLock, _, monitor, ui, run) = Prompting();
        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);
        monitor.PendingQuestions[0].DeferCommand.Execute(null);
        host.Shown.Clear();

        interfaceLock.Lock();
        interfaceLock.Unlock();

        Assert.Empty(host.Shown);
    }

    private static (QuestionPromptService Service, RecordingHost Host, FakeInterfaceLock Lock, List<QuestionAttentionEventArgs> Attention, RunMonitorViewModel Monitor, QueueSynchronizationContext Ui, FakeRun Run) Prompting(bool inView = true)
    {
        var (monitor, run, ui, _) = Open(View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2", WorkTaskStatus.Running));
        var host = new RecordingHost { IsAppInView = inView };
        var interfaceLock = new FakeInterfaceLock();
        var service = new QuestionPromptService(monitor, host, interfaceLock);
        var attention = new List<QuestionAttentionEventArgs>();
        service.AttentionRequested += (_, e) => attention.Add(e);
        return (service, host, interfaceLock, attention, monitor, ui, run);
    }

    private sealed class RecordingHost : IQuestionWindowHost
    {
        public bool IsAppInView { get; set; } = true;

        public List<string> Shown { get; } = [];

        public List<string> Closed { get; } = [];

        public int Withdrawals { get; private set; }

        public int Restorations { get; private set; }

        public List<bool> Activations { get; } = [];

        public void Show(QuestionViewModel question, bool activate = true)
        {
            Shown.Add(question.Id);
            Activations.Add(activate);
        }

        public void Close(QuestionViewModel question) => Closed.Add(question.Id);

        public void Withdraw() => Withdrawals++;

        public void Restore() => Restorations++;
    }

    private sealed class FakeInterfaceLock : IInterfaceLock
    {
        public event EventHandler? Changed;

        public bool IsLocked { get; private set; }

        public void Lock()
        {
            IsLocked = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Unlock()
        {
            IsLocked = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<CancellationToken> WaitUntilUnlockedAsync(ApprovalRequest question, CancellationToken cancellationToken) => Task.FromResult(CancellationToken.None);
    }
}
