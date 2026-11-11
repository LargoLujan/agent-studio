using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// Why Windows Hello is not there, or did not confirm the person, as the screens of the orchestrator say it, in the language of the
/// interface. The log keeps its own, fixed text (<c>HelloAnswers</c>, <see cref="OrchestratorProtocolTexts"/>): this one is only for
/// what is on screen, so an English screen never gets a Spanish reason inside.
/// </summary>
public static class HelloScreenTexts
{
    /// <summary>Why Windows Hello is not there.</summary>
    public static string WhyAbsent(UserPresenceAvailability availability) => Texts.Get(availability switch
    {
        UserPresenceAvailability.NotConfiguredForUser => "Orchestrator_HelloWhy_NotConfigured",
        UserPresenceAvailability.DeviceNotPresent => "Orchestrator_HelloWhy_NoDevice",
        UserPresenceAvailability.DisabledByPolicy => "Orchestrator_HelloWhy_Policy",
        UserPresenceAvailability.DeviceBusy => "Orchestrator_HelloWhy_Busy",
        _ => "Orchestrator_HelloWhy_AbsentUnknown",
    });

    /// <summary>What a result other than a confirmation means.</summary>
    public static string WhyNotConfirmed(UserPresenceResult result) => Texts.Get(result switch
    {
        UserPresenceResult.Canceled => "Orchestrator_HelloWhy_Canceled",
        UserPresenceResult.RetriesExhausted => "Orchestrator_HelloWhy_Retries",
        UserPresenceResult.DeviceBusy => "Orchestrator_HelloWhy_BusyBefore",
        UserPresenceResult.DeviceNotPresent => "Orchestrator_HelloWhy_NoDevice",
        UserPresenceResult.NotConfiguredForUser => "Orchestrator_HelloWhy_NotConfigured",
        UserPresenceResult.DisabledByPolicy => "Orchestrator_HelloWhy_Policy",
        UserPresenceResult.Verified => "Orchestrator_HelloWhy_Verified",
        _ => "Orchestrator_HelloWhy_NotConfirmedUnknown",
    });
}
