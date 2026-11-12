namespace AgentStudio.Providers.Cli;

/// <summary>
/// The folder the commands that only ask a provider program about itself (<c>--version</c>, <c>auth status</c>) start in. It is a folder
/// of Agent Studio, empty and outside every project, so that nothing of a repository (a <c>.npmrc</c>, a <c>global.json</c>, a
/// <c>.python-version</c>) can influence the answer, and a program that looks for files next to its current folder finds none of the
/// repository's (ADR-0018, addendum of 2026-11-12).
/// </summary>
public static class NeutralWorkingFolder
{
    /// <summary><c>%LOCALAPPDATA%\AgentStudio\doctor\cwd</c>: where Auto Doctor (docs/20 D-02) starts its own commands too.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "doctor", "cwd");

    /// <summary>
    /// <paramref name="configured"/> or <see cref="DefaultPath"/>, created when it does not exist. When it cannot be created the system
    /// folder is the answer: a folder nobody of the repository writes to, and one every process can start in.
    /// </summary>
    public static string Prepare(string? configured)
    {
        var folder = string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Environment.SystemDirectory;
        }
    }
}
