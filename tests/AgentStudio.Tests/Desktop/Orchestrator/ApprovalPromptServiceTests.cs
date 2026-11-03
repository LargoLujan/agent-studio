using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Decisions;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Desktop.Lock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// F3-7 on the desktop side: the prompt asks on the UI context and fails closed. The dialog is a double: no window is
/// shown by these tests.
/// </summary>
public sealed class ApprovalPromptServiceTests
{
    private static readonly ApprovalRequest Merge = new(
        "Fusionar en main",
        "Entregar el resultado de la ejecución 20260101-abc123 con --ff-only.",
        RiskLevel.Critical,
        Aid("orquestador"),
        ["src/Calc/Calc.cs", "tests/Calc.Tests/CalcTests.cs"],
        ProjectId: "p-1",
        RunId: "20260101-abc123",
        TaskId: "T5");

    private sealed class FakeDialog(Func<ApprovalDialogViewModel, CancellationToken, Task<ApprovalAnswer?>> answer) : IApprovalDialog
    {
        public List<ApprovalDialogViewModel> Shown { get; } = [];

        public SynchronizationContext? ShownOn { get; private set; }

        public Task<ApprovalAnswer?> ShowAsync(ApprovalDialogViewModel question, CancellationToken cancellationToken)
        {
            Shown.Add(question);
            ShownOn = SynchronizationContext.Current;
            return answer(question, cancellationToken);
        }
    }

    private static ApprovalPromptService Prompt(IApprovalDialog dialog, SynchronizationContext? context = null) =>
        new(dialog, new FakeTimeProvider(T0), context ?? new InlineSynchronizationContext());

    [Fact]
    public async Task Only_the_approve_button_approves()
    {
        var dialog = new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(true, "Revisado")));

        var decision = await Prompt(dialog).AskAsync(Merge, CancellationToken.None);

        Assert.Equal(new ApprovalDecision(true, "user", T0, "Revisado"), decision);
        Assert.Same(Merge, Assert.Single(dialog.Shown).Request);
    }

    [Fact]
    public async Task Denying_denies()
    {
        var decision = await Prompt(new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(false, null))))
            .AskAsync(Merge, CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Equal("user", decision.DecidedBy);
        Assert.Equal("Denegada.", decision.Comment);
    }

    [Fact]
    public async Task Closing_the_dialog_without_answering_denies()
    {
        var decision = await Prompt(new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(null)))
            .AskAsync(Merge, CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Equal("Se cerró sin responder: cuenta como denegada.", decision.Comment);
    }

    [Fact]
    public async Task A_cancelled_or_expired_request_gets_no_answer_in_the_persons_name_even_if_the_dialog_never_answers()
    {
        using var expiry = new CancellationTokenSource();
        var showing = new TaskCompletionSource<ApprovalAnswer?>();
        var dialog = new FakeDialog((_, _) => showing.Task);

        var asking = Prompt(dialog).AskAsync(Merge, expiry.Token);
        await expiry.CancelAsync();

        // Nobody answered: the gate denies it in its own name, with the reason.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);

        // An answer that comes too late changes nothing.
        showing.SetResult(new ApprovalAnswer(true, null));
        Assert.True(asking.IsCanceled);
    }

    [Fact]
    public async Task A_request_cancelled_before_asking_shows_no_dialog_and_gets_no_answer()
    {
        var dialog = new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(true, null)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prompt(dialog).AskAsync(Merge, new CancellationToken(canceled: true)));

        Assert.Empty(dialog.Shown);
    }

    [Fact]
    public async Task A_dialog_that_fails_gets_no_answer_and_its_failure_reaches_the_gate()
    {
        var dialog = new FakeDialog((_, _) => throw new InvalidOperationException("The window could not be created."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Prompt(dialog).AskAsync(Merge, CancellationToken.None));

        Assert.Equal("The window could not be created.", error.Message);
    }

    [Fact]
    public async Task An_expired_question_is_recorded_by_the_gate_as_the_systems_denial_and_never_in_the_persons_name()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var dialog = new FakeDialog((_, _) => new TaskCompletionSource<ApprovalAnswer?>().Task);
        var gate = new ApprovalGate(
            new ApprovalPromptService(dialog, clock, new InlineSynchronizationContext()),
            new DecisionRecorder(store, clock),
            clock,
            new ApprovalGateOptions(TimeSpan.FromMinutes(30)));

        var pending = gate.RequestAsync(Merge, TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && !pending.IsCompleted; i++)
        {
            if (dialog.Shown.Count > 0)
            {
                clock.Advance(TimeSpan.FromMinutes(30));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var decision = await pending;

        Assert.False(decision.Approved);
        var answer = store.Of("p-1")[^1];
        Assert.Equal(DecisionKind.ApprovalDenied, answer.Kind);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), answer.Actor);
        var payload = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(answer.PayloadJson);
        Assert.Equal((ApprovalGate.SystemId, "Sin respuesta en 30 min: se deniega."), (payload.DecidedBy, payload.Comment));
    }

    [Fact]
    public async Task The_question_is_shown_on_the_UI_context()
    {
        var context = new QueueSynchronizationContext();
        var dialog = new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(true, null)));
        var prompt = Prompt(dialog, context);

        var asking = Task.Run(() => prompt.AskAsync(Merge, CancellationToken.None));
        for (var i = 0; i < 400 && context.Pending == 0; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Empty(dialog.Shown);
        context.RunAll();
        var decision = await asking;

        Assert.True(decision.Approved);
        Assert.Same(context, dialog.ShownOn);
    }

    [Fact]
    public void The_question_shows_risk_action_and_paths_and_a_critical_one_needs_confirmation()
    {
        var question = new ApprovalDialogViewModel(Merge);
        var answered = 0;
        question.CloseRequested += (_, _) => answered++;

        Assert.Equal("Riesgo crítico", question.RiskLabel);
        Assert.Equal(Tone.Error, question.RiskTone);
        Assert.Equal("Fusionar en main", question.Action);
        Assert.Equal("Rutas afectadas (2)", question.PathsHeader);
        Assert.Equal(["src/Calc/Calc.cs", "tests/Calc.Tests/CalcTests.cs"], question.AffectedPaths);
        Assert.Equal("Lo pide el agente «orquestador».", question.RequestedBy);
        Assert.Equal("Origen: ejecución 20260101-abc123 · tarea T5 · proyecto p-1", question.Origin);

        Assert.False(question.ApproveCommand.CanExecute(null));
        question.IsConfirmed = true;
        Assert.True(question.ApproveCommand.CanExecute(null));

        question.ApproveCommand.Execute(null);
        question.DenyCommand.Execute(null);
        Assert.Equal(new ApprovalAnswer(true, null), question.Answer);
        Assert.Equal(2, answered);
    }

    [Fact]
    public void A_destructive_question_needs_no_confirmation()
    {
        var question = new ApprovalDialogViewModel(Merge with { Risk = RiskLevel.Destructive, AffectedPaths = [] });

        Assert.Equal("Acción destructiva", question.RiskLabel);
        Assert.True(question.ApproveCommand.CanExecute(null));
        Assert.False(question.HasPaths);
    }

    // ---- the reinforced confirmation of the critical approvals (docs/16 D-13, F8-11); Windows Hello is a double ----

    private static FakeDialog Answers(bool approved, string? comment = null) =>
        new((_, _) => Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(approved, comment)));

    private static ApprovalPromptService Prompt(IApprovalDialog dialog, FakeUserPresenceVerifier hello, Func<bool>? isOn = null, SynchronizationContext? context = null) =>
        new(dialog, new FakeTimeProvider(T0), context ?? new InlineSynchronizationContext(), new ReinforcedConfirmation(hello, isOn ?? (() => true)));

    [Fact]
    public async Task A_critical_approval_is_given_only_once_windows_hello_confirms_the_person()
    {
        var hello = FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified);
        var dialog = Answers(approved: true, "Revisado");

        var decision = await Prompt(dialog, hello).AskAsync(Merge, CancellationToken.None);

        Assert.Equal(new ApprovalDecision(true, "user", T0, "Revisado", Verification: ApprovalVerification.WindowsHello), decision);
        var question = Assert.Single(dialog.Shown);
        Assert.Equal(ApprovalVerification.WindowsHello, question.Confirmation);
        Assert.Contains("Windows Hello", question.HelloNotice, StringComparison.Ordinal);
        Assert.Equal("Agent Studio: confirma que eres tú para aprobar «Fusionar en main».", Assert.Single(hello.Messages));
    }

    /// <summary>Whatever Windows answers that is not a confirmation, the yes is not taken (the gate denies it as the system's).</summary>
    [Theory]
    [InlineData(UserPresenceResult.Canceled)]
    [InlineData(UserPresenceResult.RetriesExhausted)]
    [InlineData(UserPresenceResult.DeviceBusy)]
    [InlineData(UserPresenceResult.DeviceNotPresent)]
    [InlineData(UserPresenceResult.NotConfiguredForUser)]
    [InlineData(UserPresenceResult.DisabledByPolicy)]
    public async Task A_yes_windows_hello_does_not_confirm_is_never_an_approval(UserPresenceResult result)
    {
        var hello = FakeUserPresenceVerifier.Answering(result);

        var decision = await Prompt(Answers(approved: true, "sí"), hello).AskAsync(Merge, CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalVerification.WindowsHello, decision.Verification);
        Assert.Contains(result.ToString(), decision.Comment, StringComparison.Ordinal);
        Assert.Single(hello.Messages);
    }

    [Fact]
    public async Task A_verifier_that_fails_gets_a_denial_never_an_approval()
    {
        var hello = new FakeUserPresenceVerifier { Verify = (_, _) => throw new InvalidOperationException("The interop is not there.") };

        var decision = await Prompt(Answers(approved: true), hello).AskAsync(Merge, CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalVerification.WindowsHello, decision.Verification);
        Assert.Contains("The interop is not there.", decision.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_no_in_the_dialog_never_asks_windows_hello_and_is_the_dialogs()
    {
        var hello = new FakeUserPresenceVerifier();

        var denied = await Prompt(Answers(approved: false), hello).AskAsync(Merge, CancellationToken.None);
        var closed = await Prompt(new FakeDialog((_, _) => Task.FromResult<ApprovalAnswer?>(null)), hello).AskAsync(Merge, CancellationToken.None);

        Assert.Equal((false, ApprovalVerification.Dialog, "Denegada."), (denied.Approved, denied.Verification, denied.Comment));
        Assert.Equal((false, ApprovalVerification.Dialog), (closed.Approved, closed.Verification));
        Assert.Empty(hello.Messages);
    }

    /// <summary>
    /// «Sin Hello: no disponible» (the «Hecho cuando» of WP4): Windows says Hello is not there, so the dialog decides alone,
    /// with a warning, and the answer says so. Only these three answers of Windows let the dialog decide alone.
    /// </summary>
    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser, true)]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser, false)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent, true)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent, false)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy, true)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy, false)]
    public async Task Without_windows_hello_the_dialog_decides_alone_with_a_warning(UserPresenceAvailability availability, bool approved)
    {
        var hello = FakeUserPresenceVerifier.Without(availability);
        var dialog = Answers(approved);

        var decision = await Prompt(dialog, hello).AskAsync(Merge, CancellationToken.None);

        Assert.Equal((approved, "user", ApprovalVerification.WindowsHelloUnavailable), (decision.Approved, decision.DecidedBy, decision.Verification));
        Assert.Empty(hello.Messages);
        var question = Assert.Single(dialog.Shown);
        Assert.Equal(ApprovalVerification.WindowsHelloUnavailable, question.Confirmation);
        Assert.Equal(Tone.Warning, question.HelloNoticeTone);
        Assert.Contains("no está disponible", question.HelloNotice, StringComparison.Ordinal);
    }

    /// <summary>Busy, or Windows could not say: Hello is asked all the same, and only a confirmation approves.</summary>
    [Fact]
    public async Task When_windows_cannot_say_that_hello_is_absent_it_is_asked()
    {
        var busy = FakeUserPresenceVerifier.Without(UserPresenceAvailability.DeviceBusy);
        var unknown = new FakeUserPresenceVerifier { Availability = _ => throw new InvalidOperationException("WinRT failed.") };

        var whenBusy = await Prompt(Answers(approved: true), busy).AskAsync(Merge, CancellationToken.None);
        var whenUnknown = await Prompt(Answers(approved: true), unknown).AskAsync(Merge, CancellationToken.None);

        Assert.Equal((true, ApprovalVerification.WindowsHello), (whenBusy.Approved, whenBusy.Verification));
        Assert.Equal((true, ApprovalVerification.WindowsHello), (whenUnknown.Approved, whenUnknown.Verification));
        Assert.Single(busy.Messages);
        Assert.Single(unknown.Messages);
    }

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Destructive)]
    public async Task Windows_hello_is_not_asked_for_an_action_that_is_not_critical(RiskLevel risk)
    {
        var hello = new FakeUserPresenceVerifier();

        var decision = await Prompt(Answers(approved: true), hello).AskAsync(Merge with { Risk = risk }, CancellationToken.None);

        Assert.Equal((true, ApprovalVerification.Dialog), (decision.Approved, decision.Verification));
        Assert.Equal(0, hello.AvailabilityChecks);
        Assert.Empty(hello.Messages);
    }

    [Fact]
    public async Task The_setting_is_read_at_each_question_and_off_it_asks_nothing_of_windows()
    {
        var hello = new FakeUserPresenceVerifier();
        var on = false;
        var prompt = Prompt(Answers(approved: true), hello, () => on);

        var off = await prompt.AskAsync(Merge, CancellationToken.None);
        on = true;
        var then = await prompt.AskAsync(Merge, CancellationToken.None);

        Assert.Equal(ApprovalVerification.Dialog, off.Verification);
        Assert.Equal(ApprovalVerification.WindowsHello, then.Verification);
        Assert.Equal(1, hello.AvailabilityChecks);
        Assert.Single(hello.Messages);
    }

    [Fact]
    public async Task Withdrawing_the_question_while_windows_hello_is_asked_gets_no_answer()
    {
        using var expiry = new CancellationTokenSource();
        var asked = new TaskCompletionSource();
        var hello = new FakeUserPresenceVerifier
        {
            Verify = async (_, ct) =>
            {
                asked.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return UserPresenceResult.Verified;
            },
        };

        var asking = Prompt(Answers(approved: true), hello).AskAsync(Merge, expiry.Token);
        await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await expiry.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.True(hello.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Windows_hello_is_asked_on_the_UI_context_after_the_dialog()
    {
        var context = new QueueSynchronizationContext();
        var hello = new FakeUserPresenceVerifier();
        var prompt = Prompt(Answers(approved: true), hello, context: context);

        var asking = Task.Run(() => prompt.AskAsync(Merge, CancellationToken.None));
        for (var i = 0; i < 400 && context.Pending == 0; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Empty(hello.Messages);
        context.RunAll();
        var decision = await asking;

        Assert.Equal(ApprovalVerification.WindowsHello, decision.Verification);
        Assert.Same(context, Assert.Single(hello.VerifiedOn));
    }

    // ---- through the gate, into the log: what F8-11 asks ----

    private static (ApprovalGate Gate, InMemoryDecisionLogStore Store) GateOver(FakeUserPresenceVerifier hello, IApprovalDialog dialog)
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var prompt = new ApprovalPromptService(dialog, clock, new InlineSynchronizationContext(), new ReinforcedConfirmation(hello, () => true));
        return (new ApprovalGate(prompt, new DecisionRecorder(store, clock), clock, new ApprovalGateOptions(TimeSpan.FromMinutes(30))), store);
    }

    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy)]
    public async Task Without_windows_hello_the_person_decides_in_the_dialog_and_the_log_says_hello_was_unavailable(UserPresenceAvailability availability)
    {
        var (gate, store) = GateOver(FakeUserPresenceVerifier.Without(availability), Answers(approved: true));

        var decision = await gate.RequestAsync(Merge, TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        var answer = store.Of("p-1")[^1];
        Assert.Equal((DecisionKind.ApprovalGranted, new Actor(ActorKind.User, "user")), (answer.Kind, answer.Actor));
        var payload = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(answer.PayloadJson);
        Assert.Equal((2, ApprovalVerification.WindowsHelloUnavailable), (payload.V, payload.Verification));
    }

    [Fact]
    public async Task With_windows_hello_the_log_says_it_confirmed_the_person()
    {
        var (gate, store) = GateOver(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified), Answers(approved: true));

        var decision = await gate.RequestAsync(Merge, TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        var answer = store.Of("p-1")[^1];
        Assert.Equal((DecisionKind.ApprovalGranted, new Actor(ActorKind.User, "user")), (answer.Kind, answer.Actor));
        var payload = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(answer.PayloadJson);
        Assert.Equal((2, ApprovalVerification.WindowsHello), (payload.V, payload.Verification));
    }

    /// <summary>
    /// Windows never says whether Hello is there: past the time limit it «could not say», so Hello is asked (and only its
    /// confirmation approves), well within the time the gate gives the question.
    /// </summary>
    [Fact]
    public async Task When_windows_never_says_whether_hello_is_there_hello_is_asked_after_the_time_limit_within_the_gates_time()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var hello = new FakeUserPresenceVerifier { Availability = _ => new TaskCompletionSource<UserPresenceAvailability>().Task };
        var dialog = Answers(approved: true, "sí");
        var prompt = new ApprovalPromptService(dialog, clock, new InlineSynchronizationContext(), new ReinforcedConfirmation(hello, () => true, clock));
        var gate = new ApprovalGate(prompt, new DecisionRecorder(store, clock), clock, new ApprovalGateOptions(TimeSpan.FromMinutes(30)));

        var pending = gate.RequestAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => hello.AvailabilityChecks == 1);
        Assert.Empty(dialog.Shown);
        clock.Advance(HelloAnswers.AvailabilityTimeout);
        var decision = await pending.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal((true, ApprovalVerification.WindowsHello), (decision.Approved, decision.Verification));
        Assert.Equal(ApprovalVerification.WindowsHello, Assert.Single(dialog.Shown).Confirmation);
        Assert.Single(hello.Messages);
        var answer = store.Of("p-1")[^1];
        Assert.Equal((DecisionKind.ApprovalGranted, new Actor(ActorKind.User, "user")), (answer.Kind, answer.Actor));
        Assert.Equal(ApprovalVerification.WindowsHello, DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(answer.PayloadJson).Verification);
    }

    [Fact]
    public async Task A_yes_windows_hello_did_not_confirm_is_denied_in_the_systems_name_with_the_reason()
    {
        var (gate, store) = GateOver(FakeUserPresenceVerifier.Answering(UserPresenceResult.Canceled), Answers(approved: true));

        var decision = await gate.RequestAsync(Merge, TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        var answer = store.Of("p-1")[^1];
        Assert.Equal((DecisionKind.ApprovalDenied, new Actor(ActorKind.System, ApprovalGate.SystemId)), (answer.Kind, answer.Actor));
        var payload = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(answer.PayloadJson);
        Assert.Equal((2, ApprovalVerification.WindowsHello, ApprovalGate.SystemId), (payload.V, payload.Verification, payload.DecidedBy));
        Assert.Contains("Canceled", payload.Comment, StringComparison.Ordinal);
    }

    // ---- the lock of the interface (docs/16 D-13, F8-10): the questions wait for the unlock ----

    private static (ApprovalPromptService Prompt, InterfaceLock Lock) Locked(IApprovalDialog dialog)
    {
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        return (new ApprovalPromptService(dialog, new FakeTimeProvider(T0), new InlineSynchronizationContext(), interfaceLock: interfaceLock), interfaceLock);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition never held.");
    }

    [Fact]
    public async Task While_the_interface_is_locked_the_question_waits_and_is_shown_once_unlocked()
    {
        var dialog = Answers(approved: true, "ok");
        var (prompt, interfaceLock) = Locked(dialog);
        await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);

        var asking = prompt.AskAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);

        Assert.Empty(dialog.Shown);
        Assert.Equal(("Fusionar en main", RiskLevel.Critical), (interfaceLock.Waiting[0].Action, interfaceLock.Waiting[0].Risk));

        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        var decision = await asking.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        Assert.Single(dialog.Shown);
        Assert.Empty(interfaceLock.Waiting);
    }

    /// <summary>The approval window is a window of its own, which the lock screen does not cover: it closes, and the question comes back after the unlock.</summary>
    [Fact]
    public async Task A_question_open_when_the_interface_locks_is_withdrawn_and_asked_again_once_unlocked()
    {
        var tokens = new List<CancellationToken>();
        var dialog = new FakeDialog((_, ct) =>
        {
            tokens.Add(ct);
            if (tokens.Count == 1)
            {
                // The first window stays open until it is closed by its token (the lock), as WpfApprovalDialog does.
                var closed = new TaskCompletionSource<ApprovalAnswer?>();
                ct.Register(() => closed.TrySetResult(null));
                return closed.Task;
            }

            return Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(true, "después"));
        });
        var (prompt, interfaceLock) = Locked(dialog);

        var asking = prompt.AskAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => dialog.Shown.Count == 1);
        await interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);

        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(asking.IsCompleted);

        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        var decision = await asking.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal((true, "después"), (decision.Approved, decision.Comment));
        Assert.Equal(2, dialog.Shown.Count);
        Assert.NotSame(dialog.Shown[0], dialog.Shown[1]);
    }

    [Fact]
    public async Task An_answer_given_as_the_interface_locks_is_not_taken()
    {
        var first = new TaskCompletionSource<ApprovalAnswer?>();
        var shown = 0;
        var dialog = new FakeDialog((_, _) => ++shown == 1 ? first.Task : Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(false, "no")));
        var (prompt, interfaceLock) = Locked(dialog);

        var asking = prompt.AskAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => dialog.Shown.Count == 1);
        await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);

        // A yes from a window that ignored the lock: it is not the answer.
        first.SetResult(new ApprovalAnswer(true, "a la vez que se bloqueaba"));
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);
        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        var decision = await asking.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal((false, "no"), (decision.Approved, decision.Comment));
        Assert.Equal(2, dialog.Shown.Count);
    }

    [Fact]
    public async Task A_question_withdrawn_while_it_waits_for_the_unlock_gets_no_answer_and_leaves_the_lock_screen()
    {
        var dialog = Answers(approved: true);
        var (prompt, interfaceLock) = Locked(dialog);
        await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);
        using var withdrawn = new CancellationTokenSource();

        var asking = prompt.AskAsync(Merge, withdrawn.Token);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);
        await withdrawn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Empty(interfaceLock.Waiting);
        Assert.Empty(dialog.Shown);
    }

    /// <summary>The question was on its way to the UI thread when the interface locked: it is shown after the unlock, and never before.</summary>
    [Fact]
    public async Task A_lock_that_lands_between_posting_the_question_and_showing_it_opens_no_window()
    {
        var context = new QueueSynchronizationContext();
        var dialog = Answers(approved: true, "después");
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        var prompt = new ApprovalPromptService(dialog, new FakeTimeProvider(T0), context, interfaceLock: interfaceLock);

        var asking = Task.Run(() => prompt.AskAsync(Merge, CancellationToken.None), TestContext.Current.CancellationToken);
        await UntilAsync(() => context.Pending > 0);
        await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);
        context.RunAll();

        Assert.Empty(dialog.Shown);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);

        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => context.Pending > 0);
        context.RunAll();
        var decision = await asking.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal((true, "después"), (decision.Approved, decision.Comment));
        Assert.Single(dialog.Shown);
    }

    /// <summary>The interface locked while Windows Hello was asked: the question is withdrawn with it, and asked again, Hello and all, after the unlock.</summary>
    [Fact]
    public async Task A_lock_while_windows_hello_is_asked_withdraws_the_question_and_asks_it_again_once_unlocked()
    {
        var asked = 0;
        var first = CancellationToken.None;
        var hello = new FakeUserPresenceVerifier
        {
            Verify = async (_, ct) =>
            {
                if (Interlocked.Increment(ref asked) == 1)
                {
                    first = ct;
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return UserPresenceResult.Verified;
            },
        };
        var dialog = Answers(approved: true, "sí");
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        var prompt = new ApprovalPromptService(dialog, new FakeTimeProvider(T0), new InlineSynchronizationContext(), new ReinforcedConfirmation(hello, () => true), interfaceLock);

        var asking = prompt.AskAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => Volatile.Read(ref asked) == 1);
        await interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);

        Assert.True(first.IsCancellationRequested);
        Assert.False(asking.IsCompleted);

        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        var decision = await asking.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal((true, ApprovalVerification.WindowsHello), (decision.Approved, decision.Verification));
        Assert.Equal(2, dialog.Shown.Count);
        Assert.Equal(2, hello.Messages.Count);
    }

    /// <summary>The lock of the interface as the questions see it, scripted: each wait returns the next period, the first one maybe over already.</summary>
    private sealed class ScriptedPeriods(params CancellationToken[] periods) : IInterfaceLock
    {
        private int _calls;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsLocked => false;

        public int Calls => _calls;

        public Task<CancellationToken> WaitUntilUnlockedAsync(ApprovalRequest question, CancellationToken cancellationToken) =>
            Task.FromResult(periods[Math.Min(_calls++, periods.Length - 1)]);
    }

    /// <summary>The interface locked right after the wait returned its period: the question links to a token already cancelled, opens no window and waits again.</summary>
    [Fact]
    public async Task A_period_already_over_when_the_question_links_to_it_opens_no_window_and_the_question_waits_again()
    {
        using var over = new CancellationTokenSource();
        await over.CancelAsync();
        var periods = new ScriptedPeriods(over.Token, CancellationToken.None);
        var dialog = Answers(approved: true);
        var prompt = new ApprovalPromptService(dialog, new FakeTimeProvider(T0), new InlineSynchronizationContext(), interfaceLock: periods);

        var decision = await prompt.AskAsync(Merge, TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        Assert.Single(dialog.Shown);
        Assert.Equal(2, periods.Calls);
    }

    /// <summary>
    /// ADR-0037: the gate's time to answer keeps running while the interface is locked. A question nobody answers in time is
    /// the system's denial, as with an unlocked interface; the run asks again when it needs to.
    /// </summary>
    [Fact]
    public async Task A_question_that_waits_for_the_unlock_longer_than_the_gate_allows_is_the_systems_denial()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var dialog = Answers(approved: true);
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), clock);
        var prompt = new ApprovalPromptService(dialog, clock, new InlineSynchronizationContext(), interfaceLock: interfaceLock);
        var gate = new ApprovalGate(prompt, new DecisionRecorder(store, clock), clock, new ApprovalGateOptions(TimeSpan.FromMinutes(30)));
        await interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);

        var pending = gate.RequestAsync(Merge, TestContext.Current.CancellationToken);
        await UntilAsync(() => interfaceLock.Waiting.Count == 1);
        for (var i = 0; i < 200 && !pending.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var decision = await pending;

        Assert.False(decision.Approved);
        Assert.Empty(dialog.Shown);
        Assert.Empty(interfaceLock.Waiting);
        var answer = store.Of("p-1")[^1];
        Assert.Equal((DecisionKind.ApprovalDenied, new Actor(ActorKind.System, ApprovalGate.SystemId)), (answer.Kind, answer.Actor));
    }

    // ---- what the question says of Windows Hello ----

    [Fact]
    public void A_question_without_windows_hello_says_nothing_of_it()
    {
        var question = new ApprovalDialogViewModel(Merge);

        Assert.Equal(ApprovalVerification.Dialog, question.Confirmation);
        Assert.Null(question.HelloNotice);
        Assert.False(question.HasHelloNotice);
    }

    [Fact]
    public void A_question_with_windows_hello_says_it_is_asked_after_approving()
    {
        var question = new ApprovalDialogViewModel(Merge, ApprovalVerification.WindowsHello, UserPresenceAvailability.Available);

        Assert.True(question.HasHelloNotice);
        Assert.Equal(Tone.Neutral, question.HelloNoticeTone);
        Assert.Equal("Al aprobar, Windows Hello te pedirá que confirmes que eres tú. Sin esa confirmación no se aprueba nada.", question.HelloNotice);
    }

    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser, "no está configurado para tu usuario")]
    [InlineData(UserPresenceAvailability.DeviceNotPresent, "no hay ningún dispositivo con el que verificarte")]
    [InlineData(UserPresenceAvailability.DisabledByPolicy, "lo desactiva una directiva")]
    public void A_question_without_windows_hello_on_this_computer_warns_and_says_why(UserPresenceAvailability availability, string why)
    {
        var question = new ApprovalDialogViewModel(Merge, ApprovalVerification.WindowsHelloUnavailable, availability);

        Assert.Equal(Tone.Warning, question.HelloNoticeTone);
        Assert.Equal(
            $"Windows Hello no está disponible en este equipo ({why}): esta aprobación la decide solo este diálogo, y el registro lo dice.",
            question.HelloNotice);
    }
}
