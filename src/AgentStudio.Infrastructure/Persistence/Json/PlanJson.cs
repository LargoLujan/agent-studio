using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>
/// The content of a plan as stored in <c>plans.payload_json</c> (schema version <see cref="Version"/>). Source and
/// author are columns of their own: they are provenance, not part of the plan's hash.
/// </summary>
internal sealed record PlanJson(string Summary, IReadOnlyList<string> Risks, IReadOnlyList<TaskSpecJson> Tasks)
{
    public const int Version = 1;

    public static PlanJson From(PlanDefinition plan) => new(plan.Summary, plan.Risks, [.. plan.Tasks.Select(TaskSpecJson.From)]);

    /// <summary>Creates the plan again, which puts it in canonical form and recomputes its hash.</summary>
    public PlanDefinition ToDomain(PlanSource source, AgentId? author) =>
        PlanDefinition.Create(Summary, [.. (Tasks ?? []).Select(t => t.ToDomain())], Risks ?? [], source, author);
}

internal sealed record TaskSpecJson(
    string Id,
    string Title,
    WorkTaskKind Kind,
    string? Agent,
    string Objective,
    string Instructions,
    IReadOnlyList<DependencyJson> DependsOn,
    JoinPolicy Join,
    IReadOnlyList<string> Covers,
    IReadOnlyList<string> WriteScope,
    IReadOnlyList<string> VerificationSteps,
    PredicateJson? Condition,
    string? ToolId,
    int MaxRetries,
    TimeSpan? Timeout,
    WorkspaceMode Workspace)
{
    public static TaskSpecJson From(TaskSpec spec) => new(
        spec.Id.Value,
        spec.Title,
        spec.Kind,
        spec.Agent?.Value,
        spec.Objective,
        spec.Instructions,
        [.. spec.DependsOn.Select(d => new DependencyJson(d.Prerequisite.Value, d.Branch))],
        spec.Join,
        [.. spec.Covers.Select(r => r.Value)],
        spec.WriteScope,
        spec.VerificationSteps,
        PredicateJson.From(spec.Condition),
        spec.ToolId,
        spec.MaxRetries,
        spec.Timeout,
        spec.Workspace);

    public TaskSpec ToDomain() => new(
        new TaskId(Id),
        Title,
        Kind,
        Agent is null ? null : new AgentId(Agent),
        Objective,
        Instructions,
        [.. (DependsOn ?? []).Select(d => new DependencySpec(new TaskId(d.Prerequisite), d.Branch))],
        Join,
        [.. (Covers ?? []).Select(r => new RequirementId(r))],
        WriteScope ?? [],
        VerificationSteps ?? [],
        Condition?.ToDomain(),
        ToolId,
        MaxRetries,
        Timeout,
        Workspace);
}

internal sealed record DependencyJson(string Prerequisite, string? Branch);

/// <summary>The closed set of condition predicates, with <see cref="Type"/> naming which one.</summary>
internal sealed record PredicateJson(string Type, string Task, string? Key = null, string? Value = null, string? PathPrefix = null)
{
    public static PredicateJson? From(ConditionPredicate? predicate) => predicate switch
    {
        null => null,
        TaskSucceeded p => new(nameof(TaskSucceeded), p.Task.Value),
        TestsPassed p => new(nameof(TestsPassed), p.Task.Value),
        OutputEquals p => new(nameof(OutputEquals), p.Task.Value, Key: p.Key, Value: p.Value),
        PathsChanged p => new(nameof(PathsChanged), p.Task.Value, PathPrefix: p.PathPrefix),
        _ => throw new InvalidOperationException($"Unknown predicate type {predicate.GetType().Name}."),
    };

    public ConditionPredicate ToDomain() => Type switch
    {
        nameof(TaskSucceeded) => new TaskSucceeded(new TaskId(Task)),
        nameof(TestsPassed) => new TestsPassed(new TaskId(Task)),
        nameof(OutputEquals) => new OutputEquals(new TaskId(Task), Key ?? throw Missing(nameof(Key)), Value ?? throw Missing(nameof(Value))),
        nameof(PathsChanged) => new PathsChanged(new TaskId(Task), PathPrefix ?? throw Missing(nameof(PathPrefix))),
        _ => throw new InvalidOperationException($"Unknown predicate type '{Type}'."),
    };

    private InvalidOperationException Missing(string member) => new($"A {Type} predicate needs its {member}.");
}
