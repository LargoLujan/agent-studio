namespace AgentStudio.Providers.Cli;

/// <summary>
/// The folder the commands that only ask a provider program about itself (<c>--version</c>, <c>auth status</c>) start in. It is a folder
/// of Agent Studio, empty and outside every project, so that nothing of a repository (a <c>.npmrc</c>, a <c>global.json</c>, a
/// <c>.python-version</c>) can influence the answer, and a program that looks for files next to its current folder finds none of the
/// repository's (ADR-0018, addendum of 2026-11-12). The composition gives it (<see cref="NeutralWorkingFolderOptions"/>, under the local
/// data of the app, <c>doctor\cwd</c>); without one the system folder is used, so nothing is written to the data of the person.
/// </summary>
public static class NeutralWorkingFolder
{
    /// <summary>
    /// <paramref name="configured"/>, created when it does not exist. Without one, or when it cannot be created, the system folder: one
    /// nobody of the repository writes to, and one every process can start in.
    /// </summary>
    public static string Prepare(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Environment.SystemDirectory;
        }

        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Environment.SystemDirectory;
        }
    }
}
