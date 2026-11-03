using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Terminal;

/// <summary>
/// Owns an HPCON. Releasing it calls <c>ClosePseudoConsole</c>, which on some Windows builds blocks until the output
/// pipe is drained: the session therefore disposes it on a worker thread while its reader keeps draining.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePseudoConsoleHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}
