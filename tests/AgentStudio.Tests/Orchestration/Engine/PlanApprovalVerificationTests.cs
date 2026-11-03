using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/16 D-13, F8-11: the approval of the plan is a critical question. With Windows Hello on, «Aprobar plan» confirms the
/// person with Hello, or decides alone when Hello is not there (the canvas: <c>PlanApprovalHelloTests</c>); the engine records
/// how, exactly as the approval gate records an answer: version 2 with the method, unless it is the dialog alone.
/// </summary>
public sealed class PlanApprovalVerificationTests
{
    [Theory]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable)]
    public async Task The_approval_of_the_plan_records_how_the_person_was_confirmed(ApprovalVerification verification)
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));
        var request = harness.Request(plan);

        await using var run = await harness.CreateOrchestrator().StartAsync(
            request with { PlanApproval = request.PlanApproval with { Verification = verification } },
            TestContext.Current.CancellationToken);
        await run.CompleteAsync(harness);

        var granted = Assert.Single(harness.Payloads<ApprovalAnsweredPayload>(), p => p.Payload.Action == Orchestrator.PlanApprovalAction);
        Assert.Equal(DecisionKind.ApprovalGranted, granted.Entry.Kind);
        Assert.Equal(new Actor(ActorKind.User, "usuario"), granted.Entry.Actor);
        Assert.Equal((ApprovalAnsweredPayload.VerificationVersion, (ApprovalVerification?)verification), (granted.Payload.V, granted.Payload.Verification));
        Assert.Equal(
            $"{{\"action\":\"plan.approve\",\"approved\":true,\"decidedBy\":\"usuario\",\"comment\":\"adelante\",\"verification\":\"{verification}\",\"v\":2,\"runId\":\"{run.Id.Value}\"}}",
            granted.Entry.PayloadJson);
    }

    /// <summary>With Windows Hello off, or a plan approved before Phase 8, the answer is the one it always was, byte for byte.</summary>
    [Fact]
    public async Task An_approval_of_the_plan_by_the_button_alone_is_still_version_1()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var granted = Assert.Single(harness.Payloads<ApprovalAnsweredPayload>(), p => p.Payload.Action == Orchestrator.PlanApprovalAction);
        Assert.Equal(
            $"{{\"action\":\"plan.approve\",\"approved\":true,\"decidedBy\":\"usuario\",\"comment\":\"adelante\",\"v\":1,\"runId\":\"{run.Id.Value}\"}}",
            granted.Entry.PayloadJson);
        Assert.Equal(ApprovalVerification.Dialog, granted.Payload.ResolvedVerification());
    }
}
