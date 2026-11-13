using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>
/// The counts of the card of publication over the real reader of history (docs/21 D-08): the history of a branch <c>agentstudio/run-…</c> is read with git, not guessed. Real temporary
/// repositories, no network.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class PublicationHistoryLocalToolsTests : IDisposable
{
    private const string RunBranch = "agentstudio/run-20261113-a1b2c3";

    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new("0f8fad5b-d9cb-469f-a165-70867728950e", _repo.Root);

    public void Dispose() => _repo.Dispose();

    private RepositoryHistoryReader Reader() => new(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));

    private string Commit(string file, string message)
    {
        _repo.Write(file, message + "\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", message);
        return _repo.Head;
    }

    [Fact]
    public async Task TheBranchOfARunReturnsItsHistoryAndTheCountsAreMadeFromIt()
    {
        var baseHead = _repo.Head;
        _repo.Run("checkout", "-q", "-b", RunBranch);
        var own = Commit("own.txt", "a commit of the person");
        var system = Commit("system.txt", "a commit of the system");
        _repo.Run("checkout", "-q", "main");

        var graph = await Reader().CommitsAsync(Project, new CommitGraphQuery(500, Branch: RunBranch), Ct);

        // The history of the run branch: its two commits and the base they grew from.
        Assert.Equal([system, own, baseHead], graph.Select(c => c.Id));
        var annotated = graph.Select(c => new AnnotatedCommit(c, c.Id == system ? new CommitAnnotation("20261113-a1b2c3", null, null, null, null, [], []) : null)).ToList();

        var counts = CommitCounts.Of(graph, annotated, baseHead);

        Assert.Equal(1, counts.System);
        Assert.Equal(1, counts.OwnUnpublished);
    }

    [Fact]
    public async Task ACommitOfAnotherRunCountsAsOfAgentStudioAndNotAsOwn()
    {
        var baseHead = _repo.Head;
        _repo.Run("checkout", "-q", "-b", RunBranch);
        var other = Commit("other.txt", "a commit of another run");
        _repo.Run("checkout", "-q", "main");
        var graph = await Reader().CommitsAsync(Project, new CommitGraphQuery(500, Branch: RunBranch), Ct);
        var annotated = graph.Select(c => new AnnotatedCommit(c, c.Id == other ? new CommitAnnotation("20260101-ffffff", null, null, null, null, [], []) : null)).ToList();

        var counts = CommitCounts.Of(graph, annotated, baseHead);

        Assert.Equal(1, counts.System);
        Assert.Equal(0, counts.OwnUnpublished);
    }

    [Fact]
    public async Task ABaseHeadThatIsNotInTheHistoryReadMakesTheOwnCountUnknown()
    {
        _repo.Run("checkout", "-q", "-b", RunBranch);
        Commit("own.txt", "a commit");
        _repo.Run("checkout", "-q", "main");
        var graph = await Reader().CommitsAsync(Project, new CommitGraphQuery(500, Branch: RunBranch), Ct);
        var annotated = graph.Select(c => new AnnotatedCommit(c, null)).ToList();

        var counts = CommitCounts.Of(graph, annotated, new string('9', 40));

        Assert.Null(counts.OwnUnpublished);
    }

    [Fact]
    public async Task ABranchThatDoesNotExistReadsAsAnEmptyGraphWhichTheCardTreatsAsUnreadable()
    {
        var graph = await Reader().CommitsAsync(Project, new CommitGraphQuery(500, Branch: RunBranch), Ct);

        Assert.Empty(graph);
    }
}
