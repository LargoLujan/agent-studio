using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The recorder verifies the log incrementally (<see cref="IDecisionLogTailReader"/>): after the first load it reads and
/// verifies only what was appended, so loading a log of thousands of entries costs what it grew. A log changed behind its
/// back must still be detected: an entry changed before the last verified point (the store's stamp changes when a trigger
/// is dropped), entries added after it by someone else (the anchor), entries cut off, a chain rewritten and its triggers
/// restored, a project opened again, and a writer that is another process. A change that leaves the stamp as it was (the
/// schema version forged back, the file replaced by a copy, bytes written in place) is found by the next whole
/// verification: at a critical moment, five minutes after the last one, after the bound of loads, or on opening again.
/// </summary>
public sealed class IncrementalLogVerificationTests : IDisposable
{
    private const string Run = "20261104-a1b2c3";

    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real store, counting how the recorder reads it: the whole log, or only what follows what it verified.</summary>
    private sealed class CountingStore(SqliteDecisionLogStore inner) : IDecisionLogStore, IDecisionLogTailReader
    {
        private int _whole;
        private int _tails;

        public int Whole => Volatile.Read(ref _whole);

        public int Tails => Volatile.Read(ref _tails);

        public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken) => inner.AppendAsync(projectId, entry, cancellationToken);

        public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _whole);
            return inner.ReadAllAsync(projectId, cancellationToken);
        }

        public Task<DecisionLogTail> ReadTailAsync(string projectId, DecisionLogMark? after, CancellationToken cancellationToken)
        {
            if (after is null)
            {
                Interlocked.Increment(ref _whole);
            }
            else
            {
                Interlocked.Increment(ref _tails);
            }

            return inner.ReadTailAsync(projectId, after, cancellationToken);
        }
    }

    /// <summary>An open project with a recorder of its own over the counting store and the real anchor outside the project.</summary>
    private sealed class World : IDisposable
    {
        private World(ProjectInstance instance, string anchors)
        {
            Instance = instance;
            Store = new CountingStore(instance.DecisionLog);
            Recorder = new DecisionRecorder(Store, instance.Clock, new FileDecisionLogAnchorStore(new DecisionLogAnchorOptions(anchors), instance.Databases));
        }

        public ProjectInstance Instance { get; }

        public CountingStore Store { get; }

        public DecisionRecorder Recorder { get; }

        public string ProjectId => Instance.ProjectId;

        public static async Task<World> OpenAsync(string root, string anchors) => new(await ProjectInstance.OpenAsync(root, anchorFolder: anchors), anchors);

        public async Task<IReadOnlyList<DecisionEntry>> WriteAsync(int count)
        {
            var entries = new List<DecisionEntry>();
            for (var i = 1; i <= count; i++)
            {
                Instance.Clock.Advance(TimeSpan.FromSeconds(1));
                entries.Add(await Recorder.RecordAsync(ProjectId, System, DecisionKind.AuditAlertRaised, $"Entrada {i}", new AuditAlertPayload("prueba", [], $"Entrada {i}") { RunId = Run }, cancellationToken: Ct));
            }

            return entries;
        }

        public Task<DecisionLogState> LoadAsync() => Recorder.LoadAsync(ProjectId, Ct);

        /// <summary>What someone with the database open does behind the stores' back: another connection to it.</summary>
        public void Tamper(params string[] statements)
        {
            using var connection = Instance.Connect();
            foreach (var statement in statements)
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }

        public void Dispose() => Instance.Dispose();
    }

    private Task<World> OpenAsync() => World.OpenAsync(_folder.Root, _anchors);

    private const string RestoreNoUpdate = "CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END;";

    private const string RestoreNoDelete = "CREATE TRIGGER decision_log_no_delete BEFORE DELETE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END;";

    /// <summary>An entry chained to the last of <paramref name="kept"/> with a hash recomputed for it: the chain alone cannot tell it from a real one.</summary>
    private static DecisionEntry Forged(IReadOnlyList<DecisionEntry> kept, string summary)
    {
        var log = DecisionLog.Load(kept);
        return log.Append(kept[^1].RecordedAt.AddMinutes(1), System, DecisionKind.AuditAlertRaised, summary, DecisionPayloadSerializer.Serialize(new AuditAlertPayload("prueba", [], summary) { RunId = Run }));
    }

    private static string InsertOf(string projectId, DecisionEntry entry) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        INSERT INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, corrects_sequence, previous_hash, hash)
        VALUES ('{projectId}', {entry.Sequence}, '{entry.RecordedAt.ToUniversalTime():O}', '{entry.Actor.Kind}', '{entry.Actor.Id}', '{entry.Kind}', '{entry.Summary}', '{entry.PayloadJson}', NULL, '{entry.PreviousHash}', '{entry.Hash}');
        """);

    // ---- what it costs ----

    [Fact]
    public async Task After_the_first_load_only_what_was_appended_is_read_and_verified()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(60);
        Assert.Equal(1, world.Store.Whole); // the first write read the (empty) log whole; the rest appended to it

        for (var i = 0; i < 25; i++)
        {
            var state = await world.LoadAsync();
            Assert.True(state.Verification.IsValid, state.Verification.Problem);
            Assert.Equal(60, state.Entries.Count);
        }

        await world.WriteAsync(5);
        var grown = await world.LoadAsync();

        Assert.True(grown.Verification.IsValid, grown.Verification.Problem);
        Assert.Equal(65, grown.Entries.Count);
        Assert.Equal(1, world.Store.Whole);
        Assert.Equal(26, world.Store.Tails);
        Assert.Equal(DecisionLog.Load(await world.Instance.DecisionLog.ReadAllAsync(world.ProjectId, Ct)).HeadHash, grown.Entries[^1].Hash);
    }

    [Fact]
    public async Task What_another_recorder_appended_is_verified_and_taken_from_the_tail()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(10);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        // The other writer of the same process (the instance's own recorder) appends three entries, announced in the anchor.
        for (var i = 0; i < 3; i++)
        {
            await world.Instance.Recorder.RecordAsync(world.ProjectId, System, DecisionKind.AuditAlertRaised, "Otro escritor", new AuditAlertPayload("prueba", [], "otro") { RunId = Run }, cancellationToken: Ct);
        }

        var state = await world.LoadAsync();

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(13, state.Entries.Count);
        Assert.Equal(1, world.Store.Whole);
    }

    // ---- what it must still detect ----

    [Fact]
    public async Task An_entry_changed_before_the_last_verified_one_is_detected_without_reopening_even_with_its_trigger_restored()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(8);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        world.Tamper(
            "DROP TRIGGER decision_log_no_update;",
            "UPDATE decision_log SET summary = 'Entrada cambiada' WHERE sequence = 2;",
            RestoreNoUpdate);
        var state = await world.LoadAsync();

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);
        Assert.Contains("modified", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Equal(2, world.Store.Whole); // the stamp changed with the schema: the whole log was read and verified again
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => world.Recorder.RecordAsync(world.ProjectId, System, DecisionKind.AuditAlertRaised, "no", new AuditAlertPayload("prueba", [], "no") { RunId = Run }, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_entry_added_after_the_last_verified_one_by_someone_else_is_detected()
    {
        using var world = await OpenAsync();
        var written = await world.WriteAsync(5);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        world.Tamper(InsertOf(world.ProjectId, Forged(written, "Entrada-añadida")));
        var state = await world.LoadAsync();

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid); // its chain holds: only the anchor can tell
        Assert.False(state.Verification.IsValid);
        Assert.Equal(6, state.Verification.FirstInvalidSequence);
        Assert.Contains("added", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Equal(1, world.Store.Whole); // read from the tail, and still caught
    }

    [Fact]
    public async Task An_entry_added_after_the_last_verified_one_with_a_wrong_hash_breaks_the_chain()
    {
        using var world = await OpenAsync();
        var written = await world.WriteAsync(5);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        var forged = Forged(written, "Entrada-añadida");
        world.Tamper(InsertOf(world.ProjectId, forged with { Summary = "Otra cosa" }));
        var state = await world.LoadAsync();

        Assert.False(state.Verification.IsValid);
        Assert.Equal(6, state.Verification.FirstInvalidSequence);
        Assert.Contains("modified", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_cut_off_the_end_are_detected_without_reopening()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(6);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        world.Tamper("DROP TRIGGER decision_log_no_delete;", "DELETE FROM decision_log WHERE sequence > 4;", RestoreNoDelete);
        var state = await world.LoadAsync();

        Assert.Equal(4, state.Entries.Count);
        Assert.False(state.Verification.IsValid);
        Assert.Contains("removed", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_rewritten_from_an_entry_on_with_its_triggers_restored_is_detected()
    {
        using var world = await OpenAsync();
        var written = await world.WriteAsync(6);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        var kept = written.Take(3).ToList();
        var first = Forged(kept, "Reescrita 4");
        var second = Forged([.. kept, first], "Reescrita 5");
        var third = Forged([.. kept, first, second], "Reescrita 6");
        world.Tamper(
            "DROP TRIGGER decision_log_no_delete;",
            "DELETE FROM decision_log WHERE sequence > 3;",
            RestoreNoDelete,
            InsertOf(world.ProjectId, first),
            InsertOf(world.ProjectId, second),
            InsertOf(world.ProjectId, third));
        var state = await world.LoadAsync();

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid);
        Assert.Equal(6, state.Entries.Count);
        Assert.False(state.Verification.IsValid);
        Assert.Contains("rewritten", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_trigger_dropped_every_load_verifies_the_whole_log_so_a_later_change_is_detected()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(6);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        // The trigger goes first, alone: the log still verifies, and nothing in the schema will change for what comes next.
        world.Tamper("DROP TRIGGER decision_log_no_update;");
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        world.Tamper("UPDATE decision_log SET summary = 'Cambiada sin tocar el esquema' WHERE sequence = 3;");
        var state = await world.LoadAsync();

        Assert.False(state.Verification.IsValid);
        Assert.Equal(3, state.Verification.FirstInvalidSequence);
        Assert.Equal(4, world.Store.Whole); // with the trigger missing, no load trusts the tail
    }

    [Fact]
    public async Task A_trigger_rewritten_with_the_same_name_but_another_body_is_not_trusted_to_keep_the_log()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(6);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        world.Tamper("DROP TRIGGER decision_log_no_update;", "CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log WHEN 0 BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END;");
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        world.Tamper("UPDATE decision_log SET summary = 'Cambiada con el trigger cambiado' WHERE sequence = 2;");
        var state = await world.LoadAsync();

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);

        // The genuine trigger back and the entry as it was: the log verifies again, and only its tail is read once more.
        world.Tamper("DROP TRIGGER decision_log_no_update;", "UPDATE decision_log SET summary = 'Entrada 2' WHERE sequence = 2;", RestoreNoUpdate);
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        var whole = world.Store.Whole;
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        Assert.Equal(whole, world.Store.Whole);
    }

    [Fact]
    public async Task Bytes_written_in_place_where_no_stamp_sees_them_are_found_within_the_bound_of_loads()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(4);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        // SQLite's blob interface writes into a value in place: no trigger runs and the schema does not change.
        using (var connection = world.Instance.Connect())
        {
            using var rowid = connection.CreateCommand();
            rowid.CommandText = "SELECT rowid FROM decision_log WHERE sequence = 2;";
            using var blob = new SqliteBlob(connection, "decision_log", "summary", (long)rowid.ExecuteScalar()!, readOnly: false);
            blob.Write("Entrada X"u8);
        }

        var loads = 0;
        DecisionLogState state;
        do
        {
            state = await world.LoadAsync();
            loads++;
        }
        while (state.Verification.IsValid && loads <= DecisionRecorder.IncrementalLoadsBeforeFullVerification + 1);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);
        Assert.InRange(loads, 1, DecisionRecorder.IncrementalLoadsBeforeFullVerification + 1);
    }

    /// <summary>What makes a load verify the whole log after a change the stamp cannot see.</summary>
    public enum WholeVerification
    {
        /// <summary>A load at a critical moment (<see cref="LogLoad.Full"/>).</summary>
        CriticalMoment,

        /// <summary>An ordinary load, once the last whole verification is five minutes old (<see cref="DecisionRecorder.IncrementalAgeBeforeFullVerification"/>).</summary>
        FiveMinutesLater,
    }

    [Theory]
    [InlineData(StampBlindChange.SchemaVersionForged, WholeVerification.CriticalMoment)]
    [InlineData(StampBlindChange.SchemaVersionForged, WholeVerification.FiveMinutesLater)]
    [InlineData(StampBlindChange.FileReplaced, WholeVerification.CriticalMoment)]
    [InlineData(StampBlindChange.FileReplaced, WholeVerification.FiveMinutesLater)]
    public async Task A_change_that_leaves_the_stamp_as_it_was_is_found_at_the_next_critical_moment_or_five_minutes_after_the_last_whole_verification(
        StampBlindChange change,
        WholeVerification whole)
    {
        using var world = await OpenAsync();
        await world.WriteAsync(8);
        Assert.True((await world.Recorder.LoadAsync(world.ProjectId, LogLoad.Full, Ct)).Verification.IsValid); // the five minutes start here
        var reads = world.Store.Whole;

        StampBlindTampering.Apply(world.Instance, change, "DROP TRIGGER decision_log_no_update;", "UPDATE decision_log SET summary = 'Entrada cambiada' WHERE sequence = 2;", RestoreNoUpdate);

        // The tail cannot tell, and nobody in this process is given the changed entry: the log verified is served from memory.
        var unseen = await world.LoadAsync();
        Assert.True(unseen.Verification.IsValid);
        Assert.Equal("Entrada 2", unseen.Entries[1].Summary);
        Assert.Equal(reads, world.Store.Whole);

        DecisionLogState state;
        if (whole == WholeVerification.CriticalMoment)
        {
            state = await world.Recorder.LoadAsync(world.ProjectId, LogLoad.Full, Ct);
        }
        else
        {
            world.Instance.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.True((await world.LoadAsync()).Verification.IsValid);
            world.Instance.Clock.Advance(TimeSpan.FromSeconds(1));
            state = await world.LoadAsync();
        }

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);
        Assert.Contains("modified", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Equal(reads + 1, world.Store.Whole);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => world.Recorder.RecordAsync(world.ProjectId, System, DecisionKind.AuditAlertRaised, "no", new AuditAlertPayload("prueba", [], "no") { RunId = Run }, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_whole_verification_starts_the_five_minutes_again_and_a_load_from_the_tail_does_not()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(3);
        Assert.True((await world.Recorder.LoadAsync(world.ProjectId, LogLoad.Full, Ct)).Verification.IsValid);
        var reads = world.Store.Whole;

        // Loads from the tail for four minutes do not move the start of the five minutes...
        for (var minute = 1; minute <= 4; minute++)
        {
            world.Instance.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await world.LoadAsync()).Verification.IsValid);
        }

        Assert.Equal(reads, world.Store.Whole);
        world.Instance.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        Assert.Equal(reads + 1, world.Store.Whole);

        // ...and the whole verification of the fifth does: the next loads read the tail again.
        world.Instance.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        Assert.Equal(reads + 1, world.Store.Whole);
    }

    [Fact]
    public async Task Opening_the_project_again_verifies_the_whole_log_again()
    {
        using var world = await OpenAsync();
        await world.WriteAsync(4);
        Assert.True((await world.LoadAsync()).Verification.IsValid);

        world.Instance.Databases.Close(world.ProjectId);
        await world.Instance.Workspace.OpenAsync(world.Instance.Root, Ct);
        var state = await world.LoadAsync();

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(2, world.Store.Whole);
    }

    // ---- a writer that is another process ----

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task Another_process_that_changes_an_entry_and_restores_its_trigger_or_appends_one_is_detected()
    {
        var python = Python();
        Assert.SkipWhen(python is null, "There is no python on the PATH to write to the database from another process.");
        using var world = await OpenAsync();
        var written = await world.WriteAsync(5);
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        var database = world.Instance.Databases.Get(world.ProjectId).DatabasePath;

        await RunPythonAsync(python!, database, "DROP TRIGGER decision_log_no_update;", "UPDATE decision_log SET summary = 'Cambiada fuera' WHERE sequence = 3;", RestoreNoUpdate);
        var changed = await world.LoadAsync();

        Assert.False(changed.Verification.IsValid);
        Assert.Equal(3, changed.Verification.FirstInvalidSequence);

        // Restored as it was (another process again), the log verifies; then that process appends an entry of its own.
        await RunPythonAsync(python!, database, "DROP TRIGGER decision_log_no_update;", "UPDATE decision_log SET summary = 'Entrada 3' WHERE sequence = 3;", RestoreNoUpdate);
        Assert.True((await world.LoadAsync()).Verification.IsValid);
        await RunPythonAsync(python!, database, InsertOf(world.ProjectId, Forged(written, "Añadida fuera")));
        var added = await world.LoadAsync();

        Assert.False(added.Verification.IsValid);
        Assert.Equal(6, added.Verification.FirstInvalidSequence);
        Assert.Contains("added", added.Verification.Problem, StringComparison.Ordinal);
    }

    private static string? Python()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, "python.exe");
            if (File.Exists(candidate) && !folder.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Another process (python and its own SQLite) runs <paramref name="statements"/> on the database, given on stdin.</summary>
    private static async Task RunPythonAsync(string python, string database, params string[] statements)
    {
        const string Script = "import json, sqlite3, sys\njob = json.load(sys.stdin)\ndb = sqlite3.connect(job['database'], isolation_level=None)\nfor s in job['statements']:\n    db.execute(s)\ndb.close()\n";
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Script);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { database, statements }));
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, error);
    }
}
