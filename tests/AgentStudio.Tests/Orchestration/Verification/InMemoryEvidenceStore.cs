using System.IO;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// An <see cref="IEvidenceStore"/> that keeps everything in memory and, like the real store, computes the SHA-256 of
/// the bytes it receives itself. The SQLite store is written by another package; the tests of verification and
/// integration only need the contract.
/// </summary>
internal sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly List<Item> _items = [];
    private readonly Lock _gate = new();

    internal sealed record Item(string ProjectId, EvidenceDraft Draft, EvidenceRecord Record, byte[] Content);

    public IReadOnlyList<Item> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public async Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var sha = ContentHash.Sha256Hex(bytes);

        lock (_gate)
        {
            var record = new EvidenceRecord(
                new EvidenceId($"ev-{_items.Count + 1}-{sha[..8]}"),
                draft.Kind,
                draft.Origin,
                draft.Title,
                draft.ProducedBy,
                DateTimeOffset.UtcNow,
                sha,
                draft.TestRun,
                draft.Covers,
                draft.SubjectCommit);
            _items.Add(new Item(projectId, draft, record, bytes));
            return record;
        }
    }

    public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
    {
        var item = Find(projectId, id);
        return Task.FromResult<Stream>(new MemoryStream(item.Content, writable: false));
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>(_items.Where(i => i.ProjectId == projectId).Select(i => i.Record).ToList());
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>(
                _items.Where(i => i.ProjectId == projectId && i.Draft.RunId == runId).Select(i => i.Record).ToList());
        }
    }

    /// <summary>The captured content of an evidence, decoded as UTF-8.</summary>
    public string Text(EvidenceId id) => System.Text.Encoding.UTF8.GetString(Single(id).Content);

    public Item Single(EvidenceId id)
    {
        lock (_gate)
        {
            return _items.Single(i => i.Record.Id == id);
        }
    }

    private Item Find(string projectId, EvidenceId id)
    {
        lock (_gate)
        {
            return _items.SingleOrDefault(i => i.ProjectId == projectId && i.Record.Id == id)
                ?? throw new KeyNotFoundException($"No evidence '{id}' in project '{projectId}'.");
        }
    }
}
