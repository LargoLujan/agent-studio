using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The outcome of reading the orchestrator's answer. <see cref="Plan"/> is null when the text is not a plan in the
/// shape of <see cref="PlanSchema"/>; <see cref="Problems"/> then says why (in English, like the validator, because the
/// correction round sends them back to the agent).
/// </summary>
public sealed record PlanParseResult(PlanDefinition? Plan, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the orchestrator's JSON strictly (ADR-0013): exactly one object, every property of the schema present (also the
/// nullable ones), no property the schema does not have, no duplicated property, no nulls where the schema has none, no
/// comments, trailing commas or type coercion, and only the names of the closed sets (task kinds, joins, workspaces and
/// predicates, case-sensitive). Parsing says nothing about whether the plan is sound: that is <see cref="PlanValidator"/>.
/// </summary>
public static class PlanParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
    };

    /// <param name="json">The structured output of the orchestrator agent.</param>
    /// <param name="author">The orchestrator that wrote it; it becomes the plan's author (provenance, not content).</param>
    public static PlanParseResult Parse(string? json, AgentId? author)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Rejected("The answer is empty: expected one JSON object with the plan.");
        }

        PlanDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PlanDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" (at {ex.Path})";
            return Rejected($"The JSON does not follow the plan schema{where}: {ex.Message}");
        }

        if (document is null)
        {
            return Rejected("The answer is null: expected one JSON object with the plan.");
        }

        var problems = new List<string>();
        var tasks = new List<TaskSpec>();
        RequireNoNulls(document.Risks, "risks", problems);
        if (document.Tasks.Any(t => t is null))
        {
            problems.Add("'tasks' contains null.");
        }

        var index = 0;
        foreach (var task in document.Tasks.Where(t => t is not null))
        {
            if (Map(task, index++, problems) is { } spec)
            {
                tasks.Add(spec);
            }
        }

        if (problems.Count > 0)
        {
            return new PlanParseResult(null, problems);
        }

        return new PlanParseResult(
            PlanDefinition.Create(document.Summary, tasks, document.Risks, PlanSource.OrchestratorAgent, author),
            []);
    }

    private static PlanParseResult Rejected(string problem) => new(null, [problem]);

    private static TaskSpec? Map(TaskDocument task, int index, List<string> problems)
    {
        var label = string.IsNullOrWhiteSpace(task.Id) ? $"tasks[{index}]" : $"Task '{task.Id}'";
        var before = problems.Count;

        var id = Id(task.Id, s => new TaskId(s), $"{label}: 'id' is blank.", problems);
        var kind = Name<WorkTaskKind>(task.Kind, $"{label}: 'kind'", problems);
        var join = Name<JoinPolicy>(task.Join, $"{label}: 'join'", problems);
        var workspace = Name<WorkspaceMode>(task.Workspace, $"{label}: 'workspace'", problems);
        AgentId? agent = task.Agent is null ? null : Id(task.Agent, s => new AgentId(s), $"{label}: 'agent' is blank (use null for no agent).", problems);

        RequireNoNulls(task.DependsOn, $"{label}: 'dependsOn'", problems);
        RequireNoNulls(task.Covers, $"{label}: 'covers'", problems);
        RequireNoNulls(task.WriteScope, $"{label}: 'writeScope'", problems);
        RequireNoNulls(task.VerificationSteps, $"{label}: 'verificationSteps'", problems);

        var dependencies = new List<DependencySpec>();
        foreach (var dependency in task.DependsOn.Where(d => d is not null))
        {
            if (Id(dependency.Prerequisite, s => new TaskId(s), $"{label}: a dependency has a blank 'prerequisite'.", problems) is { } prerequisite)
            {
                dependencies.Add(new DependencySpec(prerequisite, dependency.Branch));
            }
        }

        var covers = new List<RequirementId>();
        foreach (var requirement in task.Covers.Where(r => r is not null))
        {
            if (Id(requirement, s => new RequirementId(s), $"{label}: 'covers' has a blank requirement.", problems) is { } covered)
            {
                covers.Add(covered);
            }
        }

        var condition = task.Condition is null ? null : Predicate(task.Condition, label, problems);
        if (problems.Count > before || id is null || kind is null || join is null || workspace is null)
        {
            return null;
        }

        return new TaskSpec(
            id.Value,
            task.Title,
            kind.Value,
            agent,
            task.Objective,
            task.Instructions,
            dependencies,
            join.Value,
            covers,
            [.. task.WriteScope],
            [.. task.VerificationSteps],
            condition,
            task.ToolId,
            task.MaxRetries,
            task.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            workspace.Value);
    }

    private static ConditionPredicate? Predicate(PredicateDocument predicate, string label, List<string> problems)
    {
        var task = Id(predicate.Task, s => new TaskId(s), $"{label}: the condition names a blank 'task'.", problems);
        if (task is not { } referenced)
        {
            return null;
        }

        switch (predicate.Type)
        {
            case nameof(TaskSucceeded):
                return new TaskSucceeded(referenced);
            case nameof(TestsPassed):
                return new TestsPassed(referenced);
            case nameof(OutputEquals) when predicate.Key is not null && predicate.Value is not null:
                return new OutputEquals(referenced, predicate.Key, predicate.Value);
            case nameof(OutputEquals):
                problems.Add($"{label}: an OutputEquals condition needs both 'key' and 'value'.");
                return null;
            case nameof(PathsChanged) when predicate.PathPrefix is not null:
                return new PathsChanged(referenced, predicate.PathPrefix);
            case nameof(PathsChanged):
                problems.Add($"{label}: a PathsChanged condition needs 'pathPrefix' (an empty one means any path).");
                return null;
            default:
                problems.Add($"{label}: condition type '{predicate.Type}' is not one of TaskSucceeded, TestsPassed, OutputEquals, PathsChanged.");
                return null;
        }
    }

    /// <summary>Only the exact name of a member: no numbers, no other casing.</summary>
    private static T? Name<T>(string value, string what, List<string> problems)
        where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        if (names.Contains(value, StringComparer.Ordinal))
        {
            return Enum.Parse<T>(value, ignoreCase: false);
        }

        problems.Add($"{what} '{value}' is not one of {string.Join(", ", names)}.");
        return null;
    }

    private static T? Id<T>(string value, Func<string, T> create, string problem, List<string> problems)
        where T : struct
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(problem);
            return null;
        }

        return create(value);
    }

    private static void RequireNoNulls<T>(IReadOnlyList<T?> values, string what, List<string> problems)
    {
        if (values.Any(v => v is null))
        {
            problems.Add($"{what} contains null.");
        }
    }

    // The shape of the schema. Positional records: with RespectRequiredConstructorParameters every member is required,
    // the nullable ones included, exactly as strict mode requires every property to be present.
    private sealed record PlanDocument(string Summary, IReadOnlyList<TaskDocument> Tasks, IReadOnlyList<string> Risks);

    private sealed record TaskDocument(
        string Id,
        string Title,
        string Kind,
        string? Agent,
        string Objective,
        string Instructions,
        IReadOnlyList<DependencyDocument> DependsOn,
        string Join,
        IReadOnlyList<string> Covers,
        IReadOnlyList<string> WriteScope,
        IReadOnlyList<string> VerificationSteps,
        PredicateDocument? Condition,
        string? ToolId,
        int MaxRetries,
        int? TimeoutMinutes,
        string Workspace);

    private sealed record DependencyDocument(string Prerequisite, string? Branch);

    private sealed record PredicateDocument(string Type, string Task, string? Key, string? Value, string? PathPrefix);
}
