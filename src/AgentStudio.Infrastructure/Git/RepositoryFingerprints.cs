using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Common;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// The fingerprints the capture of the user's repository takes (<see cref="GitWorktreeManager.CaptureUserRepositoryStateAsync"/>):
/// the content of the paths <c>git status</c> lists and the files git reads or runs from the repository. Read-only,
/// never through a link, and bounded by a <see cref="CaptureBudget"/>: the content of a file, and of every file inside an
/// untracked or ignored folder, is hashed (SHA-256), so a same-length swap with the timestamp restored is not invisible
/// (N-07); a file over the per-file cap, everything past the total budget, and anything that cannot be read are marked
/// indeterminate instead of trusted by size and time. The metadata is always taken as well, for a folder that of every
/// entry in it (R3-02), so a path that could not be verified is still compared by it.
/// </summary>
/// <remarks>
/// A link is recognised by its reparse-point attribute, which the folder listing already carries: on Windows every
/// symbolic link and junction is one. Asking each file for its link target would open every file listed.
/// </remarks>
internal static class RepositoryFingerprints
{
    /// <summary>How many files of the hooks folder are fingerprinted.</summary>
    internal const int MaxHookFiles = 512;

    /// <summary>How many levels below the hooks folder its files are fingerprinted (husky keeps a helper one level down).</summary>
    private const int HookDepth = 2;

    /// <summary>One level, every entry, and an error when the folder cannot be listed: never an empty listing for it (R3-03).</summary>
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.None,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// The fingerprint of a folder the capture could not list (R3-03): without metadata, so it can never be compared and
    /// always blocks. A folder that cannot be listed is never taken for an empty one.
    /// </summary>
    internal static FileFingerprint Unlisted { get; } = new(null, string.Empty, "the folder could not be listed");

    /// <summary>
    /// The fingerprint of each path of the status, and of each tracked path the status cannot show (repository relative,
    /// '/' separators, a folder ending with '/'): a file by the SHA-256 of its bytes, a link by its target, a folder (an
    /// untracked or ignored one of the status, or a submodule checked out) by a digest of the paths and contents of its
    /// entries and another of their metadata (<see cref="OfFolderAsync"/>). Paths that no longer exist, and tracked
    /// folders the status lists, get none.
    /// </summary>
    /// <remarks>
    /// Only folder content can run out of <paramref name="budget"/>: the individual files (those the status lists, the
    /// user's own work, and the hidden ones) are hashed first and only against the per-file cap, so a big ignored folder
    /// can never make one of them indeterminate. Folders, of the status and submodules alike, are hashed last, in sorted
    /// order, against the shared total budget and the shared metadata cap, so the same folders fall outside them in two
    /// captures of the same repository.
    /// </remarks>
    internal static async Task<(IReadOnlyDictionary<string, FileFingerprint> Status, IReadOnlyDictionary<string, FileFingerprint> Hidden)> OfPathsAsync(
        string root, IEnumerable<string> statusPaths, IEnumerable<string> hiddenPaths, CaptureBudget budget, CancellationToken ct)
    {
        var status = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);
        var hidden = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);
        var links = new LinkCache(root);
        var sorted = statusPaths.Distinct(StringComparer.Ordinal).Select(path => (Path: path, Into: status))
            .Concat(hiddenPaths.Distinct(StringComparer.Ordinal).Select(path => (Path: path, Into: hidden)))
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ThenBy(p => ReferenceEquals(p.Into, status) ? 0 : 1)
            .ToList();

        // Files first, each against the per-file cap only: they never spend the total budget.
        var perFile = Budget.PerFileOnly(budget.MaxFileBytes);
        foreach (var (path, into) in sorted.Where(p => !p.Path.EndsWith('/')))
        {
            await AddAsync(path, into, isFolder: false, perFile).ConfigureAwait(false);
        }

        // Folders last, against the shared total budget, in sorted order.
        var folders = new Budget(budget);
        foreach (var (path, into) in sorted.Where(p => p.Path.EndsWith('/')))
        {
            await AddAsync(path, into, isFolder: true, folders).ConfigureAwait(false);
        }

        return (status, hidden);

        async Task AddAsync(string path, SortedDictionary<string, FileFingerprint> into, bool isFolder, Budget spend)
        {
            if (FullPath(root, path) is not { } full)
            {
                return;
            }

            var fingerprint = links.HasLinkOnTheWay(full)
                ? Link(full)
                : isFolder ? await OfFolderAsync(full, spend, ct).ConfigureAwait(false) : await OfFileAsync(full, spend, ct).ConfigureAwait(false);
            if (fingerprint is not null)
            {
                into[path] = fingerprint;
            }
        }
    }

    /// <summary>
    /// The files git takes hooks from in <paramref name="folder"/>: those directly in it and in its subfolders (two
    /// levels down), or only those directly in it when it is the root of the working tree or holds it, where the rest is
    /// the user's tree that the status already covers. The folder itself is read as git reads it (through a link if it
    /// is one); links inside it are listed, never followed. <c>Unlisted</c> holds the folders among them that could not
    /// be listed: git can still run a hook from one by name, so the capture keeps each as a git file without metadata
    /// (<see cref="Unlisted"/>), which always blocks, instead of taking it for an empty one (R3-03).
    /// </summary>
    internal static (IReadOnlyList<string> Files, IReadOnlyList<string> Unlisted) HookFiles(string folder, string workingTreeRoot)
    {
        if (!Directory.Exists(folder))
        {
            return ([], []);
        }

        var depth = GitNaming.SamePath(folder, workingTreeRoot) || GitNaming.IsUnder(workingTreeRoot, folder) ? 0 : HookDepth;
        var files = new List<string>();
        var unlisted = new List<string>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>([(new DirectoryInfo(folder), 0)]);
        while (pending.Count > 0 && files.Count < MaxHookFiles)
        {
            var (directory, level) = pending.Pop();
            if (Entries(directory) is not { } listed)
            {
                unlisted.Add(directory.FullName);
                continue;
            }

            foreach (var entry in listed)
            {
                if (entry is DirectoryInfo subfolder && !IsLink(entry))
                {
                    if (level < depth)
                    {
                        pending.Push((subfolder, level + 1));
                    }
                }
                else if (files.Count < MaxHookFiles)
                {
                    files.Add(entry.FullName);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        unlisted.Sort(StringComparer.Ordinal);
        return (files, unlisted);
    }

    /// <summary>
    /// The fingerprint of one file within <paramref name="budget"/>: the SHA-256 of its bytes (exact) when it is within
    /// the per-file cap and the budget still has room; indeterminate when it is larger than the cap, the budget is spent,
    /// or it cannot be read. A link is fingerprinted by its target and never read. Null when the path is not a file.
    /// </summary>
    internal static async Task<FileFingerprint?> OfFileAsync(string full, Budget budget, CancellationToken ct)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return null;
        }

        if (IsLink(info))
        {
            return Link(full);
        }

        var approximation = Approximation(info);
        if (info.Length > budget.MaxFileBytes)
        {
            return Indeterminate(approximation, $"larger than the {budget.MaxFileBytes}-byte per-file cap");
        }

        if (!budget.TryTake(info.Length))
        {
            return Indeterminate(approximation, "beyond the capture budget");
        }

        try
        {
            return new FileFingerprint(await Sha256Async(full, ct).ConfigureAwait(false), approximation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or locked: its content cannot be read, so it is not trusted as unchanged.
            return Indeterminate(approximation, "could not be read");
        }
    }

    /// <summary>How a path is named in <see cref="UserRepositoryState.GitFiles"/>: relative to the working tree with '/', or absolute outside it.</summary>
    internal static string DisplayPath(string workingTreeRoot, string full) =>
        GitNaming.IsUnder(full, workingTreeRoot) ? Path.GetRelativePath(workingTreeRoot, full).Replace('\\', '/') : GitNaming.NormalizePath(full);

    /// <summary>
    /// An untracked or ignored folder (<c>bin/</c>, a nested repository...), walked once in a fixed order, with links
    /// listed by their target and never followed. Two canonical digests (every field carries its length, R3-02):
    /// <see cref="FileFingerprint.Sha256"/> covers the type, the relative path and the content (its SHA-256) or link
    /// target of every entry, and their count; <see cref="FileFingerprint.Approximation"/> covers the type, the relative
    /// path, the size, the modification time and the link target of every entry, and their count. The content is what
    /// catches a same-length swap; when it cannot be verified (a file larger than the per-file cap, the budget runs out, or
    /// a file cannot be read) the folder is indeterminate and the walk goes on for the metadata, so a file added, removed,
    /// resized or touched there still shows. Past the metadata cap not even the metadata is known: no approximation.
    /// </summary>
    private static async Task<FileFingerprint> OfFolderAsync(string full, Budget budget, CancellationToken ct)
    {
        var root = new DirectoryInfo(full);
        if (!root.Exists)
        {
            return new FileFingerprint(null, "missing");
        }

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var metadata = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var entries = 0;
        string? unverified = null;
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>([(root, string.Empty)]);
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();

            // A folder that cannot be listed, at any depth, leaves the whole folder without metadata (R3-03).
            if (Entries(directory) is not { } listed)
            {
                return prefix.Length == 0 ? Unlisted : Indeterminate(string.Empty, $"the folder '{prefix}' in it could not be listed");
            }

            var subfolders = new List<(DirectoryInfo, string)>();
            foreach (var entry in listed)
            {
                if (!budget.TryTakeEntry())
                {
                    return Indeterminate(string.Empty, string.Create(CultureInfo.InvariantCulture, $"the {budget.MaxFolderEntries}-entry metadata cap ran out inside this folder"));
                }

                entries++;
                var name = prefix + entry.Name;
                var link = IsLink(entry);
                if (Metadata(entry, link) is not { } read)
                {
                    return Indeterminate(string.Empty, $"the metadata of '{name}' in it could not be read");
                }

                var (modified, target) = read;
                if (link)
                {
                    Append(metadata, "link", name, string.Empty, modified, target);
                    Append(content, "link", name, target);
                }
                else if (entry is DirectoryInfo subfolder)
                {
                    Append(metadata, "folder", name + "/", string.Empty, modified, string.Empty);
                    Append(content, "folder", name + "/");
                    subfolders.Add((subfolder, name + "/"));
                }
                else
                {
                    var file = (FileInfo)entry;
                    Append(metadata, "file", name, Number(file.Length), modified, string.Empty);

                    // Once the content cannot be verified, no more of it is read: only the metadata goes on.
                    unverified ??= await AppendContentAsync(content, name, file, budget, ct).ConfigureAwait(false);
                }
            }

            // Pushed in reverse, so the folders are walked in name order.
            for (var i = subfolders.Count - 1; i >= 0; i--)
            {
                pending.Push(subfolders[i]);
            }
        }

        var count = Number(entries);
        Append(metadata, "entries", count);
        Append(content, "entries", count);
        var approximation = $"folder:{count} entries;metadata:{Convert.ToHexStringLower(metadata.GetHashAndReset())}";
        return unverified is null
            ? new FileFingerprint(Convert.ToHexStringLower(content.GetHashAndReset()), approximation)
            : Indeterminate(approximation, unverified);
    }

    /// <summary>
    /// Appends one file of a folder to its content digest (its relative path and the SHA-256 of its bytes) within the
    /// budget. Returns why it could not, and appends nothing, when the file is larger than the per-file cap, the budget
    /// runs out, or it cannot be read.
    /// </summary>
    private static async Task<string?> AppendContentAsync(IncrementalHash content, string name, FileInfo file, Budget budget, CancellationToken ct)
    {
        if (file.Length > budget.MaxFileBytes)
        {
            return $"a file is larger than the {budget.MaxFileBytes}-byte per-file cap";
        }

        if (!budget.TryTake(file.Length))
        {
            return "the capture budget ran out inside this folder";
        }

        try
        {
            Append(content, "file", name, await Sha256Async(file.FullName, ct).ConfigureAwait(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "a file in it could not be read";
        }
    }

    private static FileFingerprint Link(string full)
    {
        string? target;
        try
        {
            target = new FileInfo(full).LinkTarget ?? new DirectoryInfo(full).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        return new FileFingerprint(null, "link:" + (target ?? "(reached through a link)"));
    }

    private static FileFingerprint Indeterminate(string approximation, string reason) => new(null, approximation, reason);

    private static string Approximation(FileInfo info) => string.Create(CultureInfo.InvariantCulture, $"size:{info.Length};mtime:{info.LastWriteTimeUtc.Ticks}");

    /// <summary>The SHA-256 of the bytes of a file, read once, shared with its writers.</summary>
    private static async Task<string> Sha256Async(string full, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The modification time (ticks) and, for a link (as the listing says it is one), the target of an entry of a folder,
    /// or null when either cannot be read. A file's time is the one the listing gives; a folder's or a link's is read from
    /// the entry itself. NTFS keeps a copy of each subfolder's time in its parent's listing and refreshes it late, when the
    /// subfolder is next opened, which is what walking it does: read from the listing, the walk of one capture would
    /// change what the next one reads, and two captures of an untouched folder would differ (measured on NTFS).
    /// </summary>
    private static (string Modified, string Target)? Metadata(FileSystemInfo entry, bool link)
    {
        try
        {
            if (link || entry is DirectoryInfo)
            {
                entry.Refresh();
            }

            return (Number(entry.LastWriteTimeUtc.Ticks), link ? entry.LinkTarget ?? string.Empty : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The attribute the listing already carries: every symbolic link and junction is a reparse point.</summary>
    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The entries of a folder in name order, or null when it cannot be listed, at its start or halfway: never an empty list for it.</summary>
    private static List<FileSystemInfo>? Entries(DirectoryInfo directory)
    {
        try
        {
            return [.. directory.EnumerateFileSystemInfos("*", OneLevel).OrderBy(e => e.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends one record to a digest, canonically (<see cref="ContentHash.Canonical"/>: each field with its length, so
    /// no two lists of records feed the same bytes). Its first field names the kind of record, which fixes how many
    /// fields follow. The UTF-16 code units are hashed as they are: a name that is not valid Unicode is not folded into
    /// another one.
    /// </summary>
    private static void Append(IncrementalHash hash, params string[] fields) =>
        hash.AppendData(MemoryMarshal.AsBytes(ContentHash.Canonical(fields).AsSpan()));

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The full path of a status path, or null when it would leave the working tree (git never lists one, but nothing is read outside).</summary>
    private static string? FullPath(string root, string path)
    {
        var relative = path.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return null;
        }

        try
        {
            var full = GitNaming.NormalizePath(Path.Combine(root, relative));
            return GitNaming.IsUnder(full, root) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// How much of a capture may still be hashed by content: a per-file cap, a total of bytes and a count of files.
    /// Once a file would exceed what is left, the budget is spent for good, so the rest of the capture is indeterminate
    /// in a fixed, deterministic way.
    /// </summary>
    internal sealed class Budget
    {
        private long _bytesLeft;
        private int _filesLeft;
        private int _entriesLeft;
        private bool _spent;

        internal Budget(CaptureBudget budget)
        {
            MaxFileBytes = budget.MaxFileBytes;
            MaxFolderEntries = budget.MaxFolderEntries;
            _bytesLeft = budget.MaxTotalBytes;
            _filesLeft = budget.MaxFiles;
            _entriesLeft = budget.MaxFolderEntries;
        }

        internal long MaxFileBytes { get; }

        internal int MaxFolderEntries { get; }

        /// <summary>A budget with only the per-file cap (for the configuration and hook files, which have no total budget).</summary>
        internal static Budget PerFileOnly(long maxFileBytes) => new(new CaptureBudget(maxFileBytes, long.MaxValue, int.MaxValue, int.MaxValue));

        /// <summary>Takes room to list one more entry of a folder by its metadata; false (and spent for good) once the cap is reached.</summary>
        internal bool TryTakeEntry()
        {
            if (_entriesLeft <= 0)
            {
                return false;
            }

            _entriesLeft--;
            return true;
        }

        /// <summary>Takes room for a file of <paramref name="length"/> bytes; false (and spent for good) when it no longer fits.</summary>
        internal bool TryTake(long length)
        {
            if (_spent || _filesLeft <= 0 || _bytesLeft < length)
            {
                _spent = true;
                return false;
            }

            _filesLeft--;
            _bytesLeft -= length;
            return true;
        }
    }

    /// <summary>Whether a path, or a folder on the way to it below the root, is a link; one attribute read per folder.</summary>
    private sealed class LinkCache(string root)
    {
        private readonly Dictionary<string, bool> _links = new(StringComparer.OrdinalIgnoreCase);

        public bool HasLinkOnTheWay(string full)
        {
            for (var current = full; GitNaming.IsUnder(current, root); current = Path.GetDirectoryName(current) ?? root)
            {
                if (!_links.TryGetValue(current, out var link))
                {
                    link = IsLinkPath(current);
                    _links[current] = link;
                }

                if (link)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLinkPath(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or unreadable: nothing is read through it.
                return false;
            }
        }
    }
}
