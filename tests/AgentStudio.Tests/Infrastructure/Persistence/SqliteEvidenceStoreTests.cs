using System.IO;
using System.IO.Pipes;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>F3-5: evidence is content-addressed, hashed by the store itself and checked again every time it is read.</summary>
public sealed class SqliteEvidenceStoreTests : IDisposable
{
    private const string Run = "20261015-a1b2c3";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EvidenceDraft Draft(string title = "salida del build", EvidenceKind kind = EvidenceKind.BuildLog, string? run = Run) =>
        new(kind, EvidenceOrigin.CapturedBySystem, title, "agent-studio", RunId: run, TaskId: "T1", Attempt: 1, MediaType: "text/plain");

    private static async Task<EvidenceRecord> CaptureAsync(ProjectInstance instance, byte[] content, EvidenceDraft? draft = null)
    {
        using var stream = new MemoryStream(content);
        return await instance.Evidence.CaptureAsync(instance.ProjectId, draft ?? Draft(), stream, Ct);
    }

    private static async Task<byte[]> ReadAsync(ProjectInstance instance, EvidenceId id)
    {
        await using var stream = await instance.Evidence.OpenContentAsync(instance.ProjectId, id, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private string[] EvidenceFiles() =>
        Directory.Exists(_folder.EvidenceDirectory) ? Directory.GetFiles(_folder.EvidenceDirectory) : [];

    private string[] TempFiles()
    {
        var temp = Path.Combine(_folder.DataDirectory, "tmp");
        return Directory.Exists(temp) ? Directory.GetFiles(temp, "*", SearchOption.AllDirectories) : [];
    }

    private static void AssertSameRecord(EvidenceRecord expected, EvidenceRecord actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Origin, actual.Origin);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.ProducedBy, actual.ProducedBy);
        Assert.Equal(expected.CapturedAt, actual.CapturedAt);
        Assert.Equal(expected.ContentSha256, actual.ContentSha256);
        Assert.Equal(expected.TestRun, actual.TestRun);
        Assert.Equal(expected.Covers, actual.Covers);
        Assert.Equal(expected.SubjectCommit, actual.SubjectCommit);
    }

    [Fact]
    public void A_caller_cannot_hand_the_store_a_hash()
    {
        var members = typeof(EvidenceDraft).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(members, name => name.Contains("Sha", StringComparison.OrdinalIgnoreCase) || name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_store_computes_the_hash_from_the_bytes_it_receives()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("Compilación correcta.\n0 Advertencia(s)\n");

        var record = await CaptureAsync(instance, content);

        Assert.Equal(Sha256(content), record.ContentSha256);
        Assert.True(record.Matches(content));
    }

    [Fact]
    public async Task The_content_is_kept_in_a_file_named_by_its_sha256_and_nothing_is_left_in_tmp()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("diff --git a/x b/x\n");

        var record = await CaptureAsync(instance, content);

        var file = Assert.Single(EvidenceFiles());
        Assert.Equal(record.ContentSha256, Path.GetFileName(file));
        Assert.Equal(content, File.ReadAllBytes(file));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Reading_gives_back_the_same_bytes()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("passed: 12, failed: 0");
        var record = await CaptureAsync(instance, content);

        Assert.Equal(content, await ReadAsync(instance, record.Id));
    }

    [Fact]
    public async Task Reading_content_that_was_modified_after_the_capture_fails()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var record = await CaptureAsync(instance, Bytes("failed: 3"));
        File.WriteAllBytes(Path.Combine(_folder.EvidenceDirectory, record.ContentSha256), Bytes("failed: 0"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => instance.Evidence.OpenContentAsync(instance.ProjectId, record.Id, Ct));

        Assert.Contains(record.Id.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_content_that_disappeared_fails_as_an_integrity_problem()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var record = await CaptureAsync(instance, Bytes("log"));
        File.Delete(Path.Combine(_folder.EvidenceDirectory, record.ContentSha256));

        await Assert.ThrowsAsync<InvalidDataException>(() => instance.Evidence.OpenContentAsync(instance.ProjectId, record.Id, Ct));
    }

    [Fact]
    public async Task Reading_evidence_that_does_not_exist_fails()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => instance.Evidence.OpenContentAsync(instance.ProjectId, Eid("no-existe"), Ct));
    }

    [Fact]
    public async Task Identical_content_is_stored_once_but_each_capture_is_its_own_record()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("mismo contenido");

        var first = await CaptureAsync(instance, content, Draft("intento 1"));
        var second = await CaptureAsync(instance, content, Draft("intento 2"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Single(EvidenceFiles());
        Assert.Equal(content, await ReadAsync(instance, first.Id));
        Assert.Equal(content, await ReadAsync(instance, second.Id));
        Assert.Equal(["intento 1", "intento 2"], (await instance.Evidence.ListAsync(instance.ProjectId, Ct)).Select(e => e.Title));
    }

    [Fact]
    public async Task Concurrent_captures_of_the_same_content_end_in_one_file()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes(string.Concat(Enumerable.Repeat("línea de log\n", 5000)));

        var records = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() => CaptureAsync(instance, content, Draft($"captura {i}")), Ct)));

        Assert.Equal(8, records.Select(r => r.Id).Distinct().Count());
        Assert.Single(EvidenceFiles());
        Assert.Empty(TempFiles());
        foreach (var record in records)
        {
            Assert.Equal(content, await ReadAsync(instance, record.Id));
        }
    }

    /// <summary>
    /// Another handle on the stored file (the capture of the same content going on right now, a scanner that opened it) makes
    /// the check of a second capture fail for a moment: the capture waits for it, and does not fail.
    /// </summary>
    [Fact]
    public async Task A_capture_of_content_already_stored_waits_while_another_handle_holds_the_stored_file()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("contenido repetido");
        var first = await CaptureAsync(instance, content);
        var held = new FileStream(Path.Combine(_folder.EvidenceDirectory, first.ContentSha256), FileMode.Open, FileAccess.Read, FileShare.None);

        Task<EvidenceRecord> second;
        try
        {
            second = Task.Run(() => CaptureAsync(instance, content, Draft("segunda captura")), Ct);
            await Task.WhenAny(second, Task.Delay(300, Ct));

            Assert.False(second.IsCompleted, "The capture ended while the stored file was held: it must wait for it.");
        }
        finally
        {
            await held.DisposeAsync();
        }

        var record = await second;
        Assert.Equal(first.ContentSha256, record.ContentSha256);
        Assert.Single(EvidenceFiles());
        Assert.Empty(TempFiles());
        Assert.Equal(content, await ReadAsync(instance, record.Id));
    }

    [Fact]
    public async Task Capturing_the_same_content_again_restores_a_file_that_was_modified()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = Bytes("original");
        var first = await CaptureAsync(instance, content);
        File.WriteAllBytes(Path.Combine(_folder.EvidenceDirectory, first.ContentSha256), Bytes("manipulado"));

        await CaptureAsync(instance, content);

        Assert.Equal(content, await ReadAsync(instance, first.Id));
    }

    [Fact]
    public async Task Content_is_captured_while_it_streams_from_a_producer_of_unknown_length()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var content = new byte[4 * 1024 * 1024 + 123];
        new Random(20261015).NextBytes(content);
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        Assert.False(client.CanSeek);
        Assert.Throws<NotSupportedException>(() => client.Length);

        // The pipe holds a few kilobytes: the producer can only finish if the store keeps consuming as it goes.
        var producer = Task.Run(
            async () =>
            {
                for (var offset = 0; offset < content.Length; offset += 64 * 1024)
                {
                    await server.WriteAsync(content.AsMemory(offset, Math.Min(64 * 1024, content.Length - offset)), Ct);
                }

                server.Dispose();
            },
            Ct);
        var record = await instance.Evidence.CaptureAsync(instance.ProjectId, Draft("salida del test"), client, Ct);
        await producer;

        Assert.Equal(Sha256(content), record.ContentSha256);
        Assert.Equal(content, await ReadAsync(instance, record.Id));
        using var connection = instance.Connect();
        Assert.Equal(content.Length, Scalar(connection, $"SELECT size_bytes FROM evidence WHERE id = '{record.Id.Value}'"));
    }

    [Fact]
    public async Task The_record_and_its_place_in_the_run_are_stored()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var draft = new EvidenceDraft(
            EvidenceKind.TestRun,
            EvidenceOrigin.CapturedBySystem,
            "dotnet test",
            "agent-studio",
            new TestRunSummary(Passed: 12, Failed: 1, Skipped: 2, ExitCode: 1),
            [Rid("RQ-02"), Rid("RQ-01"), Rid("RQ-02")],
            Run,
            "T3",
            2,
            "application/xml");

        var record = await CaptureAsync(instance, Bytes("<TestRun />"), draft);

        Assert.Equal([Rid("RQ-02"), Rid("RQ-01")], record.Covers);
        AssertSameRecord(record, Assert.Single(await instance.Evidence.ListAsync(instance.ProjectId, Ct)));
        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"SELECT COUNT(*) FROM evidence WHERE id = '{record.Id.Value}' AND run_id = '{Run}' AND task_id = 'T3' AND attempt = 2 AND media_type = 'application/xml' AND size_bytes = 11"));
        Assert.Equal(2, Scalar(connection, $"SELECT COUNT(*) FROM evidence_requirements WHERE evidence_id = '{record.Id.Value}'"));
    }

    [Fact]
    public async Task The_commit_the_evidence_verified_is_stored_and_read_back()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var bound = await CaptureAsync(instance, Bytes("<TestRun />"), new EvidenceDraft(
            EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test", "agent-studio", Green, RunId: Run, TaskId: "T1", Attempt: 1, SubjectCommit: CommitA));
        var unbound = await CaptureAsync(instance, Bytes("prompt"), Draft("prompt", EvidenceKind.Document));

        Assert.Equal(CommitA, bound.SubjectCommit);
        Assert.Null(unbound.SubjectCommit);
        Assert.Equal([CommitA, null], (await instance.Evidence.ListByRunAsync(instance.ProjectId, Run, Ct)).Select(e => e.SubjectCommit));
        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"SELECT COUNT(*) FROM evidence WHERE id = '{bound.Id.Value}' AND subject_commit = '{CommitA}'"));
    }

    [Fact]
    public async Task A_subject_commit_that_is_not_a_git_object_id_leaves_nothing_behind()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        await Assert.ThrowsAsync<DomainRuleViolationException>(() => CaptureAsync(instance, Bytes("x"), Draft() with { SubjectCommit = "HEAD" }));

        Assert.Empty(EvidenceFiles());
        Assert.Empty(TempFiles());
        Assert.Empty(await instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task Records_survive_reopening_the_project()
    {
        EvidenceRecord record;
        var content = Bytes("persistente");
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            record = await CaptureAsync(first, content, Draft(kind: EvidenceKind.CodeDiff) with { SubjectCommit = CommitB });
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);

        AssertSameRecord(record, Assert.Single(await second.Evidence.ListByRunAsync(second.ProjectId, Run, Ct)));
        Assert.Equal(content, await ReadAsync(second, record.Id));
    }

    [Fact]
    public async Task Evidence_is_listed_by_run_in_capture_order()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var e1 = await CaptureAsync(instance, Bytes("1"), Draft("e1", run: Run));
        await CaptureAsync(instance, Bytes("2"), Draft("e2", run: "20261015-otra00"));
        var e3 = await CaptureAsync(instance, Bytes("3"), Draft("e3", run: Run));
        await CaptureAsync(instance, Bytes("4"), Draft("e4", run: null));

        var byRun = await instance.Evidence.ListByRunAsync(instance.ProjectId, Run, Ct);
        var all = await instance.Evidence.ListAsync(instance.ProjectId, Ct);

        Assert.Equal([e1.Id, e3.Id], byRun.Select(e => e.Id));
        Assert.Equal(["e1", "e2", "e3", "e4"], all.Select(e => e.Title));
        Assert.Empty(await instance.Evidence.ListByRunAsync(instance.ProjectId, "20261015-nada00", Ct));
    }

    [Fact]
    public async Task A_draft_the_domain_rejects_leaves_nothing_behind()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var noSummary = Draft(kind: EvidenceKind.TestRun);

        await Assert.ThrowsAsync<DomainRuleViolationException>(() => CaptureAsync(instance, Bytes("x"), noSummary));

        Assert.Empty(EvidenceFiles());
        Assert.Empty(TempFiles());
        Assert.Empty(await instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_producer_that_fails_half_way_leaves_no_file_and_no_record()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        using var broken = new BrokenStream(Bytes("la mitad de un log"));

        await Assert.ThrowsAsync<IOException>(() => instance.Evidence.CaptureAsync(instance.ProjectId, Draft(), broken, Ct));

        Assert.Empty(EvidenceFiles());
        Assert.Empty(TempFiles());
        Assert.Empty(await instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_cancelled_capture_leaves_no_file_and_no_record()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        using var cts = new CancellationTokenSource();
        using var stream = new MemoryStream(Bytes("contenido"));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => instance.Evidence.CaptureAsync(instance.ProjectId, Draft(), stream, cts.Token));

        Assert.Empty(EvidenceFiles());
        Assert.Empty(TempFiles());
        Assert.Empty(await instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_stored_hash_that_is_not_a_sha256_is_never_used_as_a_path()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var record = await CaptureAsync(instance, Bytes("x"));
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER evidence_no_update");
            Exec(connection, $"UPDATE evidence SET content_sha256 = '..\\..\\agentstudio.db' WHERE id = '{record.Id.Value}'");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => instance.Evidence.OpenContentAsync(instance.ProjectId, record.Id, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    /// <summary>Gives its bytes and then fails, like a process whose output pipe breaks.</summary>
    private sealed class BrokenStream(byte[] content) : Stream
    {
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served)
            {
                throw new IOException("El productor se cayó.");
            }

            _served = true;
            var n = Math.Min(count, content.Length);
            Array.Copy(content, 0, buffer, offset, n);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
