using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Questions;
using static AgentStudio.Orchestration.Planning.StrictJsonSchema;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// What the executor says it did (docs/13 section 4.6). It is evidence reported by the agent, never proof. Phase 13 (docs/17
/// D-28) adds <paramref name="Question"/>: a doubt of the agent with two to five options, only with <see cref="Blocked"/>.
/// A report stored before it has no <c>question</c> and is read as it always was.
/// </summary>
public sealed record ExecutorReport(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> Claims,
    IReadOnlyList<string> OpenIssues,
    IReadOnlyList<ExecutorOutput> Outputs,
    ReportedQuestion? Question = null)
{
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Failed = "failed";

    /// <summary>The efforts an option may declare (docs/17 D-28), as the schema names them.</summary>
    public static IReadOnlyList<string> Efforts { get; } = ["small", "medium", "large"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 8,
    };

    /// <summary>
    /// The report schema: portable to Claude's <c>--json-schema</c> and Codex's <c>--output-schema</c> (strict mode). The
    /// question is required and nullable, as strict mode has no optional properties; its lengths are not in the schema (strict
    /// mode does not take them): <see cref="QuestionRules"/> checks them after parsing.
    /// </summary>
    public static string Schema { get; } = ToCompactJson(Object(
        ("status", Enum([Completed, Blocked, Failed])),
        ("summary", String()),
        ("filesChanged", Array(String())),
        ("claims", Array(String())),
        ("openIssues", Array(String())),
        ("outputs", Array(Object(("key", String()), ("value", String())))),
        ("question", Nullable(Object(
            ("text", String()),
            ("why", String()),
            ("options", Array(Object(
                ("id", String()),
                ("label", String()),
                ("detail", String()),
                ("effort", Enum(Efforts)),
                ("recommended", Boolean())))),
            ("allowOther", Boolean()))))));

    /// <summary>Reads a report strictly; null with a problem when it is not one, or when its question breaks a rule of <see cref="QuestionRules"/>.</summary>
    public static ExecutorReport? Parse(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            problem = "The agent returned no report.";
            return null;
        }

        ExecutorReport? report;
        try
        {
            report = JsonSerializer.Deserialize<ExecutorReport>(json, Options);
        }
        catch (JsonException ex)
        {
            problem = "The report does not follow the report schema: " + ex.Message;
            return null;
        }

        if (report is null)
        {
            problem = "The report is null.";
            return null;
        }

        if (report.Status is not (Completed or Blocked or Failed))
        {
            problem = $"The report status '{report.Status}' is not completed, blocked or failed.";
            return null;
        }

        if (report.FilesChanged.Any(f => f is null) || report.Claims.Any(c => c is null) || report.OpenIssues.Any(i => i is null) || report.Outputs.Any(o => o is null))
        {
            problem = "The report has null items in a list.";
            return null;
        }

        if (report.Question is { } question && QuestionProblem(report.Status, question) is { } wrong)
        {
            problem = wrong;
            return null;
        }

        return report;
    }

    /// <summary>The question of the report as the domain takes it, or null when it asks none.</summary>
    public AgentQuestion? AgentQuestion() => Question?.ToAgentQuestion();

    /// <summary>The outputs as a dictionary (the last value of a repeated key wins), for conditions.</summary>
    public IReadOnlyDictionary<string, string> OutputMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in Outputs)
        {
            map[output.Key] = output.Value;
        }

        return map;
    }

    /// <summary>
    /// Why a question makes the report invalid (docs/17 D-28): it goes only with <see cref="Blocked"/>, every option declares an
    /// effort of the list, and it keeps every rule of <see cref="QuestionRules"/>. Null when it may be shown. The problem names
    /// the codes of the rules, never the text of the agent.
    /// </summary>
    private static string? QuestionProblem(string status, ReportedQuestion question)
    {
        if (status != Blocked)
        {
            return $"The report asks a question with status '{status}': a question only goes with status '{Blocked}'.";
        }

        if (question.Options is null || question.Options.Any(o => o is null))
        {
            return "The question of the report has null options.";
        }

        if (question.Options.Any(o => !Efforts.Contains(o.Effort, StringComparer.Ordinal)))
        {
            return "An option of the question declares an effort that is not small, medium or large.";
        }

        var broken = QuestionRules.Validate(question.ToAgentQuestion());
        return broken.Count == 0 ? null : "The question of the report breaks the rules of a question: " + string.Join(", ", broken) + ".";
    }

    /// <summary>A boolean schema, which the portable subset allows (ADR-0013) and the builder of the plan does not need.</summary>
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
}

public sealed record ExecutorOutput(string Key, string Value);

/// <summary>The question of a report as the agent wrote it (docs/17 D-28): the shape of the schema, before the domain reads it.</summary>
public sealed record ReportedQuestion(string Text, string Why, IReadOnlyList<ReportedOption> Options, bool AllowOther)
{
    /// <summary>The question as the domain takes it. An effort outside <see cref="ExecutorReport.Efforts"/> becomes a value the rules refuse (<c>question.effort-unknown</c>).</summary>
    public AgentQuestion ToAgentQuestion() =>
        new(Text, Why, [.. (Options ?? []).Select(o => new QuestionOption(o.Id, o.Label, o.Detail, EffortOf(o.Effort), o.Recommended))], AllowOther);

    private static QuestionEffort EffortOf(string effort) => effort switch
    {
        "small" => QuestionEffort.Small,
        "medium" => QuestionEffort.Medium,
        "large" => QuestionEffort.Large,
        _ => (QuestionEffort)(-1),
    };
}

/// <summary>One option of the question of a report, as the agent wrote it.</summary>
public sealed record ReportedOption(string Id, string Label, string Detail, string Effort, bool Recommended);
