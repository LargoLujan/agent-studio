using AgentStudio.Application.Localization;
using System.Threading.Channels;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// What the scheduler did with one run that was left unfinished: resumed it by the consent of its header (<see cref="Cause"/>,
/// and the run going again in <see cref="Resumed"/>), or left it for a person, with <see cref="Reason"/> in Spanish.
/// </summary>
public sealed record ScheduledResume(RunId Run, RunResumeCause? Cause, IOrchestrationRun? Resumed, string Reason, UserText? ReasonText = null);

/// <summary>A run of <see cref="Project"/> that the scheduler resumed by the consent of its header, going now.</summary>
public sealed class ScheduledResumeEventArgs(ProjectContext project, IOrchestrationRun run, RunResumeCause cause) : EventArgs
{
    public ProjectContext Project { get; } = project;

    public IOrchestrationRun Run { get; } = run;

    public RunResumeCause Cause { get; } = cause;
}

/// <summary>
/// What a resume by consent did (<see cref="Orchestrator.ResumeByConsentAsync"/>): the run going again and its cause, or
/// why not, with nothing written.
/// </summary>
internal sealed record ConsentResume(IOrchestrationRun? Run, RunResumeCause? Cause, string Reason, UserText? ReasonText = null);

/// <summary>
/// The resume after a restart that the consent of a header allows (F8-4; docs/16 D-39, ADR-0034 decisions 1, 5 and 6). When the
/// app starts, and while it stays in the tray, the composition asks it for each project: it resumes the runs left unfinished
/// whose header has <c>AutoResumeConsent</c> and that were waiting for quota or were cut by a close nobody asked for. Never one
/// a person paused (the last <c>RunDispatchChanged</c> is a pause) nor one a person left on exit (any <c>RunExitChosen</c> that
/// is a person's choice, even with a <c>SessionEnded</c> of Windows after it); what came before the last resume of a run does
/// not count, since that resume started it again. Everything that decides is in the log, which must verify. The resume
/// records <c>RunResumed</c> with the cause before anything is launched, and the run goes on within its approved budget
/// (<c>MaxAgentRuns</c>), as any resumed run.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The passes go one at a time, through one queue with a single reader, whoever asks for them and from wherever.</item>
/// <item>A pass decides on the log it read, and the orchestrator decides again, under the reservation of the run, on the log
/// it reads then (<see cref="Orchestrator.ResumeByConsentAsync"/>): a person who paused the run or chose to leave in between
/// is never overruled. A run that stopped in this session of the app on a system error is left for a person.</item>
/// <item>What goes wrong with a run is that run's alone: the pass goes on with the others. A run whose resume failed after
/// writing to its log (an alert that it is not resumable, or <c>RunResumed</c> before its recovery failed) is not tried again
/// in this session of the app, so the tray does not write the same alert, or another resume, at every pass.</item>
/// <item><see cref="Suspend"/> stops the passes while the app leaves, and <see cref="WaitForIdleAsync"/> waits for the one in
/// progress: the managed exit and the end of the Windows session take a suspension before they look at the work that is
/// going on, and a wait that gives up never releases it.</item>
/// </list>
/// </remarks>
public sealed class ResumeScheduler
{
    private readonly Orchestrator _orchestrator;
    private readonly IDecisionRecorder _decisions;
    private readonly IRunRegistry _runs;
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>The runs whose resume failed after writing to their log in this session of the app, and why. Only the reader of the queue touches it.</summary>
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);

    private int _suspensions;

    /// <summary>Cancelled by every <see cref="Suspend"/> and replaced: the waits of a resume for the project (docs/21 D-25) are linked to the one they started with, so suspending ends them at once.</summary>
    private CancellationTokenSource _suspendSignal = new();

    /// <param name="orchestrator">The engine's orchestrator: only it resumes a run by the consent of its header.</param>
    public ResumeScheduler(IOrchestrator orchestrator, IDecisionRecorder decisions, IRunRegistry runs)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(runs);
        _orchestrator = orchestrator as Orchestrator
            ?? throw new ArgumentException("Resuming a run by the consent of its header goes through the engine's Orchestrator.", nameof(orchestrator));
        _decisions = decisions;
        _runs = runs;
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Raised for every run the scheduler resumes, as soon as it is going, on a thread of the pool, and before both
    /// <see cref="ResumeConsentedAsync"/> and a later <see cref="WaitForIdleAsync"/> complete: the composition attaches it to
    /// the monitor of the orchestrator document of its project, as it follows the runs it starts, so that the run has its
    /// place in the tray, its pause and its choice on exit. The events of a run are kept until their one reader comes
    /// (<see cref="IOrchestrationRun.Events"/>), so a monitor that
    /// attaches later, once a window exists, loses none of them: whoever resumes before there is a window keeps the run (this
    /// event, or <see cref="ScheduledResume.Resumed"/>) and hands it to the monitor when the window opens.
    /// </summary>
    public event EventHandler<ScheduledResumeEventArgs>? Resumed;

    private bool Suspended => Volatile.Read(ref _suspensions) > 0;

    /// <summary>
    /// Resumes the runs of <paramref name="project"/> its consent allows, and says what it did with every run left unfinished
    /// that no process runs, in the order they were created. The pass waits for the ones asked for before it. A log that does
    /// not verify resumes nothing, and neither does a pass while the resumes are suspended (<see cref="Suspend"/>).
    /// </summary>
    public Task<IReadOnlyList<ScheduledResume>> ResumeConsentedAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var pass = new Pass(project, cancellationToken);
        _requests.Writer.TryWrite(pass);
        return pass.Done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Suspends the resumes at once, until the returned suspension is disposed, whatever happens to any wait: the pass in
    /// progress, if any, resumes no further run, and no later pass resumes anything (it returns no runs). Several can be held;
    /// the resumes go on when the last is released. Take it first, and then wait for the pass in progress
    /// (<see cref="WaitForIdleAsync"/>): the managed exit takes it before it looks at the work going on to ask the person (and
    /// releases it if they stay), and the end of the Windows session before it records anything (and keeps it).
    /// </summary>
    public IDisposable Suspend() => new Suspension(this);

    /// <summary>
    /// Completes once every pass asked for before the call has ended. Every run those passes resumed has been announced through
    /// <see cref="Resumed"/>, its handlers returned, by then: the passes and this wait go through the same single reader, in
    /// order. With a suspension held (<see cref="Suspend"/>, taken before the call), no later pass resumes anything, so from
    /// then on every run going on in the app is one the caller can see. Cancelling the wait releases nothing.
    /// </summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        var barrier = new Barrier();
        _requests.Writer.TryWrite(barrier);
        return barrier.Done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// <see cref="Suspend"/> and then <see cref="WaitForIdleAsync"/>, for a caller that keeps the suspension for good (the end
    /// of the Windows session). If the wait is cancelled the suspension stays held, and this call no longer gives a way to
    /// release it: a caller that may have to release it (the managed exit, when the person stays) uses the two.
    /// </summary>
    public async Task<IDisposable> SuspendAsync(CancellationToken cancellationToken)
    {
        var suspension = Suspend();
        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        return suspension;
    }

    /// <summary>
    /// Whether the run of <paramref name="record"/>, left unfinished with the log <paramref name="entries"/>, resumes by itself,
    /// and why; null when only a person may resume it.
    /// </summary>
    internal static (RunResumeCause? Cause, string Reason) Decide(RunRecord record, IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries)
    {
        if (!record.Options.AutoResumeConsent)
        {
            return (null, "Su cabecera no tiene el consentimiento para reanudarse sola: la reanudas tú.");
        }

        if (entries.All(e => e.Entry.Kind != DecisionKind.RunStarted) || entries.Any(e => e.Entry.Kind == DecisionKind.RunFinished))
        {
            return (null, "No está a medias.");
        }

        // Only what happened since the run was last resumed: that resume started it again.
        var last = entries.Select((e, i) => (e, i)).LastOrDefault(x => x.e.Entry.Kind == DecisionKind.RunResumed).i;
        var session = entries.Skip(entries.Any(e => e.Entry.Kind == DecisionKind.RunResumed) ? last + 1 : 0).Select(e => e.Payload).ToList();
        if (session.OfType<RunDispatchChangedPayload>().LastOrDefault() is { } dispatch && !dispatch.AllowsAutomaticResume())
        {
            return (null, "La pausaste: nunca se reanuda sola.");
        }

        // The last choice of a person decides, not the last exit: a person who chose (pause and leave, or leave now) is
        // never overruled by Windows ending the session afterwards.
        var exits = session.OfType<RunExitChosenPayload>().ToList();
        if (exits.Any(e => !e.AllowsAutomaticResume()))
        {
            return (null, "La dejaste al salir: nunca se reanuda sola, aunque Windows cerrara la sesión después.");
        }

        if (exits.Count > 0)
        {
            return (RunResumeCause.ConsentAfterSessionEnded, "Windows cerró la sesión con la ejecución en marcha.");
        }

        return WasWaitingForQuota(entries)
            ? (RunResumeCause.ConsentAfterQuotaWait, "Esperaba cuota cuando se cerró la aplicación.")
            : (RunResumeCause.ConsentAfterUnrequestedClose, "La cortó un cierre que no pediste.");
    }

    /// <summary>
    /// Whether the run was only waiting for quota when the app closed: a task blocked by a limit, and none running nor any
    /// launch without its end.
    /// </summary>
    private static bool WasWaitingForQuota(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries)
    {
        var last = new Dictionary<TaskId, TaskStatusChangedPayload>();
        var open = 0;
        foreach (var payload in entries.Select(e => e.Payload))
        {
            switch (payload)
            {
                case TaskStatusChangedPayload change:
                    last[change.Task] = change;
                    break;
                case AgentRunStartedPayload { Task: not null }:
                    open++;
                    break;
                case AgentRunFinishedPayload { Task: not null }:
                    open--;
                    break;
            }
        }

        return open <= 0
            && last.Values.All(c => c.To != WorkTaskStatus.Running)
            && last.Values.Any(c => c.To == WorkTaskStatus.Blocked && BlockReason.Parse(c.BlockReason) is { Kind: BlockKind.RateLimitedUntil });
    }

    /// <summary>The single reader of the queue: one pass at a time, and each barrier once the passes before it are done.</summary>
    private async Task ConsumeAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (request)
            {
                case Barrier barrier:
                    barrier.Done.TrySetResult();
                    break;
                case Pass pass when pass.Token.IsCancellationRequested:
                    pass.Done.TrySetCanceled(pass.Token);
                    break;
                case Pass pass:
                    try
                    {
                        pass.Done.TrySetResult(await PassAsync(pass.Project, pass.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (pass.Token.IsCancellationRequested)
                    {
                        pass.Done.TrySetCanceled(pass.Token);
                    }
                    catch (Exception ex)
                    {
                        // The log could not be read, or a handler of Resumed threw: the caller of the pass learns it.
                        pass.Done.TrySetException(ex);
                    }

                    break;
            }
        }
    }

    private async Task<IReadOnlyList<ScheduledResume>> PassAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        if (Suspended)
        {
            return [];
        }

        var log = await _decisions.LoadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return [];
        }

        // A run is left unfinished, and no process of this app runs it, exactly when the orchestrator lists it as interrupted.
        var left = (await _orchestrator.ListRunsAsync(project, cancellationToken).ConfigureAwait(false))
            .Where(r => r.Phase == RunPhase.Interrupted)
            .ToList();
        var outcome = new List<ScheduledResume>();
        foreach (var summary in left)
        {
            if (Suspended)
            {
                break; // the app is leaving: nothing more is resumed
            }

            if (await OneAsync(project, summary.Id, log.Entries, cancellationToken).ConfigureAwait(false) is not { } done)
            {
                continue;
            }

            outcome.Add(done);
            if (done is { Resumed: { } resumed, Cause: { } cause })
            {
                Resumed?.Invoke(this, new ScheduledResumeEventArgs(project, resumed, cause));
            }
        }

        return outcome;
    }

    /// <summary>A result with its sentence and the coded text derived from it (the same sentences <see cref="LoggedTexts.ResumeReason"/> knows).</summary>
    private static ScheduledResume Said(RunId run, RunResumeCause? cause, IOrchestrationRun? resumed, string reason) =>
        new(run, cause, resumed, reason, LoggedTexts.ResumeReason(reason));

    /// <summary>What the pass does with one run left unfinished; null for one that is not registered.</summary>
    private async Task<ScheduledResume?> OneAsync(ProjectContext project, RunId run, IReadOnlyList<DecisionEntry> log, CancellationToken cancellationToken)
    {
        var key = project.ProjectId + "/" + run.Value;
        if (_failed.TryGetValue(key, out var failure))
        {
            return Said(run, null, null, "En esta sesión de la aplicación ya no se intenta reanudarla sola: el intento anterior falló (" + failure + ").");
        }

        var entries = RunLog.Of(log, run.Value);
        RunResumeCause? cause = null;
        try
        {
            if (await _runs.FindAsync(project.ProjectId, run, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                return null;
            }

            (cause, var reason) = Decide(record, entries);
            if (cause is null)
            {
                return Said(run, null, null, reason);
            }

            if (Suspended)
            {
                return Said(run, null, null, "La reanudación sola está suspendida mientras la aplicación sale.");
            }

            // A remote operation may hold the project for up to 180 s (docs/21 D-25): suspending ends that wait, and a resume that did get the project asks again.
            var resume = await _orchestrator.ResumeByConsentAsync(project, run, cancellationToken, () => !Suspended, Volatile.Read(ref _suspendSignal).Token).ConfigureAwait(false);
            return new ScheduledResume(run, resume.Cause, resume.Run, resume.Reason, resume.ReasonText ?? LoggedTexts.ResumeReason(resume.Reason));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (Suspended)
        {
            // The wait for the project ended because the app is leaving: nothing was written, and it is not a failure of the run.
            return Said(run, null, null, "La reanudación sola está suspendida mientras la aplicación sale.");
        }
        catch (UserTextException busy) when (busy.Text.Code == ProjectActivityEntry.BusyRemoteCode)
        {
            // The remote operation did not let go in time: the engine wrote nothing, so the run is not marked as failed for the session; the next pass tries again.
            return Said(run, cause, null, "No se pudo reanudar: " + busy.Message);
        }
        catch (Exception ex)
        {
            // Whatever went wrong is this run's alone: the pass goes on with the others. A failure that wrote to its log is
            // not tried again in this session of the app; one that wrote nothing (the project held by a debate, say) is.
            if (await WroteAsync(project, run, entries.Count, cancellationToken).ConfigureAwait(false))
            {
                _failed[key] = ex.Message;
            }

            return Said(run, cause, null, "No se pudo reanudar: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether the failed resume of <paramref name="run"/> wrote to its log: more entries than the <paramref name="before"/> it
    /// had when the pass read it (an alert that it is not resumable, or <c>RunResumed</c> before its recovery failed). A log
    /// that cannot be read counts as written: the run is not tried again in this session.
    /// </summary>
    private async Task<bool> WroteAsync(ProjectContext project, RunId run, int before, CancellationToken cancellationToken)
    {
        try
        {
            var log = await _decisions.LoadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
            return RunLog.Of(log.Entries, run.Value).Count > before;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    private abstract class Request;

    /// <summary>A pass over the runs of one project, for whoever asked.</summary>
    private sealed class Pass(ProjectContext project, CancellationToken token) : Request
    {
        public ProjectContext Project { get; } = project;

        public CancellationToken Token { get; } = token;

        public TaskCompletionSource<IReadOnlyList<ScheduledResume>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Done when the reader reaches it: every pass asked for before it has ended.</summary>
    private sealed class Barrier : Request
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One suspension of the resumes, released once however often it is disposed.</summary>
    private sealed class Suspension : IDisposable
    {
        private ResumeScheduler? _owner;

        public Suspension(ResumeScheduler owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._suspensions);
            Interlocked.Exchange(ref owner._suspendSignal, new CancellationTokenSource()).Cancel();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
            {
                Interlocked.Decrement(ref owner._suspensions);
            }
        }
    }
}
