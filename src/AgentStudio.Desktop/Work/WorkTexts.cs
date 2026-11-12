using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// The words of the «Trabajo» document (ADR-0054): every code the layers below speak (columns, states, roles, stages, the reasons a
/// card is where it is, the rules a close was refused by) is said here with the resources, in the language of the interface. A code
/// that has no text is shown as itself, never as nothing.
/// </summary>
public static class WorkTexts
{
    public static string Column(BoardColumn column) => Named("Work_Column_", column);

    public static string Status(MilestoneStatus status) => Named("Work_Status_", status);

    public static string Priority(RequirementPriority priority) => Named("Work_Priority_", priority);

    public static string Method(VerificationMethod method) => Named("Work_Method_", method);

    public static string Role(ParticipationRole role) => Named("Work_Role_", role);

    public static string Stage(TraceChainStage stage) => Named("Work_Stage_", stage);

    public static string Node(TraceNodeKind kind) => Named("Work_Node_", kind);

    public static string Period(BudgetPeriod period) => Named("Work_Period_", period);

    public static string Asserted(TraceAssertedBy by) => Named("Work_Asserted_", by);

    public static string Suggestion(TraceSuggestionSource source) => Named("Work_Suggestion_", source);

    public static string CardKind(BoardCardKind kind) => Named("Work_CardKind_", kind);

    /// <summary>Why a card is where it is, for the cases its column alone does not explain; null when there is nothing to say.</summary>
    public static string? Reason(string? code) => code switch
    {
        null => null,
        BoardCardReasons.AwaitingApproval => Texts.Get("Work_Reason_AwaitingApproval"),
        BoardCardReasons.WaitingForQuota => Texts.Get("Work_Reason_WaitingForQuota"),
        BoardCardReasons.AwaitingAnswer => Texts.Get("Work_Reason_AwaitingAnswer"),
        BoardCardReasons.Blocked => Texts.Get("Work_Reason_Blocked"),
        BoardCardReasons.Challenged => Texts.Get("Work_Reason_Challenged"),
        BoardCardReasons.EscalatedToPerson => Texts.Get("Work_Reason_EscalatedToPerson"),
        BoardCardReasons.Unresolved => Texts.Get("Work_Reason_Unresolved"),
        BoardCardReasons.RunFailed => Texts.Get("Work_Reason_RunFailed"),
        _ => code,
    };

    /// <summary>Where a role of the participation comes from; null for «no consta», which has no source.</summary>
    public static string? Source(string? code) => code switch
    {
        null => null,
        ParticipationSources.Attempt => Texts.Get("Work_Source_Attempt"),
        ParticipationSources.Commit => Texts.Get("Work_Source_Commit"),
        ParticipationSources.Verdict => Texts.Get("Work_Source_Verdict"),
        ParticipationSources.Approval => Texts.Get("Work_Source_Approval"),
        ParticipationSources.LocalDelivery => Texts.Get("Work_Source_LocalDelivery"),
        ParticipationSources.SkippedReview => Texts.Get("Work_Source_SkippedReview"),
        _ => code,
    };

    /// <summary>Why a close was refused, by the rule the service gave (<c>MilestoneCloseRefusedException.Rule</c>).</summary>
    public static string Refusal(string rule)
    {
        var key = "Work_Refused_" + rule.Replace('.', '_').Replace('-', '_');
        return Texts.Current.Has(key) ? Texts.Get(key) : Texts.Format("Work_Refused_Other", rule);
    }

    public static string Short(string id) => id.Length <= 12 ? id : id[..12];

    private static string Named<T>(string prefix, T value)
        where T : struct, Enum
    {
        var key = prefix + value;
        return Texts.Current.Has(key) ? Texts.Get(key) : value.ToString();
    }
}
