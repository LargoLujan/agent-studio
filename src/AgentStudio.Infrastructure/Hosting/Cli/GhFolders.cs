namespace AgentStudio.Infrastructure.Hosting.Cli;

/// <summary>
/// The two folders of Agent Studio that <c>gh</c> uses (docs/21 D-11): the working folder, empty and outside every project (<c>gh</c> never starts inside a repository, whose
/// configuration is not trusted), and the temporary folder (<c>TEMP</c> and <c>TMP</c>). Nothing is ever written to either of them by the app.
/// </summary>
public sealed record GhFolders(string WorkingFolder, string TempFolder)
{
    /// <summary>The folders under <paramref name="localBase"/> (<c>%LOCALAPPDATA%\AgentStudio</c>): <c>hosting\cwd</c> and <c>hosting\tmp</c>.</summary>
    public static GhFolders Under(string localBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBase);
        var hosting = Path.Combine(localBase, "hosting");
        return new GhFolders(Path.Combine(hosting, "cwd"), Path.Combine(hosting, "tmp"));
    }

    /// <summary>Creates the folders when they do not exist.</summary>
    /// <exception cref="IOException">A folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder could not be created.</exception>
    public void Ensure()
    {
        Directory.CreateDirectory(WorkingFolder);
        Directory.CreateDirectory(TempFolder);
    }
}
