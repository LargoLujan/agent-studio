using AgentStudio.Application.Diagnostics;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// The questions of the project section that the file system and the PATH answer (docs/20 D-03): it lists names and asks whether things exist, and
/// opens no file of the project and starts no process.
/// </summary>
public sealed class ProjectEnvironmentProbe(Func<string?>? pathVariable = null) : IProjectEnvironmentProbe
{
    private const int MaxHooks = 50;

    // The container files of docs/20 D-05, relative to the root. Only whether they exist.
    private static readonly string[] ContainerFiles =
    [
        "Dockerfile", "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml",
        ".devcontainer/devcontainer.json", ".devcontainer.json",
    ];

    private readonly Func<string?> _path = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));

    public ProjectLayout ReadLayout(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var git = Path.Combine(projectRoot, ".git");
        try
        {
            var isFolder = Directory.Exists(git);
            var hasGit = isFolder || File.Exists(git);
            if (!isFolder || IsLink(git))
            {
                return new ProjectLayout(hasGit, false, []);
            }

            var hooks = Path.Combine(git, "hooks");
            if (!Directory.Exists(hooks))
            {
                return new ProjectLayout(true, true, []);
            }

            if (IsLink(hooks))
            {
                return new ProjectLayout(true, false, []);
            }

            var names = Directory.EnumerateFiles(hooks)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !n.EndsWith(".sample", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxHooks)
                .ToList();
            return new ProjectLayout(true, true, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ProjectLayout(false, false, []);
        }
    }

    public IReadOnlyList<string> PathFoldersInside(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var roots = UntrustedPaths.Resolve([projectRoot]);
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(_path()))
        {
            return [];
        }

        var inside = new List<string>();
        foreach (var raw in _path()!.Split(Path.PathSeparator))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length > 0 && Path.IsPathFullyQualified(directory) && UntrustedPaths.IsInside(directory, roots) && !inside.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                inside.Add(directory);
            }
        }

        return inside;
    }

    public bool DeclaresContainers(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        try
        {
            return ContainerFiles.Any(f => File.Exists(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
