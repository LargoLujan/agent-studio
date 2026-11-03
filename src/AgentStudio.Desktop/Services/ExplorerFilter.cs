namespace AgentStudio.Desktop.Services;

/// <summary>Which entries the project explorer hides.</summary>
public static class ExplorerFilter
{
    private static readonly HashSet<string> HiddenDirectories =
        new([".git", "bin", "obj", "node_modules", ".vs"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DatabaseSuffixes = [".db", ".db-wal", ".db-shm", ".db-journal"];

    /// <param name="name">File or directory name (no path).</param>
    /// <param name="isDirectory">True for directories.</param>
    /// <param name="parentName">Name of the containing directory.</param>
    public static bool IsHidden(string name, bool isDirectory, string parentName)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (isDirectory)
        {
            return HiddenDirectories.Contains(name);
        }

        // In a git worktree ".git" is a file that points at the real repository folder.
        if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // .agentstudio/*.db: SQLite state (and its sidecar files) is not meant to be opened as text.
        return string.Equals(parentName, ".agentstudio", StringComparison.OrdinalIgnoreCase)
            && DatabaseSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
