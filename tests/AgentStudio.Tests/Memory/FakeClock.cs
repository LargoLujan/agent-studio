namespace AgentStudio.Tests.Memory;

/// <summary>Deterministic clock with a fixed +02:00 local zone, so local-day logic is exercised.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "test", "test");

    private DateTimeOffset _now = now;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public void Advance(TimeSpan by) => _now += by;
}
