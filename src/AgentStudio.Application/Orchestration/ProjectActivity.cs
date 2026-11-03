namespace AgentStudio.Application.Orchestration;

/// <summary>What holds a project in this process (docs/15 D-05): a run or a planning of the orchestrator, a debate or an audit.</summary>
public enum ProjectActivityKind
{
    Run,
    Planning,
    Debate,
    Audit,
}

/// <summary>
/// One activity with agents per project in this process (docs/15 D-05). A debate, an audit and a run of the same project do
/// not go on at once: the engine compares the user's repository around each step and all of them write the same log.
/// It is also the <see cref="IRunActivity"/> the memory waits on: while a debate or an audit holds the project the memory
/// does not write to the log (the head would move between showing a verdict and using it) and is not exported.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="TryEnter"/> returns null, and nothing is entered, when another activity holds the project; the caller
/// refuses with the reason (<see cref="Current"/>) before writing anything. The coordinator of debates, the audit and the
/// orchestrator enter before their first entry.</item>
/// <item>The orchestrator's own activities, its planning and its run, may share the project between them as they do today
/// (counted); a debate or an audit shares it with nothing, not even another of its kind.</item>
/// <item>Disposing the lease releases the project at once and only once. A run releases its project before its end can be
/// seen (the end of its events or its completion, as <c>OrchestrationRun</c> does), so what reacts to that end, such as the
/// derivation of the memory, finds it free.</item>
/// <item>The implementation depends on nothing, so the container builds it before the memory and the orchestrator without
/// a cycle (the orchestrator needs the context packer, the packer the memory, and the memory this).</item>
/// </list>
/// </remarks>
public interface IProjectActivity : IRunActivity
{
    /// <summary>Enters <paramref name="kind"/> in the project, or returns null when another activity holds it.</summary>
    IDisposable? TryEnter(string projectId, ProjectActivityKind kind);

    /// <summary>What holds the project now, or null when nothing does (a run wins over a planning that shares it).</summary>
    ProjectActivityKind? Current(string projectId);
}
