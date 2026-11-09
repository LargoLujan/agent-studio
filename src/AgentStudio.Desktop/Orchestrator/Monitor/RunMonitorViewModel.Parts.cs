using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

// Phase 13 (docs/17 D-13, F13-6, WP8): the parts of the prompt of the attempt in course, read from the verified log.

public sealed partial class RunMonitorViewModel
{
    /// <summary>How many times the parts of an attempt were asked: once when it starts and once more at its first step.</summary>
    private readonly Dictionary<(TaskId Task, int Attempt), int> _partsAsked = [];
    private readonly List<Task> _partsChecks = [];

    /// <summary>
    /// Where the parts of each attempt are read from; the shell gives it once. Without it (or without the project of the run)
    /// the bar of context by parts says «sin desglose».
    /// </summary>
    public IContextPartsSource? ContextPartsSource { get; set; }

    /// <summary>Completes once every read of the parts asked so far has finished and been applied.</summary>
    public Task PartsCheck
    {
        get
        {
            _partsChecks.RemoveAll(t => t.IsCompleted);
            return Task.WhenAll([.. _partsChecks]);
        }
    }

    /// <summary>A new attempt of the task started: what the one before showed goes, and the parts of this one are read.</summary>
    private void StartParts(MonitorTaskViewModel task)
    {
        var attempt = Math.Max(task.Attempt, 1);
        task.ForgetParts(attempt);
        RequestParts(task, attempt);
    }

    /// <summary>The start of the attempt may be written a moment after the monitor hears of it: the first step asks once more if there is nothing.</summary>
    private void RetryPartsIfMissing(MonitorTaskViewModel task)
    {
        var attempt = Math.Max(task.Attempt, 1);
        if (!task.HasParts && _partsAsked.GetValueOrDefault((task.Id, attempt)) < 2)
        {
            RequestParts(task, attempt);
        }
    }

    private void RequestParts(MonitorTaskViewModel task, int attempt)
    {
        if (ContextPartsSource is not { } source || _project is not { } project || RunId is not { } run || IsInspecting || task.Kind == Domain.Work.WorkTaskKind.Condition)
        {
            return;
        }

        var key = (task.Id, attempt);
        _partsAsked[key] = _partsAsked.GetValueOrDefault(key) + 1;
        _partsChecks.RemoveAll(t => t.IsCompleted);
        _partsChecks.Add(ReadPartsAsync(source, project, run, task, attempt, _generation));
    }

    private async Task ReadPartsAsync(IContextPartsSource source, Application.Projects.ProjectContext project, RunId run, MonitorTaskViewModel task, int attempt, int generation)
    {
        IReadOnlyList<ContextPartSnapshot>? parts = null;
        var failed = false;
        try
        {
            parts = await source.ReadAsync(project, run, task.Id, attempt, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The log is behind a port: whatever it throws is shown as «sin leer», never thrown on a background task.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            failed = true;
        }

        Post(generation, () =>
        {
            task.SetParts(attempt, parts, failed);
            MarkLive(task);
        });
    }
}
