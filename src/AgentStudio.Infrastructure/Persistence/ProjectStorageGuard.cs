using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentStudio.Infrastructure.Processes.Isolation;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Checks what Agent Studio keeps under a project's <c>.agentstudio</c> folder before it is used (ADR-0010, addenda of
/// 2026-10-18; ADR-0018: a link left in the project before a step is in the model). Every folder must be a real folder and
/// every file a file with a single name, never a link, and Windows must reach each one at its own place under the final
/// path of the project root. The root itself may be reached through links: the folders above it are the user's, so it is
/// taken at its final path. Anything else is a <see cref="ProjectStorageIntegrityException"/>, before anything is written.
/// It is the one policy of every writer there: the workspace, the evidence store and <see cref="ProjectStorageFiles"/>
/// (the prompt journal, the canvas layout, the verification profile and the Markdown of the memory), and of the readers
/// that feed what Agent Studio writes: the checked read of <see cref="ProjectStorageFiles"/> (the journal of the daily
/// summary) and <see cref="ReadOnlyProjectLogReader"/> (the log of another project).
/// </summary>
/// <remarks>
/// A process of the same user running at the same time can still change a folder between the check and its use: that
/// race is a residual of ADR-0018 (point 2), narrowed by checking the handle that writes where there is one.
/// </remarks>
internal static class ProjectStorageGuard
{
    // CreateFileW: write access, and the only sharing a file opened to append allows (others may read it).
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>The final path of the project folder <paramref name="root"/>, which exists (on other systems, the path itself).</summary>
    public static string FinalRoot(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return root;
        }

        return IntegrityLabels.FinalPath(root) is { } final
            ? Path.TrimEndingDirectorySeparator(final)
            : throw new ProjectStorageIntegrityException($"The project folder '{root}' cannot be opened to learn where Windows really reaches it.");
    }

    /// <summary>
    /// A folder of the storage: a real folder at its place, when it exists. With <paramref name="mustExist"/> a missing
    /// folder is refused too.
    /// </summary>
    public static void CheckFolder(ProjectStorage storage, string folder, bool mustExist = false)
    {
        if (AttributesOf(folder) is not { } attributes)
        {
            if (mustExist)
            {
                throw Refused(folder, "it does not exist");
            }

            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(folder, "it is a link (junction or symbolic link)");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw Refused(folder, "it is not a folder");
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureReachedAt(storage, folder, IntegrityLabels.FinalPath(folder));
        }
    }

    /// <summary>
    /// A file of the storage, when it exists: not a link, a single name (a hard link would let another file be written
    /// through it), and at its place.
    /// </summary>
    public static void CheckFile(ProjectStorage storage, string file)
    {
        if (AttributesOf(file) is not { } attributes)
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(file, "it is a link (symbolic link or junction)");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw Refused(file, "it is a folder, not a file");
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = IntegrityLabels.OpenWithoutFollowing(file, out var error);
        if (handle is null)
        {
            if (error == ErrorFileNotFound)
            {
                return;
            }

            throw Refused(file, $"it cannot be inspected ({new Win32Exception(error).Message})");
        }

        CheckOpenedFile(storage, handle, file);
    }

    /// <summary>
    /// What <paramref name="handle"/> has open must be <paramref name="file"/> of the storage: a regular file, never a link
    /// or a folder, with a single name (a hard link would let another file be written through it), and at its place.
    /// </summary>
    public static void CheckOpenedFile(ProjectStorage storage, SafeFileHandle handle, string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!IsolationNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw Refused(file, "it cannot be inspected");
        }

        if ((information.FileAttributes & (IsolationNativeMethods.FileAttributeReparsePoint | IsolationNativeMethods.FileAttributeDirectory)) != 0)
        {
            throw Refused(file, "it is not a regular file");
        }

        if (information.NumberOfLinks != 1)
        {
            throw Refused(file, $"it has {information.NumberOfLinks} names, so writing it would write another file too");
        }

        EnsureReachedAt(storage, file, IntegrityLabels.FinalPath(handle));
    }

    /// <summary>
    /// Opens <paramref name="file"/> of the storage to read and append, created when it is missing, without following a
    /// link it may be: before a byte is written, what the handle has open must be a regular file with a single name at its
    /// place (<see cref="CheckOpenedFile"/>). Its folder must have been checked already. Only reading is shared, so another
    /// writer gets a sharing violation: an <see cref="IOException"/> with that <see cref="Exception.HResult"/>, never a
    /// <see cref="ProjectStorageIntegrityException"/>, so it may try again.
    /// </summary>
    public static FileStream OpenForAppend(ProjectStorage storage, string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            CheckFile(storage, file);
            return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }

        var handle = IsolationNativeMethods.CreateFile(
            file,
            IsolationNativeMethods.GenericRead | GenericWrite,
            FileShareRead,
            0,
            IsolationNativeMethods.OpenAlways,
            IsolationNativeMethods.FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NotOpened(file, error);
        }

        try
        {
            CheckOpenedFile(storage, handle, file);
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="file"/> of the storage to read, without following a link it may be: what the handle has open
    /// must be a regular file with a single name at its place (<see cref="CheckOpenedFile"/>), the same as a file opened
    /// to append, before a byte is read. Its folder must have been checked already. Reading, writing and deleting are
    /// shared, so a day of the journal can be read while its writer appends to it. Nothing is created.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file, or a folder on its way, does not exist.</exception>
    public static FileStream OpenForRead(ProjectStorage storage, string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            CheckFile(storage, file);
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var handle = IntegrityLabels.OpenWithoutFollowing(file, out var error)
            ?? throw NotOpenedForRead(file, error);
        try
        {
            CheckOpenedFile(storage, handle, file);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The lock file is opened to be deleted on close, so a link there would delete what it points to: it must not be a
    /// link when it exists. Only its attributes are read, since another instance may hold it open with no sharing.
    /// </summary>
    public static void CheckLockFile(ProjectStorage storage)
    {
        if (AttributesOf(storage.LockPath) is { } attributes && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw Refused(storage.LockPath, "it is a link or a folder, not the lock file");
        }
    }

    /// <summary>The database files, the evidence, the temporary folder and the prompt journal, whichever exist.</summary>
    public static void CheckStorage(ProjectStorage storage)
    {
        foreach (var file in storage.DatabaseFiles)
        {
            CheckFile(storage, file);
        }

        CheckFolder(storage, storage.EvidenceDirectory);
        CheckFolder(storage, storage.TempDirectory);
        CheckFolder(storage, storage.JournalDirectory);
    }

    /// <summary>Creates the data folder when it is missing: checked before, so nothing is created through a link, and after.</summary>
    public static void CreateDataFolder(ProjectStorage storage)
    {
        CheckFolder(storage, storage.DataDirectory);
        Directory.CreateDirectory(storage.DataDirectory);
        CheckFolder(storage, storage.DataDirectory, mustExist: true);
    }

    /// <summary>
    /// Creates <paramref name="folder"/>, a folder right under the data folder: the data folder is checked first, so
    /// nothing is created through a link, and the new folder after.
    /// </summary>
    public static void CreateFolder(ProjectStorage storage, string folder)
    {
        CheckFolder(storage, storage.DataDirectory, mustExist: true);
        CheckFolder(storage, folder);
        Directory.CreateDirectory(folder);
        CheckFolder(storage, folder, mustExist: true);
    }

    /// <summary>What <paramref name="handle"/> has open must be <paramref name="path"/> of the storage, at its place: no link on the way led elsewhere.</summary>
    public static void CheckOpened(ProjectStorage storage, SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureReachedAt(storage, path, IntegrityLabels.FinalPath(handle));
        }
    }

    private static void EnsureReachedAt(ProjectStorage storage, string path, string? final)
    {
        var expected = storage.FinalPathOf(path);
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(path, final is null ? "Windows cannot say where it really is" : $"Windows reaches it at '{final}', not at '{expected}' under the project");
        }
    }

    /// <summary>The attributes of the entry itself (a link is not followed), or null when there is none.</summary>
    private static FileAttributes? AttributesOf(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Why <paramref name="file"/> could not be opened to append: a writer to wait for, a folder or link in its place, or something else.</summary>
    private static Exception NotOpened(string file, int error) => error switch
    {
        ErrorSharingViolation or ErrorLockViolation => new IOException($"'{file}' is open in another writer.", HResultOf(error)),

        // A folder, or a junction, cannot be opened as a file.
        ErrorAccessDenied when AttributesOf(file) is { } attributes && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            => Refused(file, "it is a folder or a link, not a file"),
        ErrorAccessDenied => new UnauthorizedAccessException($"'{file}' cannot be written: {new Win32Exception(error).Message}"),
        _ => new IOException($"'{file}' cannot be opened: {new Win32Exception(error).Message}", HResultOf(error)),
    };

    /// <summary>Why <paramref name="file"/> could not be opened to read: it is missing, a folder or link is in its place, or something else.</summary>
    private static Exception NotOpenedForRead(string file, int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => new FileNotFoundException($"'{file}' does not exist.", file),

        // A folder, or a junction, cannot be opened as a file.
        ErrorAccessDenied when AttributesOf(file) is { } attributes && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            => Refused(file, "it is a folder or a link, not a file"),
        ErrorAccessDenied => new UnauthorizedAccessException($"'{file}' cannot be read: {new Win32Exception(error).Message}"),
        _ => new IOException($"'{file}' cannot be opened: {new Win32Exception(error).Message}", HResultOf(error)),
    };

    /// <summary>HRESULT_FROM_WIN32, as .NET gives its own I/O exceptions.</summary>
    private static int HResultOf(int win32Error) => unchecked((int)0x80070000 | win32Error);

    private static ProjectStorageIntegrityException Refused(string path, string why) =>
        new($"Refusing to use '{path}' of the project's storage: {why}. Nothing was written through it; remove or fix it and open the project again.");
}
