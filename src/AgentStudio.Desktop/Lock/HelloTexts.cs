using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Resources;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// The texts of the fixed barrier of Windows Hello (docs/17 D-37 to D-41, ADR-0053), from the resources of the area
/// (<c>Resources/HelloStrings.resx</c> and its satellites) in the language in use. The keys are named with <c>nameof</c>, so a
/// key that does not exist does not compile.
/// </summary>
public static class HelloTexts
{
    /// <summary>The setting of the configurable approvals, as «Ajustes» names it.</summary>
    public static string SettingLabel => Texts.Get(nameof(HelloStrings.Hello_Setting_Label));

    /// <summary>What the setting governs and what it never turns off, as «Ajustes» says it.</summary>
    public static string SettingExplanation => Texts.Get(nameof(HelloStrings.Hello_Setting_Explanation));

    /// <summary>What the question of an action of the list says: Windows Hello is asked whatever the setting says.</summary>
    public static string AlwaysAsksHello => Texts.Get(nameof(HelloStrings.Hello_Notice_AlwaysHello));

    /// <summary>What the question of an action of the list says on a computer without Windows Hello: Windows locks after «Aprobar».</summary>
    public static string LocksWindowsAfterYes => Texts.Get(nameof(HelloStrings.Hello_Notice_WindowsLock));

    /// <summary>What the review of a plan with a capability of the list says before it is approved.</summary>
    public static string PlanAlwaysAsks => Texts.Get(nameof(HelloStrings.Hello_Plan_AlwaysAsks));

    /// <summary>What the review of a plan says once the verified lock of Windows confirmed its approval. <paramref name="plan"/>: the short hash.</summary>
    public static string PlanApprovedWithLock(string plan) => Texts.Format(nameof(HelloStrings.Hello_Plan_ApprovedWithLock), plan);

    /// <summary>
    /// What to do when nothing can confirm the person on this computer (D-40 (b)): set up Windows Hello, and where. Without Hello,
    /// a Windows that does not lock (a policy forbids it) leaves the actions of the list without any way to be approved.
    /// </summary>
    public static string SetUpHello => Texts.Get(nameof(HelloStrings.Hello_SetUpHello));

    /// <summary>Why a plan was not approved: the verified lock of Windows did not confirm the person; when Windows did not lock, what to do.</summary>
    public static string PlanLockNotConfirmed(WindowsLockResult result) =>
        WithWhatToDo(Texts.Format(nameof(HelloStrings.Hello_Plan_LockNotConfirmed), WhyNotConfirmed(result)), result);

    /// <summary>Why a plan was not approved: Windows could not be locked to confirm the person; and what to do.</summary>
    public static string PlanLockNotAsked(string problem) => Texts.Format(nameof(HelloStrings.Hello_Plan_LockNotAsked), problem) + " " + SetUpHello;

    /// <summary>The reason of the denial of a yes the verified lock of Windows did not confirm, and what to do when Windows did not lock; the log keeps it.</summary>
    public static string ApprovalLockNotConfirmed(WindowsLockResult result) =>
        WithWhatToDo(Texts.Format(nameof(HelloStrings.Hello_Approval_LockNotConfirmed), result, WhyNotConfirmed(result)), result);

    /// <summary>The reason of the denial of a yes when Windows could not be locked to confirm the person, and what to do; the log keeps it.</summary>
    public static string ApprovalLockNotAsked(string problem) => Texts.Format(nameof(HelloStrings.Hello_Approval_LockNotAsked), problem) + " " + SetUpHello;

    /// <summary><paramref name="text"/> and, when Windows did not lock, what to do (<see cref="SetUpHello"/>).</summary>
    private static string WithWhatToDo(string text, WindowsLockResult result) =>
        result == WindowsLockResult.LockFailed ? text + " " + SetUpHello : text;

    /// <summary>Why the person cannot be confirmed: Windows Hello is not there and the verified lock of Windows is not composed.</summary>
    public static string LockNotComposed => Texts.Get(nameof(HelloStrings.Hello_LockNotComposed));

    /// <summary>What a result of the verified lock other than a confirmation means, as the screens and the log say it.</summary>
    public static string WhyNotConfirmed(WindowsLockResult result) => result switch
    {
        WindowsLockResult.UnlockedAfterRequest => Texts.Get(nameof(HelloStrings.Hello_LockResult_Unlocked)),
        WindowsLockResult.LockFailed => Texts.Get(nameof(HelloStrings.Hello_LockResult_LockFailed)),
        WindowsLockResult.NotVerified => Texts.Get(nameof(HelloStrings.Hello_LockResult_NotVerified)),
        WindowsLockResult.TimedOut => Texts.Get(nameof(HelloStrings.Hello_LockResult_TimedOut)),
        _ => Texts.Get(nameof(HelloStrings.Hello_LockResult_Unknown)),
    };
}
