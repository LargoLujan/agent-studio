namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The two places of Agent Studio the remote executor of git uses (docs/21 D-06), both outside every repository: the empty file every command receives as <c>core.hooksPath</c>, and the
/// folder of <c>TEMP</c> and <c>TMP</c>. The file is empty on purpose and a file and not a folder (the reasoning and the measurement of the internal layer, <c>GitCommandRunner.NoHooksFileName</c>):
/// git looks for each hook below that path and nothing can be below a file, so no hook runs and git-lfs cannot install its own there.
/// </summary>
/// <param name="HooksFile">The empty file.</param>
/// <param name="TempFolder">The temporary folder that <c>TEMP</c> and <c>TMP</c> point to.</param>
internal sealed record RemoteGitFolders(string HooksFile, string TempFolder)
{
    /// <summary>The folders under <paramref name="localBase"/> (<c>%LOCALAPPDATA%\AgentStudio</c>): <c>hosting\no-hooks</c> and <c>hosting\tmp</c>.</summary>
    public static RemoteGitFolders Under(string localBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBase);
        var hosting = Path.Combine(localBase, "hosting");
        return new RemoteGitFolders(Path.Combine(hosting, "no-hooks"), Path.Combine(hosting, "tmp"));
    }

    /// <summary>The folders of the person: <c>%LOCALAPPDATA%\AgentStudio\hosting</c>.</summary>
    public static RemoteGitFolders Default =>
        Under(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio"));

    /// <summary>
    /// Creates the temporary folder and the empty file when they are missing, and checks the file every time: it must be a file with no content and no link. Anything else (a folder, a link, a file with
    /// content) is refused and nothing is emptied or replaced: the person looks at it first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path of the hooks file is not an empty file.</exception>
    /// <exception cref="IOException">A folder or the file could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder or the file could not be created.</exception>
    public void Ensure()
    {
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        if (!File.Exists(HooksFile) && !Directory.Exists(HooksFile))
        {
            try
            {
                File.Open(HooksFile, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            }
            catch (IOException) when (File.Exists(HooksFile))
            {
                // Another command created it first; what is there is checked below.
            }
        }

        var info = new FileInfo(HooksFile);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != 0)
        {
            throw new InvalidOperationException(
                $"'{HooksFile}' must be an empty file: it is passed to git as core.hooksPath so that no hook runs. It is a folder, a link or a file with content; refusing to run git until it is deleted.");
        }
    }
}
