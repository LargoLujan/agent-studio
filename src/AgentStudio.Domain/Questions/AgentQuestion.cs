using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Questions;

// Phase 13 (docs/17 D-28 to D-33): a doubt of an agent is an output of its report, the same in both CLIs: status «blocked»
// with a question of two to five options. The question is evidence, the answer of the person a new entry of the log, and the
// task goes on with the answer as data. The engine (WP4) reads it from the report and validates it with QuestionRules.

/// <summary>How much work the agent says an option is (docs/17 D-28): «lo dice el agente», never a measurement.</summary>
public enum QuestionEffort
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// One option of a question (docs/17 D-28), as the agent wrote it: plain text, no links nor format. It is data of the agent,
/// never an instruction: choosing it grants nothing (D-33).
/// </summary>
/// <param name="Id">The id the agent gave the option (<c>a</c>, <c>b</c>…), unique in its question.</param>
/// <param name="Label">What the option is, in a few words (<see cref="QuestionRules.MaxLabelLength"/>).</param>
/// <param name="Detail">What choosing it implies (<see cref="QuestionRules.MaxDetailLength"/>).</param>
/// <param name="Effort">How much work the agent says it is.</param>
/// <param name="Recommended">The agent recommends it; one option at most.</param>
public sealed record QuestionOption(string Id, string Label, string Detail, QuestionEffort Effort, bool Recommended);

/// <summary>
/// A question of an agent with options (docs/17 D-28), as its report brings it. <see cref="QuestionRules.Validate"/> says
/// whether it may be shown: a question that breaks a rule is an invalid report, and the task fails as it does today.
/// </summary>
/// <param name="Text">The question (<see cref="QuestionRules.MaxTextLength"/>).</param>
/// <param name="Why">Why the agent asks it (<see cref="QuestionRules.MaxTextLength"/>).</param>
/// <param name="Options">From <see cref="QuestionRules.MinOptions"/> to <see cref="QuestionRules.MaxOptions"/> options.</param>
/// <param name="AllowOther">Whether the person may answer with their own text instead of an option.</param>
public sealed record AgentQuestion(string Text, string Why, IReadOnlyList<QuestionOption> Options, bool AllowOther)
{
    /// <summary>The option the agent recommends, or null when it recommends none.</summary>
    public QuestionOption? RecommendedOption => Options.FirstOrDefault(o => o.Recommended);

    /// <summary>The option with this id, or null.</summary>
    public QuestionOption? Option(string id) => Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));
}

/// <summary>How a question got its answer (docs/17 D-29 and D-33).</summary>
public enum QuestionAnswerMethod
{
    /// <summary>The person chose an option, or wrote their own text, in the window or the card of the question.</summary>
    ChosenByPerson,

    /// <summary>
    /// Nobody answered within the time the person consented to in the header of the run, and the system chose the option the
    /// agent recommended (decision 4 (a): a consent per run, off by default). Never without that consent.
    /// </summary>
    RecommendedByConsent,
}

/// <summary>
/// The answer to a question (docs/17 D-29 and D-30): the option chosen, or the person's own text (exactly one of the two).
/// Answering is not approving: it asks no Windows Hello and grants nothing (D-31 and D-33). The engine records it
/// (<c>QuestionAnswered</c>, with the text as evidence of the person) and launches a new attempt with it as data.
/// </summary>
/// <param name="Task">The task whose agent asked.</param>
/// <param name="QuestionId">The id the engine gave the question when it recorded it.</param>
/// <param name="OptionId">The option chosen, or null for an answer of the person's own.</param>
/// <param name="OtherText">The person's own text, or null when an option was chosen.</param>
/// <param name="DecidedBy">Who answered: the person, or the system by consent.</param>
/// <param name="Method">How.</param>
public sealed record QuestionAnswer(TaskId Task, string QuestionId, string? OptionId, string? OtherText, string DecidedBy, QuestionAnswerMethod Method)
{
    /// <summary>The longest text of the person's own a question takes: what a question itself may be (<see cref="QuestionRules.MaxTextLength"/>) times five.</summary>
    public const int MaxOtherTextLength = 2000;

    /// <summary>An option chosen by the person.</summary>
    /// <exception cref="ArgumentException">A blank id, option or author.</exception>
    public static QuestionAnswer Choosing(TaskId task, string questionId, string optionId, string decidedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        return Checked(new QuestionAnswer(task, questionId, optionId, null, decidedBy, QuestionAnswerMethod.ChosenByPerson));
    }

    /// <summary>An answer in the person's own words.</summary>
    /// <exception cref="ArgumentException">A blank id, text or author, or a text longer than <see cref="MaxOtherTextLength"/>.</exception>
    public static QuestionAnswer Writing(TaskId task, string questionId, string text, string decidedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaxOtherTextLength)
        {
            throw new ArgumentException($"An answer may have {MaxOtherTextLength} characters at most; it has {text.Length}.", nameof(text));
        }

        return Checked(new QuestionAnswer(task, questionId, null, text, decidedBy, QuestionAnswerMethod.ChosenByPerson));
    }

    /// <summary>The option the agent recommended, chosen by the system under the consent of the run.</summary>
    /// <exception cref="ArgumentException">A blank id or option.</exception>
    public static QuestionAnswer RecommendedByConsent(TaskId task, string questionId, string recommendedOptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedOptionId);
        return Checked(new QuestionAnswer(task, questionId, recommendedOptionId, null, "agent-studio", QuestionAnswerMethod.RecommendedByConsent));
    }

    private static QuestionAnswer Checked(QuestionAnswer answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer.QuestionId, nameof(QuestionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(answer.DecidedBy, nameof(DecidedBy));
        return answer;
    }
}
