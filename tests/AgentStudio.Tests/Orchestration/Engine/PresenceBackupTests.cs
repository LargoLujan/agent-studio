using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The backup of the fixed barrier of Windows Hello in the engine (security review of WP5, 2026-11-09; docs/17 D-37,
/// ADR-0053): a plan with writers at medium integrity runs only on an approval that confirmed the person, with Windows Hello or
/// the verified lock of Windows, whatever sends the request; and a run approved before the barrier with the dialog alone is not
/// resumed, but approved again. The canvas already asks; this is what holds if a path forgets.
/// </summary>
public sealed class PresenceBackupTests
{
    private const string NotResumable = "run.not-resumable";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MediumIntegrityWriterEntry Unmeasured = new(new TaskId("T1"), "UnmeasuredClaudeVersion", new AgentId("ejecutor"));

    [Theory]
    [InlineData(ApprovalVerification.Dialog)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable)]
    public async Task A_plan_with_writers_approved_without_confirming_the_person_does_not_start(ApprovalVerification verification)
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var request = harness.Request(Plan(Work("T1")));
        Assert.NotEmpty(request.ApprovedWarning!);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateOrchestrator().StartAsync(request with { PlanApproval = request.PlanApproval with { Verification = verification } }, Ct));

        Assert.Contains("Windows Hello", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads<RunStartedPayload>());
        Assert.Empty(harness.Claude.Starts);
    }

    [Theory]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsLock)]
    public async Task A_plan_with_writers_approved_with_hello_or_the_lock_of_windows_starts(ApprovalVerification verification)
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var request = harness.Request(Plan(Work("T1")));

        await using var run = await harness.CreateOrchestrator().StartAsync(request with { PlanApproval = request.PlanApproval with { Verification = verification } }, Ct);

        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }

    /// <summary>A run approved before the barrier, with the dialog alone, whose plan has writers: approved again, never resumed on that approval.</summary>
    [Fact]
    public async Task A_run_with_writers_approved_by_the_dialog_alone_is_not_resumed_and_asks_for_a_new_approval()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [Unmeasured], planApproval: ApprovalVerification.Dialog);

        var refusal = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.Equal(run, refusal.Run);
        Assert.Contains("Windows Hello", refusal.Message, StringComparison.Ordinal);
        Assert.Single(harness.Alerts(NotResumable));
        Assert.Empty(harness.Claude.Starts);
        Assert.True((await harness.Decisions.LoadAsync(harness.Project.ProjectId, Ct)).Verification.IsValid);
    }

    /// <summary>
    /// The re-review of WP5 (2026-11-09): a run that resumes by consent is tried at every start of the app. Two starts in a row
    /// leave one alert, not two; a new approval of its plan in between is a change, and the refusal that follows is said again.
    /// </summary>
    [Fact]
    public async Task Two_starts_in_a_row_leave_one_alert_and_a_new_approval_in_between_another()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [Unmeasured], planApproval: ApprovalVerification.Dialog);

        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));
        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.Single(harness.Alerts(NotResumable));

        await harness.Decisions.RecordAsync(
            harness.Project.ProjectId,
            new AgentStudio.Domain.Decisions.Actor(AgentStudio.Domain.Decisions.ActorKind.User, "usuario"),
            AgentStudio.Domain.Decisions.DecisionKind.ApprovalGranted,
            "Plan aprobado otra vez",
            ApprovalAnswers.Payload(Orchestrator.PlanApprovalAction, true, "usuario", "otra vez", null, ApprovalVerification.Dialog, run.Value),
            null,
            Ct);
        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.Equal(2, harness.Alerts(NotResumable).Count);
        Assert.True((await harness.Decisions.LoadAsync(harness.Project.ProjectId, Ct)).Verification.IsValid);
    }

    /// <summary>The same for a run whose writers changed since it was approved (G-02): one alert for two starts in a row.</summary>
    [Fact]
    public async Task Two_starts_of_a_run_whose_writers_changed_leave_one_alert()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [new MediumIntegrityWriterEntry(new TaskId("T1"), "Shell", new AgentId("ejecutor"))]);

        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));
        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.Single(harness.Alerts(NotResumable));
        Assert.Empty(harness.Claude.Starts);
    }

    [Theory]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsLock)]
    public async Task A_run_with_writers_approved_with_hello_or_the_lock_of_windows_is_resumed(ApprovalVerification verification)
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [Unmeasured], planApproval: verification);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct);

        Assert.Equal(RunPhase.Succeeded, (await resumed.CompleteAsync(harness)).Phase);
        Assert.Empty(harness.Alerts(NotResumable));
    }

    /// <summary>
    /// A run whose start anchored no writers asks nothing of its approval: the dialog alone was enough then. If its plan has
    /// writers now, the resumer refuses it for that (G-02), not for the approval.
    /// </summary>
    [Fact]
    public async Task A_run_whose_start_anchored_no_writers_is_not_held_by_its_approval()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [], planApproval: ApprovalVerification.Dialog);

        // The plan has writers now (the probe measured nothing), so the resumer refuses it for that, not for the approval.
        var refusal = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.DoesNotContain("Windows Hello", refusal.Message, StringComparison.Ordinal);
    }
}
