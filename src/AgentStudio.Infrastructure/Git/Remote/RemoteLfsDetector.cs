namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// Whether the push would carry Git LFS objects (docs/21 D-08): without the <c>pre-push</c> hook (it never runs, D-06) the objects would not go up and the pull request would hold broken pointers. There is
/// no <c>git lfs</c> to ask and no command outside the closed list, so the second sign, next to a configured <c>filter.lfs.*</c> (which the system configuration of Git for Windows sets for everybody), is
/// the attributes: a <c>.gitattributes</c> of the working tree, or <c>info/attributes</c>, that says <c>filter=lfs</c>. Ante la duda se rechaza: a file that cannot be read, is too large or a tree too big to walk counts as «yes».
/// </summary>
/// <remarks>
/// This reads the files of the working tree, not those of the commit that goes up (that would need <c>ls-tree</c> or <c>show</c>, which are not in the closed list), so it can say «no» for a commit
/// that has LFS files under a <c>.gitattributes</c> that was not checked out. It is the narrowest sign that needs no more commands, and it is said in §13 of docs/21.
/// </remarks>
internal static class RemoteLfsDetector
{
    private const int MaxDirectories = 20_000;
    private const int MaxAttributeFiles = 500;
    private const long MaxFileBytes = 1024 * 1024;
    private const string FilterLfs = "filter=lfs";

    public static bool UsesLfs(RemoteConfigRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        try
        {
            foreach (var info in new[] { roots.CommonDirectory, roots.GitDirectory }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Mentions(Path.Combine(info, "info", "attributes")))
                {
                    return true;
                }
            }

            return WalkAttributes(roots.RepositoryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool WalkAttributes(string root)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var directories = 0;
        var files = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            if (++directories > MaxDirectories)
            {
                return true;
            }

            var attributes = Path.Combine(directory, ".gitattributes");
            if (File.Exists(attributes))
            {
                if (++files > MaxAttributeFiles || Mentions(attributes))
                {
                    return true;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or "node_modules" || new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                pending.Enqueue(child);
            }
        }

        return false;
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

        foreach (var line in File.ReadLines(file))
        {
            var text = line.TrimStart();
            if (!text.StartsWith('#') && text.Contains(FilterLfs, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
