using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Desktop.Orchestrator.Session;

/// <summary>
/// What a pass of the consented resume did over a project: what it did with each run left unfinished, and whether it went over
/// the whole project. One held back (the managed exit does not exist yet, or holds the resumes while the app leaves) did not:
/// the shell asks for it again once the passes may run (<see cref="IConsentedResumes.Available"/>).
/// </summary>
public sealed record ConsentedPass(IReadOnlyList<ScheduledResume> Outcome, bool Complete)
{
    /// <summary>A pass held back before it looked at anything.</summary>
    public static ConsentedPass Skipped { get; } = new([], Complete: false);
}

/// <summary>
/// The resume after a restart that the consent of a run allows (F8-4; docs/16 D-39, ADR-0034), as the shell asks it: one pass
/// per project, and the runs it resumed. An interface so the shell can be tested without the engine.
/// </summary>
public interface IConsentedResumes
{
    /// <summary>
    /// Raised for every run a pass resumed, as soon as it is going, on whatever thread the pass runs: the shell attaches it to
    /// the monitor of the «Orquestador» document of its project. The events of the run wait for that one reader.
    /// </summary>
    event EventHandler<ScheduledResumeEventArgs>? Resumed;

    /// <summary>
    /// Raised when the passes may run again, on the thread that let them: the managed exit exists now, or the last suspension
    /// was released (the person stayed). A pass held back meanwhile is asked for again.
    /// </summary>
    event EventHandler? Available;

    /// <summary>
    /// One pass over the project at <paramref name="projectRoot"/>: the runs its consent allows are resumed, and what was done
    /// with each run left unfinished is returned. A project without its storage does nothing. A pass while the resumes are held
    /// back, or cut short by a hold taken meanwhile, is not <see cref="ConsentedPass.Complete"/>.
    /// </summary>
    Task<ConsentedPass> ResumeAsync(string projectRoot, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IConsentedResumes"/> over the engine's <see cref="ResumeScheduler"/>, which decides from the log and resumes only
/// with the consent of each header: nothing here launches a run by itself.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Opening a folder never creates <c>.agentstudio/</c>: a project without its database has no run to resume, and is not
/// opened for a pass.</item>
/// <item>No pass runs until the managed exit exists (<see cref="Enable"/>, once the tray started; ADR-0036): without it, closing
/// the app writes no choice the scheduler could read, and a deliberate close would look like one nobody asked for.</item>
/// <item>While the app leaves, the resumes are suspended (<see cref="Suspend"/>, the scheduler's, at once) and the pass in
/// progress is waited for (<see cref="WaitForIdleAsync"/>): the managed exit holds a suspension from before it reads the work
/// going on until the person stays, and the end of the Windows session takes one it never releases (ADR-0036). A pass that
/// runs then resumes nothing, and the one in progress resumes no further run; every run it resumed before has been announced
/// when the wait ends. Cancelling the wait releases nothing: whoever took the suspension releases it. Once the last is
/// released, <see cref="Available"/> says so.</item>
/// </list>
/// </remarks>
public sealed class ConsentedResumes : IConsentedResumes, ISuspendableResumes
{
    private readonly ResumeScheduler? _scheduler;
    private readonly IProjectWorkspace? _workspace;

    /// <summary>What holds the passes back: one until the managed exit exists, and one for each suspension held.</summary>
    private int _holds = 1;
    private int _enabled;

    /// <summary>Every suspension taken so far: a pass during which one was taken may have been cut short.</summary>
    private long _taken;

    /// <param name="scheduler">The engine's scheduler, over the one orchestrator of the app; null when the engine was not composed.</param>
    /// <param name="workspace">Opens a project to know its id; null when it was not composed.</param>
    public ConsentedResumes(ResumeScheduler? scheduler, IProjectWorkspace? workspace)
    {
        _scheduler = scheduler;
        _workspace = workspace;
        if (scheduler is not null)
        {
            scheduler.Resumed += (_, resumed) => Resumed?.Invoke(this, resumed);
        }
    }

    public event EventHandler<ScheduledResumeEventArgs>? Resumed;

    public event EventHandler? Available;

    /// <summary>The engine's scheduler, when there is one.</summary>
    public ResumeScheduler? Scheduler => _scheduler;

    /// <summary>Whether there is anything to resume with: the scheduler and the workspace were composed.</summary>
    public bool IsAvailable => _scheduler is not null && _workspace is not null;

    /// <summary>Whether the passes are held back now: the managed exit does not exist yet, or a suspension is held.</summary>
    public bool IsHeld => Volatile.Read(ref _holds) > 0;

    /// <summary>
    /// The managed exit exists (the tray started, ADR-0036): from now on the passes run, and <see cref="Available"/> says so.
    /// Once; later calls do nothing.
    /// </summary>
    public void Enable()
    {
        if (Interlocked.Exchange(ref _enabled, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>
    /// Suspends the resumes at once, until the returned suspension is disposed (<see cref="ResumeScheduler.Suspend"/>: several
    /// can be held, and the last release lets them go on). Without a scheduler no pass resumes anything anyway.
    /// </summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _holds);
        Interlocked.Increment(ref _taken);
        return new Hold(this, _scheduler?.Suspend());
    }

    /// <summary>
    /// Completes once every pass asked for before the call has ended, every run it resumed announced through <see cref="Resumed"/>
    /// (<see cref="ResumeScheduler.WaitForIdleAsync"/>). Cancelling it releases nothing. Without a scheduler no pass runs.
    /// </summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken) =>
        _scheduler?.WaitForIdleAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task<ConsentedPass> ResumeAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!IsAvailable || !File.Exists(Path.Combine(projectRoot, ".agentstudio", "agentstudio.db")))
        {
            return new ConsentedPass([], Complete: true);
        }

        var taken = Interlocked.Read(ref _taken);
        if (IsHeld)
        {
            return ConsentedPass.Skipped;
        }

        var project = await _workspace!.OpenAsync(projectRoot, cancellationToken).ConfigureAwait(true);
        var outcome = await _scheduler!.ResumeConsentedAsync(project, cancellationToken).ConfigureAwait(true);
        return new ConsentedPass(outcome, Complete: !IsHeld && Interlocked.Read(ref _taken) == taken);
    }

    /// <summary>One hold released: the last one lets the passes run again.</summary>
    private void Release()
    {
        if (Interlocked.Decrement(ref _holds) == 0)
        {
            Available?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A suspension handed out: the scheduler's, and one hold of the passes; released once.</summary>
    private sealed class Hold(ConsentedResumes owner, IDisposable? scheduler) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            scheduler?.Dispose();
            owner.Release();
        }
    }
}
