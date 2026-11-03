namespace AgentStudio.Application.Execution;

// Phase 8 contracts (docs/16 D-40, F8-5): the power transitions of the computer, and keeping it awake while there is work.
// The desktop implements them (WP3, Desktop/Background) with what Windows reports; the engine (WP1) reads the transitions to
// re-evaluate its deadlines, the abandonment and its waits, discounting the time slept. Tests use doubles.

/// <summary>A change of the power state of the computer, as Windows reports it.</summary>
public enum PowerTransition
{
    /// <summary>The computer is about to sleep or hibernate.</summary>
    Suspending,

    /// <summary>The computer woke up.</summary>
    Resumed,
}

/// <summary>
/// One power transition. <paramref name="At"/> is the wall-clock time it was seen. For <see cref="PowerTransition.Resumed"/>,
/// <paramref name="Slept"/> is how long the computer slept as the implementation measured it, or null when it did not see the
/// suspension: then only the wall clock says it.
/// </summary>
public sealed record PowerEvent(PowerTransition Transition, DateTimeOffset At, TimeSpan? Slept = null);

/// <summary>The power transitions of the computer (docs/16 D-40).</summary>
public interface IPowerEvents
{
    /// <summary>
    /// Raised on each transition, on a thread of the implementation and never on the UI's: a handler posts to its own loop
    /// and returns at once (Windows waits for the app before it sleeps).
    /// </summary>
    event EventHandler<PowerEvent>? Changed;
}

/// <summary>
/// Keeps the computer from sleeping when idle while there is work going on, as an option (docs/16 D-40), with
/// <c>SetThreadExecutionState</c> and no administrator. It never stops the person from putting the computer to sleep,
/// closing the lid or shutting it down. On computers with modern standby its effect is not verified (docs/16 §8).
/// </summary>
public interface IKeepAwake
{
    /// <summary>
    /// Keeps the computer awake until the returned lease is disposed; disposing it twice is harmless. Several leases may be
    /// held at once, and the computer may sleep again when the last one is released. <paramref name="reason"/> says why, for
    /// the log of the app.
    /// </summary>
    IDisposable Hold(string reason);

    /// <summary>Whether a lease is held now.</summary>
    bool IsHeld { get; }
}
