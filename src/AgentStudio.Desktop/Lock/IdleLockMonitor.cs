using System.ComponentModel;
using System.Diagnostics;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// The idle lock (docs/16 D-13, F8-10; ADR-0037): with <see cref="AppSettings.IdleLockEnabled"/> on, the interface locks after
/// <see cref="AppSettings.IdleLockMinutes"/> without input in the whole session, and the work goes on. Off by default.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The settings are read at each check, so a change in «Ajustes» counts at the next one. A time out of range (only a
/// value built by hand can have one) counts as the default.</item>
/// <item>The time without input counts from the last unlock at most: after an unlock it never locks again at once, even when
/// Windows saw no input since. That time is measured (<see cref="InterfaceLock.SinceLastUnlock"/>), never read from the wall
/// clock, which can go back. And a locked interface is not locked again: without Windows Hello, Windows is locked once.</item>
/// <item>Each check asks Windows whether Hello is there with a time limit (<see cref="HelloAnswers.AvailabilityTimeout"/>):
/// a Windows that never answers never stops the checks that follow.</item>
/// <item>When Windows cannot say when the last input was, nothing is locked: locking a person who is working would be worse
/// than the lock it misses, and the next check tries again.</item>
/// </list>
/// </remarks>
public sealed class IdleLockMonitor : IDisposable
{
    /// <summary>How often it checks: the interface locks at most this late.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly InterfaceLock _lock;
    private readonly IIdleInput _input;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private ITimer? _timer;
    private int _checking;
    private bool _disposed;

    /// <param name="settings">The settings in force, read at each check (the app's store, <c>IAppSettingsStore.Load</c>).</param>
    /// <param name="pollInterval">How often it checks; <see cref="DefaultPollInterval"/> by default.</param>
    public IdleLockMonitor(InterfaceLock interfaceLock, IIdleInput input, Func<AppSettings> settings, TimeProvider time, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(interfaceLock);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        var interval = pollInterval ?? DefaultPollInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(pollInterval));
        _lock = interfaceLock;
        _input = input;
        _settings = settings;
        _time = time;
        _interval = interval;
    }

    /// <summary>Starts checking every interval; calling it again does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => OnTick(), null, _interval, _interval);
        }
    }

    /// <summary>One check: locks the interface when the idle lock is on and the time without input has come.</summary>
    /// <returns>Whether it locked the interface now.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (!settings.IdleLockEnabled || _lock.IsLocked)
        {
            return false;
        }

        var minutes = AppSettings.IsValidIdleLockMinutes(settings.IdleLockMinutes) ? settings.IdleLockMinutes : AppSettings.DefaultIdleLockMinutes;
        TimeSpan idle;
        try
        {
            idle = _input.IdleFor();
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"Idle lock: Windows could not say when the last input was ({ex.Message}); nothing is locked.");
            return false;
        }

        // Measured with timestamps, never with the wall clock: a clock set back would keep the interface unlocked.
        var sinceUnlock = _lock.SinceLastUnlock;
        var quiet = idle < sinceUnlock ? idle : sinceUnlock;
        if (quiet < TimeSpan.FromMinutes(minutes))
        {
            return false;
        }

        var attempt = await _lock.LockAsync(LockReason.Idle, cancellationToken).ConfigureAwait(false);
        return attempt.Outcome != LockOutcome.AlreadyLocked;
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private void OnTick()
    {
        // One check at a time: a slow answer of Windows never piles checks up.
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        _ = RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        try
        {
            await CheckAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A timer callback must not throw; the next check tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Idle lock: the check failed ({ex.Message}).");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }
}
