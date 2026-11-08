using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Security;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Security.Approvals;

/// <summary>
/// F13-16 in the approval gate (docs/17 D-37 to D-39, ADR-0053): what an approval asks of the person's presence goes by its
/// action, never by its words, and an approval of an action of the list counts only when Windows Hello or the verified lock of
/// Windows confirmed the person, whatever prompt answered it. The prompt is scripted, as a rogue or badly composed one would
/// answer: no Windows Hello is asked by these tests.
/// </summary>
public sealed class PresenceBarrierGateTests
{
    private const string Project = "p-barrier";
    private const string Run = "20261107-b4rr13";

    private sealed class ScriptedPrompt(bool approved, ApprovalVerification verification, string? comment = "ok") : IApprovalPrompt
    {
        public int Asked { get; private set; }

        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new ApprovalDecision(approved, "user", T0, comment, Verification: verification));
        }
    }

    private static (ApprovalGate Gate, InMemoryDecisionLogStore Store, ScriptedPrompt Prompt) Gate(bool approved, ApprovalVerification verification, string? comment = "ok")
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var prompt = new ScriptedPrompt(approved, verification, comment);
        return (new ApprovalGate(prompt, new DecisionRecorder(store, clock), clock, new ApprovalGateOptions()), store, prompt);
    }

    private static ApprovalRequest Request(string action, RiskLevel risk = RiskLevel.Critical) =>
        new(action, "Lo que se pide.", risk, null, [], Project, Run, "T1");

    /// <summary>The identifiers the gate knows are the constants of the code that asks each action: a rename fails here, never takes it off the list.</summary>
    [Fact]
    public void Every_known_action_is_the_constant_of_the_code_that_asks_it_with_its_action_of_the_barrier()
    {
        var expected = new Dictionary<string, PresenceAction>(StringComparer.Ordinal)
        {
            [DeliveryService.ApprovalAction] = PresenceAction.DeliverToBranch,
            [UnanchoredHistory.AcceptAction] = PresenceAction.AcceptHistoryAsOrigin,
            [DebateCoordinator.OpenAction] = PresenceAction.OpenOrResumeDebate,
            [DebateCoordinator.ResumeAction] = PresenceAction.OpenOrResumeDebate,
            [DebateRun.HumanResolutionAction] = PresenceAction.OpenOrResumeDebate,
            [ProjectAuditService.ApprovalAction] = PresenceAction.AuditProject,
            [ChallengeLog.RevisionAcceptanceAction] = PresenceAction.AcceptReview,
            [WorkspaceCleanup.ApprovalAction] = PresenceAction.DiscardAgentWork,
            [SkillsDocumentViewModel.AssistedAction] = PresenceAction.AuxiliaryQuotaConsent,
        };

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), ApprovalRiskClassifier.KnownPresenceActions.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.All(expected, p => Assert.Equal(p.Value, ApprovalRiskClassifier.PresenceActionOf(Request(p.Key))));
    }

    [Theory]
    [InlineData("deliver-integration", PresenceRequirement.Always)]
    [InlineData("decision-log.accept-origin", PresenceRequirement.Always)]
    [InlineData("debate.open", PresenceRequirement.Configurable)]
    [InlineData("audit.run", PresenceRequirement.Configurable)]
    [InlineData("verdict.revision.accept", PresenceRequirement.Configurable)]
    [InlineData("discard-run-work", PresenceRequirement.Configurable)]
    [InlineData("skills.recomendacion-asistida", PresenceRequirement.Never)]
    public void A_known_action_asks_what_the_list_says_whatever_its_declared_risk(string action, PresenceRequirement requirement)
    {
        Assert.All(Enum.GetValues<RiskLevel>(), risk => Assert.Equal(requirement, ApprovalRiskClassifier.PresenceOf(Request(action, risk))));
    }

    /// <summary>
    /// An action the list does not name (the approval node of a plan, whose action the plan writes) goes by its level: its words
    /// never put it on the list nor take it off (decision 6 (a): configurable the rest).
    /// </summary>
    [Theory]
    [InlineData("Fusionar en main", RiskLevel.Critical, PresenceRequirement.Configurable)]
    [InlineData("deliver-to-main", RiskLevel.Low, PresenceRequirement.Configurable)]
    [InlineData("Deliver-Integration", RiskLevel.Low, PresenceRequirement.Configurable)]
    [InlineData("worktree-remove", RiskLevel.Low, PresenceRequirement.Configurable)]
    [InlineData("revisar-informe", RiskLevel.Critical, PresenceRequirement.Configurable)]
    [InlineData(RunMonitorViewModel.ReplanDismissAction, RiskLevel.Low, PresenceRequirement.Never)]
    [InlineData("approve-plan", RiskLevel.Low, PresenceRequirement.Never)]
    public void An_action_the_list_does_not_name_goes_by_its_level_never_by_its_words(string action, RiskLevel risk, PresenceRequirement requirement)
    {
        Assert.Null(ApprovalRiskClassifier.PresenceActionOf(Request(action, risk)));
        Assert.Equal(requirement, ApprovalRiskClassifier.PresenceOf(Request(action, risk)));
    }

    /// <summary>
    /// A prompt that approves a delivery, or the acceptance of a history, without the person confirmed (the dialog alone, or
    /// the dialog with the warning that Hello is not there) gets no approval: the system denies it, with the reason, as it does
    /// a question that expired. No setting and no prompt composed without Windows Hello lets one through.
    /// </summary>
    [Theory]
    [InlineData("deliver-integration", ApprovalVerification.Dialog)]
    [InlineData("deliver-integration", ApprovalVerification.WindowsHelloUnavailable)]
    [InlineData("decision-log.accept-origin", ApprovalVerification.Dialog)]
    [InlineData("decision-log.accept-origin", ApprovalVerification.WindowsHelloUnavailable)]
    public async Task A_yes_to_an_action_of_the_list_that_nothing_confirmed_is_the_systems_denial(string action, ApprovalVerification claimed)
    {
        var (gate, store, prompt) = Gate(approved: true, claimed);

        var decision = await gate.RequestAsync(Request(action), TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        Assert.Equal(1, prompt.Asked);
        Assert.Equal((ApprovalGate.SystemId, ApprovalGate.PresenceNotConfirmed), (decision.DecidedBy, decision.Comment));
        var log = store.Of(Project);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), log[1].Actor);
        var answered = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(log[1].PayloadJson);
        Assert.Equal((action, false, ApprovalGate.SystemId, ApprovalGate.PresenceNotConfirmed), (answered.Action, answered.Approved, answered.DecidedBy, answered.Comment));
    }

    [Theory]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsLock)]
    public async Task A_yes_to_an_action_of_the_list_that_hello_or_the_lock_of_windows_confirmed_is_the_persons(ApprovalVerification method)
    {
        var (gate, store, _) = Gate(approved: true, method);

        var decision = await gate.RequestAsync(Request(DeliveryService.ApprovalAction), TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        Assert.Equal(("user", method), (decision.DecidedBy, decision.Verification));
        var log = store.Of(Project);
        Assert.Equal(DecisionKind.ApprovalGranted, log[1].Kind);
        Assert.Equal(new Actor(ActorKind.User, "user"), log[1].Actor);
        Assert.Contains($"\"verification\":\"{method}\",\"v\":2", log[1].PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>The method of the verified lock is written by its name, in version 2, as any other (docs/04 §21.2).</summary>
    [Fact]
    public async Task An_approval_the_lock_of_windows_confirmed_records_the_method_byte_for_byte()
    {
        var (gate, store, _) = Gate(approved: true, ApprovalVerification.WindowsLock);

        await gate.RequestAsync(Request(DeliveryService.ApprovalAction), TestContext.Current.CancellationToken);

        Assert.Equal(
            "{\"action\":\"deliver-integration\",\"approved\":true,\"decidedBy\":\"user\",\"comment\":\"ok\",\"task\":\"T1\",\"verification\":\"WindowsLock\",\"v\":2,\"runId\":\"20261107-b4rr13\"}",
            store.Of(Project)[1].PayloadJson);
    }

    /// <summary>What is configurable keeps the answer of the dialog alone: the setting governs it (docs/17 D-38).</summary>
    [Theory]
    [InlineData("debate.open")]
    [InlineData("audit.run")]
    [InlineData("discard-run-work")]
    [InlineData("Fusionar en main")]
    public async Task A_configurable_action_takes_the_answer_of_the_dialog_alone(string action)
    {
        var (gate, store, _) = Gate(approved: true, ApprovalVerification.Dialog);

        var decision = await gate.RequestAsync(Request(action), TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        Assert.Equal(DecisionKind.ApprovalGranted, store.Of(Project)[1].Kind);
        Assert.DoesNotContain("verification", store.Of(Project)[1].PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>A yes the verified lock of Windows did not confirm is no decision of the person: the system denies it, with the reason and the method.</summary>
    [Fact]
    public async Task A_yes_the_lock_of_windows_did_not_confirm_is_the_systems_denial_with_the_reason()
    {
        const string Reason = "El bloqueo de Windows no confirmó que eres tú (TimedOut: nadie lo desbloqueó a tiempo): se deniega.";
        var (gate, store, _) = Gate(approved: false, ApprovalVerification.WindowsLock, Reason);

        var decision = await gate.RequestAsync(Request(DeliveryService.ApprovalAction), TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        Assert.Equal((ApprovalGate.SystemId, Reason, ApprovalVerification.WindowsLock), (decision.DecidedBy, decision.Comment, decision.Verification));
        var log = store.Of(Project);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), log[1].Actor);
        Assert.Contains("\"verification\":\"WindowsLock\"", log[1].PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>A no is the person's whatever the action: the barrier confirms what is granted, never what is refused (ADR-0037, decision 3).</summary>
    [Fact]
    public async Task A_no_to_an_action_of_the_list_is_the_persons()
    {
        var (gate, store, _) = Gate(approved: false, ApprovalVerification.Dialog, "no");

        var decision = await gate.RequestAsync(Request(DeliveryService.ApprovalAction), TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        Assert.Equal(new Actor(ActorKind.User, "user"), store.Of(Project)[1].Actor);
        Assert.Equal("no", decision.Comment);
    }
}
