using AgentStudio.Application.Localization;

namespace AgentStudio.Application.Orchestration;

/// <summary>
/// What holds a project in this process (docs/15 D-05): a run or a planning of the orchestrator, a debate or an audit; and, from Phase 10, a remote operation of
/// Agent Studio itself (docs/21 D-25), which lasts seconds and is not work with agents.
/// </summary>
public enum ProjectActivityKind
{
    Run,
    Planning,
    Debate,
    Audit,

    /// <summary>
    /// The mailbox of the remote holds the project to launch git or to write to the log (docs/21 D-25). It shares the project with nothing and is not shown as work
    /// (the tray and the lock screen ignore it); whoever arrives while it holds it waits (<see cref="ProjectActivityEntry.EnterAsync"/>) instead of being refused.
    /// </summary>
    RemoteOperation,
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

/// <summary>
/// Entering a project when what may hold it is a remote operation of Agent Studio itself (docs/21 D-25): it lasts seconds, so the caller waits instead of
/// refusing. With a run, a planning, a debate or an audit in the way nothing changes: <see cref="IProjectActivity.TryEnter"/> answers at once, as before.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The four callers (the orchestrator, the coordinator of debates, the audit and the tests of HEAD) have written nothing when they call it, so waiting
/// leaves nothing half done.</item>
/// <item>The time comes from a <see cref="TimeProvider"/> (the wait and the cap), so a test advances it and never waits.</item>
/// <item>A caller never gets null while the project is held by a remote operation: it waits or, past the cap, is refused with <see cref="BusyRemoteCode"/>.</item>
/// </list>
/// </remarks>
public static class ProjectActivityEntry
{
    /// <summary>The longest a caller waits for a remote operation: more than the longest stretch of git of a publication (about 160 seconds).</summary>
    public static readonly TimeSpan RemoteWaitCap = TimeSpan.FromSeconds(180);

    /// <summary>How often it asks whether the project is free: <see cref="IProjectActivity"/> has no events.</summary>
    public static readonly TimeSpan RemoteWaitPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The code of the refusal when the cap passes: «Agent Studio está sincronizando con GitHub. Inténtalo en unos segundos».</summary>
    public const string BusyRemoteCode = "project.busy-remote";

    /// <summary>
    /// Waits while <see cref="IProjectActivity.Current"/> is <see cref="ProjectActivityKind.RemoteOperation"/>, up to <see cref="RemoteWaitCap"/>; then returns
    /// <c>TryEnter(projectId, kind)</c>, null included (the project is held by something else, and nothing waits for that).
    /// </summary>
    /// <exception cref="UserTextException">The remote operation still held the project after the cap, with <see cref="BusyRemoteCode"/>: nothing was written, so an approval is not spent.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public static async Task<IDisposable?> EnterAsync(IProjectActivity activity, string projectId, ProjectActivityKind kind, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(time);
        if (kind == ProjectActivityKind.RemoteOperation)
        {
            throw new ArgumentException("A remote operation does not wait for another one: it enters with TryEnter.", nameof(kind));
        }

        var started = time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (activity.TryEnter(projectId, kind) is { } lease)
            {
                return lease;
            }

            switch (activity.Current(projectId))
            {
                case null:
                    // Released between the two calls: ask again at once.
                    continue;
                case ProjectActivityKind.RemoteOperation:
                    if (time.GetElapsedTime(started) >= RemoteWaitCap)
                    {
                        throw new UserTextException(
                            UserText.Of(BusyRemoteCode),
                            $"Project '{projectId}' is still held by a remote operation after {RemoteWaitCap.TotalSeconds:0} seconds: nothing was started.");
                    }

                    await Task.Delay(RemoteWaitPoll, time, ct).ConfigureAwait(false);
                    break;
                default:
                    return null;
            }
        }
    }
}