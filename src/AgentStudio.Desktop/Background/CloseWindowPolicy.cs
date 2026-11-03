using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.Background;

/// <summary>What closing the main window does, once the setting and the work going on are known (docs/16 D-14).</summary>
public enum CloseWindowDecision
{
    /// <summary>Hide the window: the app, and whatever is going on, stay in the tray. Nothing is asked.</summary>
    HideToTray,

    /// <summary>
    /// There is work going on and the setting says «preguntar»: keep it going in the tray, pause and exit when the attempts
    /// going end, exit now, or cancel.
    /// </summary>
    AskKeepOrExit,

    /// <summary>The setting says «salir» and there is work going on: the managed exit asks how to leave it (D-16).</summary>
    ManagedExit,

    /// <summary>Nothing is going on: the app exits, and there is nothing to ask.</summary>
    Exit,
}

/// <summary>
/// The rule of closing the main window (docs/16 D-14), apart from the window so it can be tested whole: «seguir en la
/// bandeja» always hides it, with or without work; «salir» exits, through the managed exit when there is work; and
/// «preguntar», the default, asks only when there is work and exits when there is none.
/// </summary>
public static class CloseWindowPolicy
{
    public static CloseWindowDecision Decide(CloseWindowAction setting, bool hasActiveWork) => setting switch
    {
        CloseWindowAction.KeepInTray => CloseWindowDecision.HideToTray,
        CloseWindowAction.Exit => hasActiveWork ? CloseWindowDecision.ManagedExit : CloseWindowDecision.Exit,

        // «Preguntar», and a value that is not one of the three (the store already reads a nonsense one as Ask).
        _ => hasActiveWork ? CloseWindowDecision.AskKeepOrExit : CloseWindowDecision.Exit,
    };
}
