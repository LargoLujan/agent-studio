using System.Windows.Automation;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>
/// F13-7 and F13-8 (docs/17 D-14, D-16 and D-17): the monitor implements <see cref="IRunLiveProjection"/>. The state reaches the
/// screens grouped, at most four times a second per node, and each agent has a card with a whole name that announces its changes.
/// </summary>
public sealed class LiveProjectionTests
{
    private static AgentId Agent(string id) => new(id);

    [Fact]
    public void Attaching_publishes_the_state_of_the_snapshot_at_once()
    {
        var (monitor, _, _, _) = Open(View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2"));
        monitor.Roster = [AgentDef("exec1", "Ejecutor 1"), AgentDef("exec2", "Ejecutor 2"), AgentDef("exec3", "Ejecutor 3")];

        // The roster gives the agents without a task: grouped, so it arrives with the next change; the first state is the snapshot's.
        var state = Assert.IsType<RunLiveState>(monitor.Current);

        Assert.Equal("20260101-abc123", state.Run.Value);
        Assert.Equal(RunPhase.Running, state.Phase);
        Assert.Equal(AgentLiveStatus.Working, state.Agents[Agent("exec1")].Status);
        Assert.Equal(AgentLiveStatus.Idle, state.Agents[Agent("exec2")].Status);
        Assert.Equal(WorkTaskStatus.Running, state.Tasks[Tid("T1")].Status);
        Assert.Equal(Agent("exec1"), state.Tasks[Tid("T1")].Agent);
    }

    [Fact]
    public void Without_a_run_there_is_no_state()
    {
        var monitor = new RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), new QueueSynchronizationContext());

        Assert.Null(monitor.Current);
    }

    [Fact]
    public void A_burst_of_events_is_one_grouped_change_of_the_nodes_it_touched()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2", WorkTaskStatus.Running));
        var changes = new List<RunLiveChangedEventArgs>();
        monitor.Changed += (_, e) => changes.Add(e);

        run.Emit([.. Enumerable.Range(1, 100).Select(i => (OrchestrationEvent)Step("T1", i * 1000))]);
        Pump(ui, 100);

        // A hundred events were applied, and nobody was told yet: the repaint is the one of the group.
        Assert.Empty(changes);

        Settle(time, ui);

        var change = Assert.Single(changes);
        Assert.Equal(["T1", "exec1"], change.ChangedNodes.Order(StringComparer.Ordinal));
        Assert.Equal(100_000, change.State.Agents[Agent("exec1")].Context!.UsedTokens.Value);
        Assert.Same(change.State, monitor.Current);
    }

    [Fact]
    public void A_node_changes_at_most_four_times_a_second()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        var changes = new List<(DateTimeOffset At, RunLiveChangedEventArgs Change)>();
        monitor.Changed += (_, e) => changes.Add((time.GetUtcNow(), e));

        // An event every 10 ms for two seconds: 200 events.
        for (var i = 1; i <= 200; i++)
        {
            run.Emit(Step("T1", i * 100));
            Pump(ui, 1);
            time.Advance(TimeSpan.FromMilliseconds(10));
            ui.RunAll();
        }

        Assert.All(changes, c => Assert.Contains("T1", c.Change.ChangedNodes));
        Assert.InRange(changes.Count, 6, 9);
        for (var i = 1; i < changes.Count; i++)
        {
            Assert.True(changes[i].At - changes[i - 1].At >= LiveProjection.MinimumInterval, "Two changes of one node came closer than 250 ms.");
        }

        Assert.True(changes.Select(c => c.Change.State.Generation).SequenceEqual(changes.Select(c => c.Change.State.Generation).Order()), "The generation only grows.");
    }

    [Fact]
    public void Each_state_of_an_agent_comes_from_what_its_tasks_say()
    {
        var until = T0.AddHours(4);
        var (monitor, run, ui, time) = Open(
            View("T1", "working", WorkTaskStatus.Running),
            View("T2", "queued"),
            View("T3", "asking", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason),
            View("T4", "approving", WorkTaskStatus.AwaitingApproval),
            View("T5", "quota", WorkTaskStatus.Blocked, $"RateLimitedUntil({until:O})"),
            View("T6", "blocked", WorkTaskStatus.Blocked, "ProviderUnavailable"),
            View("T7", "done", WorkTaskStatus.Completed));
        monitor.Roster = [AgentDef("idle", "Libre")];
        run.Emit(new TaskQueuedEvent(T0, Tid("T2"), ProviderId.CodexCli));
        Pump(ui, 1);
        Settle(time, ui);

        var agents = monitor.Current!.Agents;

        Assert.Equal(AgentLiveStatus.Working, agents[Agent("working")].Status);
        Assert.Equal(Tid("T1"), agents[Agent("working")].Task);
        Assert.Equal(AgentLiveStatus.Queued, agents[Agent("queued")].Status);
        Assert.Equal(AgentLiveStatus.Asking, agents[Agent("asking")].Status);
        Assert.Equal(AgentLiveStatus.AwaitingApproval, agents[Agent("approving")].Status);
        Assert.Equal(AgentLiveStatus.WaitingForQuota, agents[Agent("quota")].Status);
        Assert.Equal<DateTimeOffset?>(until, agents[Agent("quota")].WaitingUntil);
        Assert.Equal(AgentLiveStatus.Blocked, agents[Agent("blocked")].Status);
        Assert.Equal("Bloqueada · el programa del agente no está disponible", agents[Agent("blocked")].BlockReason);
        Assert.Equal(AgentLiveStatus.Finished, agents[Agent("done")].Status);
        Assert.Equal(AgentLiveStatus.Idle, agents[Agent("idle")].Status);
        Assert.Null(agents[Agent("idle")].Task);

        // The node of the task says why it is blocked as the monitor says it, not only «Bloqueada».
        Assert.Equal("Bloqueada · el programa del agente no está disponible", monitor.Current.Tasks[Tid("T6")].BlockReason);
        Assert.Equal("Espera tu respuesta", monitor.Current.Tasks[Tid("T3")].BlockReason);
    }

    [Fact]
    public void The_counter_of_the_top_bar_says_the_states_that_have_an_agent()
    {
        var (monitor, _, _, _) = Open(
            View("T1", "a", WorkTaskStatus.Running),
            View("T2", "b", WorkTaskStatus.Running),
            View("T3", "c", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason),
            View("T4", "d", WorkTaskStatus.Blocked, "ProviderUnavailable"),
            View("T5", "e", WorkTaskStatus.Completed));

        Assert.Equal("Agentes: 2 trabajando · 1 te preguntan · 1 bloqueados", AgentLiveTexts.Counter(monitor.Current!));
    }

    [Fact]
    public void A_task_that_starts_pulses_the_dependencies_that_are_met()
    {
        var (monitor, run, ui, time) = Open(View("T1", "a", WorkTaskStatus.Completed), View("T2", "b", WorkTaskStatus.Completed), View("T3", "c"));
        monitor.PlanDependencies = new Dictionary<TaskId, IReadOnlyList<TaskId>> { [Tid("T3")] = [Tid("T1"), Tid("T2")] };
        var changes = new List<RunLiveChangedEventArgs>();
        monitor.Changed += (_, e) => changes.Add(e);

        run.Emit(Move("T3", WorkTaskStatus.Pending, WorkTaskStatus.Running));
        Pump(ui, 1);
        Settle(time, ui);

        var change = Assert.Single(changes);
        Assert.Equal([(Tid("T1"), Tid("T3")), (Tid("T2"), Tid("T3"))], change.Pulses);

        // A pulse is for one change only.
        run.Emit(Step("T3", 1000));
        Pump(ui, 1);
        Settle(time, ui);
        Assert.Empty(changes[1].Pulses);
    }

    [Fact]
    public void The_integration_is_marked_while_an_integration_task_runs()
    {
        var (monitor, run, ui, time) = Open(View("T1", "a", WorkTaskStatus.Completed), View("T2", null, WorkTaskStatus.Pending, kind: WorkTaskKind.Integration));
        Assert.False(monitor.Current!.Integrating);

        run.Emit(Move("T2", WorkTaskStatus.Pending, WorkTaskStatus.Running));
        Pump(ui, 1);
        Settle(time, ui);

        Assert.True(monitor.Current!.Integrating);
    }

    [Fact]
    public void A_question_makes_the_agent_ask_and_its_card_announce_it_at_once()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        monitor.Roster = [AgentDef("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "sonnet")];
        var question = Pending();

        run.Emit(Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "Espera tu respuesta"), new QuestionPendingEvent(T0, question));
        Pump(ui, 2);
        Settle(time, ui);

        var card = Assert.Single(monitor.AgentCards);
        Assert.Equal(AgentLiveStatus.Asking, card.Status);
        Assert.Equal(AutomationLiveSetting.Assertive, card.LiveSetting);
        Assert.StartsWith("Ejecutor 1 · Claude Code · sonnet · Te pregunta algo: espera tu respuesta · contexto", card.AccessibleName, StringComparison.Ordinal);
        Assert.Single(monitor.PendingQuestions);
        Assert.True(monitor.Tasks[0].HasQuestion);

        // «Reintentar» would relaunch the task without the answer: it is not offered, and the status says what it waits for.
        Assert.False(monitor.Tasks[0].IsBlocked);
        Assert.True(monitor.Tasks[0].AwaitingAnswer);
        Assert.False(monitor.RetryBlockedCommand.CanExecute(monitor.Tasks[0]));

        run.Emit(new QuestionAnsweredEvent(T0, question.Id, question.Task, QuestionAnswerMethod.ChosenByPerson), Move("T1", WorkTaskStatus.Blocked, WorkTaskStatus.Pending, "respondida"));
        Pump(ui, 2);
        Settle(time, ui);

        Assert.Empty(monitor.PendingQuestions);
        Assert.False(monitor.Tasks[0].HasQuestion);
        Assert.Equal(AutomationLiveSetting.Polite, Assert.Single(monitor.AgentCards).LiveSetting);
    }

    [Fact]
    public void The_cards_follow_the_agents_of_the_roster_and_name_what_the_agent_runs_on()
    {
        var (monitor, _, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        monitor.Roster = [AgentDef("exec1", "Ejecutor 1", ProviderId.CodexCli, "gpt-5"), AgentDef("exec2", "Ejecutor 2")];

        // Setting the roster marks the run: the next group has the agents that have no task.
        Settle(time, ui);

        Assert.Equal(["exec1", "exec2"], monitor.AgentCards.Select(c => c.Id.Value));
        var first = monitor.AgentCards[0];
        Assert.Equal("Codex CLI", first.Program);
        Assert.Equal("gpt-5", first.Model);
        Assert.Equal("Libre", monitor.AgentCards[1].StatusText);
        Assert.Equal("Trabajando en T1", first.StatusText);
    }

    [Fact]
    public void A_card_says_the_context_it_does_not_know_in_words_never_as_a_zero()
    {
        var (monitor, _, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Pending));
        Settle(time, ui);

        var card = Assert.Single(monitor.AgentCards);

        Assert.False(card.ContextBar.HasValue);
        Assert.Null(card.ContextBar.Value);
        Assert.Equal("sin empezar", card.ContextBar.ValueText);
        Assert.Contains("contexto sin empezar", card.AccessibleName, StringComparison.Ordinal);
        Assert.False(card.ConsumptionBar.HasValue);
        Assert.Equal("sin intentos todavía", card.ConsumptionBar.ValueText);
    }
}
