using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Lock;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// What the lock screen says goes on (docs/16 D-13), read from the registry the tray follows: the kind of each piece of work
/// going on, a project counted once (<see cref="BackgroundWorkRegistry.Active"/>), and never which project. Read it on the UI
/// thread, where the registry lives: the lock screen refreshes there.
/// </summary>
public sealed class BackgroundWorkInProgress(BackgroundWorkRegistry work) : IWorkInProgress
{
    public IReadOnlyList<ProjectActivityKind> Current() => [.. work.Active.Select(w => KindOf(w.Kind))];

    private static ProjectActivityKind KindOf(BackgroundWorkKind kind) => kind switch
    {
        BackgroundWorkKind.Run => ProjectActivityKind.Run,
        BackgroundWorkKind.Debate => ProjectActivityKind.Debate,
        BackgroundWorkKind.Planning => ProjectActivityKind.Planning,
        _ => ProjectActivityKind.Audit,
    };
}
