using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Decisions;
using AgentStudio.Memory.Store;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// What the snapshot of the memory reflects without reading its rows again, and what it does with what it cannot vouch for
/// (the review of F12-5): reviews and the trust of the journal change the entries it serves; recalls and writes at once
/// over a snapshot already loaded never fail and never go back; a query by category or topic selects on the entry verified,
/// never on the row of the store; an entry announced without its row is remembered as missing, and alerted, until the log
/// is verified whole again or this process stores the row; a project forgotten is read again; and a row replaced through its
/// rowid by a connection without recursive triggers is never served and is found at the next whole verification.
/// </summary>
public sealed class MemorySnapshotStateTests
{
    private const string RestoreNoUpdate = "CREATE TRIGGER memory_entries_no_update BEFORE UPDATE ON memory_entries BEGIN SELECT RAISE(ABORT, 'memory_entries is append-only'); END;";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryDraft Draft(int i, MemoryOrigin origin = MemoryOrigin.System) =>
        new(MemoryKind.Decision, $"Tema {i}", $"Decisión {i}: se despliega con blue green.", origin, $"registro:20261105-a1b2c3#{i}");

    private static Task<IReadOnlyList<MemoryEntry>> RecallAsync(MemoryWorld world, MemoryQuery query) => world.Memory.RecallAsync(world.Project, query, Ct);

    private static Task<IReadOnlyList<MemoryEntry>> EverythingAsync(MemoryWorld world) => RecallAsync(world, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: false));

    private static async Task<string[]> TrustedAsync(MemoryWorld world) => [.. (await RecallAsync(world, new MemoryQuery(MaxEntries: int.MaxValue))).Select(e => e.Id)];

    private static async Task<MemoryEntry> RecordAsync(MemoryWorld world, MemoryDraft draft)
    {
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        return await world.Memory.RecordAsync(world.Project, draft, Ct);
    }

    /// <summary>A connection of another program: none of the pragmas of the app, so recursive triggers are off.</summary>
    private static SqliteConnection Foreign(MemoryWorld world)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = world.Instance.Databases.Get(world.Project.ProjectId).DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task A_review_changes_the_trust_the_snapshot_serves_without_looking_for_any_row()
    {
        using var world = await MemoryWorld.CreateAsync();
        var proposed = await RecordAsync(world, Draft(1, MemoryOrigin.Agent));
        Assert.Equal(MemoryTrust.Proposed, Assert.Single(await EverythingAsync(world)).Trust);
        Assert.Empty(await TrustedAsync(world));
        var finds = world.Spy.Finds.Count;

        await world.Memory.ReviewAsync(world.Project, proposed.Id, MemoryReview.Accepted, "manuel", null, Ct);
        Assert.Equal(MemoryTrust.Trusted, Assert.Single(await EverythingAsync(world)).Trust);
        Assert.Equal([proposed.Id], await TrustedAsync(world));

        await world.Memory.ReviewAsync(world.Project, proposed.Id, MemoryReview.Retracted, "manuel", "Ya no vale.", Ct);
        Assert.Equal(MemoryTrust.Retracted, Assert.Single(await EverythingAsync(world)).Trust);
        Assert.Empty(await TrustedAsync(world));

        Assert.Equal(finds, world.Spy.Finds.Count); // a review announces no entry: no row is looked for
    }

    [Fact]
    public async Task Trusting_the_journal_trusts_its_entries_in_the_snapshot_already_loaded()
    {
        using var world = await MemoryWorld.CreateAsync();
        var day = new DateOnly(2026, 11, 5);
        var journal = await RecordAsync(world, new MemoryDraft(MemoryKind.JournalEntry, "Bitácora del 5 de noviembre", "Se revisó el lector de pruebas.", MemoryOrigin.Agent, MemorySources.Journal(day, new string('a', 64))));
        Assert.Equal(MemoryTrust.Quarantined, Assert.Single(await EverythingAsync(world)).Trust);

        await world.Instance.Recorder.RecordAsync(
            world.Project.ProjectId,
            new Actor(ActorKind.User, "manuel"),
            DecisionKind.JournalTrustChanged,
            "Confiar en la bitácora de este proyecto",
            JournalTrustChangedPayload.Deciding(JournalTrustScope.Project, null, true, "manuel", null),
            cancellationToken: Ct);

        Assert.Equal(MemoryTrust.Trusted, Assert.Single(await EverythingAsync(world)).Trust);
        Assert.Equal([journal.Id], await TrustedAsync(world));
    }

    [Fact]
    public async Task Recalls_and_writes_at_once_over_a_loaded_snapshot_never_fail_never_go_back_and_end_with_every_entry()
    {
        using var world = await MemoryWorld.CreateAsync();
        await RecordAsync(world, Draft(0));
        Assert.Single(await EverythingAsync(world));

        var writes = Task.Run(
            async () =>
            {
                for (var i = 1; i <= 8; i++)
                {
                    await world.Memory.RecordAsync(world.Project, Draft(i), Ct);
                }
            },
            Ct);
        var recalls = Enumerable.Range(0, 4).Select(_ => Task.Run(
            async () =>
            {
                var counts = new List<int>();
                while (!writes.IsCompleted)
                {
                    counts.Add((await EverythingAsync(world)).Count);
                }

                return counts;
            },
            Ct)).ToList();
        await writes.WaitAsync(Guard, Ct);
        var seen = await Task.WhenAll(recalls).WaitAsync(Guard, Ct);

        Assert.All(seen, counts => Assert.Equal(counts.Order(), counts)); // what one recall sees after another is never older
        Assert.Equal(9, (await EverythingAsync(world)).Count);
        Assert.Empty(await world.AlertsAsync());
    }

    [Fact]
    public async Task A_query_by_category_or_topic_selects_on_the_entry_verified_never_on_the_row_of_the_store()
    {
        using var world = await MemoryWorld.CreateAsync();
        var decision = await RecordAsync(world, Draft(1));
        Assert.Single(await RecallAsync(world, new MemoryQuery(Kinds: [MemoryKind.Decision])));

        // The row now says it is another category and another topic, where the stamp of the store cannot see it.
        StampBlindTampering.Apply(world.Instance, StampBlindChange.SchemaVersionForged, "DROP TRIGGER memory_entries_no_update;", $"UPDATE memory_entries SET kind = 'KnownError', topic = 'Otro tema' WHERE id = '{decision.Id}';", RestoreNoUpdate);

        Assert.Empty(await RecallAsync(world, new MemoryQuery(Kinds: [MemoryKind.KnownError])));
        Assert.Empty(await RecallAsync(world, new MemoryQuery(Topic: "Otro tema")));
        Assert.Equal(decision.Id, Assert.Single(await RecallAsync(world, new MemoryQuery(Kinds: [MemoryKind.Decision]))).Id);
        Assert.Equal(decision.Id, Assert.Single(await RecallAsync(world, new MemoryQuery(Topic: "tema 1"))).Id); // as the store compares a topic
        Assert.Equal(decision.Id, Assert.Single(await RecallAsync(world, new MemoryQuery(Text: "blue green", Kinds: [MemoryKind.Decision]))).Id);
    }

    [Fact]
    public async Task An_entry_announced_without_its_row_is_remembered_as_missing_alerted_once_and_comes_when_the_row_is_stored()
    {
        using var world = await MemoryWorld.CreateAsync();
        var kept = await RecordAsync(world, Draft(1));

        // The app dies between the announcement and the row: it never came.
        world.Spy.FailNextAppend = true;
        await Assert.ThrowsAsync<IOException>(() => RecordAsync(world, Draft(2)));
        Assert.Equal([kept.Id], (await EverythingAsync(world)).Select(e => e.Id));
        var finds = world.Spy.Finds.Count;

        Assert.Equal([kept.Id], (await EverythingAsync(world)).Select(e => e.Id));
        Assert.Equal([kept.Id], (await EverythingAsync(world)).Select(e => e.Id));
        Assert.Equal(finds, world.Spy.Finds.Count); // remembered as missing: no recall looks for it again
        var alert = Assert.Single(await world.AlertsAsync());
        Assert.Equal(ProjectMemory.MissingRowAlert, alert.Code);

        // Deriving it again stores its row, and the next recall serves it.
        var again = await RecordAsync(world, Draft(2));
        Assert.Equal([again.Id, kept.Id], (await EverythingAsync(world)).Select(e => e.Id));
        Assert.Single(await world.AlertsAsync());
    }

    [Fact]
    public async Task A_project_forgotten_is_read_and_checked_again_at_the_next_recall()
    {
        using var world = await MemoryWorld.CreateAsync();
        var entry = await RecordAsync(world, Draft(1));
        Assert.Single(await EverythingAsync(world));
        Assert.Single(await EverythingAsync(world));
        var whole = world.Spy.WholeReads;

        world.Memory.Forget(world.Project.ProjectId);

        Assert.Equal(entry.Id, Assert.Single(await EverythingAsync(world)).Id);
        Assert.Equal(whole + 1, world.Spy.WholeReads);
    }

    [Fact]
    public async Task A_row_replaced_through_its_rowid_by_a_connection_without_recursive_triggers_is_never_served_and_is_found_at_the_next_whole_verification()
    {
        using var world = await MemoryWorld.CreateAsync();
        var entry = await RecordAsync(world, Draft(1));
        Assert.Single(await EverythingAsync(world));

        // REPLACE deletes the row it conflicts with by rowid without its DELETE trigger; the new id is no one's, so the
        // trigger of the insert lets it in. The schema does not change.
        using (var foreign = Foreign(world))
        {
            Exec(foreign, $"INSERT OR REPLACE INTO memory_entries(rowid, id, project_id, kind, topic, content, source, created_at, origin, content_sha256, supersedes_entry_id) SELECT rowid, 'mem-plantada', project_id, kind, topic, 'Se despliega a mano.', source, created_at, origin, content_sha256, supersedes_entry_id FROM memory_entries WHERE id = '{entry.Id}';");
        }

        var served = Assert.Single(await EverythingAsync(world));
        Assert.Equal(entry.Content, served.Content);
        Assert.True((await world.Instance.Recorder.LoadAsync(world.Project.ProjectId, LogLoad.Full, Ct)).Verification.IsValid);

        Assert.Empty(await EverythingAsync(world));
        Assert.Equal([ProjectMemory.MissingRowAlert, ProjectMemory.NotAnchoredAlert], (await world.AlertsAsync()).Select(a => a.Code).Order(StringComparer.Ordinal));
    }
}
