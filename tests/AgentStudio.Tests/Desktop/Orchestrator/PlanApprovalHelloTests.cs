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
/// docs/16 D-13, F8-11: «Aprobar plan» is a critical approval, so with Windows Hello on it follows the rule of every critical
/// question (<see cref="ReinforcedConfirmation"/>): Hello after the click, and only a confirmation approves; the click alone,
/// with a warning, when Windows says Hello is not there. What the run carries is what the engine records
/// (<c>PlanApprovalVerificationTests</c>). Windows Hello and the engine are doubles.
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

    [Fact]
    public async Task With_windows_hello_off_the_click_alone_approves_as_before()
    {
        var hello = new FakeUserPresenceVerifier();
        var (document, engine) = await PlanReadyAsync(new ReinforcedConfirmation(hello, () => false));

        await document.ApproveCommand.ExecuteAsync(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(ApprovalVerification.Dialog, Assert.Single(engine.RunRequests).PlanApproval.Verification);
        Assert.Equal(0, hello.AvailabilityChecks);
        Assert.Empty(hello.Messages);
        Assert.Null(document.HelloNotice);
    }

    [Fact]
    public async Task Without_the_confirmation_composed_the_click_alone_approves_as_before()
    {
        var (document, engine) = await PlanReadyAsync(null);

        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(ApprovalVerification.Dialog, Assert.Single(engine.RunRequests).PlanApproval.Verification);
    }

    /// <summary>«Sin Hello: no disponible»: the click decides alone, the review warns and says why, and the run carries it to the log.</summary>
    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser, "no está configurado para tu usuario")]
    [InlineData(UserPresenceAvailability.DeviceNotPresent, "no hay ningún dispositivo con el que verificarte")]
    [InlineData(UserPresenceAvailability.DisabledByPolicy, "lo desactiva una directiva")]
    public async Task Without_windows_hello_on_this_computer_the_click_decides_alone_with_a_warning(UserPresenceAvailability absent, string why)
    {
        var hello = FakeUserPresenceVerifier.Without(absent);
        var (document, engine) = await PlanReadyAsync(On(hello));

        await document.ApproveCommand.ExecuteAsync(null);

        Assert.True(document.IsApproved);
        Assert.Empty(hello.Messages);
        Assert.True(document.HasHelloNotice);
        Assert.Equal(Tone.Warning, document.HelloNoticeTone);
        Assert.Equal($"Windows Hello no está disponible en este equipo ({why}): el plan lo aprobó solo el botón, y el registro lo dice.", document.HelloNotice);

        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(ApprovalVerification.WindowsHelloUnavailable, Assert.Single(engine.RunRequests).PlanApproval.Verification);
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

    [Fact]
    public async Task The_warning_goes_with_the_approval_it_belongs_to()
    {
        var (document, _) = await PlanReadyAsync(On(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser)));
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.True(document.HasHelloNotice);

        document.RejectCommand.Execute(null);

        Assert.False(document.HasHelloNotice);
        Assert.Null(document.HelloNotice);
    }
}
