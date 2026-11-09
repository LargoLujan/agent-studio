using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Domain.Security;
using AgentStudio.Security.Approvals;

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
/// How a yes to one question is confirmed, decided before it is asked (docs/16 D-13, docs/17 D-37 to D-40): by the answer
/// alone, by Windows Hello after a yes, by the answer alone with a warning because Windows says Hello is not there (only for
/// what is configurable), or, for an action of the fixed barrier on a computer without Hello, by the verified lock of Windows
/// after a yes.
/// </summary>
/// <param name="Confirmation">
/// <see cref="ApprovalVerification.Dialog"/> (it asks nothing, or the setting is off), <see cref="ApprovalVerification.WindowsHello"/>
/// (Hello after a yes), <see cref="ApprovalVerification.WindowsHelloUnavailable"/> (the answer alone, with a warning) or
/// <see cref="ApprovalVerification.WindowsLock"/> (the lock of Windows after a yes).
/// </param>
/// <param name="Availability">What Windows said of Windows Hello; null when it was not asked or could not say.</param>
/// <param name="Requirement">What the action asks (<see cref="PresencePolicy"/>): the dialog says when it is always.</param>
/// <param name="Action">The action of the barrier, when the list names it: the one rule judges the answer with it (<see cref="ReinforcedConfirmation.Approves"/>).</param>
/// <param name="SettingOn">What the setting of the important approvals said when the plan was made; false when it was not read (never for «Siempre»).</param>
public sealed record ConfirmationPlan(
    ApprovalVerification Confirmation,
    UserPresenceAvailability? Availability = null,
    PresenceRequirement Requirement = PresenceRequirement.Configurable,
    PresenceAction? Action = null,
    bool SettingOn = false)
{
    /// <summary>The answer alone, as before Phase 8: the action asks nothing, or the setting is off.</summary>
    public static ConfirmationPlan AnswerAlone { get; } = new(ApprovalVerification.Dialog);

    /// <summary>Whether a yes is confirmed with Windows Hello.</summary>
    public bool AsksHello => Confirmation == ApprovalVerification.WindowsHello;

    /// <summary>Whether a yes is confirmed with the verified lock of Windows (docs/17 D-40 (a)).</summary>
    public bool LocksWindows => Confirmation == ApprovalVerification.WindowsLock;

    /// <summary>Whether a yes needs a confirmation of the person after it: Windows Hello or the lock of Windows.</summary>
    public bool ConfirmsAfterYes => AsksHello || LocksWindows;

    /// <summary>
    /// Whether Windows Hello can be asked on this computer, as the one rule needs it: false only when Windows said for certain
    /// that it is not there (<see cref="HelloAnswers.IsAbsent"/>). Then, and only then, the lock of Windows counts (D-40 (a)).
    /// </summary>
    public bool HelloAvailable => Availability is not { } said || !HelloAnswers.IsAbsent(said);

    /// <summary>
    /// What the answer alone proves, for a plan that confirms nothing after a yes: the dialog, or the dialog with its warning
    /// that Hello is not there. Neither confirms the person; the proofs that do come only from <see cref="PresenceProofs"/>.
    /// </summary>
    public PresenceProof AnswerAloneProof => Confirmation == ApprovalVerification.WindowsHelloUnavailable
        ? PresenceProof.DialogWithHelloUnavailable
        : PresenceProof.Dialog;

    /// <summary>The method a no carries. A no is never confirmed: with a confirmation to be asked after a yes, a no is the answer's alone.</summary>
    public ApprovalVerification ForNo => ConfirmsAfterYes ? ApprovalVerification.Dialog : Confirmation;
}

/// <summary>
/// What came of confirming the person after a yes: the result of Windows Hello or of the verified lock of Windows, or why it
/// could not be asked.
/// </summary>
/// <param name="Result">What Windows Hello answered, when it was asked.</param>
/// <param name="Problem">Why the person could not be asked; null when they were.</param>
/// <param name="Lock">What the verified lock of Windows said, when Windows was locked.</param>
public sealed record HelloOutcome(UserPresenceResult? Result, string? Problem = null, WindowsLockResult? Lock = null)
{
    /// <summary>
    /// What the outcome proves, as the barrier judges it (<see cref="PresenceProofs"/>): Windows Hello when it confirmed the
    /// person, the lock of Windows when the state of the session says it was unlocked after the request, nothing otherwise.
    /// </summary>
    public PresenceProof Proof => Result is { } result ? PresenceProofs.FromHello(result)
        : Lock is { } locked ? PresenceProofs.FromWindowsLock(locked)
        : PresenceProof.None;
}

/// <summary>
/// The reinforced confirmation of the approvals (docs/16 D-13, F8-11; ADR-0037) and the fixed barrier of Windows Hello (docs/17
/// D-37 to D-41; ADR-0053): the one rule the questions of the approval gate (<see cref="ApprovalPromptService"/>), accepting a
/// history, «Aprobar plan» on the canvas and every new path follow, so they cannot drift apart: <see cref="PlanAsync(PresenceRequirement, CancellationToken)"/>
/// before the question, <see cref="ConfirmAsync"/> after a yes and <see cref="Approves"/>, the one rule of the domain
/// (<see cref="PresencePolicy.IsSatisfiedBy"/>), to judge what confirmed the person.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>What each action asks is <see cref="PresencePolicy"/>'s: a list in the code. <see cref="PresenceRequirement.Always"/>
/// asks Windows Hello whatever any setting or file says: <see cref="IsOn"/> is never read for it. Only
/// <see cref="PresenceRequirement.Configurable"/> reads it, at each question. <see cref="PresenceRequirement.Never"/> asks
/// nothing.</item>
/// <item>Without Hello on this computer (a definite «not there» of Windows, <see cref="HelloAnswers.IsAbsent"/>): what is
/// configurable is decided by the answer alone, with a warning, as in Phase 8; what is always locks Windows after a yes and
/// confirms the unlock from the state of the session (<see cref="WindowsLock"/>, docs/17 D-40 (a)). Without that lock
/// composed, nothing confirms it: a yes is denied, never left to the dialog.</item>
/// </list>
/// </remarks>
/// <param name="Verifier">Asks Windows to confirm the person; the app's is <c>WindowsHelloVerifier</c>.</param>
/// <param name="IsOn">
/// Whether the configurable approvals ask Windows Hello now («Pedir Windows Hello también en las aprobaciones importantes»,
/// <c>AppSettings.WindowsHelloForImportantApprovals</c>). Read only for those.
/// </param>
/// <param name="Time">The clock of the time limit of <see cref="HelloAnswers.AvailabilityAsync"/>; the system's by default.</param>
/// <param name="WindowsLock">The verified lock of Windows, for the actions of the fixed barrier without Hello; the app's is <see cref="WindowsLockConfirmation"/>.</param>
public sealed record ReinforcedConfirmation(
    IUserPresenceVerifier Verifier,
    Func<bool> IsOn,
    TimeProvider? Time = null,
    IWindowsLockConfirmation? WindowsLock = null)
{
    /// <summary>
    /// How long the person has to unlock Windows after a yes confirmed with the lock. The question of the gate keeps its own
    /// time (30 minutes) meanwhile, and a withdrawal stops the wait.
    /// </summary>
    public static readonly TimeSpan WindowsLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How a yes to <paramref name="action"/> is confirmed: what <see cref="PresencePolicy"/> asks of it (see
    /// <see cref="PlanAsync(PresenceRequirement, CancellationToken)"/>), and the answer is judged by the one rule
    /// (<see cref="Approves"/>).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ConfirmationPlan> PlanAsync(PresenceAction action, CancellationToken cancellationToken) =>
        PlanAsync(PresencePolicy.RequirementOf(action), action, cancellationToken);

    /// <summary>
    /// How a yes to an action the list does not name, which asks <paramref name="requirement"/>, is confirmed. Never, or
    /// configurable with the setting off: by the answer alone. Otherwise Windows is asked whether Hello is there: a definite
    /// «not there» means the answer alone, with a warning, for what is configurable, and the lock of Windows after a yes for
    /// what is always; anything else (available, busy, Windows could not say, or did not answer in
    /// <see cref="HelloAnswers.AvailabilityTimeout"/>) means Windows Hello after a yes. Nothing is shown.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requirement is not one of the three: nothing unknown is let through.</exception>
    public Task<ConfirmationPlan> PlanAsync(PresenceRequirement requirement, CancellationToken cancellationToken) =>
        PlanAsync(requirement, null, cancellationToken);

    /// <summary>
    /// Whether a yes confirmed by <paramref name="proof"/> approves, by the one rule of the domain: for an action of the list,
    /// <see cref="PresencePolicy.IsSatisfiedBy"/> with the setting as the plan read it (never for «Siempre») and whether this
    /// computer has Windows Hello (for «Siempre», only Hello when it has it; the lock of Windows unlocked after the request only
    /// when it has not). For an action the list does not name, which is never «Siempre», what the plan asked: a proof that is
    /// not <see cref="PresenceProof.None"/>. <paramref name="proof"/> comes from <see cref="HelloOutcome.Proof"/> (that is,
    /// from <see cref="PresenceProofs"/>) or from <see cref="ConfirmationPlan.AnswerAloneProof"/>.
    /// </summary>
    public static bool Approves(ConfirmationPlan plan, PresenceProof proof)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Action is { } action
            ? PresencePolicy.IsSatisfiedBy(action, plan.SettingOn, plan.HelloAvailable, proof)
            : proof is not PresenceProof.None && Enum.IsDefined(proof);
    }

    private async Task<ConfirmationPlan> PlanAsync(PresenceRequirement requirement, PresenceAction? action, CancellationToken cancellationToken)
    {
        var settingOn = false;
        switch (requirement)
        {
            case PresenceRequirement.Never:
                return ConfirmationPlan.AnswerAlone with { Requirement = requirement, Action = action };
            case PresenceRequirement.Configurable:
                settingOn = IsOn();
                if (!settingOn)
                {
                    return ConfirmationPlan.AnswerAlone with { Action = action };
                }

                break;
            case PresenceRequirement.Always:
                // The fixed barrier: the setting is not read.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unknown requirement: the barrier lets nothing unknown through.");
        }

        var availability = await HelloAnswers.AvailabilityAsync(Verifier, Time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        if (availability is { } said && HelloAnswers.IsAbsent(said))
        {
            return requirement == PresenceRequirement.Always
                ? new ConfirmationPlan(ApprovalVerification.WindowsLock, said, requirement, action)
                : new ConfirmationPlan(ApprovalVerification.WindowsHelloUnavailable, said, requirement, action, settingOn);
        }

        return new ConfirmationPlan(ApprovalVerification.WindowsHello, availability, requirement, action, settingOn);
    }

    /// <summary>
    /// After a yes, what <paramref name="plan"/> says: Windows Hello over the window of the app, with <paramref name="message"/>,
    /// or the verified lock of Windows (at most <see cref="WindowsLockTimeout"/>). Its <see cref="HelloOutcome.Proof"/> takes the
    /// yes only when <see cref="Approves"/> says so. When the person cannot be asked (Windows Hello fails, there is no lock of
    /// Windows composed), the outcome says why.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the question was withdrawn).</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> confirms nothing after a yes.</exception>
    public async Task<HelloOutcome> ConfirmAsync(ConfirmationPlan plan, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ConfirmsAfterYes)
        {
            throw new ArgumentException("The plan confirms nothing after a yes.", nameof(plan));
        }

        try
        {
            if (plan.LocksWindows)
            {
                if (WindowsLock is not { } windowsLock)
                {
                    return new HelloOutcome(null, HelloTexts.LockNotComposed);
                }

                var locked = await windowsLock.LockAndConfirmAsync(WindowsLockTimeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new HelloOutcome(null, Lock: locked);
            }

            var result = await Verifier.VerifyAsync(message, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new HelloOutcome(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The person could not be asked: the yes is not taken, and the outcome says why.
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
/// <item>What a question asks of the person's presence is the fixed list's (docs/17 D-37, <see cref="ApprovalRiskClassifier.PresenceOf"/>),
/// by its action and never by its words; with a <see cref="ReinforcedConfirmation"/>, the rule of
/// <see cref="ReinforcedConfirmation.PlanAsync(PresenceAction, CancellationToken)"/>, the one «Aprobar plan» follows too.
/// What is on the list asks Windows Hello after «Aprobar» whatever the setting says, and without Hello on this computer the
/// verified lock of Windows; what is configurable asks Hello while the setting is on, and only a definite answer of Windows
/// that Hello is not there (not set up, no device, a policy: <see cref="HelloAnswers.IsAbsent"/>) lets the dialog decide it
/// alone, with a warning (<see cref="ApprovalVerification.WindowsHelloUnavailable"/>). Busy, Windows could not say, or did not
/// answer in time: Hello is asked. Only what the one rule of the domain takes approves
/// (<see cref="ReinforcedConfirmation.Approves"/>, <c>PresencePolicy.IsSatisfiedBy</c>), with
/// <see cref="ApprovalVerification.WindowsHello"/> or <see cref="ApprovalVerification.WindowsLock"/>. A yes they did not
/// confirm, or could not be asked to, is returned as a denial with that method and the reason, which the gate records as the
/// system's (see <c>ApprovalGate</c>). Without a reinforced confirmation, an action of the list is not even shown: the gate
/// denies it. A no in the dialog never asks Hello: it is the dialog's.</item>
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

    /// <summary>
    /// One showing of the question, on the UI thread: how a yes is confirmed, the dialog and, after a yes, Windows Hello or the
    /// verified lock of Windows.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The action is on the list of the fixed barrier and no reinforced confirmation is composed: nothing could confirm the
    /// person, so the question is not shown and the gate denies it.
    /// </exception>
    private async Task<ApprovalDecision> AskOnceAsync(ApprovalRequest request, CancellationToken asking)
    {
        asking.ThrowIfCancellationRequested();

        // What the action asks is the list's (docs/17 D-37), by its action and never by its words.
        var action = ApprovalRiskClassifier.PresenceActionOf(request);
        var requirement = ApprovalRiskClassifier.PresenceOf(request);
        if (_confirmation is null && requirement == PresenceRequirement.Always)
        {
            throw new InvalidOperationException($"The action '{request.Action}' always asks for Windows Hello, and no reinforced confirmation is composed: it cannot be approved.");
        }

        var plan = _confirmation is null ? ConfirmationPlan.AnswerAlone with { Action = action }
            : action is { } known ? await _confirmation.PlanAsync(known, asking).ConfigureAwait(true)
            : await _confirmation.PlanAsync(requirement, asking).ConfigureAwait(true);

        // A lock, a cancellation or an expiry that came while the question was on its way to the UI thread (or while Windows
        // said whether Hello is there) opens no window: the question is withdrawn before it is shown.
        asking.ThrowIfCancellationRequested();
        var question = new ApprovalDialogViewModel(request, plan.Confirmation, plan.Availability, ProjectName(request), plan.Requirement);
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

        if (!plan.ConfirmsAfterYes)
        {
            // The answer alone (the dialog, or the dialog with Hello unavailable): the one rule must take it, or nothing does.
            return ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof)
                ? new ApprovalDecision(true, DecidedBy, _time.GetUtcNow(), answer.Comment, Verification: plan.Confirmation)
                : throw new InvalidOperationException($"The answer alone does not satisfy the barrier for '{request.Action}': it cannot be approved.");
        }

        // After a yes, Windows Hello or the verified lock of Windows: only what the one rule takes approves. One that comes once
        // the question was withdrawn, expired or locked away is no answer (ConfirmAsync throws).
        var confirmed = await _confirmation!.ConfirmAsync(plan, HelloAnswers.ApprovalMessage(question.Title), asking).ConfigureAwait(true);
        if (ReinforcedConfirmation.Approves(plan, confirmed.Proof))
        {
            return new ApprovalDecision(true, DecidedBy, _time.GetUtcNow(), answer.Comment, Verification: plan.Confirmation);
        }

        return confirmed switch
        {
            { Problem: { } problem } => Unconfirmed(plan.LocksWindows ? HelloTexts.ApprovalLockNotAsked(problem) : HelloAnswers.ApprovalNotAsked(problem), plan.Confirmation),
            { Lock: { } locked } => Unconfirmed(HelloTexts.ApprovalLockNotConfirmed(locked), plan.Confirmation),
            _ => Unconfirmed(HelloAnswers.ApprovalNotConfirmed(confirmed.Result ?? UserPresenceResult.Canceled), plan.Confirmation),
        };
    }

    /// <summary>The name of the project of <paramref name="request"/>, when the request names one and it is known.</summary>
    private string? ProjectName(ApprovalRequest request) =>
        request.ProjectId is { } id && _projectNames is { } names ? names(id) : null;

    private ApprovalDecision Deny(string reason, ApprovalVerification verification) =>
        new(false, DecidedBy, _time.GetUtcNow(), reason, Verification: verification);

    /// <summary>
    /// A yes Windows Hello or the lock of Windows did not confirm, as the gate reads it: a denial with the method
    /// (<see cref="ApprovalVerification.WindowsHello"/> or <see cref="ApprovalVerification.WindowsLock"/>) and the reason,
    /// which it makes the system's.
    /// </summary>
    private ApprovalDecision Unconfirmed(string reason, ApprovalVerification method) =>
        new(false, DecidedBy, _time.GetUtcNow(), reason, Verification: method);

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
