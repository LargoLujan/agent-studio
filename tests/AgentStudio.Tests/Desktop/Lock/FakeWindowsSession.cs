using AgentStudio.Desktop.Lock;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>The Windows session, scripted: no test ever locks the user's session nor listens to its events.</summary>
internal sealed class FakeWindowsSession : IWindowsSession
{
    public event EventHandler? Unlocked;

    /// <summary>How many times Windows was asked to lock the session.</summary>
    public int Locks { get; private set; }

    /// <summary>Whether Windows refuses to lock (LockWorkStation returning false).</summary>
    public bool Refuses { get; set; }

    public bool Lock()
    {
        Locks++;
        return !Refuses;
    }

    /// <summary>The person unlocked the Windows session (Windows verified them).</summary>
    public void RaiseUnlocked() => Unlocked?.Invoke(this, EventArgs.Empty);
}
