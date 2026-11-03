using System.Runtime.InteropServices;

namespace AgentStudio.Infrastructure.Clocks;

/// <summary>
/// The clock of the app (F8-5; docs/16 section 2.14, D-40): the wall clock and the timers of the system, with a timestamp
/// that stops while the machine sleeps or hibernates, QueryUnbiasedInterruptTime, in hundreds of nanoseconds. The timestamp
/// of <see cref="TimeProvider.System"/> is QueryPerformanceCounter, which goes on counting while the machine sleeps; with
/// this one, the engine finds the time asleep as the gap between how far the wall clock and the timestamp moved, and leaves
/// it out of the silence of its attempts. The timers need nothing of it: on Windows those of .NET already wait on this
/// unbiased count.
/// </summary>
/// <remarks>
/// The count moves with the clock tick of the system (15.6 ms by default), not finely like QueryPerformanceCounter: enough to
/// find gaps of seconds. Never measured across a real sleep (docs/16 section 8): that it stops then is what the documentation
/// of Windows says ("includes only time that the system spends in the working state").
/// </remarks>
public sealed partial class AwakeTimeProvider : TimeProvider
{
    /// <summary>Hundreds of nanoseconds, the unit of QueryUnbiasedInterruptTime.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The unbiased interrupt time: since the system started, without the time it spent asleep.</summary>
    public override long GetTimestamp() =>
        QueryUnbiasedInterruptTime(out var unbiased)
            ? (long)unbiased
            : throw new InvalidOperationException("QueryUnbiasedInterruptTime failed.");

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
