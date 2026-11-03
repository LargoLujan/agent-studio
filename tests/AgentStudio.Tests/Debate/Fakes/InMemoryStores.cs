using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Tests.Debate.Fakes;

// In-memory doubles of the Application stores, shared by the Debate and Security/Approvals tests. They keep the
// contracts the real SQLite stores keep (append-only, content-addressed evidence whose hash the store computes).

/// <summary>The append-only decision_log table: refuses a sequence that is already stored, like the primary key.</summary>
internal sealed class InMemoryDecisionLogStore : IDecisionLogStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DecisionEntry>> _entries = [];

    /// <summary>The next appends fail before anything is stored.</summary>
    public int FailBeforeStoring { get; set; }

    /// <summary>Appends whose 1-based position (counting every call) is listed here fail before storing.</summary>
    public HashSet<int> FailingAppends { get; } = [];

    public int Appends { get; private set; }

    public IReadOnlyList<DecisionEntry> Of(string projectId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(projectId, out var list) ? [.. list.OrderBy(e => e.Sequence)] : [];
        }
    }

    /// <summary>Rewrites a stored entry behind the recorder's back, as someone editing the database would.</summary>
    public void Tamper(string projectId, long sequence, Func<DecisionEntry, DecisionEntry> change)
    {
        lock (_gate)
        {
            var list = _entries[projectId];
            var index = list.FindIndex(e => e.Sequence == sequence);
            list[index] = change(list[index]);
        }
    }

    public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Appends++;
            if (FailBeforeStoring > 0 || FailingAppends.Contains(Appends))
            {
                FailBeforeStoring = Math.Max(0, FailBeforeStoring - 1);
                throw new IOException("disk full");
            }

            var list = _entries.TryGetValue(projectId, out var existing) ? existing : _entries[projectId] = [];
            if (list.Any(e => e.Sequence == entry.Sequence))
            {
                throw new InvalidOperationException($"UNIQUE constraint failed: decision_log.sequence {entry.Sequence}");
            }

            list.Add(entry);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(projectId));
}

/// <summary>Content-addressed evidence: the store hashes the bytes it receives and hands out the content later.</summary>
internal sealed class FakeEvidenceStore(TimeProvider clock) : IEvidenceStore
{
    private readonly Lock _gate = new();
    private readonly List<(string Project, string? Run, EvidenceRecord Record)> _records = [];
    private readonly Dictionary<EvidenceId, byte[]> _content = [];

    public List<EvidenceDraft> Drafts { get; } = [];

    /// <summary>Ids whose content cannot be opened any more (a file removed from disk).</summary>
    public HashSet<EvidenceId> Unreadable { get; } = [];

    public async Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        lock (_gate)
        {
            var id = new EvidenceId($"EV-{_records.Count + 1}");
            var record = new EvidenceRecord(
                id,
                draft.Kind,
                draft.Origin,
                draft.Title,
                draft.ProducedBy,
                clock.GetUtcNow(),
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                draft.TestRun,
                draft.Covers,
                draft.SubjectCommit);
            _records.Add((projectId, draft.RunId, record));
            _content[id] = bytes;
            Drafts.Add(draft);
            return record;
        }
    }

    /// <summary>
    /// Rewrites the row of a piece of evidence (its metadata) and leaves its content as it was, as someone who drops the
    /// update trigger of the table, changes the row and puts the trigger back would.
    /// </summary>
    public void TamperRow(EvidenceId id, Func<EvidenceRecord, EvidenceRecord> change)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Record.Id == id);
            var (project, run, record) = _records[index];
            _records[index] = (project, run, change(record));
        }
    }

    /// <summary>Replaces the stored bytes without touching the recorded hash, as tampering on disk would.</summary>
    public void TamperContent(EvidenceId id, byte[] bytes)
    {
        lock (_gate)
        {
            _content[id] = bytes;
        }
    }

    public string ContentText(EvidenceId id)
    {
        lock (_gate)
        {
            return System.Text.Encoding.UTF8.GetString(_content[id]);
        }
    }

    public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Unreadable.Contains(id) || !_content.TryGetValue(id, out var bytes))
            {
                throw new FileNotFoundException($"Evidence '{id}' has no content.");
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>([.. _records.Where(r => r.Project == projectId).Select(r => r.Record)]);
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>(
                [.. _records.Where(r => r.Project == projectId && r.Run == runId).Select(r => r.Record)]);
        }
    }
}

internal sealed class FakeRunRegistry : IRunRegistry
{
    private readonly List<RunRecord> _runs = [];

    public Task CreateAsync(RunRecord run, CancellationToken cancellationToken)
    {
        _runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken) =>
        Task.FromResult(_runs.FirstOrDefault(r => r.ProjectId == projectId && r.Id == id));

    public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunRecord>>([.. _runs.Where(r => r.ProjectId == projectId)]);

    /// <summary>Rewrites a stored header behind the store's back, as someone who drops the update trigger of the table would.</summary>
    public void Replace(RunId id, Func<RunRecord, RunRecord> change)
    {
        var index = _runs.FindIndex(r => r.Id == id);
        _runs[index] = change(_runs[index]);
    }
}

internal sealed class FakePlanStore : IPlanStore
{
    private readonly Dictionary<(string, string), PlanDefinition> _plans = [];

    public Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken)
    {
        _plans[(projectId, plan.ContentHash)] = plan;
        return Task.FromResult(plan.ContentHash);
    }

    public Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken) =>
        Task.FromResult(_plans.GetValueOrDefault((projectId, hash)));
}

internal sealed class FakeRequirementStore : IRequirementStore
{
    private readonly List<(string Project, FrozenRequirementSet Set)> _sets = [];

    public Task SaveAsync(string projectId, FrozenRequirementSet requirements, CancellationToken cancellationToken)
    {
        _sets.Add((projectId, requirements));
        return Task.CompletedTask;
    }

    public Task<FrozenRequirementSet?> FindAsync(string projectId, string contentHash, CancellationToken cancellationToken) =>
        Task.FromResult<FrozenRequirementSet?>(_sets.LastOrDefault(s => s.Project == projectId && s.Set.ContentHash == contentHash).Set);

    public Task<FrozenRequirementSet?> LatestAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<FrozenRequirementSet?>(_sets.LastOrDefault(s => s.Project == projectId).Set);
}

/// <summary>Append-only verdicts: every append is kept, in order, exactly as given.</summary>
internal sealed class FakeVerdictStore : IVerdictStore
{
    public List<(string Project, StoredVerdict Verdict)> Appended { get; } = [];

    public Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken)
    {
        Appended.Add((projectId, verdict));
        return Task.FromResult(verdict.Id);
    }

    public Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredVerdict>>(
            [.. Appended.Where(v => v.Project == projectId && (runId is null || v.Verdict.RunId == runId)).Select(v => v.Verdict)]);

    public IReadOnlyList<StoredVerdict> Stored => [.. Appended.Select(v => v.Verdict)];
}
