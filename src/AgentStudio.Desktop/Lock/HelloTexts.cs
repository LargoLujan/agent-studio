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

    // The three texts below are the reasons of a denial: the decision log keeps them, anchored and never translated (docs/17 D-48,
    // F14-4). They are fixed Spanish in code, not resources, so they do not change with the language of the interface.

    /// <summary>The reason of the denial of a yes the verified lock of Windows did not confirm, and what to do when Windows did not lock; the log keeps it.</summary>
    public static string ApprovalLockNotConfirmed(WindowsLockResult result)
    {
        var text = $"El bloqueo de Windows no confirmó que eres tú ({result}: {WhyNotConfirmedForLog(result)}): se deniega.";
        return result == WindowsLockResult.LockFailed ? text + " " + SetUpHelloForLog : text;
    }

    /// <summary>The reason of the denial of a yes when Windows could not be locked to confirm the person, and what to do; the log keeps it.</summary>
    public static string ApprovalLockNotAsked(string problem) =>
        $"No se pudo bloquear Windows para confirmar que eres tú ({problem}): se deniega. " + SetUpHelloForLog;

    /// <summary><paramref name="text"/> and, when Windows did not lock, what to do (<see cref="SetUpHello"/>).</summary>
    private static string WithWhatToDo(string text, WindowsLockResult result) =>
        result == WindowsLockResult.LockFailed ? text + " " + SetUpHello : text;

    /// <summary>The reason of the denial of a yes whose confirmation was not asked because another one was pending; the log keeps it.</summary>
    public static string ApprovalConfirmationPending =>
        "Ya hay una confirmación pendiente: termina primero la de Windows Hello o la del bloqueo de Windows que está abierta. Esta aprobación se deniega; vuelve a pedirla después.";

    private const string SetUpHelloForLog =
        "Para aprobarlo, configura Windows Hello: abre Configuración, entra en Cuentas y luego en Opciones de inicio de sesión, y elige el PIN, la huella o la cara.";

    private static string WhyNotConfirmedForLog(WindowsLockResult result) => result switch
    {
        WindowsLockResult.UnlockedAfterRequest => "lo desbloqueaste después de la petición",
        WindowsLockResult.LockFailed => "Windows no se bloqueó",
        WindowsLockResult.NotVerified => "el estado de la sesión no confirma que lo desbloqueaste después de la petición",
        WindowsLockResult.TimedOut => "nadie lo desbloqueó a tiempo",
        _ => "Windows dio un resultado que Agent Studio no conoce",
    };

    /// <summary>Why a plan was not approved: another confirmation of the person was pending.</summary>
    public static string PlanConfirmationPending => Texts.Get(nameof(HelloStrings.Hello_Plan_ConfirmationPending));

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
