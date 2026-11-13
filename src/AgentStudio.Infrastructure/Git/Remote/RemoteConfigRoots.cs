namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The folders that count as «inside the repository» for the inspection of the configuration (docs/21 D-05): the working tree, the git directory, the common directory, the main working tree git derives from
/// it, and the root of the worktrees of Agent Studio. A configuration file there is the repository's own (or one an agent with a shell could write), whatever the scope git gives its keys: an
/// <c>include</c> or an <c>includeIf</c> of the global configuration that points inside is judged by where its file lives.
/// </summary>
/// <remarks>
/// They are computed here with code of this folder, from what is on disk (<c>.git</c> as a folder, or as a file with <c>gitdir:</c> and the <c>commondir</c> of the administrative folder): the logic of the
/// internal layer lives inside <c>GitCommandRunner</c>, which this component does not use, and <c>rev-parse</c> is not in the closed list of commands. What cannot be read fails closed.
/// </remarks>
internal sealed class RemoteConfigRoots
{
    private const int MaxGitFileCharacters = 4096;
    private const int MaxParents = 64;

    private readonly string[] _roots;

    private RemoteConfigRoots(string repositoryRoot, string commonDirectory, string? gitDirectory, string workingDirectory, string[] roots)
    {
        RepositoryRoot = repositoryRoot;
        CommonDirectory = commonDirectory;
        GitDirectory = gitDirectory;
        WorkingDirectory = workingDirectory;
        _roots = roots;
    }

    /// <summary>The root of the working tree: where <c>.git</c> is, found from the folder of the project going up.</summary>
    public string RepositoryRoot { get; }

    /// <summary>The common directory of the repository (the one with the objects).</summary>
    public string CommonDirectory { get; }

    /// <summary>The administrative folder of this working tree when it is a linked worktree; null for the main one.</summary>
    public string? GitDirectory { get; }

    /// <summary>The folder git runs in: the one of the project. A relative origin path is relative to it.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Every root, as it is named and with its links resolved.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>The roots of the repository the folder <paramref name="projectRoot"/> belongs to, or null when they cannot be read (no folder, no repository, a <c>.git</c> file that says nothing usable).</summary>
    public static RemoteConfigRoots? TryCreate(string projectRoot, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(worktreeRoot) || !Path.IsPathFullyQualified(projectRoot))
        {
            return null;
        }

        try
        {
            var working = GitNaming.NormalizePath(projectRoot);
            if (!Directory.Exists(working))
            {
                return null;
            }

            var current = working;
            for (var level = 0; level < MaxParents && current is not null; level++, current = Path.GetDirectoryName(current))
            {
                var dotGit = Path.Combine(current, ".git");
                if (Directory.Exists(dotGit))
                {
                    return Build(current, GitNaming.NormalizePath(dotGit), null, working, worktreeRoot);
                }

                if (File.Exists(dotGit))
                {
                    return FromGitFile(current, dotGit, working, worktreeRoot);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether the configuration file an origin of <c>git config --show-origin</c> names (<c>file:&lt;path&gt;</c>) lives inside a root. Anything that cannot be understood counts as inside: it fails closed.</summary>
    public bool IsInside(string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        const string prefix = "file:";
        if (!origin.StartsWith(prefix, StringComparison.Ordinal))
        {
            // «command line:», «standard input:», «blob:…»: not a file of the repository.
            return false;
        }

        var path = origin[prefix.Length..];
        if (path.Length == 0)
        {
            return true;
        }

        try
        {
            var full = GitNaming.NormalizePath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));
            var resolved = GitCallbacks.ResolveFinalPath(full);
            return _roots.Any(root => Covers(full, root) || (resolved is not null && Covers(resolved, root)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool Covers(string path, string root) => GitNaming.SamePath(path, root) || GitNaming.IsUnder(path, root);

    private static RemoteConfigRoots? FromGitFile(string repositoryRoot, string gitFile, string working, string worktreeRoot)
    {
        var text = ReadStart(gitFile);
        const string marker = "gitdir:";
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (!firstLine.StartsWith(marker, StringComparison.Ordinal))
        {
            return null;
        }

        var named = firstLine[marker.Length..].Trim();
        if (named.Length == 0)
        {
            return null;
        }

        var admin = GitNaming.NormalizePath(Path.IsPathRooted(named) ? named : Path.Combine(repositoryRoot, named));
        if (!Directory.Exists(admin))
        {
            return null;
        }

        var common = admin;
        var commonFile = Path.Combine(admin, "commondir");
        if (File.Exists(commonFile))
        {
            var relative = ReadStart(commonFile).Split('\n', 2)[0].Trim();
            if (relative.Length == 0)
            {
                return null;
            }

            common = GitNaming.NormalizePath(Path.IsPathRooted(relative) ? relative : Path.Combine(admin, relative));
            if (!Directory.Exists(common))
            {
                return null;
            }
        }

        return Build(repositoryRoot, common, admin, working, worktreeRoot);
    }

    private static RemoteConfigRoots Build(string repositoryRoot, string common, string? gitDirectory, string working, string worktreeRoot)
    {
        var named = new List<string> { repositoryRoot, common, working, GitNaming.NormalizePath(worktreeRoot) };
        if (gitDirectory is not null)
        {
            named.Add(gitDirectory);
        }

        // The main working tree of a linked worktree: the parent of a common directory called .git (git's own rule).
        if (string.Equals(Path.GetFileName(common), ".git", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(common) is { } main)
        {
            named.Add(main);
        }

        var all = new List<string>(named.Count * 2);
        foreach (var root in named)
        {
            all.Add(root);
            if (GitCallbacks.ResolveFinalPath(root) is { } resolved)
            {
                all.Add(GitNaming.NormalizePath(resolved));
            }
        }

        return new RemoteConfigRoots(repositoryRoot, common, gitDirectory, working, [.. all.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static string ReadStart(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxGitFileCharacters];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
