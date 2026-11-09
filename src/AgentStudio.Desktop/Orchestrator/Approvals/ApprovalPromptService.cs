using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;

namespace AgentStudio.Desktop.Orchestrator.Approvals;

/// <summary>
/// Shows an approval question to a person and returns the answer; null when the dialog closed without one. In the app
/// it is <see cref="WpfApprovalDialog"/>, a themed modal window; tests use a double, so no window is ever shown by them.
/// </summary>
public interface IApprovalDialog
{
    Task<ApprovalAnswer?> ShowAsync(ApprovalDialogViewModel question, CancellationToken cancellationToken);
}

/// <summary>
/// How a yes to one question is confirmed, decided before it is asked (docs/16 D-13): by the answer alone, by Windows Hello
/// after a yes, or by the answer alone with a warning because Windows says Hello is not there.
/// </summary>
/// <param name="Confirmation">
/// <see cref="ApprovalVerification.Dialog"/> (not critical, or Hello off), <see cref="ApprovalVerification.WindowsHello"/>
/// (Hello after a yes) or <see cref="ApprovalVerification.WindowsHelloUnavailable"/> (the answer alone, with a warning).
/// </param>
/// <param name="Availability">What Windows said of Windows Hello; null when it was not asked or could not say.</param>
public sealed record ConfirmationPlan(ApprovalVerification Confirmation, UserPresenceAvailability? Availability = null)
{
    /// <summary>The answer alone, as before Phase 8: the question is not critical, or Windows Hello is off.</summary>
    public static ConfirmationPlan AnswerAlone { get; } = new(ApprovalVerification.Dialog);

    /// <summary>Whether a yes is confirmed with Windows Hello.</summary>
    public bool AsksHello => Confirmation == ApprovalVerification.WindowsHello;

    /// <summary>The method a no carries. A no never asks Hello: with Hello to be asked after a yes, a no is the answer's alone.</summary>
    public ApprovalVerification ForNo => AsksHello ? ApprovalVerification.Dialog : Confirmation;
}

/// <summary>What came of asking Windows Hello after a yes: its result, or why it could not be asked.</summary>
public sealed record HelloOutcome(UserPresenceResult? Result, string? Problem = null)
{
    /// <summary>Only <see cref="UserPresenceResult.Verified"/> confirms the person.</summary>
    public bool Confirmed => Result == UserPresenceResult.Verified;
}

/// <summary>
/// The reinforced confirmation of the critical approvals (docs/16 D-13, F8-11; ADR-0037): who asks Windows Hello, whether the
/// user has it on (<c>AppSettings.WindowsHelloForImportantApprovals</c>, read at each question), and the one rule both the
/// questions of the approval gate (<see cref="ApprovalPromptService"/>) and «Aprobar plan» on the canvas follow, so they
/// cannot drift apart: <see cref="PlanAsync"/> before the question and <see cref="ConfirmAsync"/> after a yes.
/// </summary>
/// <param name="Verifier">Asks Windows to confirm the person; the app's is <c>WindowsHelloVerifier</c>.</param>
/// <param name="IsOn">Whether critical approvals ask Windows Hello now.</param>
/// <param name="Time">The clock of the time limit of <see cref="HelloAnswers.AvailabilityAsync"/>; the system's by default.</param>
public sealed record ReinforcedConfirmation(IUserPresenceVerifier Verifier, Func<bool> IsOn, TimeProvider? Time = null)
{
    /// <summary>
    /// How a yes to a question of <paramref name="risk"/> is confirmed. Not critical, or Hello off: by the answer alone. A
    /// definite «not there» of Windows (<see cref="HelloAnswers.IsAbsent"/>): by the answer alone, with a warning. Anything
    /// else (available, busy, Windows could not say, or did not answer in <see cref="HelloAnswers.AvailabilityTimeout"/>): by
    /// Windows Hello after a yes. Nothing is shown.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ConfirmationPlan> PlanAsync(RiskLevel risk, CancellationToken cancellationToken)
    {
        if (risk != RiskLevel.Critical || !IsOn())
        {
            return ConfirmationPlan.AnswerAlone;
        }

        var availability = await HelloAnswers.AvailabilityAsync(Verifier, Time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        return availability is { } said && HelloAnswers.IsAbsent(said)
            ? new ConfirmationPlan(ApprovalVerification.WindowsHelloUnavailable, said)
            : new ConfirmationPlan(ApprovalVerification.WindowsHello, availability);
    }

    /// <summary>
    /// After a yes, Windows Hello over the window of the app, with <paramref name="message"/>: only
    /// <see cref="HelloOutcome.Confirmed"/> takes the yes. When Windows cannot be asked, the outcome says why.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the question was withdrawn).</exception>
    public async Task<HelloOutcome> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Verifier.VerifyAsync(message, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new HelloOutcome(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Windows Hello could not be asked: the yes is not taken, and the outcome says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new HelloOutcome(null, ex.Message);
        }
    }
}

/// <summary>
/// The desktop side of <see cref="IApprovalGate"/> (F3-7): asks the person on the UI thread, whatever thread the gate
/// calls from, and fails closed. Only the «Aprobar» button approves; «Denegar» and closing the window are the person's
/// denial.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A request cancelled or expired before the answer, and a dialog that cannot be shown, get no answer at all: the
/// cancellation or the failure reaches the gate, which denies in its own name and records why. Answering "no" in the
/// person's name would put in the log a decision nobody took.</item>
/// <item>With a <see cref="ReinforcedConfirmation"/> that is on, a critical question (the level the gate classified) asks
/// Windows Hello after «Aprobar» (docs/16 D-13, F8-11), by the rule of <see cref="ReinforcedConfirmation.PlanAsync"/>, the one
/// «Aprobar plan» follows too. Only a definite answer of Windows that Hello is not there (not set up, no device, a policy:
/// <see cref="HelloAnswers.IsAbsent"/>) lets the dialog decide alone, with a warning, and the answer says
/// <see cref="ApprovalVerification.WindowsHelloUnavailable"/>. Anything else (busy, Windows could not say, or did not answer
/// in time) asks Hello, and only <see cref="UserPresenceResult.Verified"/> approves, with
/// <see cref="ApprovalVerification.WindowsHello"/>. A yes Hello did not confirm, or could not be asked to, is returned as a
/// denial with that method and the reason, which the gate records as the system's (see <c>ApprovalGate</c>). A no in the
/// dialog never asks Hello: it is the dialog's.</item>
/// <item>With an <see cref="IInterfaceLock"/>, a question waits while the interface is locked, and one open when it locks
/// is withdrawn (its window closes) and asked again once it is unlocked: nothing is answered on a locked interface, an answer
/// given as it locked is not taken, and a question the lock caught on its way to the UI thread opens no window. The gate's
/// time to answer keeps running meanwhile (ADR-0037).</item>
/// </list>
/// </remarks>
public sealed class ApprovalPromptService : IApprovalPrompt
{
    /// <summary>Who decided, as recorded by the gate: the person at the desktop.</summary>
    public const string DecidedBy = "user";

    private readonly IApprovalDialog _dialog;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly ReinforcedConfirmation? _confirmation;
    private readonly IInterfaceLock? _lock;
    private readonly Func<string, string?>? _projectNames;

    /// <param name="uiContext">The UI thread's context; defaults to the creating thread's context.</param>
    /// <param name="confirmation">Windows Hello for the critical approvals; without it, the dialog alone decides, as before Phase 8.</param>
    /// <param name="interfaceLock">The lock of the interface the questions wait on; without it, they never wait.</param>
    /// <param name="projectNames">
    /// The name the screens give a project, by its id (its folder), or null when it does not know it; it must not throw.
    /// Without it, or for a project it does not know, the question names no project (never its id).
    /// </param>
    public ApprovalPromptService(
        IApprovalDialog dialog,
        TimeProvider time,
        SynchronizationContext? uiContext = null,
        ReinforcedConfirmation? confirmation = null,
        IInterfaceLock? interfaceLock = null,
        Func<string, string?>? projectNames = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(time);
        _dialog = dialog;
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _confirmation = confirmation;
        _lock = interfaceLock;
        _projectNames = projectNames;
    }

    /// <exception cref="OperationCanceledException">The request was cancelled or expired before the person answered.</exception>
    public async Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            // Locked, the question waits for the unlock; the token returned is cancelled when the interface locks again.
            var unlocked = _lock is null ? CancellationToken.None : await _lock.WaitUntilUnlockedAsync(request, cancellationToken).ConfigureAwait(false);
            using var asking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, unlocked);
            var token = asking.Token;
            try
            {
                // WaitAsync: even a dialog that ignores the token cannot keep a cancelled, expired or locked-away question open.
                return await RunOnUiAsync(() => AskOnceAsync(request, token)).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (unlocked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The interface locked while the question was open, or on its way to the UI thread: it was withdrawn, and is
                // asked again once unlocked.
            }
        }
    }

    /// <summary>One showing of the question, on the UI thread: how a yes is confirmed, the dialog and, after a yes, Windows Hello.</summary>
    private async Task<ApprovalDecision> AskOnceAsync(ApprovalRequest request, CancellationToken asking)
    {
        asking.ThrowIfCancellationRequested();
        var plan = _confirmation is null ? ConfirmationPlan.AnswerAlone : await _confirmation.PlanAsync(request.Risk, asking).ConfigureAwait(true);

        // A lock, a cancellation or an expiry that came while the question was on its way to the UI thread (or while Windows
        // said whether Hello is there) opens no window: the question is withdrawn before it is shown.
        asking.ThrowIfCancellationRequested();
        var question = new ApprovalDialogViewModel(request, plan.Confirmation, plan.Availability, ProjectName(request));
        var answer = await _dialog.ShowAsync(question, asking).ConfigureAwait(true);

        // The window also closes when the request is cancelled, expires or the interface locks: that is no answer.
        asking.ThrowIfCancellationRequested();
        if (answer is null)
        {
            return Deny("Se cerró sin responder: cuenta como denegada.", plan.ForNo);
        }

        if (!answer.Approved)
        {
            return Deny(answer.Comment ?? "Denegada.", plan.ForNo);
        }

        if (!plan.AsksHello)
        {
            return new ApprovalDecision(true, DecidedBy, _time.GetUtcNow(), answer.Comment, Verification: plan.Confirmation);
        }

        // After a yes, Windows Hello: only a confirmation approves. One that comes once the question was withdrawn, expired
        // or locked away is no answer (ConfirmAsync throws).
        var hello = await _confirmation!.ConfirmAsync(HelloAnswers.ApprovalMessage(question.Title), asking).ConfigureAwait(true);
        return hello switch
        {
            { Confirmed: true } => new ApprovalDecision(true, DecidedBy, _time.GetUtcNow(), answer.Comment, Verification: ApprovalVerification.WindowsHello),
            { Problem: { } problem } => Unconfirmed(HelloAnswers.ApprovalNotAsked(problem)),
            _ => Unconfirmed(HelloAnswers.ApprovalNotConfirmed(hello.Result ?? UserPresenceResult.Canceled)),
        };
    }

    /// <summary>The name of the project of <paramref name="request"/>, when the request names one and it is known.</summary>
    private string? ProjectName(ApprovalRequest request) =>
        request.ProjectId is { } id && _projectNames is { } names ? names(id) : null;

    private ApprovalDecision Deny(string reason, ApprovalVerification verification) =>
        new(false, DecidedBy, _time.GetUtcNow(), reason, Verification: verification);

    /// <summary>A yes Windows Hello did not confirm, as the gate reads it: a denial with the method and the reason, which it makes the system's.</summary>
    private ApprovalDecision Unconfirmed(string reason) =>
        new(false, DecidedBy, _time.GetUtcNow(), reason, Verification: ApprovalVerification.WindowsHello);

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> work)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            return work();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ => _ = RunAndCompleteAsync(work, done), null);
        return done.Task;
    }

    private static async Task RunAndCompleteAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> done)
    {
        try
        {
            done.TrySetResult(await work().ConfigureAwait(true));
        }
#pragma warning disable CA1031 // Handed over to the caller, which denies.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            done.TrySetException(ex);
        }
    }
}
