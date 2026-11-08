using AgentStudio.Desktop.Lock;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The state of the Windows session, scripted (docs/17 D-40 (a)): no test ever locks the session of the person running it nor
/// reads it. Each read takes the next state of <see cref="States"/>, and the last one repeats.
/// </summary>
internal sealed class FakeWindowsSessionState : IWindowsSessionState
{
    private int _next;

    /// <summary>What the session manager says at each read, in order; the last one repeats.</summary>
    public List<SessionLockState> States { get; } = [SessionLockState.Unlocked];

    /// <summary>When set, a read throws it: Windows could not say.</summary>
    public Exception? ReadFails { get; set; }

    /// <summary>Whether Windows refuses to lock (<c>LockWorkStation</c> returning false).</summary>
    public bool Refuses { get; set; }

    /// <summary>How many times Windows was asked to lock the session.</summary>
    public int Locks { get; private set; }

    /// <summary>How many times the state was read.</summary>
    public int Reads { get; private set; }

    public static FakeWindowsSessionState Saying(params SessionLockState[] states)
    {
        var session = new FakeWindowsSessionState();
        session.States.Clear();
        session.States.AddRange(states);
        return session;
    }

    public bool Lock()
    {
        Locks++;
        return !Refuses;
    }

    public SessionLockState Read()
    {
        Reads++;
        if (ReadFails is { } failure)
        {
            throw failure;
        }

        var state = States[Math.Min(_next, States.Count - 1)];
        _next++;
        return state;
    }
}
