using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Hosting;

// The policies and the publication (Phase 10, docs/21 D-15 to D-22, ADR-0066 and ADR-0067). The gate and the service are
// Orchestration/Publication (WP3); here is only what the screen and the other packages build against.

/// <summary>
/// The identifiers of the approvals of this phase. <c>ApprovalRiskClassifier</c> lists each as <c>RemoteOrSecretOperation</c> («siempre», with Windows Hello),
/// and <c>CriticalApprovalCoverageTests</c> watches that none is missing.
/// </summary>
public static class HostingActions
{
    public const string GrantPolicyAction = "hosting.policy.grant";
    public const string PublishAction = "hosting.publish";
    public const string PullRequestWriteAction = "hosting.pr.write";
    public const string MergeAction = "hosting.pr.merge";
    public const string ClosePullRequestAction = "hosting.pr.close";
    public const string DeleteBranchAction = "hosting.branch.delete";

    /// <summary>Every identifier of this class.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GrantPolicyAction, PublishAction, PullRequestWriteAction, MergeAction, ClosePullRequestAction, DeleteBranchAction,
    ];

    /// <summary>
    /// The approval that a use of <paramref name="operation"/> asks for when no standing grant covers it. Asking to grant a class is
    /// <see cref="GrantPolicyAction"/>, which no operation is.
    /// </summary>
    public static string ActionOf(HostingOperationKind operation) => operation switch
    {
        HostingOperationKind.PushRunBranch => PublishAction,
        HostingOperationKind.OpenPullRequest
            or HostingOperationKind.UpdatePullRequest
            or HostingOperationKind.RequestReviewers
            or HostingOperationKind.PublishVerdictStatus
            or HostingOperationKind.PublishVerdictComment => PullRequestWriteAction,
        HostingOperationKind.MergePullRequest => MergeAction,
        HostingOperationKind.ClosePullRequest => ClosePullRequestAction,
        HostingOperationKind.DeleteRemoteBranch => DeleteBranchAction,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown remote operation."),
    };
}

/// <summary>One operation on the remote, as the gate sees it: what, where and, when it has them, the pull request, the commit and the merge method.</summary>
public sealed record HostingOperation(
    ProjectContext Project,
    RunId Run,
    HostingOperationKind Kind,
    RemoteRepository Repository,
    RemoteBranch? BaseBranch,
    PullRequestRef? PullRequest,
    string? Commit,
    MergeMethod? Method);

public enum HostingAuthorizationSource
{
    StandingGrant,
    Approval,
}

/// <summary>What the gate answered: whether it is allowed, what allowed it (the grant or the binding of the approval) and, when not, why.</summary>
public sealed record HostingAuthorization(bool Allowed, HostingAuthorizationSource? Source, string? GrantId, string? ApprovalBinding, UserText? Refusal);

/// <summary>
/// The gate of the policies (docs/21 D-15): every remote operation passes here, and only here, before it runs. <see cref="GrantAsync"/> and
/// <see cref="RevokeAsync"/> write to the log: they go through the mailbox and take the project for an instant (D-16 and D-25). A grant of a class covers only
/// the operations of that class for which <see cref="HostingPolicy.CanBePersistent"/> is true.
/// </summary>
public interface IHostingPolicyGate
{
    Task<HostingAuthorization> AuthorizeAsync(HostingOperation operation, CancellationToken ct);

    Task<HostingPolicyGrant?> GrantAsync(ProjectContext project, HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch, CancellationToken ct);

    Task RevokeAsync(ProjectContext project, string grantId, CancellationToken ct);

    Task<IReadOnlyList<HostingPolicyGrant>> GrantsAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>
/// What the card of publication shows before the button (docs/21 D-17 and D-18): whether it can be published and why not, where to, what commit, how many
/// commits go up, the protections that could be read, the inspection of the configuration, what the CLI can do, the proposed text and the pull request that
/// already exists.
/// </summary>
public sealed record PublicationPreview(
    bool Ready,
    UserText? NotReadyReason,
    RemoteRepository? Repository,
    string? Commit,
    RemoteBranch? BaseBranch,
    int SystemCommits,
    int? OwnUnpublishedCommits,
    BranchRules? Rules,
    RemoteGitInspection? Inspection,
    HostingCapabilities Capabilities,
    PullRequestText ProposedText,
    PullRequestRef? Existing);

/// <summary>What the person asks to publish: the run, the base branch, the text (the system's or the one the person edited) and whether it opens as a draft.</summary>
public sealed record PublicationRequest(ProjectContext Project, RunId Run, RemoteBranch BaseBranch, PullRequestText Text, bool Draft);

/// <summary>
/// What the person asks for (docs/21 D-18 and D-20). Each call enqueues work in the mailbox of the project and returns its ticket. <see cref="PreviewAsync"/> and
/// <see cref="InspectAsync"/> run inside the mailbox too, holding the project: with the project busy they launch nothing and say so.
/// </summary>
public interface IPublicationService
{
    /// <summary>For Auto Doctor; null when the project is busy.</summary>
    Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct);

    Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct);

    Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct);

    Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);

    Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct);

    Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct);

    Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);

    Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct);
}
