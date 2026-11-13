using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Decisions;

// The payloads of the decision log in Phase 10 (docs/21 §5.3, ADR-0067 and ADR-0068). Like the others they are written once in one fixed format
// (DecisionPayloadSerializer): to change one, add a field and raise V.
//
// - All four carry RunId = NoRun, as the entries of the memory and the links do: the run goes in a field. Replay and the dossier of the judge never select
//   them, and a finished run gains no entries.
// - No free text: no title, no name of a check or an environment, no error message of the remote, no path of the machine. Validated identifiers, commits,
//   numbers and enums; the text that was sent (title and body) is evidence.
// - The factories validate. A grant of a class that admits none cannot be built; a commit is 40 or 64 lowercase hexadecimal characters; the owner and the
//   repository pass the rule of D-04. The log is read as data, so reading back validates again.
// - Nothing of a credential, by construction: no field could carry one.

/// <summary>
/// A person gave a standing authorisation (docs/21 D-16): the class, the repository (provider, host, owner and name), the base branch when it names one, and who
/// gave it. <see cref="GrantId"/> is the one a later <see cref="HostingPolicyRevokedPayload"/> names.
/// </summary>
public sealed record HostingPolicyGrantedPayload(
    string GrantId,
    HostingOperationClass Class,
    HostingProviderKind Provider,
    string Host,
    string Owner,
    string Repository,
    string? BaseBranch,
    string GrantedBy) : DecisionPayload
{
    /// <summary>The payload of <paramref name="grant"/>, outside every run.</summary>
    public static HostingPolicyGrantedPayload Granting(HostingPolicyGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Of(grant.GrantId, grant.Class, grant.Repository.Provider, grant.Repository.Host, grant.Repository.Owner, grant.Repository.Name, grant.BaseBranch?.Name, grant.GrantedBy);
    }

    /// <exception cref="ArgumentException">A class that admits no standing grant, an owner, a name or a branch outside the rules, or a field that is empty.</exception>
    public static HostingPolicyGrantedPayload Of(
        string grantId, HostingOperationClass @class, HostingProviderKind provider, string host, string owner, string repository, string? baseBranch, string grantedBy)
    {
        HostingPayloadRules.RequireText(grantId, nameof(grantId));
        HostingPayloadRules.RequireText(grantedBy, nameof(grantedBy));
        if (!HostingPolicy.ClassAdmitsGrant(@class))
        {
            throw new ArgumentException($"A standing grant of the class {@class} cannot be recorded: none of its operations may be persistent.", nameof(@class));
        }

        if (baseBranch is not null && (@class != HostingOperationClass.PullRequestWrite || RemoteBranch.TryParse(baseBranch) is null))
        {
            throw new ArgumentException("Only a grant to write on the pull request names a base branch, and it is a valid one.", nameof(baseBranch));
        }

        HostingPayloadRules.RequireRepository(provider, host, owner, repository);
        return new HostingPolicyGrantedPayload(grantId, @class, provider, host, owner, repository, baseBranch, grantedBy) { RunId = NoRun };
    }

    /// <summary>The grant this entry records, validated again as the log is read as data.</summary>
    /// <exception cref="ArgumentException">The entry does not describe a valid grant.</exception>
    public HostingPolicyGrant ToGrant(DateTimeOffset at)
    {
        _ = Of(GrantId, Class, Provider, Host, Owner, Repository, BaseBranch, GrantedBy);
        return new HostingPolicyGrant(GrantId, Class, RemoteRepository.TryCreateGitHub(Owner, Repository)!, BaseBranch is null ? null : RemoteBranch.TryParse(BaseBranch), GrantedBy, at);
    }
}

/// <summary>A standing authorisation was withdrawn (docs/21 D-16): the first grant is named, and nothing is edited.</summary>
public sealed record HostingPolicyRevokedPayload(string GrantId, string RevokedBy) : DecisionPayload
{
    /// <exception cref="ArgumentException">A field that is empty.</exception>
    public static HostingPolicyRevokedPayload Revoking(string grantId, string revokedBy)
    {
        HostingPayloadRules.RequireText(grantId, nameof(grantId));
        HostingPayloadRules.RequireText(revokedBy, nameof(revokedBy));
        return new HostingPolicyRevokedPayload(grantId, revokedBy) { RunId = NoRun };
    }
}

/// <summary>
/// The app did something on the remote with the account of the person (docs/21 D-15 and D-16): the operation and its class, the run, where, what, what allowed it
/// (exactly one of <see cref="GrantId"/> and <see cref="ApprovalSequence"/>) and how it ended. The title and body that were sent are evidence
/// (<see cref="TextEvidence"/> and <see cref="TextSha256"/>, D-19; the field is not called «Text»: the guard of the payloads forbids that name).
/// </summary>
/// <param name="Run">The run it was about (a field, because the entry belongs to no run).</param>
/// <param name="Account">The login that published, validated; null is «no consta».</param>
/// <param name="Verdict">The standing that was published (<see cref="VerdictStanding"/> by name), for the operations that publish a verdict.</param>
public sealed record HostingOperationPerformedPayload(
    HostingOperationKind Operation,
    HostingOperationClass Class,
    string Run,
    HostingProviderKind Provider,
    string Host,
    string Owner,
    string Repository,
    int? PullRequest,
    string? Branch,
    string? Commit,
    MergeMethod? Method,
    string? GrantId,
    long? ApprovalSequence,
    HostingOutcome Outcome,
    string? FailureCode,
    string? Account,
    EvidenceId? TextEvidence,
    string? TextSha256,
    string? Verdict,
    long? CommentId) : DecisionPayload
{
    /// <summary>The entry of an operation that was performed (or refused or failed), outside every run.</summary>
    /// <exception cref="ArgumentException">A field outside its rules, both or neither of the grant and the approval, or a grant for an operation that cannot be persistent.</exception>
    public static HostingOperationPerformedPayload Of(
        HostingOperationKind operation,
        string run,
        RemoteRepository repository,
        int? pullRequest,
        string? branch,
        string? commit,
        MergeMethod? method,
        string? grantId,
        long? approvalSequence,
        HostingOutcome outcome,
        string? failureCode,
        RemoteLogin? account,
        EvidenceId? text,
        string? textSha256,
        string? verdict,
        long? commentId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _ = new RunId(run);
        if ((grantId is null) == (approvalSequence is null))
        {
            throw new ArgumentException("An operation was allowed by exactly one of a standing grant and an approval.", nameof(grantId));
        }

        if (grantId is not null)
        {
            HostingPayloadRules.RequireText(grantId, nameof(grantId));
            if (!HostingPolicy.CanBePersistent(operation))
            {
                throw new ArgumentException($"The operation {operation} cannot be allowed by a standing grant.", nameof(grantId));
            }
        }

        if (approvalSequence is < 1)
        {
            throw new ArgumentException("The sequence of an approval starts at 1.", nameof(approvalSequence));
        }

        if (pullRequest is < 1)
        {
            throw new ArgumentException("A pull request number starts at 1.", nameof(pullRequest));
        }

        if (branch is not null && RemoteBranch.TryParse(branch) is null)
        {
            throw new ArgumentException("The branch is outside the rules of a branch name.", nameof(branch));
        }

        if (commit is not null && !TraceCommit(commit))
        {
            throw new ArgumentException("A commit is 40 or 64 lowercase hexadecimal characters.", nameof(commit));
        }

        if (failureCode is not null && !UserText.IsCode(failureCode))
        {
            throw new ArgumentException("A failure is a code: lowercase words joined by '.' and '-', never text of the remote.", nameof(failureCode));
        }

        if ((text is null) != (textSha256 is null) || (textSha256 is not null && !Hex64(textSha256)))
        {
            throw new ArgumentException("The text sent is evidence with its SHA-256 (64 lowercase hexadecimal characters), or neither.", nameof(textSha256));
        }

        if (verdict is not null && !(Enum.TryParse<VerdictStanding>(verdict, ignoreCase: false, out var standing) && Enum.IsDefined(standing) && standing.ToString() == verdict))
        {
            throw new ArgumentException("The verdict is a standing: Approved, Rejected or InReview.", nameof(verdict));
        }

        if (commentId is < 1)
        {
            throw new ArgumentException("A comment id starts at 1.", nameof(commentId));
        }

        return new HostingOperationPerformedPayload(
            operation, HostingPolicy.ClassOf(operation), run, repository.Provider, repository.Host, repository.Owner, repository.Name, pullRequest, branch, commit, method,
            grantId, approvalSequence, outcome, failureCode, account?.Value, text, textSha256, verdict, commentId) { RunId = NoRun };
    }

    private static bool TraceCommit(string commit) => AgentStudio.Domain.Tracing.TraceRef.IsFullCommitId(commit);

    private static bool Hex64(string value) => value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>
/// What was seen of a pull request (docs/21 D-23): the fingerprint, the previous one, what changed and the snapshot as the log stores it. It is the source of
/// «integrado», «desplegado» and the chain. Never rewritten (docs/16 D-46).
/// </summary>
public sealed record RemoteStateObservedPayload(
    HostingProviderKind Provider,
    string Host,
    string Owner,
    string Repository,
    int PullRequest,
    string? Run,
    string Fingerprint,
    string? PreviousFingerprint,
    IReadOnlyList<RemoteChangeKind> Changes,
    RemoteSnapshotPayload Snapshot) : DecisionPayload
{
    /// <summary>The observation of <paramref name="snapshot"/> against the last one recorded, outside every run.</summary>
    /// <exception cref="ArgumentException">A snapshot that does not validate, or a run that is not an identifier of a run.</exception>
    public static RemoteStateObservedPayload Observing(RemoteSnapshot snapshot, string? run, string? previousFingerprint, IReadOnlyList<RemoteChangeKind> changes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        snapshot.EnsureValid();
        if (run is not null)
        {
            _ = new RunId(run);
        }

        if (previousFingerprint is not null && !(previousFingerprint.Length == 64 && previousFingerprint.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException("A fingerprint is 64 lowercase hexadecimal characters.", nameof(previousFingerprint));
        }

        if (changes.Any(c => !Enum.IsDefined(c)))
        {
            throw new ArgumentException("A change is one of the known kinds.", nameof(changes));
        }

        var repository = snapshot.PullRequest.Repository;
        return new RemoteStateObservedPayload(
            repository.Provider, repository.Host, repository.Owner, repository.Name, snapshot.PullRequest.Number, run, snapshot.Fingerprint, previousFingerprint, [.. changes],
            RemoteSnapshotPayload.From(snapshot)) { RunId = NoRun };
    }

    /// <summary>The pull request this entry is about, validated again as the log is read as data.</summary>
    /// <exception cref="ArgumentException">The identifiers do not name a valid pull request.</exception>
    public PullRequestRef ToPullRequest()
    {
        HostingPayloadRules.RequireRepository(Provider, Host, Owner, Repository);
        return new PullRequestRef(RemoteRepository.TryCreateGitHub(Owner, Repository)!, PullRequest);
    }

    /// <summary>The snapshot this entry stores, validated again; its fingerprint must be the one the entry names.</summary>
    /// <exception cref="ArgumentException">The snapshot does not validate or does not have the fingerprint of the entry.</exception>
    public RemoteSnapshot ToSnapshot()
    {
        var snapshot = Snapshot.ToSnapshot(ToPullRequest());
        if (!string.Equals(snapshot.Fingerprint, Fingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException("The snapshot of the entry is not the one its fingerprint names.");
        }

        return snapshot;
    }
}

/// <summary>
/// <see cref="RemoteSnapshot"/> as the log stores it: the same fields, flat, with primitives only (no domain type in the JSON).
/// </summary>
public sealed record RemoteSnapshotPayload(
    PullRequestState State,
    string HeadCommit,
    string BaseBranch,
    bool HeadBranchExists,
    string? MergeCommit,
    RemoteMergeMode? MergeMode,
    string? MergedBy,
    IReadOnlyList<string> OriginalCommits,
    IReadOnlyList<string> ResultCommits,
    bool CommitsTruncated,
    ChecksRollup ChecksRollup,
    int ChecksPassing,
    int ChecksFailing,
    int ChecksPending,
    bool ChecksTruncated,
    int ReviewsApproved,
    int ReviewsChangesRequested,
    int ReviewsCommented,
    IReadOnlyList<string> Approvers,
    int ForcePushes,
    bool TimelineTruncated,
    IReadOnlyList<string> RevertCommits,
    long? DeploymentId,
    string? DeploymentCommit,
    DeploymentState? DeploymentState,
    DateTimeOffset UpdatedAt)
{
    public static RemoteSnapshotPayload From(RemoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RemoteSnapshotPayload(
            snapshot.State, snapshot.HeadCommit, snapshot.BaseBranch.Name, snapshot.HeadBranchExists, snapshot.MergeCommit, snapshot.MergeMode, snapshot.MergedBy?.Value,
            [.. snapshot.OriginalCommits], [.. snapshot.ResultCommits], snapshot.CommitsTruncated,
            snapshot.Checks.Rollup, snapshot.Checks.Passing, snapshot.Checks.Failing, snapshot.Checks.Pending, snapshot.Checks.Truncated,
            snapshot.Reviews.Approved, snapshot.Reviews.ChangesRequested, snapshot.Reviews.Commented, [.. snapshot.Reviews.Approvers.Select(a => a.Value)],
            snapshot.ForcePushes, snapshot.TimelineTruncated, [.. snapshot.RevertCommits],
            snapshot.Deployment?.Id, snapshot.Deployment?.Commit, snapshot.Deployment?.State, snapshot.UpdatedAt);
    }

    /// <summary>The snapshot of <paramref name="pullRequest"/> this stores. It validates again: the log is read as data.</summary>
    /// <exception cref="ArgumentException">A branch, a login, a commit or a count outside the rules, or a deployment with some of its fields missing.</exception>
    public RemoteSnapshot ToSnapshot(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        var baseBranch = RemoteBranch.TryParse(BaseBranch) ?? throw new ArgumentException("The base branch is outside the rules of a branch name.", nameof(BaseBranch));
        RemoteLogin? mergedBy = null;
        if (MergedBy is not null)
        {
            mergedBy = RemoteLogin.TryParse(MergedBy) ?? throw new ArgumentException("An account is an account name of the platform.", nameof(MergedBy));
        }

        var approvers = (Approvers ?? throw new ArgumentException("The approvers are a list.", nameof(Approvers)))
            .Select(a => RemoteLogin.TryParse(a) ?? throw new ArgumentException("An account is an account name of the platform.", nameof(Approvers))).ToList();
        RemoteDeployment? deployment = null;
        if (DeploymentId is not null || DeploymentCommit is not null || DeploymentState is not null)
        {
            if (DeploymentId is null || DeploymentCommit is null || DeploymentState is null)
            {
                throw new ArgumentException("A deployment has its id, its commit and its state, or none of them.", nameof(DeploymentId));
            }

            deployment = new RemoteDeployment(DeploymentId.Value, DeploymentCommit, DeploymentState.Value);
        }

        var snapshot = new RemoteSnapshot(
            pullRequest, State, HeadCommit, baseBranch, HeadBranchExists, MergeCommit, MergeMode, mergedBy, OriginalCommits, ResultCommits, CommitsTruncated,
            new ChecksSummary(ChecksRollup, ChecksPassing, ChecksFailing, ChecksPending, ChecksTruncated),
            new ReviewsSummary(ReviewsApproved, ReviewsChangesRequested, ReviewsCommented, approvers),
            ForcePushes, TimelineTruncated, RevertCommits, deployment, UpdatedAt);
        snapshot.EnsureValid();
        return snapshot;
    }
}

internal static class HostingPayloadRules
{
    public static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException("A field of this entry is one line of up to 200 characters.", name);
        }
    }

    /// <summary>The provider, the host, the owner and the repository are what <see cref="RemoteRepository"/> accepts, and the id of a pull request does not need more.</summary>
    public static void RequireRepository(HostingProviderKind provider, string host, string owner, string repository)
    {
        if (provider != HostingProviderKind.GitHub || !string.Equals(host, RemoteRepository.GitHubHost, StringComparison.Ordinal)
            || RemoteRepository.TryCreateGitHub(owner, repository) is null)
        {
            throw new ArgumentException($"'{host}/{owner}/{repository}' is not a repository of the supported provider ({provider}).", nameof(repository));
        }
    }
}
