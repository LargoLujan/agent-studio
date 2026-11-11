using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>
/// Append-only decision log kept in memory, used under the real <c>DecisionRecorder</c>. <see cref="FailFromSequence"/>
/// simulates a store that dies: every entry with that sequence or a later one is refused, as a full disk would.
/// </summary>
internal sealed class InMemoryDecisionLogStore : IDecisionLogStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DecisionEntry>> _entries = new(StringComparer.Ordinal);

    /// <summary>When set, appending an entry whose sequence is at least this value throws <see cref="IOException"/>.</summary>
    public long? FailFromSequence { get; set; }

    /// <summary>When it returns true for an entry, appending it throws <see cref="IOException"/>.</summary>
    public Func<DecisionEntry, bool>? FailWhen { get; set; }

    public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if ((FailFromSequence is { } limit && entry.Sequence >= limit) || (FailWhen?.Invoke(entry) ?? false))
            {
                throw new IOException($"Simulated store failure at entry {entry.Sequence}.");
            }

            if (!_entries.TryGetValue(projectId, out var list))
            {
                list = [];
                _entries[projectId] = list;
            }

            if (list.Count + 1 != entry.Sequence)
            {
                throw new InvalidOperationException($"Entry {entry.Sequence} does not follow {list.Count}.");
            }

            list.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult(Entries(projectId));

    public IReadOnlyList<DecisionEntry> Entries(string projectId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(projectId, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Rewrites a stored entry behind the recorder's back (what a manual UPDATE on the table would do).</summary>
    public void Tamper(string projectId, int index, Func<DecisionEntry, DecisionEntry> change)
    {
        lock (_gate)
        {
            _entries[projectId][index] = change(_entries[projectId][index]);
        }
    }
}

/// <summary>Content-addressed evidence kept in memory. The store computes the hash, as the real one does.</summary>
internal sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly Lock _gate = new();
    private readonly List<(string Project, EvidenceDraft Draft, EvidenceRecord Record, byte[] Content)> _items = [];

    public Func<EvidenceDraft, Exception?>? FailWhen { get; set; }

    public async Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(content);
        if (FailWhen?.Invoke(draft) is { } failure)
        {
            throw failure;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        lock (_gate)
        {
            var record = new EvidenceRecord(
                new EvidenceId($"ev-{_items.Count + 1:D4}"),
                draft.Kind,
                draft.Origin,
                draft.Title,
                draft.ProducedBy,
                DateTimeOffset.UnixEpoch,
                ContentHash.Sha256Hex(bytes),
                draft.TestRun,
                draft.Covers,
                draft.SubjectCommit);
            _items.Add((projectId, draft, record, bytes));
            return record;
        }
    }

    public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var item = _items.Single(i => i.Project == projectId && i.Record.Id == id);
            return Task.FromResult<Stream>(new MemoryStream(item.Content, writable: false));
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>([.. _items.Where(i => i.Project == projectId).Select(i => i.Record)]);
        }
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EvidenceRecord>>(
                [.. _items.Where(i => i.Project == projectId && i.Draft.RunId == runId).Select(i => i.Record)]);
        }
    }

    public EvidenceRecord Record(EvidenceId id)
    {
        lock (_gate)
        {
            return _items.Single(i => i.Record.Id == id).Record;
        }
    }

    public EvidenceDraft Draft(EvidenceId id)
    {
        lock (_gate)
        {
            return _items.Single(i => i.Record.Id == id).Draft;
        }
    }

    public string Text(EvidenceId id)
    {
        lock (_gate)
        {
            return System.Text.Encoding.UTF8.GetString(_items.Single(i => i.Record.Id == id).Content);
        }
    }

    public bool Contains(EvidenceId id)
    {
        lock (_gate)
        {
            return _items.Any(i => i.Record.Id == id);
        }
    }

    /// <summary>
    /// Rewrites the content of a piece of evidence and the hash the store keeps for it, as someone with access to both
    /// the evidence folder and the database could: the store itself then sees nothing wrong.
    /// </summary>
    public void Rewrite(EvidenceId id, string content)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Record.Id == id);
            var item = _items[index];
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var record = item.Record;
            var rewritten = new EvidenceRecord(record.Id, record.Kind, record.Origin, record.Title, record.ProducedBy, record.CapturedAt, ContentHash.Sha256Hex(bytes), record.TestRun, record.Covers, record.SubjectCommit);
            _items[index] = (item.Project, item.Draft, rewritten, bytes);
        }
    }

    /// <summary>
    /// Rewrites the row of a piece of evidence (its metadata) and leaves its content and hash as they were, as someone
    /// who drops the update trigger of the table, changes the row and puts the trigger back would.
    /// </summary>
    public void ChangeRow(EvidenceId id, Func<EvidenceRecord, EvidenceRecord> change)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Record.Id == id);
            var item = _items[index];
            _items[index] = (item.Project, item.Draft, change(item.Record), item.Content);
        }
    }

    /// <summary>
    /// Gives the row of a piece of evidence to another run (its <c>run_id</c>), as someone who rewrites the table behind its
    /// triggers would: the store no longer lists it for the run that registered it, but still opens it by its id.
    /// </summary>
    public void MoveToRun(EvidenceId id, string runId)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Record.Id == id);
            var item = _items[index];
            _items[index] = (item.Project, item.Draft with { RunId = runId }, item.Record, item.Content);
        }
    }

    /// <summary>Loses a piece of evidence, as a damaged or restored-from-backup evidence folder would.</summary>
    public void Lose(EvidenceId id)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Record.Id == id);
        }
    }

    public IReadOnlyList<(EvidenceDraft Draft, EvidenceRecord Record)> All()
    {
        lock (_gate)
        {
            return [.. _items.Select(i => (i.Draft, i.Record))];
        }
    }
}

internal sealed class InMemoryPlanStore : IPlanStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PlanDefinition> _plans = new(StringComparer.Ordinal);

    public int Saves { get; private set; }

    public Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            Saves++;
            _plans.TryAdd(projectId + "/" + plan.ContentHash, plan);
        }

        return Task.FromResult(plan.ContentHash);
    }

    public Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_plans.GetValueOrDefault(projectId + "/" + hash));
        }
    }

    public IReadOnlyList<PlanDefinition> All()
    {
        lock (_gate)
        {
            return [.. _plans.Values];
        }
    }
}

internal sealed class InMemoryRunRegistry : IRunRegistry
{
    private readonly Lock _gate = new();
    private readonly List<RunRecord> _runs = [];

    public Task CreateAsync(RunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            if (_runs.Any(r => r.ProjectId == run.ProjectId && r.Id == run.Id))
            {
                throw new InvalidOperationException($"Run {run.Id} already exists.");
            }

            _runs.Add(run);
        }

        return Task.CompletedTask;
    }

    public Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.FirstOrDefault(r => r.ProjectId == projectId && r.Id == id));
        }
    }

    /// <summary>Changes a stored header behind the engine's back, as a store that lost a field, or someone with the database, would.</summary>
    public void Replace(RunId id, Func<RunRecord, RunRecord> change)
    {
        lock (_gate)
        {
            var index = _runs.FindIndex(r => r.Id == id);
            _runs[index] = change(_runs[index]);
        }
    }

    public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([.. _runs.Where(r => r.ProjectId == projectId)]);
        }
    }
}

internal sealed class InMemoryRequirementStore : IRequirementStore
{
    private readonly Lock _gate = new();
    private readonly List<(string Project, FrozenRequirementSet Set)> _sets = [];

    public int Saves { get; private set; }

    public Task SaveAsync(string projectId, FrozenRequirementSet requirements, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Saves++;
            if (_sets.Any(s => s.Project == projectId && s.Set.ContentHash == requirements.ContentHash))
            {
                throw new InvalidOperationException("The requirement set is already stored.");
            }

            _sets.Add((projectId, requirements));
        }

        return Task.CompletedTask;
    }

    public Task<FrozenRequirementSet?> FindAsync(string projectId, string contentHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_sets.Where(s => s.Project == projectId && s.Set.ContentHash == contentHash).Select(s => s.Set).FirstOrDefault());
        }
    }

    public Task<FrozenRequirementSet?> LatestAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_sets.Where(s => s.Project == projectId).Select(s => s.Set).LastOrDefault());
        }
    }
}

internal sealed class InMemoryAgentRoster(IEnumerable<AgentDefinition> agents) : IAgentRoster
{
    private readonly List<AgentDefinition> _agents = [.. agents];

    /// <summary>The roster as the store lists it now: what the canvas validates a plan against before it is approved.</summary>
    public IReadOnlyList<AgentDefinition> Agents => [.. _agents];

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        _agents.RemoveAll(a => a.Id == agent.Id);
        _agents.Add(agent);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken)
    {
        _agents.RemoveAll(a => a.Id == agent);
        return Task.CompletedTask;
    }

    /// <summary>Deletes an agent behind the engine's back, as someone editing the store would.</summary>
    public void Remove(AgentId agent) => _agents.RemoveAll(a => a.Id == agent);
}

/// <summary>The prompt journal of the provider pipeline, kept in memory.</summary>
internal sealed class InMemoryPromptJournal : IPromptJournal
{
    private readonly Lock _gate = new();

    public List<PromptJournalEntry> Prompts { get; } = [];

    public List<(JournalEntryId Id, PromptOutcome Outcome)> Outcomes { get; } = [];

    public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            Prompts.Add(entry);
            return Task.FromResult(new JournalEntryId(new DateOnly(2026, 10, 15), Prompts.Count));
        }
    }

    public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
    {
        lock (_gate)
        {
            Outcomes.Add((id, outcome));
        }

        return Task.CompletedTask;
    }
}
