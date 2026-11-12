using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Team;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// The branch, the HEAD and the changes not committed of the repository, read by the hardened git layer
/// (<see cref="IWorktreeManager.InspectAsync"/>, with its review of the configuration): a narrow port so the node of the
/// repository never reaches for git by itself, and a test can answer without a repository.
/// </summary>
public interface IRepositoryStateReader
{
    Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken);

    /// <summary>
    /// The remotes of the repository as the hardened git layer lists them (<see cref="IRepositoryRemoteReader"/>); null when this reader
    /// has no such layer, and the node then reads the configuration file itself, bounded.
    /// </summary>
    Task<RemoteListing?> ListRemotesAsync(string repoRoot, CancellationToken cancellationToken) => Task.FromResult<RemoteListing?>(null);
}

/// <summary>The hardened git layer as the reader of the state of the repository and of its remotes.</summary>
public sealed class WorktreeRepositoryStateReader(IWorktreeManager worktrees, IRepositoryRemoteReader? remotes = null) : IRepositoryStateReader
{
    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) =>
        worktrees.InspectAsync(repoRoot, cancellationToken);

    public async Task<RemoteListing?> ListRemotesAsync(string repoRoot, CancellationToken cancellationToken) =>
        remotes is null ? null : await remotes.ListRemotesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
}

/// <summary>What was read of the repository: the node, or why there is none.</summary>
public sealed record RepositoryReading(RepositoryNode? Node, string? Problem)
{
    /// <summary>Why the remotes were not read (the configuration file is too big); null when they were.</summary>
    public string? RemotesNote { get; init; }

    public static RepositoryReading Of(RepositoryNode node) => new(node, null);

    public static RepositoryReading Failed(string problem) => new(null, problem);
}

/// <summary>
/// Reads the node of the repository of the Team layer (docs/17 D-10 and F13-3) without network: nothing is fetched and nothing
/// is asked of a remote.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The branch, the short HEAD and the changes not committed come from the hardened git layer (<see cref="IRepositoryStateReader"/>),
/// which reviews the configuration of the repository before it runs git.</item>
/// <item>The remotes come from <see cref="IRepositoryRemoteReader"/> (<c>ListRemotesAsync</c> of the hardened git layer: local
/// configuration only, no <c>include</c> followed, bounded output, fail closed). Without that layer (a reader that does not give it)
/// they are read from the text of the configuration file of the repository, with no git process, and the cap of
/// <see cref="MaxConfigBytes"/> is applied while reading. Each URL is cleaned with <see cref="RepositoryRemote.Cleaned"/> the moment it is
/// read: no user and no password is ever held, shown or saved. Only <c>[remote "name"]</c> and its <c>url</c> are read; the
/// files an <c>include</c> names are not followed.</item>
/// <item>The main folders are, by default, the top-level ones with most files, without <c>.git</c>, <c>bin</c>, <c>obj</c> nor
/// <c>node_modules</c> (the folders that begin with a dot are left out, too).</item>
/// </list>
/// </remarks>
public sealed class RepositoryReader(IRepositoryStateReader? state)
{
    /// <summary>How many main folders the node proposes by default.</summary>
    public const int DefaultMainFolders = 4;

    /// <summary>The most files looked at to choose the folders and the stack: a large repository is never walked in full.</summary>
    public const int MaxFilesLooked = 20_000;

    public async Task<RepositoryReading> ReadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (state is null)
        {
            return RepositoryReading.Failed(RepositoryProblems.NotComposed);
        }

        RepositoryState inspected;
        try
        {
            inspected = await state.InspectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The git layer is behind a port: why it failed is shown on the node, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return RepositoryReading.Failed(WithoutCredentials(ex.Message));
        }

        var root = inspected.Root;
        var (remotes, remotesNote) = await ReadRemotesAsync(state, root, cancellationToken).ConfigureAwait(false);
        var (folders, paths) = Look(projectRoot);
        var stack = ProjectStackDetector.Detect(paths);
        return RepositoryReading.Of(new RepositoryNode(
            Path.GetFullPath(projectRoot),
            inspected.HeadBranch,
            inspected.HeadCommit.Length > 7 ? inspected.HeadCommit[..7] : inspected.HeadCommit,
            remotes,
            inspected.HasUncommittedChanges,
            stack.Count == 0 ? null : string.Join(", ", stack),
            folders)) with { RemotesNote = remotesNote };
    }

    /// <summary>
    /// A message of the git layer without the user and the password of any address in it: why the repository could not be read is
    /// shown on the node, and no credential may be there either (docs/17 D-10, F13-3).
    /// </summary>
    internal static string WithoutCredentials(string message) =>
        System.Text.RegularExpressions.Regex.Replace(
            message ?? string.Empty,
            @"[^\s'""()<>]+",
            match => match.Value.Contains("://", StringComparison.Ordinal) || match.Value.Contains('@', StringComparison.Ordinal)
                ? RepositoryRemote.WithoutCredentials(match.Value)
                : match.Value,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    /// <summary>
    /// The remotes through the hardened git layer when <paramref name="source"/> has it (P51): a refusal gives none (and, for an answer
    /// past the bound, says so), never a fallback to the file. Without that layer, the bounded read of the configuration file.
    /// </summary>
    private static async Task<(IReadOnlyList<RepositoryRemote> Remotes, string? Note)> ReadRemotesAsync(IRepositoryStateReader source, string root, CancellationToken cancellationToken)
    {
        RemoteListing? listing = null;
        try
        {
            listing = await source.ListRemotesAsync(root, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The git layer is behind a port: a failure there reads as «no remotes», never as an error on the node.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return ([], null);
        }

        if (listing is null)
        {
            var remotes = ReadRemotes(root, out var note);
            return (remotes, note);
        }

        return (listing.Remotes, listing.RefusalCode == RemoteListing.OutputTooBig ? RepositoryProblems.ConfigurationTooBig : null);
    }

    /// <summary>The remotes of the repository at <paramref name="root"/>, with their URL clean, in the order of the file.</summary>
    public static IReadOnlyList<RepositoryRemote> ReadRemotes(string root) => ReadRemotes(root, out _);

    /// <summary>The most a configuration file may weigh to be read for its remotes: a larger one is not read, and the node says so.</summary>
    public const long MaxConfigBytes = 1_000_000;

    /// <summary>The remotes, and when the configuration was not read because of its size, <paramref name="note"/> says it (a code of <see cref="RepositoryProblems"/>).</summary>
    public static IReadOnlyList<RepositoryRemote> ReadRemotes(string root, out string? note)
    {
        note = null;
        var config = ConfigFileOf(root);
        if (config is null)
        {
            return [];
        }

        // The cap is applied while reading (never more than the cap plus one byte is held), not by a check before it: a file that
        // grows between the check and the read cannot be read in full.
        string text;
        try
        {
            using var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaxConfigBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += read;
            }

            if (length > MaxConfigBytes)
            {
                note = RepositoryProblems.ConfigurationTooBig;
                return [];
            }

            // Through a reader, so a UTF-8 byte order mark is dropped (as File.ReadAllLines did) and does not spoil the first section.
            using var reader = new StreamReader(new MemoryStream(buffer, 0, length), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var lines = text.Split('\n');

        var remotes = new List<RepositoryRemote>();
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                current = RemoteName(line);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !string.Equals(line[..equals].Trim(), "url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = line[(equals + 1)..].Trim().Trim('"');
            if (url.Length > 0 && remotes.All(r => !string.Equals(r.Name, current, StringComparison.Ordinal)))
            {
                try
                {
                    remotes.Add(RepositoryRemote.Cleaned(current, url));
                }
                catch (ArgumentException)
                {
                    // A name or an address git itself would refuse: the node has no use for it.
                }
            }
        }

        return remotes;
    }

    /// <summary>The name of a <c>[remote "name"]</c> section header; null for any other section.</summary>
    private static string? RemoteName(string header)
    {
        const string prefix = "[remote \"";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = header.IndexOf('"', prefix.Length);
        return end > prefix.Length ? header[prefix.Length..end] : null;
    }

    /// <summary>The configuration file of the repository: <c>.git/config</c>, or that of the common directory when <c>.git</c> is the file of a worktree.</summary>
    private static string? ConfigFileOf(string root)
    {
        var dot = Path.Combine(root, ".git");
        string? gitDirectory = null;
        if (Directory.Exists(dot))
        {
            gitDirectory = dot;
        }
        else if (File.Exists(dot))
        {
            try
            {
                var pointer = File.ReadAllText(dot).Trim();
                const string marker = "gitdir:";
                if (pointer.StartsWith(marker, StringComparison.Ordinal))
                {
                    gitDirectory = Path.GetFullPath(Path.Combine(root, pointer[marker.Length..].Trim()));
                    var common = Path.Combine(gitDirectory, "commondir");
                    if (File.Exists(common))
                    {
                        gitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(common).Trim()));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        var config = gitDirectory is null ? null : Path.Combine(gitDirectory, "config");
        return config is not null && File.Exists(config) ? config : null;
    }

    /// <summary>The main folders by default and the relative paths looked at, to detect the stack.</summary>
    private static (IReadOnlyList<string> Folders, IReadOnlyList<string> Paths) Look(string projectRoot)
    {
        var counts = new List<(string Name, int Files)>();
        var paths = new List<string>();
        var looked = 0;
        try
        {
            foreach (var top in Directory.EnumerateFiles(projectRoot).Take(500))
            {
                paths.Add(Path.GetFileName(top));
            }

            foreach (var directory in Directory.EnumerateDirectories(projectRoot).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || RepositoryNode.ExcludedByDefault.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var files = 0;
                foreach (var file in EnumerateFilesOutside(directory))
                {
                    if (++looked > MaxFilesLooked)
                    {
                        break;
                    }

                    files++;
                    paths.Add(Path.GetRelativePath(projectRoot, file));
                }

                counts.Add((name, files));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could not be read is not counted: the node shows what it could.
        }

        var folders = counts
            .Where(c => c.Files > 0)
            .OrderByDescending(c => c.Files)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(DefaultMainFolders)
            .Select(c => c.Name + "/")
            .Order(StringComparer.Ordinal)
            .ToList();
        return (folders, paths);
    }

    private static IEnumerable<string> EnumerateFilesOutside(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                children = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!RepositoryNode.ExcludedByDefault.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }
        }
    }
}

/// <summary>Why the node of the repository could not be read: the code the screen turns into a sentence.</summary>
public static class RepositoryProblems
{
    /// <summary>The configuration file of the repository is too big to read its remotes: it is not read, and the node says so.</summary>
    public const string ConfigurationTooBig = "repository.configuration-too-big";

    /// <summary>The repository is being read: the node says so until the answer comes.</summary>
    public const string Reading = "repository.reading";

    /// <summary>The git layer was not composed: there is nothing to read the repository with.</summary>
    public const string NotComposed = "repository.not-composed";
}
