using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>The history reader of «Repositorio y cambios» (docs/16 WP4) over real temporary repositories, with no network.</summary>
[Trait("Category", "LocalTools")]
public sealed class RepositoryHistoryReaderTests : IDisposable
{
    private const string ProjectId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new(ProjectId, _repo.Root);

    private RepositoryHistoryReader NewReader() =>
        new(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));

    public void Dispose() => _repo.Dispose();

    private string Commit(string file, string content, string message)
    {
        _repo.Write(file, content);
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", message);
        return _repo.Head;
    }

    [Fact]
    public async Task The_summary_gives_the_head_and_whether_there_are_uncommitted_changes()
    {
        var reader = NewReader();

        var clean = await reader.SummaryAsync(Project, Ct);

        Assert.True(clean.Accepted);
        Assert.Equal("main", clean.HeadBranch);
        Assert.Equal(_repo.Head, clean.HeadCommit);
        Assert.False(clean.HasUncommittedChanges);
        Assert.Empty(clean.Warnings);

        _repo.Write("nuevo.txt", "sin confirmar\n");
        var dirty = await reader.SummaryAsync(Project, Ct);

        Assert.True(dirty.HasUncommittedChanges);
    }

    [Fact]
    public async Task The_summary_of_a_detached_head_has_no_branch()
    {
        _repo.Run("checkout", "-q", "--detach");

        var summary = await NewReader().SummaryAsync(Project, Ct);

        Assert.True(summary.Accepted);
        Assert.Null(summary.HeadBranch);
        Assert.Equal(_repo.Head, summary.HeadCommit);
    }

    [Fact]
    public async Task The_graph_is_newest_first_with_parents_subject_author_and_refs()
    {
        var first = _repo.BaseCommit;
        var second = Commit("a.txt", "a\n", "segundo cambio");
        var third = Commit("b.txt", "b\n", "tercer cambio");

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal([third, second, first], commits.Select(commit => commit.Id).ToArray());
        Assert.Equal([second], commits[0].Parents);
        Assert.Empty(commits[2].Parents);
        Assert.Equal("tercer cambio", commits[0].Subject);
        Assert.Equal("Agent Studio Tests", commits[0].AuthorName);
        Assert.Contains(commits[0].Refs, reference => reference.EndsWith("main", StringComparison.Ordinal));
        Assert.True(commits[0].At >= commits[2].At);
    }

    [Fact]
    public async Task The_graph_is_bounded_by_the_commit_limit_and_the_hard_cap()
    {
        Commit("a.txt", "a\n", "dos");
        Commit("b.txt", "b\n", "tres");

        var two = await NewReader().CommitsAsync(Project, new CommitGraphQuery(MaxCommits: 2), Ct);
        var none = await NewReader().CommitsAsync(Project, new CommitGraphQuery(MaxCommits: -5), Ct);

        Assert.Equal(2, two.Count);
        Assert.Single(none);
        Assert.Equal(1000, RepositoryHistoryReader.MaxCommitsLimit);
    }

    [Fact]
    public async Task The_graph_can_be_read_for_one_branch_and_includes_the_other_branches_by_default()
    {
        var onMain = _repo.Head;
        _repo.Run("checkout", "-q", "-b", "trabajo");
        var onWork = Commit("w.txt", "w\n", "solo en trabajo");
        _repo.Run("checkout", "-q", "main");

        var all = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);
        var main = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: "main"), Ct);
        var work = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: "trabajo"), Ct);

        Assert.Contains(all, commit => commit.Id == onWork);
        Assert.Equal([onMain], main.Select(commit => commit.Id).ToArray());
        Assert.Contains(work, commit => commit.Id == onWork);
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("main..trabajo")]
    [InlineData("-n1")]
    [InlineData("a b")]
    [InlineData("main^")]
    public async Task A_branch_that_is_not_a_plain_branch_name_reads_nothing(string branch)
    {
        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: branch), Ct);

        Assert.Empty(commits);
    }

    [Fact]
    public async Task A_branch_that_does_not_exist_reads_nothing()
    {
        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: "no-existe"), Ct);

        Assert.Empty(commits);
    }

    [Fact]
    public async Task The_period_filters_the_graph()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1);

        var after = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Since: future), Ct);
        var before = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Until: DateTimeOffset.UtcNow.AddDays(1)), Ct);

        Assert.Empty(after);
        Assert.Single(before);
    }

    [Fact]
    public async Task A_long_subject_is_cut_and_a_control_character_in_a_text_of_git_is_cleaned()
    {
        var subject = new string('x', 500);
        _repo.Write("l.txt", "l\n");
        _repo.Run("add", "-A");
        _repo.Run("-c", "user.name=Mala\u001b[31mPersona", "commit", "-q", "-m", subject);

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(MaxCommits: 1), Ct);

        Assert.Equal(RepositoryHistoryReader.MaxSubjectCharacters, commits[0].Subject.Length);
        Assert.DoesNotContain(commits[0].AuthorName, character => char.IsControl(character));
    }

    [Fact]
    public async Task The_branches_are_the_local_ones_with_the_current_one_and_the_run_ones_marked()
    {
        _repo.Run("branch", "agentstudio/run-1/tarea");
        _repo.Run("branch", "otra");
        // A remote-tracking branch is not listed: the contract has no kind for it until Phase 10.
        _repo.Run("update-ref", "refs/remotes/origin/main", _repo.Head);

        var branches = await NewReader().BranchesAsync(Project, Ct);

        Assert.Equal(["agentstudio/run-1/tarea", "main", "otra"], branches.Select(branch => branch.Name).ToArray());
        Assert.Equal("main", Assert.Single(branches, branch => branch.IsCurrent).Name);
        Assert.Equal(BranchKind.Run, branches[0].Kind);
        Assert.Equal(BranchKind.Local, branches[1].Kind);
        Assert.All(branches, branch => Assert.Equal(_repo.Head, branch.Commit));
    }

    [Fact]
    public async Task The_remote_tracking_branch_shows_as_a_ref_of_its_commit()
    {
        _repo.Run("update-ref", "refs/remotes/origin/main", _repo.Head);

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Contains("origin/main", commits[0].Refs);
    }

    [Fact]
    public async Task Reading_changes_nothing_in_the_repository()
    {
        Commit("a.txt", "a\n", "dos");
        _repo.Write("sucio.txt", "x\n");
        var before = _repo.Snapshot();
        var reader = NewReader();

        await reader.SummaryAsync(Project, Ct);
        await reader.BranchesAsync(Project, Ct);
        await reader.CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_refused_with_a_code()
    {
        var folder = Path.Combine(Path.GetTempPath(), "as-nohist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var project = new ProjectContext(ProjectId, folder);
            var reader = NewReader();

            var summary = await reader.SummaryAsync(project, Ct);

            Assert.False(summary.Accepted);
            Assert.Null(summary.HeadCommit);
            Assert.Equal(["not-a-repository"], summary.Warnings);
            Assert.Empty(await reader.BranchesAsync(project, Ct));
            Assert.Empty(await reader.CommitsAsync(project, new CommitGraphQuery(), Ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_refused()
    {
        var project = new ProjectContext(ProjectId, Path.Combine(_repo.WorktreeRoot, "no-existe"));

        var summary = await NewReader().SummaryAsync(project, Ct);

        Assert.False(summary.Accepted);
        Assert.Single(summary.Warnings);
    }

    [Fact]
    public async Task A_configuration_that_runs_a_program_of_the_repository_is_refused_and_reads_nothing()
    {
        _repo.Write(".gitattributes", "*.fmt filter=format\n");
        _repo.Write("tools/filter.sh", "#!/bin/sh\ncat\n");
        _repo.Write("doc.fmt", "contenido\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", "filtro del repositorio");
        _repo.Run("config", "filter.format.clean", "sh tools/filter.sh");
        _repo.Run("config", "filter.format.smudge", "sh tools/filter.sh");
        var reader = NewReader();

        var summary = await reader.SummaryAsync(Project, Ct);

        Assert.False(summary.Accepted);
        Assert.Equal(["unsafe-configuration"], summary.Warnings);
        Assert.Null(summary.HeadBranch);
        Assert.Empty(await reader.BranchesAsync(Project, Ct));
        Assert.Empty(await reader.CommitsAsync(Project, new CommitGraphQuery(), Ct));
    }

    [Fact]
    public async Task A_repository_inside_the_worktree_root_is_refused()
    {
        var reader = new RepositoryHistoryReader(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.Root));

        var summary = await reader.SummaryAsync(Project, Ct);

        Assert.False(summary.Accepted);
        Assert.Equal(["inside-worktree-root"], summary.Warnings);
    }

    [Fact]
    public async Task Git_that_cannot_run_reads_as_a_refusal_not_as_an_exception()
    {
        var options = new GitWorktreeOptions(_repo.WorktreeRoot, GitExecutable: Path.Combine(_repo.WorktreeRoot, "no-git.exe"));
        var reader = new RepositoryHistoryReader(new ProcessRunner(), new ExecutableLocator(), options);

        var summary = await reader.SummaryAsync(Project, Ct);

        Assert.False(summary.Accepted);
        Assert.Empty(await reader.CommitsAsync(Project, new CommitGraphQuery(), Ct));
    }

    [Fact]
    public async Task A_cancelled_read_propagates_the_cancellation()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewReader().CommitsAsync(Project, new CommitGraphQuery(), source.Token));
    }

    [Fact]
    public void The_git_module_registers_the_history_reader()
    {
        using var provider = new ServiceCollection().AddAgentStudioGit(new GitWorktreeOptions(_repo.WorktreeRoot)).BuildServiceProvider();

        var reader = provider.GetRequiredService<IRepositoryHistoryReader>();

        Assert.IsType<RepositoryHistoryReader>(reader);
        Assert.Same(reader, provider.GetRequiredService<IRepositoryHistoryReader>());
    }
}
