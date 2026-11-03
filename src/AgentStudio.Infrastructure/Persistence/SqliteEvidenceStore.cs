using System.Buffers;
using System.Security.Cryptography;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Content-addressed, append-only evidence (F3-5). The content goes to <c>.agentstudio/evidence/&lt;sha256&gt;</c> and
/// the metadata to the <c>evidence</c> table.
/// <list type="bullet">
/// <item>The store computes the hash while it streams the content to a temporary file, so neither the length nor the
/// hash is ever taken from the caller and the content never has to fit in memory (process output qualifies).</item>
/// <item>The file is moved into place only when it is complete. The same content is kept once, however many records
/// cite it; if the file already there no longer matches its name, the authentic bytes just received replace it.</item>
/// <item>Every read hashes the file again through the same handle it returns, which no one can write while it is
/// open: content that was modified, or is missing, is an <see cref="InvalidDataException"/>.</item>
/// <item>Each capture is a record of its own (a GUID id); records are listed in capture order.</item>
/// </list>
/// </summary>
public sealed class SqliteEvidenceStore(ProjectDatabases databases, TimeProvider timeProvider) : IEvidenceStore
{
    private const int BufferSize = 81920;

    /// <summary>A well-formed hash for validating a draft before its content is read.</summary>
    private static readonly string NoHashYet = new('0', ContentHash.HexLength);

    public async Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream cannot be read.", nameof(content));
        }

        var covers = (draft.Covers ?? []).Distinct().ToList();
        if (covers.Any(c => c.Value is null))
        {
            throw new ArgumentException("A covered requirement has no id.", nameof(draft));
        }

        var storage = databases.Get(projectId);
        var id = new EvidenceId(Guid.NewGuid().ToString("N"));
        var capturedAt = timeProvider.GetUtcNow();

        // The record checks the draft (title, author, a test run needs its summary) before anything is written.
        _ = Record(id, draft, capturedAt, NoHashYet, covers);

        var (sha256, size) = await StoreContentAsync(storage, content, cancellationToken).ConfigureAwait(false);
        var record = Record(id, draft, capturedAt, sha256, covers);
        await InsertAsync(projectId, record, draft, size, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        var storage = databases.Get(projectId);
        var sha256 = await FindHashAsync(projectId, id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Evidence '{id}' does not exist in project '{projectId}'.");
        if (!ContentHash.IsSha256Hex(sha256))
        {
            throw new InvalidDataException($"Evidence '{id}' records '{sha256}' as its hash, which is not a SHA-256: the row was modified.");
        }

        FileStream stream;
        try
        {
            stream = OpenForReading(Path.Combine(storage.EvidenceDirectory, sha256));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException($"The content of evidence '{id}' ({sha256}) is missing from the evidence store.", ex);
        }

        try
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The content of evidence '{id}' no longer matches its SHA-256 {sha256}: it was modified after it was captured.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        ListCoreAsync(projectId, runId: null, cancellationToken);

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return ListCoreAsync(projectId, runId, cancellationToken);
    }

    private static EvidenceRecord Record(EvidenceId id, EvidenceDraft draft, DateTimeOffset capturedAt, string sha256, IReadOnlyList<RequirementId> covers) =>
        new(id, draft.Kind, draft.Origin, draft.Title, draft.ProducedBy, capturedAt, sha256, draft.TestRun, covers, draft.SubjectCommit);

    private static FileStream OpenForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<(string Sha256, long Size)> StoreContentAsync(ProjectStorage storage, Stream content, CancellationToken cancellationToken)
    {
        // A link left at .agentstudio, tmp or evidence would take the content elsewhere: each folder is checked before
        // anything is created in it, and the temporary file through the handle that writes it.
        ProjectStorageGuard.CreateFolder(storage, storage.TempDirectory);
        ProjectStorageGuard.CreateFolder(storage, storage.EvidenceDirectory);

        // Same volume as the evidence folder, so the final move is a rename that either happens entirely or not at all.
        var temp = Path.Combine(storage.TempDirectory, "evidence-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            string sha256;
            long size = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    ProjectStorageGuard.CheckOpened(storage, file.SafeFileHandle, temp);
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int read;
                        while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            size += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            await PlaceAsync(temp, Path.Combine(storage.EvidenceDirectory, sha256), sha256, cancellationToken).ConfigureAwait(false);
            return (sha256, size);
        }
        finally
        {
            // After a successful move there is nothing here any more; after a failure this removes the partial file.
            TryDelete(temp);
        }
    }

    private static async Task PlaceAsync(string temp, string target, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            File.Move(temp, target);
            return;
        }
        catch (IOException) when (File.Exists(target))
        {
            // The same content is already stored, by an earlier capture or by one running right now.
        }

        string existing;
        var stream = OpenForReading(target);
        await using (stream.ConfigureAwait(false))
        {
            existing = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(existing, sha256, StringComparison.Ordinal))
        {
            // The stored file was modified; the bytes just hashed are the authentic content of that name.
            File.Move(temp, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover in tmp/ is harmless: it is never read and the folder is excluded from git.
        }
    }

    private async Task InsertAsync(string projectId, EvidenceRecord record, EvidenceDraft draft, long size, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = Command(connection, """
            INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, storage_uri,
                                 test_passed, test_failed, test_skipped, test_exit_code, run_id, task_id, attempt, media_type, size_bytes,
                                 subject_commit)
            VALUES ($id, $project, $kind, $origin, $title, $producedBy, $at, $sha, $uri,
                    $passed, $failed, $skipped, $exitCode, $run, $task, $attempt, $mediaType, $size,
                    $subjectCommit);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", record.Id.Value);
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$kind", record.Kind.ToString());
            insert.Parameters.AddWithValue("$origin", record.Origin.ToString());
            insert.Parameters.AddWithValue("$title", record.Title);
            insert.Parameters.AddWithValue("$producedBy", record.ProducedBy);
            insert.Parameters.AddWithValue("$at", Time(record.CapturedAt));
            insert.Parameters.AddWithValue("$sha", record.ContentSha256);
            insert.Parameters.AddWithValue("$uri", "evidence/" + record.ContentSha256);
            insert.Parameters.AddWithValue("$passed", OrNull(record.TestRun?.Passed));
            insert.Parameters.AddWithValue("$failed", OrNull(record.TestRun?.Failed));
            insert.Parameters.AddWithValue("$skipped", OrNull(record.TestRun?.Skipped));
            insert.Parameters.AddWithValue("$exitCode", OrNull(record.TestRun?.ExitCode));
            insert.Parameters.AddWithValue("$run", OrNull(draft.RunId));
            insert.Parameters.AddWithValue("$task", OrNull(draft.TaskId));
            insert.Parameters.AddWithValue("$attempt", OrNull(draft.Attempt));
            insert.Parameters.AddWithValue("$mediaType", OrNull(draft.MediaType));
            insert.Parameters.AddWithValue("$size", size);
            insert.Parameters.AddWithValue("$subjectCommit", OrNull(record.SubjectCommit));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var requirement in record.Covers)
        {
            await using var cover = Command(connection, "INSERT INTO evidence_requirements(evidence_id, requirement_id) VALUES ($id, $requirement);", transaction);
            cover.Parameters.AddWithValue("$id", record.Id.Value);
            cover.Parameters.AddWithValue("$requirement", requirement.Value);
            await cover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindHashAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, "SELECT content_sha256 FROM evidence WHERE project_id = $project AND id = $id;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$id", id.Value);
        return await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<IReadOnlyList<EvidenceRecord>> ListCoreAsync(string projectId, string? runId, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        var filter = runId is null ? string.Empty : " AND e.run_id = $run";

        var covers = new Dictionary<string, List<RequirementId>>(StringComparer.Ordinal);
        await using (var selectCovers = Command(connection, $"""
            SELECT r.evidence_id, r.requirement_id
            FROM evidence_requirements r JOIN evidence e ON e.id = r.evidence_id
            WHERE e.project_id = $project{filter}
            ORDER BY r.rowid;
            """))
        {
            selectCovers.Parameters.AddWithValue("$project", projectId);
            selectCovers.Parameters.AddWithValue("$run", OrNull(runId));
            await using var reader = await selectCovers.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var evidenceId = reader.GetString(0);
                if (!covers.TryGetValue(evidenceId, out var list))
                {
                    covers[evidenceId] = list = [];
                }

                list.Add(new RequirementId(reader.GetString(1)));
            }
        }

        var records = new List<EvidenceRecord>();
        await using var select = Command(connection, $"""
            SELECT e.id, e.kind, e.origin, e.title, e.produced_by, e.captured_at, e.content_sha256,
                   e.test_passed, e.test_failed, e.test_skipped, e.test_exit_code, e.subject_commit
            FROM evidence e
            WHERE e.project_id = $project{filter}
            ORDER BY e.rowid;
            """);
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$run", OrNull(runId));
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRecord(reader, covers));
            }
        }

        return records;
    }

    private static EvidenceRecord ReadRecord(SqliteDataReader reader, Dictionary<string, List<RequirementId>> covers)
    {
        var id = reader.GetString(0);
        string Problem(string what) => $"Evidence row '{id}' is not valid: {what}.";

        var kindText = reader.GetString(1);
        if (!TryParseEnum<EvidenceKind>(kindText, out var kind))
        {
            throw new InvalidDataException(Problem($"unknown kind '{kindText}'"));
        }

        var originText = reader.GetString(2);
        if (!TryParseEnum<EvidenceOrigin>(originText, out var origin))
        {
            throw new InvalidDataException(Problem($"unknown origin '{originText}'"));
        }

        var capturedText = reader.GetString(5);
        if (!TryParseTime(capturedText, out var capturedAt))
        {
            throw new InvalidDataException(Problem($"captured_at '{capturedText}' is not a round-trip time"));
        }

        var testColumns = new[] { NullableInt32(reader, 7), NullableInt32(reader, 8), NullableInt32(reader, 9), NullableInt32(reader, 10) };
        TestRunSummary? testRun = null;
        if (testColumns.All(c => c is not null))
        {
            testRun = new TestRunSummary(testColumns[0]!.Value, testColumns[1]!.Value, testColumns[2]!.Value, testColumns[3]!.Value);
        }
        else if (testColumns.Any(c => c is not null))
        {
            throw new InvalidDataException(Problem("its test summary is incomplete"));
        }

        try
        {
            return new EvidenceRecord(
                new EvidenceId(id),
                kind,
                origin,
                reader.GetString(3),
                reader.GetString(4),
                capturedAt,
                reader.GetString(6),
                testRun,
                covers.TryGetValue(id, out var list) ? list : [],
                reader.IsDBNull(11) ? null : reader.GetString(11));
        }
        catch (DomainRuleViolationException ex)
        {
            throw new InvalidDataException(Problem(ex.Message), ex);
        }
    }
}
