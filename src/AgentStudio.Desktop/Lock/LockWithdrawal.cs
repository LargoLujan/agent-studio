namespace AgentStudio.Desktop.Lock;

/// <summary>
/// A question the lock of the interface withdraws (docs/16 D-13, ADR-0037), as <c>ApprovalPromptService</c> withdraws its own:
/// <see cref="Token"/> is cancelled when the interface locks, and already is when it is locked, so nothing is shown nor answered
/// over the lock screen. Linked to the caller's token too. Dispose it once the question is answered.
/// </summary>
public sealed class LockWithdrawal : IDisposable
{
    private readonly IInterfaceLock? _lock;
    private readonly CancellationTokenSource _source;

    /// <param name="interfaceLock">The lock; without it, only <paramref name="cancellationToken"/> withdraws the question.</param>
    /// <param name="cancellationToken">The caller's own withdrawal.</param>
    public LockWithdrawal(IInterfaceLock? interfaceLock, CancellationToken cancellationToken = default)
    {
        _lock = interfaceLock;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_lock is not null)
        {
            _lock.Changed += OnLockChanged;
            OnLockChanged(null, EventArgs.Empty);
        }
    }

    /// <summary>Cancelled once the interface locked (or the caller withdrew the question).</summary>
    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        if (_lock is not null)
        {
            _lock.Changed -= OnLockChanged;
        }

        _source.Dispose();
    }

    /// <summary>On the thread of the change: a lock cancels the token, an unlock changes nothing (the question stays withdrawn).</summary>
    private void OnLockChanged(object? sender, EventArgs e)
    {
        if (!_lock!.IsLocked)
        {
            return;
        }

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The question was answered and its withdrawal disposed while the lock was being applied on another thread.
        }
    }
}
