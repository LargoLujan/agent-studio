using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// F3-2 complete (docs/13 sections 5 and 8) with every module real but the CLIs: the engine, the SQLite stores, the
/// <c>GitWorktreeManager</c>, the <c>VerificationRunner</c> (<c>dotnet build</c> and <c>dotnet test</c> on a tiny xUnit
/// v3 solution), the <c>Integrator</c>, the approval gate (with a scripted person) and the provider pipeline with the
/// project's real Markdown journal. Two tasks work at the same time in their own worktrees on the same base; the system
/// commits their work, merges it in the deterministic order and builds and tests the result on the integration branch.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ParallelWorktreesIntegrationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(8);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FrozenRequirementSet Requirements() => FrozenRequirementSet.Freeze(
        [
            new Requirement(
                new RequirementId("RQ-01"),
                "Añadir Calc.Multiply(a,b)",
                RequirementPriority.Mandatory,
                [new AcceptanceCriterion("c1", "Multiply(2,3) devuelve 6", VerificationMethod.AutomatedTest)]),
            new Requirement(
                new RequirementId("RQ-02"),
                "Añadir Calc.Subtract(a,b)",
                RequirementPriority.Mandatory,
                [new AcceptanceCriterion("c2", "Subtract(7,4) devuelve 3", VerificationMethod.AutomatedTest)]),
        ],
        Start);

    [Fact]
    public async Task Two_parallel_tasks_are_merged_in_order_then_built_and_tested_on_the_integration_branch_and_the_users_repository_is_untouched()
    {
        using var repo = Phase3Repos.DotNet();
        var paths = Phase3Paths.Beside(repo.Root);
        var baseCommit = repo.Head;
        var claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, TimeProvider.System, maxConcurrentRuns: 2);
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, TimeProvider.System);
        var t1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var multiply = Phase3Repos.Operation("Multiply", '*');
        var subtract = Phase3Repos.Operation("Subtract", '-');
        claude.Script("ejecutor", "T1", 1, AgentScript.Completed(
            ("src/Calc/Multiply.cs", multiply),
            ("tests/Calc.Tests/MultiplyTests.cs", Phase3Repos.OperationTest("Multiply", 2, 3, 6))) with { Started = t1Started, Gate = together });
        claude.Script("ejecutor2", "T2", 1, AgentScript.Completed(
            ("src/Calc/Subtract.cs", subtract),
            ("tests/Calc.Tests/SubtractTests.cs", Phase3Repos.OperationTest("Subtract", 7, 4, 3))) with { Started = t2Started, Gate = together });
        var person = new ScriptedApprovalPrompt();
        var plan = Plan(
            Work("T1", "ejecutor", scope: ["src/Calc/Multiply.cs", "tests/Calc.Tests/MultiplyTests.cs"]).Covering("RQ-01"),
            Work("T2", "ejecutor2", scope: ["src/Calc/Subtract.cs", "tests/Calc.Tests/SubtractTests.cs"]).Covering("RQ-02"),
            Node("I1", WorkTaskKind.Integration).After(On("T1"), On("T2")) with { MaxRetries = 0 });

        RunId runId;
        string headHash;
        int entryCount;
        await using (var app = await Phase3App.OpenAsync(repo.Root, paths, person, ScriptedProjectAgentProviders.Over(claude, codex), Roster()))
        {
            // What Agent Studio must leave as it was, taken once the project is open (opening writes .agentstudio/ and info/exclude).
            var before = await app.Worktrees.CaptureUserRepositoryStateAsync(repo.Root, Ct);
            var snapshot = repo.Snapshot();
            var profile = DotNetVerificationProfile.Propose();
            var request = new RunRequest(
                app.Project,
                null,
                "Añade Calc.Multiply(a,b) y Calc.Subtract(a,b), cada una con su prueba.",
                Requirements(),
                plan,
                new ApprovalDecision(true, "user", DateTimeOffset.UtcNow, "Plan aprobado en el lienzo.", Verification: ApprovalVerification.WindowsHello),
                profile,
                new RunOptions(),
                ValidatedRoster: await app.RosterAsync(),
                ApprovedWarning: await app.ApprovedWarningAsync(plan, profile));

            await using var run = await app.Orchestrator.StartAsync(request, Ct);
            runId = run.Id;

            // Both agents are at work at the same time before either may finish: the run is really parallel.
            await Task.WhenAll(t1Started.Task, t2Started.Task).WaitAsync(TimeSpan.FromMinutes(2), Ct);
            together.SetResult();
            var result = await run.Completion.WaitAsync(Guard, Ct);
            var log = await Phase3Checks.DescribeAsync(app, runId);

            Assert.True(result.Phase == RunPhase.Succeeded, Phase3Checks.Describe(result) + "\n" + log);
            Assert.Equal(2, claude.MaxObservedConcurrency);
            Assert.Equal(["T1", "T2"], claude.Starts.Select(s => s.Request.TaskId).Order(StringComparer.Ordinal));
            Assert.Empty(codex.Starts);

            // One worktree per task, both on the same base, outside the repository, where each agent worked.
            var worktrees = (await app.PayloadsAsync<WorktreeCreatedPayload>(runId)).Select(p => p.Payload).Where(w => w.Task is not null).ToDictionary(w => w.Task!.Value.Value);
            Assert.Equal(baseCommit, worktrees["T1"].BaseCommit);
            Assert.Equal(baseCommit, worktrees["T2"].BaseCommit);
            Assert.NotEqual(worktrees["T1"].Path, worktrees["T2"].Path);
            foreach (var worktree in worktrees.Values)
            {
                Assert.StartsWith(Path.GetFullPath(paths.WorktreeRoot), Path.GetFullPath(worktree.Path), StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(worktrees["T1"].Path, claude.Starts.Single(s => s.Request.TaskId == "T1").Request.WorkingDirectory);
            Assert.Equal(worktrees["T2"].Path, claude.Starts.Single(s => s.Request.TaskId == "T2").Request.WorkingDirectory);

            // The deterministic merge: T1 then T2 (same depth, then id), each a merge commit of the system, on the run's branch.
            var t1 = Git(repo, "rev-parse", $"refs/agentstudio/{runId}/T1");
            var t2 = Git(repo, "rev-parse", $"refs/agentstudio/{runId}/T2");
            var (_, integration) = Assert.Single(await app.PayloadsAsync<IntegrationPerformedPayload>(runId));
            Assert.Equal([t1, t2], integration.Sources); // the commits each task was verified at, not the names of their refs
            Assert.Equal(IntegrationNames.BranchName(runId), integration.TargetRef);
            Assert.False(integration.IsDelivery);
            Assert.Empty(integration.ConflictPaths);
            var head = Assert.IsType<string>(integration.ResultCommit);
            Assert.Equal(head, result.IntegrationCommit);
            Assert.Equal(head, Git(repo, "rev-parse", IntegrationNames.BranchRef(runId)));
            var last = Parents(repo, head);
            Assert.Equal(t2, last[1]);
            Assert.Equal([baseCommit, t1], Parents(repo, last[0]));
            Assert.Equal(multiply, Git(repo, "show", head + ":src/Calc/Multiply.cs", trim: false));
            Assert.Equal(subtract, Git(repo, "show", head + ":src/Calc/Subtract.cs", trim: false));

            // Built and tested where it was merged: the _integration worktree is on the merge, and the tests ran from its TRX.
            var owned = await app.Worktrees.ListOwnedAsync(app.Project, Ct);
            var integrationWorktree = owned.Single(l => l.Run == runId && l.Name == IntegrationNames.IntegrationWorktree);
            Assert.Equal(head, Git(integrationWorktree.Path, "rev-parse", "HEAD"));
            var completed = (await app.PayloadsAsync<TaskStatusChangedPayload>(runId)).Single(p => p.Payload is { Task.Value: "I1", To: WorkTaskStatus.Completed }).Payload;
            var records = await Phase3Checks.AssertEvidenceIntactAsync(app, runId);
            var cited = records.Where(r => completed.Evidence.Contains(r.Id)).ToList();
            Assert.Contains(cited, r => r.Kind == EvidenceKind.BuildLog && r.Origin == EvidenceOrigin.CapturedBySystem);
            var tests = Assert.Single(cited, r => r.Kind == EvidenceKind.TestRun);
            Assert.Equal(new TestRunSummary(Passed: 3, Failed: 0, Skipped: 0, ExitCode: 0), tests.TestRun);
            Assert.Contains(records, r => r.Kind == EvidenceKind.CodeDiff);

            // The user's repository: HEAD, branches, tags, stash, index and status as they were. The only addition is the
            // prompt journal (ADR-0008) under .agentstudio/, and the only new ref is the run's own branch.
            var after = await app.Worktrees.CaptureUserRepositoryStateAsync(repo.Root, Ct);
            Assert.Empty(UserRepositoryState.Compare(Phase3Checks.WithoutAgentStudio(before), Phase3Checks.WithoutAgentStudio(after)));
            var now = repo.Snapshot();
            Assert.Equal(snapshot.Head, now.Head);
            Assert.Equal(snapshot.HeadFile, now.HeadFile);
            Assert.Equal(snapshot.UserRefs, now.UserRefs);
            Assert.Equal(snapshot.IndexHash, now.IndexHash);
            Assert.Equal(baseCommit, now.Head);
            var added = Phase3Checks.StatusEntries(now).Except(Phase3Checks.StatusEntries(snapshot), StringComparer.Ordinal).ToList();
            Assert.All(added, line => Assert.StartsWith("?? .agentstudio/bitacora/", line, StringComparison.Ordinal));
            Assert.Empty(Phase3Checks.StatusEntries(snapshot).Except(Phase3Checks.StatusEntries(now), StringComparer.Ordinal));
            Assert.Equal(
                ["refs/heads/agentstudio/run-" + runId, "refs/heads/main"],
                Git(repo, "for-each-ref", "--format=%(refname)", "refs/heads/").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

            // Every prompt went through the real journal of the project before it reached the (scripted) CLI.
            var journal = string.Concat(Phase3Checks.JournalFiles(repo.Root).Select(File.ReadAllText));
            Assert.Contains("P-0001", journal, StringComparison.Ordinal);
            Assert.Contains("P-0002", journal, StringComparison.Ordinal);
            Assert.Contains("tarea T1", journal, StringComparison.Ordinal);
            Assert.Contains("tarea T2", journal, StringComparison.Ordinal);

            // Nothing in this run needed a person: the plan was approved in the request, and the gate was never asked.
            Assert.Empty(person.Requests);
            var state = await app.LoadLogAsync();
            Assert.True(state.Verification.IsValid, state.Verification.Problem);
            headHash = state.HeadHash;
            entryCount = state.Entries.Count;
        }

        // Closing and opening the app again: the log read back from disk verifies, ends where it ended, and the evidence
        // still matches its hashes.
        await using var reopened = await Phase3App.OpenAsync(repo.Root, paths, person, ScriptedProjectAgentProviders.Over(claude, codex));
        var reloaded = await reopened.LoadLogAsync();
        Assert.True(reloaded.Verification.IsValid, reloaded.Verification.Problem);
        Assert.Equal(entryCount, reloaded.Entries.Count);
        Assert.Equal(headHash, reloaded.HeadHash);
        Assert.Equal(RunPhase.Succeeded, Assert.Single(await reopened.Orchestrator.ListRunsAsync(reopened.Project, Ct)).Phase);
        await Phase3Checks.AssertEvidenceIntactAsync(reopened, runId);
    }

    private static string Git(TempGitRepo repo, params string[] arguments) => Git(repo.Root, arguments);

    private static string Git(string directory, params string[] arguments) => TempGitRepo.Git(directory, arguments).Trim();

    private static string Git(TempGitRepo repo, string command, string argument, bool trim) =>
        trim ? Git(repo, command, argument) : TempGitRepo.Git(repo.Root, command, argument);

    private static string[] Parents(TempGitRepo repo, string commit) => Git(repo, "rev-list", "--parents", "-n", "1", commit).Split(' ')[1..];
}
