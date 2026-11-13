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

/// <summary>The base of the flows that talk to the platform: how an answer interrupts, and how an operation is annotated (docs/21 D-18, D-25 and D-26).</summary>
internal abstract class HostingFlow(RemoteWorkTicket ticket, string key, HostingOperation? operation, FlowDependencies deps, ApprovalLease lease) : RemoteFlow(ticket, key, operation)
{
    protected FlowDependencies Deps { get; } = deps;

    protected ApprovalLease Lease { get; } = lease;

    private int _attempt;

    /// <summary>Passes the gate just before an operation of the mailbox runs: it never asks (the person was asked at the click).</summary>
    protected async Task<FlowResult?> AuthorizeAsync(FlowContext context, HostingOperation operation, bool holdsProject, CancellationToken ct)
    {
        var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, mayAsk: false, holdsProject, ct).ConfigureAwait(false);
        return authorization.Allowed ? null : new FlowResult(FlowEnd.Failed, Message: authorization.Refusal ?? UserText.Of(PublicationCodes.StateChanged));
    }

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

    /// <summary>The entry of an operation performed (or refused or failed): null when what allowed it cannot be said (an approval with no entry in the log), and then nothing is written.</summary>
    protected HostingOperationPerformedPayload? Payload(
        HostingOperation operation, HostingAuthorization authorization, HostingOutcome outcome, string? failure, string? branch, RemoteLogin? account,
        EvidenceId? text, string? textSha, string? verdict, long? commentId)
    {
        string? grant = null;
        long? sequence = null;
        if (authorization.Source == HostingAuthorizationSource.StandingGrant)
        {
            grant = authorization.GrantId;
        }
        else
        {
            sequence = Deps.Gate.SequenceOf(Lease, operation);
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
}

/// <summary>
/// «Publicar y abrir PR» (docs/21 D-18), in stretches, idempotent: A with the project in hand and not split (the inspection, the <c>push</c>, <c>ls-remote</c> and their entry), B the pull
/// request (look for it, open or update), C its entry, D the verdict, E its entries and the follow. After a cut, the next try asks what is already done before it repeats anything.
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
    }

    private Stage _stage;
    private PullRequestRef? _pullRequest;
    private HostingOperationKind _prKind;
    private HostingAuthorization? _prAuthorization;
    private RemoteLogin? _account;
    private bool _accountRead;
    private PublishedVerdict? _published;
    private VerdictPackage? _package;

    private RemoteBranch Head => RemoteBranch.ForRun(run);

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        while (true)
        {
            if (_stage != Stage.Git && context.Closing)
            {
                return FlowResult.WaitForSync(PendingReason.Offline, TimeSpan.FromMinutes(1));
            }

            FlowResult? stop = _stage switch
            {
                Stage.Git => await GitAsync(context).ConfigureAwait(false),
                Stage.PullRequest => await PullRequestAsync(context).ConfigureAwait(false),
                Stage.NotePullRequest => await NotePullRequestAsync(context).ConfigureAwait(false),
                Stage.Verdict => await VerdictAsync(context).ConfigureAwait(false),
                _ => await NoteVerdictAsync(context).ConfigureAwait(false),
            };
            if (stop is { } result)
            {
                return result;
            }

            if (_stage == Stage.NoteVerdict + 1)
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
        var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, mayAsk: false, holdsProject: true, CancellationToken.None).ConfigureAwait(false);
        if (!authorization.Allowed)
        {
            return new FlowResult(FlowEnd.Failed, Message: authorization.Refusal);
        }

        // No token that can be cancelled reaches git: a push is not cut halfway.
        var push = await Deps.Git.PushRunBranchAsync(new RemotePush(project, repository, run, commit), CancellationToken.None).ConfigureAwait(false);
        var read = await Deps.Git.ReadBranchAsync(project, repository, Head, CancellationToken.None).ConfigureAwait(false);
        var confirmed = read.Reached && string.Equals(read.Commit, commit, StringComparison.Ordinal);
        var pushed = push.Outcome is RemotePushOutcome.Pushed or RemotePushOutcome.AlreadyUpToDate || (push.Outcome == RemotePushOutcome.NotConfirmed && confirmed);
        var outcome = pushed && confirmed ? HostingOutcome.Ok : push.Outcome == RemotePushOutcome.Unreachable || !read.Reached || push.Outcome == RemotePushOutcome.NotConfirmed ? HostingOutcome.Unreachable : HostingOutcome.Refused;
        var failure = pushed && confirmed ? null : push.FailureCode is { } code && UserText.IsCode(code) ? code : "push." + push.Outcome.ToString().ToLowerInvariant();
        if (Payload(operation, authorization, outcome, failure, Head.Name, null, null, null, null, null) is { } payload)
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
        var operation = new HostingOperation(project, run, _prKind, repository, baseBranch, existing, commit, null);
        if (await AuthorizeAsync(context, operation, holdsProject: false, context.Stopping).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        _prAuthorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, false, false, context.Stopping).ConfigureAwait(false);
        if (existing is null)
        {
            var opened = await provider.OpenPullRequestAsync(new PullRequestDraft(repository, Head, baseBranch, text, draft), context.Stopping).ConfigureAwait(false);
            if (Interrupted(context, opened) is { } failed)
            {
                return failed;
            }

            _pullRequest = opened.Value;
        }
        else
        {
            var updated = await provider.UpdatePullRequestAsync(existing, text, context.Stopping).ConfigureAwait(false);
            if (Interrupted(context, updated) is { } failed)
            {
                return failed;
            }

            _pullRequest = existing;
        }

        _stage = Stage.NotePullRequest;
        return null;
    }

    private async Task<FlowResult?> NotePullRequestAsync(FlowContext context)
    {
        var operation = new HostingOperation(project, run, _prKind, repository, baseBranch, _pullRequest, commit, null);
        var payload = Payload(operation, _prAuthorization!, HostingOutcome.Ok, null, Head.Name, _account, textEvidence, textSha, null, null);
        await NoteAsync(context, Key, payload).ConfigureAwait(false);
        _stage = Stage.Verdict;
        return null;
    }

    private async Task<FlowResult?> VerdictAsync(FlowContext context)
    {
        _package ??= await Deps.Notices.BuildAsync(project, run, null, context.Stopping).ConfigureAwait(false);
        if (_package is null)
        {
            // The log does not verify or no verdict judged the run: nothing is published (D-21).
            await FinishAsync(context).ConfigureAwait(false);
            return null;
        }

        var pr = _pullRequest!;
        foreach (var kind in new[] { HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment })
        {
            if (await AuthorizeAsync(context, new HostingOperation(project, run, kind, repository, null, pr, null, null), holdsProject: false, context.Stopping).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        var provider = Deps.Providers.For(repository)!;
        var result = await provider.PublishVerdictAsync(pr, _package.Notice, _package.StatusDescription, _package.CommentBody, null, context.Stopping).ConfigureAwait(false);
        if (Interrupted(context, result) is { } interrupted)
        {
            return interrupted;
        }

        _published = result.Value;
        _stage = Stage.NoteVerdict;
        return null;
    }

    private async Task<FlowResult?> NoteVerdictAsync(FlowContext context)
    {
        var pr = _pullRequest!;
        var standing = _package!.Notice.Standing.ToString();
        foreach (var kind in new[] { HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment })
        {
            var operation = new HostingOperation(project, run, kind, repository, null, pr, null, null);
            var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, false, false, context.Stopping).ConfigureAwait(false);
            if (!authorization.Allowed)
            {
                continue;
            }

            var published = kind == HostingOperationKind.PublishVerdictStatus ? _published?.StatusPublished != false : _published?.CommentId is not null;
            var payload = Payload(
                operation, authorization, published ? HostingOutcome.Ok : HostingOutcome.Refused, published ? null : "verdict.not-published", null, _account, null, null, standing,
                kind == HostingOperationKind.PublishVerdictComment ? _published?.CommentId : null);
            await NoteAsync(context, Key, payload).ConfigureAwait(false);
        }

        await FinishAsync(context).ConfigureAwait(false);
        return null;
    }

    private Task FinishAsync(FlowContext context)
    {
        if (_pullRequest is { } pr)
        {
            context.Follow(pr, run.Value);
        }

        _stage = Stage.NoteVerdict + 1;
        return Task.CompletedTask;
    }
}

/// <summary>The verdict published again for a pull request the person already has (docs/21 D-22): stretch D and E of the publication.</summary>
internal sealed class VerdictFlow(
    RemoteWorkTicket ticket, ProjectContext project, RunId run, PullRequestRef pullRequest, string? head, FlowDependencies deps, ApprovalLease lease)
    : HostingFlow(ticket, pullRequest.TraceId, new HostingOperation(project, run, HostingOperationKind.PublishVerdictStatus, pullRequest.Repository, null, pullRequest, null, null), deps, lease)
{
    private PublishedVerdict? _published;
    private VerdictPackage? _package;

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        if (_published is null)
        {
            _package ??= await Deps.Notices.BuildAsync(project, run, head, context.Stopping).ConfigureAwait(false);
            if (_package is null)
            {
                return FlowResult.Failed(PublicationCodes.NoVerdict);
            }

            foreach (var kind in new[] { HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment })
            {
                if (await AuthorizeAsync(context, new HostingOperation(project, run, kind, pullRequest.Repository, null, pullRequest, null, null), false, context.Stopping).ConfigureAwait(false) is { } refused)
                {
                    return new FlowResult(refused.End, Message: refused.Message);
                }
            }

            var provider = Deps.Providers.For(pullRequest.Repository);
            if (provider is null)
            {
                return FlowResult.Failed(PublicationCodes.NoProvider);
            }

            var result = await provider.PublishVerdictAsync(pullRequest, _package.Notice, _package.StatusDescription, _package.CommentBody, null, context.Stopping).ConfigureAwait(false);
            if (Interrupted(context, result) is { } interrupted)
            {
                return interrupted;
            }

            _published = result.Value;
        }

        var standing = _package!.Notice.Standing.ToString();
        foreach (var kind in new[] { HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment })
        {
            var operation = new HostingOperation(project, run, kind, pullRequest.Repository, null, pullRequest, null, null);
            var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, false, false, context.Stopping).ConfigureAwait(false);
            if (authorization.Allowed)
            {
                var published = kind == HostingOperationKind.PublishVerdictStatus ? _published?.StatusPublished != false : _published?.CommentId is not null;
                await NoteAsync(
                    context, Key,
                    Payload(operation, authorization, published ? HostingOutcome.Ok : HostingOutcome.Refused, published ? null : "verdict.not-published", null, null, null, null, standing,
                        kind == HostingOperationKind.PublishVerdictComment ? _published?.CommentId : null)).ConfigureAwait(false);
            }
        }

        return FlowResult.Done;
    }
}

/// <summary>Merging (docs/21 D-20): the platform is asked what the pull request is before and after, and what is annotated is what really happened, never what was asked.</summary>
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

        // After a cut it may be merged already: it is asked before the merge is asked for again.
        var before = await context.ObserveAsync(pullRequest).ConfigureAwait(false);
        if (before is { State: PullRequestState.Merged })
        {
            return await NoteAsync(context, operation, HostingOutcome.Ok, null).ConfigureAwait(false);
        }

        if (before is not null && !string.Equals(before.HeadCommit, expectedHead, StringComparison.Ordinal))
        {
            return FlowResult.Failed(PublicationCodes.BranchMoved);
        }

        if (await AuthorizeAsync(context, operation, false, context.Stopping).ConfigureAwait(false) is { } refused)
        {
            return refused;
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
            var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, false, false, context.Stopping).ConfigureAwait(false);
            if (authorization.Allowed)
            {
                await NoteAsync(context, Key, Payload(operation, authorization, outcome, failure, null, null, null, null, null, null)).ConfigureAwait(false);
            }

            _noted = true;
        }

        return message is null ? FlowResult.Done : FlowResult.Failed(message);
    }
}

/// <summary>Asking for reviewers, closing a pull request: one call to the platform, passed by the gate, and its entry (closing is read back afterwards).</summary>
internal sealed class WriteFlow(
    RemoteWorkTicket ticket, HostingOperation target, Func<IHostingProvider, CancellationToken, Task<HostingResult<bool>>> call, bool observeAfter, FlowDependencies deps, ApprovalLease lease)
    : HostingFlow(ticket, target.PullRequest?.TraceId ?? "run:" + target.Run.Value, target, deps, lease)
{
    private bool _called;

    public override async Task<FlowResult> RunAsync(FlowContext context)
    {
        var operation = Operation!;
        var provider = Deps.Providers.For(operation.Repository);
        if (provider is null)
        {
            return FlowResult.Failed(PublicationCodes.NoProvider);
        }

        if (!_called)
        {
            if (await AuthorizeAsync(context, operation, false, context.Stopping).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var result = await call(provider, context.Stopping).ConfigureAwait(false);
            if (Interrupted(context, result) is { } interrupted)
            {
                if (!observeAfter || operation.PullRequest is null)
                {
                    return interrupted;
                }

                // An answer that was lost does not say it did not happen: the pull request is read first.
                var seen = await context.ObserveAsync(operation.PullRequest).ConfigureAwait(false);
                if (seen is not { State: PullRequestState.Closed })
                {
                    return interrupted;
                }
            }

            _called = true;
            if (observeAfter && operation.PullRequest is not null)
            {
                await context.ObserveAsync(operation.PullRequest).ConfigureAwait(false);
            }
        }

        var authorization = await Deps.Gate.AuthorizeUseAsync(operation, Lease, false, false, context.Stopping).ConfigureAwait(false);
        if (authorization.Allowed)
        {
            await NoteAsync(context, Key, Payload(operation, authorization, HostingOutcome.Ok, null, operation.Kind == HostingOperationKind.DeleteRemoteBranch ? RemoteBranch.ForRun(operation.Run).Name : null, null, null, null, null, null)).ConfigureAwait(false);
        }

        return FlowResult.Done;
    }
}
