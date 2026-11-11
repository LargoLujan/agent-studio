namespace AgentStudio.Providers.Cli;

/// <summary>
/// Where the adapters and the Codex quota reader make their per-run folders (<see cref="RunTempFolder"/>). The composition
/// root registers it; without one, the adapters use <see cref="RunTempFolder.BaseDirectory"/>, the folder of the user. A test
/// passes a temporary folder of its own, so it neither writes the real <c>%LOCALAPPDATA%</c> (Windows denies it at Low
/// integrity) nor leaves anything in it.
/// </summary>
/// <param name="BaseDirectory">The parent of every per-run folder.</param>
public sealed record RunTempFolderOptions(string BaseDirectory)
{
    /// <summary><c>%LOCALAPPDATA%\AgentStudio\runs</c>, the folder of today.</summary>
    public static RunTempFolderOptions ForCurrentUser() => new(RunTempFolder.BaseDirectory);
}
