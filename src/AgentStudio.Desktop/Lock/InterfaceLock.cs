using AgentStudio.Application.Security;

namespace AgentStudio.Desktop.Lock;

/// <summary>Why the interface locked.</summary>
public enum LockReason
{
    /// <summary>The person locked it («Bloquear»).</summary>
    Manual,

    /// <summary>No input for the time of the idle lock (<c>AppSettings.IdleLockMinutes</c>).</summary>
    Idle,
}

/// <summary>A question waiting for the unlock, as the lock screen shows it: its action, its risk and since when. Nothing else.</summary>
public sealed record WaitingQuestion(string Action, RiskLevel Risk, DateTimeOffset Since);

public enum LockOutcome
{
    /// <summary>The interface locked; Windows Hello unlocks it.</summary>
    Locked,

    /// <summary>It was locked already: nothing changed.</summary>
    AlreadyLocked,

    /// <summary>Windows Hello is not there: the interface locked and Windows locked too; the unlock of Windows unlocks it.</summary>
    LockedWithWindows,

    /// <summary>Windows Hello is not there and Windows refused to lock: the interface is locked, and «Bloquear Windows» can be tried again.</summary>
    WindowsRefused,
}

/// <param name="HelloAbsent">Why Windows Hello is not there, when Windows said so.</param>
public sealed record LockAttempt(LockOutcome Outcome, UserPresenceAvailability? HelloAbsent = null);

public enum UnlockOutcome
{
    /// <summary>Windows Hello confirmed the person: the interface is unlocked.</summary>
    Unlocked,

    /// <summary>The interface was not locked: nothing was asked.</summary>
    NotLocked,

    /// <summary>Windows says Hello is not there: it stays locked, and «Bloquear Windows» unlocks it through Windows.</summary>
    HelloAbsent,

    /// <summary>Windows Hello did not confirm the person (cancelled, failed...): it stays locked.</summary>
    NotConfirmed,

    /// <summary>Windows Hello could not be asked: it stays locked.</summary>
    HelloFailed,
}

/// <param name="Availability">Why Windows Hello is not there (<see cref="UnlockOutcome.HelloAbsent"/>).</param>
/// <param name="Result">What Windows Hello answered (<see cref="UnlockOutcome.NotConfirmed"/>).</param>
/// <param name="Problem">Why it could not be asked (<see cref="UnlockOutcome.HelloFailed"/>).</param>
public sealed record UnlockAttempt(UnlockOutcome Outcome, UserPresenceAvailability? Availability = null, UserPresenceResult? Result = null, string? Problem = null);

/// <summary>
/// The lock of the interface (docs/16 D-13, F8-10; ADR-0037): manual or after a time without input, it hides the content
/// while the work goes on, and the approvals wait for the unlock. Windows Hello unlocks it; the app never asks for the
/// Windows password, and only a result of Windows ever reaches it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="LockAsync"/> locks at once, then asks Windows whether Hello is there, for
/// <see cref="HelloAnswers.AvailabilityTimeout"/> at most: a Windows that never answers leaves it locked and lets it end. When
/// Windows says for certain that it is not (<see cref="HelloAnswers.IsAbsent"/>), it also locks Windows
/// (<see cref="IWindowsSession.Lock"/>): «sin Hello, Bloquear Windows».</item>
/// <item>The unlock of Windows unlocks the interface only when the interface was locked and the app itself asked Windows to
/// lock (<see cref="WaitsForWindows"/>): then Windows has just verified the person. An unlock of Windows the app did not ask
/// for (the person pressed Win+L, say) leaves it locked, so a forged notice of an unlock needs the session locked.</item>
/// <item>Without Hello while locked, the lock screen offers «Bloquear Windows» (<see cref="LockWindows"/>), and that unlock
/// unlocks it: Hello missing never leaves the interface locked for good, and the app never unlocks by itself.</item>
/// <item>Each unlocked period has its token (<see cref="WaitUntilUnlockedAsync"/>), cancelled when the interface locks: the
/// approval open then is withdrawn. Thread-safe: the approvals, the idle monitor and Windows' notices come from any thread,
/// and <see cref="Changed"/> is raised on the thread of the change, never under the lock.</item>
/// </list>
/// </remarks>
public sealed class InterfaceLock : IInterfaceLock, IDisposable
{
    private readonly object _gate = new();
    private readonly IUserPresenceVerifier _verifier;
    private readonly IWindowsSession _session;
    private readonly TimeProvider _time;
    private readonly List<WaitingQuestion> _waiting = [];

    private CancellationTokenSource _period = new();
    private TaskCompletionSource _unlocked = Completed();
    private bool _locked;
    private LockReason? _reason;
    private DateTimeOffset? _lockedAt;
    private DateTimeOffset _lastUnlockedAt;
    private long _unlockedTimestamp;
    private bool _waitsForWindows;
    private UserPresenceAvailability? _helloAbsent;
    private bool _disposed;

    public InterfaceLock(IUserPresenceVerifier verifier, IWindowsSession session, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        _verifier = verifier;
        _session = session;
        _time = time;
        _lastUnlockedAt = time.GetUtcNow();
        _unlockedTimestamp = time.GetTimestamp();
        session.Unlocked += OnWindowsUnlocked;
    }

    /// <summary>Raised when what the lock screen shows changes: locked or unlocked, the questions waiting, Windows Hello missing, Windows locked.</summary>
    public event EventHandler? Changed;

    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    /// <summary>Why it is locked; null while unlocked.</summary>
    public LockReason? Reason
    {
        get
        {
            lock (_gate)
            {
                return _reason;
            }
        }
    }

    /// <summary>Since when it is locked; null while unlocked.</summary>
    public DateTimeOffset? LockedAt
    {
        get
        {
            lock (_gate)
            {
                return _lockedAt;
            }
        }
    }

    /// <summary>When it was last unlocked (or created), on the wall clock: what the screens say.</summary>
    public DateTimeOffset LastUnlockedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastUnlockedAt;
            }
        }
    }

    /// <summary>
    /// How long since it was last unlocked (or created), measured with the timestamps of the <see cref="TimeProvider"/>, which
    /// never go back: the time without input of the idle lock counts from here at most, whatever the wall clock does.
    /// </summary>
    public TimeSpan SinceLastUnlock
    {
        get
        {
            long unlocked;
            lock (_gate)
            {
                unlocked = _unlockedTimestamp;
            }

            return _time.GetElapsedTime(unlocked);
        }
    }

    /// <summary>Whether the app asked Windows to lock while the interface was locked: the unlock of Windows then unlocks it.</summary>
    public bool WaitsForWindows
    {
        get
        {
            lock (_gate)
            {
                return _waitsForWindows;
            }
        }
    }

    /// <summary>Why Windows Hello is not there, when Windows said so while locked; null otherwise.</summary>
    public UserPresenceAvailability? HelloAbsent
    {
        get
        {
            lock (_gate)
            {
                return _helloAbsent;
            }
        }
    }

    /// <summary>The questions waiting for the unlock, oldest first.</summary>
    public IReadOnlyList<WaitingQuestion> Waiting
    {
        get
        {
            lock (_gate)
            {
                return [.. _waiting];
            }
        }
    }

    /// <summary>
    /// Locks the interface at once; when Windows says Hello is not there, locks Windows too, so the unlock of Windows unlocks
    /// it. The work goes on, and the question open now is withdrawn until the unlock.
    /// </summary>
    public async Task<LockAttempt> LockAsync(LockReason reason, CancellationToken cancellationToken)
    {
        CancellationTokenSource ended;
        lock (_gate)
        {
            if (_locked)
            {
                return new LockAttempt(LockOutcome.AlreadyLocked);
            }

            _locked = true;
            _reason = reason;
            _lockedAt = _time.GetUtcNow();
            _unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ended = _period;
        }

        // Outside the lock: cancelling runs the callbacks of the open question (its window closes). Not disposed: the question
        // may still be linking to its token.
        ended.Cancel();
        OnChanged();

        var absent = await AbsentAsync(cancellationToken).ConfigureAwait(false);
        if (absent is null)
        {
            return new LockAttempt(LockOutcome.Locked);
        }

        return LockWindows() ? new LockAttempt(LockOutcome.LockedWithWindows, absent) : new LockAttempt(LockOutcome.WindowsRefused, absent);
    }

    /// <summary>
    /// Unlocks with Windows Hello: only <see cref="UserPresenceResult.Verified"/> unlocks. When Windows says Hello is not there,
    /// nothing is asked and it stays locked: «Bloquear Windows» (<see cref="LockWindows"/>) is then the way.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<UnlockAttempt> UnlockWithHelloAsync(CancellationToken cancellationToken)
    {
        if (!IsLocked)
        {
            return new UnlockAttempt(UnlockOutcome.NotLocked);
        }

        if (await AbsentAsync(cancellationToken).ConfigureAwait(false) is { } absent)
        {
            return new UnlockAttempt(UnlockOutcome.HelloAbsent, Availability: absent);
        }

        UserPresenceResult result;
        try
        {
            result = await _verifier.VerifyAsync(HelloAnswers.UnlockMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Windows Hello could not be asked: it stays locked, and the screen says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new UnlockAttempt(UnlockOutcome.HelloFailed, Problem: ex.Message);
        }

        if (result != UserPresenceResult.Verified)
        {
            return new UnlockAttempt(UnlockOutcome.NotConfirmed, Result: result);
        }

        Unlock();
        return new UnlockAttempt(UnlockOutcome.Unlocked);
    }

    /// <summary>
    /// «Bloquear Windows» (<c>LockWorkStation</c>): Windows shows its own lock screen and the work goes on. While the interface
    /// is locked, the unlock of Windows, which verifies the person, unlocks it too.
    /// </summary>
    /// <returns>False when Windows refused to lock.</returns>
    public bool LockWindows()
    {
        // Armed before asking, so an unlock of Windows that comes back quickly is not missed.
        lock (_gate)
        {
            _waitsForWindows = _locked;
        }

        var accepted = _session.Lock();
        if (!accepted)
        {
            lock (_gate)
            {
                _waitsForWindows = false;
            }
        }

        OnChanged();
        return accepted;
    }

    public Task<CancellationToken> WaitUntilUnlockedAsync(ApprovalRequest question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        cancellationToken.ThrowIfCancellationRequested();
        WaitingQuestion waiting;
        TaskCompletionSource unlocked;
        lock (_gate)
        {
            if (!_locked)
            {
                return Task.FromResult(_period.Token);
            }

            waiting = new WaitingQuestion(question.Action, question.Risk, _time.GetUtcNow());
            _waiting.Add(waiting);
            unlocked = _unlocked;
        }

        OnChanged();
        return WaitAsync(waiting, unlocked, cancellationToken);
    }

    public void Dispose()
    {
        CancellationTokenSource period;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            period = _period;
        }

        _session.Unlocked -= OnWindowsUnlocked;
        period.Dispose();
    }

    private async Task<CancellationToken> WaitAsync(WaitingQuestion waiting, TaskCompletionSource unlocked, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await unlocked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_locked)
                    {
                        return _period.Token;
                    }

                    // It locked again before this question woke up: it keeps waiting.
                    unlocked = _unlocked;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                var index = _waiting.FindIndex(w => ReferenceEquals(w, waiting));
                if (index >= 0)
                {
                    _waiting.RemoveAt(index);
                }
            }

            OnChanged();
        }
    }

    /// <summary>
    /// Why Windows Hello is not there when Windows says so for certain; null when it is there, busy, or Windows could not say
    /// (an error, or no answer within <see cref="HelloAnswers.AvailabilityTimeout"/>): then Hello is asked, as if it were there.
    /// </summary>
    private async Task<UserPresenceAvailability?> AbsentAsync(CancellationToken cancellationToken)
    {
        var availability = await HelloAnswers.AvailabilityAsync(_verifier, _time, cancellationToken).ConfigureAwait(false);
        var absent = availability is { } said && HelloAnswers.IsAbsent(said) ? availability : null;

        bool changed;
        lock (_gate)
        {
            changed = _locked && _helloAbsent != absent;
            if (_locked)
            {
                _helloAbsent = absent;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return absent;
    }

    private void OnWindowsUnlocked(object? sender, EventArgs e)
    {
        bool unlock;
        lock (_gate)
        {
            unlock = !_disposed && _locked && _waitsForWindows;
        }

        if (unlock)
        {
            Unlock();
        }
    }

    private void Unlock()
    {
        TaskCompletionSource unlocked;
        lock (_gate)
        {
            if (!_locked)
            {
                return;
            }

            _locked = false;
            _reason = null;
            _lockedAt = null;
            _waitsForWindows = false;
            _helloAbsent = null;
            _lastUnlockedAt = _time.GetUtcNow();
            _unlockedTimestamp = _time.GetTimestamp();
            _period = new CancellationTokenSource();
            unlocked = _unlocked;
        }

        unlocked.TrySetResult();
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}

/// <summary>The Windows session, as the lock of the interface needs it (ADR-0037). The app's is <see cref="WindowsSession"/>.</summary>
public interface IWindowsSession
{
    /// <summary>Raised when the person unlocked the Windows session: Windows verified them.</summary>
    event EventHandler? Unlocked;

    /// <summary>Locks the Windows session (<c>LockWorkStation</c>): Windows shows its own lock screen and the work goes on.</summary>
    /// <returns>False when Windows refused.</returns>
    bool Lock();
}
