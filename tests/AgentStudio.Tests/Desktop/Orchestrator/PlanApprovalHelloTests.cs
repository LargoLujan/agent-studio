using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// docs/16 D-13, F8-11 and docs/17 D-37 to D-41 (F13-16): «Aprobar plan» follows the rule of every approval
/// (<see cref="ReinforcedConfirmation"/>). The plan of the fixture has writers at medium integrity, so approving it is on the
/// list of the fixed barrier: Windows Hello after the click whatever the setting says, and only a confirmation approves;
/// without Hello on the computer, the verified lock of Windows. What the run carries is what the engine records
/// (<c>PlanApprovalVerificationTests</c>). Windows Hello, the lock of Windows and the engine are doubles.
/// </summary>
public sealed class PlanApprovalHelloTests
{
    private const string Root = @"C:\proyectos\calc";

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        private CanvasLayout _saved = CanvasLayout.Empty;

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => _saved;

        public void Save(string projectRoot, CanvasLayout layout) => _saved = layout;
    }

    private static async Task<(OrchestratorDocumentViewModel Document, FakeOrchestrator Engine)> PlanReadyAsync(ReinforcedConfirmation? confirmation)
    {
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(new RunId("20260101-abc123"), RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var document = new OrchestratorDocumentViewModel(
            Root, new OrchestrationSession(Backends.Complete(engine)), new MemoryLayoutStore(), new FakeTimeProvider(T0), new InlineSynchronizationContext(), confirmation);
        await document.OpenAsync();
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
        Assert.True(document.ApproveCommand.CanExecute(null));
        return (document, engine);
    }

    private static ReinforcedConfirmation On(FakeUserPresenceVerifier hello) => new(hello, () => true);

    [Fact]
    public async Task With_windows_hello_on_the_plan_is_approved_only_once_hello_confirms_the_person_and_the_run_says_so()
    {
        var hello = FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified);
        var (document, engine) = await PlanReadyAsync(On(hello));

        await document.ApproveCommand.ExecuteAsync(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.True(document.IsApproved);
        Assert.Equal($"Agent Studio: confirma que eres tú para aprobar el plan {document.CurrentHash[..12]}.", Assert.Single(hello.Messages));
        Assert.Equal(ApprovalVerification.WindowsHello, Assert.Single(engine.RunRequests).PlanApproval.Verification);
        Assert.Null(document.HelloNotice);
    }

    /// <summary>A click Windows Hello does not confirm approves nothing: no run starts, so the engine writes no grant; the screen says why.</summary>
    [Theory]
    [InlineData(UserPresenceResult.Canceled, "se canceló la verificación")]
    [InlineData(UserPresenceResult.RetriesExhausted, "demasiados intentos fallidos")]
    [InlineData(UserPresenceResult.DeviceBusy, "el dispositivo estaba ocupado")]
    public async Task A_click_windows_hello_does_not_confirm_approves_nothing_and_starts_no_run(UserPresenceResult result, string why)
    {
        var (document, engine) = await PlanReadyAsync(On(FakeUserPresenceVerifier.Answering(result)));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.False(document.IsApproved);
        Assert.Null(document.PlanDecision);
        Assert.Equal($"Windows Hello no confirmó que eres tú ({why}): el plan no se aprueba.", document.ErrorMessage);
        Assert.False(document.ExecuteCommand.CanExecute(null));

        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(engine.RunRequests);
    }

    [Fact]
    public async Task A_verifier_that_fails_approves_nothing_and_says_why()
    {
        var hello = new FakeUserPresenceVerifier { Verify = (_, _) => throw new InvalidOperationException("No window.") };
        var (document, engine) = await PlanReadyAsync(On(hello));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.False(document.IsApproved);
        Assert.Equal("No se pudo pedir Windows Hello (No window.): el plan no se aprueba.", document.ErrorMessage);
        Assert.Empty(engine.RunRequests);
    }

    /// <summary>
    /// F13-16 (docs/17 D-37 and D-38): the plan of the fixture has writers at medium integrity, so approving it is on the list of
    /// the fixed barrier. With the setting off, or a setting that cannot even be read, Windows Hello is asked all the same: the
    /// setting is never read for it.
    /// </summary>
    [Fact]
    public async Task With_the_setting_off_or_unreadable_a_plan_with_writers_still_asks_windows_hello()
    {
        foreach (var isOn in new Func<bool>[] { () => false, () => throw new InvalidOperationException("The settings file was edited by hand.") })
        {
            var hello = FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified);
            var (document, engine) = await PlanReadyAsync(new ReinforcedConfirmation(hello, isOn));
            Assert.Equal(HelloTexts.PlanAlwaysAsks, document.HelloNotice);

            await document.ApproveCommand.ExecuteAsync(null);
            await document.ExecuteCommand.ExecuteAsync(null);

            Assert.Equal(ApprovalVerification.WindowsHello, Assert.Single(engine.RunRequests).PlanApproval.Verification);
            Assert.Single(hello.Messages);
            Assert.Null(document.HelloNotice);
        }
    }

    [Fact]
    public void A_plan_is_on_the_list_when_it_has_writers_at_medium_integrity_and_off_it_when_it_has_none()
    {
        var withWriters = PlanFixtures.Plan();
        var onlyApproval = PlanFixtures.Plan([PlanFixtures.Spec("T1", AgentStudio.Domain.Work.WorkTaskKind.HumanApproval)]);

        Assert.Equal(AgentStudio.Domain.Security.PresenceAction.ApprovePlanWithHighRiskCapability, PlanPresence.Of(withWriters, PlanFixtures.Roster(), Backends.Profile, claudeWritesConfined: false));
        Assert.Equal(AgentStudio.Domain.Security.PresenceAction.ApprovePlan, PlanPresence.Of(onlyApproval, PlanFixtures.Roster(), Backends.Profile, claudeWritesConfined: false));

        // Without the template or the profile the writers cannot be read: it counts as one (fail closed).
        Assert.Equal(AgentStudio.Domain.Security.PresenceAction.ApprovePlanWithHighRiskCapability, PlanPresence.Of(onlyApproval, null, Backends.Profile, claudeWritesConfined: false));
        Assert.Equal(AgentStudio.Domain.Security.PresenceAction.ApprovePlanWithHighRiskCapability, PlanPresence.Of(onlyApproval, PlanFixtures.Roster(), null, claudeWritesConfined: false));
    }

    [Fact]
    public async Task Without_the_confirmation_composed_the_click_alone_approves_as_before()
    {
        var (document, engine) = await PlanReadyAsync(null);

        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(ApprovalVerification.Dialog, Assert.Single(engine.RunRequests).PlanApproval.Verification);
    }

    /// <summary>
    /// Without Windows Hello on this computer (docs/17 D-40 (a)), a plan on the list is not decided by the click alone: Windows
    /// locks, and only an unlock the state of the session confirms approves it. The run carries the method to the log.
    /// </summary>
    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy)]
    public async Task Without_windows_hello_on_this_computer_a_plan_with_writers_locks_windows_and_the_run_says_so(UserPresenceAvailability absent)
    {
        var hello = FakeUserPresenceVerifier.Without(absent);
        var windowsLock = FakeWindowsLockConfirmation.Answering(WindowsLockResult.UnlockedAfterRequest);
        var (document, engine) = await PlanReadyAsync(new ReinforcedConfirmation(hello, () => false, WindowsLock: windowsLock));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.True(document.IsApproved);
        Assert.Empty(hello.Messages);
        Assert.Equal([ReinforcedConfirmation.WindowsLockTimeout], windowsLock.Locks);
        Assert.Equal(HelloTexts.PlanApprovedWithLock(document.CurrentHash[..12]), document.ApprovalText);
        Assert.Null(document.HelloNotice);

        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(ApprovalVerification.WindowsLock, Assert.Single(engine.RunRequests).PlanApproval.Verification);
    }

    [Theory]
    [InlineData(WindowsLockResult.LockFailed)]
    [InlineData(WindowsLockResult.NotVerified)]
    [InlineData(WindowsLockResult.TimedOut)]
    public async Task A_lock_of_windows_the_session_does_not_confirm_approves_nothing_and_says_why(WindowsLockResult result)
    {
        var hello = FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser);
        var (document, engine) = await PlanReadyAsync(new ReinforcedConfirmation(hello, () => true, WindowsLock: FakeWindowsLockConfirmation.Answering(result)));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.False(document.IsApproved);
        Assert.Null(document.PlanDecision);
        Assert.Equal(HelloTexts.PlanLockNotConfirmed(result), document.ErrorMessage);
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(engine.RunRequests);
    }

    /// <summary>
    /// The security review of WP5 (2026-11-09): «Aprobar plan» and the questions of the gate share one confirmation of the
    /// person at a time. While a question of the gate waits for Windows Hello, the plan is not confirmed by that same
    /// verification: it is not approved, and the screen says another confirmation is pending.
    /// </summary>
    [Fact]
    public async Task While_another_confirmation_is_pending_the_plan_is_not_approved_and_the_screen_says_why()
    {
        var verified = new TaskCompletionSource<UserPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hello = new FakeUserPresenceVerifier { Verify = (_, _) => verified.Task };
        var confirmation = On(hello);
        var (document, engine) = await PlanReadyAsync(confirmation);
        var gateQuestion = await confirmation.PlanAsync(AgentStudio.Domain.Security.PresenceAction.DeliverToBranch, CancellationToken.None);
        var pending = confirmation.ConfirmAsync(gateQuestion, "Agent Studio: entrega", CancellationToken.None);

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.False(document.IsApproved);
        Assert.Equal(HelloTexts.PlanConfirmationPending, document.ErrorMessage);
        Assert.Single(hello.Messages);
        verified.SetResult(UserPresenceResult.Verified);
        Assert.Equal(AgentStudio.Domain.Security.PresenceProof.WindowsHello, (await pending).Proof);
        Assert.Empty(engine.RunRequests);
    }

    /// <summary>Without Hello and without the verified lock composed, nothing can confirm the person: the plan is not approved, never left to the click.</summary>
    [Fact]
    public async Task Without_windows_hello_and_without_the_lock_of_windows_a_plan_with_writers_is_not_approved()
    {
        var hello = FakeUserPresenceVerifier.Without(UserPresenceAvailability.DeviceNotPresent);
        var (document, engine) = await PlanReadyAsync(new ReinforcedConfirmation(hello, () => true));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.False(document.IsApproved);
        Assert.Equal(HelloTexts.PlanLockNotAsked(HelloTexts.LockNotComposed), document.ErrorMessage);
        Assert.Empty(engine.RunRequests);
    }

    [Fact]
    public async Task Rejecting_the_plan_never_asks_windows_hello()
    {
        var hello = new FakeUserPresenceVerifier();
        var (document, _) = await PlanReadyAsync(On(hello));

        document.RejectCommand.Execute(null);

        Assert.Equal(0, hello.AvailabilityChecks);
        Assert.Empty(hello.Messages);
        Assert.NotNull(document.PlanDecision);
        Assert.False(document.PlanDecision.Approved);
    }

    /// <summary>Before approving a plan with writers, the review says that approving it always asks; once approved, it says nothing more; rejected, it says it again.</summary>
    [Fact]
    public async Task The_notice_goes_with_the_approval_it_belongs_to()
    {
        var (document, _) = await PlanReadyAsync(On(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified)));
        Assert.True(document.HasHelloNotice);
        Assert.Equal(HelloTexts.PlanAlwaysAsks, document.HelloNotice);

        await document.ApproveCommand.ExecuteAsync(null);
        Assert.False(document.HasHelloNotice);

        document.RejectCommand.Execute(null);
        Assert.Equal(HelloTexts.PlanAlwaysAsks, document.HelloNotice);
    }
}
