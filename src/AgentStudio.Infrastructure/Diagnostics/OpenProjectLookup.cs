using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Persistence;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>Asks the databases that are open in this session whether a project is one of them. It opens nothing and writes nothing (see <see cref="IOpenProjects"/>).</summary>
public sealed class OpenProjectLookup(ProjectDatabases databases) : IOpenProjects
{
    public ProjectContext? Find(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            return databases.FindByRoot(root) is { } open ? new ProjectContext(open.ProjectId, open.RootPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
