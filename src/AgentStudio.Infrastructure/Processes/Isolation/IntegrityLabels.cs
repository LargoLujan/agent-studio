using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static AgentStudio.Infrastructure.Processes.Isolation.IsolationNativeMethods;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// Mandatory integrity labels of the folders Agent Studio creates for isolated runs (ADR-0017). A label is only ever set
/// on a folder this process has just created: never on a folder of the user, of the system or of the NuGet cache.
/// Setting a label at or below the own level needs no privilege, only <c>WRITE_OWNER</c>, which the creator has (spike).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IntegrityLabels
{
    /// <summary>
    /// Medium, no-write-up, inherited by files and folders. An explicit label takes the place of the one a folder would
    /// inherit (a Low one under LocalLow), so a Low process can create nothing inside. The SACL is not marked protected:
    /// that would need <c>SeSecurityPrivilege</c>.
    /// </summary>
    internal const string Medium = "S:(ML;OICI;NW;;;ME)";

    /// <summary>Low, no-write-up, inherited by files and folders: a Low process can write inside.</summary>
    internal const string Low = "S:(ML;OICI;NW;;;LW)";

    internal const uint LowRid = 0x1000;
    internal const uint MediumRid = 0x2000;

    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Creates a new folder, born with <paramref name="label"/> (or with what it inherits when null). A folder that already
    /// exists is never reused: <see cref="IOException"/>.
    /// </summary>
    internal static void CreateDirectory(string path, string? label)
    {
        nint descriptor = 0;
        try
        {
            if (label is not null && !ConvertStringSecurityDescriptorToSecurityDescriptor(label, SddlRevision1, out descriptor, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The security descriptor of a mandatory label could not be built.");
            }

            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            if (!IsolationNativeMethods.CreateDirectory(path, ref attributes))
            {
                var error = Marshal.GetLastPInvokeError();
                throw error == ErrorAlreadyExists
                    ? new IOException($"'{path}' already exists.")
                    : new IOException($"'{path}' could not be created: {new Win32Exception(error).Message}");
            }
        }
        finally
        {
            if (descriptor != 0)
            {
                LocalFree(descriptor);
            }
        }
    }

    /// <summary>Sets the label of a folder this process created; Windows propagates it to everything below.</summary>
    internal static void Set(string path, string label)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(label, SddlRevision1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The security descriptor of a mandatory label could not be built.");
        }

        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The mandatory label has no SACL.");
            }

            var error = SetNamedSecurityInfo(path, SeFileObject, LabelSecurityInformation, 0, 0, 0, sacl);
            if (error != 0)
            {
                throw new Win32Exception((int)error, $"The mandatory label of '{path}' could not be set.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>The level of the mandatory label of a file or folder (0x1000 Low...), or null without one (which counts as Medium).</summary>
    internal static uint? ReadRid(string path)
    {
        var error = GetNamedSecurityInfo(path, SeFileObject, LabelSecurityInformation, out _, out _, out _, out var sacl, out var descriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error, $"The mandatory label of '{path}' could not be read.");
        }

        try
        {
            if (sacl == 0)
            {
                return null;
            }

            // ACL: revision (1), padding (1), size (2), ACE count (2), padding (2).
            var count = (ushort)Marshal.ReadInt16(sacl, 4);
            for (uint index = 0; index < count; index++)
            {
                // SYSTEM_MANDATORY_LABEL_ACE: ACE_HEADER (4 bytes), ACCESS_MASK (4 bytes), then the SID of the level.
                if (GetAce(sacl, index, out var ace) && Marshal.ReadByte(ace) == SystemMandatoryLabelAceType)
                {
                    var sid = ace + 8;
                    int subAuthorities = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(subAuthorities - 1))));
                }
            }

            return null;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// The path Windows reaches for <paramref name="path"/>, with every link resolved and without the <c>\\?\</c> prefix,
    /// or null when it cannot be opened.
    /// </summary>
    internal static string? FinalPath(string path)
    {
        using var handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    /// <summary>
    /// The path of what <paramref name="handle"/> has open, with every link on the way to it resolved and without the
    /// <c>\\?\</c> prefix, or null when Windows cannot say.
    /// </summary>
    internal static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
        }

        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            }

            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }
        }

        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    /// <summary>Opens a file for reading without following a link it may be, or null when it cannot be opened.</summary>
    internal static SafeFileHandle? OpenWithoutFollowing(string path, out int error)
    {
        var handle = CreateFile(path, GenericRead, FileShareAll, 0, OpenExisting, FileFlagOpenReparsePoint | FileFlagSequentialScan, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }
}
