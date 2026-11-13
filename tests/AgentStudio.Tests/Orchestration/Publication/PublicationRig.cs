using System.IO;
using System.Security.Cryptography;
using AgentStudio.Domain.Common;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;
using AgentStudio.Tests.TestSupport;
using AgentStudio.Domain.Judging;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The events of a test in the order they happened, written by every double: what ran, and whether the project was taken.</summary>
internal sealed class Trace
{
    public List<string> Events { get; } = [];

    public void Add(string text)
    {
        lock (Events)
        {
            Events.Add(text);
        }
    }

    public int Count(string prefix)
    {
        lock (Events)
        {
            return Events.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}

internal sealed class RigActivity(Trace trace) : IProjectActivity
{
    private readonly Trace _trace = trace;
    private readonly Dictionary<string, ProjectActivityKind> _held = new(StringComparer.Ordinal);

    public int Takes { get; private set; }

    public IDisposable? TryEnter(string projectId, ProjectActivityKind kind)
    {
        lock (_held)
        {
            if (_held.ContainsKey(projectId))
            {
                return null;
            }

            _held[projectId] = kind;
            if (kind == ProjectActivityKind.RemoteOperation)
            {
                Takes++;
                _trace.Add("take");
            }

            return new Lease(this, projectId, kind);
        }
    }

    public ProjectActivityKind? Current(string projectId)
    {
        lock (_held)
        {
            return _held.TryGetValue(projectId, out var kind) ? kind : null;
        }
    }

    public bool IsBusy(string projectId) => Current(projectId) is not null;

    private sealed class Lease(RigActivity owner, string projectId, ProjectActivityKind kind) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._held)
            {
                owner._held.Remove(projectId);
                if (kind == ProjectActivityKind.RemoteOperation)
                {
                    owner._trace.Add("release");
                }
            }
        }
    }
}

/// <summary>The log in memory: it writes only with the project taken for a remote operation, as the mailbox must.</summary>
internal sealed class RigRecorder(RigActivity activity, Trace trace, TimeProvider time) : IDecisionRecorder
{
    public List<DecisionEntry> Entries { get; } = [];

    public bool Verifies { get; set; } = true;

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (Entries)
        {
            return Task.FromResult(new DecisionLogState([.. Entries], Verifies ? DecisionLogVerification.Valid : new DecisionLogVerification(false, 1, "broken")));
        }
    }

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => LoadAsync(projectId, cancellationToken);

    /// <summary>When set, a write waits here after it announced itself in <see cref="Entered"/>: a test holds a write of the log to see what the close does around it.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Writes { get; private set; }

    public async Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default)
    {
        if (activity.Current(projectId) != ProjectActivityKind.RemoteOperation)
        {
            throw new InvalidOperationException("Written to the log without the project taken by a remote operation.");
        }

        Writes++;
        trace.Add("record:" + kind);
        if (Hold is { } hold)
        {
            Entered.TrySetResult();
            await hold.Task;
        }

        return Add(kind, actor, summary, payload);
    }

    /// <summary>An entry that somebody else wrote (an approval of the gate, a grant of the past): it does not need the project.</summary>
    public DecisionEntry Add(DecisionKind kind, Actor actor, string summary, DecisionPayload payload, string? hash = null)
    {
        lock (Entries)
        {
            var entry = new DecisionEntry(Entries.Count + 1, time.GetUtcNow(), actor, kind, summary, DecisionPayloadSerializer.Serialize(payload), null, new string('0', 64), hash ?? new string('1', 64));
            Entries.Add(entry);
            return entry;
        }
    }

    public int Count(DecisionKind kind)
    {
        lock (Entries)
        {
            return Entries.Count(e => e.Kind == kind);
        }
    }

    public IReadOnlyList<RemoteStateObservedPayload> Observations()
    {
        lock (Entries)
        {
            return [.. Entries.Where(e => e.Kind == DecisionKind.RemoteStateObserved).Select(e => (RemoteStateObservedPayload)DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)!)];
        }
    }
}

internal sealed class RigGit(RigActivity activity, Trace trace) : IRemoteGitRunner
{
    public RemoteGitInspection Inspection { get; set; } = new(true, [], false, null);

    public RemotePushOutcome PushOutcome { get; set; } = RemotePushOutcome.Pushed;

    public string? RemoteHead { get; set; } = HostingP0Data.Head;

    /// <summary>What successive reads of a branch answer (the head of the run branch, then none for a branch that is gone); empty: <see cref="RemoteHead"/>.</summary>
    public Queue<string?> Reads { get; } = new();

    public Action? OnPush { get; set; }

    private void Held(string name)
    {
        if (activity.Current("p1") != ProjectActivityKind.RemoteOperation)
        {
            throw new InvalidOperationException($"git ran ({name}) without the project taken by a remote operation.");
        }

        trace.Add("git:" + name);
    }

    public Task<RemoteGitInspection> InspectAsync(ProjectContext project, RemoteRepository destination, CancellationToken ct)
    {
        Held("inspect");
        return Task.FromResult(Inspection);
    }

    public Task<RemoteBranchRead> ReadBranchAsync(ProjectContext project, RemoteRepository destination, RemoteBranch branch, CancellationToken ct)
    {
        Held("read");
        return Task.FromResult(new RemoteBranchRead(true, Reads.Count > 0 ? Reads.Dequeue() : RemoteHead, null));
    }

    public Task<RemotePushResult> PushRunBranchAsync(RemotePush push, CancellationToken ct)
    {
        Held("push");
        OnPush?.Invoke();
        return Task.FromResult(new RemotePushResult(PushOutcome, RemoteHead, Inspection));
    }
}

internal sealed class RigProvider(RigActivity activity, Trace trace) : IHostingProvider
{
    public Func<PullRequestRef, ObservationCursor?, HostingResult<PullRequestObservation>> Observe { get; set; } =
        (pr, _) => new HostingResult<PullRequestObservation>(HostingOutcome.NotModified);

    public HostingResult<PullRequestRef?> Found { get; set; } = new(HostingOutcome.Ok, null);

    public Queue<HostingResult<PullRequestRef?>> FoundQueue { get; } = new();

    public HostingResult<bool> Deleted { get; set; } = new(HostingOutcome.Ok, true);

    public HostingResult<bool> Closed { get; set; } = new(HostingOutcome.Ok, true);

    /// <summary>A merge in progress: it ends when this task does (the test releases it).</summary>
    public Func<CancellationToken, Task>? MergeHold { get; set; }

    public TaskCompletionSource MergeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken LastMergeToken { get; private set; }

    public HostingResult<PublishedVerdict> VerdictResult { get; set; } = new(HostingOutcome.Ok, new PublishedVerdict(true, 7));

    public (VerdictNotice Notice, string Status, string Body)? LastVerdict { get; private set; }

    public HostingCapability Capabilities { get; set; } = (HostingCapability)65535;

    public HostingResult<MergeOutcome> Merged { get; set; } = new(HostingOutcome.Ok, new MergeOutcome(true, HostingP0Data.Result, null));

    public HostingResult<PullRequestRef> Opened { get; set; } = new(HostingOutcome.Ok, HostingP0Data.PullRequest);

    public DateTimeOffset? Restored { get; private set; }

    public HostingProviderKind Kind => HostingProviderKind.GitHub;

    public DateTimeOffset? RateLimitedUntil { get; set; }

    public void RestoreRateLimit(DateTimeOffset? until) => Restored = until;

    private void Outside(string name)
    {
        if (activity.Current("p1") == ProjectActivityKind.RemoteOperation)
        {
            throw new InvalidOperationException($"The platform's CLI ran ({name}) with the project taken.");
        }

        trace.Add("gh:" + name);
    }

    public Task<HostingAvailability> ProbeAsync(CancellationToken ct)
    {
        Outside("probe");
        return Task.FromResult(new HostingAvailability(true, new Version(2, 96, 0), new HostingCapabilities(Capabilities, [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], false)));
    }

    public Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct)
    {
        Outside("account");
        return Task.FromResult(new HostingResult<RemoteLogin>(HostingOutcome.Ok, RemoteLogin.TryParse("manuel")));
    }

    public Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct)
    {
        Outside("repository");
        return Task.FromResult(new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, new RemoteRepositoryFacts(HostingP0Data.Main, [MergeMethod.Squash], true, false)));
    }

    public Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct)
    {
        Outside("rules");
        return Task.FromResult(new HostingResult<BranchRules>(HostingOutcome.Ok, new BranchRules(true, [])));
    }

    public Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct)
    {
        Outside("find");
        return Task.FromResult(FoundQueue.Count > 0 ? FoundQueue.Dequeue() : Found);
    }

    public Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct)
    {
        Outside("open");
        return Task.FromResult(Opened);
    }

    public Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct)
    {
        Outside("update");
        return Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));
    }

    public Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct)
    {
        Outside("reviewers");
        return Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));
    }

    /// <summary>A long call of the platform: it ends only when the token it is given is cancelled (no wall clock is involved).</summary>
    public Func<CancellationToken, Task>? ObserveDelay { get; set; }

    /// <summary>Completed when the provider is entered by a read of a pull request: the signal that the consumer reached the call.</summary>
    public TaskCompletionSource ObserveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct)
    {
        Outside("observe");
        ObserveEntered.TrySetResult();
        if (ObserveDelay is { } delay)
        {
            await delay(ct);
        }

        return Observe(pullRequest, cursor);
    }

    public Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string statusDescription, string commentBody, long? commentId, CancellationToken ct)
    {
        Outside("verdict");
        LastVerdict = (notice, statusDescription, commentBody);
        return Task.FromResult(VerdictResult);
    }

    public async Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct)
    {
        Outside("merge");
        LastMergeToken = ct;
        MergeEntered.TrySetResult();
        if (MergeHold is { } hold)
        {
            await hold(ct);
        }

        return Merged;
    }

    public Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct)
    {
        Outside("close");
        return Task.FromResult(Closed);
    }

    public Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct)
    {
        Outside("delete");
        return Task.FromResult(Deleted);
    }
}

internal sealed class RigProviders(RigActivity activity, Trace trace, RigProvider provider) : IHostingProviders
{
    public RemoteRepository? Identified { get; set; } = HostingP0Data.Repository;

    public IHostingProvider? For(RemoteRepository repository) => provider;

    public Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct)
    {
        if (activity.Current(project.ProjectId) != ProjectActivityKind.RemoteOperation)
        {
            throw new InvalidOperationException("The remote was read from git without the project taken.");
        }

        trace.Add("git:identify");
        return Task.FromResult(Identified);
    }
}

internal sealed class RigApprovals(RigRecorder recorder) : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];

    public bool Approve { get; set; } = true;

    public string? WrongAnswer { get; set; }

    /// <summary>What happens while the person decides (the state may move).</summary>
    public Action? WhileDeciding { get; set; }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        WhileDeciding?.Invoke();
        recorder.Add(DecisionKind.ApprovalRequested, new Actor(ActorKind.System, "gate"), "asked", new ApprovalRequestedPayload(request.Action, request.Description, RiskLevel.Critical, null, []) { RunId = "r1" });
        if (Approve)
        {
            recorder.Add(DecisionKind.ApprovalGranted, new Actor(ActorKind.User, "person"), "ok", new ApprovalAnsweredPayload(request.Action, true, "person", null, null) { RunId = "r1" });
        }

        return Task.FromResult(new ApprovalDecision(Approve, "person", DateTimeOffset.UnixEpoch, null, WrongAnswer ?? request.Binding));
    }
}

internal sealed class RigDelivery : IDeliveryService
{
    public bool Ready { get; set; } = true;

    public Task<DeliveryReadiness> CheckAsync(ProjectContext project, AgentStudio.Domain.Orchestration.RunId run, CancellationToken cancellationToken) =>
        Task.FromResult(new DeliveryReadiness(Ready, Ready ? HostingP0Data.Head : null, Ready ? "ok" : "not approved"));

    public Task<DeliveryResult> DeliverAsync(ProjectContext project, AgentStudio.Domain.Orchestration.RunId run, DeliveryMode mode, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class RigVerdicts : IVerdictStore
{
    private readonly List<StoredVerdict> _verdicts = [];

    public Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken)
    {
        _verdicts.Add(verdict);
        return Task.FromResult(verdict.Id);
    }

    public Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredVerdict>>([.. _verdicts.Where(v => runId is null || v.RunId == runId)]);
}

internal sealed class RigHistory(RigActivity activity) : IRepositoryHistoryReader
{
    public IReadOnlyList<CommitNode> Graph { get; set; } = [new CommitNode(new string('a', 40), [], new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), "subject", "author", [])];

    public bool Fails { get; set; }

    public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken)
    {
        if (activity.Current(project.ProjectId) != ProjectActivityKind.RemoteOperation)
        {
            throw new InvalidOperationException("The history was read without the project taken.");
        }

        return Fails ? throw new InvalidOperationException("unreadable") : Task.FromResult(Graph);
    }
}

internal sealed class RigAnnotator : IRepositoryAnnotator
{
    public HashSet<string> OfTheRun { get; } = [];

    public Task<IReadOnlyList<AnnotatedCommit>> AnnotateAsync(ProjectContext project, IReadOnlyList<CommitNode> commits, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AnnotatedCommit>>([.. commits.Select(c => new AnnotatedCommit(c, OfTheRun.Contains(c.Id) ? new CommitAnnotation(AgentStudio.Tests.Domain.Hosting.HostingP0Data.Run.Value, null, null, null, null, [], []) : null))]);
}

internal sealed class RigEvidence : IEvidenceStore
{
    public async Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        return new EvidenceRecord(new EvidenceId("ev-1"), draft.Kind, draft.Origin, draft.Title, draft.ProducedBy, DateTimeOffset.UnixEpoch, Convert.ToHexStringLower(SHA256.HashData(memory.ToArray())));
    }

    public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EvidenceRecord>>([]);

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EvidenceRecord>>([]);
}

internal sealed class RigStore : IRemoteSyncStore
{
    public RemoteSyncState State { get; set; } = RemoteSyncState.Empty;

    public int Saves { get; private set; }

    public Task<RemoteSyncState> LoadAsync(string projectId, CancellationToken ct) => Task.FromResult(State);

    public Task SaveAsync(string projectId, RemoteSyncState state, CancellationToken ct)
    {
        State = state;
        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed class RigPolicy : IRemoteSyncPolicy
{
    public bool Allowed { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public bool PeriodicSyncAllowed(string projectId) => Allowed;
}

internal sealed class RigTexts : IUserTextRenderer
{
    public string Render(UserText text, string language) => text.Code + ":" + string.Join(",", text.Arguments);
}

/// <summary>Everything of the publication wired to doubles and a clock the test moves: no wall clock, no process, no network.</summary>
internal sealed class PublicationRig : IAsyncDisposable
{
    public static readonly ProjectContext Project = new("p1", @"C:\work\calc");

    public PublicationRig(RemoteSyncState? state = null, bool policy = true)
    {
        Time = new RemoteWaitClock();
        Activity = new RigActivity(Trace);
        Log = new RigRecorder(Activity, Trace, Time);
        Git = new RigGit(Activity, Trace);
        History = new RigHistory(Activity);
        Provider = new RigProvider(Activity, Trace);
        Providers = new RigProviders(Activity, Trace, Provider);
        Approvals = new RigApprovals(Log);
        Store = new RigStore { State = state ?? RemoteSyncState.Empty };
        Policy = new RigPolicy();
        Sync = new RemoteSyncService(Log, Activity, Providers, Store, policy ? Policy : null, Time);
        Gate = new HostingPolicyGate(Log, Approvals, Delivery, Verdicts, Providers, Git, Sync);
        var texts = new RigTexts();
        Service = new PublicationService(
            Gate, Sync, Delivery, Providers, Git, Log, new RigEvidence(),
            new PullRequestTextComposer(texts, null, Verdicts, new RigEvidence()),
            new VerdictNoticeBuilder(Log, Verdicts, texts, null), History, Annotator);
    }

    public Trace Trace { get; } = new();

    public RigVerdicts Verdicts { get; } = new();

    public RigHistory History { get; }

    public RigAnnotator Annotator { get; } = new();

    public const string IntegrationHash = "abababababababababababababababababababababababababababababababab";

    /// <summary>A judged run: the integration the judge was shown (its hash is the head of the verdict) and a valid verdict, stored and announced in the log, as the engine writes them.</summary>
    public async Task JudgeAsync(AgentStudio.Domain.Judging.VerdictDecision decision = AgentStudio.Domain.Judging.VerdictDecision.Approved)
    {
        var run = HostingP0Data.Run.Value;
        Log.Add(DecisionKind.IntegrationPerformed, new Actor(ActorKind.System, "agent-studio"), "integration", new IntegrationPerformedPayload(null, "agentstudio/run-" + run, [], HostingP0Data.Head, [], false) { RunId = run }, IntegrationHash);
        var verdict = AgentStudio.Tests.Domain.TestData.ValidVerdict(AgentStudio.Tests.Domain.TestData.Dossier(), decision) with { DecisionLogHeadHash = IntegrationHash };
        await Verdicts.AppendAsync("p1", new StoredVerdict("v1", verdict, run, "T7", AgentStudio.Application.Judging.AuditScope.CurrentPlan, true, [], null), CancellationToken.None);
        Log.Add(DecisionKind.VerdictIssued, new Actor(ActorKind.System, "agent-studio"), "verdict", new VerdictIssuedPayload("v1", null, decision, true, [], verdict.RequirementSetHash, IntegrationHash, null, null) { RunId = run });
    }

    /// <summary>The observation of the pull request the log already holds (what a merge, a close or a new verdict starts from).</summary>
    public void Observed(RemoteSnapshot snapshot) =>
        Log.Add(DecisionKind.RemoteStateObserved, new Actor(ActorKind.System, "remote-sync"), "observed", RemoteStateObservedPayload.Observing(snapshot, HostingP0Data.Run.Value, null, [RemoteChangeKind.Opened]));

    /// <summary>The clock of the tests: a fake that counts the timers asked of it, so a test knows that the code under test is waiting on one before it moves the time.</summary>
    public RemoteWaitClock Time { get; }

    public RigActivity Activity { get; }

    public RigRecorder Log { get; }

    public RigGit Git { get; }

    public RigProvider Provider { get; }

    public RigProviders Providers { get; }

    public RigApprovals Approvals { get; }

    public RigDelivery Delivery { get; } = new();

    public RigStore Store { get; }

    public RigPolicy Policy { get; }

    public RemoteSyncService Sync { get; }

    public HostingPolicyGate Gate { get; }

    public PublicationService Service { get; }

    public async Task<ProjectRemoteMailbox> BoxAsync() => await Sync.OfAsync(Project, CancellationToken.None);

    /// <summary>Waits until the mailbox has nothing queued, then moves the clock: what a test does to let a timer fire and its work end.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        // The clock moves in steps, and after each one the mailbox is waited for until it is idle: what the consumer does in answer to a timer happens at the time of that step, never at some
        // later time of a clock that this thread went on moving. A single big jump would make the courtesy second and the retries depend on how fast the consumer thread runs.
        var box = await BoxAsync();
        await box.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));
        var step = by <= TimeSpan.FromMinutes(5) ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMinutes(5);
        for (var left = by; left > TimeSpan.Zero; left -= step)
        {
            Time.Advance(left < step ? left : step);
            await box.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));
        }
    }

    public async Task IdleAsync() => await (await BoxAsync()).WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));

    public ValueTask DisposeAsync() => Sync.DisposeAsync();

    public static HostingOperation Operation(HostingOperationKind kind, PullRequestRef? pullRequest = null, MergeMethod? method = null, RemoteBranch? baseBranch = null) =>
        new(Project, HostingP0Data.Run, kind, HostingP0Data.Repository, baseBranch, pullRequest, kind is HostingOperationKind.PublishVerdictStatus or HostingOperationKind.PublishVerdictComment ? null : HostingP0Data.Head, method);

    public static PullRequestObservation Observation(RemoteSnapshot snapshot) =>
        new(snapshot, new ObservationCursor(new Dictionary<ObservedResource, string>(), snapshot), RemoteDisplayTexts.None);
}
