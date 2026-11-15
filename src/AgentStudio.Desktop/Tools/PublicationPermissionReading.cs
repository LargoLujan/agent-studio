using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Desktop.Tools;

/// <summary>General publication rules, never a project's current grants or an authorization to act.</summary>
public sealed record PublicationPermissionReading(
    HostingOperationKind Operation,
    string Title,
    string Data,
    string Approval,
    string Verdict,
    bool CanUseStandingGrant,
    bool RequiresApprovedVerdict)
{
    public string AccessibleName => $"{Title}. {Data} {Approval} {Verdict}";

    public override string ToString() => AccessibleName;

    /// <summary>Read the same operation policy as the gate, without calling it or reading any project data.</summary>
    public static IReadOnlyList<PublicationPermissionReading> Read(IUiTextProvider texts) =>
        [.. Enum.GetValues<HostingOperationKind>().Select(operation =>
        {
            var persistent = HostingPolicy.CanBePersistent(operation);
            var approved = HostingPolicy.RequiresApprovedVerdict(operation);
            var dataKey = operation switch
            {
                HostingOperationKind.PushRunBranch => "Tools_Publication_Data_Commits",
                HostingOperationKind.OpenPullRequest or HostingOperationKind.UpdatePullRequest => "Tools_Publication_Data_Text",
                HostingOperationKind.RequestReviewers => "Tools_Publication_Data_Reviewers",
                HostingOperationKind.PublishVerdictStatus or HostingOperationKind.PublishVerdictComment => "Tools_Publication_Data_Verdict",
                HostingOperationKind.MergePullRequest or HostingOperationKind.ClosePullRequest or HostingOperationKind.DeleteRemoteBranch => "Tools_Publication_Data_Operation",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown remote operation."),
            };
            return new PublicationPermissionReading(
                operation,
                texts.Get("Hosting_Op_" + operation),
                texts.Get(dataKey),
                texts.Get(persistent ? "Tools_Publication_StandingGrant" : "Tools_Publication_PerUse"),
                texts.Get(approved ? "Tools_Publication_Approved" : "Tools_Publication_OtherChecks"),
                persistent,
                approved);
        })];
}
