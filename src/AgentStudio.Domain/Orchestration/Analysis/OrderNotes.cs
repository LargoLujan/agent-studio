using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Orchestration.Analysis;

// Phase 13 (docs/17 D-19 and D-20): the orchestrator explains the order inside the plan it proposes («orderNotes»), and
// «Revisar el orden» asks it again for a plan the person changed, with this same shape. They are text of an agent: shown as
// «nota del orquestador», they never rule. The system checks that the tasks exist and that nothing makes a cycle (TaskGraph),
// and offers each suggestion as a typed fix (AddDependencyFix); nothing is applied by itself.

/// <summary>Why <paramref name="To"/> depends on <paramref name="From"/>: «T3 necesita la migración de T2».</summary>
public sealed record OrderDependencyNote(TaskId From, TaskId To, string Reason);

/// <summary>Two tasks that may run at once, and why.</summary>
public sealed record ParallelOrderNote(TaskId A, TaskId B, string Reason);

/// <summary>An order that would be harmful: <paramref name="First"/> before <paramref name="Then"/> («T5 antes que T2 rompería la compilación»).</summary>
public sealed record HarmfulOrderNote(TaskId First, TaskId Then, string Reason);

/// <summary>
/// The notes of order of a plan (docs/17 D-19), or the answer of «Revisar el orden» (D-20):
/// <c>{ dependencies:[{from,to,reason}], parallelOk:[{a,b,reason}], harmful:[{first,then,reason}] }</c>.
/// </summary>
public sealed record OrderNotes(
    IReadOnlyList<OrderDependencyNote> Dependencies,
    IReadOnlyList<ParallelOrderNote> ParallelOk,
    IReadOnlyList<HarmfulOrderNote> Harmful)
{
    /// <summary>The longest reason of a note: the orchestrator writes one sentence.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>No notes.</summary>
    public static OrderNotes None { get; } = new([], [], []);

    /// <summary>Every task the notes name, once each, in the order they are first named.</summary>
    public IReadOnlyList<TaskId> Tasks =>
    [
        .. Dependencies.SelectMany(d => new[] { d.From, d.To })
            .Concat(ParallelOk.SelectMany(p => new[] { p.A, p.B }))
            .Concat(Harmful.SelectMany(h => new[] { h.First, h.Then }))
            .Distinct(),
    ];
}
