using System.IO;

namespace AgentStudio.Desktop.Services;

/// <summary>Base directory for per-user application data (settings, layouts). Injectable so tests use a temp folder.</summary>
public sealed record AppDataPaths(string BaseDirectory)
{
    public string SettingsFile => Path.Combine(BaseDirectory, "settings.json");

    public string LayoutsDirectory => Path.Combine(BaseDirectory, "layouts");

    /// <summary>%APPDATA%\AgentStudio.</summary>
    public static AppDataPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentStudio"));
}
