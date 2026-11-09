using System.Windows.Automation;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Live;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// How the live state of an agent is said (docs/17 D-14 and D-17): the state in words, how a change is announced, and the
/// counter of the top bar. Every sentence is from the resources; the colour of a state is the tone <see cref="ToneOf"/>, and it
/// never goes alone.
/// </summary>
public static class AgentLiveTexts
{
    /// <summary>The state in words: «Trabajando en T3», «Esperando cuota hasta las 14:05», «Te pregunta algo: espera tu respuesta».</summary>
    public static string Status(AgentLiveState state, TimeZoneInfo zone, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);
        var t = texts ?? Texts.Current;
        return state.Status switch
        {
            AgentLiveStatus.Idle => t.Get("Monitor_AgentStatus_Idle"),
            AgentLiveStatus.Queued => t.Get("Monitor_AgentStatus_Queued"),
            AgentLiveStatus.Working => state.Task is { } task ? t.Format("Monitor_AgentStatus_Working", task.Value) : t.Get("Monitor_AgentStatus_WorkingNoTask"),
            AgentLiveStatus.WaitingForQuota => state.WaitingUntil is { } until
                ? t.Format("Monitor_AgentStatus_WaitingForQuota", TimeZoneInfo.ConvertTime(until, zone).ToString("HH:mm", t.Culture))
                : t.Get("Monitor_AgentStatus_WaitingForQuotaNoTime"),
            AgentLiveStatus.Asking => t.Get("Monitor_AgentStatus_Asking"),
            AgentLiveStatus.AwaitingApproval => t.Get("Monitor_AgentStatus_AwaitingApproval"),
            AgentLiveStatus.Blocked => string.IsNullOrWhiteSpace(state.BlockReason) ? t.Get("Monitor_AgentStatus_Blocked") : t.Format("Monitor_AgentStatus_BlockedWhy", state.BlockReason),
            AgentLiveStatus.Finished => t.Get("Monitor_AgentStatus_Finished"),
            _ => state.Status.ToString(),
        };
    }

    /// <summary>The tone of the dot of a state (D-14): the icon and the text carry the same.</summary>
    public static Tone ToneOf(AgentLiveStatus status) => status switch
    {
        AgentLiveStatus.Working => Tone.Active,
        AgentLiveStatus.Queued or AgentLiveStatus.WaitingForQuota or AgentLiveStatus.Asking or AgentLiveStatus.AwaitingApproval => Tone.Warning,
        AgentLiveStatus.Blocked => Tone.Error,
        AgentLiveStatus.Finished => Tone.Success,
        _ => Tone.Neutral,
    };

    /// <summary>
    /// How a change of the state is announced by the screen reader (D-17): <c>Assertive</c> for a question and for a block,
    /// <c>Polite</c> for the rest.
    /// </summary>
    public static AutomationLiveSetting LiveSetting(AgentLiveStatus status) =>
        status is AgentLiveStatus.Asking or AgentLiveStatus.Blocked ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    /// <summary>
    /// The counter of the agents of the top bar (D-43): «Agentes: 2 trabajando · 1 te pregunta · 1 bloqueado». Only the states
    /// that have an agent are said, in this order; with none of them, «ninguno trabajando».
    /// </summary>
    public static string Counter(RunLiveState state, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var t = texts ?? Texts.Current;
        var parts = new List<string>();
        void Add(AgentLiveStatus status, string key)
        {
            if (state.CountOf(status) is > 0 and var count)
            {
                parts.Add(t.Format(key, count));
            }
        }

        Add(AgentLiveStatus.Working, "Monitor_Counter_Working");
        Add(AgentLiveStatus.Asking, "Monitor_Counter_Asking");
        Add(AgentLiveStatus.AwaitingApproval, "Monitor_Counter_AwaitingApproval");
        Add(AgentLiveStatus.WaitingForQuota, "Monitor_Counter_WaitingForQuota");
        Add(AgentLiveStatus.Blocked, "Monitor_Counter_Blocked");
        Add(AgentLiveStatus.Queued, "Monitor_Counter_Queued");
        return t.Format("Monitor_AgentCard_Counter", parts.Count == 0 ? t.Get("Monitor_Counter_None") : string.Join(" · ", parts));
    }
}
