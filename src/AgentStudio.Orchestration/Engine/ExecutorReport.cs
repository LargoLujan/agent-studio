using System.Text.Json;
using System.Text.Json.Serialization;
using static AgentStudio.Orchestration.Planning.StrictJsonSchema;

namespace AgentStudio.Orchestration.Engine;

/// <summary>What the executor says it did (docs/13 section 4.6). It is evidence reported by the agent, never proof.</summary>
public sealed record ExecutorReport(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> Claims,
    IReadOnlyList<string> OpenIssues,
    IReadOnlyList<ExecutorOutput> Outputs)
{
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Failed = "failed";

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

    /// <summary>The report schema: portable to Claude's <c>--json-schema</c> and Codex's <c>--output-schema</c> (strict mode).</summary>
    public static string Schema { get; } = ToCompactJson(Object(
        ("status", Enum([Completed, Blocked, Failed])),
        ("summary", String()),
        ("filesChanged", Array(String())),
        ("claims", Array(String())),
        ("openIssues", Array(String())),
        ("outputs", Array(Object(("key", String()), ("value", String()))))));

    /// <summary>Reads a report strictly; null with a problem when it is not one.</summary>
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

        return report;
    }

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
}

public sealed record ExecutorOutput(string Key, string Value);
