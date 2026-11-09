using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The contract of an answered question (docs/17 D-29 and D-33; re-review of the P0, 2026-11-08): an answer only goes with its
/// question when it fits it. The option chosen is one of the question's, the person's own text only goes when the question
/// takes it, and the system never answers the orchestrator by consent: that consent lives in the header of a run, which a
/// question before planning does not have.
/// </summary>
public sealed class AnsweredQuestionContractTests
{
    private static readonly RunId Run = new("20261015-a1b2c3");

    private static PendingQuestion Question(TaskId? task, bool allowOther = true) => new(
        "q-1",
        task,
        Aid(task is null ? "orquestador" : "ejecutor"),
        1,
        new AgentQuestion(
            "¿Guardo las sesiones en SQLite o en ficheros JSON?",
            "Las dos cumplen RQ-02.",
            [new QuestionOption("a", "SQLite", "Detalle", QuestionEffort.Small, true), new QuestionOption("b", "JSON", "Detalle", QuestionEffort.Medium, false)],
            allowOther),
        Eid("ev-question"),
        [],
        T0);

    [Theory]
    [InlineData(null)]
    [InlineData("T3")]
    public void An_option_the_question_does_not_have_is_refused(string? task)
    {
        var of = task is null ? (TaskId?)null : Tid(task);

        var refused = Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(of), QuestionAnswer.Choosing(of, "q-1", "z", "user")));

        Assert.Contains("no option 'z'", refused.Message, StringComparison.Ordinal);
        Assert.Equal("b", new AnsweredQuestion(Run, Question(of), QuestionAnswer.Choosing(of, "q-1", "b", "user")).Answer.OptionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("T3")]
    public void The_person_s_own_text_goes_only_when_the_question_takes_it(string? task)
    {
        var of = task is null ? (TaskId?)null : Tid(task);
        var own = QuestionAnswer.Writing(of, "q-1", "Ninguna de las dos: PostgreSQL.", "user");

        var refused = Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(of, allowOther: false), own));

        Assert.Contains("options only", refused.Message, StringComparison.Ordinal);
        Assert.Equal(own.OtherText, new AnsweredQuestion(Run, Question(of, allowOther: true), own).Answer.OtherText);
    }

    [Fact]
    public void The_orchestrator_is_never_answered_by_consent_and_an_executor_may_be()
    {
        var refused = Assert.Throws<ArgumentException>(() => new AnsweredQuestion(Run, Question(null), QuestionAnswer.RecommendedByConsent(null, "q-1", "a")));

        Assert.Contains("by consent", refused.Message, StringComparison.Ordinal);
        var byConsent = new AnsweredQuestion(Run, Question(Tid("T3")), QuestionAnswer.RecommendedByConsent(Tid("T3"), "q-1", "a"));
        Assert.Equal(QuestionAnswerMethod.RecommendedByConsent, byConsent.Answer.Method);
    }
}
