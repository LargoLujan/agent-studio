using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Decisions;
using AgentStudio.Memory.Store;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// The snapshot of the memory under concurrent recalls and writes (the review of F12-5): a recall that read the log, held up
/// while an entry is recorded and another recall publishes a newer snapshot, must neither fail (it threw a
/// <see cref="NullReferenceException"/>: the newer rows had no announcement in its older ledger) nor put back an older
/// snapshot. One reader per project builds and extends it; recalls take what it published.
/// </summary>
public sealed class MemorySnapshotRaceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A recorder whose next load, once armed, gets the log and then waits until the test lets it go on.</summary>
    private sealed class HeldRecorder(IDecisionRecorder inner) : IDecisionRecorder
    {
        private int _armed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) =>
            LoadAsync(projectId, LogLoad.Incremental, cancellationToken);

        public async Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken)
        {
            var state = await inner.LoadAsync(projectId, load, cancellationToken);
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
            }

            return state;
        }

        public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default) =>
            inner.RecordAsync(projectId, actor, kind, summary, payload, corrects, cancellationToken);
    }

    private static MemoryDraft Draft(int i) =>
        new(MemoryKind.Decision, $"Tema {i}", $"Decisión {i}: se despliega con blue green.", MemoryOrigin.System, $"registro:20261105-a1b2c3#{i}");

    private static Task<IReadOnlyList<MemoryEntry>> EverythingAsync(ProjectMemory memory, MemoryWorld world) =>
        memory.RecallAsync(world.Project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: false), Ct);

    [Fact]
    public async Task A_recall_held_up_after_it_read_the_log_while_a_newer_snapshot_is_published_neither_fails_nor_puts_back_an_older_one()
    {
        using var world = await MemoryWorld.CreateAsync();
        var held = new HeldRecorder(world.Instance.Recorder);
        var memory = new ProjectMemory(held, world.Spy, world.Activity, world.Redactor, world.Clock);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var first = await memory.RecordAsync(world.Project, Draft(1), Ct);
        Assert.Single(await EverythingAsync(memory, world));

        // B reads the log and is held there.
        held.Arm();
        var b = Task.Run(() => EverythingAsync(memory, world), Ct);
        await held.Entered.Task.WaitAsync(Guard, Ct);

        // Meanwhile an entry is recorded and another recall publishes the snapshot with it, if nothing stops them.
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var written = Task.Run(() => memory.RecordAsync(world.Project, Draft(2), Ct), Ct);
        var c = Task.Run(
            async () =>
            {
                await written;
                return await EverythingAsync(memory, world);
            },
            Ct);
        await Task.WhenAny(c, Task.Delay(TimeSpan.FromSeconds(2), Ct));

        held.Release.TrySetResult();
        var recalledByB = await b.WaitAsync(Guard, Ct);
        var second = await written.WaitAsync(Guard, Ct);
        var recalledByC = await c.WaitAsync(Guard, Ct);

        Assert.Contains(recalledByB, e => e.Id == first.Id);
        Assert.Equal([second.Id, first.Id], recalledByC.Select(e => e.Id));
        Assert.Equal([second.Id, first.Id], (await EverythingAsync(memory, world)).Select(e => e.Id));
        Assert.Empty(await world.AlertsAsync());
    }
}
