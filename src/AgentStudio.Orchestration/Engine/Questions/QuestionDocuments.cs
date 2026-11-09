using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// The documents of a question (docs/17 D-29 and D-33), as the evidence store keeps them:
/// <list type="bullet">
/// <item>the question, whole, as the agent wrote it after redaction (<c>AgentStatement</c> reported by the agent), which
/// <c>QuestionAsked</c> cites and a resume reads back, only as the log anchored it;</item>
/// <item>the decision of the person (a document the system captures when the question is answered): which question, the
/// ids of its options, which one the person chose (or the evidence of their own text), who decided, how and when; ids,
/// hashes and instants only, never a text of the agent, nor the option it recommended, nor the estimates of the system. It
/// is what the judge gets of a question (D-33).</item>
/// </list>
/// Both are JSON written through <see cref="PromptData"/>, so a text of the agent never closes a block it is quoted in.
/// </summary>
internal static class QuestionDocuments
{
    public const string QuestionFormat = "agent-studio/question/1";
    public const string DecisionFormat = "agent-studio/question-decision/1";
    public const string MediaType = "application/json";

    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
    };

    /// <summary>The title of the evidence of a question.</summary>
    public static string QuestionTitle(string question, string task, int attempt) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Pregunta {question} de {task} (intento {attempt})");

    /// <summary>The title of the decision of the person about a question.</summary>
    public static string DecisionTitle(string question, string task) => $"Decisión sobre la pregunta {question} de {task}";

    /// <summary>The title of the person's own answer to a question.</summary>
    public static string OwnAnswerTitle(string question, string task) => $"Tu respuesta a la pregunta {question} de {task}";

    /// <summary>
    /// The question with every text of the agent redacted (D-28: «los secretos, redactados»), or as it is without a redactor.
    /// The ids of the options are letters, digits, <c>-</c> and <c>_</c>, and are kept.
    /// </summary>
    public static AgentQuestion Redacted(AgentQuestion question, ISecretRedactor? redactor) =>
        redactor is null
            ? question
            : question with
            {
                Text = redactor.Redact(question.Text),
                Why = redactor.Redact(question.Why),
                Options = [.. question.Options.Select(o => o with { Label = redactor.Redact(o.Label), Detail = redactor.Redact(o.Detail ?? string.Empty) })],
            };

    /// <summary>The evidence of a question: the whole question as the agent wrote it, after redaction.</summary>
    public static string QuestionJson(string question, string task, int attempt, string agent, AgentQuestion asked) =>
        PromptData.Json(new
        {
            format = QuestionFormat,
            question,
            task,
            attempt,
            agent,
            text = asked.Text,
            why = asked.Why,
            options = asked.Options.Select(o => new { id = o.Id, label = o.Label, detail = o.Detail ?? string.Empty, effort = EffortName(o.Effort), recommended = o.Recommended }).ToList(),
            allowOther = asked.AllowOther,
        });

    /// <summary>The question in its evidence, or null when the text is not one of question <paramref name="question"/>.</summary>
    public static AgentQuestion? ReadQuestion(string json, string question)
    {
        QuestionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<QuestionDocument>(json, Reading);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null || document.Format != QuestionFormat || !string.Equals(document.Question, question, StringComparison.Ordinal)
            || document.Options is null || document.Options.Any(o => o is null || EffortOf(o.Effort) is null))
        {
            return null;
        }

        return new AgentQuestion(
            document.Text,
            document.Why,
            [.. document.Options.Select(o => new QuestionOption(o.Id, o.Label, o.Detail, EffortOf(o.Effort)!.Value, o.Recommended))],
            document.AllowOther);
    }

    /// <summary>The decision of the person about a question, for the judge (D-33): what the agent asked, without its recommendation nor its efforts, and the choice.</summary>
    /// <remarks>
    /// The system captures it, so it is objective evidence for the judge (<c>CapturedBySystem</c>): it carries ids, hashes and
    /// instants only, never a text of the agent (its question, its reason, its labels), which would reach the judge as if the
    /// system had checked it (review of WP4, 2026-11-09). What the agent wrote stays in the evidence of the question, reported
    /// by the agent, which the judge never gets.
    /// </remarks>
    public static string DecisionJson(RecordedAnswer answered)
    {
        var asked = answered.Asked;
        return PromptData.Json(new
        {
            format = DecisionFormat,
            question = asked.Id,
            task = asked.Task.Value,
            attempt = asked.Attempt,
            askedBy = asked.Agent.Value,
            questionEvidence = asked.Evidence.Value,
            questionSha256 = asked.Sha256,
            askedAt = asked.AskedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            options = asked.Question.Options.Select(o => o.Id).ToList(),
            decision = answered.Option is { } option
                ? (object)new { option }
                : new { ownAnswer = new { evidence = answered.OwnTextEvidence?.Value, sha256 = answered.OwnTextSha256 } },
            decidedBy = answered.DecidedBy,
            method = answered.Method.ToString(),
            answeredAt = answered.AnsweredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    public static string EffortName(QuestionEffort effort) => effort switch
    {
        QuestionEffort.Small => "small",
        QuestionEffort.Medium => "medium",
        _ => "large",
    };

    private static QuestionEffort? EffortOf(string? effort) => effort switch
    {
        "small" => QuestionEffort.Small,
        "medium" => QuestionEffort.Medium,
        "large" => QuestionEffort.Large,
        _ => null,
    };

    private sealed record QuestionDocument(
        string Format,
        string Question,
        string Task,
        int Attempt,
        string Agent,
        string Text,
        string Why,
        IReadOnlyList<OptionDocument> Options,
        bool AllowOther);

    private sealed record OptionDocument(string Id, string Label, string Detail, string Effort, bool Recommended);
}
