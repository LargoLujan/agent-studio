using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentStudio.Application.Execution;
using Microsoft.Win32.SafeHandles;
using static AgentStudio.Infrastructure.Processes.Isolation.IsolationNativeMethods;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// The lock that only one isolated verification of the user holds at a time (ADR-0017): a file opened without sharing
/// anything, held from before the work area is made until after it is deleted. Windows lets it go when the process that
/// holds it ends, also when it crashes.
/// <list type="bullet">
/// <item>It lives where no Low process can create, rename or delete anything: by default
/// <c>%LOCALAPPDATA%\AgentStudio</c>, Medium all the way up. Under <c>LocalLow</c> a Low process can rename a Medium folder
/// whose parent is Low, once no handle is open inside it (measured), and the next run would take another lock.</item>
/// <item>It is born with a Medium label that also refuses reading and executing to a lower level, so no Low process can
/// open it and hold it so that no verification runs (without <c>NR</c> it can, measured).</item>
/// <item>A link at its path is never followed: the file is opened as it is, and anything but a regular file is refused.</item>
/// <item>The label only applies when the file is created, so it is read back, through the handle that holds the lock,
/// every time the lock is taken. A lock that already exists with any other label (none, which Windows reads as Medium
/// with no-write-up alone, or a Low one) was not made by Agent Studio, which has always created it labelled: it is
/// refused, never relabelled nor replaced (only what this process has just created is ever labelled, as with
/// <see cref="IntegrityLabels"/>), and the message tells the user to delete it (ADR-0018).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class IsolationLock : IDisposable
{
    /// <summary>Medium, no-write-up, no-read-up and no-execute-up.</summary>
    internal const string Label = "S:(ML;;NWNRNX;;;ME)";

    /// <summary>SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, NO_READ_UP and NO_EXECUTE_UP: the policy of <see cref="Label"/>.</summary>
    private const uint NoWriteNoReadNoExecuteUp = 0x1 | 0x2 | 0x4;

    private const int ErrorSharingViolation = 32;

    private static readonly TimeSpan FirstWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(1);

    private readonly SafeFileHandle _handle;

    private IsolationLock(SafeFileHandle handle) => _handle = handle;

    /// <summary>Waits, for as long as another verification of the user holds it, until this one holds the lock.</summary>
    /// <exception cref="ProcessIsolationException">The lock cannot be opened for another reason, or is not a regular file.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while waiting.</exception>
    internal static async Task<IsolationLock> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ProcessIsolationException($"The lock '{path}' of the isolated verifications has no folder; nothing was prepared."));

        var wait = FirstWait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = Open(path, out var error);
            if (handle is not null)
            {
                return new IsolationLock(handle);
            }

            if (error != ErrorSharingViolation)
            {
                throw new ProcessIsolationException(
                    $"The lock '{path}' of the isolated verifications could not be opened ({new Win32Exception(error).Message}); nothing was prepared.");
            }

            // Another verification of the user holds it: one at a time.
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            wait = wait * 2 < LongestWait ? wait * 2 : LongestWait;
        }
    }

    public void Dispose() => _handle.Dispose();

    private static unsafe SafeFileHandle? Open(string path, out int error)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(Label, SddlRevision1, out var descriptor, out _))
        {
            throw new ProcessIsolationException(
                "The label of the lock of the isolated verifications could not be built; nothing was prepared.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            // The label only applies when the file is created; the share mode of none is the lock.
            var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), SecurityDescriptor = descriptor };
            var handle = CreateFile(path, GenericRead, 0, (nint)(&attributes), OpenAlways, FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                return null;
            }

            if (!GetFileInformationByHandle(handle, out var information)
                || (information.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
            {
                handle.Dispose();
                throw new ProcessIsolationException($"The lock '{path}' of the isolated verifications is not a regular file; nothing was prepared.");
            }

            // The label of the file this handle holds, also when it was already there (OPEN_ALWAYS keeps it as it is).
            (uint? Level, uint Policy) label;
            try
            {
                label = LabelOf(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            var (level, policy) = label;
            if (level != IntegrityLabels.MediumRid || (policy & NoWriteNoReadNoExecuteUp) != NoWriteNoReadNoExecuteUp)
            {
                handle.Dispose();
                throw new ProcessIsolationException(
                    $"The lock '{path}' of the isolated verifications already exists with the label {Describe(level, policy)}, not the one Agent Studio creates it with (Medium, refusing to write, read and execute to a lower level): Agent Studio did not make it, and a Low process could open it and hold it so that no verification runs. Nothing was prepared. If no isolated verification is running, delete the file and try again.");
            }

            error = 0;
            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// The mandatory label of the file <paramref name="handle"/> holds: the level (0x1000 Low, 0x2000 Medium...) and the
    /// policy bits of its ACE, or no level without a label (which Windows reads as Medium with no-write-up alone).
    /// </summary>
    /// <exception cref="ProcessIsolationException">The label cannot be read: the lock cannot be trusted.</exception>
    private static (uint? Level, uint Policy) LabelOf(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, SeFileObject, LabelSecurityInformation, out _, out _, out _, out var sacl, out var descriptor);
        if (error != 0)
        {
            throw new ProcessIsolationException(
                "The label of the lock of the isolated verifications could not be read; nothing was prepared.",
                new Win32Exception((int)error));
        }

        try
        {
            if (sacl == 0)
            {
                return (null, 0);
            }

            // ACL: revision (1), padding (1), size (2), ACE count (2), padding (2).
            var count = (ushort)Marshal.ReadInt16(sacl, 4);
            for (uint index = 0; index < count; index++)
            {
                // SYSTEM_MANDATORY_LABEL_ACE: ACE_HEADER (4 bytes), the policy as its ACCESS_MASK (4 bytes), then the SID of the level.
                if (GetAce(sacl, index, out var ace) && Marshal.ReadByte(ace) == SystemMandatoryLabelAceType)
                {
                    var policy = unchecked((uint)Marshal.ReadInt32(ace, 4));
                    var sid = ace + 8;
                    int subAuthorities = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return (unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(subAuthorities - 1)))), policy);
                }
            }

            return (null, 0);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string Describe(uint? level, uint policy)
    {
        if (level is not { } rid)
        {
            return "none (Windows reads it as Medium with no-write-up alone)";
        }

        var name = rid switch
        {
            IntegrityLabels.LowRid => "Low",
            IntegrityLabels.MediumRid => "Medium",
            _ => string.Create(CultureInfo.InvariantCulture, $"0x{rid:X4}"),
        };
        var bits = new[] { (0x1u, "NW"), (0x2u, "NR"), (0x4u, "NX") }.Where(b => (policy & b.Item1) != 0).Select(b => b.Item2);
        return $"{name} ({string.Join(", ", bits)})";
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInfo,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);
}
