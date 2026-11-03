using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>Opens every root as the same project id; records what it opened.</summary>
internal sealed class FakeProjectWorkspace(string projectId = "p-1") : IProjectWorkspace
{
    public List<string> Opened { get; } = [];

    public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
    {
        Opened.Add(projectRoot);
        return Task.FromResult(new ProjectContext(projectId, projectRoot));
    }
}

/// <summary>
/// The memory of a project as the test sets it: <see cref="Entries"/> is what recall answers (every trust), and the reviews
/// are recorded. A review can be held (<see cref="HoldReviews"/>) as the real one waits while a run goes on.
/// </summary>
internal sealed class FakeProjectMemory : IProjectMemory
{
    public List<MemoryEntry> Entries { get; } = [];

    public List<(ProjectContext Project, MemoryQuery Query)> Recalls { get; } = [];

    public List<(ProjectContext Project, string Entry, MemoryReview Decision, string DecidedBy, string? Comment)> Reviews { get; } = [];

    /// <summary>When set, a review is recorded only once this completes (the real one waits for the run to end).</summary>
    public TaskCompletionSource? HoldReviews { get; set; }

    /// <summary>When set, every review throws it.</summary>
    public Exception? ReviewFailure { get; set; }

    public Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct) => throw new NotSupportedException("The memory screen records no entry.");

    public async Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct)
    {
        if (HoldReviews is { } hold)
        {
            await hold.Task;
        }

        if (ReviewFailure is not null)
        {
            throw ReviewFailure;
        }

        Reviews.Add((project, entry, decision, decidedBy, comment));
    }

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct)
    {
        Recalls.Add((project, query));
        return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. Entries]);
    }
}

/// <summary>Answers a fixed export result and counts the exports.</summary>
internal sealed class FakeMemoryExporter(MemoryExportResult? result = null) : IMemoryExporter
{
    public List<ProjectContext> Exports { get; } = [];

    public MemoryExportResult Result { get; set; } = result ?? new MemoryExportResult(true, ["memory/cronologico-2026-10.md", "memory/tema-decision.md"], 7);

    public Task<MemoryExportResult> ExportAsync(ProjectContext project, CancellationToken ct)
    {
        Exports.Add(project);
        return Task.FromResult(Result);
    }
}

/// <summary>Whether a run or a planning goes on, as the test says.</summary>
internal sealed class FakeRunActivity : IRunActivity
{
    public bool Busy { get; set; }

    public bool IsBusy(string projectId) => Busy;
}

/// <summary>Answers the summary the test sets and records the days it was asked.</summary>
internal sealed class FakeDailySummaryService(Func<DateOnly, DailySummaryResult>? result = null) : IDailySummaryService
{
    public List<(ProjectContext Project, DateOnly Day)> Asked { get; } = [];

    public Func<DateOnly, DailySummaryResult> Result { get; set; } = result ?? (day => new DailySummaryResult(day, false, new string('a', 64), "Resumen", 0, 0, null));

    public Task<DailySummaryResult> SummarizeAsync(ProjectContext project, DateOnly day, CancellationToken ct)
    {
        Asked.Add((project, day));
        return Task.FromResult(Result(day));
    }
}
