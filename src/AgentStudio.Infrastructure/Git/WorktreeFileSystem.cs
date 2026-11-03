using System.IO.Enumeration;
using System.Runtime.InteropServices;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// File-system operations on folders an agent could have written to (worktrees and the folders around them). A link
/// (junction or symbolic link) is never followed: git itself follows a junction when it deletes a worktree, and writes
/// through one on <c>reset --hard</c>, so those operations never get to see one.
/// </summary>
internal static class WorktreeFileSystem
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    // FILE_READ_ATTRIBUTES: enough to read the number of names of a file, without the right to read its content, and exempt
    // from the sharing checks, so a file another process holds open without sharing (a git lock file) is still counted.
    private const uint FileReadAttributes = 0x0080;

    // The longest path CreateFileW takes without the \\?\ prefix.
    private const int MaxPath = 260;

    // Short waits for the locks Windows takes on files in use (antivirus, indexer, a build server): about 3 seconds.
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600),
    ];

    internal static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    /// <summary>True when <paramref name="path"/>, or one of its existing ancestors strictly below <paramref name="root"/>, is a link.</summary>
    internal static bool HasLinkBelow(string root, string path)
    {
        for (var current = GitNaming.NormalizePath(path);
            GitNaming.IsUnder(current, root);
            current = Path.GetDirectoryName(current) ?? root)
        {
            var info = new DirectoryInfo(current);
            if ((info.Exists || File.Exists(current)) && IsLink(info))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="file"/> is a file with more than one name, a hard link (no privilege needed to make one),
    /// or a file whose names cannot be counted (fail closed). git appends to a reflog and rewrites <c>MERGE_MSG</c>,
    /// <c>MERGE_MODE</c> and <c>COMMIT_EDITMSG</c> in place, so through a hard link it writes the other file too (measured
    /// with git 2.45.2). False when it does not exist or is a folder; a link it may be is not followed.
    /// </summary>
    internal static bool HasOtherNames(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return File.Exists(file) && HasMoreThanOneName(file);
    }

    /// <summary>
    /// The number of names of a file known to exist (the walk read it from a listing), from a handle opened with the least
    /// access (<c>FILE_READ_ATTRIBUTES</c>, every sharing, the link itself and never what it points to): true when it has
    /// more than one, or when they cannot be counted (fail closed); false when the file vanished before it could be opened.
    /// </summary>
    private static bool HasMoreThanOneName(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = IsolationNativeMethods.CreateFile(
            ExtendedPath(file),
            FileReadAttributes,
            IsolationNativeMethods.FileShareAll,
            0,
            IsolationNativeMethods.OpenExisting,
            IsolationNativeMethods.FileFlagOpenReparsePoint | IsolationNativeMethods.FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() is not (ErrorFileNotFound or ErrorPathNotFound) || File.Exists(file);
        }

        return !IsolationNativeMethods.GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1;
    }

    /// <summary><paramref name="path"/>, a full path, with the <c>\\?\</c> prefix CreateFileW needs past <c>MAX_PATH</c>.</summary>
    private static string ExtendedPath(string path) =>
        path.Length < MaxPath || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    // ---- S-01 to S-03: the walk of the git directory -----------------------------------------------------------------

    // Every entry a listing returns: hidden and system ones too, and a folder that cannot be listed throws (R3-03). A new
    // EnumerationOptions skips Hidden and System and ignores what it cannot read, so each is set explicitly.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Where a folder of the walk sits: which of its files may have a second name, and what its subfolders are.</summary>
    private enum WalkZone
    {
        /// <summary>Every file must have a single name.</summary>
        Checked,

        /// <summary>A git directory: its <c>objects</c> and <c>lfs</c> hold object stores, its <c>modules</c> git directories.</summary>
        GitDirectory,

        /// <summary><c>modules/</c> at any depth: the git directories of the submodules, with object stores of their own.</summary>
        Modules,

        /// <summary><c>objects/</c>: its fan-out folders, <c>pack</c> and <c>info</c> hold the immutable files.</summary>
        Objects,

        /// <summary><c>objects/&lt;xx&gt;</c> and <c>objects/pack</c>: loose objects and packs, any number of names.</summary>
        ObjectFiles,

        /// <summary><c>objects/info</c> and below (<c>commit-graphs/</c>): any number of names.</summary>
        ObjectInfo,

        /// <summary><c>lfs/</c>: only its <c>objects</c> holds immutable files.</summary>
        LfsStorage,

        /// <summary><c>lfs/objects/</c>.</summary>
        LfsObjects,

        /// <summary><c>lfs/objects/&lt;aa&gt;</c>.</summary>
        LfsFanOut,

        /// <summary><c>lfs/objects/&lt;aa&gt;/&lt;bb&gt;</c>: the lfs objects, any number of names.</summary>
        LfsObjectFiles,
    }

    /// <summary>
    /// The control for the class the eighth round found again (S-01 to S-03, ADR-0012): a link planted beforehand under the
    /// user's git directory, which an internal git command or the git-lfs filter later writes through. Five rounds named a
    /// new destination each; this walk names none. It reads the whole of <paramref name="commonDirectory"/>, and of
    /// <paramref name="gitDirectory"/> when that lies outside it (a git directory under the common one is walked with it),
    /// and refuses:
    /// <list type="number">
    /// <item>any reparse point, a file or a folder (a junction, a symbolic link or any other kind), read from the attributes
    /// the listing itself returns; the walk never enters one;</item>
    /// <item>any file with more than one name (a hard link), counted from a handle per file, but the files of an object
    /// store: <c>objects/&lt;xx&gt;/</c>, <c>objects/pack/</c>, <c>objects/info/</c> and below, and
    /// <c>lfs/objects/&lt;aa&gt;/&lt;bb&gt;/</c>, of the common directory and of each git directory under <c>modules/</c>.
    /// They are immutable, git and git-lfs replace them by a rename and never write them in place, and
    /// <c>git clone --local</c> gives them a second name (measured with git 2.45.2 and git-lfs 3.5.1). Their folders are
    /// still checked by the first rule;</item>
    /// <item>any folder that cannot be listed (fail closed, as R3-03). A folder or a file that vanished while it was walked
    /// is skipped: it holds nothing to write through.</item>
    /// </list>
    /// The per-destination checks stay as a second line. A process that plants a link while git runs is still a residual
    /// (ADR-0018, point 2): nothing is cached, so each walk sees the git directory as it is.
    /// </summary>
    /// <exception cref="WorktreeOwnershipException">A link or a second name was found, or a folder could not be listed.</exception>
    internal static void RequireGitDirectoriesWithoutLinks(string commonDirectory, string? gitDirectory = null)
    {
        WalkGitDirectory(commonDirectory, WalkZone.GitDirectory);
        if (gitDirectory is not null && !GitNaming.SamePath(gitDirectory, commonDirectory) && !GitNaming.IsUnder(gitDirectory, commonDirectory))
        {
            // The git directory of a worktree: HEAD, the index and the logs of its working tree, no object store.
            WalkGitDirectory(gitDirectory, WalkZone.Checked);
        }
    }

    /// <summary>
    /// The <c>.git</c> of a working tree is its git directory or a gitfile that names it (a linked worktree, a separate git
    /// directory), never a link (O-01): git reports the git directory by its real path, so a <c>.git</c> junction would
    /// otherwise lead every write of git elsewhere unseen. A missing <c>.git</c> is left to git, which then finds no repository.
    /// </summary>
    /// <exception cref="WorktreeOwnershipException"><c>.git</c> is a reparse point or cannot be inspected.</exception>
    internal static void RequireDotGitWithoutLink(string workingTree)
    {
        var dotGit = Path.Combine(workingTree, ".git");
        FileAttributes attributes;
        try
        {
            if (!Path.Exists(dotGit))
            {
                return;
            }

            attributes = File.GetAttributes(dotGit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new WorktreeOwnershipException($"'{dotGit}' cannot be inspected ({ex.Message}); refusing to run git on this repository.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorktreeOwnershipException(
                $"'{dotGit}' is a link (a junction, a symbolic link or another reparse point), so what git or git-lfs writes in the git directory would be reached through a link; refusing to run git on this repository until it is removed.");
        }
    }

    private static void WalkGitDirectory(string root, WalkZone rootZone)
    {
        // The directory itself first (O-01): a git directory that is a junction leads every write of git elsewhere.
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new WorktreeOwnershipException($"The git directory '{root}' cannot be inspected ({ex.Message}); refusing to run git on this repository.");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new WorktreeOwnershipException(
                $"The git directory '{root}' is a link (a junction, a symbolic link or another reparse point) or not a directory, so what git or git-lfs writes there would be reached through a link; refusing to run git on this repository until it is removed.");
        }

        var pending = new Stack<(string Folder, WalkZone Zone)>();
        pending.Push((root, rootZone));
        while (pending.TryPop(out var current))
        {
            var (folder, zone) = current;
            var objectFiles = zone is WalkZone.ObjectFiles or WalkZone.ObjectInfo or WalkZone.LfsObjectFiles;
            try
            {
                // The files of an object store that are no reparse point need nothing but the attributes of the listing.
                var entries = new FileSystemEnumerable<(string Path, FileAttributes Attributes)>(folder, (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes), ListingOptions)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => !objectFiles || entry.IsDirectory || (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                };
                foreach (var (path, attributes) in entries)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new WorktreeOwnershipException(
                            $"'{path}' in the git directory '{root}' is a link (a junction, a symbolic link or another reparse point), so what git or git-lfs writes there would be reached through a link; refusing to run git on this repository until it is removed.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push((path, ZoneOf(zone, Path.GetFileName(path))));
                    }
                    else if (!objectFiles && HasMoreThanOneName(path))
                    {
                        throw new WorktreeOwnershipException(
                            $"'{path}' in the git directory '{root}' has more than one name (a hard link) or its names cannot be counted, so what git or git-lfs writes in it would be reached through a link to another file; refusing to run git on this repository until it is removed.");
                    }
                }
            }
            catch (DirectoryNotFoundException) when (!GitNaming.SamePath(folder, root))
            {
                // It vanished while the walk was on its way (a ref folder git removed): nothing left to write through.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new WorktreeOwnershipException(
                    $"The folder '{folder}' of the git directory '{root}' could not be listed ({ex.Message}), so a link in it cannot be ruled out; refusing to run git on this repository.");
            }
        }
    }

    /// <summary>The zone of the subfolder <paramref name="name"/> of a folder in <paramref name="zone"/>.</summary>
    private static WalkZone ZoneOf(WalkZone zone, string name) => zone switch
    {
        WalkZone.GitDirectory or WalkZone.Modules when Is(name, "objects") => WalkZone.Objects,
        WalkZone.GitDirectory or WalkZone.Modules when Is(name, "lfs") => WalkZone.LfsStorage,
        WalkZone.GitDirectory when Is(name, "modules") => WalkZone.Modules,
        WalkZone.Modules => WalkZone.Modules,
        WalkZone.Objects when IsFanOut(name) || Is(name, "pack") => WalkZone.ObjectFiles,
        WalkZone.Objects when Is(name, "info") => WalkZone.ObjectInfo,
        WalkZone.ObjectInfo => WalkZone.ObjectInfo,
        WalkZone.LfsStorage when Is(name, "objects") => WalkZone.LfsObjects,
        WalkZone.LfsObjects when IsFanOut(name) => WalkZone.LfsFanOut,
        WalkZone.LfsFanOut when IsFanOut(name) => WalkZone.LfsObjectFiles,
        _ => WalkZone.Checked,
    };

    private static bool Is(string name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

    // A fan-out folder: the first two hex digits of an object id (git, SHA-1 and SHA-256) or of an lfs oid, per level.
    private static bool IsFanOut(string name) => name.Length == 2 && char.IsAsciiHexDigit(name[0]) && char.IsAsciiHexDigit(name[1]);

    /// <summary>
    /// True when <paramref name="path"/> does not exist, or when its final path (every link resolved) is under the final
    /// path of <paramref name="root"/>, or is that path when <paramref name="orSame"/>; a path Windows cannot resolve fails
    /// closed.
    /// </summary>
    internal static bool ResolvesUnder(string root, string path, bool orSame = false)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return true;
        }

        var finalRoot = GitCallbacks.ResolveFinalPath(root);
        var final = GitCallbacks.ResolveFinalPath(path);
        if (finalRoot is null || final is null)
        {
            return false;
        }

        var normalizedRoot = GitNaming.NormalizePath(finalRoot);
        var resolved = GitNaming.NormalizePath(final);
        return GitNaming.IsUnder(resolved, normalizedRoot) || (orSame && GitNaming.SamePath(resolved, normalizedRoot));
    }

    // The folders git-lfs 3.5.1 writes in its storage (<common>/lfs; `git lfs env`): the objects, the temporary files of a
    // clean or a download, and the error logs.
    private static readonly string[] GitLfsFolders = ["objects", "tmp", "logs"];

    /// <summary>
    /// G-01: the git-lfs filters a content command can run write into <c>&lt;common&gt;/lfs</c>, so nothing on the way may be
    /// a link: <c>lfs</c> itself, any entry of it (<c>objects</c>, <c>tmp</c>, <c>logs</c> and anything else), any entry of
    /// <c>tmp</c>, <c>logs</c> and <c>objects</c> (the first fan-out level), and any entry of each first-level folder (the
    /// second fan-out level, which holds the object files). <c>lfs</c>, <c>objects</c>, <c>tmp</c> and <c>logs</c> also resolve
    /// under the common directory. Measured with git-lfs 3.5.1 (git 2.45.2): the clean of <c>git add</c>, also from a
    /// worktree with an explicit <c>--git-dir</c>, wrote <c>lfs/objects/&lt;aa&gt;/&lt;bb&gt;/&lt;oid&gt;</c> through
    /// <c>lfs/tmp</c>, and a junction at <c>lfs</c>, <c>lfs/objects</c>, <c>objects/&lt;aa&gt;</c> or
    /// <c>objects/&lt;aa&gt;/&lt;bb&gt;</c> received the object, outside the repository; a smudge whose object is missing
    /// wrote <c>lfs/logs</c>. Every folder git-lfs creates there has a checked parent; a folder that cannot be listed fails
    /// closed. Only the attributes each listing returns are read.
    /// </summary>
    /// <exception cref="WorktreeOwnershipException">A link was found, a folder resolves outside, or a folder cannot be listed.</exception>
    internal static void RequireLfsStorageWithoutLinks(string commonDirectory)
    {
        var lfs = Path.Combine(commonDirectory, "lfs");
        string? offending;
        try
        {
            offending = FindLfsStorageLink(commonDirectory, lfs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new WorktreeOwnershipException(
                $"The git-lfs storage '{lfs}' could not be listed ({ex.Message}), so a link in it cannot be ruled out; refusing to run a git command that can run the git-lfs filters, which write there.");
        }

        if (offending is not null)
        {
            throw new WorktreeOwnershipException(
                $"The git-lfs storage '{lfs}' is reached through a link ('{offending}') or resolves outside '{commonDirectory}'; refusing to run a git command that can run the git-lfs filters, which write there.");
        }
    }

    private static string? FindLfsStorageLink(string commonDirectory, string lfs)
    {
        if (HasLinkBelow(commonDirectory, lfs) || !ResolvesUnder(commonDirectory, lfs))
        {
            return lfs;
        }

        if (!Directory.Exists(lfs))
        {
            return null;
        }

        if (FirstLinkAmong(lfs) is { } entry)
        {
            return entry;
        }

        foreach (var name in GitLfsFolders)
        {
            var folder = Path.Combine(lfs, name);
            if (!ResolvesUnder(commonDirectory, folder))
            {
                return folder;
            }

            if (Directory.Exists(folder) && FirstLinkAmong(folder) is { } inner)
            {
                return inner;
            }
        }

        var objects = Path.Combine(lfs, "objects");
        if (Directory.Exists(objects))
        {
            // No entry of objects is a link (checked above), so each first-level folder is a real one.
            foreach (var first in new DirectoryInfo(objects).EnumerateDirectories())
            {
                if (FirstLinkAmong(first.FullName) is { } second)
                {
                    return second;
                }
            }
        }

        return null;
    }

    // A junction and a symbolic link both carry the reparse-point attribute, which the listing returns with each entry.
    private static string? FirstLinkAmong(string folder) =>
        new DirectoryInfo(folder).EnumerateFileSystemInfos().FirstOrDefault(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint))?.FullName;

    /// <summary>
    /// The first entry inside <paramref name="directory"/>, at any depth, that is a link (a junction or a symbolic link),
    /// or null when there is none. Links are never followed: the walk descends only into real folders.
    /// </summary>
    internal static string? FindLinkInside(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                return entry.FullName;
            }

            if (entry is DirectoryInfo subdirectory && FindLinkInside(subdirectory.FullName) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Removes every link inside <paramref name="directory"/> as a link; what a link points to is never touched.</summary>
    internal static void RemoveLinks(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                DeleteLink(entry);
            }
            else if (entry is DirectoryInfo subdirectory)
            {
                RemoveLinks(subdirectory.FullName);
            }
        }
    }

    /// <summary>Deletes a folder tree, read-only files included. Links inside are removed as links, never followed.</summary>
    internal static void DeleteTree(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (IsLink(root))
        {
            DeleteLink(root);
            return;
        }

        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                DeleteLink(entry);
            }
            else if (entry is DirectoryInfo subdirectory)
            {
                DeleteTree(subdirectory.FullName);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        if (root.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            root.Attributes &= ~FileAttributes.ReadOnly;
        }

        root.Delete();
    }

    /// <summary>Runs <paramref name="action"/>, retrying with a short backoff while Windows reports the files as in use.</summary>
    internal static async Task RetryWhileInUseAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Backoff.Length)
            {
                await Task.Delay(Backoff[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    // RemoveDirectory on a junction or a directory symbolic link removes the link itself; DeleteFile on a file link too.
    private static void DeleteLink(FileSystemInfo link)
    {
        if (link is DirectoryInfo directory)
        {
            directory.Delete(recursive: false);
        }
        else
        {
            link.Delete();
        }
    }
}
