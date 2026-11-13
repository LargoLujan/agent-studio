using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>The doubles of the ports of the remote and the data of the tests of the screens of Phase 10: no network, no <c>gh</c>, no clock of the wall.</summary>
internal static class PrData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    public static readonly ProjectContext Project = new("p-1", @"C:\work\calc");

    public static readonly RemoteRepository Repository = RemoteRepository.TryCreateGitHub("acme", "calc")!;

    public static readonly RemoteBranch Main = RemoteBranch.TryParse("main")!;

    public static string Sha(char digit) => new(digit, 40);

    public static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(Now);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    public static PullRequestRef Pr(int number) => new(Repository, number);

    public static HostingCapabilities Capabilities(HostingCapability supported, params MergeMethod[] methods) => new(supported, methods, false);

    public const HostingCapability Reads = HostingCapability.ReadRepository | HostingCapability.ReadBranchRules | HostingCapability.ReadReviews | HostingCapability.ReadChecks
        | HostingCapability.ReadDeployments;

    public const HostingCapability Writes = HostingCapability.OpenPullRequest | HostingCapability.UpdatePullRequest | HostingCapability.Comment | HostingCapability.RequestReviewers
        | HostingCapability.Merge | HostingCapability.ClosePullRequest | HostingCapability.DeleteBranch | HostingCapability.CommitStatus | HostingCapability.DraftPullRequest;

    public static RemoteSnapshot Snapshot(
        int number = 12,
        PullRequestState state = PullRequestState.Open,
        char head = 'a',
        RemoteMergeMode? mode = null,
        bool headBranchExists = true,
        RemoteDeployment? deployment = null,
        string? mergeCommit = null,
        IReadOnlyList<string>? results = null,
        IReadOnlyList<string>? reverts = null) => new(
            Pr(number),
            state,
            Sha(head),
            Main,
            headBranchExists,
            state == PullRequestState.Merged ? mergeCommit ?? Sha('m') : null,
            state == PullRequestState.Merged ? mode ?? RemoteMergeMode.Squash : null,
            null,
            [Sha('1')],
            results ?? [],
            false,
            new ChecksSummary(ChecksRollup.Passing, 5, 1, 2, false),
            new ReviewsSummary(1, 0, 0, []),
            0,
            false,
            reverts ?? [],
            deployment,
            Now);

    public static PullRequestView View(
        RemoteSnapshot? snapshot,
        int number = 12,
        string? run = "20261112-a1b2c3",
        string? judged = null,
        bool headIsJudged = true,
        VerdictStanding? verdict = VerdictStanding.Approved,
        RemoteDisplayTexts? display = null,
        IReadOnlyList<RemoteWorkStatus>? pending = null) => new(
            Pr(number),
            run is null ? null : new RunId(run),
            snapshot,
            judged ?? snapshot?.HeadCommit,
            headIsJudged,
            verdict,
            display ?? new RemoteDisplayTexts("T-101 calc", ["unit"], "prod"),
            snapshot is null ? null : Now.AddMinutes(-3),
            pending ?? []);
}

internal sealed class FakeProviders(FakeProvider? provider, RemoteRepository? repository) : IHostingProviders
{
    /// <summary>The repository the remote names; a test sets it to null to say the remote is not recognised.</summary>
    public RemoteRepository? Repository { get; set; } = repository;

    public IHostingProvider? For(RemoteRepository repository1) => provider;

    public Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct) => Task.FromResult(Repository);
}

internal sealed class FakeProvider : IHostingProvider
{
    public HostingProviderKind Kind => HostingProviderKind.GitHub;

    public HostingAvailability Availability { get; set; } = new(true, new Version(2, 96, 0), PrData.Capabilities(PrData.Reads | PrData.Writes, MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase));

    public HostingResult<RemoteRepositoryFacts> Facts { get; set; } = new(
        HostingOutcome.Ok,
        new RemoteRepositoryFacts(PrData.Main, [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], true, false));

    public HostingResult<BranchRules> Rules { get; set; } = new(HostingOutcome.Ok, new BranchRules(true, [new BranchRule(BranchRuleKind.RequiredReviews, 1)]));

    public HostingResult<RemoteLogin> Account { get; set; } = new(HostingOutcome.Ok, RemoteLogin.TryParse("octo"));

    public int FactsReads { get; private set; }

    public Task<HostingAvailability> ProbeAsync(CancellationToken ct) => Task.FromResult(Availability);

    public Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct) => Task.FromResult(Account);

    public Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct)
    {
        FactsReads++;
        return Task.FromResult(Facts);
    }

    /// <summary>When set, the next read of the rules waits for it (a detail that is still being read).</summary>
    public TaskCompletionSource? HoldRules { get; set; }

    public async Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct)
    {
        if (HoldRules is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return Rules;
    }

    public Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct) => throw new NotSupportedException("The screen never writes or reads the PR by itself.");

    public Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string statusDescription, string commentBody, long? commentId, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct) => throw new NotSupportedException();

    public Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct) => throw new NotSupportedException();

    public DateTimeOffset? RateLimitedUntil => null;

    public void RestoreRateLimit(DateTimeOffset? until)
    {
    }
}

internal sealed class FakeProjector : IPullRequestProjector
{
    public List<PullRequestView> Views { get; } = [];

    public int Reads { get; private set; }

    /// <summary>Completes the next read as the test says (a slow read that a later one overtakes).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completed every time a read starts after the first one: the test awaits it instead of the clock.</summary>
    public TaskCompletionSource ReadAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? Failure { get; set; }

    public async Task<IReadOnlyList<PullRequestView>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        Reads++;
        if (Reads > 1)
        {
            ReadAgain.TrySetResult();
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        var answer = Views.ToList();
        if (Hold is { } hold)
        {
            Hold = null;
            await hold.Task.WaitAsync(ct);
        }

        return answer;
    }
}

internal sealed class FakeSync : IRemoteSync
{
    public List<RemoteWorkStatus> PendingWork { get; } = [];

    public RemoteSyncHealth HealthNow { get; set; } = new(PrData.Now.AddMinutes(-5), HostingOutcome.Ok, null, PrData.Now.AddMinutes(-5));

    public int SyncNows { get; private set; }

    public List<PullRequestRef> Unfollowed { get; } = [];

    public event EventHandler<string>? Changed;

    /// <summary>When set, «Sincronizar ahora» waits for it, honouring the token it was given: a read that cancelled it would throw.</summary>
    public TaskCompletionSource? HoldSync { get; set; }

    /// <summary>When set, «Sincronizar ahora» waits for it and ignores its token: the answer of a port that ends after the person changed project.</summary>
    public TaskCompletionSource? HoldSyncRaw { get; set; }

    public async Task SyncNowAsync(ProjectContext project, CancellationToken ct)
    {
        SyncNows++;
        if (HoldSyncRaw is { } raw)
        {
            await raw.Task;
        }

        if (HoldSync is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }
    }

    public Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct) => Task.CompletedTask;

    public Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct)
    {
        Unfollowed.Add(pullRequest);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string projectId, string ticketId, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<RemoteWorkStatus> Pending(string projectId) => [.. PendingWork];

    public RemoteSyncHealth Health(string projectId) => HealthNow;

    public void RaiseChanged(string projectId) => Changed?.Invoke(this, projectId);

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
}

internal sealed class FakePublication : IPublicationService
{
    public List<string> Calls { get; } = [];

    public MergeMethod? MergedWith { get; private set; }

    public PublicationRequest? Published { get; private set; }

    public IReadOnlyList<RemoteLogin>? Reviewers { get; private set; }

    public Exception? Failure { get; set; }

    public PublicationPreview Preview { get; set; } = new(
        true, null, PrData.Repository, PrData.Sha('b'), PrData.Main, 2, 1,
        new BranchRules(true, [new BranchRule(BranchRuleKind.RequiredChecks, 2)]),
        new RemoteGitInspection(true, [], false, null),
        PrData.Capabilities(PrData.Writes, MergeMethod.Squash), new PullRequestText("T-101 calc", "Body"), null);

    private Task<RemoteWorkTicket> Ticket(string call, HostingOperationKind kind)
    {
        Calls.Add(call);
        return Failure is not null ? throw Failure : Task.FromResult(new RemoteWorkTicket("t-" + Calls.Count, kind, null));
    }

    public Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException();

    public Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct)
    {
        Calls.Add("preview");
        return Task.FromResult(Preview);
    }

    /// <summary>When set, the publication waits for it with its token.</summary>
    public TaskCompletionSource? HoldPublish { get; set; }

    public async Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct)
    {
        Published = request;
        if (HoldPublish is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return await Ticket("publish", HostingOperationKind.PushRunBranch);
    }

    public Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => Ticket("verdict", HostingOperationKind.PublishVerdictStatus);

    public Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct)
    {
        Reviewers = reviewers;
        return Ticket("reviewers", HostingOperationKind.RequestReviewers);
    }

    public Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct)
    {
        MergedWith = method;
        return Ticket("merge", HostingOperationKind.MergePullRequest);
    }

    public Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => Ticket("close", HostingOperationKind.ClosePullRequest);

    public Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct) => Ticket("delete", HostingOperationKind.DeleteRemoteBranch);
}

/// <summary>The gate as a test sets it. <see cref="AuthorizeAsync"/> throws: the screen must never decide a permission by itself, so any call to it fails the test.</summary>
internal sealed class FakeGate : IHostingPolicyGate
{
    public List<HostingPolicyGrant> Held { get; } = [];

    public List<string> Revoked { get; } = [];

    public bool GrantAnswersNull { get; set; }

    public Task<HostingAuthorization> AuthorizeAsync(HostingOperation operation, CancellationToken ct) => throw new InvalidOperationException("The screen asked the gate to authorize.");

    /// <summary>When set, a grant or a withdrawal waits for it, as the gate does while Windows Hello waits for the person.</summary>
    public TaskCompletionSource? HoldHello { get; set; }

    public async Task<HostingPolicyGrant?> GrantAsync(ProjectContext project, HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch, CancellationToken ct)
    {
        if (HoldHello is { } hold)
        {
            await hold.Task;
        }

        if (GrantAnswersNull)
        {
            return null;
        }

        var grant = new HostingPolicyGrant("g-" + (Held.Count + 1), @class, repository, baseBranch, "person", PrData.Now);
        Held.Add(grant);
        return grant;
    }

    public async Task RevokeAsync(ProjectContext project, string grantId, CancellationToken ct)
    {
        if (HoldHello is { } hold)
        {
            await hold.Task;
        }

        Revoked.Add(grantId);
        Held.RemoveAll(g => g.GrantId == grantId);
    }

    public Task<IReadOnlyList<HostingPolicyGrant>> GrantsAsync(ProjectContext project, CancellationToken ct) => Task.FromResult<IReadOnlyList<HostingPolicyGrant>>([.. Held]);
}

internal sealed class RecordingOpener : IAddressOpener
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri address) => Opened.Add(address);
}

internal sealed class FakePermission : IRemoteSyncPermission
{
    private readonly HashSet<string> _allowed = [];

    public List<(string Project, bool Allowed)> Sets { get; } = [];

    public bool IsAllowed(string projectId) => _allowed.Contains(projectId);

    public TaskCompletionSource? HoldSet { get; set; }

    public async Task SetAsync(string projectId, bool allowed, CancellationToken ct)
    {
        if (HoldSet is { } hold)
        {
            await hold.Task;
        }

        Sets.Add((projectId, allowed));
        _ = allowed ? _allowed.Add(projectId) : _allowed.Remove(projectId);
    }

    public event EventHandler<string>? Changed;

    public void Allow(string projectId) => _allowed.Add(projectId);

    public void RaiseChanged(string projectId) => Changed?.Invoke(this, projectId);

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
}

/// <summary>A context that keeps what is posted to it: the test runs it when it says, like the dispatcher of the interface would.</summary>
internal sealed class QueueContext : SynchronizationContext
{
    public Queue<(SendOrPostCallback Callback, object? State)> Posted { get; } = [];

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (Posted)
        {
            Posted.Enqueue((d, state));
        }
    }

    /// <summary>Runs what was posted, on the calling thread with this context current.</summary>
    public int Pump()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var run = 0;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (Posted)
                {
                    if (!Posted.TryDequeue(out item))
                    {
                        return run;
                    }
                }

                item.Callback(item.State);
                run++;
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}

internal sealed class FakeSyncPolicy(bool allowed) : IRemoteSyncPolicy
{
    public bool PeriodicSyncAllowed(string projectId) => allowed;

    public TimeSpan Interval => TimeSpan.FromMinutes(5);
}

/// <summary>Everything a test of the section needs, composed the way the shell composes it.</summary>
internal sealed class PrWorld
{
    public FakeProvider Provider { get; } = new();

    public FakeProjector Projector { get; } = new();

    public FakeSync Sync { get; } = new();

    public FakePublication Publication { get; } = new();

    public FakeGate Gate { get; } = new();

    public RecordingOpener Opener { get; } = new();

    public FakeProviders Providers { get; }

    public FakePermission? Permission { get; }

    public PullRequestsViewModel Model { get; }

    public PrWorld(RemoteRepository? repository = null, bool withProvider = true, bool syncAllowed = true, bool withRemote = true, bool withPermission = false)
    {
        Permission = withPermission ? new FakePermission() : null;
        Providers = new FakeProviders(withProvider ? Provider : null, withRemote ? repository ?? PrData.Repository : null);
        Model = new PullRequestsViewModel(
            Providers,
            Projector,
            Sync,
            Publication,
            Gate,
            new FakeSyncPolicy(syncAllowed),
            PrData.Clock(),
            Opener,
            Permission);
    }

    /// <summary>Reads the section for the project, selects the row of the pull request and waits for its detail.</summary>
    public async Task<PullRequestRow> OpenAsync(int number = 12)
    {
        await Model.SetProjectAsync(PrData.Project);
        var row = Model.Rows.Single(r => r.Number == number);
        Model.Selected = row;
        for (var i = 0; i < 200 && Model.Detail is null; i++)
        {
            await Task.Yield();
        }

        Assert.NotNull(Model.Detail);
        return row;
    }
}
