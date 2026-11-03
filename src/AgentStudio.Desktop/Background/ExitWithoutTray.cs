using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// Closing the window when the managed exit does not exist: before the tray started, or when it could not start (ADR-0036).
/// The app ends without its question, as the person chose by closing it, so each piece of work going on gets «Salir ahora» in
/// its log (<see cref="RunExitChoice.ExitNow"/>), as the managed exit writes it: a run with the consent of its header is never
/// read as cut by a close nobody asked for, and does not resume by itself at the next start (docs/16 D-39). Work without an id
/// (an audit, a planning) has no entry, as in the managed exit. The end of the Windows session is not this: nobody chose it.
/// </summary>
public sealed class ExitWithoutTray
{
    /// <summary>How long the close may wait for the writes: as long as the end of the session (ADR-0036, decision 8).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly BackgroundWorkRegistry _work;
    private readonly IRunExitRecorder _recorder;
    private readonly ILogger? _log;

    public ExitWithoutTray(BackgroundWorkRegistry work, IRunExitRecorder recorder, ILogger<ExitWithoutTray>? log = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(recorder);
        _work = work;
        _recorder = recorder;
        _log = log;
    }

    /// <summary>
    /// Writes «Salir ahora» in the log of each piece of work going on, waiting at most <paramref name="budget"/>; returns how many
    /// it was for. The work is read on the calling (UI) thread, and the writes run off it, each on its own: a log that cannot be
    /// written never holds the others, and is noted in the app's log.
    /// </summary>
    public int Record(TimeSpan budget)
    {
        var going = new List<(ProjectContext Project, string WorkId)>();
        foreach (var work in _work.Active)
        {
            try
            {
                if (work.Project is { } project && work.WorkId is { } id)
                {
                    going.Add((project, id));
                }
            }
#pragma warning disable CA1031 // A piece of work that cannot be read gets no entry; the others still do.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogWarning(ex, "A piece of work could not be read for the exit without the tray.");
            }
        }

        if (going.Count == 0)
        {
            return 0;
        }

        var writes = Task.Run(() => RecordAsync(going));
        try
        {
            if (!writes.Wait(budget))
            {
                _log?.LogWarning("The app ended before the exit was written in the log of {Count} pieces of work.", going.Count);
            }
        }
        catch (AggregateException ex)
        {
            _log?.LogWarning(ex, "The exit without the tray could not be written in every log.");
        }

        return going.Count;
    }

    private async Task RecordAsync(IReadOnlyList<(ProjectContext Project, string WorkId)> going)
    {
        foreach (var (project, id) in going)
        {
            try
            {
                await _recorder.RecordAsync(project, id, RunExitChoice.ExitNow, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A log that cannot be written must not keep the app from closing: it is noted in the app's log.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.LogError(ex, "The exit without the tray could not be written in the log of {Work}.", id);
            }
        }
    }
}
