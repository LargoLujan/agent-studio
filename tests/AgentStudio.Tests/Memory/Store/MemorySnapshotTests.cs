using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Memory.Store;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// What recall selects from is served from memory while the log only grows (ADR-0010, addendum of 2026-11-04): a recall
/// with nothing new reads no row, one after a write reads only the row of what was announced, and an entry announced before
/// its row was stored comes once the row is there. A row changed where the stamp of the store cannot see it is never served
/// changed: it is left out, with its alert, at the next whole verification of the log (a critical moment, five minutes).
/// With a trigger of the memory dropped, every recall reads and checks every row again.
/// </summary>
public sealed class MemorySnapshotTests
{
    private const string RestoreNoUpdate = "CREATE TRIGGER memory_entries_no_update BEFORE UPDATE ON memory_entries BEGIN SELECT RAISE(ABORT, 'memory_entries is append-only'); END;";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What makes the log be verified whole again.</summary>
    public enum WholeVerification
    {
        /// <summary>A load at a critical moment (<see cref="LogLoad.Full"/>).</summary>
        CriticalMoment,

        /// <summary>Five minutes since the last whole verification.</summary>
        FiveMinutesLater,
    }

    private static MemoryDraft Draft(int i) =>
        new(MemoryKind.Decision, $"Tema {i}", $"Decisión {i}: se despliega con blue green.", MemoryOrigin.System, $"registro:20261105-a1b2c3#{i}");

    /// <summary>Everything anchored, as the semantic index recalls it before every search.</summary>
    private static Task<IReadOnlyList<MemoryEntry>> EverythingAsync(MemoryWorld world) =>
        world.Memory.RecallAsync(world.Project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: false), Ct);

    private static async Task<string[]> IdsAsync(MemoryWorld world) => [.. (await EverythingAsync(world)).Select(e => e.Id)];

    private static async Task<MemoryEntry> RecordAsync(MemoryWorld world, int i)
    {
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        return await world.Memory.RecordAsync(world.Project, Draft(i), Ct);
    }

    [Fact]
    public async Task A_recall_with_nothing_new_reads_no_row_from_the_store()
    {
        using var world = await MemoryWorld.CreateAsync();
        for (var i = 1; i <= 3; i++)
        {
            await RecordAsync(world, i);
        }

        var first = await IdsAsync(world);
        var (searches, finds) = (world.Spy.Searches, world.Spy.Finds.Count);

        var again = await IdsAsync(world);

        Assert.Equal(3, again.Length);
        Assert.Equal(first, again);
        Assert.Equal((searches, finds), (world.Spy.Searches, world.Spy.Finds.Count));
    }

    [Fact]
    public async Task After_a_write_the_next_recall_reads_only_the_row_of_what_was_announced()
    {
        using var world = await MemoryWorld.CreateAsync();
        var kept = await RecordAsync(world, 1);
        Assert.Equal([kept.Id], await IdsAsync(world));
        var whole = world.Spy.WholeReads;

        var added = await RecordAsync(world, 2);
        var recalled = await IdsAsync(world);

        Assert.Equal([added.Id, kept.Id], recalled);
        Assert.Equal(whole, world.Spy.WholeReads);
        Assert.Equal([added.Id], Assert.Single(world.Spy.Finds));
    }

    [Fact]
    public async Task An_entry_announced_before_its_row_was_stored_comes_once_the_row_is_there()
    {
        using var world = await MemoryWorld.CreateAsync();
        var kept = await RecordAsync(world, 1);
        Assert.Equal([kept.Id], await IdsAsync(world));
        string[]? between = null;
        world.Spy.BeforeAppend = async _ => between = await IdsAsync(world);

        var added = await RecordAsync(world, 2);
        world.Spy.BeforeAppend = null;

        Assert.NotNull(between);
        Assert.Equal([kept.Id], between); // announced, and its row not stored yet
        Assert.Equal([added.Id, kept.Id], await IdsAsync(world));
        Assert.Empty(await world.AlertsAsync());
    }

    [Theory]
    [InlineData(StampBlindChange.SchemaVersionForged, WholeVerification.CriticalMoment)]
    [InlineData(StampBlindChange.SchemaVersionForged, WholeVerification.FiveMinutesLater)]
    [InlineData(StampBlindChange.FileReplaced, WholeVerification.CriticalMoment)]
    [InlineData(StampBlindChange.FileReplaced, WholeVerification.FiveMinutesLater)]
    public async Task A_row_changed_where_the_stamp_cannot_see_is_never_served_changed_and_is_left_out_at_the_next_whole_verification(
        StampBlindChange change,
        WholeVerification whole)
    {
        using var world = await MemoryWorld.CreateAsync();
        var entry = await RecordAsync(world, 1);
        Assert.True((await world.Instance.Recorder.LoadAsync(world.Project.ProjectId, LogLoad.Full, Ct)).Verification.IsValid); // the five minutes start here
        Assert.Equal([entry.Id], await IdsAsync(world));

        StampBlindTampering.Apply(world.Instance, change, "DROP TRIGGER memory_entries_no_update;", $"UPDATE memory_entries SET content = 'Se despliega a mano.' WHERE id = '{entry.Id}';", RestoreNoUpdate);

        // Until the log is verified whole again the change is not seen, and nobody is given it: the entry is served as verified.
        var served = Assert.Single(await EverythingAsync(world));
        Assert.Equal(entry.Content, served.Content);
        Assert.Empty(await world.AlertsAsync());

        if (whole == WholeVerification.CriticalMoment)
        {
            Assert.True((await world.Instance.Recorder.LoadAsync(world.Project.ProjectId, LogLoad.Full, Ct)).Verification.IsValid);
        }
        else
        {
            world.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Empty(await EverythingAsync(world));
        var alert = Assert.Single(await world.AlertsAsync());
        Assert.Equal(ProjectMemory.ModifiedAlert, alert.Code);
        Assert.Contains(entry.Id, alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_trigger_of_the_memory_dropped_every_recall_reads_and_checks_every_row_so_a_later_change_is_left_out()
    {
        using var world = await MemoryWorld.CreateAsync();
        var entry = await RecordAsync(world, 1);
        Assert.Equal([entry.Id], await IdsAsync(world));
        using (var connection = world.Instance.Connect())
        {
            Exec(connection, "DROP TRIGGER memory_entries_no_update;");
        }

        var whole = world.Spy.WholeReads;
        Assert.Equal([entry.Id], await IdsAsync(world));
        Assert.Equal([entry.Id], await IdsAsync(world));
        Assert.Equal(whole + 2, world.Spy.WholeReads); // with the trigger missing, no recall serves what it verified before

        // The change no longer touches the schema, and the next recall still finds it.
        using (var connection = world.Instance.Connect())
        {
            Exec(connection, $"UPDATE memory_entries SET content = 'Se despliega a mano.' WHERE id = '{entry.Id}';");
        }

        Assert.Empty(await IdsAsync(world));
        Assert.Equal(ProjectMemory.ModifiedAlert, Assert.Single(await world.AlertsAsync()).Code);
    }
}
