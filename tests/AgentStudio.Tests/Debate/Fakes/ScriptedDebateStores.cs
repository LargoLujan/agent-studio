using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>The headers of the debates, append-only like <c>debate_sessions</c>: the same row twice is a no-op, another with the same id is refused.</summary>
internal sealed class FakeDebateRegistry : IDebateRegistry
{
    private readonly Lock _gate = new();
    private readonly List<DebateRecord> _records = [];

    /// <summary>What the store keeps of a header it is given (by default the header itself), as a store that normalizes it would.</summary>
    public Func<DebateRecord, DebateRecord> OnStore { get; set; } = debate => debate;

    public Task CreateAsync(DebateRecord debate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            debate = OnStore(debate);
            if (_records.FirstOrDefault(r => r.ProjectId == debate.ProjectId && r.Id == debate.Id) is { } existing)
            {
                return ReferenceEquals(existing, debate) || existing == debate
                    ? Task.CompletedTask
                    : throw new InvalidOperationException($"debate_sessions is append-only: {debate.Id} is already stored.");
            }

            _records.Add(debate);
            return Task.CompletedTask;
        }
    }

    public Task<DebateRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.FirstOrDefault(r => r.ProjectId == projectId && r.Id == id));
        }
    }

    public Task<IReadOnlyList<DebateRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DebateRecord>>([.. _records.Where(r => r.ProjectId == projectId)]);
        }
    }

    /// <summary>Rewrites a stored header behind the store's back, as someone who drops its triggers would.</summary>
    public void Replace(RunId id, Func<DebateRecord, DebateRecord> change)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == id);
            _records[index] = change(_records[index]);
        }
    }
}

/// <summary>One activity with agents per project (D-05), as WP5 implements it: a debate shares the project with nothing.</summary>
internal sealed class FakeProjectActivity : IProjectActivity
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<ProjectActivityKind>> _held = new(StringComparer.Ordinal);

    public IDisposable? TryEnter(string projectId, ProjectActivityKind kind)
    {
        lock (_gate)
        {
            var held = _held.TryGetValue(projectId, out var list) ? list : _held[projectId] = [];
            var shares = held.Count == 0 || (kind is ProjectActivityKind.Run or ProjectActivityKind.Planning && held.All(k => k is ProjectActivityKind.Run or ProjectActivityKind.Planning));
            if (!shares)
            {
                return null;
            }

            held.Add(kind);
            return new Lease(this, projectId, kind);
        }
    }

    public ProjectActivityKind? Current(string projectId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(projectId, out var held) && held.Count > 0 ? held[^1] : null;
        }
    }

    public bool IsBusy(string projectId) => Current(projectId) is not null;

    private void Leave(string projectId, ProjectActivityKind kind)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(projectId, out var held))
            {
                held.Remove(kind);
            }
        }
    }

    private sealed class Lease(FakeProjectActivity owner, string projectId, ProjectActivityKind kind) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Leave(projectId, kind);
            }
        }
    }
}

/// <summary>The template of agents of the project.</summary>
internal sealed class ScriptedDebateRoster(IEnumerable<AgentDefinition> agents) : IAgentRoster
{
    private readonly List<AgentDefinition> _agents = [.. agents];

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        _agents.RemoveAll(a => a.Id == agent.Id);
        _agents.Add(agent);
        return Task.CompletedTask;
    }
}

/// <summary>The person who answers the approvals: approves by default, as «usuario».</summary>
internal sealed class ScriptedApprovalPrompt : IApprovalPrompt
{
    private readonly Lock _gate = new();
    private readonly List<ApprovalRequest> _asked = [];

    public Func<ApprovalRequest, bool> Approve { get; set; } = _ => true;

    public IReadOnlyList<ApprovalRequest> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _asked.Add(request);
        }

        var approved = Approve(request);
        return Task.FromResult(new ApprovalDecision(approved, "usuario", DateTimeOffset.UnixEpoch, approved ? "adelante" : "no"));
    }
}

/// <summary>
/// The user's repository as the arena sees it: its HEAD (with or without uncommitted changes), the state each check captures
/// (a test changes it to see the debate stop) and the files of each commit, which <see cref="ArchiveCommitAsync"/> writes into
/// the copy.
/// </summary>
internal sealed class ScriptedDebateWorktrees : IWorktreeManager
{
    private readonly Lock _gate = new();

    public string HeadCommit { get; set; } = new('a', 40);

    public bool HasUncommittedChanges { get; set; }

    public Dictionary<string, Dictionary<string, string>> Commits { get; } = new(StringComparer.Ordinal);

    public UserRepositoryState State { get; set; } = Clean();

    public int Captures { get; private set; }

    public List<(string Commit, string Destination)> Archived { get; } = [];

    public static UserRepositoryState Clean() => new(new string('a', 40), "refs/heads/main", new Dictionary<string, string>(), new Dictionary<string, string>(), null, "index-1", []);

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) =>
        Task.FromResult(new RepositoryState(repoRoot, HeadCommit, "main", HasUncommittedChanges, new Version(2, 45)));

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Captures++;
            return Task.FromResult(State);
        }
    }

    public async Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Archived.Add((commit, destination));
        }

        foreach (var (path, content) in Commits.GetValueOrDefault(commit) ?? [])
        {
            var full = Path.Combine(destination, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, cancellationToken);
        }
    }

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A debate has no worktree.");

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) => throw new NotSupportedException("A debate commits nothing.");

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
}
