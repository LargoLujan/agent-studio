using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Orchestration.Publication;

/// <summary>
/// What the person asks of the remote (docs/21 D-18 and D-20). Each call does at the click what needs a person (the checks, the approval with Windows Hello when no standing grant covers it,
/// the text as evidence) and enqueues the rest in the mailbox of the project, which runs it in stretches: git and the log with the project taken, the platform's CLI without it.
/// </summary>
/// <remarks>
/// It reaches the platform and git only through the gate (<see cref="IHostingUseGate"/>): the two ports are used inside flows, after the gate passed. With the project busy, what needs git
/// (the preview, the inspection, publishing) launches nothing and says so.
/// </remarks>
public sealed class PublicationService : IPublicationService
{
    private readonly IHostingUseGate _gate;
    private readonly IRemoteMailboxes _mailboxes;
    private readonly IDeliveryService _delivery;
    private readonly IHostingProviders _providers;
    private readonly IRemoteGitRunner _git;
    private readonly IDecisionRecorder _recorder;
    private readonly IEvidenceStore _evidence;
    private readonly PullRequestTextComposer _composer;
    private readonly FlowDependencies _deps;

    internal PublicationService(
        IHostingUseGate gate, IRemoteMailboxes mailboxes, IDeliveryService delivery, IHostingProviders providers, IRemoteGitRunner git, IDecisionRecorder recorder,
        IEvidenceStore evidence, PullRequestTextComposer composer, VerdictNoticeBuilder notices)
    {
        _gate = gate;
        _mailboxes = mailboxes;
        _delivery = delivery;
        _providers = providers;
        _git = git;
        _recorder = recorder;
        _evidence = evidence;
        _composer = composer;
        _deps = new FlowDependencies(gate, providers, git, notices);
    }

    public async Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        return await box.ExecAsync(async context =>
        {
            using var taken = context.Take();
            if (taken is null)
            {
                return null;
            }

            var repository = await _providers.IdentifyAsync(project, "origin", CancellationToken.None).ConfigureAwait(false);
            return repository is null ? null : await _git.InspectAsync(project, repository, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var readiness = await _delivery.CheckAsync(project, run, ct).ConfigureAwait(false);
        var empty = new PullRequestText(string.Empty, string.Empty);
        if (!readiness.Ready || readiness.Commit is null)
        {
            return NotReady(readiness.ReasonText ?? UserText.Of(PublicationCodes.VerdictNotApproved), null, null, empty);
        }

        // The part of git, with the project in hand: with it busy nothing is launched.
        var local = await box.ExecAsync(async context =>
        {
            using var taken = context.Take();
            if (taken is null)
            {
                return new Located(true, null);
            }

            var repository = await _providers.IdentifyAsync(project, "origin", CancellationToken.None).ConfigureAwait(false);
            if (repository is null)
            {
                return new Located(false, null);
            }

            context.Trust(repository);
            var inspection = await _git.InspectAsync(project, repository, CancellationToken.None).ConfigureAwait(false);
            var remote = inspection.Accepted ? await _git.ReadBranchAsync(project, repository, RemoteBranch.ForRun(run), CancellationToken.None).ConfigureAwait(false) : null;
            return new Located(false, repository, inspection, remote);
        }).ConfigureAwait(false);
        if (local.Busy)
        {
            return NotReady(UserText.Of(PublicationCodes.ProjectBusy), null, null, empty);
        }

        if (local.Repository is not { } repo || _providers.For(repo) is not { } provider)
        {
            return NotReady(UserText.Of(local.Repository is null ? PublicationCodes.NoRemote : PublicationCodes.NoProvider), local.Repository, local.Inspection, empty);
        }

        // The part of the platform, without the project.
        var availability = await provider.ProbeAsync(ct).ConfigureAwait(false);
        var facts = await provider.ReadRepositoryAsync(repo, null, ct).ConfigureAwait(false);
        var capabilities = facts.Value is { } known ? availability.Capabilities.NarrowedBy(known) : availability.Capabilities;
        var target = baseBranch ?? facts.Value?.DefaultBranch;
        BranchRules? rules = null;
        PullRequestRef? existing = null;
        if (target is not null)
        {
            var read = await provider.ReadBranchRulesAsync(repo, target, ct).ConfigureAwait(false);
            rules = read.Outcome == HostingOutcome.Ok && read.Value is not null ? read.Value : BranchRules.Unreadable;
            var found = await provider.FindPullRequestAsync(repo, RemoteBranch.ForRun(run), ct).ConfigureAwait(false);
            existing = found.Outcome == HostingOutcome.Ok ? found.Value : null;
        }

        var text = await _composer.ComposeAsync(project, run, readiness.Commit, ct).ConfigureAwait(false);
        UserText? reason = null;
        if (local.Inspection is { Accepted: false })
        {
            reason = UserText.Of(PublicationCodes.ConfigRefused);
        }
        else if (!capabilities.Has(HostingCapability.OpenPullRequest))
        {
            reason = UserText.Of(PublicationCodes.CapabilityMissing);
        }
        else if (target is null)
        {
            reason = UserText.Of(PublicationCodes.ProviderRefused);
        }

        // The counts of commits need a reader of history that the ports of this package do not give: they are left unknown (pending in the ADR).
        return new PublicationPreview(reason is null, reason, repo, readiness.Commit, target, 0, null, rules, local.Inspection, capabilities, text, existing);
    }

    public async Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = request.Project;
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.PushRunBranch, null);
        var readiness = await _delivery.CheckAsync(project, request.Run, ct).ConfigureAwait(false);
        if (!readiness.Ready || readiness.Commit is not { } commit)
        {
            return Rejected(box, ticket, readiness.ReasonText ?? UserText.Of(PublicationCodes.VerdictNotApproved));
        }

        if (request.Text.Title.Length > PullRequestTextComposer.MaxTitle || request.Text.Body.Length > PullRequestTextComposer.MaxBody)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.StateChanged));
        }

        var identified = await box.ExecAsync(async context =>
        {
            using var taken = context.Take();
            if (taken is null)
            {
                return new Located(true, null);
            }

            var repository = await _providers.IdentifyAsync(project, "origin", CancellationToken.None).ConfigureAwait(false);
            if (repository is not null)
            {
                context.Trust(repository);
            }

            return new Located(false, repository);
        }).ConfigureAwait(false);
        if (identified.Busy)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.ProjectBusy));
        }

        if (identified.Repository is not { } repo)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.NoRemote));
        }

        // At the click, outside the project and the consumer: the person is asked for each class that no standing grant covers.
        var lease = new ApprovalLease();
        var push = new HostingOperation(project, request.Run, HostingOperationKind.PushRunBranch, repo, null, null, commit, null);
        var open = new HostingOperation(project, request.Run, HostingOperationKind.OpenPullRequest, repo, request.BaseBranch, null, commit, null);
        foreach (var operation in new[] { push, open })
        {
            var authorization = await _gate.AuthorizeUseAsync(operation, lease, mayAsk: true, holdsProject: false, ct).ConfigureAwait(false);
            if (!authorization.Allowed)
            {
                return Rejected(box, ticket, authorization.Refusal ?? UserText.Of(PublicationCodes.NotApproved));
            }
        }

        // The text that goes out is evidence with its SHA-256 (D-19); it belongs to no run, so the dossier of the judge never holds it.
        var sent = $"{request.Text.Title}\n\n{request.Text.Body}";
        EvidenceRecord record;
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes(sent)))
        {
            record = await _evidence.CaptureAsync(
                project.ProjectId,
                new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "pull-request-text", "agent-studio", MediaType: "text/markdown"),
                content,
                ct).ConfigureAwait(false);
        }

        var flow = new PublishFlow(ticket, project, request.Run, repo, commit, request.BaseBranch, request.Text, request.Draft, record.Id, record.ContentSha256, _deps, lease);
        return await box.RunAsync(flow).ConfigureAwait(false);
    }

    public async Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.PublishVerdictStatus, pullRequest);
        var known = await KnownAsync(project, pullRequest, ct).ConfigureAwait(false);
        if (known.Run is not { } run)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.UnknownPullRequest));
        }

        var lease = new ApprovalLease();
        var operation = new HostingOperation(project, run, HostingOperationKind.PublishVerdictStatus, pullRequest.Repository, null, pullRequest, null, null);
        var authorization = await _gate.AuthorizeUseAsync(operation, lease, true, false, ct).ConfigureAwait(false);
        if (!authorization.Allowed)
        {
            return Rejected(box, ticket, authorization.Refusal ?? UserText.Of(PublicationCodes.NotApproved));
        }

        return await box.RunAsync(new VerdictFlow(ticket, project, run, pullRequest, known.Head, _deps, lease)).ConfigureAwait(false);
    }

    public async Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.RequestReviewers, pullRequest);
        var known = await KnownAsync(project, pullRequest, ct).ConfigureAwait(false);
        if (known.Run is not { } run || reviewers.Count == 0)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.UnknownPullRequest));
        }

        var readiness = await _delivery.CheckAsync(project, run, ct).ConfigureAwait(false);
        if (!readiness.Ready || readiness.Commit is not { } commit)
        {
            return Rejected(box, ticket, readiness.ReasonText ?? UserText.Of(PublicationCodes.VerdictNotApproved));
        }

        // Always by use: it warns people by name, and no standing grant covers it.
        var operation = new HostingOperation(project, run, HostingOperationKind.RequestReviewers, pullRequest.Repository, null, pullRequest, commit, null);
        return await StartAsync(box, ticket, operation, (provider, token) => provider.RequestReviewersAsync(pullRequest, [.. reviewers], token), false, ct).ConfigureAwait(false);
    }

    public async Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.MergePullRequest, pullRequest);
        var known = await KnownAsync(project, pullRequest, ct).ConfigureAwait(false);
        if (known.Run is not { } run || known.BaseBranch is not { } baseBranch)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.UnknownPullRequest));
        }

        var readiness = await _delivery.CheckAsync(project, run, ct).ConfigureAwait(false);
        if (!readiness.Ready || readiness.Commit is not { } commit)
        {
            return Rejected(box, ticket, readiness.ReasonText ?? UserText.Of(PublicationCodes.VerdictNotApproved));
        }

        // The pull request is read before asking (D-20): the head must still be the judged commit.
        if (known.Head is not null && !string.Equals(known.Head, commit, StringComparison.Ordinal))
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.BranchMoved));
        }

        var lease = new ApprovalLease();
        var operation = new HostingOperation(project, run, HostingOperationKind.MergePullRequest, pullRequest.Repository, baseBranch, pullRequest, commit, method);
        var authorization = await _gate.AuthorizeUseAsync(operation, lease, true, false, ct).ConfigureAwait(false);
        if (!authorization.Allowed)
        {
            return Rejected(box, ticket, authorization.Refusal ?? UserText.Of(PublicationCodes.NotApproved));
        }

        return await box.RunAsync(new MergeFlow(ticket, project, run, pullRequest, commit, method, baseBranch, _deps, lease)).ConfigureAwait(false);
    }

    public async Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.ClosePullRequest, pullRequest);
        var known = await KnownAsync(project, pullRequest, ct).ConfigureAwait(false);
        if (known.Run is not { } run)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.UnknownPullRequest));
        }

        var operation = new HostingOperation(project, run, HostingOperationKind.ClosePullRequest, pullRequest.Repository, null, pullRequest, null, null);
        return await StartAsync(box, ticket, operation, (provider, token) => provider.CloseAsync(pullRequest, token), true, ct).ConfigureAwait(false);
    }

    public async Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct)
    {
        var box = await _mailboxes.OfAsync(project, ct).ConfigureAwait(false);
        var ticket = NewTicket(HostingOperationKind.DeleteRemoteBranch, null);
        var branch = RemoteBranch.ForRun(run);

        // The head of the branch on the remote is read just before (the API of deletion admits no condition, so a minimal window remains: it is a residual of ADR-0067).
        var read = await box.ExecAsync(async context =>
        {
            using var taken = context.Take();
            if (taken is null)
            {
                return new Located(true, null);
            }

            var repository = await _providers.IdentifyAsync(project, "origin", CancellationToken.None).ConfigureAwait(false);
            if (repository is null)
            {
                return new Located(false, null);
            }

            context.Trust(repository);
            var head = await _git.ReadBranchAsync(project, repository, branch, CancellationToken.None).ConfigureAwait(false);
            return new Located(false, repository, Commit: head.Reached ? head.Commit : null);
        }).ConfigureAwait(false);
        if (read.Busy)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.ProjectBusy));
        }

        if (read.Repository is not { } repo || read.Commit is not { } expected)
        {
            return Rejected(box, ticket, UserText.Of(PublicationCodes.NoRemote));
        }

        var operation = new HostingOperation(project, run, HostingOperationKind.DeleteRemoteBranch, repo, null, null, null, null);
        return await StartAsync(box, ticket, operation, (provider, token) => provider.DeleteRunBranchAsync(repo, branch, expected, token), false, ct).ConfigureAwait(false);
    }

    private async Task<RemoteWorkTicket> StartAsync(
        ProjectRemoteMailbox box, RemoteWorkTicket ticket, HostingOperation operation, Func<IHostingProvider, CancellationToken, Task<HostingResult<bool>>> call, bool observeAfter, CancellationToken ct)
    {
        var lease = new ApprovalLease();
        var authorization = await _gate.AuthorizeUseAsync(operation, lease, mayAsk: true, holdsProject: false, ct).ConfigureAwait(false);
        if (!authorization.Allowed)
        {
            return Rejected(box, ticket, authorization.Refusal ?? UserText.Of(PublicationCodes.NotApproved));
        }

        return await box.RunAsync(new WriteFlow(ticket, operation, call, observeAfter, _deps, lease)).ConfigureAwait(false);
    }

    /// <summary>What the verified log says of a pull request: its run, its base branch and the head as last observed. A log that does not verify says nothing.</summary>
    private async Task<(string? Head, RunId? Run, RemoteBranch? BaseBranch)> KnownAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct)
    {
        var log = await _recorder.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return (null, null, null);
        }

        var reading = RemoteLogReading.ReadEntries(log.Entries);
        var observed = reading.Find(pullRequest);
        var run = observed?.Run ?? reading.Operations
            .LastOrDefault(o => o.Payload is { Operation: HostingOperationKind.OpenPullRequest, Outcome: HostingOutcome.Ok } p && p.PullRequest == pullRequest.Number
                && $"{p.Host}/{p.Owner}/{p.Repository}" == pullRequest.Repository.Id)?.Payload.Run;
        return (observed?.Latest.HeadCommit, run is null ? null : new RunId(run), observed?.Latest.BaseBranch);
    }

    private sealed record Located(bool Busy, RemoteRepository? Repository, RemoteGitInspection? Inspection = null, RemoteBranchRead? Remote = null, string? Commit = null);

    private static PublicationPreview NotReady(UserText reason, RemoteRepository? repository, RemoteGitInspection? inspection, PullRequestText text) =>
        new(false, reason, repository, null, null, 0, null, null, inspection, HostingCapabilities.None, text, null);

    private static RemoteWorkTicket NewTicket(HostingOperationKind kind, PullRequestRef? pullRequest) => new(Guid.NewGuid().ToString("N"), kind, pullRequest);

    private static RemoteWorkTicket Rejected(ProjectRemoteMailbox box, RemoteWorkTicket ticket, UserText reason)
    {
        box.Reject(ticket, reason);
        return ticket;
    }
}

/// <summary>The registration of the publication and the mailbox. The lead calls it from the composition root (<c>Composition/**</c>).</summary>
public static class PublicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gate, the service of publication and the mailboxes with the poll. It needs, from the rest of the app: the decision recorder, the approval gate, the delivery, the stores of verdicts
    /// and evidence, the activity of the project, the providers and the remote runner of git, the local state (<c>AddAgentStudioRemoteSyncStore</c>) and the renderer of texts. The permission of the
    /// network (<see cref="IRemoteSyncPolicy"/>) and the language (<c>IResponseLanguage</c>) are optional: without a policy the poll never runs.
    /// </summary>
    public static IServiceCollection AddAgentStudioPublication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new RemoteSyncService(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<Application.Orchestration.IProjectActivity>(),
            provider.GetRequiredService<IHostingProviders>(),
            provider.GetRequiredService<IRemoteSyncStore>(),
            provider.GetService<IRemoteSyncPolicy>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IRemoteSync>(provider => provider.GetRequiredService<RemoteSyncService>());
        services.TryAddSingleton<IRemoteMailboxes>(provider => provider.GetRequiredService<RemoteSyncService>());
        services.TryAddSingleton(provider => new HostingPolicyGate(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<Application.Security.IApprovalGate>(),
            provider.GetRequiredService<IDeliveryService>(),
            provider.GetRequiredService<IVerdictStore>(),
            provider.GetRequiredService<IHostingProviders>(),
            provider.GetRequiredService<IRemoteGitRunner>(),
            provider.GetRequiredService<IRemoteMailboxes>()));
        services.TryAddSingleton<IHostingPolicyGate>(provider => provider.GetRequiredService<HostingPolicyGate>());
        services.TryAddSingleton<IHostingUseGate>(provider => provider.GetRequiredService<HostingPolicyGate>());
        services.TryAddSingleton(provider => new PullRequestTextComposer(
            provider.GetRequiredService<Application.Documents.IUserTextRenderer>(),
            provider.GetService<Application.Prompts.IResponseLanguage>(),
            provider.GetRequiredService<IVerdictStore>(),
            provider.GetRequiredService<IEvidenceStore>()));
        services.TryAddSingleton(provider => new VerdictNoticeBuilder(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IVerdictStore>(),
            provider.GetRequiredService<Application.Documents.IUserTextRenderer>(),
            provider.GetService<Application.Prompts.IResponseLanguage>()));
        services.TryAddSingleton<IPublicationService>(provider => new PublicationService(
            provider.GetRequiredService<IHostingUseGate>(),
            provider.GetRequiredService<IRemoteMailboxes>(),
            provider.GetRequiredService<IDeliveryService>(),
            provider.GetRequiredService<IHostingProviders>(),
            provider.GetRequiredService<IRemoteGitRunner>(),
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<PullRequestTextComposer>(),
            provider.GetRequiredService<VerdictNoticeBuilder>()));
        return services;
    }
}
