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
    public async Task A_subject_or_author_with_the_separators_of_the_record_does_not_hide_the_commit()
    {
        var before = _repo.Head;
        _repo.Write("s.txt", "s\n");
        _repo.Run("add", "-A");
        _repo.Run("-c", "user.name=Autor\u001fcon\u001eseparadores", "commit", "-q", "-m", "asunto\u001fcon\u001eseparadores y un\u001f\u001f\u001e\u001e final");
        var hidden = _repo.Head;
        var after = Commit("t.txt", "t\n", "el siguiente");

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal([after, hidden, before], commits.Select(commit => commit.Id).ToArray());
        Assert.Equal([before], commits[1].Parents);
        Assert.Contains("separadores", commits[1].Subject);
        Assert.Contains("separadores", commits[1].AuthorName);
        Assert.Equal("el siguiente", commits[0].Subject);
    }

    [Fact]
    public async Task The_trailers_of_a_commit_of_the_system_arrive_in_its_message_and_read_back()
    {
        var run = new AgentStudio.Domain.Orchestration.RunId("20261111-a1b2c3");
        var message = AgentStudio.Domain.Tracing.CommitTrailers.Append("T1: Tarea (intento 2)\n\nCuerpo del mensaje con varias líneas\ny un párrafo más.", run, new AgentStudio.Domain.Common.TaskId("T1"), 2, new AgentStudio.Domain.Common.AgentId("ejecutor"), "sonnet");
        var path = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "mensaje-del-sistema.txt");
        File.WriteAllText(path, message);
        _repo.Write("sistema.txt", "s\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-F", path);
        var system = _repo.Head;
        var plain = Commit("plano.txt", "p\n", "un commit de la persona");

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal(plain, commits[0].Id);
        Assert.Null(commits[0].Message);
        Assert.Equal(system, commits[1].Id);
        var trailers = AgentStudio.Domain.Tracing.CommitTrailers.Read(commits[1].Message);
        Assert.NotNull(trailers);
        Assert.Equal((run, new AgentStudio.Domain.Common.TaskId("T1"), 2, new AgentStudio.Domain.Common.AgentId("ejecutor"), "sonnet"), (trailers.Run, trailers.Task, trailers.Attempt, trailers.Agent, trailers.Model));
        Assert.StartsWith("T1: Tarea (intento 2)\n\n", commits[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cuerpo del mensaje", commits[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_of_megabytes_costs_nothing_and_its_trailers_at_the_end_still_arrive()
    {
        var run = new AgentStudio.Domain.Orchestration.RunId("20261111-a1b2c3");
        var body = string.Join('\n', Enumerable.Repeat(new string('z', 99), 50_000));
        var message = AgentStudio.Domain.Tracing.CommitTrailers.Append("T1: enorme\n\n" + body, run, new AgentStudio.Domain.Common.TaskId("T1"), 1, new AgentStudio.Domain.Common.AgentId("ejecutor"), null);
        var path = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "mensaje-de-megas.txt");
        File.WriteAllText(path, message);
        _repo.Write("m.txt", "m\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-F", path);
        var huge = _repo.Head;
        var after = Commit("n.txt", "n\n", "el siguiente");

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal([after, huge, _repo.BaseCommit], commits.Select(c => c.Id).ToArray());
        Assert.NotNull(AgentStudio.Domain.Tracing.CommitTrailers.Read(commits[1].Message));
        Assert.True(commits[1].Message!.Length < 1000);
        Assert.DoesNotContain("zzzzzzzz", commits[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_last_paragraph_that_is_not_a_block_of_trailers_gives_no_message_and_one_with_a_control_character_is_cleaned()
    {
        var path = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "mensaje-raro.txt");
        File.WriteAllText(path, "asunto\n\nAgent-Studio-Run: 20261111-a1b2c3\ny esto no es un trailer sino texto libre que sigue\n");
        _repo.Write("r.txt", "r\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-F", path);
        var odd = _repo.Head;
        File.WriteAllText(path, "otro\n\nAgent-Studio-Run: 20261111-a1b2c3\u001b[31m\nAgent-Studio-Task: T1\nAgent-Studio-Attempt: 1\nAgent-Studio-Agent: ejecutor\n");
        _repo.Write("c.txt", "c\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-F", path);

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal(odd, commits[1].Id);
        Assert.Null(AgentStudio.Domain.Tracing.CommitTrailers.Read(commits[1].Message));
        Assert.DoesNotContain(commits[0].Message ?? string.Empty, character => char.IsControl(character) && character != '\n');
    }

    [Fact]
    public async Task A_huge_subject_and_a_commit_with_many_refs_are_cut_in_git_and_lose_no_commit()
    {
        for (var index = 0; index < 25; index++)
        {
            _repo.Run("branch", "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), _repo.Head);
        }

        _repo.Write("h.txt", "h\n");
        _repo.Run("add", "-A");
        var message = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "mensaje-enorme.txt");
        File.WriteAllText(message, new string('y', 300_000));
        _repo.Run("commit", "-q", "-F", message);
        var after = _repo.Head;

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(), Ct);

        Assert.Equal(after, commits[0].Id);
        Assert.Equal(RepositoryHistoryReader.MaxSubjectCharacters, commits[0].Subject.Length);
        Assert.Equal(2, commits.Count);
        Assert.NotEmpty(commits[1].Refs);
        Assert.True(commits[1].Refs.Count <= RepositoryHistoryReader.MaxRefsPerCommit);
    }

    [Fact]
    public async Task A_tree_with_many_untracked_files_says_there_are_changes_without_reading_the_list()
    {
        for (var index = 0; index < 2000; index++)
        {
            _repo.Write("u/archivo-con-nombre-largo-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", "x");
        }

        var summary = await NewReader().SummaryAsync(Project, Ct);

        Assert.True(summary.Accepted);
        Assert.True(summary.HasUncommittedChanges);
        Assert.Empty(summary.Warnings);
    }

    [Theory]
    [InlineData("feature@2")]
    [InlineData("a+b")]
    [InlineData("issue#12")]
    [InlineData("rama-ñandú")]
    public async Task A_branch_name_git_accepts_with_at_plus_hash_or_unicode_is_read(string branch)
    {
        _repo.Run("checkout", "-q", "-b", branch);
        var onBranch = Commit("w.txt", "w\n", "en la rama");
        _repo.Run("checkout", "-q", "main");

        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: branch), Ct);

        Assert.Contains(commits, commit => commit.Id == onBranch);
    }

    [Theory]
    [InlineData("main\n")]
    [InlineData("a@{1}")]
    [InlineData("@")]
    [InlineData("a//b")]
    [InlineData("a/.b")]
    [InlineData("a.lock")]
    [InlineData("rama.")]
    [InlineData("a*b")]
    [InlineData("a\\b")]
    public async Task A_branch_name_git_refuses_or_that_ends_in_a_newline_reads_nothing(string branch)
    {
        var commits = await NewReader().CommitsAsync(Project, new CommitGraphQuery(Branch: branch), Ct);

        Assert.Empty(commits);
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
