namespace AgentStudio.Domain.Questions;

/// <summary>
/// The rules a question of an agent must keep before anyone sees it (docs/17 D-28 and D-29), and the caps of D-29. A question
/// that breaks one is an invalid report, and the task fails as it does today. The strict schema of the report cannot say
/// lengths (the strict mode does not take them): the system checks them here. Redacting secrets is not a rule of the
/// question: the engine does it with <c>ISecretRedactor</c> before it records the question as evidence.
/// </summary>
public static class QuestionRules
{
    /// <summary>The fewest options a question has.</summary>
    public const int MinOptions = 2;

    /// <summary>The most options a question has.</summary>
    public const int MaxOptions = 5;

    /// <summary>The longest question, and the longest «why».</summary>
    public const int MaxTextLength = 400;

    /// <summary>The longest label of an option.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>The longest detail of an option.</summary>
    public const int MaxDetailLength = 300;

    /// <summary>The longest id of an option (<c>a</c>, <c>b</c>…): letters, digits, <c>-</c> and <c>_</c>.</summary>
    public const int MaxOptionIdLength = 16;

    /// <summary>The most questions one task may ask; the next one blocks it with «demasiadas preguntas: revisa los requisitos».</summary>
    public const int MaxQuestionsPerTask = 3;

    /// <summary>The most questions one run may ask; the next one blocks its task the same way.</summary>
    public const int MaxQuestionsPerRun = 10;

    /// <summary>The most questions the orchestrator asks before planning, in one round, instead of a plan (D-28).</summary>
    public const int MaxPlannerQuestions = 3;

    /// <summary>
    /// Returns every rule the question breaks, as stable codes, in a fixed order; an empty list means it may be shown. No text
    /// of the agent is in a code.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        CheckText(question.Text, MaxTextLength, "question.text-required", "question.text-too-long", required: true, Add);
        CheckText(question.Why, MaxTextLength, "question.why-required", "question.why-too-long", required: true, Add);

        var options = question.Options ?? [];
        if (options.Count is < MinOptions or > MaxOptions)
        {
            Add("question.options-count");
        }

        foreach (var option in options)
        {
            if (option is null)
            {
                Add("question.option-missing");
                continue;
            }

            if (!IsOptionId(option.Id))
            {
                Add("question.option-id-invalid");
            }

            CheckText(option.Label, MaxLabelLength, "question.label-required", "question.label-too-long", required: true, Add);
            CheckText(option.Detail, MaxDetailLength, "question.detail-required", "question.detail-too-long", required: false, Add);
            if (!Enum.IsDefined(option.Effort))
            {
                Add("question.effort-unknown");
            }
        }

        if (options.Where(o => o is not null).GroupBy(o => o.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            Add("question.option-id-duplicate");
        }

        if (options.Count(o => o is { Recommended: true }) > 1)
        {
            Add("question.recommended-more-than-one");
        }

        return problems;
    }

    /// <summary>Whether <paramref name="text"/> has a character that controls the terminal or the layout: a control character, or a line or paragraph separator.</summary>
    public static bool HasControlCharacter(string? text) =>
        text is not null && text.Any(c => char.IsControl(c) || c is LineSeparator or ParagraphSeparator);

    /// <summary>
    /// Whether <paramref name="text"/> has a character that changes the direction of the text (bidi): with one, a label could
    /// show reversed and pass for a button of the system (D-33).
    /// </summary>
    public static bool HasBidiCharacter(string? text) =>
        text is not null && text.Any(c => c is ArabicLetterMark or LeftToRightMark or RightToLeftMark or (>= FirstEmbedding and <= LastEmbedding) or (>= FirstIsolate and <= LastIsolate));

    private const char LineSeparator = '\u2028';
    private const char ParagraphSeparator = '\u2029';
    private const char ArabicLetterMark = '\u061C';
    private const char LeftToRightMark = '\u200E';
    private const char RightToLeftMark = '\u200F';

    /// <summary>From LEFT-TO-RIGHT EMBEDDING to RIGHT-TO-LEFT OVERRIDE.</summary>
    private const char FirstEmbedding = '\u202A';
    private const char LastEmbedding = '\u202E';

    /// <summary>From LEFT-TO-RIGHT ISOLATE to POP DIRECTIONAL ISOLATE.</summary>
    private const char FirstIsolate = '\u2066';
    private const char LastIsolate = '\u2069';

    private static bool IsOptionId(string? id) =>
        id is { Length: > 0 and <= MaxOptionIdLength } && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    private static void CheckText(string? text, int maxLength, string requiredCode, string tooLongCode, bool required, Action<string> add)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                add(requiredCode);
            }

            return;
        }

        if (text.Length > maxLength)
        {
            add(tooLongCode);
        }

        if (HasControlCharacter(text))
        {
            add("question.control-character");
        }

        if (HasBidiCharacter(text))
        {
            add("question.bidi-character");
        }
    }
}
