using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>Where the power transitions come from: Windows in the app (<see cref="SuspendResumeNotifications"/>), a double in the tests.</summary>
public interface IPowerTransitionSource : IDisposable
{
    /// <summary>Raised on a thread of Windows (never the UI's) for each suspension and each wake it reports.</summary>
    event EventHandler<PowerTransition>? Changed;
}

/// <summary>The two clocks of Windows that tell how long the computer slept, in milliseconds since it started.</summary>
public interface ISleepClock
{
    /// <summary>The time since start, the time asleep included (<c>GetTickCount64</c>).</summary>
    long WithSleep();

    /// <summary>The time since start without the time asleep (<c>QueryUnbiasedInterruptTime</c>).</summary>
    long WithoutSleep();
}

/// <summary>The clocks of the kernel: <c>GetTickCount64</c> counts the time asleep, <c>QueryUnbiasedInterruptTime</c> does not.</summary>
public sealed class KernelSleepClock : ISleepClock
{
    public long WithSleep() => (long)NativeMethods.GetTickCount64();

    public long WithoutSleep() => NativeMethods.QueryUnbiasedInterruptTime(out var hundredsOfNanoseconds)
        ? (long)(hundredsOfNanoseconds / 10_000)
        : throw new Win32Exception(Marshal.GetLastPInvokeError());

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
    }
}

/// <summary>
/// The suspensions and wakes Windows reports to a callback (<c>PowerRegisterSuspendResumeNotification</c> with
/// <c>DEVICE_NOTIFY_CALLBACK</c>, Windows 8 and later, no window and no administrator): <c>PBT_APMSUSPEND</c> is a
/// suspension, and both <c>PBT_APMRESUMEAUTOMATIC</c> (every wake, also one nobody is at the computer for) and
/// <c>PBT_APMRESUMESUSPEND</c> (the person is there) are wakes. <c>SystemEvents.PowerModeChanged</c> would miss the
/// automatic wake, which is what unattended work needs (ADR-0036). The callback comes on a thread of Windows; it only
/// passes the transition on and returns, since Windows waits for it before it sleeps.
/// </summary>
public sealed class SuspendResumeNotifications : IPowerTransitionSource
{
    private const uint DeviceNotifyCallback = 2;
    private const uint SuspendKind = 4;
    private const uint ResumeSuspendKind = 7;
    private const uint ResumeAutomaticKind = 18;

    private readonly NativeMethods.DeviceNotifyCallbackRoutine _callback;
    private readonly IntPtr _parameters;
    private IntPtr _registration;

    /// <exception cref="Win32Exception">Windows refused the registration.</exception>
    public SuspendResumeNotifications()
    {
        // Both the delegate and the parameters it is registered with live as long as the registration; if the registration
        // fails, or the call itself throws (no powrprof.dll, no entry point), the parameters are freed before anything leaves.
        _callback = OnNotification;
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.DeviceNotifySubscribeParameters>());
        uint result;
        try
        {
            Marshal.StructureToPtr(new NativeMethods.DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback), Context = IntPtr.Zero }, _parameters, false);
            result = NativeMethods.PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration);
        }
        catch
        {
            Marshal.FreeHGlobal(_parameters);
            throw;
        }

        if (result != 0)
        {
            Marshal.FreeHGlobal(_parameters);
            throw new Win32Exception((int)result, "Windows did not register the suspend and resume notifications.");
        }
    }

    public event EventHandler<PowerTransition>? Changed;

    /// <summary>Whether Windows holds the registration.</summary>
    public bool IsRegistered => _registration != IntPtr.Zero;

    public void Dispose()
    {
        if (_registration == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.PowerUnregisterSuspendResumeNotification(_registration);
        _registration = IntPtr.Zero;
        Marshal.FreeHGlobal(_parameters);
    }

    private uint OnNotification(IntPtr context, uint kind, IntPtr setting)
    {
        try
        {
            switch (kind)
            {
                case SuspendKind:
                    Changed?.Invoke(this, PowerTransition.Suspending);
                    break;
                case ResumeAutomaticKind or ResumeSuspendKind:
                    Changed?.Invoke(this, PowerTransition.Resumed);
                    break;
            }
        }
#pragma warning disable CA1031 // An exception must never go back into Windows from its callback: it would end the process.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return 0;
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

        [DllImport("powrprof.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

        [DllImport("powrprof.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceNotifySubscribeParameters
        {
            public IntPtr Callback;
            public IntPtr Context;
        }
    }
}

/// <summary>
/// The power transitions of the computer (docs/16 D-40, F8-5; the port of P0): when it suspends and when it wakes, with
/// how long it slept as the two clocks of the kernel measure it (<see cref="ISleepClock"/>: the time with the sleep minus
/// the time without it), so the wall clock's own jumps do not count. The engine (WP1) reads them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each event is stamped when Windows reports it and goes into a queue with a single reader, which runs the handlers
/// on a thread of its own (never the UI's, never the caller's) one event after another, in the order Windows reported
/// them: a wake never reaches a handler before the suspension it follows. A handler that throws is logged and the others
/// still run.</item>
/// <item>A wake whose suspension was not seen (the app started asleep, or Windows did not say it) has no
/// <see cref="PowerEvent.Slept"/>: only the wall clock says it.</item>
/// <item>Windows reports a wake twice when the person is at the computer (automatic, then by the person): only the first
/// after each suspension is raised.</item>
/// </list>
/// </remarks>
public sealed class WindowsPowerEvents : IPowerEvents, IDisposable
{
    private readonly IPowerTransitionSource _source;
    private readonly ISleepClock _clock;
    private readonly TimeProvider _time;
    private readonly Action<Action>? _raise;
    private readonly Channel<PowerEvent>? _queue;
    private readonly ILogger? _log;
    // Qualified: the namespace AgentStudio.Desktop.Lock (WP4) hides System.Threading.Lock inside AgentStudio.Desktop.
    private readonly System.Threading.Lock _gate = new();
    private (long WithSleep, long WithoutSleep)? _suspendedAt;
    private bool _resumedSinceSuspension;

    /// <summary>
    /// The transitions Windows reports, measured with the kernel's clocks. If Windows refuses the registration, nothing is
    /// ever raised (the wall clock is all the engine has, as before Phase 8) and the app's log says why.
    /// </summary>
    public WindowsPowerEvents(TimeProvider time, ILogger<WindowsPowerEvents>? log = null)
        : this(WindowsSource(log), new KernelSleepClock(), time, null, log)
    {
    }

    /// <param name="source">Where the transitions come from.</param>
    /// <param name="clock">The clocks that measure the sleep.</param>
    /// <param name="time">The wall clock of <see cref="PowerEvent.At"/>.</param>
    /// <param name="raise">
    /// How the handlers are run instead of the queue (the tests run them in place); null, the default, is the queue with a
    /// single reader that delivers in order.
    /// </param>
    /// <param name="log">The app's log, for a handler that throws.</param>
    public WindowsPowerEvents(IPowerTransitionSource source, ISleepClock clock, TimeProvider time, Action<Action>? raise = null, ILogger<WindowsPowerEvents>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _clock = clock;
        _time = time;
        _raise = raise;
        _log = log;
        if (raise is null)
        {
            _queue = Channel.CreateUnbounded<PowerEvent>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
            _ = Task.Run(DeliverAsync);
        }

        source.Changed += OnTransition;
    }

    public event EventHandler<PowerEvent>? Changed;

    /// <summary>Stops listening; what was already reported is still delivered, and then the queue ends.</summary>
    public void Dispose()
    {
        _source.Changed -= OnTransition;
        _source.Dispose();
        _queue?.Writer.TryComplete();
    }

    private static IPowerTransitionSource WindowsSource(ILogger? log)
    {
        try
        {
            return new SuspendResumeNotifications();
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            log?.LogWarning(ex, "Windows did not register the suspend and resume notifications: the suspensions will not be seen.");
            return new NoTransitions();
        }
    }

    private void OnTransition(object? sender, PowerTransition transition)
    {
        var at = _time.GetUtcNow();
        PowerEvent power;
        lock (_gate)
        {
            if (transition == PowerTransition.Suspending)
            {
                _suspendedAt = (_clock.WithSleep(), _clock.WithoutSleep());
                _resumedSinceSuspension = false;
                power = new PowerEvent(PowerTransition.Suspending, at);
            }
            else
            {
                if (_resumedSinceSuspension)
                {
                    return;
                }

                _resumedSinceSuspension = true;
                TimeSpan? slept = null;
                if (_suspendedAt is { } then)
                {
                    var withSleep = _clock.WithSleep() - then.WithSleep;
                    var withoutSleep = _clock.WithoutSleep() - then.WithoutSleep;
                    slept = TimeSpan.FromMilliseconds(Math.Max(0, withSleep - withoutSleep));
                }

                _suspendedAt = null;
                power = new PowerEvent(PowerTransition.Resumed, at, slept);
            }

            // Queued under the same lock that ordered the transitions, so the queue keeps their order.
            if (_queue is { } queue)
            {
                queue.Writer.TryWrite(power);
                return;
            }
        }

        _raise!(() => Raise(power));
    }

    /// <summary>The single reader of the queue: runs the handlers of each event in turn, in the order they were reported.</summary>
    private async Task DeliverAsync()
    {
        await foreach (var power in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Raise(power);
        }
    }

    private void Raise(PowerEvent power)
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<EventHandler<PowerEvent>>() ?? [])
        {
            try
            {
                handler(this, power);
            }
#pragma warning disable CA1031 // A handler that fails must not keep the others from knowing the computer slept.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogError(ex, "A handler of the power transition {Transition} failed.", power.Transition);
            }
        }
    }

    /// <summary>What is left when Windows refused the registration: no transition is ever raised.</summary>
    private sealed class NoTransitions : IPowerTransitionSource
    {
        public event EventHandler<PowerTransition>? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
