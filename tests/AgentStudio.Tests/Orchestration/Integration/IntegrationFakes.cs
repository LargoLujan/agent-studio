using System.Globalization;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration.Integration;
using FakeVerdictStore = AgentStudio.Tests.Debate.Fakes.FakeVerdictStore;
using InMemoryDecisionLogStore = AgentStudio.Tests.Orchestration.Fakes.InMemoryDecisionLogStore;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>
/// An in-memory model of the user's repository that the fakes of the ports share: commits with their ancestry, refs,
/// the branch the user has checked out, and which sources conflict when merged. Commit ids are full 40-character ids.
/// </summary>
internal sealed class FakeRepository
{
    private readonly Dictionary<string, HashSet<string>> _ancestry = new(StringComparer.Ordinal);

    public FakeRepository()
    {
        Base = Commit();
        UserHead = Base;
    }

    public string Root { get; } = @"C:\repos\proyecto";

    /// <summary>The commit the run started from.</summary>
    public string Base { get; }

    public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

    /// <summary>Sources (by commit) whose merge conflicts, with the paths and the messages git would report.</summary>
    public Dictionary<string, (IReadOnlyList<string> Paths, string Messages)> Conflicts { get; } = new(StringComparer.Ordinal);

    public string? UserBranch { get; set; } = "main";

    public string UserHead { get; set; }

    public bool UserHasUncommittedChanges { get; set; }

    public int CommitCount => _ancestry.Count;

    public string Commit(params string[] parents)
    {
        var id = (_ancestry.Count + 1).ToString("x40", CultureInfo.InvariantCulture);
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { id };
        foreach (var parent in parents)
        {
            ancestors.UnionWith(_ancestry[parent]);
        }

        _ancestry[id] = ancestors;
        return id;
    }

    public bool Exists(string commit) => _ancestry.ContainsKey(commit);

    public bool IsAncestor(string ancestor, string descendant) =>
        _ancestry.TryGetValue(descendant, out var ancestors) && ancestors.Contains(ancestor);
}

/// <summary>
/// <see cref="IWorktreeManager"/> over <see cref="FakeRepository"/>, with the semantics of the real one where the
/// integration depends on them: <c>MergeIntoAsync</c> and <c>MergeIntoCandidateAsync</c> check idempotency first, then the
/// compare-and-swap (a moved target throws an <see cref="InvalidOperationException"/>, like <c>StaleRefException</c>), and
/// report a conflict without moving anything; the candidate is kept when it holds the head it is prepared at and started
/// again from it otherwise; the run's branch only moves by a compare-and-swap fast-forward; refs are created and deleted
/// as compare-and-swaps; the delivery needs an explicit critical yes and the user's HEAD where it was approved. It records
/// every call.
/// </summary>
internal sealed class FakeWorktreeManager(FakeRepository repo) : IWorktreeManager
{
    public sealed record Merge(string Target, string Expected, string Source, string Message, string? Result);

    public List<Merge> Merges { get; } = [];

    /// <summary>The candidates prepared, in order: the ref, the commit it was prepared at and the commit it was left at.</summary>
    public List<(string Ref, string Start, string Result)> Prepared { get; } = [];

    /// <summary>The fast-forwards that moved a run's branch, in order.</summary>
    public List<(string From, string To)> FastForwards { get; } = [];

    /// <summary>Called right before a fast-forward of the run's branch, for example to stop there as a crash would.</summary>
    public Action? BeforeFastForward { get; set; }

    /// <summary>
    /// The ref operations and deliveries, in order ("resolve X", "is-ancestor A B", "ensure-run-branch R", "list R",
    /// "delete X", "hook-paths", "delivery-refusals H C", "deliver C").
    /// </summary>
    public List<string> Calls { get; } = [];

    public List<(string Ref, string Commit)> CreatedRefs { get; } = [];

    public List<string> DeletedRefs { get; } = [];

    public List<ApprovedDelivery> Deliveries { get; } = [];

    /// <summary>Listed by <see cref="ListRunRefsAsync"/> although they are not refs of the run (a defensive case).</summary>
    public List<RefEntry> ExtraListedRefs { get; } = [];

    /// <summary>Called right before a delete, for example to move the ref in the meantime.</summary>
    public Action<string>? BeforeDelete { get; set; }

    /// <summary>When set, <see cref="DeliverToUserBranchAsync"/> answers with it and moves nothing.</summary>
    public UserBranchDelivery? FailedDelivery { get; set; }

    /// <summary>The paths <see cref="FindHookPathsAsync"/> reports when they are among the ones it is given.</summary>
    public IReadOnlyList<string> HookPaths { get; set; } = [];

    /// <summary>What <see cref="FindDeliveryRefusalsAsync"/> reports the delivery would refuse: nothing by default.</summary>
    public IReadOnlyList<DeliveryRefusal> DeliveryRefusals { get; set; } = [];

    public List<WorktreeRequest> Ensured { get; } = [];

    public List<(string Name, string Commit)> Checkouts { get; } = [];

    public List<(string Name, bool DiscardChanges)> Removals { get; } = [];

    public List<WorktreeLease> Owned { get; } = [];

    /// <summary>Worktree names with uncommitted changes.</summary>
    public HashSet<string> Dirty { get; } = new(StringComparer.Ordinal);

    /// <summary>Worktree names a process still uses: removing them throws <see cref="IOException"/>, as the real manager does.</summary>
    public HashSet<string> InUse { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> ChangedPaths { get; set; } = [];

    public List<(string From, string To)> ChangedPathsCalls { get; } = [];

    public int Inspections { get; private set; }

    public static WorktreeLease LeaseFor(FakeRepository repo, RunId run, string name, string baseCommit) =>
        new($@"C:\wt\{run.Value}\{name}", name, $"refs/agentstudio/{run.Value}/{name}", baseCommit, run, repo.Root, $@"{repo.Root}\.git\worktrees\{name}");

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken)
    {
        Inspections++;
        return Task.FromResult(new RepositoryState(repo.Root, repo.UserHead, repo.UserBranch, repo.UserHasUncommittedChanges, new Version(2, 45, 2)));
    }

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken)
    {
        Ensured.Add(request);
        return Task.FromResult(LeaseFor(repo, request.Run, request.Name, request.BaseCommit));
    }

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken)
    {
        if (!repo.Refs.TryGetValue(targetRef, out var current))
        {
            throw new InvalidOperationException($"The target ref '{targetRef}' does not exist.");
        }

        if (repo.IsAncestor(source, current))
        {
            Merges.Add(new Merge(targetRef, expectedTarget, source, message, current));
            return Task.FromResult(new MergeAttempt(false, true, current, [], string.Empty));
        }

        if (!string.Equals(current, expectedTarget, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Ref '{targetRef}' was expected at {expectedTarget} but is at {current}.");
        }

        if (repo.Conflicts.TryGetValue(source, out var conflict))
        {
            Merges.Add(new Merge(targetRef, expectedTarget, source, message, null));
            return Task.FromResult(new MergeAttempt(false, false, null, conflict.Paths, conflict.Messages));
        }

        var merge = repo.Commit(current, source);
        repo.Refs[targetRef] = merge;
        Merges.Add(new Merge(targetRef, expectedTarget, source, message, merge));
        return Task.FromResult(new MergeAttempt(true, false, merge, [], "Auto-merging"));
    }

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken)
    {
        Checkouts.Add((lease.Name, commit));
        return Task.CompletedTask;
    }

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken)
    {
        Removals.Add((lease.Name, discardChanges));
        if (InUse.Contains(lease.Name))
        {
            throw new IOException($"The worktree '{lease.Path}' is in use.");
        }

        if (Dirty.Contains(lease.Name) && !discardChanges)
        {
            return Task.FromResult(RemovalOutcome.RefusedDirty);
        }

        Owned.RemoveAll(l => l.Path == lease.Path);
        return Task.FromResult(RemovalOutcome.Removed);
    }

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorktreeLease>>([.. Owned]);

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
    {
        ChangedPathsCalls.Add((from, to));
        return Task.FromResult(ChangedPaths);
    }

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Integration does not capture the user's repository.");

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Integration never commits in a worktree.");

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Integration does not write diffs.");

    public Task<string?> ResolveCommitAsync(string repoRoot, string revision, CancellationToken cancellationToken)
    {
        Calls.Add("resolve " + revision);
        string? commit = IntegrationNames.IsObjectId(revision)
            ? (repo.Exists(revision) ? revision : null)
            : (repo.Refs.TryGetValue(revision, out var value) ? value : null);
        return Task.FromResult(commit);
    }

    public Task<bool> IsAncestorAsync(string repoRoot, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        Calls.Add($"is-ancestor {ancestor} {descendant}");
        return Task.FromResult(repo.IsAncestor(ancestor, descendant));
    }

    public Task<string> EnsureRunBranchAsync(string repoRoot, RunId run, string baseCommit, CancellationToken cancellationToken)
    {
        Calls.Add("ensure-run-branch " + run.Value);
        var branch = IntegrationNames.BranchRef(run);
        if (!repo.Refs.TryGetValue(branch, out var current))
        {
            current = baseCommit;
            repo.Refs[branch] = current;
            CreatedRefs.Add((branch, current));
        }

        return Task.FromResult(current);
    }

    public Task<string> PrepareCandidateAsync(string repoRoot, RunId run, TaskId task, string startCommit, CancellationToken cancellationToken)
    {
        Calls.Add("prepare-candidate " + task.Value);
        if (!repo.Exists(startCommit))
        {
            throw new InvalidOperationException($"Commit {startCommit} does not exist.");
        }

        var candidate = IntegrationNames.CandidateRef(run, task);
        if (!repo.Refs.TryGetValue(candidate, out var current) || !repo.IsAncestor(startCommit, current))
        {
            current = startCommit;
            repo.Refs[candidate] = current;
        }

        Prepared.Add((candidate, startCommit, current));
        return Task.FromResult(current);
    }

    public Task<MergeAttempt> MergeIntoCandidateAsync(string repoRoot, RunId run, TaskId task, string expectedCandidate, string source, string message, CancellationToken cancellationToken) =>
        MergeIntoAsync(repoRoot, IntegrationNames.CandidateRef(run, task), expectedCandidate, source, message, cancellationToken);

    public Task FastForwardRunBranchAsync(string repoRoot, RunId run, string expectedHead, string newHead, CancellationToken cancellationToken)
    {
        Calls.Add($"fast-forward {expectedHead} {newHead}");
        BeforeFastForward?.Invoke();
        var branch = IntegrationNames.BranchRef(run);
        var current = repo.Refs.GetValueOrDefault(branch);
        if (current == newHead)
        {
            return Task.CompletedTask;
        }

        if (!repo.IsAncestor(expectedHead, newHead))
        {
            throw new InvalidOperationException($"Refusing to move '{branch}' from {expectedHead} to {newHead}: it is not a fast-forward.");
        }

        if (current != expectedHead)
        {
            throw new InvalidOperationException($"Ref '{branch}' was expected at {expectedHead} but is at {current ?? "(missing)"}.");
        }

        repo.Refs[branch] = newHead;
        FastForwards.Add((expectedHead, newHead));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RefEntry>> ListRunRefsAsync(string repoRoot, RunId run, CancellationToken cancellationToken)
    {
        Calls.Add("list " + run.Value);
        var listed = repo.Refs
            .Where(r => r.Key == IntegrationNames.BranchRef(run) || r.Key.StartsWith(IntegrationNames.RunRefPrefix(run), StringComparison.Ordinal))
            .Select(r => new RefEntry(r.Key, r.Value))
            .Concat(ExtraListedRefs)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<RefEntry>>(listed);
    }

    public Task<bool> DeleteRefAsync(string repoRoot, string refName, string expectedCommit, CancellationToken cancellationToken)
    {
        Calls.Add("delete " + refName);
        BeforeDelete?.Invoke(refName);
        if (!repo.Refs.TryGetValue(refName, out var current) || current != expectedCommit)
        {
            return Task.FromResult(false);
        }

        repo.Refs.Remove(refName);
        DeletedRefs.Add(refName);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> FindHookPathsAsync(string repoRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add("hook-paths");
        return Task.FromResult<IReadOnlyList<string>>([.. paths.Where(p => HookPaths.Contains(p, StringComparer.Ordinal))]);
    }

    public Task<IReadOnlyList<DeliveryRefusal>> FindDeliveryRefusalsAsync(string repoRoot, string head, string commit, CancellationToken cancellationToken)
    {
        Calls.Add($"delivery-refusals {head} {commit}");
        return Task.FromResult(DeliveryRefusals);
    }

    public Task<UserBranchDelivery> DeliverToUserBranchAsync(ApprovedDelivery delivery, CancellationToken cancellationToken)
    {
        Calls.Add("deliver " + delivery.Commit);
        Deliveries.Add(delivery);
        if (delivery.Request is not { Risk: RiskLevel.Critical } || delivery.Decision is not { Approved: true } || string.IsNullOrWhiteSpace(delivery.Decision.DecidedBy))
        {
            throw new InvalidOperationException("The delivery was not approved.");
        }

        // As the real one: the approval was asked for exactly this delivery, and the yes answers that request.
        if (!DeliveryApproval.Matches(delivery.Request.Binding, delivery) || delivery.Decision.Answers != delivery.Request.Binding)
        {
            throw new InvalidOperationException("The approval is not the one given to this delivery.");
        }

        if (FailedDelivery is not null)
        {
            return Task.FromResult(FailedDelivery);
        }

        if (delivery.TargetBranch != repo.UserBranch || delivery.ExpectedHead != repo.UserHead)
        {
            return Task.FromResult(new UserBranchDelivery(UserBranchDeliveryStatus.RepositoryChanged, repo.UserHead, [], "HEAD moved."));
        }

        if (delivery.Mode == DeliveryMode.FastForwardOnly)
        {
            if (!repo.IsAncestor(repo.UserHead, delivery.Commit))
            {
                return Task.FromResult(new UserBranchDelivery(UserBranchDeliveryStatus.GitFailed, repo.UserHead, [], "fatal: Not possible to fast-forward, aborting."));
            }

            repo.UserHead = delivery.Commit;
        }
        else
        {
            repo.UserHead = repo.Commit(repo.UserHead, delivery.Commit);
        }

        return Task.FromResult(new UserBranchDelivery(UserBranchDeliveryStatus.Delivered, repo.UserHead, [], "ok"));
    }
}

/// <summary>
/// An <see cref="IApprovalGate"/> that records every request and answers as told. Like the real gate, its decision
/// answers the request's binding, unless <see cref="Answers"/> says otherwise.
/// </summary>
internal sealed class RecordingApprovalGate : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];

    public List<ApprovalDecision> Decisions { get; } = [];

    public bool Approve { get; set; } = true;

    /// <summary>When set, what the decision says it answers instead of the request's binding (a gate that misbehaves).</summary>
    public Func<ApprovalRequest, string?>? Answers { get; set; }

    /// <summary>Runs while the request is "pending", for example to change the repository in the meantime.</summary>
    public Action<ApprovalRequest>? WhilePending { get; set; }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        WhilePending?.Invoke(request);
        var decision = new ApprovalDecision(Approve, "usuario", DateTimeOffset.UtcNow, Approve ? null : "todavía no", Answers is null ? request.Binding : Answers(request));
        Decisions.Add(decision);
        return Task.FromResult(decision);
    }
}

/// <summary>
/// The decision log (in memory, under the real <see cref="DecisionRecorder"/>) and the verdict store of a project: what
/// <see cref="DeliveryService"/> reads to know whether a run may be delivered. The helpers write what a run leaves there,
/// as the engine (integration, end of the run) and the independent review (stored verdict, <c>VerdictIssued</c>) do.
/// </summary>
internal sealed class DeliveryRecords
{
    /// <summary>The review task of the verdicts these records keep, unless a test names another.</summary>
    public const string ReviewTask = "T3";

    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    /// <summary>The last clean integration of each run so far: where the engine starts a review that waits for it.</summary>
    private readonly Dictionary<string, string> _lastIntegration = new(StringComparer.Ordinal);

    /// <summary>The reviews whose worktree is already recorded: the engine records it once, the first time.</summary>
    private readonly HashSet<(string Run, string Review)> _reviewWorktrees = [];

    public DeliveryRecords(string projectId)
    {
        ProjectId = projectId;
        Recorder = new DecisionRecorder(Log, TimeProvider.System);
    }

    public string ProjectId { get; }

    public InMemoryDecisionLogStore Log { get; } = new();

    public FakeVerdictStore Verdicts { get; } = new();

    public DecisionRecorder Recorder { get; }

    public IReadOnlyList<DecisionEntry> Entries => Log.Entries(ProjectId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a run that went well leaves: its integration, a valid approved verdict of it, and <c>RunFinished(Succeeded)</c>.</summary>
    public async Task<string> SucceededAsync(RunId run, string integrationCommit)
    {
        await IntegratedAsync(run, integrationCommit);
        var verdict = await VerdictAsync(run);
        await FinishedAsync(run, RunPhase.Succeeded, integrationCommit, verdict);
        return verdict;
    }

    /// <summary>The engine's record of an integration into the run's branch (never a delivery).</summary>
    public async Task IntegratedAsync(RunId run, string? commit, IReadOnlyList<string>? conflicts = null)
    {
        await RecordAsync(
            DecisionKind.IntegrationPerformed,
            new IntegrationPerformedPayload(new TaskId("T2"), IntegrationNames.BranchName(run), [$"refs/agentstudio/{run.Value}/T1"], commit, conflicts ?? [], false) { RunId = run.Value });
        if (commit is not null && (conflicts is null || conflicts.Count == 0))
        {
            _lastIntegration[run.Value] = commit;
        }
    }

    /// <summary>
    /// The engine's record of the worktree of a review (<c>WorktreeCreated</c> of its task), started on
    /// <paramref name="baseCommit"/>: the code its judge works on. Recorded once per review, as the engine does.
    /// </summary>
    public async Task ReviewWorktreeAsync(RunId run, string baseCommit, string review = ReviewTask)
    {
        _reviewWorktrees.Add((run.Value, review));
        await RecordAsync(
            DecisionKind.WorktreeCreated,
            new WorktreeCreatedPayload(new TaskId(review), review, $@"C:\wt\{run.Value}\{review}", $"refs/agentstudio/{run.Value}/{review}", baseCommit) { RunId = run.Value });
    }

    /// <summary>
    /// A verdict kept as the review keeps it: the worktree of the review first, unless it is already recorded (on
    /// <paramref name="reviewBase"/>, or else on the run's last clean integration so far, as the engine starts a review that
    /// waits for it; none without either), then stored and announced with <c>VerdictIssued</c>, echoing the head of the log
    /// its dossier was built on (<paramref name="head"/>; the head of the log now when not given, as a review that has just
    /// been shown the run).
    /// </summary>
    public async Task<string> VerdictAsync(RunId run, VerdictDecision decision = VerdictDecision.Approved, bool valid = true, string? supersedes = null, string? head = null, string review = ReviewTask, string? reviewBase = null)
    {
        if (!_reviewWorktrees.Contains((run.Value, review)) && (reviewBase ?? _lastIntegration.GetValueOrDefault(run.Value)) is { } start)
        {
            await ReviewWorktreeAsync(run, start, review);
        }

        var id = await StoreVerdictAsync(run, decision, valid, supersedes, review: review);
        await AnnounceVerdictAsync(run, id, decision, valid, supersedes, head, review);
        return id;
    }

    /// <summary>The head of the log now: what a dossier built at this moment would name.</summary>
    public string Head => Entries.Count == 0 ? DecisionLog.GenesisHash : Entries[^1].Hash;

    public async Task<string> StoreVerdictAsync(RunId run, VerdictDecision decision, bool valid, string? supersedes = null, string? id = null, string review = ReviewTask)
    {
        var verdictId = id ?? Guid.NewGuid().ToString("N");
        await Verdicts.AppendAsync(ProjectId, new StoredVerdict(verdictId, Verdict(decision), run.Value, review, AuditScope.CurrentPlan, valid, [], supersedes), Ct);
        return verdictId;
    }

    public Task AnnounceVerdictAsync(RunId run, string id, VerdictDecision decision, bool valid, string? supersedes = null, string? head = null, string review = ReviewTask) =>
        RecordAsync(
            DecisionKind.VerdictIssued,
            new VerdictIssuedPayload(id, new TaskId(review), decision, valid, [], "hash-de-requisitos", head ?? Head, supersedes, null) { RunId = run.Value },
            new Actor(ActorKind.Judge, "juez"));

    public Task FinishedAsync(RunId run, RunPhase phase, string? integrationCommit, string? verdictId) =>
        RecordAsync(DecisionKind.RunFinished, new RunFinishedPayload(phase, "Fin de la ejecución.", integrationCommit, verdictId) { RunId = run.Value });

    public async Task RecordAsync(DecisionKind kind, DecisionPayload payload, Actor? actor = null) =>
        await Recorder.RecordAsync(ProjectId, actor ?? SystemActor, kind, kind.ToString(), payload, cancellationToken: Ct);

    /// <summary>The payloads of one kind in the log, in order.</summary>
    public IReadOnlyList<T> Payloads<T>()
        where T : DecisionPayload =>
        [.. Entries.Select(e => DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)).OfType<T>()];

    public static Verdict Verdict(VerdictDecision decision) => new(
        decision,
        "Revisado.",
        [],
        [],
        [],
        [],
        [],
        new Confidence(ConfidenceLevel.High, "Pruebas del sistema en verde."),
        [],
        RequiresHumanIntervention: decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved,
        new JudgeIdentity(new AgentId("juez"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI)),
        "hash-de-requisitos",
        "cabeza-del-registro",
        DateTimeOffset.UnixEpoch);
}

/// <summary>An <see cref="IVerificationRunner"/> that runs nothing: every step passes unless told otherwise.</summary>
internal sealed class FakeVerificationRunner : IVerificationRunner
{
    public List<(string WorkingDirectory, IReadOnlyList<VerificationStep> Steps, EvidenceScope Scope)> Runs { get; } = [];

    public Func<IReadOnlyList<VerificationStep>, IReadOnlyList<VerificationResult>>? Results { get; set; }

    /// <summary>Called when a verification starts, with its scope: for example to look at the run's branch meanwhile, or to move it.</summary>
    public Action<EvidenceScope>? OnRun { get; set; }

    public static VerificationResult Pass(VerificationStep step) => Result(step, succeeded: true);

    public static VerificationResult Fail(VerificationStep step) => Result(step, succeeded: false);

    public Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        Runs.Add((workingDirectory, steps, scope));
        OnRun?.Invoke(scope);
        return Task.FromResult(Results?.Invoke(steps) ?? steps.Select(Pass).ToList());
    }

    private static VerificationResult Result(VerificationStep step, bool succeeded)
    {
        var summary = step.Kind == EvidenceKind.TestRun ? new TestRunSummary(succeeded ? 1 : 0, succeeded ? 0 : 1, 0, succeeded ? 0 : 2) : null;
        var evidence = new EvidenceRecord(
            new EvidenceId("verificacion-" + step.Id),
            step.Kind,
            EvidenceOrigin.CapturedBySystem,
            step.Title,
            "agent-studio",
            DateTimeOffset.UtcNow,
            new string('a', 64),
            summary);
        return new VerificationResult(step.Id, evidence, succeeded, false, succeeded ? 0 : 1);
    }
}
