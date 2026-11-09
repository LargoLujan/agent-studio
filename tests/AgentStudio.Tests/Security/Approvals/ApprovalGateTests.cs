using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Security.Approvals;

public sealed class ApprovalGateTests
{
    private const string Project = "p-approvals";
    private const string Run = "20261015-a1b2c3";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>The person behind the dialog, scripted. It remembers what it was shown and the token it got.</summary>
    private sealed class ScriptedPrompt(Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> answer) : IApprovalPrompt
    {
        public List<ApprovalRequest> Asked { get; } = [];

        public CancellationToken Token { get; private set; }

        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            Token = cancellationToken;
            return answer(request, cancellationToken);
        }

        public static ScriptedPrompt Says(bool approved, string decidedBy = "manuel", string? comment = "ok") =>
            new((_, _) => Task.FromResult(new ApprovalDecision(approved, decidedBy, T0, comment)));
    }

    private sealed class Harness
    {
        public Harness(ScriptedPrompt prompt)
        {
            Prompt = prompt;
            Recorder = new DecisionRecorder(Store, Clock);
            Gate = new ApprovalGate(prompt, Recorder, Clock, new ApprovalGateOptions(Timeout));
        }

        public ScriptedPrompt Prompt { get; }

        public InMemoryDecisionLogStore Store { get; } = new();

        public FakeTimeProvider Clock { get; } = new(T0);

        public DecisionRecorder Recorder { get; }

        public ApprovalGate Gate { get; }

        public IReadOnlyList<DecisionEntry> Log => Store.Of(Project);
    }

    private static ApprovalRequest Request(RiskLevel risk = RiskLevel.Destructive, string action = "discard-run-work", string? project = Project) =>
        new(action, "Quitar el worktree T1 con cambios sin commit", risk, null, ["C:\\wt\\T1"], project, Run, "T1");

    private static async Task<ApprovalDecision> AskAsync(Harness harness, ApprovalRequest? request = null, CancellationToken? cancellationToken = null) =>
        await harness.Gate.RequestAsync(request ?? Request(), cancellationToken ?? TestContext.Current.CancellationToken);

    private static T Payload<T>(DecisionEntry entry)
        where T : DecisionPayload => DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);

    /// <summary>Lets the clock run until the pending request ends (the gate's timers fire when the fake clock moves).</summary>
    private static async Task<ApprovalDecision> AdvanceUntilAnsweredAsync(Harness harness, Task<ApprovalDecision> pending)
    {
        for (var i = 0; i < 200 && !pending.IsCompleted; i++)
        {
            if (harness.Prompt.Asked.Count > 0)
            {
                harness.Clock.Advance(Timeout);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(pending.IsCompleted, "The gate never answered.");
        return await pending;
    }

    [Theory]
    [InlineData(RiskLevel.Destructive)]
    [InlineData(RiskLevel.Critical)]
    public async Task A_destructive_or_critical_action_always_asks_a_person(RiskLevel risk)
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));

        var decision = await AskAsync(harness, Request(risk));

        Assert.True(decision.Approved);
        Assert.Equal("manuel", decision.DecidedBy);
        Assert.Single(harness.Prompt.Asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_decision_answers_the_binding_of_its_request_whatever_the_prompt_says(bool approved)
    {
        // A delivery is on the list of the fixed barrier: its yes counts only confirmed (docs/17 D-37).
        var harness = new Harness(new ScriptedPrompt((_, _) => Task.FromResult(new ApprovalDecision(approved, "manuel", T0, null, Answers: "otra-peticion", Verification: ApprovalVerification.WindowsHello))));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "deliver-integration") with { Binding = "delivery-v1:la-peticion" });

        Assert.Equal(approved, decision.Approved);
        Assert.Equal("delivery-v1:la-peticion", decision.Answers);
        Assert.Null((await AskAsync(harness, Request())).Answers);
    }

    [Fact]
    public async Task Even_a_low_risk_request_is_never_approved_by_the_gate_itself()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: false));

        var decision = await AskAsync(harness, Request(RiskLevel.Low, action: "approve-plan"));

        Assert.False(decision.Approved);
        Assert.Single(harness.Prompt.Asked);
    }

    [Fact]
    public async Task The_request_and_then_the_answer_go_to_the_decision_log_in_that_order()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true, comment: "adelante"));

        await AskAsync(harness);

        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.System, "agent-studio"), harness.Log[0].Actor);
        var asked = Payload<ApprovalRequestedPayload>(harness.Log[0]);
        Assert.Equal(("discard-run-work", RiskLevel.Destructive, Tid("T1"), Run), (asked.Action, asked.Risk, asked.Task!.Value, asked.RunId));
        Assert.Equal(["C:\\wt\\T1"], asked.AffectedPaths);
        Assert.Equal(new Actor(ActorKind.User, "manuel"), harness.Log[1].Actor);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[1]);
        Assert.Equal(("discard-run-work", true, "manuel", "adelante", Run), (answered.Action, answered.Approved, answered.DecidedBy, answered.Comment, answered.RunId));
        Assert.True((await harness.Recorder.LoadAsync(Project, TestContext.Current.CancellationToken)).Verification.IsValid);
    }

    [Fact]
    public async Task A_denial_is_recorded_as_the_persons_answer()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: false, comment: "todavía no"));

        var decision = await AskAsync(harness);

        Assert.False(decision.Approved);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.User, "manuel"), harness.Log[1].Actor);
        Assert.Equal("todavía no", Payload<ApprovalAnsweredPayload>(harness.Log[1]).Comment);
    }

    [Fact]
    public async Task No_answer_in_time_is_a_denial()
    {
        var harness = new Harness(new ScriptedPrompt(async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new ApprovalDecision(true, "manuel", T0, null);
        }));

        var decision = await AdvanceUntilAnsweredAsync(harness, harness.Gate.RequestAsync(Request(), TestContext.Current.CancellationToken));

        Assert.False(decision.Approved);
        Assert.True(harness.Prompt.Token.IsCancellationRequested);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.System, "agent-studio"), harness.Log[1].Actor);
        Assert.Contains("30", Payload<ApprovalAnsweredPayload>(harness.Log[1]).Comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_that_ignores_cancellation_still_runs_out_of_time()
    {
        var never = new TaskCompletionSource<ApprovalDecision>();
        var harness = new Harness(new ScriptedPrompt((_, _) => never.Task));

        var decision = await AdvanceUntilAnsweredAsync(harness, harness.Gate.RequestAsync(Request(), TestContext.Current.CancellationToken));

        Assert.False(decision.Approved);
        Assert.Equal(DecisionKind.ApprovalDenied, harness.Log[^1].Kind);
        never.SetResult(new ApprovalDecision(true, "manuel", T0, "demasiado tarde"));
        Assert.Equal(2, harness.Log.Count);
    }

    [Fact]
    public async Task A_denial_the_prompt_gives_once_the_question_expired_is_recorded_as_the_systems_with_the_reason()
    {
        // The dialog closes when the deadline cancels its token, and this prompt answers "no" in the person's name then.
        var harness = new Harness(new ScriptedPrompt((_, ct) =>
        {
            var closed = new TaskCompletionSource<ApprovalDecision>();
            ct.Register(() => closed.TrySetResult(new ApprovalDecision(false, "manuel", T0, "se cerró al caducar")));
            return closed.Task;
        }));

        var decision = await AdvanceUntilAnsweredAsync(harness, harness.Gate.RequestAsync(Request(), TestContext.Current.CancellationToken));

        Assert.False(decision.Approved);
        Assert.Equal("agent-studio", decision.DecidedBy);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.System, "agent-studio"), harness.Log[1].Actor);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[1]);
        Assert.Equal("agent-studio", answered.DecidedBy);
        Assert.Equal("Sin respuesta en 30 min: se deniega.", answered.Comment);
    }

    [Fact]
    public async Task Cancelling_the_request_is_a_denial_and_not_an_exception()
    {
        var harness = new Harness(new ScriptedPrompt(async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new ApprovalDecision(true, "manuel", T0, null);
        }));
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Gate.RequestAsync(Request(), cancellation.Token);
        for (var i = 0; i < 200 && harness.Prompt.Asked.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cancellation.CancelAsync();
        var decision = await pending;

        Assert.False(decision.Approved);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_prompt_that_fails_is_a_denial()
    {
        var harness = new Harness(new ScriptedPrompt((_, _) => throw new InvalidOperationException("no window")));

        var decision = await AskAsync(harness);

        Assert.False(decision.Approved);
        Assert.Equal(DecisionKind.ApprovalDenied, harness.Log[^1].Kind);
        Assert.Contains("no window", Payload<ApprovalAnsweredPayload>(harness.Log[^1]).Comment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_approval_that_does_not_say_who_gave_it_is_a_denial(string decidedBy)
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true, decidedBy: decidedBy));

        var decision = await AskAsync(harness);

        Assert.False(decision.Approved);
        Assert.Equal(DecisionKind.ApprovalDenied, harness.Log[^1].Kind);
    }

    [Fact]
    public async Task Without_a_project_to_record_the_decision_in_nobody_is_asked_and_the_answer_is_no()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));

        var decision = await AskAsync(harness, Request(project: null));

        Assert.False(decision.Approved);
        Assert.Empty(harness.Prompt.Asked);
        Assert.Empty(harness.Log);
    }

    [Fact]
    public async Task If_the_request_cannot_be_recorded_nobody_is_asked_and_the_answer_is_no()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));
        harness.Store.FailBeforeStoring = 1;

        var decision = await AskAsync(harness);

        Assert.False(decision.Approved);
        Assert.Empty(harness.Prompt.Asked);
    }

    [Fact]
    public async Task An_approval_that_cannot_be_recorded_is_a_denial()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));
        harness.Store.FailingAppends.Add(2);

        var decision = await AskAsync(harness);

        Assert.False(decision.Approved);
        Assert.Single(harness.Prompt.Asked);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
    }

    [Fact]
    public async Task The_person_is_shown_and_the_log_records_the_risk_the_gate_classified()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));

        await AskAsync(harness, Request(RiskLevel.Low, action: "delete-ref"));

        Assert.Equal(RiskLevel.Destructive, Assert.Single(harness.Prompt.Asked).Risk);
        Assert.Equal(RiskLevel.Destructive, Payload<ApprovalRequestedPayload>(harness.Log[0]).Risk);
    }

    [Fact]
    public async Task An_agent_that_asks_is_the_actor_of_the_request()
    {
        var harness = new Harness(ScriptedPrompt.Says(approved: true));

        await AskAsync(harness, Request() with { RequestedBy = new AgentId("ejecutor") });

        Assert.Equal(new Actor(ActorKind.Agent, "ejecutor"), harness.Log[0].Actor);
    }

    // ---- how the person was confirmed (docs/16 D-13, F8-11): ApprovalGranted and ApprovalDenied version 2 ----

    private static ScriptedPrompt Confirmed(bool approved, ApprovalVerification verification, string decidedBy = "user", string? comment = "ok") =>
        new((_, _) => Task.FromResult(new ApprovalDecision(approved, decidedBy, T0, comment, Verification: verification)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_the_dialog_gave_alone_is_still_recorded_as_version_1(bool approved)
    {
        var harness = new Harness(Confirmed(approved, ApprovalVerification.Dialog, comment: "adelante"));

        // A critical action that is configurable (docs/17 D-37): the dialog alone may decide it.
        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "debate.open"));

        Assert.Equal(ApprovalVerification.Dialog, decision.Verification);
        var answer = harness.Log[1];
        Assert.Equal(new Actor(ActorKind.User, "user"), answer.Actor);

        // The same text as before Phase 8, byte for byte: a version 1 answer is one the dialog gave.
        var v1 = DecisionPayloadSerializer.Serialize(new ApprovalAnsweredPayload("debate.open", approved, "user", "adelante", Tid("T1")) { RunId = Run });
        Assert.Equal(v1, answer.PayloadJson);
        var read = Payload<ApprovalAnsweredPayload>(answer);
        Assert.Equal(1, read.V);
        Assert.Null(read.Verification);
        Assert.Equal(ApprovalVerification.Dialog, read.ResolvedVerification());
    }

    [Fact]
    public async Task An_approval_windows_hello_confirmed_is_the_persons_and_records_the_method()
    {
        var harness = new Harness(Confirmed(approved: true, ApprovalVerification.WindowsHello));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "deliver-integration"));

        Assert.True(decision.Approved);
        Assert.Equal(("user", ApprovalVerification.WindowsHello), (decision.DecidedBy, decision.Verification));
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.User, "user"), harness.Log[1].Actor);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[1]);
        Assert.Equal((2, ApprovalVerification.WindowsHello), (answered.V, answered.Verification));
        Assert.True(answered.RecordsVerification());
        Assert.Equal(
            "{\"action\":\"deliver-integration\",\"approved\":true,\"decidedBy\":\"user\",\"comment\":\"ok\",\"task\":\"T1\",\"verification\":\"WindowsHello\",\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            harness.Log[1].PayloadJson);
        Assert.True((await harness.Recorder.LoadAsync(Project, TestContext.Current.CancellationToken)).Verification.IsValid);
    }

    /// <summary>
    /// «Sin Hello: no disponible»: the dialog decided alone, with a warning, and the answer (yes or no) is the person's. Only for
    /// what is configurable (docs/17 D-37): an action of the list never takes such a yes (<see cref="PresenceBarrierGateTests"/>).
    /// </summary>
    [Theory]
    [InlineData(true, DecisionKind.ApprovalGranted)]
    [InlineData(false, DecisionKind.ApprovalDenied)]
    public async Task An_answer_the_dialog_gave_alone_because_windows_hello_is_unavailable_records_it(bool approved, DecisionKind kind)
    {
        var harness = new Harness(Confirmed(approved, ApprovalVerification.WindowsHelloUnavailable));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "debate.open"));

        Assert.Equal((approved, "user", ApprovalVerification.WindowsHelloUnavailable), (decision.Approved, decision.DecidedBy, decision.Verification));
        Assert.Equal(kind, harness.Log[1].Kind);
        Assert.Equal(new Actor(ActorKind.User, "user"), harness.Log[1].Actor);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[1]);
        Assert.Equal((2, ApprovalVerification.WindowsHelloUnavailable), (answered.V, answered.Verification));
    }

    /// <summary>
    /// A yes in the dialog that Windows Hello did not confirm (the prompt answers it as a denial with the method
    /// <see cref="ApprovalVerification.WindowsHello"/>) is no decision of the person: the system denies it, with the reason.
    /// A person's no to opening a revision debate drops the challenge (<c>DeliveryService</c>); this one must not.
    /// </summary>
    [Fact]
    public async Task A_yes_windows_hello_did_not_confirm_is_the_systems_denial_with_the_reason()
    {
        const string Reason = "Windows Hello no confirmó que eres tú (Canceled: cancelaste la verificación): se deniega.";
        var harness = new Harness(Confirmed(approved: false, ApprovalVerification.WindowsHello, comment: Reason));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "debate.open"));

        Assert.False(decision.Approved);
        Assert.Equal((ApprovalGate.SystemId, Reason, ApprovalVerification.WindowsHello), (decision.DecidedBy, decision.Comment, decision.Verification));
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalDenied], harness.Log.Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), harness.Log[1].Actor);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[1]);
        Assert.Equal((ApprovalGate.SystemId, Reason, 2, ApprovalVerification.WindowsHello), (answered.DecidedBy, answered.Comment, answered.V, answered.Verification));
    }

    [Fact]
    public async Task A_yes_windows_hello_did_not_confirm_without_a_reason_still_says_why()
    {
        var harness = new Harness(Confirmed(approved: false, ApprovalVerification.WindowsHello, comment: null));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "deliver-integration"));

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalGate.SystemId, decision.DecidedBy);
        Assert.Contains("Windows Hello", decision.Comment, StringComparison.Ordinal);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), harness.Log[1].Actor);
    }

    /// <summary>The denials the gate decides by itself for its own reasons (no answer, the prompt failed...) say nothing of a confirmation.</summary>
    [Fact]
    public async Task The_gates_own_denials_record_no_method_and_stay_version_1()
    {
        var harness = new Harness(new ScriptedPrompt((_, _) => throw new InvalidOperationException("no window")));

        var decision = await AskAsync(harness, Request(RiskLevel.Critical, action: "deliver-integration"));

        Assert.Equal(ApprovalVerification.Dialog, decision.Verification);
        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[^1]);
        Assert.Equal((1, (ApprovalVerification?)null), (answered.V, answered.Verification));
    }

    /// <summary>The recorded version follows the method, whatever the answer: a method is always version 2, and none is version 1.</summary>
    [Theory]
    [InlineData(true, ApprovalVerification.Dialog)]
    [InlineData(false, ApprovalVerification.Dialog)]
    [InlineData(true, ApprovalVerification.WindowsHello)]
    [InlineData(false, ApprovalVerification.WindowsHello)]
    [InlineData(true, ApprovalVerification.WindowsHelloUnavailable)]
    [InlineData(false, ApprovalVerification.WindowsHelloUnavailable)]
    public async Task Every_answer_recorded_with_a_method_is_version_2_and_without_one_version_1(bool approved, ApprovalVerification verification)
    {
        var harness = new Harness(Confirmed(approved, verification));

        await AskAsync(harness, Request(RiskLevel.Critical, action: "deliver-integration"));

        var answered = Payload<ApprovalAnsweredPayload>(harness.Log[^1]);
        Assert.Equal(answered.Verification is null ? 1 : ApprovalAnsweredPayload.VerificationVersion, answered.V);
        Assert.Equal(verification, answered.ResolvedVerification());
        Assert.Equal(verification != ApprovalVerification.Dialog, answered.RecordsVerification());
    }
}
