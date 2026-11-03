using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using AgentStudio.Application.Execution;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>Asks Windows to keep the computer from sleeping when idle, or lets it sleep again. A double in the tests.</summary>
public interface IExecutionState
{
    /// <summary>On: the computer does not sleep when idle (the person can still put it to sleep). Off: it may again.</summary>
    void KeepSystemAwake(bool on);
}

/// <summary>
/// <c>SetThreadExecutionState</c> from one thread of its own (docs/16 D-40, ADR-0036). The request belongs to the thread
/// that made it: it lasts while that thread lives and until that same thread clears it, so every call is made from this
/// one, which lives as long as the app. <c>ES_SYSTEM_REQUIRED</c> only: the display may still turn off, and nothing keeps
/// the computer awake once the request is cleared or the app ends. No administrator is needed.
/// </summary>
public sealed class ExecutionStateThread : IExecutionState, IDisposable
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<(bool On, TaskCompletionSource Done)> _requests = new();
    private readonly Func<uint, uint> _native;
    private readonly Thread _thread;
    private int _disposed;

    /// <summary>The calls go to Windows.</summary>
    public ExecutionStateThread()
        : this(NativeMethods.SetThreadExecutionState)
    {
    }

    /// <param name="native">The call itself; the tests give one that only records the thread and the flags.</param>
    public ExecutionStateThread(Func<uint, uint> native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Agent Studio · mantener despierto" };
        _thread.Start();
    }

    /// <summary>The flags of a request: on is <c>ES_CONTINUOUS | ES_SYSTEM_REQUIRED</c>, off is <c>ES_CONTINUOUS</c> alone.</summary>
    public static uint Flags(bool on) => on ? Continuous | SystemRequired : Continuous;

    /// <summary>Makes the request on the thread of its own and waits for it.</summary>
    /// <exception cref="ObjectDisposedException">The thread was stopped.</exception>
    /// <exception cref="TimeoutException">The thread did not answer.</exception>
    /// <exception cref="InvalidOperationException">Windows did not accept the request (<c>SetThreadExecutionState</c> returned 0).</exception>
    public void KeepSystemAwake(bool on)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add((on, done));
        try
        {
            if (!done.Task.Wait(Wait))
            {
                throw new TimeoutException("The thread that keeps the computer awake did not answer.");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
    }

    /// <summary>Clears the request and ends the thread (its end also clears whatever it held).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.CompleteAdding();
        _thread.Join(Wait);
        _requests.Dispose();
    }

    private void Loop()
    {
        foreach (var (on, done) in _requests.GetConsumingEnumerable())
        {
            try
            {
                // Windows answers the state it had before, or 0 when it did not take the request: never a silent success.
                if (_native(Flags(on)) == 0)
                {
                    done.TrySetException(new InvalidOperationException(on
                        ? "Windows did not accept the request to keep the computer awake (SetThreadExecutionState returned 0)."
                        : "Windows did not accept the request to let the computer sleep again (SetThreadExecutionState returned 0)."));
                    continue;
                }

                done.TrySetResult();
            }
#pragma warning disable CA1031 // The caller is told; this thread must keep serving the next request.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.TrySetException(ex);
            }
        }

        _native(Flags(on: false));
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint SetThreadExecutionState(uint flags);
    }
}

/// <summary>
/// A lease of «mantener despierto» that can say why it ends, for the app's log (V26): the log said why the computer was
/// kept awake when it was let sleep again, not why it was let go. Disposing it releases it too, without a reason.
/// </summary>
public interface IKeepAwakeLease : IDisposable
{
    /// <summary>Releases the lease, as disposing it does, saying why for the log; a lease already released does nothing.</summary>
    void Release(string why);
}

/// <summary>
/// «Mantener despierto» (the port of P0, docs/16 D-40): any number of leases at once; the computer is kept awake from the
/// first and may sleep again when the last is released. Each lease is released once, however often it is disposed. Its
/// leases are <see cref="IKeepAwakeLease"/>: the log says that the computer may sleep again only when the last one goes,
/// and why it went when its holder says so.
/// </summary>
public sealed class KeepAwakeService : IKeepAwake
{
    private readonly IExecutionState _state;
    private readonly ILogger? _log;
    // Qualified: the namespace AgentStudio.Desktop.Lock (WP4) hides System.Threading.Lock inside AgentStudio.Desktop.
    private readonly System.Threading.Lock _gate = new();
    private int _held;

    public KeepAwakeService(IExecutionState state, ILogger<KeepAwakeService>? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _log = log;
    }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _held > 0;
            }
        }
    }

    /// <summary>
    /// Takes a lease. If Windows does not accept the request, the lease is still counted (its release keeps the count right)
    /// and the app's log says so: the work goes on, and the computer may sleep when idle.
    /// </summary>
    public IDisposable Hold(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_held == 0)
            {
                Ask(true, reason);
            }

            _held++;
        }

        _log?.LogInformation("The computer is kept awake: {Reason}", reason);
        return new Lease(this, reason);
    }

    /// <param name="reason">Why the lease was taken.</param>
    /// <param name="why">Why it ends, when its holder said so; null when it was only disposed.</param>
    private void Release(string reason, string? why)
    {
        int left;
        lock (_gate)
        {
            _held--;
            left = _held;
            if (_held == 0)
            {
                Ask(false, reason);
            }
        }

        if (left > 0)
        {
            _log?.LogInformation("A request to keep the computer awake ended ({Why}); other requests still hold it: {Left}.", why ?? reason, left);
        }
        else if (why is not null)
        {
            _log?.LogInformation("The computer may sleep again: {Why}.", why);
        }
        else
        {
            _log?.LogInformation("The computer may sleep again: the request to keep it awake was released ({Reason}).", reason);
        }
    }

    /// <summary>Asks Windows; a refusal or a thread that does not answer is written in the app's log, never ignored.</summary>
    private void Ask(bool on, string reason)
    {
        try
        {
            _state.KeepSystemAwake(on);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (on)
            {
                _log?.LogWarning(ex, "Windows did not keep the computer awake ({Reason}).", reason);
            }
            else
            {
                _log?.LogWarning(ex, "Windows did not let the computer sleep again ({Reason}).", reason);
            }
        }
    }

    private sealed class Lease(KeepAwakeService owner, string reason) : IKeepAwakeLease
    {
        private int _released;

        public void Dispose() => End(null);

        public void Release(string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            End(why);
        }

        private void End(string? why)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(reason, why);
            }
        }
    }
}

/// <summary>
/// Keeps the computer awake while there is work going on and the setting «Impedir la suspensión mientras hay trabajo» is on
/// (<c>PreventSleepWhileWorking</c>, D-40): one lease, taken when both hold and released as soon as one does not. The
/// setting is read when this starts and again on <see cref="Refresh"/> (the shell calls it when the settings are saved).
/// The log says why the lease is taken and, when it is released, why (V26): the work ended, the setting was turned off or
/// the app is closing.
/// </summary>
public sealed class KeepAwakeWhileWorking : IDisposable
{
    /// <summary>Why the computer is kept awake, for the app's log.</summary>
    public const string Reason = "hay trabajo en marcha y el ajuste «Impedir la suspensión mientras hay trabajo» está activo";

    /// <summary>Why the computer may sleep again when no work goes on any more, for the app's log.</summary>
    public const string NoWorkReason = "ya no hay trabajo en marcha";

    /// <summary>Why the computer may sleep again when the setting was turned off, for the app's log.</summary>
    public const string SettingOffReason = "se desactivó el ajuste «Impedir la suspensión mientras hay trabajo»";

    /// <summary>Why the computer may sleep again when the app stops following the work, for the app's log.</summary>
    public const string ClosingReason = "Agent Studio se cierra";

    private readonly BackgroundWorkRegistry _work;
    private readonly IKeepAwake _keepAwake;
    private readonly Func<bool> _enabled;
    private bool _isEnabled;
    private IDisposable? _lease;
    private bool _disposed;

    /// <param name="enabled">Whether the setting is on (<c>IAppSettingsStore.Load().PreventSleepWhileWorking</c>).</param>
    public KeepAwakeWhileWorking(BackgroundWorkRegistry work, IKeepAwake keepAwake, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(keepAwake);
        ArgumentNullException.ThrowIfNull(enabled);
        _work = work;
        _keepAwake = keepAwake;
        _enabled = enabled;
        _isEnabled = enabled();
        work.Changed += OnChanged;
        Evaluate();
    }

    /// <summary>Whether it holds a lease now.</summary>
    public bool IsHolding => _lease is not null;

    /// <summary>Reads the setting again and holds or releases accordingly.</summary>
    public void Refresh()
    {
        _isEnabled = _enabled();
        Evaluate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.Changed -= OnChanged;
        if (_lease is { } lease)
        {
            _lease = null;
            Release(lease, ClosingReason);
        }
    }

    /// <summary>Releases <paramref name="lease"/> saying why, when the lease can say it; otherwise only disposes it.</summary>
    private static void Release(IDisposable lease, string why)
    {
        if (lease is IKeepAwakeLease explained)
        {
            explained.Release(why);
        }
        else
        {
            lease.Dispose();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Evaluate();

    private void Evaluate()
    {
        if (_disposed)
        {
            return;
        }

        var hold = _isEnabled && _work.HasActiveWork;
        if (hold && _lease is null)
        {
            _lease = _keepAwake.Hold(Reason);
        }
        else if (!hold && _lease is { } lease)
        {
            _lease = null;
            Release(lease, _isEnabled ? NoWorkReason : SettingOffReason);
        }
    }
}
