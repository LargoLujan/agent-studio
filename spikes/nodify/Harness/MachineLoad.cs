using System.Runtime.InteropServices;

namespace NodifySpike.Harness;

/// <summary>
/// Whole-machine CPU utilisation over a short window. The benchmark numbers depend on what else the machine is doing
/// (other agents compile and test on the same one), so every run records the load it started under.
/// </summary>
internal static class MachineLoad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    public static async Task<double> MeasureAsync(int windowMilliseconds = 800)
    {
        GetSystemTimes(out var idleA, out var kernelA, out var userA);
        await Task.Delay(windowMilliseconds);
        GetSystemTimes(out var idleB, out var kernelB, out var userB);

        // Kernel time includes the idle time.
        var total = (kernelB.Value - kernelA.Value) + (userB.Value - userA.Value);
        var idle = idleB.Value - idleA.Value;
        return total == 0 ? 0 : Math.Round((1d - ((double)idle / total)) * 100d, 0);
    }
}
