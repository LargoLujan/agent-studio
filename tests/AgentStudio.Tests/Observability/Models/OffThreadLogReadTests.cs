using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Estimates;
using AgentStudio.Observability.Models;
using AgentStudio.Tests.Observability.Usage;

namespace AgentStudio.Tests.Observability.Models;

/// <summary>
/// What the log teaches and the estimator read the log off the caller's thread: a reader that completes synchronously (as
/// SQLite does) never blocks the window that asked. Each call returns at once, still running, and the read happens on another
/// thread.
/// </summary>
public sealed class OffThreadLogReadTests
{
    /// <summary>A reader that does its work synchronously inside <c>ReadAsync</c>, held until the test opens the gate.</summary>
    private sealed class BlockingReader(ProjectLogSnapshot snapshot) : IProjectLogReader, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public int? ThreadId { get; private set; }

        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            _gate.Wait(TimeSpan.FromSeconds(10), ct);
            return Task.FromResult(snapshot);
        }

        public void Open() => _gate.Set();

        public void Dispose() => _gate.Dispose();
    }

    private static ProjectLogSnapshot Snapshot()
    {
        var log = new UsageLogBuilder();
        log.Start("r1", "T1", 1).Finish("r1", "T1", 1, session: "s1", usage: Snapshots.Claude(1000, 0, 0, 100, 0.1m), reportedModel: "claude-sonnet-5-5");
        return new ProjectLogSnapshot("p1", "C:\\repo", log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored);
    }

    [Fact]
    public async Task Learning_from_the_log_returns_at_once_and_reads_on_another_thread()
    {
        using var reader = new BlockingReader(Snapshot());
        var caller = Environment.CurrentManagedThreadId;

        var learning = new ProjectModelLearning(reader, new BuiltInModelCatalog()).LearnAsync("C:\\repo", TestContext.Current.CancellationToken);

        Assert.False(learning.IsCompleted);
        reader.Open();
        var learned = await learning;
        Assert.NotEqual(caller, reader.ThreadId);
        Assert.Single(learned.Models);
    }

    [Fact]
    public async Task Estimating_returns_at_once_and_reads_on_another_thread()
    {
        using var reader = new BlockingReader(Snapshot());
        var caller = Environment.CurrentManagedThreadId;
        var request = new UsageEstimateRequest(new ProjectContext("p1", "C:\\repo"), new AgentId("ejecutor"), ProviderId.ClaudeCode, "sonnet", AgentRunPurpose.Executor);

        var estimating = new UsageEstimator(reader, new BuiltInModelCatalog()).EstimateAsync(request, TestContext.Current.CancellationToken);

        Assert.False(estimating.IsCompleted);
        reader.Open();
        var estimate = await estimating;
        Assert.NotEqual(caller, reader.ThreadId);
        Assert.Equal(1, estimate.References);
    }
}
