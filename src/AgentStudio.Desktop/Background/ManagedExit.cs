using System.Diagnostics;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>What the person answered when asked how to leave the work going on (docs/16 D-14 and D-16).</summary>
public enum ExitChoice
{
    /// <summary>«Seguir en la bandeja»: the window closes and everything goes on (only when closing the window).</summary>
    KeepInTray,

    /// <summary>«Pausar y salir cuando terminen los intentos en curso».</summary>
    PauseAndExitWhenIdle,

    /// <summary>«Salir ahora y dejarlas interrumpidas».</summary>
    ExitNow,

    /// <summary>«Cancelar»: nothing changes and nothing is written.</summary>
    Cancel,
}

/// <summary>One piece of work going on, as the exit question lists it: «monorepo · Ejecución 2026… · En ejecución · 2 intentos en curso».</summary>
public sealed record ExitWorkLine(string Project, string Title, string State, bool CanPause)
{
    /// <summary>What a screen reader says of the line (A1-bis): «monorepo · Ejecución 2026… · En ejecución», never the members of the record.</summary>
    public override string ToString() => $"{Project} · {Title} · {State}";
}

/// <summary>
/// The question of the managed exit: what is going on, and whether «Seguir en la bandeja» is one of the answers (it is
/// when the window is being closed, not when exiting from the tray).
/// </summary>
public sealed record ExitQuestion(IReadOnlyList<ExitWorkLine> Work, bool OffersKeepInTray)
{
    /// <summary>Some of the work cannot be paused (a debate, an audit): «Pausar y salir…» waits for it to end.</summary>
    public bool HasWorkThatCannotPause => Work.Any(w => !w.CanPause);
}

/// <summary>Asks the person how to leave the work going on. An interface so the exit can be tested without dialogs.</summary>
public interface IExitPrompt
{
    /// <summary>Asks once; closing the question any other way than with its buttons is <see cref="ExitChoice.Cancel"/>.</summary>
    Task<ExitChoice> AskAsync(ExitQuestion question, CancellationToken cancellationToken);
}

/// <summary>
/// The main window, as the background needs it: shown again or hidden, never closed (the app lives in the tray). The
/// background shows it only through <see cref="ManagedExit.ShowWindow"/>, which knows whether the app was waiting to exit.
/// </summary>
public interface IShellWindow
{
    /// <summary>Shows the window, restored and in front.</summary>
    void Show();

    /// <summary>Hides the window; the app and its work go on in the tray.</summary>
    void Hide();
}

/// <summary>Ends the app (in the app, <c>Application.Current.Shutdown()</c>): the host stops and the Job Object kills what is left.</summary>
public interface IAppShutdown
{
    void Shutdown();
}

/// <summary>
/// The consented resumes (docs/16 D-39) as the managed exit holds them back while the app leaves: in the app,
/// <c>ConsentedResumes</c> over the engine's <c>ResumeScheduler.Suspend</c> and <c>WaitForIdleAsync</c>. An interface so the
/// exit can be tested without the engine.
/// </summary>
public interface ISuspendableResumes
{
    /// <summary>
    /// Suspends the resumes at once, until what it returns is disposed, whatever happens to any wait: no pass resumes anything
    /// from then on. Several can be held; the resumes go on when the last is released.
    /// </summary>
    IDisposable Suspend();

    /// <summary>
    /// Completes once every pass asked for before the call has ended: every run they resumed has been announced by then, and so
    /// is in the registry of the work. Cancelling the wait releases nothing.
    /// </summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken);
}

/// <summary>Where the managed exit stands.</summary>
public enum ExitState
{
    /// <summary>The app is not exiting.</summary>
    None,

    /// <summary>«Pausar y salir…» was chosen: the app waits in the tray for the attempts going to end.</summary>
    WaitingForIdle,

    /// <summary>The app is shutting down: closing the window must not ask anything any more.</summary>
    Exiting,
}

/// <summary>What closing the window ended up doing.</summary>
public enum CloseOutcome
{
    /// <summary>The window stays as it was (the person cancelled).</summary>
    StayOpen,

    /// <summary>The window was hidden: the app goes on in the tray.</summary>
    HiddenToTray,

    /// <summary>The app is exiting, now or once the attempts going end.</summary>
    Exiting,
}

/// <summary>
/// The managed exit (docs/16 D-14 and D-16, F8-7 and F8-9). Closing the window follows <see cref="CloseWindowPolicy"/>;
/// exiting with work going on asks (<see cref="IExitPrompt"/>), and the person's choice is written in the log of each piece
/// of work before anything else happens (<see cref="IRunExitRecorder"/>):
/// <list type="bullet">
/// <item>«Pausar y salir cuando terminen los intentos en curso»: each run is paused (nothing new is dispatched), the window
/// hides, and the app waits in the tray until no attempt is in flight and the work that cannot be paused (a debate, an
/// audit) has ended; then it exits. Meanwhile the tray offers «Salir ahora» and «No salir», and opening the window
/// (<see cref="ShowWindow"/>) is «No salir» too, with a notice.</item>
/// <item>«Salir ahora y dejarlas interrumpidas»: the app exits at once; what was running is interrupted and is resumed by
/// replaying the log, when the person asks.</item>
/// <item>«Cancelar» writes nothing and touches nothing; «Seguir en la bandeja» hides the window and writes nothing.</item>
/// <item>The work is read when the question is put, on the UI thread, and the choice is written only for what is still
/// that same work afterwards: a run that ended meanwhile has nothing to leave, and one its document follows now instead was
/// not asked about. Each write stands alone: a log that cannot be written (its chain does not verify, the disk failed)
/// never holds the others nor the exit, and is noted in the app's log. A run whose log does not verify cannot be resumed
/// either, by anyone.</item>
/// <item>Windows ending the session (<see cref="RecordSessionEnded"/>) counts as exiting now, but nobody chose it: it is
/// written as <see cref="RunExitChoice.SessionEnded"/>, within the little time Windows gives, and only in what the person
/// did not decide in this session of the app. A person's choice stays in force: «No salir» after «Pausar y salir…», or an
/// exit already under way, is never written over by an end of the session that would let the run resume by itself. It is
/// written while the interface is locked too.</item>
/// <item>While the interface is locked (<see cref="IsLocked"/>, docs/16 D-13) nothing is asked: closing the window hides it,
/// «Salir» waits, and a question open when it locks (this one, or the shell's confirmation) is withdrawn as «Cancelar».</item>
/// </list>
/// Work whose id is not known here (an audit, a planning) is listed and waited for, but has no entry: it never resumes by
/// itself anyway.
/// <para>
/// The consented resumes (docs/16 D-39) are suspended before the work going on is read (<see cref="ISuspendableResumes.Suspend"/>,
/// at once), and the exit waits for the pass in progress (<see cref="ISuspendableResumes.WaitForIdleAsync"/>): a run the
/// scheduler resumes is never left out of the question, nor resumed once the person chose. The suspension is released when the
/// person stays («Cancelar», «Seguir en la bandeja», «No salir», or the window shown again while the exit waits, for the pass
/// or for the attempts), held while the app waits or exits, and taken for good when Windows ends the session.
/// </para>
/// </summary>
public sealed class ManagedExit : IDisposable
{
    /// <summary>What the person is told when opening the window cancelled the exit that waited for the attempts to end.</summary>
    public const string PendingExitCancelledNotice =
        "Abriste Agent Studio, así que ya no sale cuando terminen los intentos en curso. Las ejecuciones siguen en pausa hasta que las reanudes.";

    private readonly BackgroundWorkRegistry _work;
    private readonly IExitPrompt _prompt;
    private readonly IRunExitRecorder _recorder;
    private readonly IShellWindow _window;
    private readonly IAppShutdown _shutdown;
    private readonly Func<CancellationToken, Task<bool>>? _confirmLeaving;
    private readonly ILogger? _log;
    private readonly ISuspendableResumes? _resumes;

    /// <summary>The questions of the exit open now (its own, and the shell's confirmation): the lock withdraws them.</summary>
    private CancellationTokenSource? _questions;

    private bool _locked;

    /// <summary>The suspension of the consented resumes this exit holds; null while it holds none.</summary>
    private IDisposable? _suspension;

    /// <summary>
    /// The wait for the pass of the resumes in progress before the question, while it lasts: the window shown again, or the end
    /// of the session, cancels it, and the exit stops there.
    /// </summary>
    private CancellationTokenSource? _passWait;

    /// <summary>
    /// The pieces of work (project id, work id) whose exit the person chose in this session of the app, written or not: an
    /// end of the session never writes over that choice (docs/04 §18.2).
    /// </summary>
    private readonly HashSet<(string ProjectId, string WorkId)> _decided = [];

    /// <summary>A question (the exit, or the shell's confirmation) is open: a second request waits for it to be answered.</summary>
    private bool _deciding;

    /// <param name="work">What is going on.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="recorder">Writes the choice in the log of each piece of work.</param>
    /// <param name="window">The main window.</param>
    /// <param name="shutdown">Ends the app.</param>
    /// <param name="confirmLeaving">
    /// Asked once the person chose to exit and before anything is written (the shell: unsaved changes in the editor); false
    /// stops the exit as if it had been cancelled. Its token is cancelled when the interface locks (<see cref="IsLocked"/>): a
    /// withdrawn confirmation stops the exit too.
    /// </param>
    /// <param name="log">The app's log, for a choice that could not be written.</param>
    /// <param name="resumes">The consented resumes, held back while the app leaves; null when nothing resumes by itself.</param>
    public ManagedExit(
        BackgroundWorkRegistry work,
        IExitPrompt prompt,
        IRunExitRecorder recorder,
        IShellWindow window,
        IAppShutdown shutdown,
        Func<CancellationToken, Task<bool>>? confirmLeaving = null,
        ILogger<ManagedExit>? log = null,
        ISuspendableResumes? resumes = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shutdown);
        _work = work;
        _prompt = prompt;
        _recorder = recorder;
        _window = window;
        _shutdown = shutdown;
        _confirmLeaving = confirmLeaving;
        _log = log;
        _resumes = resumes;
    }

    /// <summary>Whether this exit holds a suspension of the consented resumes now.</summary>
    public bool SuspendsResumes => _suspension is not null;

    /// <summary>
    /// The interface is locked (docs/16 D-13; the background follows it, ADR-0037): nothing of the exit is asked over the lock
    /// screen. Closing the window then only hides it, «Salir» waits for the unlock, and a question open when it locks (the one of
    /// the exit, or the shell's confirmation) is withdrawn as «Cancelar»: nothing is written, and the suspension of the resumes is
    /// released. On the UI thread.
    /// </summary>
    public bool IsLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            if (value)
            {
                // Locked first, then withdrawn: what follows a withdrawn question may run inside Cancel, and must see the lock.
                _questions?.Cancel();
                _passWait?.Cancel();
            }
        }
    }

    /// <summary>Raised on the UI thread when <see cref="State"/> or <see cref="AttemptsLeft"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised on the UI thread with <see cref="PendingExitCancelledNotice"/> when opening the window cancelled the exit that
    /// waited for the attempts to end (the tray says it; the shell may too).
    /// </summary>
    public event EventHandler<string>? PendingExitCancelled;

    public ExitState State { get; private set; }

    /// <summary>The app is shutting down: the window closes without asking.</summary>
    public bool IsExiting => State == ExitState.Exiting;

    /// <summary>While waiting to exit: the attempts still in flight, or null when some work cannot tell.</summary>
    public int? AttemptsLeft { get; private set; }

    /// <summary>
    /// Shows the main window: every way the background opens it comes here (the menu of the tray, its notifications, the
    /// status panel), and so should the shell's. While the app waits to exit, opening the window cancels that exit, as «No
    /// salir» does, and says so (<see cref="PendingExitCancelled"/>): what the person does once the window is back is never
    /// cut by an exit they no longer expect. The runs stay paused until the person resumes them. Opening it while the exit
    /// still waits for the pass of the resumes in progress, before its question, stops that exit too: nothing is asked.
    /// </summary>
    public void ShowWindow()
    {
        if (State == ExitState.WaitingForIdle)
        {
            CancelPendingExit();
            PendingExitCancelled?.Invoke(this, PendingExitCancelledNotice);
            return;
        }

        _passWait?.Cancel();
        _window.Show();
    }

    /// <summary>
    /// The window is being closed (the shell cancels the close and calls this): <see cref="CloseWindowPolicy"/> with the
    /// setting <paramref name="setting"/> and the work going on decides whether to hide it, to ask, or to exit.
    /// </summary>
    public async Task<CloseOutcome> WindowClosingAsync(CloseWindowAction setting)
    {
        if (State != ExitState.None)
        {
            // Already exiting, or waiting to: closing the window again only hides it.
            if (State == ExitState.WaitingForIdle)
            {
                _window.Hide();
                return CloseOutcome.HiddenToTray;
            }

            return CloseOutcome.Exiting;
        }

        if (_deciding)
        {
            // A question is already open (the menu of the tray stays usable over the modal one): it decides.
            return CloseOutcome.StayOpen;
        }

        if (IsLocked)
        {
            // Locked: no question over the lock screen, and no exit. The window goes to the tray, where the work goes on.
            return Hide();
        }

        _deciding = true;
        using var questions = new CancellationTokenSource();
        _questions = questions;
        try
        {
            // Before the work is read: what is asked about is all that goes on. The window shown again meanwhile stops here.
            if (!await HoldResumesAsync().ConfigureAwait(true))
            {
                ReleaseResumesIfStaying();
                return Stopped();
            }

            var outcome = CloseWindowPolicy.Decide(setting, _work.HasActiveWork) switch
            {
                CloseWindowDecision.HideToTray => Hide(),
                CloseWindowDecision.Exit => await LeaveAsync(RunExitChoice.ExitNow, [], questions.Token).ConfigureAwait(true),
                CloseWindowDecision.AskKeepOrExit => await AskAndLeaveAsync(offersKeepInTray: true, questions.Token).ConfigureAwait(true),
                _ => await AskAndLeaveAsync(offersKeepInTray: false, questions.Token).ConfigureAwait(true),
            };
            ReleaseResumesIfStaying();
            return outcome;
        }
        catch
        {
            ReleaseResumesIfStaying();
            throw;
        }
        finally
        {
            _questions = null;
            _deciding = false;
        }
    }

    /// <summary>«Salir» (the tray): with work going on, asks how to leave it; without, exits.</summary>
    public async Task<CloseOutcome> ExitAsync()
    {
        if (State == ExitState.Exiting)
        {
            return CloseOutcome.Exiting;
        }

        if (State == ExitState.WaitingForIdle)
        {
            // Already waiting: «Salir» again means «Salir ahora».
            await ExitNowAsync().ConfigureAwait(true);
            return CloseOutcome.Exiting;
        }

        if (_deciding || IsLocked)
        {
            // A question is open, or the interface is locked: leaving waits for it (the tray offers no «Salir» while locked).
            return CloseOutcome.StayOpen;
        }

        _deciding = true;
        using var questions = new CancellationTokenSource();
        _questions = questions;
        try
        {
            // Before the work is read: what is asked about is all that goes on. The window shown again meanwhile stops here.
            if (!await HoldResumesAsync().ConfigureAwait(true))
            {
                ReleaseResumesIfStaying();
                return Stopped();
            }

            var outcome = _work.HasActiveWork
                ? await AskAndLeaveAsync(offersKeepInTray: false, questions.Token).ConfigureAwait(true)
                : await LeaveAsync(RunExitChoice.ExitNow, [], questions.Token).ConfigureAwait(true);
            ReleaseResumesIfStaying();
            return outcome;
        }
        catch
        {
            ReleaseResumesIfStaying();
            throw;
        }
        finally
        {
            _questions = null;
            _deciding = false;
        }
    }

    /// <summary>
    /// «Salir ahora» while waiting to exit: writes it in the log of what is still going on and exits at once. The app is
    /// exiting from the first moment, so a second «Salir ahora» or a «No salir» while the choice is being written changes
    /// nothing.
    /// </summary>
    public async Task ExitNowAsync()
    {
        if (State != ExitState.WaitingForIdle)
        {
            return;
        }

        _work.Changed -= OnWorkChanged;
        var going = Snapshot(_work.Active);
        Decide(going);
        SetState(ExitState.Exiting, null);
        await RecordAsync(going, RunExitChoice.ExitNow).ConfigureAwait(true);
        _shutdown.Shutdown();
    }

    /// <summary>«No salir» while waiting to exit: the app stays, the window shows again, and the runs stay paused.</summary>
    public void CancelPendingExit()
    {
        if (State != ExitState.WaitingForIdle)
        {
            return;
        }

        _work.Changed -= OnWorkChanged;
        SetState(ExitState.None, null);

        // The person stays: what was resumed by consent may resume again (the runs paused for the exit stay paused).
        ReleaseResumes();
        _window.Show();
    }

    /// <summary>
    /// Windows is ending the session (<c>Application.SessionEnding</c>): writes <see cref="RunExitChoice.SessionEnded"/> in
    /// the log of each piece of work going on that the person did not decide in this session of the app, waiting at most
    /// <paramref name="budget"/>, and marks the app as exiting. When the app was already exiting, the person's choice (or an
    /// exit with nothing going on) already holds, and nothing is written. The work is read on the calling (UI) thread; the
    /// writes run off it with what was read, since Windows does not wait for an asynchronous answer.
    /// </summary>
    public void RecordSessionEnded(TimeSpan budget)
    {
        _work.Changed -= OnWorkChanged;
        var clock = Stopwatch.StartNew();

        // For good, and before anything is read or written: nothing resumes by itself while Windows ends the session.
        HoldResumesForGood(budget / 2);
        if (State == ExitState.Exiting)
        {
            return;
        }

        var nobodyChose = Snapshot(_work.Active)
            .Where(a => a.Project is { } project && a.WorkId is { } id && !_decided.Contains((project.ProjectId, id)))
            .ToList();
        SetState(ExitState.Exiting, null);

        // An exit that still waited for the pass in progress, before its question, stops there and asks nothing. Once the app
        // is exiting: on this thread, what follows that wait may run inside Cancel.
        _passWait?.Cancel();
        if (nobodyChose.Count == 0)
        {
            return;
        }

        var writes = Task.Run(() => RecordAsync(nobodyChose, RunExitChoice.SessionEnded));
        try
        {
            var left = budget - clock.Elapsed;
            if (!writes.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                _log?.LogWarning("Windows ended the session before the exit was written in the log of {Count} pieces of work.", nobodyChose.Count);
            }
        }
        catch (AggregateException ex)
        {
            _log?.LogWarning(ex, "The end of the session could not be written in every log.");
        }
    }

    public void Dispose() => _work.Changed -= OnWorkChanged;

    /// <summary>
    /// Before the work going on is read: takes the suspension of the consented resumes at once (unless this exit holds one), and
    /// then waits for the pass in progress, whose resumed runs are in the registry of the work once it ends. False when the
    /// window was shown again meanwhile, or the session ended: the exit stops there, before its question. A suspension or a
    /// wait that fails is noted in the app's log, and the exit goes on.
    /// </summary>
    private async Task<bool> HoldResumesAsync()
    {
        if (_resumes is null)
        {
            return true;
        }

        try
        {
            _suspension ??= _resumes.Suspend();
        }
#pragma warning disable CA1031 // The resumes could not be suspended: the exit still asks and leaves, and the app's log says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "The consented resumes could not be suspended before the exit.");
            return true;
        }

        using var wait = new CancellationTokenSource();
        _passWait = wait;
        try
        {
            await _resumes.WaitForIdleAsync(wait.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return false;
        }
#pragma warning disable CA1031 // The pass could not be waited for: the exit still asks, and the app's log says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "The pass of the consented resumes in progress could not be waited for before the exit.");
        }
        finally
        {
            _passWait = null;
        }

        return State == ExitState.None && !IsLocked;
    }

    /// <summary>
    /// The end of the session: the suspension at once (the one the exit holds, if any), never released, and then a wait of at
    /// most <paramref name="wait"/> for the pass in progress to end. The suspension counts before the wait, so no further run
    /// resumes even if the pass does not end in time.
    /// </summary>
    private void HoldResumesForGood(TimeSpan wait)
    {
        if (_resumes is null || _suspension is Kept)
        {
            return;
        }

        try
        {
            _suspension = new Kept(_suspension ?? _resumes.Suspend());
        }
#pragma warning disable CA1031 // The resumes could not be suspended: the end of the session is still written, and the app's log says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "The consented resumes could not be suspended when the session ended.");
            return;
        }

        // Bounded twice: the token lets the scheduler drop the wait, and the timeout does not trust that it does.
        using var bound = new CancellationTokenSource(wait);
        try
        {
            if (!_resumes.WaitForIdleAsync(bound.Token).Wait(wait))
            {
                _log?.LogWarning("The pass of the consented resumes in progress did not end before the session ended.");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            _log?.LogWarning("The pass of the consented resumes in progress did not end before the session ended.");
        }
#pragma warning disable CA1031 // The pass could not be waited for: the end of the session is still written, and the app's log says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "The pass of the consented resumes in progress could not be waited for when the session ended.");
        }
    }

    /// <summary>What an exit stopped before its question returns: nothing changed, unless the session ended meanwhile.</summary>
    private CloseOutcome Stopped() => State == ExitState.Exiting ? CloseOutcome.Exiting : CloseOutcome.StayOpen;

    /// <summary>The shell's confirmation (unsaved changes): false when declined, or withdrawn by the lock while it was open.</summary>
    private async Task<bool> ConfirmLeavingAsync(CancellationToken withdrawn)
    {
        try
        {
            return await _confirmLeaving!(withdrawn).ConfigureAwait(true) && !withdrawn.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (withdrawn.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The person stays: the suspension, if any, is released (unless the end of the session took it for good).</summary>
    private void ReleaseResumes()
    {
        if (_suspension is Kept)
        {
            return;
        }

        _suspension?.Dispose();
        _suspension = null;
    }

    /// <summary>After a question: the person stayed unless the app now waits to exit or exits.</summary>
    private void ReleaseResumesIfStaying()
    {
        if (State == ExitState.None)
        {
            ReleaseResumes();
        }
    }

    private CloseOutcome Hide()
    {
        _window.Hide();
        return CloseOutcome.HiddenToTray;
    }

    /// <summary>A suspension the end of the session took for good: never released.</summary>
    private sealed class Kept(IDisposable suspension) : IDisposable
    {
        public IDisposable Suspension { get; } = suspension;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Asks how to leave the work going on, and does what was answered. A question the lock withdrew (<paramref name="withdrawn"/>)
    /// is «Cancelar», whatever was answered as it locked.
    /// </summary>
    private async Task<CloseOutcome> AskAndLeaveAsync(bool offersKeepInTray, CancellationToken withdrawn)
    {
        var asked = Snapshot(_work.Active);
        var question = new ExitQuestion([.. asked.Select(a => a.Line)], offersKeepInTray);
        ExitChoice choice;
        try
        {
            choice = await _prompt.AskAsync(question, withdrawn).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (withdrawn.IsCancellationRequested)
        {
            choice = ExitChoice.Cancel;
        }

        if (withdrawn.IsCancellationRequested)
        {
            choice = ExitChoice.Cancel;
        }

        switch (choice)
        {
            case ExitChoice.KeepInTray when offersKeepInTray:
                _window.Hide();
                return CloseOutcome.HiddenToTray;
            case ExitChoice.PauseAndExitWhenIdle:
                return await LeaveAsync(RunExitChoice.PauseAndExitWhenIdle, asked, withdrawn).ConfigureAwait(true);
            case ExitChoice.ExitNow:
                return await LeaveAsync(RunExitChoice.ExitNow, asked, withdrawn).ConfigureAwait(true);
            default:
                return CloseOutcome.StayOpen;
        }
    }

    /// <summary>
    /// Leaves what the person was asked about as chosen: the shell confirms first (unsaved changes), then the choice is
    /// written in the log of each piece of work that is still the same and going on, and then the app exits, or pauses the
    /// runs and waits. A confirmation the lock withdrew (<paramref name="withdrawn"/>) stops the exit, as a cancel does.
    /// </summary>
    private async Task<CloseOutcome> LeaveAsync(RunExitChoice choice, IReadOnlyList<AskedWork> asked, CancellationToken withdrawn)
    {
        if (_confirmLeaving is not null && !await ConfirmLeavingAsync(withdrawn).ConfigureAwait(true))
        {
            return CloseOutcome.StayOpen;
        }

        var leaving = StillGoing(asked);
        Decide(leaving);
        if (choice != RunExitChoice.PauseAndExitWhenIdle)
        {
            SetState(ExitState.Exiting, null);
            await RecordAsync(leaving, choice).ConfigureAwait(true);
            _shutdown.Shutdown();
            return CloseOutcome.Exiting;
        }

        await RecordAsync(leaving, choice).ConfigureAwait(true);
        foreach (var work in leaving.Select(a => a.Work).Where(w => w.IsPausable))
        {
            try
            {
                await work.PauseAsync().ConfigureAwait(true);
            }
#pragma warning disable CA1031 // A run that cannot be paused is waited for all the same: its failure goes to the app's log.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogWarning(ex, "{Title} could not be paused before exiting.", work.Title);
            }
        }

        _window.Hide();
        State = ExitState.WaitingForIdle;
        if (ExitIfIdle())
        {
            return CloseOutcome.Exiting;
        }

        _work.Changed += OnWorkChanged;
        SetState(ExitState.WaitingForIdle, AttemptsStillRunning());
        return CloseOutcome.Exiting;
    }

    /// <summary>The work as it is now, read on the UI thread: what the question lists and where its choice would go.</summary>
    private List<AskedWork> Snapshot(IEnumerable<IBackgroundWork> works)
    {
        var asked = new List<AskedWork>();
        foreach (var work in works)
        {
            try
            {
                asked.Add(new AskedWork(work, work.Project, work.WorkId, new ExitWorkLine(WorkTexts.ProjectName(work.ProjectRoot), work.Title, work.StateText, work.IsPausable)));
            }
#pragma warning disable CA1031 // A piece of work that cannot be read is left out of the question; the others are not.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogWarning(ex, "A piece of work could not be read for the exit.");
            }
        }

        return asked;
    }

    /// <summary>What the person was asked about that is still going on and still the same work (the same project and id).</summary>
    private List<AskedWork> StillGoing(IReadOnlyList<AskedWork> asked)
    {
        var going = new List<AskedWork>();
        foreach (var item in asked)
        {
            try
            {
                if (item.Work.IsActive
                    && string.Equals(item.Work.WorkId, item.WorkId, StringComparison.Ordinal)
                    && string.Equals(item.Work.Project?.ProjectId, item.Project?.ProjectId, StringComparison.Ordinal))
                {
                    going.Add(item);
                }
            }
#pragma warning disable CA1031 // A piece of work that cannot be read again gets no entry; the others still do.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogWarning(ex, "A piece of work could not be read again after the exit was chosen.");
            }
        }

        return going;
    }

    /// <summary>Remembers, for the rest of the session, that the person chose how to leave these pieces of work.</summary>
    private void Decide(IEnumerable<AskedWork> items)
    {
        foreach (var item in items)
        {
            if (item.Project is { } project && item.WorkId is { } id)
            {
                _decided.Add((project.ProjectId, id));
            }
        }
    }

    /// <summary>Writes the choice in the log of each piece of work with an id; each write stands alone.</summary>
    private async Task RecordAsync(IReadOnlyList<AskedWork> items, RunExitChoice choice)
    {
        foreach (var item in items)
        {
            if (item.Project is not { } project || item.WorkId is not { } id)
            {
                continue;
            }

            try
            {
                await _recorder.RecordAsync(project, id, choice, CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // A log that cannot be written must not keep the app from exiting: it is noted in the app's log.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogError(ex, "The exit ({Choice}) could not be written in the log of {Work}.", choice, id);
            }
        }
    }

    private void OnWorkChanged(object? sender, EventArgs e)
    {
        if (State != ExitState.WaitingForIdle || ExitIfIdle())
        {
            return;
        }

        var left = AttemptsStillRunning();
        if (left != AttemptsLeft)
        {
            SetState(ExitState.WaitingForIdle, left);
        }
    }

    /// <summary>Exits once nothing is left to wait for; true when it did.</summary>
    private bool ExitIfIdle()
    {
        if (State != ExitState.WaitingForIdle || !_work.Active.All(IsIdle))
        {
            return false;
        }

        _work.Changed -= OnWorkChanged;
        SetState(ExitState.Exiting, null);
        _shutdown.Shutdown();
        return true;
    }

    /// <summary>
    /// A run is idle once no attempt of it is in flight (it is paused, so nothing new starts); work that cannot be paused is
    /// idle once it ended.
    /// </summary>
    private static bool IsIdle(IBackgroundWork work) => !work.IsActive || (work.IsPausable && work.AttemptsInFlight == 0);

    /// <summary>The attempts still in flight in the work being waited for; null when some of it cannot tell.</summary>
    private int? AttemptsStillRunning()
    {
        var waited = _work.Active.Where(w => !IsIdle(w)).ToList();
        return waited.Any(w => w.AttemptsInFlight is null) ? null : waited.Sum(w => w.AttemptsInFlight ?? 0);
    }

    private void SetState(ExitState state, int? attemptsLeft)
    {
        State = state;
        AttemptsLeft = attemptsLeft;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One piece of work as the question saw it, read on the UI thread: the work, and its project and id then.</summary>
    private sealed record AskedWork(IBackgroundWork Work, ProjectContext? Project, string? WorkId, ExitWorkLine Line);
}
