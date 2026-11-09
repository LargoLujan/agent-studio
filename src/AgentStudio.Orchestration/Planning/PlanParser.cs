using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The outcome of reading the orchestrator's answer. <see cref="Plan"/> is null when the text is not a plan in the
/// shape of <see cref="PlanSchema"/>; <see cref="Problems"/> then says why (in English, like the validator, because the
/// correction round sends them back to the agent).
/// </summary>
public sealed record PlanParseResult(PlanDefinition? Plan, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Phase 13 (docs/17 D-19): the notes of order of the orchestrator that survived <see cref="OrderNotesCheck"/> against the
    /// plan (none without a plan). Text of an agent: shown as «nota del orquestador», they never rule.
    /// </summary>
    public OrderNotes OrderNotes { get; init; } = OrderNotes.None;

    /// <summary>Why notes were left out (in English, for logs): an unknown task, a reason too long or with control characters…</summary>
    public IReadOnlyList<string> DiscardedNotes { get; init; } = [];

    /// <summary>
    /// Phase 13 (docs/17 D-28): the questions the orchestrator asks instead of a plan, before it plans, valid by
    /// <see cref="QuestionRules"/> (three at most). Empty for a plan. An answer with both questions and tasks is not valid.
    /// </summary>
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = [];
}

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

        // Phase 13 (docs/17 D-28): questions instead of a plan, never both.
        if (document.Questions is { Count: > 0 } asked)
        {
            if (document.Tasks.Count > 0)
            {
                problems.Add("The answer has both 'questions' and 'tasks': either ask the person (with 'tasks' empty) or propose the plan (with 'questions' empty).");
                return new PlanParseResult(null, problems);
            }

            return Questions(asked, problems);
        }

        var index = 0;
        foreach (var task in document.Tasks.Where(t => t is not null))
        {
            if (Map(task, index++, problems) is { } spec)
            {
                tasks.Add(spec);
            }
        }

        var blanks = new List<string>();
        var raw = Notes(document.OrderNotes, problems, blanks);
        if (problems.Count > 0)
        {
            return new PlanParseResult(null, problems);
        }

        var plan = PlanDefinition.Create(document.Summary, tasks, document.Risks, PlanSource.OrchestratorAgent, author);
        var (notes, discarded) = OrderNotesCheck.Check(raw, plan.Tasks.Select(t => t.Id));
        return new PlanParseResult(plan, []) { OrderNotes = notes, DiscardedNotes = [.. blanks, .. discarded] };
    }

    /// <summary>
    /// Reads the notes of order of an answer of «Revisar el orden» (docs/17 D-20): one JSON object in the shape of
    /// <see cref="PlanSchema.OrderReviewSchema"/>, as strictly as a plan. What the notes say is checked by the caller; a note
    /// with a blank task is left out (<c>Discarded</c>), as one about a task that is not in the plan.
    /// </summary>
    public static (OrderNotes? Notes, IReadOnlyList<string> Problems, IReadOnlyList<string> Discarded) ParseOrderNotes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, ["The answer is empty: expected one JSON object with the notes of order."], []);
        }

        try
        {
            if (JsonSerializer.Deserialize<OrderNotesDocument>(json, Options) is not { } document)
            {
                return (null, ["The answer is null: expected one JSON object with the notes of order."], []);
            }

            var problems = new List<string>();
            var discarded = new List<string>();
            var notes = Notes(document, problems, discarded);
            return problems.Count > 0 ? (null, problems, discarded) : (notes, [], discarded);
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" (at {ex.Path})";
            return (null, [$"The JSON does not follow the schema of the notes of order{where}: {ex.Message}"], []);
        }
    }

    /// <summary>The questions of the orchestrator, each valid by <see cref="QuestionRules"/>, three at most (D-28).</summary>
    private static PlanParseResult Questions(IReadOnlyList<QuestionDocument> asked, List<string> problems)
    {
        if (asked.Count > QuestionRules.MaxPlannerQuestions)
        {
            problems.Add($"The answer asks {asked.Count} questions; the orchestrator asks {QuestionRules.MaxPlannerQuestions} at most, in one round.");
        }

        var questions = new List<AgentQuestion>();
        for (var i = 0; i < asked.Count; i++)
        {
            if (asked[i] is not { } document || document.Options is null || document.Options.Any(o => o is null))
            {
                problems.Add($"questions[{i}] is null or has a null option.");
                continue;
            }

            var options = new List<QuestionOption>();
            foreach (var option in document.Options)
            {
                if (Name<QuestionEffort>(Capitalized(option.Effort), $"questions[{i}]: the effort of option '{option.Id}'", problems) is { } effort)
                {
                    options.Add(new QuestionOption(option.Id, option.Label, option.Detail, effort, option.Recommended));
                }
            }

            var question = new AgentQuestion(document.Text, document.Why, options, document.AllowOther);
            var broken = QuestionRules.Validate(question);
            if (broken.Count > 0)
            {
                problems.Add($"questions[{i}] breaks the rules of a question: {string.Join(", ", broken)}.");
                continue;
            }

            questions.Add(question);
        }

        return problems.Count > 0 ? new PlanParseResult(null, problems) : new PlanParseResult(null, []) { Questions = questions };
    }

    /// <summary>«small» as the enum names it, «Small»; anything else as it is, so the strict reading refuses it.</summary>
    private static string Capitalized(string value) =>
        value is "small" or "medium" or "large" ? char.ToUpperInvariant(value[0]) + value[1..] : "?" + value;

    /// <summary>
    /// The notes as the agent wrote them, before they are checked against the plan; none when the answer has none. A note with a
    /// blank task is left out (<paramref name="discarded"/>), as one about a task that is not in the plan: notes never rule, so
    /// none of them makes the plan invalid.
    /// </summary>
    private static OrderNotes Notes(OrderNotesDocument? document, List<string> problems, List<string> discarded)
    {
        if (document is null)
        {
            return OrderNotes.None; // an answer of before Phase 13, or one that left them out
        }

        if (document.Dependencies.Any(n => n is null) || document.ParallelOk.Any(n => n is null) || document.Harmful.Any(n => n is null))
        {
            problems.Add("'orderNotes' contains null.");
            return OrderNotes.None;
        }

        var dependencies = new List<OrderDependencyNote>();
        var parallel = new List<ParallelOrderNote>();
        var harmful = new List<HarmfulOrderNote>();
        foreach (var note in document.Dependencies)
        {
            if (TaskIds(note.From, note.To, "a dependency note", discarded) is var (from, to))
            {
                dependencies.Add(new OrderDependencyNote(from, to, note.Reason));
            }
        }

        foreach (var note in document.ParallelOk)
        {
            if (TaskIds(note.A, note.B, "a parallel note", discarded) is var (a, b))
            {
                parallel.Add(new ParallelOrderNote(a, b, note.Reason));
            }
        }

        foreach (var note in document.Harmful)
        {
            if (TaskIds(note.First, note.Then, "a harmful note", discarded) is var (first, then))
            {
                harmful.Add(new HarmfulOrderNote(first, then, note.Reason));
            }
        }

        return new OrderNotes(dependencies, parallel, harmful);
    }

    private static (TaskId, TaskId)? TaskIds(string left, string right, string what, List<string> discarded)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            discarded.Add($"'orderNotes': {what} names a blank task and is left out.");
            return null;
        }

        return (new TaskId(left), new TaskId(right));
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
    // Phase 13: orderNotes and questions are in the schema (strict mode makes them required), and an answer that has them is
    // read as strictly as the rest (no null, no unknown property). An answer without them, written before Phase 13 or by a
    // test, is still a plan, without notes: their defaults say so. A default of null on a type that is not nullable keeps an
    // explicit null an error (RespectNullableAnnotations).
    private sealed record PlanDocument(
        string Summary,
        IReadOnlyList<TaskDocument> Tasks,
        IReadOnlyList<string> Risks,
        OrderNotesDocument OrderNotes = null!,
        IReadOnlyList<QuestionDocument> Questions = null!);

    private sealed record OrderNotesDocument(IReadOnlyList<DependencyNoteDocument> Dependencies, IReadOnlyList<ParallelNoteDocument> ParallelOk, IReadOnlyList<HarmfulNoteDocument> Harmful);

    private sealed record DependencyNoteDocument(string From, string To, string Reason);

    private sealed record ParallelNoteDocument(string A, string B, string Reason);

    private sealed record HarmfulNoteDocument(string First, string Then, string Reason);

    private sealed record QuestionDocument(string Text, string Why, IReadOnlyList<QuestionOptionDocument> Options, bool AllowOther);

    private sealed record QuestionOptionDocument(string Id, string Label, string Detail, string Effort, bool Recommended);

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
