using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>F3-4: the decision log in SQLite, written by the real <see cref="DecisionRecorder"/>, verifies after a restart.</summary>
public sealed class SqliteDecisionLogStoreTests : IDisposable
{
    private static readonly Actor System = new(ActorKind.System, "agent-studio");
    private static readonly Actor Orchestrator = new(ActorKind.Orchestrator, "orquestador");

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskStatusChangedPayload Moved(string task, WorkTaskStatus from, WorkTaskStatus to) =>
        new(Tid(task), from, to, "motivo con acentos: ñ", null, [Eid("ev-" + task)], 1) { RunId = "20261015-a1b2c3" };

    /// <summary>Writes a small but varied log: several kinds and actors, sub-millisecond times and one correction.</summary>
    private static async Task<IReadOnlyList<DecisionEntry>> WriteLogAsync(ProjectInstance instance, int count = 6)
    {
        var entries = new List<DecisionEntry>();
        for (var i = 1; i <= count; i++)
        {
            instance.Clock.Advance(TimeSpan.FromTicks(1_234_567));
            var actor = i % 2 == 0 ? Orchestrator : System;
            entries.Add(await instance.Recorder.RecordAsync(
                instance.ProjectId, actor, DecisionKind.TaskStatusChanged, $"Entrada {i}", Moved("T" + i, WorkTaskStatus.Pending, WorkTaskStatus.Running), cancellationToken: Ct));
        }

        entries.Add(await instance.Recorder.RecordAsync(
            instance.ProjectId, System, DecisionKind.Correction, "Corrige la entrada 2", Moved("T2", WorkTaskStatus.Running, WorkTaskStatus.Pending), corrects: 2, cancellationToken: Ct));
        return entries;
    }

    [Fact]
    public async Task A_log_written_with_the_recorder_verifies_after_reopening_with_the_same_head()
    {
        IReadOnlyList<DecisionEntry> written;
        string head;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            written = await WriteLogAsync(first);
            head = (await first.Recorder.LoadAsync(first.ProjectId, Ct)).HeadHash;
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var state = await second.Recorder.LoadAsync(second.ProjectId, Ct);

        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(head, state.HeadHash);
        Assert.Equal(written[^1].Hash, state.HeadHash);
        Assert.Equal(written, state.Entries);
    }

    [Fact]
    public async Task After_reopening_the_recorder_continues_the_chain()
    {
        IReadOnlyList<DecisionEntry> written;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            written = await WriteLogAsync(first, count: 3);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        second.Clock.Advance(TimeSpan.FromHours(1));
        var next = await second.Recorder.RecordAsync(second.ProjectId, System, DecisionKind.TaskStatusChanged, "Tras reabrir", Moved("T9", WorkTaskStatus.Pending, WorkTaskStatus.Running), cancellationToken: Ct);

        Assert.Equal(written.Count + 1, next.Sequence);
        Assert.Equal(written[^1].Hash, next.PreviousHash);
        Assert.True((await second.Recorder.LoadAsync(second.ProjectId, Ct)).Verification.IsValid);
    }

    [Fact]
    public async Task The_table_refuses_updates_while_its_triggers_are_in_place()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await WriteLogAsync(instance, count: 2);
        using var connection = instance.Connect();

        var ex = Assert.Throws<SqliteException>(() => Exec(connection, "UPDATE decision_log SET summary = 'falsificada' WHERE sequence = 1"));

        Assert.Contains("decision_log is append-only", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "summary = 'falsificada'")]
    [InlineData(3, "payload_json = '{\"v\":1,\"runId\":\"x\"}'")]
    [InlineData(5, "recorded_at = '2026-01-01T10:00:00.0000000+00:00'")]
    [InlineData(7, "actor_id = 'otro'")]
    public async Task Tampering_after_dropping_the_trigger_makes_verify_fail_at_that_sequence(long sequence, string change)
    {
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await WriteLogAsync(first);
            using var connection = first.Connect();
            Exec(connection, "DROP TRIGGER decision_log_no_update");
            Exec(connection, $"UPDATE decision_log SET {change} WHERE sequence = {sequence}");
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var state = await second.Recorder.LoadAsync(second.ProjectId, Ct);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(sequence, state.Verification.FirstInvalidSequence);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() =>
            second.Recorder.RecordAsync(second.ProjectId, System, DecisionKind.TaskStatusChanged, "no", Moved("T9", WorkTaskStatus.Pending, WorkTaskStatus.Running), cancellationToken: Ct));
    }

    [Fact]
    public async Task Deleting_an_entry_after_dropping_the_trigger_is_detected()
    {
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await WriteLogAsync(first);
            using var connection = first.Connect();
            Exec(connection, "DROP TRIGGER decision_log_no_delete");
            Exec(connection, "DELETE FROM decision_log WHERE sequence = 4");
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var state = await second.Recorder.LoadAsync(second.ProjectId, Ct);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(5, state.Verification.FirstInvalidSequence);
    }

    [Fact]
    public async Task Concurrent_writers_get_contiguous_sequences_and_a_valid_chain()
    {
        const int Writers = 40;
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var recorded = await Task.WhenAll(Enumerable.Range(1, Writers).Select(i => Task.Run(
            () => instance.Recorder.RecordAsync(
                instance.ProjectId, System, DecisionKind.TaskStatusChanged, $"Escritor {i}", Moved("T" + i, WorkTaskStatus.Pending, WorkTaskStatus.Running), cancellationToken: Ct),
            Ct)));

        var stored = await instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct);
        Assert.Equal(Enumerable.Range(1, Writers).Select(i => (long)i), stored.Select(e => e.Sequence));
        Assert.Equal(recorded.Select(e => e.Sequence).Order(), stored.Select(e => e.Sequence));
        Assert.True(DecisionLog.Load(stored).Verify().IsValid);
    }

    [Fact]
    public async Task An_entry_cannot_be_appended_twice_with_the_same_sequence()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var log = new DecisionLog();
        var entry = log.Append(T0, System, DecisionKind.RunStarted, "inicio", "{}");
        await instance.DecisionLog.AppendAsync(instance.ProjectId, entry, Ct);

        var forged = entry with { Summary = "otra" };
        var ex = await Assert.ThrowsAsync<SqliteException>(() => instance.DecisionLog.AppendAsync(instance.ProjectId, forged, Ct));

        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
        Assert.Equal([entry], await instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_time_with_an_offset_is_read_back_as_the_same_instant_and_still_verifies()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var log = new DecisionLog();
        var madrid = T0.ToOffset(TimeSpan.FromHours(2)).AddTicks(7);
        var entry = log.Append(madrid, System, DecisionKind.RunStarted, "inicio", "{}");
        await instance.DecisionLog.AppendAsync(instance.ProjectId, entry, Ct);

        var read = Assert.Single(await instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct));

        Assert.Equal(entry, read);
        Assert.Equal(madrid.UtcTicks, read.RecordedAt.UtcTicks);
        Assert.True(DecisionLog.Load([read]).Verify().IsValid);
    }

    [Fact]
    public async Task An_empty_log_reads_as_empty_and_its_head_is_the_genesis_hash()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var state = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);

        Assert.Empty(state.Entries);
        Assert.Equal(DecisionLog.GenesisHash, state.HeadHash);
        Assert.True(state.Verification.IsValid);
    }

    [Theory]
    [InlineData("kind = 'Inventado'")]
    [InlineData("kind = '3'")]
    [InlineData("actor_kind = 'Robot'")]
    [InlineData("recorded_at = 'ayer'")]
    public async Task A_row_that_is_not_a_valid_entry_is_reported_with_its_sequence(string change)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await WriteLogAsync(instance, count: 2);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER decision_log_no_update");
            Exec(connection, $"UPDATE decision_log SET {change} WHERE sequence = 2");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => instance.DecisionLog.ReadAllAsync(instance.ProjectId, Ct));

        Assert.Contains("entry 2", ex.Message, StringComparison.Ordinal);
    }
}
