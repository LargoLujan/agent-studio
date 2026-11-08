using AgentStudio.Application.Security;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The verified lock of Windows, scripted (docs/17 D-40 (a)): what it says when asked, and how often it was asked. No test ever
/// locks the session of the person running it.
/// </summary>
internal sealed class FakeWindowsLockConfirmation : IWindowsLockConfirmation
{
    public Func<TimeSpan, CancellationToken, Task<WindowsLockResult>> Answer { get; set; } =
        (_, _) => Task.FromResult(WindowsLockResult.UnlockedAfterRequest);

    /// <summary>The time each lock was given, in order: one per lock of Windows.</summary>
    public List<TimeSpan> Locks { get; } = [];

    public static FakeWindowsLockConfirmation Answering(WindowsLockResult result) =>
        new() { Answer = (_, _) => Task.FromResult(result) };

    public Task<WindowsLockResult> LockAndConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Locks.Add(timeout);
        return Answer(timeout, cancellationToken);
    }
}
