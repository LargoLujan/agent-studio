using AgentStudio.Application.Security;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// Windows Hello, scripted: what Windows says of its availability and what it answers when asked. It remembers what it was
/// asked, on which context and with which token. No test ever asks the real Windows Hello.
/// </summary>
internal sealed class FakeUserPresenceVerifier : IUserPresenceVerifier
{
    public Func<CancellationToken, Task<UserPresenceAvailability>> Availability { get; set; } =
        _ => Task.FromResult(UserPresenceAvailability.Available);

    public Func<string, CancellationToken, Task<UserPresenceResult>> Verify { get; set; } =
        (_, _) => Task.FromResult(UserPresenceResult.Verified);

    public int AvailabilityChecks { get; private set; }

    /// <summary>The messages Windows Hello was asked with, in order: one per verification.</summary>
    public List<string> Messages { get; } = [];

    /// <summary>The UI context of each verification.</summary>
    public List<SynchronizationContext?> VerifiedOn { get; } = [];

    public CancellationToken LastToken { get; private set; }

    public static FakeUserPresenceVerifier Answering(UserPresenceResult result) =>
        new() { Verify = (_, _) => Task.FromResult(result) };

    public static FakeUserPresenceVerifier Without(UserPresenceAvailability availability) =>
        new() { Availability = _ => Task.FromResult(availability) };

    public Task<UserPresenceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        AvailabilityChecks++;
        return Availability(cancellationToken);
    }

    public Task<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        VerifiedOn.Add(SynchronizationContext.Current);
        LastToken = cancellationToken;
        return Verify(message, cancellationToken);
    }
}
