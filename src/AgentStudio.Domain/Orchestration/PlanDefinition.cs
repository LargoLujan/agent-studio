using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration;

/// <summary>Who wrote the plan. The identity of the author is not part of the plan's content hash.</summary>
public enum PlanSource
{
    OrchestratorAgent,
    User,
}

/// <summary>How a task uses the repository checkout it runs in.</summary>
public enum WorkspaceMode
{
    /// <summary>No checkout: the task works on data only.</summary>
    None,

    /// <summary>A checkout the task may read but must not change.</summary>
    ReadOnly,

    /// <summary>A private worktree the task may change, inside its write scope.</summary>
    Write,
}

/// <summary>An edge of the plan: the task waits for <see cref="Prerequisite"/>, and for the given branch when it is a condition.</summary>
public sealed record DependencySpec(TaskId Prerequisite, string? Branch = null);

/// <summary>
/// One task of a plan, as the orchestrator proposes it and the user approves it. <see cref="WriteScope"/> lists
/// relative folders (<c>src/Calc/</c>) and exact files. <see cref="VerificationSteps"/> and <see cref="ToolId"/> name
/// steps of the trusted verification profile: a plan never carries a command.
/// </summary>
public sealed record TaskSpec(
    TaskId Id,
    string Title,
    WorkTaskKind Kind,
    AgentId? Agent,
    string Objective,
    string Instructions,
    IReadOnlyList<DependencySpec> DependsOn,
    JoinPolicy Join,
    IReadOnlyList<RequirementId> Covers,
    IReadOnlyList<string> WriteScope,
    IReadOnlyList<string> VerificationSteps,
    ConditionPredicate? Condition,
    string? ToolId,
    int MaxRetries,
    TimeSpan? Timeout,
    WorkspaceMode Workspace);

/// <summary>
/// The plan of an orchestration run. Creating one never fails because of its content (that is the job of
/// <see cref="PlanValidator"/>); it only puts it in canonical form so that <see cref="ContentHash"/> identifies the
/// content, not the order it arrived in. Like <c>FrozenRequirementSet</c>, tasks are ordered by id and the sets inside a
/// task (dependencies, covered requirements, write scope) and the risks are sorted. The verification steps keep their
/// order, because they run in that order. Source and author are provenance, not content, and are left out of the hash.
/// </summary>
public sealed class PlanDefinition
{
    private const string HashFormat = "plan/1";

    private PlanDefinition(string summary, IReadOnlyList<TaskSpec> tasks, IReadOnlyList<string> risks, PlanSource source, AgentId? author)
    {
        Summary = summary;
        Tasks = tasks;
        Risks = risks;
        Source = source;
        Author = author;
        ContentHash = ComputeHash(summary, tasks, risks);
    }

    public string Summary { get; }

    /// <summary>The tasks in canonical order (by id).</summary>
    public IReadOnlyList<TaskSpec> Tasks { get; }

    public IReadOnlyList<string> Risks { get; }

    public PlanSource Source { get; }

    public AgentId? Author { get; }

    /// <summary>SHA-256 of the canonical content. Independent of input order, source and author.</summary>
    public string ContentHash { get; }

    public static PlanDefinition Create(string summary, IReadOnlyList<TaskSpec> tasks, IReadOnlyList<string> risks, PlanSource source, AgentId? author)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(risks);

        var canonicalTasks = tasks.Select(Canonical).OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToList();
        var canonicalRisks = risks
            .Select(r => r ?? throw new ArgumentException("A risk cannot be null.", nameof(risks)))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new PlanDefinition(summary, canonicalTasks, canonicalRisks, source, author);
    }

    /// <summary>
    /// Builds a fresh graph with one pending task per spec, in canonical order. Throws <see cref="DependencyCycleException"/>
    /// (with the path) for a cycle and <see cref="DomainRuleViolationException"/> for a duplicate task or an unknown
    /// dependency; a plan that passes <see cref="PlanValidator"/> never throws.
    /// </summary>
    public TaskGraph BuildGraph(DateTimeOffset at)
    {
        var graph = new TaskGraph();
        foreach (var spec in Tasks)
        {
            graph.Add(new WorkTask(spec.Id, spec.Title, spec.Kind, at, spec.Agent, spec.MaxRetries, requiresEvidence: null, join: spec.Join));
        }

        var known = Tasks.Select(t => t.Id).ToHashSet();
        foreach (var spec in Tasks)
        {
            foreach (var dependency in spec.DependsOn)
            {
                if (!known.Contains(dependency.Prerequisite))
                {
                    throw new DomainRuleViolationException(
                        "plan.unknown-dependency",
                        $"Task '{spec.Id}' depends on '{dependency.Prerequisite}', which is not in the plan.");
                }

                graph.AddDependency(spec.Id, dependency.Prerequisite, dependency.Branch);
            }
        }

        return graph;
    }

    private static TaskSpec Canonical(TaskSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.DependsOn is null || spec.Covers is null || spec.WriteScope is null || spec.VerificationSteps is null)
        {
            throw new ArgumentException($"Task '{spec.Id}' has a null list.", nameof(spec));
        }

        return spec with
        {
            DependsOn = [.. spec.DependsOn.OrderBy(d => d.Prerequisite.Value, StringComparer.Ordinal).ThenBy(d => d.Branch, StringComparer.Ordinal)],
            Covers = [.. spec.Covers.OrderBy(r => r.Value, StringComparer.Ordinal)],
            WriteScope = [.. spec.WriteScope.Order(StringComparer.Ordinal)],
            VerificationSteps = [.. spec.VerificationSteps],
        };
    }

    private static string ComputeHash(string summary, IReadOnlyList<TaskSpec> tasks, IReadOnlyList<string> risks)
    {
        var fields = new List<string?> { HashFormat, summary, Count(tasks.Count) };
        foreach (var task in tasks)
        {
            fields.Add(task.Id.Value);
            fields.Add(task.Title);
            fields.Add(task.Kind.ToString());
            fields.Add(task.Agent?.Value);
            fields.Add(task.Objective);
            fields.Add(task.Instructions);
            fields.Add(task.Join.ToString());
            fields.Add(task.Workspace.ToString());
            fields.Add(task.ToolId);
            fields.Add(Count(task.MaxRetries));
            fields.Add(task.Timeout?.Ticks.ToString(CultureInfo.InvariantCulture));
            AddPredicate(fields, task.Condition);

            fields.Add(Count(task.DependsOn.Count));
            foreach (var dependency in task.DependsOn)
            {
                fields.Add(dependency.Prerequisite.Value);
                fields.Add(dependency.Branch);
            }

            AddList(fields, task.Covers.Select(r => r.Value).ToList());
            AddList(fields, task.WriteScope);
            AddList(fields, task.VerificationSteps);
        }

        AddList(fields, risks);
        return Common.ContentHash.Sha256Hex(Common.ContentHash.Canonical([.. fields]));
    }

    private static void AddPredicate(List<string?> fields, ConditionPredicate? predicate)
    {
        switch (predicate)
        {
            case null:
                fields.Add("-");
                break;
            case TaskSucceeded p:
                fields.AddRange([nameof(TaskSucceeded), p.Task.Value]);
                break;
            case TestsPassed p:
                fields.AddRange([nameof(TestsPassed), p.Task.Value]);
                break;
            case OutputEquals p:
                fields.AddRange([nameof(OutputEquals), p.Task.Value, p.Key, p.Value]);
                break;
            case PathsChanged p:
                fields.AddRange([nameof(PathsChanged), p.Task.Value, p.PathPrefix]);
                break;
            default:
                throw new InvalidOperationException($"Unknown predicate type {predicate.GetType().Name}.");
        }
    }

    private static void AddList(List<string?> fields, IReadOnlyList<string> values)
    {
        fields.Add(Count(values.Count));
        fields.AddRange(values);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
