using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The decision log in SQLite against its head anchored outside the project (ADR-0010, addendum). Whoever controls the
/// database file can drop the triggers and cut the end of the log off, or rewrite it from some entry on with recomputed
/// hashes, or add entries at the end: the chain still verifies, but the anchor no longer matches.
/// </summary>
public sealed class DecisionLogAnchorTests : IDisposable
{
    private const string Run = "20261015-a1b2c3";
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ProjectInstance> OpenAsync() => ProjectInstance.OpenAsync(_folder.Root, anchorFolder: _anchors);

    private static TaskAssignedPayload Assigned(string task) => new(Tid(task), Aid("ejecutor")) { RunId = Run };

    /// <summary>Writes <paramref name="count"/> entries with the real recorder and closes the project, as the app does.</summary>
    private async Task<(string ProjectId, IReadOnlyList<DecisionEntry> Entries)> WriteAsync(int count)
    {
        using var instance = await OpenAsync();
        var entries = new List<DecisionEntry>();
        for (var i = 1; i <= count; i++)
        {
            instance.Clock.Advance(TimeSpan.FromSeconds(1));
            entries.Add(await instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, $"Entrada {i}", Assigned("T" + i), cancellationToken: Ct));
        }

        return (instance.ProjectId, entries);
    }

    /// <summary>What someone editing the database (of the project, or of the folder at <paramref name="root"/>) does after dropping the triggers.</summary>
    private async Task TamperAsync(Action<SqliteConnection> change, string? root = null)
    {
        using var instance = await ProjectInstance.OpenAsync(root ?? _folder.Root);
        using var connection = instance.Connect();
        change(connection);
    }

    /// <summary>A copy of a project folder made with the app closed, as a file explorer makes it.</summary>
    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyFolder(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void Insert(SqliteConnection connection, string projectId, DecisionEntry entry)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, corrects_sequence, previous_hash, hash)
            VALUES ($project, $sequence, $at, $actorKind, $actorId, $kind, $summary, $payload, NULL, $previous, $hash);
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$sequence", entry.Sequence);
        command.Parameters.AddWithValue("$at", entry.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actorKind", entry.Actor.Kind.ToString());
        command.Parameters.AddWithValue("$actorId", entry.Actor.Id);
        command.Parameters.AddWithValue("$kind", entry.Kind.ToString());
        command.Parameters.AddWithValue("$summary", entry.Summary);
        command.Parameters.AddWithValue("$payload", entry.PayloadJson);
        command.Parameters.AddWithValue("$previous", entry.PreviousHash);
        command.Parameters.AddWithValue("$hash", entry.Hash);
        command.ExecuteNonQuery();
    }

    /// <summary>Entries appended to the kept ones with consistent hashes: the chain alone cannot tell them from real ones.</summary>
    private static IReadOnlyList<DecisionEntry> Forged(IReadOnlyList<DecisionEntry> kept, params DecisionPayload[] payloads)
    {
        var log = DecisionLog.Load(kept);
        foreach (var payload in payloads)
        {
            log.Append(kept[^1].RecordedAt.AddMinutes(1), System, DecisionKind.EvidenceRegistered, "Evidencia registrada", DecisionPayloadSerializer.Serialize(payload));
        }

        return log.Entries.Skip(kept.Count).ToList();
    }

    private async Task<DecisionLogState> ReopenAndLoadAsync()
    {
        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() =>
            instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "no", Assigned("T9"), cancellationToken: Ct));
        return state;
    }

    [Fact]
    public async Task A_log_nobody_touched_verifies_against_its_anchor_after_reopening_and_goes_on()
    {
        var (_, written) = await WriteAsync(4);

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        var next = await instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "Tras reabrir", Assigned("T5"), cancellationToken: Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(written[^1].Hash, state.HeadHash);
        Assert.Equal(5, next.Sequence);
        Assert.Single(Directory.GetFiles(_anchors));
        Assert.False(File.Exists(Path.Combine(_anchors, instance.ProjectId + ".json"))); // named by the location, not by the id
    }

    [Fact]
    public async Task Cutting_the_end_of_the_log_off_in_the_database_is_detected()
    {
        await WriteAsync(5);
        await TamperAsync(connection =>
        {
            Exec(connection, "DROP TRIGGER decision_log_no_delete");
            Exec(connection, "DELETE FROM decision_log WHERE sequence > 3");
        });

        var state = await ReopenAndLoadAsync();

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid); // the chain alone cannot tell
        Assert.False(state.Verification.IsValid);
        Assert.Equal(4, state.Verification.FirstInvalidSequence);
        Assert.Contains("removed", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rewriting_the_end_of_the_log_with_recomputed_hashes_is_detected()
    {
        var (project, written) = await WriteAsync(5);
        var forged = Forged([.. written.Take(3)],
            new EvidenceRegisteredPayload(Eid("EV-falsa-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, HashA, Tid("T1"), 1) { RunId = Run },
            new EvidenceRegisteredPayload(Eid("EV-falsa-2"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, HashB, Tid("T1"), 1) { RunId = Run });
        await TamperAsync(connection =>
        {
            Exec(connection, "DROP TRIGGER decision_log_no_delete");
            Exec(connection, "DELETE FROM decision_log WHERE sequence > 3");
            foreach (var entry in forged)
            {
                Insert(connection, project, entry);
            }
        });

        var state = await ReopenAndLoadAsync();

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid);
        Assert.Equal(5, state.Entries.Count);
        Assert.False(state.Verification.IsValid);
        Assert.Equal(5, state.Verification.FirstInvalidSequence);
        Assert.Contains("rewritten", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_entries_at_the_end_of_the_log_in_the_database_is_detected()
    {
        // The way around the dossier's check of EvidenceRegistered: insert the evidence row and announce it in the log.
        var (project, written) = await WriteAsync(3);
        var forged = Forged(written,
            new EvidenceRegisteredPayload(Eid("EV-falsa"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, HashA, Tid("T1"), 1) { RunId = Run });
        await TamperAsync(connection => Insert(connection, project, forged[0]));

        var state = await ReopenAndLoadAsync();

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid);
        Assert.False(state.Verification.IsValid);
        Assert.Equal(4, state.Verification.FirstInvalidSequence);
        Assert.Contains("added", state.Verification.Problem, StringComparison.Ordinal);
    }

    // ---- the anchor belongs to the project's location, not to the id its database gives (A-01) ----

    /// <summary>«Aceptar este historial como nuevo origen» as the orchestrator screen asks it, and the person's yes.</summary>
    private static Task<DecisionLogState> AcceptAsync(ProjectInstance instance, UnanchoredHistory history)
    {
        var binding = history.Binding(instance.ProjectId);
        var request = new ApprovalRequest(UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical, null, [instance.Root], instance.ProjectId, Binding: binding);
        return instance.Recorder.AcceptAsNewOriginAsync(instance.ProjectId, request, new ApprovalDecision(true, "user", T0, null, binding, ApprovalVerification.WindowsHello), Ct);
    }

    private static async Task AssertRecordingIsRefusedAsync(ProjectInstance instance)
    {
        var before = (await instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct)).Count;
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() =>
            instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "no", Assigned("T9"), cancellationToken: Ct));
        Assert.Equal(before, (await instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct)).Count);
    }

    private string LegacyFile(string projectId) => Path.Combine(_anchors, projectId + ".json");

    private static string LegacyJson(string projectId, long entryCount, string head) =>
        $$"""{"format":"agent-studio/decision-log-anchor/1","projectId":"{{projectId}}","entryCount":{{entryCount}},"headHash":"{{head}}"}""";

    /// <summary>The anchor file of an Agent Studio from before 2026-10-18, alone: named by the project id, format 1.</summary>
    private async Task WriteLegacyAnchorAsync(string projectId, long entryCount, string head)
    {
        foreach (var file in Directory.GetFiles(_anchors))
        {
            File.Delete(file);
        }

        await File.WriteAllTextAsync(LegacyFile(projectId), LegacyJson(projectId, entryCount, head), Ct);
    }

    [Fact]
    public async Task Rewriting_the_project_id_offline_and_the_end_of_the_log_is_detected_and_blocks_recording()
    {
        // With the app closed: a new id in projects and in every project_id, and a log cut short and extended with
        // consistent hashes, so that the chain alone cannot tell and no anchor is named by the new id.
        var (project, written) = await WriteAsync(5);
        var forgedId = Guid.NewGuid().ToString("D");
        var forged = Forged([.. written.Take(2)],
            new EvidenceRegisteredPayload(Eid("EV-falsa"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, HashA, Tid("T1"), 1) { RunId = Run });
        await TamperAsync(connection =>
        {
            Exec(connection, "PRAGMA foreign_keys = OFF");
            Exec(connection, "DROP TRIGGER decision_log_no_update");
            Exec(connection, "DROP TRIGGER decision_log_no_delete");
            Exec(connection, "DELETE FROM decision_log WHERE sequence > 2");
            Exec(connection, $"UPDATE decision_log SET project_id = '{forgedId}'");
            Exec(connection, $"UPDATE projects SET id = '{forgedId}'");
            Insert(connection, forgedId, forged[0]);
        });

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);

        Assert.Equal(forgedId, instance.ProjectId);
        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid);
        Assert.False(state.Verification.IsValid);
        Assert.Contains(project, state.Verification.Problem, StringComparison.Ordinal);
        Assert.Null(state.Unanchored); // the detected attack: no acceptance is offered for it
        await AssertRecordingIsRefusedAsync(instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(instance, new UnanchoredHistory(3, state.HeadHash)));
    }

    [Fact]
    public async Task A_project_moved_to_another_folder_is_blocked_until_a_person_accepts_its_history_and_works_after()
    {
        var (project, written) = await WriteAsync(3);
        var moved = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "movido");
        Directory.Move(_folder.Root, moved);

        using (var there = await ProjectInstance.OpenAsync(moved, anchorFolder: _anchors))
        {
            var state = await there.Recorder.LoadAsync(there.ProjectId, Ct);

            Assert.Equal(project, there.ProjectId);
            Assert.False(state.Verification.IsValid);
            Assert.Equal(new UnanchoredHistory(3, written[^1].Hash), state.Unanchored);
            await AssertRecordingIsRefusedAsync(there);

            var accepted = await AcceptAsync(there, state.Unanchored!);
            var next = await there.Recorder.RecordAsync(there.ProjectId, System, DecisionKind.TaskAssigned, "Tras aceptar", Assigned("T4"), cancellationToken: Ct);

            Assert.True(accepted.Verification.IsValid);
            Assert.Equal(7, next.Sequence); // 3 + alert, request and approval
        }

        using var reopened = await ProjectInstance.OpenAsync(moved, anchorFolder: _anchors);
        var after = await reopened.Recorder.LoadAsync(reopened.ProjectId, Ct);
        Assert.True(after.Verification.IsValid, after.Verification.Problem);
        Assert.Equal([DecisionKind.AuditAlertRaised, DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], after.Entries.Skip(3).Take(3).Select(e => e.Kind));
    }

    [Fact]
    public async Task A_history_written_on_another_machine_is_blocked_until_a_person_accepts_it()
    {
        // Written with no anchor folder at all: on this machine there is no anchor for it anywhere.
        using (var elsewhere = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await elsewhere.Recorder.RecordAsync(elsewhere.ProjectId, System, DecisionKind.TaskAssigned, "Otra máquina", Assigned("T1"), cancellationToken: Ct);
        }

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(1, state.Unanchored?.EntryCount);
        await AssertRecordingIsRefusedAsync(instance);
        Assert.True((await AcceptAsync(instance, state.Unanchored!)).Verification.IsValid);
    }

    [Fact]
    public async Task A_legacy_anchor_named_by_the_project_id_is_migrated_on_first_open_and_the_log_goes_on()
    {
        var (project, written) = await WriteAsync(3);
        await WriteLegacyAnchorAsync(project, 3, written[^1].Hash);

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        var next = await instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "Tras migrar", Assigned("T4"), cancellationToken: Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(4, next.Sequence);
        Assert.False(File.Exists(Path.Combine(_anchors, project + ".json")));
        Assert.Single(Directory.GetFiles(_anchors));
    }

    [Fact]
    public async Task A_log_cut_short_before_its_legacy_anchor_was_migrated_is_still_detected()
    {
        var (project, written) = await WriteAsync(4);
        await WriteLegacyAnchorAsync(project, 4, written[^1].Hash);
        await TamperAsync(connection =>
        {
            Exec(connection, "DROP TRIGGER decision_log_no_delete");
            Exec(connection, "DELETE FROM decision_log WHERE sequence > 2");
        });

        var state = await ReopenAndLoadAsync();

        Assert.False(state.Verification.IsValid);
        Assert.Contains("removed", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Null(state.Unanchored);
    }

    // ---- a legacy anchor moves to the location only once the log verified against it (P-04) ----

    [Fact]
    public async Task Opening_a_history_that_verifies_against_its_legacy_anchor_moves_the_anchor_to_the_location()
    {
        var (project, written) = await WriteAsync(3);
        await WriteLegacyAnchorAsync(project, 3, written[^1].Hash);

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.False(File.Exists(LegacyFile(project)));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_anchors)), Ct));
        Assert.Equal("agent-studio/decision-log-anchor/2", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(project, json.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("entryCount").GetInt64());
        Assert.Equal(written[^1].Hash, json.RootElement.GetProperty("headHash").GetString());
    }

    [Fact]
    public async Task An_altered_copy_opened_first_does_not_take_the_legacy_anchor_and_the_original_still_migrates()
    {
        var (project, written) = await WriteAsync(4);
        await WriteLegacyAnchorAsync(project, 4, written[^1].Hash);
        var copy = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "copia");
        CopyFolder(_folder.Root, copy);
        await TamperAsync(
            connection =>
            {
                Exec(connection, "DROP TRIGGER decision_log_no_delete");
                Exec(connection, "DELETE FROM decision_log WHERE sequence > 2");
            },
            copy);

        using (var altered = await ProjectInstance.OpenAsync(copy, anchorFolder: _anchors))
        {
            var refused = await altered.Recorder.LoadAsync(altered.ProjectId, Ct);

            Assert.Equal(project, altered.ProjectId);
            Assert.False(refused.Verification.IsValid);
            Assert.Contains("removed", refused.Verification.Problem, StringComparison.Ordinal);
            Assert.Null(refused.Unanchored);
            await AssertRecordingIsRefusedAsync(altered);
        }

        Assert.Equal([LegacyFile(project)], Directory.GetFiles(_anchors)); // the copy took nothing

        using var original = await OpenAsync();
        var state = await original.Recorder.LoadAsync(original.ProjectId, Ct);
        var next = await original.Recorder.RecordAsync(original.ProjectId, System, DecisionKind.TaskAssigned, "Tras migrar", Assigned("T5"), cancellationToken: Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(5, next.Sequence);
        Assert.False(File.Exists(LegacyFile(project)));
        Assert.Single(Directory.GetFiles(_anchors));
    }

    [Fact]
    public async Task A_move_of_the_legacy_anchor_cut_short_after_its_write_is_finished_on_the_next_open()
    {
        // The process died between the two steps: the location's anchor is written and the file named by the id is still there.
        var (project, written) = await WriteAsync(3);
        await File.WriteAllTextAsync(LegacyFile(project), LegacyJson(project, 3, written[^1].Hash), Ct);
        Assert.Equal(2, Directory.GetFiles(_anchors).Length);

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        var next = await instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "Tras recuperar", Assigned("T4"), cancellationToken: Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(4, next.Sequence);
        Assert.False(File.Exists(LegacyFile(project)));
        Assert.Single(Directory.GetFiles(_anchors));
    }

    [Fact]
    public async Task A_new_project_is_anchored_at_its_location_by_its_first_entry()
    {
        using var instance = await OpenAsync();

        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        await instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.TaskAssigned, "Primera", Assigned("T1"), cancellationToken: Ct);

        Assert.True(state.Verification.IsValid);
        Assert.Null(state.Unanchored);
        Assert.Single(Directory.GetFiles(_anchors));
        Assert.True((await instance.Recorder.LoadAsync(instance.ProjectId, Ct)).Verification.IsValid);
    }

    [Fact]
    public async Task A_new_empty_history_where_another_project_lived_needs_a_person_and_the_alert_names_that_project()
    {
        // .agentstudio deleted, or the repository cloned again into the same folder.
        var (before, _) = await WriteAsync(2);
        Directory.Delete(_folder.DataDirectory, recursive: true);

        using var instance = await OpenAsync();
        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(new UnanchoredHistory(0, DecisionLog.GenesisHash, before, 2), state.Unanchored);
        await AssertRecordingIsRefusedAsync(instance);

        var accepted = await AcceptAsync(instance, state.Unanchored!);

        Assert.True(accepted.Verification.IsValid);
        var alert = DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(accepted.Entries[0].PayloadJson);
        Assert.Contains(before, alert.Message, StringComparison.Ordinal);
    }
}
