using System.IO;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>Formats paths for display. Collapsing the profile folder keeps user names out of screenshots.</summary>
public static class PathDisplay
{
    public static string CollapseHome(string path) =>
        CollapseHome(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Replaces a leading <paramref name="home"/> folder with "~", only on a folder boundary.</summary>
    public static string CollapseHome(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        var root = Path.TrimEndingDirectorySeparator(home);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return "~";
        }

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[root.Length..]
            : path;
    }
}
