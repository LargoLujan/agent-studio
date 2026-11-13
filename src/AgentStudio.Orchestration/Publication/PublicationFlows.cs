using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Publication;

/// <summary>What the flows share: the gate, the providers, the runner of git and the builder of the verdict.</summary>
internal sealed record FlowDependencies(IHostingUseGate Gate, IHostingProviders Providers, IRemoteGitRunner Git, VerdictNoticeBuilder Notices);

/// <summary>What the flow of the verdict has done so far: the package, which channels the gate let through, and what the platform said.</summary>
internal sealed class VerdictChannels
{
    public VerdictPackage? Package { get; set; }

    public List<HostingOperationKind> Allowed { get; } = [];

    public PublishedVerdict? Published { get; set; }
}

/// <summary>The base of the flows that talk to the platform: how an answer interrupts, and how an operation is annotated (docs/21 D-18, D-25 and D-26).</summary>
internal abstract class HostingFlow(RemoteWorkTicket ticket, string key, HostingOperation? operation, FlowDependencies deps, ApprovalLease lease) : RemoteFlow(ticket, key, operation)
{
    private static readonly HostingOperationKind[] VerdictKinds = [HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment];

    private readonly Dictionary<HostingOperationKind, (HostingAuthorization Authorization, string? Detail)> _authorizations = [];
    private int _attempt;

    protected FlowDependencies Deps { get; } = deps;

    protected ApprovalLease Lease { get; } = lease;

    /// <summary>
    /// Passes the gate just before an operation of the mailbox runs: it never asks (the person was asked at the click). What it let through is kept, because the entry of the operation names what
    /// allowed it, and that is what was checked before it ran, not what a later check says once it happened.
    /// </summary>
    protected async Task<FlowResult?> AuthorizeAsync(HostingOperation operation, bool holdsProject, CancellationToken ct, string? detail = null)
    {
        var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, mayAsk: false, holdsProject, ct, detail).ConfigureAwait(false);
        if (!authorization.Allowed)
        {
            return new FlowResult(FlowEnd.Failed, Message: authorization.Refusal ?? UserText.Of(PublicationCodes.StateChanged));
        }

        _authorizations[operation.Kind] = (authorization, detail);
        return null;
    }

    protected void Allowed(HostingOperationKind kind, HostingAuthorization authorization, string? detail = null) => _authorizations[kind] = (authorization, detail);

    /// <summary>What an answer of the platform that is not a success does to the flow; null when it went well.</summary>
    protected FlowResult? Interrupted<T>(FlowContext context, HostingResult<T> result)
    {
        context.Outcome(result.Outcome);
        switch (result.Outcome)
        {
            case HostingOutcome.Ok:
            case HostingOutcome.NotModified:
                _attempt = 0;
                return null;
            case HostingOutcome.Unreachable:
                return FlowResult.WaitForSync(PendingReason.Offline, PublicationCodes.OfflineWait(_attempt++), PublicationCodes.Unreachable);
            case HostingOutcome.RateLimited:
                var wait = result.RetryAt is { } at ? at - context.Time.GetUtcNow() : TimeSpan.FromMinutes(1);
                return FlowResult.WaitForSync(PendingReason.RateLimited, wait < TimeSpan.Zero ? TimeSpan.FromMinutes(1) : wait > PublicationCodes.RateLimitCap ? PublicationCodes.RateLimitCap : wait, PublicationCodes.RateLimited);
            case HostingOutcome.SignInNeeded:
                return FlowResult.Failed(PublicationCodes.SignInNeeded);
            default:
                return FlowResult.Failed(PublicationCodes.ProviderRefused);
        }
    }

    /// <summary>
    /// Whether an answer says nothing certain about what the platform did: no connection, no reading of its answer, a refusal that may have come after the change. Before such a write is taken as
    /// failed, the state is asked (D-18, step 5): the pull request may exist, the branch may be gone.
    /// </summary>
    protected static bool Unclear(HostingOutcome outcome) => outcome is HostingOutcome.Unreachable or HostingOutcome.Forbidden or HostingOutcome.InvalidAnswer;

    /// <summary>The entry of an operation performed (or refused or failed): null when what allowed it cannot be said (an approval with no entry in the log), and then nothing is written.</summary>
    protected HostingOperationPerformedPayload? Payload(
        HostingOperation operation, HostingOutcome outcome, string? failure, string? branch, RemoteLogin? account, EvidenceId? text, string? textSha, string? verdict, long? commentId)
    {
        if (!_authorizations.TryGetValue(operation.Kind, out var allowed))
        {
            return null;
        }

        string? grant = null;
        long? sequence = null;
        if (allowed.Authorization.Source == HostingAuthorizationSource.StandingGrant)
        {
            grant = allowed.Authorization.GrantId;
        }
        else
        {
            sequence = Deps.Gate.SequenceOf(Lease, operation, allowed.Detail);
        }

        if ((grant is null) == (sequence is null))
        {
            return null;
        }

        var code = failure is not null && UserText.IsCode(failure) ? failure : null;
        return HostingOperationPerformedPayload.Of(
            operation.Kind, operation.Run.Value, operation.Repository, operation.PullRequest?.Number, branch, operation.Commit, operation.Method, grant, sequence, outcome, code, account,
            text, textSha, verdict, commentId);
    }

    protected static async Task RecordAsync(FlowContext context, HostingOperationPerformedPayload payload)
    {
        var log = await context.Recorder.LoadAsync(context.Project.ProjectId, CancellationToken.None).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(context.Project.ProjectId, log.Verification);
        }

        await context.Recorder.RecordAsync(
            context.Project.ProjectId,
            new Actor(ActorKind.System, "remote-sync"),
            DecisionKind.HostingOperationPerformed,
            $"Operación remota {payload.Operation} en {payload.Owner}/{payload.Repository}: {payload.Outcome}",
            payload,
            null,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Annotates after the fact, taking the project for an instant; when it is busy it is parked and the flow goes on (the platform part never waits for the log).</summary>
    protected static Task<bool> NoteAsync(FlowContext context, string key, HostingOperationPerformedPayload? payload) =>
        payload is null
            ? Task.FromResult(true)
            : context.NoteAsync(key, async () =>
            {
                using var taken = context.Take();
                if (taken is null)
                {
                    return false;
                }

                await RecordAsync(context, payload).ConfigureAwait(false);
                return true;
            });

    /// <summary>
    /// Stretch D of the publication (docs/21 D-18 and D-21): the status of the judged commit and the tagged comment. Each channel passes the gate on its own, with the judged commit and the base of
    /// the pull request in the operation, so the one approval of the click and any standing grant cover them; a channel the platform cannot do (no capability) is left out and the other is
    /// published, and only when there is none is it refused.
    /// </summary>
    protected async Task<FlowResult?> PublishVerdictAsync(
        FlowContext context, ProjectContext project, RunId run, PullRequestRef pullRequest, string commit, RemoteBranch? baseBranch, string? head, VerdictChannels state)
    {
        state.Package ??= await Deps.Notices.BuildAsync(project, run, head, context.Stopping).ConfigureAwait(false);
        if (state.Package is null)
        {
            return FlowResult.Failed(PublicationCodes.NoVerdict);
        }

        if (!string.Equals(state.Package.Notice.JudgedCommit, commit, StringComparison.Ordinal))
        {
            return FlowResult.Failed(PublicationCodes.StateChanged);
        }

        if (state.Allowed.Count == 0)
        {
            UserText? missing = null;
            foreach (var kind in VerdictKinds)
            {
                var operation = new HostingOperation(project, run, kind, pullRequest.Repository, baseBranch, pullRequest, commit, null);
                var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, mayAsk: false, holdsProject: false, context.Stopping).ConfigureAwait(false);
                if (authorization.Allowed)
                {
                    Allowed(kind, authorization);
                    state.Allowed.Add(kind);
                }
                else if (authorization.Refusal?.Code == PublicationCodes.CapabilityMissing)
                {
                    missing ??= authorization.Refusal;
                }
                else
                {
                    return new FlowResult(FlowEnd.Failed, Message: authorization.Refusal);
                }
            }

            if (state.Allowed.Count == 0)
            {
                return new FlowResult(FlowEnd.Failed, Message: missing ?? UserText.Of(PublicationCodes.CapabilityMissing));
            }
        }

        var provider = Deps.Providers.For(pullRequest.Repository);
        if (provider is null)
        {
            return FlowResult.Failed(PublicationCodes.NoProvider);
        }

        var result = await provider.PublishVerdictAsync(pullRequest, state.Package.Notice, state.Package.StatusDescription, state.Package.CommentBody, null, context.Stopping).ConfigureAwait(false);
        if (Interrupted(context, result) is { } interrupted)
        {
            return interrupted;
        }

        state.Published = result.Value;
        return null;
    }

    /// <summary>Stretch E: the entries of the channels that were published (or tried), each with what allowed it.</summary>
    protected async Task NoteVerdictAsync(
        FlowContext context, ProjectContext project, RunId run, PullRequestRef pullRequest, string commit, RemoteBranch? baseBranch, RemoteLogin? account, VerdictChannels state)
    {
        var standing = state.Package!.Notice.Standing.ToString();
        foreach (var kind in state.Allowed)
        {
            var operation = new HostingOperation(project, run, kind, pullRequest.Repository, baseBranch, pullRequest, commit, null);
            var published = kind == HostingOperationKind.PublishVerdictStatus ? state.Published?.StatusPublished == true : state.Published?.CommentId is not null;
            await NoteAsync(
                context, Key,
                Payload(operation, published ? HostingOutcome.Ok : HostingOutcome.Refused, published ? null : "verdict.not-published", null, account, null, null, standing,
                    kind == HostingOperationKind.PublishVerdictComment ? state.Published?.CommentId : null)).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// «Publicar y abrir PR» (docs/21 D-18), in stretches, idempotent: A with the project in hand and not split (the inspection, the <c>push</c>, <c>ls-remote</c> and their entry), B the pull
/// request (look for it, open or update), C its entry, D the verdict, E its entries and the follow. After a cut, the next try asks what is already done before it repeats anything. The pull
/// request is followed even when the stretch of the verdict fails: it exists, and the poll must see it.
/// </summary>
internal sealed class PublishFlow(
    RemoteWorkTicket ticket, ProjectContext project, RunId run, RemoteRepository repository, string commit, RemoteBranch baseBranch, PullRequestText text, bool draft,
    EvidenceId? textEvidence, string? textSha, FlowDependencies deps, ApprovalLease lease)
    : HostingFlow(ticket, "run:" + run.Value, new HostingOperation(project, run, HostingOperationKind.PushRunBranch, repository, null, null, commit, null), deps, lease)
{
    private enum Stage
    {
        Git,
        PullRequest,
        NotePullRequest,
        Verdict,
        NoteVerdict,
        Finished,
    }

    private readonly VerdictChannels _verdict = new();
    private Stage _stage;
    private PullRequestRef? _pullRequest;
    private HostingOperationKind _prKind;
    private RemoteLogin? _account;
    private bool _accountRead;

    private RemoteBranch Head => RemoteBranch.ForRun(run);

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        while (true)
        {
            if (_stage != Stage.Git && _stage != Stage.Finished && context.Closing)
            {
                return FlowResult.WaitForSync(PendingReason.Offline, TimeSpan.FromMinutes(1));
            }

            FlowResult? stop = _stage switch
            {
                Stage.Git => await GitAsync(context).ConfigureAwait(false),
                Stage.PullRequest => await PullRequestAsync(context).ConfigureAwait(false),
                Stage.NotePullRequest => await NotePullRequestAsync(context).ConfigureAwait(false),
                Stage.Verdict => await VerdictAsync(context).ConfigureAwait(false),
                Stage.NoteVerdict => await NoteVerdictStageAsync(context).ConfigureAwait(false),
                _ => FlowResult.Done,
            };
            if (stop is { } result)
            {
                // The pull request exists and is followed whatever happened to the verdict (D-18).
                if (_pullRequest is not null && _stage >= Stage.Verdict && result.End != FlowEnd.WaitSync && result.End != FlowEnd.WaitProject)
                {
                    context.Follow(_pullRequest, run.Value);
                }

                return result;
            }

            if (_stage == Stage.Finished)
            {
                return FlowResult.Done;
            }
        }
    }

    /// <summary>Stretch A: with the project in hand and without letting it go, from the inspection to the entry of the push.</summary>
    private async Task<FlowResult?> GitAsync(FlowContext context)
    {
        using var taken = context.Take();
        if (taken is null)
        {
            return FlowResult.WaitForProject();
        }

        var identified = await Deps.Providers.IdentifyAsync(project, "origin", CancellationToken.None).ConfigureAwait(false);
        if (identified is null)
        {
            return FlowResult.Failed(PublicationCodes.NoRemote);
        }

        if (identified != repository)
        {
            return FlowResult.Failed(PublicationCodes.OtherRepository);
        }

        context.Trust(repository);
        var operation = (Operation ?? throw new InvalidOperationException("The publication has no operation.")) with { Commit = commit };
        if (await AuthorizeAsync(operation, holdsProject: true, CancellationToken.None).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        // No token that can be cancelled reaches git: a push is not cut halfway.
        var push = await Deps.Git.PushRunBranchAsync(new RemotePush(project, repository, run, commit), CancellationToken.None).ConfigureAwait(false);
        var read = await Deps.Git.ReadBranchAsync(project, repository, Head, CancellationToken.None).ConfigureAwait(false);
        var confirmed = read.Reached && string.Equals(read.Commit, commit, StringComparison.Ordinal);
        var pushed = push.Outcome is RemotePushOutcome.Pushed or RemotePushOutcome.AlreadyUpToDate || (push.Outcome == RemotePushOutcome.NotConfirmed && confirmed);
        var outcome = pushed && confirmed ? HostingOutcome.Ok : push.Outcome == RemotePushOutcome.Unreachable || !read.Reached || push.Outcome == RemotePushOutcome.NotConfirmed ? HostingOutcome.Unreachable : HostingOutcome.Refused;
        var failure = pushed && confirmed ? null : push.FailureCode is { } code && UserText.IsCode(code) ? code : "push." + push.Outcome.ToString().ToLowerInvariant();
        if (Payload(operation, outcome, failure, Head.Name, null, null, null, null, null) is { } payload)
        {
            await RecordAsync(context, payload).ConfigureAwait(false);
        }

        if (outcome == HostingOutcome.Ok)
        {
            _stage = Stage.PullRequest;
            return null;
        }

        return push.Outcome switch
        {
            _ when outcome == HostingOutcome.Unreachable => FlowResult.WaitForSync(PendingReason.Offline, PublicationCodes.OfflineWait(0), PublicationCodes.Unreachable),
            RemotePushOutcome.CredentialsNeeded => FlowResult.Failed(PublicationCodes.CredentialsNeeded),
            RemotePushOutcome.RejectedNotFastForward => FlowResult.Failed(PublicationCodes.PushRejected),
            RemotePushOutcome.Redirected => FlowResult.Failed(PublicationCodes.Redirected),
            _ => FlowResult.Failed(PublicationCodes.PushRefused),
        };
    }

    /// <summary>Stretch B, without the project: the platform's CLI never runs with it taken.</summary>
    private async Task<FlowResult?> PullRequestAsync(FlowContext context)
    {
        if (Deps.Providers.For(repository) is not { } provider)
        {
            return FlowResult.Failed(PublicationCodes.NoProvider);
        }

        // The gate passes before anything of the platform runs, and again at every retry (D-26): a verdict challenged meanwhile stops it here.
        var openOperation = new HostingOperation(project, run, HostingOperationKind.OpenPullRequest, repository, baseBranch, null, commit, null);
        if (await AuthorizeAsync(openOperation, holdsProject: false, context.Stopping).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        if (!_accountRead)
        {
            var account = await provider.ReadAccountAsync(repository, context.Stopping).ConfigureAwait(false);
            _account = account.Outcome == HostingOutcome.Ok ? account.Value : null;
            _accountRead = true;
        }

        // What is already there is asked before anything is repeated (D-18, step 5): a cut after the platform opened it leaves it opened.
        var found = await provider.FindPullRequestAsync(repository, Head, context.Stopping).ConfigureAwait(false);
        if (Interrupted(context, found) is { } interrupted)
        {
            return interrupted;
        }

        var existing = found.Value;
        _prKind = existing is null ? HostingOperationKind.OpenPullRequest : HostingOperationKind.UpdatePullRequest;
        if (existing is not null)
        {
            var update = new HostingOperation(project, run, HostingOperationKind.UpdatePullRequest, repository, baseBranch, existing, commit, null);
            if (await AuthorizeAsync(update, holdsProject: false, context.Stopping).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var updated = await provider.UpdatePullRequestAsync(existing, text, context.Stopping).ConfigureAwait(false);
            if (Interrupted(context, updated) is { } failed)
            {
                return failed;
            }

            _pullRequest = existing;
        }
        else
        {
            var opened = await provider.OpenPullRequestAsync(new PullRequestDraft(repository, Head, baseBranch, text, draft), context.Stopping).ConfigureAwait(false);
            if (opened.Outcome != HostingOutcome.Ok && Unclear(opened.Outcome))
            {
                // It may have been opened although the answer did not say so: the platform is asked before the request is taken as failed.
                var check = await provider.FindPullRequestAsync(repository, Head, context.Stopping).ConfigureAwait(false);
                if (check.Outcome == HostingOutcome.Ok && check.Value is { } there)
                {
                    opened = new HostingResult<PullRequestRef>(HostingOutcome.Ok, there);
                }
            }

            if (Interrupted(context, opened) is { } failed)
            {
                return failed;
            }

            _pullRequest = opened.Value;
        }

        _stage = Stage.NotePullRequest;
        return null;
    }

    private async Task<FlowResult?> NotePullRequestAsync(FlowContext context)
    {
        var operation = new HostingOperation(project, run, _prKind, repository, baseBranch, _pullRequest, commit, null);
        await NoteAsync(context, Key, Payload(operation, HostingOutcome.Ok, null, Head.Name, _account, textEvidence, textSha, null, null)).ConfigureAwait(false);
        _stage = Stage.Verdict;
        return null;
    }

    private async Task<FlowResult?> VerdictAsync(FlowContext context)
    {
        // Without a verdict that counts (the log does not verify or none judged the run) nothing is published (D-21), and that is not a failure of the publication.
        _verdict.Package ??= await Deps.Notices.BuildAsync(project, run, null, context.Stopping).ConfigureAwait(false);
        if (_verdict.Package is null)
        {
            _stage = Stage.NoteVerdict;
            return null;
        }

        var stop = await PublishVerdictAsync(context, project, run, _pullRequest!, commit, baseBranch, null, _verdict).ConfigureAwait(false);
        if (stop is { } result)
        {
            return result;
        }

        _stage = Stage.NoteVerdict;
        return null;
    }

    private async Task<FlowResult?> NoteVerdictStageAsync(FlowContext context)
    {
        if (_verdict.Package is not null)
        {
            await NoteVerdictAsync(context, project, run, _pullRequest!, commit, baseBranch, _account, _verdict).ConfigureAwait(false);
        }

        context.Follow(_pullRequest!, run.Value);
        _stage = Stage.Finished;
        return null;
    }
}

/// <summary>The verdict published again for a pull request the person already has (docs/21 D-22): stretches D and E of the publication.</summary>
internal sealed class VerdictFlow(
    RemoteWorkTicket ticket, ProjectContext project, RunId run, PullRequestRef pullRequest, string commit, RemoteBranch? baseBranch, string? head, FlowDependencies deps, ApprovalLease lease)
    : HostingFlow(ticket, pullRequest.TraceId, new HostingOperation(project, run, HostingOperationKind.PublishVerdictStatus, pullRequest.Repository, baseBranch, pullRequest, commit, null), deps, lease)
{
    private readonly VerdictChannels _verdict = new();

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        if (_verdict.Published is null)
        {
            var stop = await PublishVerdictAsync(context, project, run, pullRequest, commit, baseBranch, head, _verdict).ConfigureAwait(false);
            if (stop is { } result)
            {
                return result;
            }
        }

        await NoteVerdictAsync(context, project, run, pullRequest, commit, baseBranch, null, _verdict).ConfigureAwait(false);
        return FlowResult.Done;
    }
}

/// <summary>
/// Merging (docs/21 D-20): the platform is asked what the pull request is before and after, and what is annotated is what really happened, never what was asked. Before it asks, the pull
/// request has to be readable, its head the judged commit and its base the one that was approved; otherwise nothing is merged.
/// </summary>
internal sealed class MergeFlow(
    RemoteWorkTicket ticket, ProjectContext project, RunId run, PullRequestRef pullRequest, string expectedHead, MergeMethod method, RemoteBranch baseBranch, FlowDependencies deps, ApprovalLease lease)
    : HostingFlow(ticket, pullRequest.TraceId, new HostingOperation(project, run, HostingOperationKind.MergePullRequest, pullRequest.Repository, baseBranch, pullRequest, expectedHead, method), deps, lease)
{
    private bool _noted;

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        var operation = Operation!;
        var provider = Deps.Providers.For(pullRequest.Repository);
        if (provider is null)
        {
            return FlowResult.Failed(PublicationCodes.NoProvider);
        }

        if (await AuthorizeAsync(operation, false, context.Stopping).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        // After a cut it may be merged already: it is asked before the merge is asked for again. What cannot be read stops the merge.
        var before = await context.ObserveAsync(pullRequest).ConfigureAwait(false);
        if (before is null)
        {
            return FlowResult.Failed(PublicationCodes.PullRequestUnreadable);
        }

        if (before.State == PullRequestState.Merged)
        {
            return await NoteAsync(context, operation, HostingOutcome.Ok, null).ConfigureAwait(false);
        }

        if (!string.Equals(before.HeadCommit, expectedHead, StringComparison.Ordinal))
        {
            return FlowResult.Failed(PublicationCodes.BranchMoved);
        }

        if (!string.Equals(before.BaseBranch.Name, baseBranch.Name, StringComparison.Ordinal))
        {
            return FlowResult.Failed(PublicationCodes.BaseChanged);
        }

        var merged = await provider.MergeAsync(pullRequest, method, expectedHead, context.Stopping).ConfigureAwait(false);
        context.Outcome(merged.Outcome);

        // Whatever came back (the server may have applied it even if the answer was lost): the platform is read before anything is annotated or repeated.
        var after = await context.ObserveAsync(pullRequest).ConfigureAwait(false);
        if (after is { State: PullRequestState.Merged })
        {
            return await NoteAsync(context, operation, HostingOutcome.Ok, null).ConfigureAwait(false);
        }

        if (merged.Outcome == HostingOutcome.Ok && merged.Value is { Merged: false })
        {
            return await NoteAsync(context, operation, HostingOutcome.Refused, "merge.blocked", PublicationCodes.MergeBlocked).ConfigureAwait(false);
        }

        if (Interrupted(context, merged) is { } interrupted)
        {
            return interrupted;
        }

        // Ok but the platform does not show it merged yet (a replica behind): it is tried again at the next round, which reads first.
        return FlowResult.WaitForSync(PendingReason.Offline, PublicationCodes.OfflineWait(0), PublicationCodes.Unreachable);
    }

    private async Task<FlowResult> NoteAsync(FlowContext context, HostingOperation operation, HostingOutcome outcome, string? failure, string? message = null)
    {
        if (!_noted)
        {
            await NoteAsync(context, Key, Payload(operation, outcome, failure, null, null, null, null, null, null)).ConfigureAwait(false);
            _noted = true;
        }

        return message is null ? FlowResult.Done : FlowResult.Failed(message);
    }
}

/// <summary>What a check of the state says about a write whose answer was not clear.</summary>
internal enum WriteCheck
{
    Unknown,
    Done,
    Busy,
}

/// <summary>
/// Asking for reviewers, closing a pull request, deleting the branch of a run: one call to the platform, passed by the gate, and its entry. An answer that says nothing certain (no connection,
/// forbidden, unreadable) does not make the write a failure: the state is asked first (the pull request is read after closing; the branch, with git, before deleting again), and a deletion
/// whose branch is already gone is done. Asking for reviewers has nothing to read back, and asking twice is harmless.
/// </summary>
internal sealed class WriteFlow(
    RemoteWorkTicket ticket, HostingOperation target, Func<IHostingProvider, CancellationToken, Task<HostingResult<bool>>> call, bool observeAfter, FlowDependencies deps, ApprovalLease lease,
    string? detail = null, Func<FlowContext, Task<WriteCheck>>? verify = null)
    : HostingFlow(ticket, target.PullRequest?.TraceId ?? "run:" + target.Run.Value, target, deps, lease)
{
    private bool _attempted;

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        var operation = Operation!;
        var provider = Deps.Providers.For(operation.Repository);
        if (provider is null)
        {
            return FlowResult.Failed(PublicationCodes.NoProvider);
        }

        if (await AuthorizeAsync(operation, false, context.Stopping, detail).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var done = false;
        if (_attempted && verify is not null)
        {
            // After a cut, before it is asked again.
            var check = await verify(context).ConfigureAwait(false);
            if (check == WriteCheck.Busy)
            {
                return FlowResult.WaitForProject();
            }

            done = check == WriteCheck.Done;
        }

        if (!done)
        {
            _attempted = true;
            var result = await call(provider, context.Stopping).ConfigureAwait(false);
            if (result.Outcome != HostingOutcome.Ok)
            {
                if (Unclear(result.Outcome))
                {
                    if (observeAfter && operation.PullRequest is not null)
                    {
                        done = await context.ObserveAsync(operation.PullRequest).ConfigureAwait(false) is { State: PullRequestState.Closed };
                    }
                    else if (verify is not null)
                    {
                        var check = await verify(context).ConfigureAwait(false);
                        if (check == WriteCheck.Busy)
                        {
                            // It could not be read now: it stays pending, and the next round reads before it asks again.
                            return FlowResult.WaitForSync(PendingReason.Offline, PublicationCodes.OfflineWait(0), PublicationCodes.Unreachable);
                        }

                        done = check == WriteCheck.Done;
                    }
                }

                if (!done)
                {
                    return Interrupted(context, result) ?? FlowResult.Failed(PublicationCodes.ProviderRefused);
                }
            }
            else if (observeAfter && operation.PullRequest is not null)
            {
                await context.ObserveAsync(operation.PullRequest).ConfigureAwait(false);
            }
        }

        context.Outcome(HostingOutcome.Ok);
        await NoteAsync(
            context, Key,
            Payload(operation, HostingOutcome.Ok, null, operation.Kind == HostingOperationKind.DeleteRemoteBranch ? RemoteBranch.ForRun(operation.Run).Name : null, null, null, null, null, null)).ConfigureAwait(false);
        return FlowResult.Done;
    }
}
