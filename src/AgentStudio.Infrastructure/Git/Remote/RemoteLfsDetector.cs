namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The part of the decision «would this push carry Git LFS objects?» (docs/21 D-08) that is read from the disk and not from the commit: without the <c>pre-push</c> hook (it never runs, D-06) the objects would not go up and the pull
/// request would hold broken pointers. The decision is made by the commit that goes up: the attributes it holds are asked of git with <see cref="RemoteGitCommandLine.LfsAttributes"/>, a closed local command with no network. What git
/// reads besides the commit is <c>info/attributes</c> of the repository, which is no part of any commit and applies to every checkout: that file is read here. A configured <c>filter.lfs.*</c> (the system configuration of Git for Windows
/// sets it for everybody) is the other sign, and the one that tells whether the files of the commit can be pointers at all.
/// </summary>
internal static class RemoteLfsDetector
{
    private const long MaxFileBytes = 1024 * 1024;
    private const string FilterLfs = "filter=lfs";

    /// <summary>Whether <c>info/attributes</c> of the common directory or of the git directory of the worktree says <c>filter=lfs</c>. Ante la duda se rechaza: a file that cannot be read, or is too large, counts as «yes».</summary>
    public static bool InfoAttributesMention(RemoteConfigRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        try
        {
            return new[] { roots.CommonDirectory, roots.GitDirectory }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Any(directory => Mentions(Path.Combine(directory, "info", "attributes")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool Mentions(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return false;
        }

        if (info.Length > MaxFileBytes)
        {
            return true;
        }

        return File.ReadLines(file).Select(line => line.TrimStart()).Any(text => !text.StartsWith('#') && text.Contains(FilterLfs, StringComparison.OrdinalIgnoreCase));
    }
}
