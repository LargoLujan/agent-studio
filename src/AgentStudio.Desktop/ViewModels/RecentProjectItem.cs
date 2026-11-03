using System.IO;

namespace AgentStudio.Desktop.ViewModels;

public sealed record RecentProjectItem(string Path, string Name, string DisplayPath)
{
    public static RecentProjectItem From(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return new RecentProjectItem(path, name.Length > 0 ? name : path, PathDisplay.CollapseHome(path));
    }

    /// <summary>What a screen reader says of the project in a list (A1-bis): its name, never the members of the record.</summary>
    public override string ToString() => Name;
}
