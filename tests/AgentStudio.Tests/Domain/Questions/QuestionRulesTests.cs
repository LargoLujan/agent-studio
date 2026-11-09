using AgentStudio.Domain.Questions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Questions;

/// <summary>
/// The rules a question of an agent keeps before anyone sees it (docs/17 D-28): two to five options, unique ids, the lengths,
/// one recommended at most, no control nor direction characters. Each rule has its code; no text of the agent is in one.
/// </summary>
public sealed class QuestionRulesTests
{
    private static QuestionOption Option(string id, bool recommended = false, string? label = null, string detail = "Detalle") =>
        new(id, label ?? "Opción " + id, detail, QuestionEffort.Small, recommended);

    private static AgentQuestion Question(params QuestionOption[] options) =>
        new("¿Guardo las sesiones en SQLite o en ficheros JSON?", "Las dos cumplen RQ-02; cambia cómo se migran los datos.", options, AllowOther: true);

    [Fact]
    public void The_question_of_the_design_is_valid()
    {
        var question = Question(Option("a", recommended: true), Option("b"));

        Assert.Empty(QuestionRules.Validate(question));
        Assert.Equal("a", question.RecommendedOption?.Id);
        Assert.Equal("b", question.Option("b")?.Id);
    }

    [Fact]
    public void A_question_has_two_to_five_options()
    {
        Assert.Contains("question.options-count", QuestionRules.Validate(Question(Option("a"))));
        Assert.Contains("question.options-count", QuestionRules.Validate(Question(Option("a"), Option("b"), Option("c"), Option("d"), Option("e"), Option("f"))));
        Assert.Empty(QuestionRules.Validate(Question(Option("a"), Option("b"), Option("c"), Option("d"), Option("e"))));
    }

    [Fact]
    public void Option_ids_are_unique_short_and_plain_and_one_is_recommended_at_most()
    {
        Assert.Contains("question.option-id-duplicate", QuestionRules.Validate(Question(Option("a"), Option("a"))));
        Assert.Contains("question.option-id-invalid", QuestionRules.Validate(Question(Option("a b"), Option("b"))));
        Assert.Contains("question.option-id-invalid", QuestionRules.Validate(Question(Option(new string('x', QuestionRules.MaxOptionIdLength + 1)), Option("b"))));
        Assert.Contains("question.recommended-more-than-one", QuestionRules.Validate(Question(Option("a", true), Option("b", true))));
    }

    [Fact]
    public void The_lengths_are_the_ones_of_the_design()
    {
        var longText = new AgentQuestion(new string('x', QuestionRules.MaxTextLength + 1), "porque", [Option("a"), Option("b")], false);
        var longLabel = Question(Option("a", label: new string('x', QuestionRules.MaxLabelLength + 1)), Option("b"));
        var longDetail = Question(Option("a", detail: new string('x', QuestionRules.MaxDetailLength + 1)), Option("b"));

        Assert.Equal(["question.text-too-long"], QuestionRules.Validate(longText));
        Assert.Equal(["question.label-too-long"], QuestionRules.Validate(longLabel));
        Assert.Equal(["question.detail-too-long"], QuestionRules.Validate(longDetail));
        Assert.Equal((400, 80, 300), (QuestionRules.MaxTextLength, QuestionRules.MaxLabelLength, QuestionRules.MaxDetailLength));
        Assert.Equal((3, 10), (QuestionRules.MaxQuestionsPerTask, QuestionRules.MaxQuestionsPerRun));
    }

    /// <summary>D-33: a label with a direction character could show reversed and pass for a button of the system; a control character could break the frame.</summary>
    [Fact]
    public void No_text_has_a_control_or_a_direction_character()
    {
        var rightToLeftOverride = ((char)0x202E).ToString();
        var lineSeparator = ((char)0x2028).ToString();

        Assert.Contains("question.bidi-character", QuestionRules.Validate(Question(Option("a", label: "Aceptar" + rightToLeftOverride + "ratpecA"), Option("b"))));
        Assert.Contains("question.control-character", QuestionRules.Validate(Question(Option("a", label: "Uno\nDos"), Option("b"))));
        Assert.Contains("question.control-character", QuestionRules.Validate(Question(Option("a", detail: "Uno" + lineSeparator + "Dos"), Option("b"))));
        Assert.True(QuestionRules.HasBidiCharacter(((char)0x2066).ToString()));
        Assert.False(QuestionRules.HasBidiCharacter("¿Guardo las sesiones?"));
    }

    [Fact]
    public void The_question_and_its_why_are_required_and_the_effort_is_one_of_the_list()
    {
        var blank = new AgentQuestion(" ", "", [Option("a"), Option("b")], false);
        var effort = Question(Option("a") with { Effort = (QuestionEffort)9 }, Option("b"));

        Assert.Equal(["question.text-required", "question.why-required"], QuestionRules.Validate(blank));
        Assert.Equal(["question.effort-unknown"], QuestionRules.Validate(effort));
        Assert.Equal(["Small", "Medium", "Large"], Enum.GetNames<QuestionEffort>());
    }

    [Fact]
    public void An_answer_is_an_option_or_the_persons_own_text_and_the_system_answers_only_with_the_recommended_one()
    {
        var chose = QuestionAnswer.Choosing(Tid("T3"), "q-T3-1", "a", "user");
        var wrote = QuestionAnswer.Writing(Tid("T3"), "q-T3-1", "Usa SQLite, pero con WAL.", "user");
        var consent = QuestionAnswer.RecommendedByConsent(Tid("T3"), "q-T3-1", "a");

        Assert.Equal(("a", (string?)null, QuestionAnswerMethod.ChosenByPerson), (chose.OptionId, chose.OtherText, chose.Method));
        Assert.Equal((null, "Usa SQLite, pero con WAL."), (wrote.OptionId, wrote.OtherText));
        Assert.Equal((QuestionAnswerMethod.RecommendedByConsent, "agent-studio"), (consent.Method, consent.DecidedBy));
        Assert.Throws<ArgumentException>(() => QuestionAnswer.Writing(Tid("T3"), "q-T3-1", new string('x', QuestionAnswer.MaxOtherTextLength + 1), "user"));
        Assert.Throws<ArgumentException>(() => QuestionAnswer.Choosing(Tid("T3"), " ", "a", "user"));
    }
}
