using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// The F3-3 demo without quota (docs/13 sections 4.4 and 10) on the real modules: a plan with a <c>ToolRun</c>, a
/// <c>Condition</c> and a <c>HumanApproval</c>. The tool is a step of the trusted profile (a PowerShell script of the
/// repository that writes a JUnit report), the condition reads the tests the system captured, and the person behind the
/// real approval gate says no: what depends on the approval can never run, the health report says so, and the decision
/// log carries one alert per problem and the question and the answer, in order.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class HealthDemoTests
{
    private const string CheckScript = """
        param([string] $Report)
        $ok = Test-Path -LiteralPath 'src/feature.txt'
        $case = if ($ok) { '<testcase name="feature" classname="check" />' } else { '<testcase name="feature" classname="check"><failure message="falta src/feature.txt" /></testcase>' }
        Set-Content -LiteralPath $Report -Value "<testsuites><testsuite name='check'>$case</testsuite></testsuites>" -Encoding utf8
        if ($ok) { exit 0 } else { exit 1 }

        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskId Id(string id) => new(id);

    [Fact]
    public async Task A_denied_approval_after_a_tool_and_a_condition_leaves_its_dependents_unreachable_with_a_health_report_and_alerts()
    {
        using var repo = Phase3Repos.WithScript("check.ps1", CheckScript);
        var paths = Phase3Paths.Beside(repo.Root);
        var claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, TimeProvider.System);
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, TimeProvider.System);
        claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/feature.txt", "función nueva\n")));
        var person = new ScriptedApprovalPrompt { Approve = _ => false };
        var profile = new VerificationProfile("demo F3-3", [Phase3Repos.ScriptStep("check", "Comprobar la función", "check.ps1", TimeSpan.FromMinutes(3))]);

        // T1 writes; L1 checks it with the profile's tool; C1 asks whether those tests passed; on yes a person approves A1
        // before T3; on no N1 documents why.
        var plan = Plan(
            Work("T1", scope: ["src/"]),
            Node("L1", WorkTaskKind.ToolRun).After(On("T1")) with { ToolId = "check", MaxRetries = 0 },
            Node("C1", WorkTaskKind.Condition).After(On("L1")) with { Condition = new TestsPassed(Id("L1")) },
            Node("A1", WorkTaskKind.HumanApproval).After(On("C1", "yes")) with { Title = "Publicar la función", Objective = "¿Se publica la función nueva?" },
            Work("N1", scope: ["docs/"]).After(On("C1", "no")) with { Covers = [] },
            Work("T3", scope: ["src/"]).After(On("A1")) with { Covers = [] });

        await using var app = await Phase3App.OpenAsync(repo.Root, paths, person, ScriptedProjectAgentProviders.Over(claude, codex), Roster());
        var request = new RunRequest(
            app.Project,
            null,
            "Añade la función y publícala si sus pruebas pasan.",
            Requirements(),
            plan,
            new ApprovalDecision(true, "user", DateTimeOffset.UtcNow, "Plan aprobado en el lienzo."),
            profile,
            new RunOptions(),
            ValidatedRoster: await app.RosterAsync(),
            ApprovedWarning: await app.ApprovedWarningAsync(plan, profile));

        await using var run = await app.Orchestrator.StartAsync(request, Ct);
        var result = await run.Completion.WaitAsync(TimeSpan.FromMinutes(4), Ct);
        var events = new List<OrchestrationEvent>();
        await foreach (var e in run.Events.WithCancellation(Ct))
        {
            events.Add(e);
        }

        var log = await Phase3Checks.DescribeAsync(app, run.Id);

        // The run fails, and says why.
        Assert.True(result.Phase == RunPhase.Failed, Phase3Checks.Describe(result) + "\n" + log);
        Assert.Contains("A1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("T3", result.Summary, StringComparison.Ordinal);

        // The health report: nothing stalled; T3 can never run because A1 failed for good. Nothing is left that could
        // move (IsDeadlocked), and the loop explains it (retries exhausted, unreachable) instead of raising a deadlock.
        Assert.Empty(result.Health.StalledTasks);
        Assert.Equal([Id("T3")], result.Health.UnreachableTasks);
        Assert.Equal([Id("A1")], result.Health.RetryExhaustedTasks);
        Assert.True(result.Health.IsDeadlocked);
        Assert.Contains(events.OfType<GraphHealthEvent>(), e => e.Report.UnreachableTasks.SequenceEqual([Id("T3")]) && e.Report.RetryExhaustedTasks.SequenceEqual([Id("A1")]));
        Assert.Equal(RunPhase.Failed, Assert.Single(events.OfType<RunFinishedEvent>()).Result.Phase);

        // One alert per problem, raised once, and no false deadlock or block.
        var alerts = (await app.PayloadsAsync<AuditAlertPayload>(run.Id)).Select(a => $"{a.Payload.Code}:{string.Join(",", a.Payload.Tasks)}").Order(StringComparer.Ordinal);
        Assert.Equal(["task.retries-exhausted:A1", "task.unreachable:T3"], alerts);

        // Every node did its part: the tool ran the profile's step on T1's code, and its tests are what the condition read.
        var snapshot = run.Snapshot();
        Assert.Equal(
            ["A1 Failed", "C1 Completed", "L1 Completed", "N1 Skipped", "T1 Completed", "T3 Pending"],
            snapshot.Tasks.Select(t => $"{t.Id} {t.Status}").Order(StringComparer.Ordinal));
        var t1Head = TempGitRepo.Git(repo.Root, "rev-parse", $"refs/agentstudio/{run.Id}/T1").Trim();
        Assert.Equal(t1Head, (await app.PayloadsAsync<WorktreeCreatedPayload>(run.Id)).Single(w => w.Payload.Task == Id("L1")).Payload.BaseCommit);
        var records = await Phase3Checks.AssertEvidenceIntactAsync(app, run.Id);
        var toolTests = Assert.Single(records, r => snapshot.View("L1").Evidence.Contains(r.Id) && r.Kind == EvidenceKind.TestRun);
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 0, Skipped: 0, ExitCode: 0), toolTests.TestRun);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, toolTests.Origin);
        var (_, condition) = Assert.Single(await app.PayloadsAsync<ConditionEvaluatedPayload>(run.Id));
        Assert.Equal(Id("C1"), condition.Task);
        Assert.Equal("TestsPassed(L1)", condition.Predicate);
        Assert.True(condition.Result);
        Assert.Equal("yes", condition.Branch);
        Assert.Contains(new ConditionFact("L1.status", "Completed"), condition.Facts);
        Assert.Contains(new ConditionFact("L1.tests", "passed=1 failed=0 skipped=0 exit=0"), condition.Facts);
        var transitions = await app.TransitionsAsync(run.Id);
        Assert.Contains("N1 Pending>Skipped", transitions);
        Assert.DoesNotContain(transitions, t => t.StartsWith("T3 ", StringComparison.Ordinal));
        Assert.Equal(["T1"], claude.Starts.Select(s => s.Request.TaskId));
        Assert.Empty(codex.Starts);

        // The real gate asked the person once, recorded the question first and the denial after it, and A1 failed on it.
        var asked = Assert.Single(person.Requests);
        Assert.Equal("orchestration.human-approval", asked.Action);
        Assert.Equal(RiskLevel.Critical, asked.Risk);
        Assert.Equal((app.Project.ProjectId, run.Id.Value, "A1"), (asked.ProjectId, asked.RunId, asked.TaskId));
        Assert.Contains(events.OfType<ApprovalPendingEvent>(), e => e.Task == Id("A1"));
        var entries = await app.RunEntriesAsync(run.Id);
        var question = entries.Single(e => e.Payload is ApprovalRequestedPayload { Action: "orchestration.human-approval" });
        var answer = entries.Single(e => e.Entry.Kind == DecisionKind.ApprovalDenied);
        var failed = entries.Single(e => e.Payload is TaskStatusChangedPayload { Task.Value: "A1", To: WorkTaskStatus.Failed });
        Assert.True(question.Entry.Sequence < answer.Entry.Sequence && answer.Entry.Sequence < failed.Entry.Sequence, log);
        Assert.Equal(Id("A1"), ((ApprovalRequestedPayload)question.Payload).Task);
        Assert.Equal(RiskLevel.Critical, ((ApprovalRequestedPayload)question.Payload).Risk);
        var denial = (ApprovalAnsweredPayload)answer.Payload;
        Assert.False(denial.Approved);
        Assert.Equal(ScriptedApprovalPrompt.Person, denial.DecidedBy);
        Assert.Equal(new Actor(ActorKind.User, ScriptedApprovalPrompt.Person), answer.Entry.Actor);
        Assert.Contains("Denegado por " + ScriptedApprovalPrompt.Person, ((TaskStatusChangedPayload)failed.Payload).Reason, StringComparison.Ordinal);

        var state = await app.LoadLogAsync();
        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(RunPhase.Failed, Assert.Single(await app.Orchestrator.ListRunsAsync(app.Project, Ct)).Phase);
    }
}
