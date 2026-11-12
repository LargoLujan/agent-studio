using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Team;

namespace AgentStudio.Tests.Desktop.RepositoryScreen;

/// <summary>Opens any root as the project <c>p-1</c> and remembers what it opened.</summary>
internal sealed class FakeWorkspace : IProjectWorkspace
{
    public List<string> Opened { get; } = [];

    public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
    {
        Opened.Add(projectRoot);
        return Task.FromResult(new ProjectContext("p-1", projectRoot));
    }
}

/// <summary>The history of git as the test sets it. A read can be held until the test lets it go (<see cref="HoldSummary"/>).</summary>
internal sealed class FakeHistory : IRepositoryHistoryReader
{
    public RepositorySummary Summary { get; set; } = new(@"C:\proyectos\calc", true, "main", Sha('c'), false, []);

    public List<BranchSummary> Branches { get; } = [];

    public List<CommitNode> Commits { get; } = [];

    public List<CommitGraphQuery> Queries { get; } = [];

    /// <summary>When set, <see cref="SummaryAsync"/> waits for it (a slow read that a later one overtakes).</summary>
    public TaskCompletionSource? HoldSummary { get; set; }

    public int SummaryCalls { get; private set; }

    public async Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        SummaryCalls++;
        var answer = Summary;
        if (HoldSummary is { } hold)
        {
            HoldSummary = null;
            await hold.Task;
        }

        return answer with { Root = project.RootPath };
    }

    public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BranchSummary>>([.. Branches]);

    public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<CommitNode>>([.. Commits]);
    }

    public static string Sha(char digit) => new(digit, 40);
}

/// <summary>Annotates with what the test sets by commit id; a commit without an entry has no annotation. It can fail as an unverified log does.</summary>
internal sealed class FakeAnnotator : IRepositoryAnnotator
{
    public Dictionary<string, CommitAnnotation> Annotations { get; } = [];

    public bool BrokenLog { get; set; }

    public List<IReadOnlyList<CommitNode>> Asked { get; } = [];

    public Task<IReadOnlyList<AnnotatedCommit>> AnnotateAsync(ProjectContext project, IReadOnlyList<CommitNode> commits, CancellationToken cancellationToken)
    {
        Asked.Add(commits);
        if (BrokenLog)
        {
            throw new DecisionLogIntegrityException("the chain breaks at entry 12");
        }

        return Task.FromResult<IReadOnlyList<AnnotatedCommit>>([.. commits.Select(c => new AnnotatedCommit(c, Annotations.GetValueOrDefault(c.Id)))]);
    }
}

/// <summary>The timeline as the test sets it, recording the filters it was asked with.</summary>
internal sealed class FakeActivity : IRepositoryActivityReader
{
    public List<RepositoryActivity> Events { get; } = [];

    public List<RepositoryActivityFilter?> Filters { get; } = [];

    public bool BrokenLog { get; set; }

    public Task<IReadOnlyList<RepositoryActivity>> ListAsync(ProjectContext project, RepositoryActivityFilter? filter, CancellationToken cancellationToken)
    {
        Filters.Add(filter);
        if (BrokenLog)
        {
            throw new DecisionLogIntegrityException("the chain breaks at entry 12");
        }

        return Task.FromResult<IReadOnlyList<RepositoryActivity>>([.. Events]);
    }
}

/// <summary>The remotes as the test sets them.</summary>
internal sealed class FakeRemotes : IRepositoryRemoteReader
{
    public RemoteListing Listing { get; set; } = new([], null);

    public List<string> Roots { get; } = [];

    public Task<RemoteListing> ListRemotesAsync(string projectRoot, CancellationToken cancellationToken)
    {
        Roots.Add(projectRoot);
        return Task.FromResult(Listing);
    }

    public static RepositoryRemote Origin => new("origin", "https://example.test/calc.git");
}
