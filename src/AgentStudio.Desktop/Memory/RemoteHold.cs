using AgentStudio.Application.Orchestration;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// Whether what holds a project is only a remote operation of Agent Studio itself (docs/21 D-25, Phase 10): it lasts seconds, and the writes of the memory and of the
/// trust of the journal wait for it as they wait for a run, but the texts of the screen must not say that a run, a debate or an audit is going on, which would be false.
/// </summary>
internal static class RemoteHold
{
    public static bool Holds(IRunActivity? activity, string projectId) =>
        activity is IProjectActivity projects && projects.Current(projectId) == ProjectActivityKind.RemoteOperation;
}
