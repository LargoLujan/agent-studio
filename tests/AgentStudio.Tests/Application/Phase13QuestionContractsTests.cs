using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The contract of the questions in the orchestration (docs/17 D-28 to D-32): a question of the orchestrator before it plans has
/// no task, and its answer goes to the next planning; what the window of a question shows of each option is exactly what
/// <c>QuestionAsked</c> records, so it is rebuilt the same after a restart.
/// </summary>
public sealed class Phase13QuestionContractsTests
{
    private static readonly RunId Run = new("20261015-a1b2c3");

    private static readonly OptionEstimateSnapshot EstimateA = new(
        "a",
        MetricSnapshot.WithOrigin(MetricValue<long>.EstimatedBySystem(9000, "p25 of 6 attempts")),
        MetricSnapshot.WithOrigin(MetricValue<long>.EstimatedBySystem(15000, "p75 of 6 attempts")),
        6);

    private static PendingQuestion Question(TaskId? task) => new(
        "q-1",
        task,
        Aid(task is null ? "orquestador" : "ejecutor"),
        1,
        new AgentQuestion(
            "¿Guardo las sesiones en SQLite o en ficheros JSON?",
            "Las dos cumplen RQ-02.",
            [new QuestionOption("a", "SQLite", "Detalle", QuestionEffort.Small, true), new QuestionOption("b", "JSON", "Detalle", QuestionEffort.Medium, false)],
            AllowOther: true),
        Eid("ev-question"),
        [EstimateA],
        T0);

    [Fact]
    public void A_question_of_the_orchestrator_has_no_task_and_its_answer_goes_to_the_next_planning()
    {
        var question = Question(null);
        var answer = QuestionAnswer.Choosing(null, "q-1", "a", "user");
        var proposal = new PlanProposal(Run, null, [], false, AgentRunStatus.Succeeded, null, Eid("ev-raw")) { Questions = [question] };

        var answered = new AnsweredQuestion(Run, question, answer);

        Assert.Null(answer.Task);
        Assert.Null(proposal.Plan);
        Assert.Single(proposal.Questions!);
        Assert.Equal(Run, answered.Run);
        Assert.Equal("a", answered.Answer.OptionId);
        Assert.Equal(3, QuestionRules.MaxPlannerQuestions);
    }

    [Fact]
    public void An_answer_answers_only_its_own_question()
    {
        Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(null), QuestionAnswer.Choosing(null, "q-2", "a", "user")));
        Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(null), QuestionAnswer.Choosing(Tid("T3"), "q-1", "a", "user")));
        Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(Tid("T3")), QuestionAnswer.Choosing(null, "q-1", "a", "user")));
        Assert.Equal(Tid("T3"), new AnsweredQuestion(Run, Question(Tid("T3")), QuestionAnswer.Choosing(Tid("T3"), "q-1", "a", "user")).Answer.Task);
    }

    /// <summary>The window shows the snapshot the log records: the interval of total tokens, with its origin, and its references.</summary>
    [Fact]
    public void What_a_pending_question_says_of_each_option_is_what_the_log_records()
    {
        var question = Question(Tid("T3"));
        var payload = new QuestionAskedPayload(Tid("T3"), 1, question.Agent, question.Id, question.Evidence, HashA, ["a", "b"], "a", question.Estimates) { RunId = Run.Value };
        var stored = DecisionPayloadSerializer.Deserialize<QuestionAskedPayload>(DecisionPayloadSerializer.Serialize(payload));

        Assert.Equal(question.Estimates, stored.Estimates);
        Assert.Same(EstimateA, question.EstimateOf("a"));
        Assert.Null(question.EstimateOf("b"));
        Assert.Equal(MetricOrigin.EstimatedBySystem, question.EstimateOf("a")!.Low.ResolvedOrigin());
    }

    [Fact]
    public void A_planning_without_answers_follows_no_questions()
    {
        var request = new PlanningRequest(null!, "Añade Multiply", null!, [], Aid("orquestador"), null!, new PlanLimits());

        Assert.Null(request.Answers);
    }
}
