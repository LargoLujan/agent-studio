using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>
/// The real <see cref="Integrator"/> over the real <see cref="GitWorktreeManager"/> (its only git path) and
/// <see cref="VerificationRunner"/> on a temporary repository: tasks in their own worktrees, then their integration. The
/// verification is a <c>verify.ps1</c> of the repository that writes a JUnit report, so no NuGet is needed. The run's
/// branch only ever points at a verified integration (ADR-0012): the merges go to the candidate of the integration task,
/// and the branch is fast-forwarded to it only when every step passed.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class IntegratorLocalToolsTests : IDisposable
{
    private const string ProjectId = "integrador-local";

    private const string VerifyScript = """
        param([string] $Report)
        $ok = (Test-Path -LiteralPath 'src/a.txt') -and (Test-Path -LiteralPath 'src/b.txt')
        $case = if ($ok) { '<testcase name="ambos" />' } else { '<testcase name="ambos"><failure message="falta un fichero" /></testcase>' }
        Set-Content -LiteralPath $Report -Value "<testsuites><testsuite name='verify'>$case</testsuite></testsuites>" -Encoding utf8
        if ($ok) { exit 0 } else { exit 1 }

        """;

    private static readonly VerificationStep Verify = new(
        "verify",
        "Comprobar la integración",
        "pwsh",
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "verify.ps1", "{results}/verify.xml"],
        EvidenceKind.TestRun,
        TestReportFormat.JUnit,
        "{results}/verify.xml",
        TimeSpan.FromMinutes(2));

    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "as-integration-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly InMemoryEvidenceStore _store = new();
    private readonly GitWorktreeManager _worktrees;
    private readonly string _base;

    public IntegratorLocalToolsTests()
    {
        _worktrees = NewWorktreeManager();

        // The verification script is part of the repository, committed by the user before the run starts.
        _repo.Write("verify.ps1", VerifyScript.ReplaceLineEndings("\n"));
        _repo.Run("add", "verify.ps1");
        _repo.Run("commit", "-q", "-m", "verificación");
        _base = _repo.Head;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new(ProjectId, _repo.Root);

    private GitWorktreeManager NewWorktreeManager() => new(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));

    // The verify step is isolated by default (ADR-0017): the runner gets the real Low work area factory and the git layer,
    // so the script runs on a copy of the candidate commit taken from the repository, never in the _integration worktree.
    private VerificationRunner NewVerification() =>
        new(new StreamingProcessRunner(), _store, new VerificationRunnerOptions(_scratch), isolation: new LowIntegrityWorkAreaFactory(), git: _worktrees);

    private Integrator Integrator(IWorktreeManager? worktrees = null, IVerificationRunner? verification = null) =>
        new(worktrees ?? _worktrees, verification ?? NewVerification(), _store);

    /// <summary>
    /// What an agent with a shell can do: write a hook into the repository the worktrees share. Git would run it, with
    /// the rights of Agent Studio, on the next ref update. It writes <paramref name="marker"/> if it ever runs.
    /// </summary>
    private void PlantHook(string marker)
    {
        var hooks = Path.Combine(_repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "pre-commit", "post-commit", "post-merge" })
        {
            File.WriteAllText(Path.Combine(hooks, hook), $"#!/bin/sh\necho {hook} >> '{marker.Replace('\\', '/')}'\n");
        }
    }

    private string Git(params string[] arguments) => TempGitRepo.Git(_repo.Root, arguments).Trim();

    private string Branch(RunId run) => Git("rev-parse", IntegrationNames.BranchRef(run));

    private string Candidate(RunId run, string task = "T9") => Git("rev-parse", IntegrationNames.CandidateRef(run, new TaskId(task)));

    /// <summary>A task worktree on the base with one file written and committed by the system, as the engine does.</summary>
    private Task<(IntegrationSource Source, string Commit, WorktreeLease Lease)> TaskAsync(RunId run, string task, string file, string content) =>
        TaskAsync(run, task, _base, lease =>
        {
            var path = Path.Combine(lease.Path, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        });

    /// <summary>A task worktree on <paramref name="baseCommit"/>, changed as <paramref name="change"/> says and committed by the system.</summary>
    private async Task<(IntegrationSource Source, string Commit, WorktreeLease Lease)> TaskAsync(RunId run, string task, string baseCommit, Action<WorktreeLease> change)
    {
        var lease = await _worktrees.EnsureAsync(new WorktreeRequest(Project, run, task, baseCommit), Ct);
        change(lease);
        var commit = await _worktrees.CommitAllAsync(lease, $"{task}: cambios", Ct);
        Assert.True(commit.Committed);
        return (new IntegrationSource(new TaskId(task), lease.RefName), commit.Commit, lease);
    }

    private IntegrationRequest Request(RunId run, params IntegrationSource[] sources) => Request(run, "T9", sources);

    private IntegrationRequest Request(RunId run, string task, params IntegrationSource[] sources) =>
        new(Project, run, new TaskId(task), 1, _base, sources, [Verify]);

    private string[] Parents(string commit) => Git("rev-list", "--parents", "-n", "1", commit).Split(' ')[1..];

    [Fact]
    public async Task Two_task_refs_merge_cleanly_in_a_deterministic_order_into_the_candidate_and_the_run_branch_moves_to_it_once_verified()
    {
        var run = new RunId("20261016-b0b0b0");
        var before = _repo.Snapshot();
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var t2 = await TaskAsync(run, "T2", "src/b.txt", "b\n");
        var marker = Path.Combine(_scratch, "hook-ran.txt");
        Directory.CreateDirectory(_scratch);
        PlantHook(marker);
        string? branchWhileVerifying = null;
        var watched = new WatchedVerification(NewVerification()) { Before = _ => branchWhileVerifying = Branch(run) };

        var result = await Integrator(verification: watched).IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct);

        Assert.False(File.Exists(marker), "A hook planted in the repository ran during the integration: " + (File.Exists(marker) ? File.ReadAllText(marker) : string.Empty));

        var verification = Assert.Single(result.Verification);
        Assert.True(result.Succeeded, _store.Text(verification.Evidence.Id));
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 0, Skipped: 0, ExitCode: 0), verification.Evidence.TestRun);
        await using (var content = await _store.OpenContentAsync(ProjectId, verification.Evidence.Id, Ct))
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, Ct);
            Assert.Equal(verification.Evidence.ContentSha256, ContentHash.Sha256Hex(copy.ToArray()));
        }

        // The candidate is the chain base -> merge(T1) -> merge(T2), in the order given.
        var head = result.ResultCommit!;
        Assert.Equal(head, Candidate(run));
        var last = Parents(head);
        Assert.Equal(t2.Commit, last[1]);
        Assert.Equal([_base, t1.Commit], Parents(last[0]));
        var files = Git("ls-tree", "-r", "--name-only", head).Split('\n');
        Assert.Contains("src/a.txt", files);
        Assert.Contains("src/b.txt", files);

        // The run's branch stayed at the base while the candidate was verified and moved to it afterwards: the head of the
        // branch, the commit the tests verified and the result of the integration are the same commit.
        Assert.Equal(_base, branchWhileVerifying);
        Assert.Equal(head, Branch(run));
        Assert.Equal(head, verification.Evidence.SubjectCommit);

        var integration = (await _worktrees.ListOwnedAsync(Project, Ct)).Single(l => l.Run == run && l.Name == IntegrationNames.IntegrationWorktree);
        Assert.Equal(head, TempGitRepo.Git(integration.Path, "rev-parse", "HEAD").Trim());

        // Integrating again is a no-op: same commit, nothing new on the branch.
        var again = await Integrator().IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct);
        Assert.True(again.Succeeded);
        Assert.Equal(head, again.ResultCommit);
        Assert.Equal(head, Branch(run));

        // The user's repository did not change: status, HEAD, branches, tags and index.
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_conflict_leaves_the_run_branch_where_it_was_the_clean_merges_in_the_candidate_and_nothing_half_merged()
    {
        var run = new RunId("20261016-c0c0c0");
        var before = _repo.Snapshot();
        var shared = File.ReadAllText(Path.Combine(_repo.Root, "shared.txt"));
        var t1 = await TaskAsync(run, "T1", "shared.txt", shared.Replace("linea 1\n", "linea 1 (T1)\n", StringComparison.Ordinal));
        var t2 = await TaskAsync(run, "T2", "shared.txt", shared.Replace("linea 1\n", "linea 1 (T2)\n", StringComparison.Ordinal));

        var result = await Integrator().IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(["shared.txt"], result.ConflictPaths);
        Assert.Empty(result.Verification);

        // The candidate holds the clean merge of T1 and nothing of T2, and no commit with conflict markers exists. Nothing was
        // verified, so the run's branch is still at the base.
        var head = result.ResultCommit!;
        Assert.Equal(head, Candidate(run));
        Assert.Equal(_base, Branch(run));
        Assert.Equal([_base, t1.Commit], Parents(head));
        Assert.Equal(1, TempGitRepo.TryGit(_repo.Root, "merge-base", "--is-ancestor", t2.Commit, head).ExitCode);
        Assert.DoesNotContain("<<<<<<<", Git("show", head + ":shared.txt"), StringComparison.Ordinal);

        // Nothing was checked out for the integration, and the worktrees of the tasks are as the system left them.
        Assert.DoesNotContain(await _worktrees.ListOwnedAsync(Project, Ct), l => l.Run == run && l.Name == IntegrationNames.IntegrationWorktree);
        Assert.Equal(string.Empty, TempGitRepo.Git(t1.Lease.Path, "status", "--porcelain"));
        Assert.Equal(string.Empty, TempGitRepo.Git(t2.Lease.Path, "status", "--porcelain"));

        var reportId = Assert.Single(result.Evidence);
        Assert.Equal(EvidenceKind.ConflictReport, _store.Single(reportId).Record.Kind);
        var report = _store.Text(reportId);
        Assert.Contains("CONFLICT", report, StringComparison.Ordinal);
        Assert.Contains("\nshared.txt\n", report, StringComparison.Ordinal);

        // The same conflict again, and still nothing moves.
        var again = await Integrator().IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct);
        Assert.False(again.Succeeded);
        Assert.Equal(head, again.ResultCommit);
        Assert.Equal(head, Candidate(run));
        Assert.Equal(_base, Branch(run));
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_failing_verification_leaves_the_run_branch_at_its_previous_head_and_keeps_the_rejected_candidate()
    {
        var run = new RunId("20261016-d0d0d0");
        var before = _repo.Snapshot();
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var t2 = await TaskAsync(run, "T2", "src/b.txt", "b\n");
        var verified = await Integrator().IntegrateAsync(Request(run, "I1", t1.Source, t2.Source), null, Ct);
        Assert.True(verified.Succeeded);
        var previous = Branch(run);
        Assert.Equal(verified.ResultCommit, previous);

        // T3 works on the verified integration and deletes a file the verification needs.
        var t3 = await TaskAsync(run, "T3", previous, lease => File.Delete(Path.Combine(lease.Path, "src", "b.txt")));

        var result = await Integrator().IntegrateAsync(Request(run, "I2", t3.Source), null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(previous, Branch(run));
        var candidate = result.ResultCommit!;
        Assert.Equal(candidate, Candidate(run, "I2"));
        Assert.Equal([previous, t3.Commit], Parents(candidate));

        // The failed test run verified the rejected candidate, and a report says what was rejected and what was kept.
        var tests = Assert.Single(result.Verification);
        Assert.False(tests.Succeeded);
        Assert.Equal(candidate, tests.Evidence.SubjectCommit);
        Assert.Equal(1, tests.Evidence.TestRun!.Failed);
        var report = _store.Single(result.Evidence[^1]).Record;
        Assert.Equal(EvidenceKind.Document, report.Kind);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, report.Origin);
        Assert.Equal(candidate, report.SubjectCommit);
        var text = _store.Text(report.Id);
        Assert.Contains("# candidate: " + IntegrationNames.CandidateRef(run, new TaskId("I2")) + " at " + candidate, text, StringComparison.Ordinal);
        Assert.Contains("# run branch: " + IntegrationNames.BranchRef(run) + " stays at " + previous, text, StringComparison.Ordinal);
        Assert.Contains("verify: failed", text, StringComparison.Ordinal);
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_run_branch_moved_during_the_verification_is_not_fast_forwarded_and_stays_where_it_was_moved()
    {
        var run = new RunId("20261016-e1e1e1");
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var t2 = await TaskAsync(run, "T2", "src/b.txt", "b\n");

        // While the candidate is verified, something with a shell points the run's branch at a commit of its own.
        var watched = new WatchedVerification(NewVerification()) { After = _ => Git("update-ref", IntegrationNames.BranchRef(run), t1.Commit, _base) };

        var refusal = await Assert.ThrowsAsync<StaleRefException>(() => Integrator(verification: watched).IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct));

        Assert.Equal((IntegrationNames.BranchRef(run), _base, t1.Commit), (refusal.RefName, refusal.Expected, refusal.Actual));
        Assert.Equal(t1.Commit, Branch(run));
        Assert.NotEqual(Candidate(run), Branch(run));
    }

    [Fact]
    public async Task A_crash_between_the_verification_and_the_fast_forward_is_completed_on_resume_without_verifying_twice()
    {
        var run = new RunId("20261016-f1f1f1");
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var t2 = await TaskAsync(run, "T2", "src/b.txt", "b\n");
        var crashed = new WatchedVerification(NewVerification());

        // The app closes once the candidate passed its verification and before the run's branch is fast-forwarded.
        await Assert.ThrowsAsync<IOException>(() => Integrator(new CrashingBeforeFastForward(_worktrees), crashed).IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct));

        var candidate = Candidate(run);
        Assert.Equal(1, crashed.Runs);
        Assert.Equal(_base, Branch(run));
        var commits = Git("rev-list", "--count", "--all");

        // Another instance of the app resumes the run: the integration task makes its next attempt.
        var resumedVerification = new WatchedVerification(NewVerification());
        var resumed = await Integrator(NewWorktreeManager(), resumedVerification).IntegrateAsync(Request(run, t1.Source, t2.Source) with { Attempt = 2 }, null, Ct);

        Assert.True(resumed.Succeeded, string.Join("\n", resumed.Verification.Select(v => _store.Text(v.Evidence.Id))));
        Assert.Equal(candidate, resumed.ResultCommit);
        Assert.Equal(candidate, Candidate(run));
        Assert.Equal(candidate, Branch(run));
        Assert.Equal(commits, Git("rev-list", "--count", "--all"));

        // The same candidate, verified once by the resumed attempt: that is the test run the log will hold for it.
        Assert.Equal(1, resumedVerification.Runs);
        Assert.Equal(candidate, Assert.Single(resumed.Verification).Evidence.SubjectCommit);
    }

    [Fact]
    public async Task The_delivery_check_takes_exactly_the_verified_integration_the_run_branch_points_at()
    {
        var run = new RunId("20261016-a2a2a2");
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var t2 = await TaskAsync(run, "T2", "src/b.txt", "b\n");
        var result = await Integrator().IntegrateAsync(Request(run, t1.Source, t2.Source), null, Ct);
        Assert.True(result.Succeeded);

        // The log as the engine and the review leave it: the integration, a valid approval of it, and the end of the run.
        var records = new DeliveryRecords(ProjectId);
        await records.SucceededAsync(run, result.ResultCommit!);
        var gate = new RecordingApprovalGate();
        var delivery = new DeliveryService(_worktrees, gate, records.Recorder, records.Verdicts);

        var readiness = await delivery.CheckAsync(Project, run, Ct);

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(result.ResultCommit, readiness.Commit);
        Assert.Equal(readiness.Commit, Branch(run));
        Assert.Equal(readiness.Commit, Assert.Single(result.Verification).Evidence.SubjectCommit);

        var delivered = await delivery.DeliverAsync(Project, run, DeliveryMode.FastForwardOnly, Ct);

        Assert.True(delivered.Delivered, delivered.Message);
        Assert.Equal(readiness.Commit, _repo.Head);
    }

    [Fact]
    public async Task A_rejected_candidate_is_never_delivered_even_if_the_log_said_it_was_approved()
    {
        var run = new RunId("20261016-a3a3a3");

        // Without src/b.txt the verification fails.
        var t1 = await TaskAsync(run, "T1", "src/a.txt", "a\n");
        var result = await Integrator().IntegrateAsync(Request(run, t1.Source), null, Ct);
        Assert.False(result.Succeeded);

        // A log that says the rejected candidate ended the run well and a judge approved it.
        var records = new DeliveryRecords(ProjectId);
        await records.SucceededAsync(run, result.ResultCommit!);
        var gate = new RecordingApprovalGate();
        var before = _repo.Snapshot();

        var delivered = await new DeliveryService(_worktrees, gate, records.Recorder, records.Verdicts).DeliverAsync(Project, run, DeliveryMode.FastForwardOnly, Ct);

        // The run's branch never pointed at the rejected candidate: nothing is asked and nothing moves.
        Assert.False(delivered.Delivered, delivered.Message);
        Assert.Equal(_base, Branch(run));
        Assert.Empty(gate.Requests);
        Assert.Equal(_base, _repo.Head);
        Assert.Equal(before, _repo.Snapshot());
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}

/// <summary>The real verification runner, counting its runs, with a hook before and after each of them.</summary>
internal sealed class WatchedVerification(IVerificationRunner inner) : IVerificationRunner
{
    public int Runs { get; private set; }

    public Action<EvidenceScope>? Before { get; init; }

    public Action<EvidenceScope>? After { get; init; }

    public async Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        Runs++;
        Before?.Invoke(scope);
        var results = await inner.RunAsync(workingDirectory, steps, scope, output, cancellationToken);
        After?.Invoke(scope);
        return results;
    }
}

/// <summary>
/// The real worktree manager, except that the app closes when the run's branch is about to be fast-forwarded: the call
/// throws before anything moves, as a crash right after the verification leaves the repository.
/// </summary>
internal sealed class CrashingBeforeFastForward(IWorktreeManager inner) : IWorktreeManager
{
    public Task FastForwardRunBranchAsync(string repoRoot, RunId run, string expectedHead, string newHead, CancellationToken cancellationToken) =>
        throw new IOException("The app closed right after the verification.");

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) => inner.InspectAsync(repoRoot, cancellationToken);

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken) =>
        inner.CaptureUserRepositoryStateAsync(repoRoot, cancellationToken);

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken) => inner.EnsureAsync(request, cancellationToken);

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) => inner.CommitAllAsync(lease, message, cancellationToken);

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken) =>
        inner.ChangedPathsAsync(repoRoot, from, to, cancellationToken);

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) =>
        inner.WriteDiffAsync(repoRoot, from, to, destination, cancellationToken);

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) =>
        inner.MergeIntoAsync(repoRoot, targetRef, expectedTarget, source, message, cancellationToken);

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken) => inner.CheckoutAsync(lease, commit, cancellationToken);

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) =>
        inner.RemoveAsync(lease, discardChanges, cancellationToken);

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) => inner.ListOwnedAsync(project, cancellationToken);

    public Task<string?> ResolveCommitAsync(string repoRoot, string revision, CancellationToken cancellationToken) => inner.ResolveCommitAsync(repoRoot, revision, cancellationToken);

    public Task<bool> IsAncestorAsync(string repoRoot, string ancestor, string descendant, CancellationToken cancellationToken) =>
        inner.IsAncestorAsync(repoRoot, ancestor, descendant, cancellationToken);

    public Task<string> EnsureRunBranchAsync(string repoRoot, RunId run, string baseCommit, CancellationToken cancellationToken) =>
        inner.EnsureRunBranchAsync(repoRoot, run, baseCommit, cancellationToken);

    public Task<IReadOnlyList<RefEntry>> ListRunRefsAsync(string repoRoot, RunId run, CancellationToken cancellationToken) => inner.ListRunRefsAsync(repoRoot, run, cancellationToken);

    public Task<bool> DeleteRefAsync(string repoRoot, string refName, string expectedCommit, CancellationToken cancellationToken) =>
        inner.DeleteRefAsync(repoRoot, refName, expectedCommit, cancellationToken);

    public Task<IReadOnlyList<string>> FindHookPathsAsync(string repoRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        inner.FindHookPathsAsync(repoRoot, paths, cancellationToken);

    public Task<UserBranchDelivery> DeliverToUserBranchAsync(ApprovedDelivery delivery, CancellationToken cancellationToken) =>
        inner.DeliverToUserBranchAsync(delivery, cancellationToken);

    public Task<string> PrepareCandidateAsync(string repoRoot, RunId run, TaskId task, string startCommit, CancellationToken cancellationToken) =>
        inner.PrepareCandidateAsync(repoRoot, run, task, startCommit, cancellationToken);

    public Task<MergeAttempt> MergeIntoCandidateAsync(string repoRoot, RunId run, TaskId task, string expectedCandidate, string source, string message, CancellationToken cancellationToken) =>
        inner.MergeIntoCandidateAsync(repoRoot, run, task, expectedCandidate, source, message, cancellationToken);
}
