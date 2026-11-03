using AgentStudio.Infrastructure.Clocks;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The clock of the orchestration engine (F8-5; docs/16 D-40, ADR-0034): <see cref="AwakeTimeProvider"/>, the wall clock and
/// the timers of the system with a timestamp that stops while the machine sleeps, so the engine finds the time asleep and
/// leaves it out of the silence of its attempts and of its waits. Only the engine gets it: the rest of the app keeps the clock
/// of the composition, whose timestamps count a sleep. The idle lock needs that (<c>InterfaceLock.SinceLastUnlock</c>): a
/// machine that slept is a machine nobody used, so it locks on waking; and so do the tray and the timestamps of the logs.
/// </summary>
public sealed record EngineClock(TimeProvider Time)
{
    /// <summary>
    /// The engine's clock in a composition whose clock is <paramref name="composed"/>: the awake clock when that is the
    /// system's (or there is none), and <paramref name="composed"/> itself otherwise, so a fake clock of a test drives the
    /// engine too.
    /// </summary>
    public static EngineClock For(TimeProvider? composed) =>
        new(composed is null || ReferenceEquals(composed, TimeProvider.System) ? new AwakeTimeProvider() : composed);
}
