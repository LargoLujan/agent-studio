using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// Recovery with git (docs/13 section 3.7) on the real modules: the app dies in the middle of a task while a real process
/// of that task (its verification) is running; another instance of the app opens the same project from disk and the
/// user resumes the run. The interrupted task is blocked as interrupted without spending a retry, its partial work is
/// kept as evidence and its worktree goes back to the base of the attempt before the attempt is made again; what was
/// completed is never repeated.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class RecoveryWithGitTests
{
    /// <summary>Verifies that T1's file is there; it hangs while the agent's flag file is in the worktree.</summary>
    private const string VerifyScript = """
        param([string] $Report)
        if (Test-Path -LiteralPath 'src/T2/hang.flag') {
            # The test kills this process with the app, so it writes who it is next to the worktree, never inside it.
            $pidFile = Join-Path (Split-Path -Parent (Get-Location).Path) 'verify-T2.pid'
            Set-Content -LiteralPath $pidFile -Value $PID -Encoding ascii
            Start-Sleep -Seconds 600
        }
        $ok = Test-Path -LiteralPath 'src/T1/a.txt'
        $case = if ($ok) { '<testcase name="t1" classname="verify" />' } else { '<testcase name="t1" classname="verify"><failure message="falta src/T1/a.txt" /></testcase>' }
        Set-Content -LiteralPath $Report -Value "<testsuites><testsuite name='verify'>$case</testsuite></testsuites>" -Encoding utf8
        if ($ok) { exit 0 } else { exit 1 }

        """;

    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Git(string directory, params string[] arguments) => TempGitRepo.Git(directory, arguments).Trim();

    [Fact]
    public async Task A_run_killed_mid_task_is_reopened_from_disk_and_resumed_on_a_reset_worktree_without_repeating_completed_work()
    {
        using var repo = Phase3Repos.WithScript("verify.ps1", VerifyScript);
        var paths = Phase3Paths.Beside(repo.Root);
        var person = new ScriptedApprovalPrompt();

        // The step outlives the test: only the death of the app can stop it.
        // Isolation off (ADR-0017): the script writes its PID beside the worktree and the test reads it there, which only
        // holds when the step runs in the worktree. The hang still dies with the app, as it did before isolation existed.
        var profile = new VerificationProfile("recuperación", [Phase3Repos.ScriptStep("verify", "Verificar", "verify.ps1", TimeSpan.FromMinutes(30)) with { Isolation = ProcessIsolation.None }]);
        var plan = Plan(
            Work("T1", scope: ["src/T1/"]).Verified("verify"),
            Work("T2", scope: ["src/T2/"]).Verified("verify").After(On("T1")) with { Covers = [] });

        // ---- First instance of the app: T1 completes, T2's agent finishes and its verification hangs; then the app dies.
        var claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, TimeProvider.System);
        claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.txt", "a\n")));
        claude.Script("ejecutor", "T2", 1, AgentScript.Completed(("src/T2/b.txt", "b a medias\n"), ("src/T2/hang.flag", "la verificación se cuelga\n")));
        RunId runId;
        string projectId;
        string t2Worktree;
        int pid;
        await using (var first = await Phase3App.OpenAsync(repo.Root, paths, person, ScriptedProjectAgentProviders.Over(claude, Codex()), Roster()))
        {
            projectId = first.Project.ProjectId;
            var run = await first.Orchestrator.StartAsync(await RequestAsync(first, plan, profile), Ct);
            runId = run.Id;
            await Phase3Checks.WaitUntilAsync(() => run.Snapshot().View("T2") is { Status: WorkTaskStatus.Running, WorktreePath: not null }, "T2 running in its worktree", Guard);
            t2Worktree = run.Snapshot().View("T2").WorktreePath!;
            var pidFile = Path.Combine(Path.GetDirectoryName(t2Worktree)!, "verify-T2.pid");
            var found = -1;
            await Phase3Checks.WaitUntilAsync(() => TryReadPid(pidFile, out found), "the verification of T2 to hang", Guard);
            pid = found;
            Assert.True(Phase3Checks.IsRunning(pid), $"The verification process {pid} is not running.");

            // The app closes: disposing the run kills the processes it started, and nothing is recorded as cancelled.
            await run.DisposeAsync();
            var interrupted = await run.Completion.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            Assert.Equal(RunPhase.Interrupted, interrupted.Phase);
            await Phase3Checks.WaitUntilAsync(() => !Phase3Checks.IsRunning(pid), $"the verification process {pid} to die with the app", TimeSpan.FromSeconds(30));
            Assert.Empty(await first.PayloadsAsync<RunFinishedPayload>(runId));
            Assert.Equal("T2 Pending>Running", (await first.TransitionsAsync(runId))[^1]);
        }

        // The agent's work of the interrupted attempt is still in its worktree, committed by the system.
        var t1Head = Git(repo.Root, "rev-parse", $"refs/agentstudio/{runId}/T1");
        var interruptedHead = Git(t2Worktree, "rev-parse", "HEAD");
        Assert.NotEqual(t1Head, interruptedHead);
        Assert.True(File.Exists(Path.Combine(t2Worktree, "src", "T2", "hang.flag")));

        // ---- Second instance, from disk: the run is listed as interrupted and resumed when the user asks.
        var claude2 = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, TimeProvider.System);
        var seen = new TaskCompletionSource<(string Head, bool Flag, bool Partial)>(TaskCreationOptions.RunContinuationsAsynchronously);
        claude2.Script("ejecutor", "T2", 2, AgentScript.Completed(("src/T2/b.txt", "b\n")) with
        {
            OnStart = request => seen.TrySetResult((
                Git(request.WorkingDirectory, "rev-parse", "HEAD"),
                File.Exists(Path.Combine(request.WorkingDirectory, "src", "T2", "hang.flag")),
                File.Exists(Path.Combine(request.WorkingDirectory, "src", "T2", "b.txt")))),
        });
        await using var second = await Phase3App.OpenAsync(repo.Root, paths, person, ScriptedProjectAgentProviders.Over(claude2, Codex()));
        Assert.Equal(projectId, second.Project.ProjectId);
        var reopened = await second.LoadLogAsync();
        Assert.True(reopened.Verification.IsValid, reopened.Verification.Problem);
        Assert.Equal(RunPhase.Interrupted, Assert.Single(await second.Orchestrator.ListRunsAsync(second.Project, Ct)).Phase);
        Assert.Empty(claude2.Starts);

        await using var resumed = await second.Orchestrator.ResumeAsync(second.Project, runId, Ct);

        // The worktree went back to the base of the attempt (T1's commit) before the agent started again: nothing of the
        // interrupted attempt was left in it.
        var (head, flag, partial) = await seen.Task.WaitAsync(Guard, Ct);
        Assert.Equal(t1Head, head);
        Assert.False(flag, "The flag of the interrupted attempt was still in the worktree.");
        Assert.False(partial, "The partial file of the interrupted attempt was still in the worktree.");
        var result = await resumed.Completion.WaitAsync(Guard, Ct);
        var log = await Phase3Checks.DescribeAsync(second, runId);
        Assert.True(result.Phase == RunPhase.Succeeded, Phase3Checks.Describe(result) + "\n" + log);

        // T2 was blocked as interrupted and reopened, without spending a retry; T1 was never repeated.
        Assert.Equal(
            ["T2 Pending>Running", "T2 Running>Blocked", "T2 Blocked>Pending", "T2 Pending>Running", "T2 Running>Completed"],
            await second.TransitionsAsync(runId, "T2"));
        Assert.Equal(["T1 Pending>Running", "T1 Running>Completed"], await second.TransitionsAsync(runId, "T1"));
        var blocked = (await second.PayloadsAsync<TaskStatusChangedPayload>(runId)).Single(p => p.Payload.To == WorkTaskStatus.Blocked).Payload;
        Assert.Equal("Interrupted", blocked.BlockReason);
        Assert.Equal(0, resumed.Snapshot().View("T2").RetryCount);
        var launches = (await second.PayloadsAsync<AgentRunStartedPayload>(runId)).Select(p => $"{p.Payload.Task}#{p.Payload.Attempt}");
        Assert.Equal(["T1#1", "T2#1", "T2#2"], launches);
        Assert.Equal(["T1#1", "T2#1"], claude.Starts.Select(s => $"{s.Request.TaskId}#{s.Attempt}"));
        Assert.Equal(["T2#2"], claude2.Starts.Select(s => $"{s.Request.TaskId}#{s.Attempt}"));
        Assert.Contains("interrumpió", claude2.Starts.Single().Request.Prompt, StringComparison.Ordinal);

        // The partial work of the interrupted attempt is kept as evidence.
        var records = await Phase3Checks.AssertEvidenceIntactAsync(second, runId);
        var diff = Assert.Single(records, r => r.Kind == EvidenceKind.CodeDiff && r.Title.Contains("parcial", StringComparison.Ordinal));
        string diffText;
        await using (var content = await second.Evidence.OpenContentAsync(second.Project.ProjectId, diff.Id, Ct))
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            diffText = await reader.ReadToEndAsync(Ct);
        }

        Assert.Contains("src/T2/hang.flag", diffText, StringComparison.Ordinal);
        Assert.Contains("b a medias", diffText, StringComparison.Ordinal);

        // The task's commit is the redone attempt, on T1's commit, without anything of the interrupted one.
        var t2Head = Git(repo.Root, "rev-parse", $"refs/agentstudio/{runId}/T2");
        Assert.Equal(t1Head, Git(repo.Root, "rev-parse", t2Head + "^"));
        Assert.Equal(["src/T1/a.txt", "src/T2/b.txt"], Git(repo.Root, "ls-tree", "-r", "--name-only", t2Head).Split('\n').Where(p => p.StartsWith("src/T", StringComparison.Ordinal)));
        Assert.Equal("b", Git(repo.Root, "show", t2Head + ":src/T2/b.txt"));
        var t2Tests = records.Where(r => resumed.Snapshot().View("T2").Evidence.Contains(r.Id) && r.Kind == EvidenceKind.TestRun).ToList();
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 0, Skipped: 0, ExitCode: 0), Assert.Single(t2Tests).TestRun);

        Assert.DoesNotContain(await second.PayloadsAsync<AuditAlertPayload>(runId), a => a.Payload.Code == "graph.deadlock");
        var state = await second.LoadLogAsync();
        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(RunPhase.Succeeded, Assert.Single(await second.Orchestrator.ListRunsAsync(second.Project, Ct)).Phase);
    }

    private static ScriptedAgentProvider Codex() => new(ProviderId.CodexCli, VendorId.OpenAI, TimeProvider.System);

    private static async Task<RunRequest> RequestAsync(Phase3App app, PlanDefinition plan, VerificationProfile profile) => new(
        app.Project,
        null,
        "Escribe a.txt y después b.txt.",
        Requirements(),
        plan,
        new ApprovalDecision(true, "user", DateTimeOffset.UtcNow, "Plan aprobado en el lienzo.", Verification: ApprovalVerification.WindowsHello),
        profile,
        new RunOptions(),
        ValidatedRoster: await app.RosterAsync(),
        ApprovedWarning: await app.ApprovedWarningAsync(plan, profile));

    /// <summary>The process id the hanging script wrote, once it is complete.</summary>
    private static bool TryReadPid(string path, out int pid)
    {
        pid = -1;
        try
        {
            return File.Exists(path)
                && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
