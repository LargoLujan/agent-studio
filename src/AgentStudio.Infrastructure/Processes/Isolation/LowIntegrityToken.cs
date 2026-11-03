using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentStudio.Application.Execution;
using static AgentStudio.Infrastructure.Processes.Isolation.IsolationNativeMethods;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// Builds the token of an isolated process: a primary copy of the token of Agent Studio whose integrity level is lowered
/// to Low (S-1-16-4096). Lowering one's own level needs no privilege (spike, verified); raising it is impossible. The
/// level is read back before the token is used, so a token that is not Low never starts a process.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LowIntegrityToken
{
    /// <summary>The relative identifier of the Low mandatory level (S-1-16-4096).</summary>
    internal const uint LowRid = 0x1000;

    /// <summary>The relative identifier of the High mandatory level: an elevated process.</summary>
    internal const uint HighRid = 0x3000;

    private const string LowSid = "S-1-16-4096";

    /// <exception cref="ProcessIsolationException">The token could not be built, or Agent Studio runs elevated.</exception>
    internal static SafeTokenHandle Create()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out var own))
        {
            throw Failure("the token of Agent Studio could not be opened");
        }

        using (own)
        {
            // An elevated token lowered to Low would keep the administrator groups and privileges (spike, risks).
            var rid = IntegrityRid(own);
            if (rid >= HighRid)
            {
                throw new ProcessIsolationException(
                    $"Agent Studio runs elevated (integrity level 0x{rid:X4}); an isolated process is refused because a lowered copy of an elevated token keeps its administrator groups and privileges. Run Agent Studio without elevation.");
            }

            if (!DuplicateTokenEx(own, 0, 0, SecurityImpersonation, TokenPrimary, out var lowered))
            {
                throw Failure("the token of Agent Studio could not be duplicated");
            }

            try
            {
                if (!ConvertStringSidToSid(LowSid, out var sid))
                {
                    throw Failure("the Low integrity SID could not be built");
                }

                try
                {
                    // TOKEN_MANDATORY_LABEL is 16 bytes on x64 (the pointer aligns it): a shorter length fails with ERROR_BAD_LENGTH.
                    var label = new TokenMandatoryLabel { Sid = sid, Attributes = SeGroupIntegrity };
                    if (!SetTokenInformation(lowered, TokenIntegrityLevel, ref label, (uint)Marshal.SizeOf<TokenMandatoryLabel>()))
                    {
                        throw Failure("the integrity level of the token could not be lowered");
                    }
                }
                finally
                {
                    LocalFree(sid);
                }

                var now = IntegrityRid(lowered);
                if (now != LowRid)
                {
                    throw new ProcessIsolationException($"The lowered token has integrity level 0x{now:X4} instead of Low (0x{LowRid:X4}); nothing was started.");
                }

                return lowered;
            }
            catch
            {
                lowered.Dispose();
                throw;
            }
        }
    }

    /// <summary>The relative identifier of the integrity level of a token (0x1000 Low, 0x2000 Medium, 0x3000 High...).</summary>
    internal static uint IntegrityRid(SafeTokenHandle token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var needed);
        if (needed == 0)
        {
            throw Failure("the integrity level of a token could not be read");
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                throw Failure("the integrity level of a token could not be read");
            }

            // TOKEN_MANDATORY_LABEL.Label.Sid; the level is the last sub-authority of that SID.
            var sid = Marshal.ReadIntPtr(buffer);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ProcessIsolationException Failure(string what)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new ProcessIsolationException($"Low integrity isolation could not be applied: {what} ({error.Message}); nothing was started.", error);
    }
}
