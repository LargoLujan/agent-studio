using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LowIlVerification.Native;

/// <summary>
/// Labels a folder (that the current user owns) with a Low mandatory integrity label using the documented, no-admin
/// path: build the SACL from the SDDL <c>S:(ML;OICI;NW;;;LW)</c> and apply it with <c>SetNamedSecurityInfo</c>
/// (LABEL_SECURITY_INFORMATION). Changing the mandatory label needs WRITE_OWNER on the object, which the user has on
/// folders they created under their profile (full control), and no privilege as long as the new label is not above the
/// caller's own integrity level.
/// </summary>
internal static class IntegrityLabeler
{
    // S: SACL; ML mandatory-label ACE; OICI object+container inherit; NW no-write-up; LW Low mandatory level.
    private const string LowLabelSddl = "S:(ML;OICI;NW;;;LW)";

    /// <summary>Applies the Low mandatory label to <paramref name="path"/>. Throws <see cref="Win32Exception"/> on failure.</summary>
    public static void ApplyLow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(LowLabelSddl, NativeMethods.SddlRevision1, out var securityDescriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertStringSecurityDescriptorToSecurityDescriptor failed.");
        }

        try
        {
            if (!NativeMethods.GetSecurityDescriptorSacl(securityDescriptor, out var present, out var sacl, out _) || !present)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSecurityDescriptorSacl did not return a SACL.");
            }

            var error = NativeMethods.SetNamedSecurityInfo(
                path,
                NativeMethods.SeFileObject,
                NativeMethods.LabelSecurityInformation,
                owner: 0,
                group: 0,
                dacl: 0,
                sacl: sacl);
            if (error != 0)
            {
                throw new Win32Exception((int)error, $"SetNamedSecurityInfo failed for '{path}' (error {error}).");
            }
        }
        finally
        {
            NativeMethods.LocalFree(securityDescriptor);
        }
    }
}
