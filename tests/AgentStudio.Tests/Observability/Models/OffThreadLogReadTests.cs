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
/// SQLite does) never blocks the window that asked. Each call returns at once, still running, before the read is done; were
/// the read on the caller's thread, the call would only return once the reader let it go.
/// </summary>
public sealed class OffThreadLogReadTests
{
    /// <summary>A reader that does its work synchronously inside <c>ReadAsync</c>, held until the test opens the gate.</summary>
    private sealed class BlockingReader(ProjectLogSnapshot snapshot) : IProjectLogReader, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        private int _finished;

        /// <summary>Whether a read got past the gate.</summary>
        public bool Finished => Volatile.Read(ref _finished) == 1;

        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
        {
            _gate.Wait(TimeSpan.FromSeconds(10), ct);
            Volatile.Write(ref _finished, 1);
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
    public async Task Learning_from_the_log_returns_before_the_read_is_done()
    {
        using var reader = new BlockingReader(Snapshot());

        var learning = new ProjectModelLearning(reader, new BuiltInModelCatalog()).LearnAsync("C:\\repo", TestContext.Current.CancellationToken);

        Assert.False(reader.Finished);
        Assert.False(learning.IsCompleted);
        reader.Open();
        var learned = await learning;
        Assert.True(reader.Finished);
        Assert.Single(learned.Models);
    }

    [Fact]
    public async Task Estimating_returns_before_the_read_is_done()
    {
        using var reader = new BlockingReader(Snapshot());
        var request = new UsageEstimateRequest(new ProjectContext("p1", "C:\\repo"), new AgentId("ejecutor"), ProviderId.ClaudeCode, "sonnet", AgentRunPurpose.Executor);

        var estimating = new UsageEstimator(reader, new BuiltInModelCatalog()).EstimateAsync(request, TestContext.Current.CancellationToken);

        Assert.False(reader.Finished);
        Assert.False(estimating.IsCompleted);
        reader.Open();
        var estimate = await estimating;
        Assert.True(reader.Finished);
        Assert.Equal(1, estimate.References);
    }
}
