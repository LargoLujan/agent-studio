using AgentStudio.Application.Projects;

namespace AgentStudio.Application.Diagnostics;

/// <summary>
/// The project at <paramref name="projectRoot"/> as the workspace has it open in this session, or null when nothing opened it yet. It never opens one: opening creates
/// <c>.agentstudio/</c>, takes the lock of the project and edits <c>.git/info/exclude</c>, and Auto Doctor, which only looks, must not be the first to write there.
/// </summary>
public interface IOpenProjects
{
    ProjectContext? Find(string projectRoot);
}

/// <summary>The project of a run of Auto Doctor, open or not (docs/20 D-03).</summary>
public static class DoctorProject
{
    /// <summary>A project that nothing has opened in this session: it has its folder and no identity. What needs the log of the project (the history, the setup) is «sin comprobar» for it.</summary>
    public static ProjectContext Unopened(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return new ProjectContext(string.Empty, projectRoot);
    }

    /// <summary>Whether the workspace has opened the project, so it has an identity and a log.</summary>
    public static bool IsOpen(ProjectContext? project) => project is { ProjectId.Length: > 0 };
}
