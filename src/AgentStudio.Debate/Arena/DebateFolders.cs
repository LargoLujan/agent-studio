using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The folders of one debate under <see cref="DebateArenaOptions.DebatesRoot"/> (docs/15 D-08):
/// <c>&lt;root&gt;\&lt;repo8&gt;\&lt;debate6&gt;</c>, with <c>snapshot</c> (the copy of the commit the debate is about, shared
/// read-only by the participants and the judges) and <c>turns\&lt;turn&gt;-a&lt;attempt&gt;</c> (the material of one attempt
/// of one turn). The arena writes there and nothing else.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Nothing is created, written or deleted through a link (a junction or a symbolic link) from the root of the debates
/// down: every folder on the way is examined before and after it is created, and every file is new
/// (<see cref="FileMode.CreateNew"/>), so a link planted in its place is never followed. The folders above the root are the
/// user's own and are not examined.</item>
/// <item>The copy comes from <see cref="IWorktreeManager.ArchiveCommitAsync"/>: the tree of the commit as committed, with
/// regular files and folders only, read from the objects of the project's repository and never through a worktree. A copy
/// that failed half-way is removed; one that is missing is extracted again from the same commit (D-04).</item>
/// <item>Removing the folders of a debate (when it closes) examines the way first and never follows a link inside.</item>
/// </list>
/// </remarks>
public sealed class DebateFolders
{
    private const string SnapshotFolder = "snapshot";
    private const string TurnsFolder = "turns";

    private readonly string _root;

    public DebateFolders(DebateArenaOptions options, string repositoryRoot, RunId debate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DebatesRoot));
        Folder = Path.Combine(_root, RepositoryKey(repositoryRoot), DebateIds.Key(debate));
    }

    /// <summary><c>&lt;root&gt;\&lt;repo8&gt;\&lt;debate6&gt;</c>.</summary>
    public string Folder { get; }

    /// <summary>The copy of the commit.</summary>
    public string Snapshot => Path.Combine(Folder, SnapshotFolder);

    /// <summary>
    /// The first eight hexadecimal digits of the SHA-256 of the repository's root, normalized and in lowercase, as the
    /// worktrees name their folder, so one root holds the debates of many repositories.
    /// </summary>
    public static string RepositoryKey(string repositoryRoot)
    {
        var full = Path.GetFullPath(repositoryRoot);
        var root = Path.GetPathRoot(full);
        var normalized = full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())))[..8];
    }

    /// <summary>Whether the copy of the commit is there (and is no link).</summary>
    public bool HasSnapshot()
    {
        var folder = new DirectoryInfo(Snapshot);
        return folder.Exists && !IsLink(folder);
    }

    /// <summary>
    /// Extracts <paramref name="commit"/> into a new, empty <see cref="Snapshot"/>; a folder left by an earlier extraction is
    /// removed first, so the copy is always exactly the commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    public async Task ExtractSnapshotAsync(IWorktreeManager worktrees, string repositoryRoot, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        Remove(Snapshot);
        CreateFolder(Snapshot);
        try
        {
            await worktrees.ArchiveCommitAsync(repositoryRoot, commit, Snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Remove(Snapshot);
            throw;
        }

        RefuseLinks(Snapshot);
    }

    /// <summary>A new, empty folder for one attempt of one turn: <c>turns\&lt;turn&gt;-a&lt;attempt&gt;</c>.</summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    public string CreateTurnFolder(string turn, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turn);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var folder = Path.Combine(Folder, TurnsFolder, Segment(turn) + "-a" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Remove(folder);
        CreateFolder(folder);
        return folder;
    }

    /// <summary>Writes a new file in a folder of this debate, never through a link.</summary>
    /// <exception cref="InvalidOperationException">The file would fall outside the folder of the debate, or a folder on the way is a link.</exception>
    public async Task WriteNewFileAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The file '{full}' would fall outside the folder of the debate '{Folder}'.");
        }

        var parent = Path.GetDirectoryName(full)!;
        CreateFolder(parent);
        var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes every folder of the debate (the copy and the turns), when it closes. Never through a link.</summary>
    public void RemoveAll() => Remove(Folder);

    /// <summary>A safe name for a folder: <c>[A-Za-z0-9_-]</c>, at most 64 characters.</summary>
    private static string Segment(string text)
    {
        var safe = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return safe.Length == 0 ? "_" : safe.Length > 64 ? safe[..64] : safe;
    }

    private void CreateFolder(string folder)
    {
        RefuseLinks(folder);
        Directory.CreateDirectory(folder);
        RefuseLinks(folder); // again, now that the folders exist: one could have been swapped meanwhile
    }

    private void Remove(string folder)
    {
        RefuseLinks(folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Refuses a path that goes through a link anywhere from the root of the debates, included, down to <paramref name="path"/>,
    /// included (the pattern of the dossier package of Phase 3).
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    private void RefuseLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The folder of the debate '{path}' goes through '{current.FullName}', which is a link (a junction or a symbolic link): nothing of a debate is written or deleted through one.");
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), _root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>A link, even one whose target is gone; a folder that does not exist is not one.</summary>
    private static bool IsLink(DirectoryInfo folder) =>
        folder.LinkTarget is not null || (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
