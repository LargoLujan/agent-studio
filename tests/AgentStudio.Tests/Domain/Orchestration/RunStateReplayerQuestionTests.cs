using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

/// <summary>
/// Phase 13 (docs/17 D-29, F13-13): replay learns the questions of the agents. A task waiting for its answer keeps waiting
/// after a restart, and a log the engine would never write (a wait without a question, an answer nobody waited for, a task
/// that goes on with its question open) is a problem of the replay, not a state.
/// </summary>
public sealed class RunStateReplayerQuestionTests
{
    private static LiveRun Asking()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Ask("T1", "q1");
        return run;
    }

    [Fact]
    public void A_task_waiting_for_its_answer_still_waits_for_it_after_replay()
    {
        var run = Asking();

        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.Equal(LiveRun.Signature(run.Graph), LiveRun.Signature(replay.Graph));
        Assert.Equal(WorkTaskStatus.Blocked, replay.Graph[Tid("T1")].Status);
        Assert.Equal("q1", Assert.Single(replay.PendingQuestions, p => p.Key == Tid("T1")).Value);
        Assert.Empty(replay.AnsweredQuestions);
    }

    [Fact]
    public void An_answered_question_is_no_longer_pending_and_its_task_goes_on()
    {
        var run = Asking();
        run.Answer("T1", "q1");
        run.Start("T1");
        run.Complete("T1");

        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.Equal(LiveRun.Signature(run.Graph), LiveRun.Signature(replay.Graph));
        Assert.Empty(replay.PendingQuestions);
        Assert.Equal(["q1"], replay.AnsweredQuestions);
    }

    [Fact]
    public void A_question_announced_before_the_app_stopped_was_never_shown_and_is_forgotten()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        IReadOnlyList<RunEvent> events = [.. run.Events, new QuestionPosed(T0.AddMinutes(5), Tid("T1"), "q1")];

        var replay = run.Replay(events);

        Assert.Empty(replay.Problems);
        Assert.Empty(replay.PendingQuestions);
        Assert.Equal(WorkTaskStatus.Running, replay.Graph[Tid("T1")].Status);
    }

    [Fact]
    public void A_task_that_waits_for_an_answer_without_a_question_does_not_replay()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        IReadOnlyList<RunEvent> events =
        [
            .. run.Events,
            new TaskTransitioned(T0.AddMinutes(5), Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Blocked, "asks", [], RunStateReplayer.AwaitingAnswerBlock),
        ];

        var replay = run.Replay(events);

        Assert.Contains("asked no question", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_that_goes_on_with_its_question_open_does_not_replay()
    {
        var run = Asking();
        IReadOnlyList<RunEvent> events =
        [
            .. run.Events,
            new TaskTransitioned(T0.AddMinutes(5), Tid("T1"), WorkTaskStatus.Blocked, WorkTaskStatus.Pending, "retry", []),
        ];

        var replay = run.Replay(events);

        Assert.Contains("still waiting for its answer", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("T1", "q2")]
    [InlineData("T2", "q1")]
    public void An_answer_to_a_question_nobody_waits_for_does_not_replay(string task, string question)
    {
        var run = Asking();
        IReadOnlyList<RunEvent> events = [.. run.Events, new QuestionResolved(T0.AddMinutes(5), Tid(task), question)];

        var replay = run.Replay(events);

        Assert.Contains("was not waiting", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_question_id_is_unique_in_the_run()
    {
        var run = Asking();
        run.Answer("T1", "q1");
        run.Start("T1");
        IReadOnlyList<RunEvent> events = [.. run.Events, new QuestionPosed(T0.AddMinutes(9), Tid("T1"), "q1")];

        var replay = run.Replay(events);

        Assert.Contains("asked twice", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_question_of_a_task_that_is_not_running_does_not_replay()
    {
        var run = new LiveRun(Plan(), T0);
        IReadOnlyList<RunEvent> events = [new QuestionPosed(T0.AddMinutes(1), Tid("T1"), "q1")];

        var replay = run.Replay(events);

        Assert.Contains("not Running", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_a_task_that_waits_for_its_answer_lets_the_question_go()
    {
        var run = Asking();
        run.Cancel("T1");

        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.Empty(replay.PendingQuestions);
    }

    [Fact]
    public void A_question_of_the_orchestrator_waits_without_any_task()
    {
        var run = new LiveRun(Plan(), T0);
        IReadOnlyList<RunEvent> asked = [new QuestionPosed(T0, null, "plan-q1")];

        var waiting = run.Replay(asked);
        var answered = run.Replay([.. asked, new QuestionResolved(T0.AddMinutes(1), null, "plan-q1")]);

        Assert.Empty(waiting.Problems);
        Assert.Equal(["plan-q1"], waiting.PendingRunQuestions);
        Assert.Empty(answered.Problems);
        Assert.Empty(answered.PendingRunQuestions);
        Assert.Equal(["plan-q1"], answered.AnsweredQuestions);
    }

    [Fact]
    public void A_reader_that_gives_no_block_reason_replays_the_graph_as_before()
    {
        // The dossier of the judge and the audits replay the tasks without the reasons of their blocks: their graph is the same.
        var run = Asking();
        run.Answer("T1", "q1");
        var withoutReasons = run.Events.OfType<TaskTransitioned>().Select(t => t with { Block = null }).ToList<RunEvent>();

        var replay = run.Replay(withoutReasons);

        Assert.Empty(replay.Problems);
        Assert.Equal(LiveRun.Signature(run.Graph), LiveRun.Signature(replay.Graph));
    }
}
