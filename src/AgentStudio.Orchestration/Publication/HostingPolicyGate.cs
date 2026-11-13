using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Work;

namespace AgentStudio.Orchestration.Publication;

/// <summary>Gives the mailbox of a project (it is made on the first call, with the local state read).</summary>
internal interface IRemoteMailboxes
{
    Task<ProjectRemoteMailbox> OfAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>
/// What a person approved for one piece of work and the mailbox may use again for the rest of the same piece (docs/21 D-15 and ADR-0067): the publication asks once for the class
/// «publicar la rama» and once for «escribir en el PR» at the click, and the stretches that run later in the mailbox (they must not ask: a dialog is a person, and the consumer
/// waits for nobody) show the approval they were given. It lives in the memory of the piece of work: it is never written to a file and it is gone when the work ends.
/// </summary>
public sealed class ApprovalLease
{
    private readonly List<LeasedApproval> _approvals = [];

    internal IReadOnlyList<LeasedApproval> Approvals => _approvals;

    internal void Add(LeasedApproval approval) => _approvals.Add(approval);
}

internal sealed record LeasedApproval(string Action, string Binding, bool IncludesPullRequest, long? Sequence);

/// <summary>
/// The gate of the policies as the publication uses it: the same one as <see cref="IHostingPolicyGate"/>, with the approvals the person already gave for the piece of work
/// (<see cref="ApprovalLease"/>) and whether the caller holds the project (then the inspection of git is part of the check).
/// </summary>
public interface IHostingUseGate : IHostingPolicyGate
{
    /// <param name="mayAsk">True at the click of the person: without a grant or an approval of the lease, the person is asked (with Windows Hello, as the classifier says). False in the
    /// mailbox: it never asks, so what needs an approval it does not have is refused.</param>
    /// <param name="holdsProject">True when the caller holds the project with a remote operation: a use that touches git (the <c>push</c>) also needs the inspection of the configuration to pass.</param>
    /// <param name="detail">What the operation does that its record has no field for, and the person must see when they approve (the accounts that are warned when reviewers are asked for): it goes into the tie and the description.</param>
    Task<HostingAuthorization> AuthorizeUseAsync(HostingOperation operation, ApprovalLease lease, bool mayAsk, bool holdsProject, CancellationToken ct, string? detail = null);

    /// <summary>The sequence of the approval that allowed a use, for the entry of the operation; null when a standing grant did.</summary>
    long? SequenceOf(ApprovalLease lease, HostingOperation operation, string? detail = null);
}

/// <summary>
/// The gate of the policies (docs/21 D-15 to D-17, ADR-0066): every operation on the remote passes here, and only here, just before it runs, and again before each retry (D-26).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The log verifies against its anchor (a full load, as at every critical moment).</item>
/// <item>The verdict, by operation (<see cref="HostingPolicy.RequiresApprovedVerdict"/>): the run is deliverable and the commit is the judged one; to publish the verdict only that there is one to tell,
/// so that it can say «no aprobado» and «en revisión»; closing and deleting need none.</item>
/// <item>The capability of the provider is declared (and, for a merge, the method).</item>
/// <item>For a use that touches git, the inspection of the configuration passes (D-05).</item>
/// <item>A standing grant covers the use (<see cref="HostingPolicyGrant.Covers"/>: never asking for reviewers, merging or the destructive ones), or an approval does: a request tied to the repository,
/// the pull request, the commit, the method and the base, whose answer must be to that tie, and the state is checked again after the approval.</item>
/// </list>
/// A grant given or withdrawn is an annotation in the log: it goes through the mailbox and takes the project for an instant, and until it is written it does not count.
/// </remarks>
public sealed class HostingPolicyGate : IHostingUseGate
{
    private readonly IDecisionRecorder _recorder;
    private readonly IApprovalGate _approvals;
    private readonly IDeliveryService _delivery;
    private readonly IVerdictStore _verdicts;
    private readonly IHostingProviders _providers;
    private readonly IRemoteGitRunner _git;
    private readonly IRemoteMailboxes _mailboxes;

    internal HostingPolicyGate(
        IDecisionRecorder recorder,
        IApprovalGate approvals,
        IDeliveryService delivery,
        IVerdictStore verdicts,
        IHostingProviders providers,
        IRemoteGitRunner git,
        IRemoteMailboxes mailboxes)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _verdicts = verdicts ?? throw new ArgumentNullException(nameof(verdicts));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _mailboxes = mailboxes ?? throw new ArgumentNullException(nameof(mailboxes));
    }

    public Task<HostingAuthorization> AuthorizeAsync(HostingOperation operation, CancellationToken ct) =>
        AuthorizeUseAsync(operation, new ApprovalLease(), mayAsk: true, holdsProject: false, ct);

    public async Task<HostingAuthorization> AuthorizeUseAsync(HostingOperation operation, ApprovalLease lease, bool mayAsk, bool holdsProject, CancellationToken ct, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(lease);
        if (Malformed(operation))
        {
            return Refuse(PublicationCodes.StateChanged);
        }

        var checkedState = await CheckStateAsync(operation, ct).ConfigureAwait(false);
        if (checkedState.Refusal is { } refusal)
        {
            return Refuse(refusal);
        }

        var capability = await CheckCapabilityAsync(operation, ct).ConfigureAwait(false);
        if (capability is not null)
        {
            return Refuse(capability);
        }

        if (holdsProject && operation.Kind == HostingOperationKind.PushRunBranch)
        {
            var inspection = await _git.InspectAsync(operation.Project, operation.Repository, ct).ConfigureAwait(false);
            if (!inspection.Accepted)
            {
                return Refuse(PublicationCodes.ConfigRefused);
            }
        }

        if (HostingPolicy.CanBePersistent(operation.Kind))
        {
            var grant = checkedState.Grants.FirstOrDefault(g => g.Covers(operation.Kind, operation.Repository, operation.BaseBranch));
            if (grant is not null)
            {
                return new HostingAuthorization(true, HostingAuthorizationSource.StandingGrant, grant.GrantId, null, null);
            }
        }

        var action = HostingActions.ActionOf(operation.Kind);
        var leased = lease.Approvals.FirstOrDefault(a => a.Action == action && string.Equals(BindingOf(operation, a.IncludesPullRequest, detail), a.Binding, StringComparison.Ordinal));
        if (leased is not null)
        {
            return new HostingAuthorization(true, HostingAuthorizationSource.Approval, null, leased.Binding, null);
        }

        if (!mayAsk)
        {
            return Refuse(PublicationCodes.NeedsApproval);
        }

        var includesPullRequest = operation.PullRequest is not null;
        var binding = BindingOf(operation, includesPullRequest, detail);
        var description = Describe(operation, detail, binding);
        var request = new ApprovalRequest(action, description, RiskLevel.Critical, null, [], operation.Project.ProjectId, operation.Run.Value, null, binding);
        var decision = await _approvals.RequestAsync(request, ct).ConfigureAwait(false);
        if (!decision.Approved)
        {
            return Refuse(PublicationCodes.NotApproved);
        }

        if (!string.Equals(decision.Answers, binding, StringComparison.Ordinal))
        {
            return Refuse(PublicationCodes.ApprovalMismatch);
        }

        // The state is checked again after the approval, as in the delivery (ADR-0012): the log, the verdict and the commit may have moved while the person decided.
        var again = await CheckStateAsync(operation, ct).ConfigureAwait(false);
        if (again.Refusal is not null || !string.Equals(again.JudgedCommit, checkedState.JudgedCommit, StringComparison.Ordinal))
        {
            return Refuse(PublicationCodes.StateChanged);
        }

        var sequence = await ApprovalSequenceAsync(operation.Project.ProjectId, action, description, ct).ConfigureAwait(false);
        lease.Add(new LeasedApproval(action, binding, includesPullRequest, sequence));
        return new HostingAuthorization(true, HostingAuthorizationSource.Approval, null, binding, null);
    }

    public long? SequenceOf(ApprovalLease lease, HostingOperation operation, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operation);
        var action = HostingActions.ActionOf(operation.Kind);
        return lease.Approvals.FirstOrDefault(a => a.Action == action && string.Equals(BindingOf(operation, a.IncludesPullRequest, detail), a.Binding, StringComparison.Ordinal))?.Sequence;
    }

    public async Task<HostingPolicyGrant?> GrantAsync(ProjectContext project, HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (!HostingPolicy.ClassAdmitsGrant(@class))
        {
            throw new ArgumentException($"A standing grant of the class {@class} cannot be given: none of its operations may be persistent.", nameof(@class));
        }

        if (baseBranch is not null && @class != HostingOperationClass.PullRequestWrite)
        {
            throw new ArgumentException("Only a grant to write on the pull request may name a base branch.", nameof(baseBranch));
        }

        var binding = string.Join('|', HostingActions.GrantPolicyAction, @class, repository.Id, baseBranch?.Name ?? "-");
        var request = new ApprovalRequest(
            HostingActions.GrantPolicyAction, $"{@class} {repository.Id}" + (baseBranch is null ? string.Empty : $" -> {baseBranch.Name}"), RiskLevel.Critical, null, [], project.ProjectId, null, null, binding);
        var decision = await _approvals.RequestAsync(request, ct).ConfigureAwait(false);
        if (!decision.Approved || !string.Equals(decision.Answers, binding, StringComparison.Ordinal))
        {
            return null;
        }

        var grant = new HostingPolicyGrant(Guid.NewGuid().ToString("N"), @class, repository, baseBranch, Person(decision.DecidedBy), decision.At);
        var payload = HostingPolicyGrantedPayload.Granting(grant);
        var mailbox = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var recorded = await mailbox.NoteFromOutsideAsync("grants", UserText.Of(PublicationCodes.AnnotationPending), () => RecordAsync(mailbox, project, DecisionKind.HostingPolicyGranted, $"Autoriza de forma persistente en {repository.Id}: {@class}", payload)).ConfigureAwait(false);

        // Parked (the project is busy): it will be written when it is free, and until then the grant does not count.
        return recorded ? grant : null;
    }

    public async Task RevokeAsync(ProjectContext project, string grantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var mailbox = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);

        // A revocation counts from this instant (the gate does not count a grant with a revocation pending) and survives a restart; the entry is written when the project is free.
        await mailbox.RevokeAsync(grantId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HostingPolicyGrant>> GrantsAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await _recorder.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        var withdrawn = (await _mailboxes.OfAsync(project, ct).ConfigureAwait(false)).PendingRevocations;
        return [.. ActiveGrants(log.Entries).Where(g => !withdrawn.Contains(g.GrantId))];
    }

    // ---- the checks ----

    private sealed record Checked(UserText? Refusal, string? JudgedCommit, IReadOnlyList<HostingPolicyGrant> Grants);

    private async Task<Checked> CheckStateAsync(HostingOperation operation, CancellationToken ct)
    {
        var log = await _recorder.LoadAsync(operation.Project.ProjectId, LogLoad.Full, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return new Checked(UserText.Of(PublicationCodes.LogNotVerified), null, []);
        }

        var mailbox = await _mailboxes.OfAsync(operation.Project, ct).ConfigureAwait(false);
        var withdrawn = mailbox.PendingRevocations;
        var grants = ActiveGrants(log.Entries).Where(g => !withdrawn.Contains(g.GrantId)).ToList();
        string? judged = null;
        if (HostingPolicy.RequiresApprovedVerdict(operation.Kind))
        {
            var readiness = await _delivery.CheckAsync(operation.Project, operation.Run, ct).ConfigureAwait(false);
            if (!readiness.Ready || readiness.Commit is null)
            {
                return new Checked(UserText.Of(PublicationCodes.VerdictNotApproved), null, grants);
            }

            if (operation.Commit is null || !string.Equals(operation.Commit, readiness.Commit, StringComparison.Ordinal))
            {
                return new Checked(UserText.Of(PublicationCodes.CommitNotJudged), null, grants);
            }

            judged = readiness.Commit;
        }
        else if (operation.Kind is HostingOperationKind.PublishVerdictStatus or HostingOperationKind.PublishVerdictComment)
        {
            var stored = (await _verdicts.ListAsync(operation.Project.ProjectId, operation.Run.Value, ct).ConfigureAwait(false))
                .GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var (commit, standing) = RemoteJudgement.VerdictInForce(WorkLog.Parse(log.Entries), log.Entries, operation.Run.Value, stored);
            if (standing is null || commit is null)
            {
                return new Checked(UserText.Of(PublicationCodes.NoVerdict), null, grants);
            }

            if (operation.Commit is not null && !string.Equals(operation.Commit, commit, StringComparison.Ordinal))
            {
                return new Checked(UserText.Of(PublicationCodes.CommitNotJudged), null, grants);
            }

            judged = commit;
        }

        return new Checked(null, judged, grants);
    }

    private async Task<UserText?> CheckCapabilityAsync(HostingOperation operation, CancellationToken ct)
    {
        if (operation.Kind == HostingOperationKind.PushRunBranch)
        {
            return null;
        }

        var provider = _providers.For(operation.Repository);
        if (provider is null)
        {
            return UserText.Of(PublicationCodes.NoProvider);
        }

        var availability = await provider.ProbeAsync(ct).ConfigureAwait(false);
        var needed = operation.Kind switch
        {
            HostingOperationKind.OpenPullRequest => HostingCapability.OpenPullRequest,
            HostingOperationKind.UpdatePullRequest => HostingCapability.UpdatePullRequest,
            HostingOperationKind.RequestReviewers => HostingCapability.RequestReviewers,
            HostingOperationKind.PublishVerdictStatus => HostingCapability.CommitStatus,
            HostingOperationKind.PublishVerdictComment => HostingCapability.Comment,
            HostingOperationKind.MergePullRequest => HostingCapability.Merge,
            HostingOperationKind.ClosePullRequest => HostingCapability.ClosePullRequest,
            HostingOperationKind.DeleteRemoteBranch => HostingCapability.DeleteBranch,
            _ => HostingCapability.None,
        };
        if (!availability.Installed || !availability.Capabilities.Has(needed))
        {
            return UserText.Of(PublicationCodes.CapabilityMissing);
        }

        if (operation.Kind == HostingOperationKind.MergePullRequest
            && (operation.Method is not { } method || !availability.Capabilities.MergeMethods.Contains(method)))
        {
            return UserText.Of(PublicationCodes.MergeMethodNotAllowed);
        }

        return null;
    }

    /// <summary>
    /// The sequence of the approval that answered <em>this</em> request: the last request of the log whose description is ours (it carries a reference made from the tie), and the first
    /// grant of that action after it. Two runs asking at once are not mixed up, because the descriptions differ.
    /// </summary>
    private async Task<long?> ApprovalSequenceAsync(string projectId, string action, string description, CancellationToken ct)
    {
        var log = await _recorder.LoadAsync(projectId, ct).ConfigureAwait(false);
        long? requested = null;
        for (var i = log.Entries.Count - 1; i >= 0 && requested is null; i--)
        {
            var entry = log.Entries[i];
            if (entry.Kind == DecisionKind.ApprovalRequested && Read(entry) is ApprovalRequestedPayload asked && string.Equals(asked.Action, action, StringComparison.Ordinal)
                && string.Equals(asked.Description, description, StringComparison.Ordinal))
            {
                requested = entry.Sequence;
            }
        }

        if (requested is null)
        {
            return null;
        }

        foreach (var entry in log.Entries.Where(e => e.Sequence > requested && e.Kind == DecisionKind.ApprovalGranted))
        {
            if (Read(entry) is ApprovalAnsweredPayload answered && string.Equals(answered.Action, action, StringComparison.Ordinal))
            {
                return entry.Sequence;
            }
        }

        return null;
    }

    private static DecisionPayload? Read(DecisionEntry entry)
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<bool> RecordAsync(ProjectRemoteMailbox mailbox, ProjectContext project, DecisionKind kind, string summary, DecisionPayload payload)
    {
        using var lease = mailbox.Take();
        if (lease is null)
        {
            return false;
        }

        var log = await _recorder.LoadAsync(project.ProjectId, LogLoad.Full, CancellationToken.None).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        await _recorder.RecordAsync(project.ProjectId, new Actor(ActorKind.User, Person(null)), kind, summary, payload, null, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>The standing grants the log holds and nobody withdrew; every entry is validated as it is read (the log is data).</summary>
    internal static IReadOnlyList<HostingPolicyGrant> ActiveGrants(IReadOnlyList<DecisionEntry> entries)
    {
        var granted = new List<HostingPolicyGrant>();
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Only what a person wrote counts: a grant or a revocation from any other actor is not one.
            if (entry.Kind is not (DecisionKind.HostingPolicyGranted or DecisionKind.HostingPolicyRevoked) || entry.Actor.Kind != ActorKind.User)
            {
                continue;
            }

            try
            {
                switch (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson))
                {
                    case HostingPolicyGrantedPayload payload:
                        granted.Add(payload.ToGrant(entry.RecordedAt));
                        break;
                    case HostingPolicyRevokedPayload payload:
                        payload.EnsureValid();
                        revoked.Add(payload.GrantId);
                        break;
                }
            }
            catch (Exception ex) when (RemoteLogReading.IsInvalidData(ex) || ex is NotSupportedException)
            {
                // an entry that does not validate grants nothing
            }
        }

        return [.. granted.Where(g => !revoked.Contains(g.GrantId))];
    }

    /// <summary>What the request is tied to (docs/21 D-15): the action, the repository, the run, the commit, the method, the base branch and, when the use has one, the pull request.</summary>
    internal static string BindingOf(HostingOperation operation, bool includePullRequest, string? detail = null) => string.Join(
        '|',
        HostingActions.ActionOf(operation.Kind),
        operation.Repository.Id,
        operation.Run.Value,
        operation.Commit ?? "-",
        operation.BaseBranch?.Name ?? "-",
        operation.Method?.ToString() ?? "-",
        includePullRequest && operation.PullRequest is { } pullRequest ? pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
        detail ?? "-");

    /// <summary>
    /// What the person reads when they approve. A class of «escribir en el PR» also covers the verdict of that pull request, so it says so; asking for reviewers names who is warned; and a short
    /// reference made from the tie ends it, so the entry of the answer can be matched to this very request.
    /// </summary>
    private static string Describe(HostingOperation operation, string? detail, string binding) =>
        Core(operation)
        + (HostingActions.ActionOf(operation.Kind) == HostingActions.PullRequestWriteAction && operation.Kind != HostingOperationKind.RequestReviewers ? " +verdict" : string.Empty)
        + (detail is null ? string.Empty : " " + detail)
        + " #" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(binding)))[..8];

    private static string Core(HostingOperation operation) =>
        $"{operation.Kind} {operation.Repository.Id}"
        + (operation.PullRequest is { } pullRequest ? $" #{pullRequest.Number}" : string.Empty)
        + (operation.Commit is { Length: > 12 } commit ? $" {commit[..12]}" : string.Empty)
        + (operation.Method is { } method ? $" {method}" : string.Empty)
        + (operation.BaseBranch is { } baseBranch ? $" -> {baseBranch.Name}" : string.Empty);

    private static bool Malformed(HostingOperation operation) =>
        !Enum.IsDefined(operation.Kind)
        || (operation.Kind == HostingOperationKind.MergePullRequest) != (operation.Method is not null)
        || (operation.Kind is HostingOperationKind.MergePullRequest or HostingOperationKind.ClosePullRequest or HostingOperationKind.RequestReviewers or HostingOperationKind.UpdatePullRequest && operation.PullRequest is null)
        || (operation.PullRequest is { } pullRequest && pullRequest.Repository != operation.Repository)
        || (operation.Commit is not null && !AgentStudio.Domain.Tracing.TraceRef.IsFullCommitId(operation.Commit));

    private static HostingAuthorization Refuse(string code) => Refuse(UserText.Of(code));

    private static HostingAuthorization Refuse(UserText text) => new(false, null, null, null, text);

    private static string Person(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) ? "user" : name.Trim();
}
