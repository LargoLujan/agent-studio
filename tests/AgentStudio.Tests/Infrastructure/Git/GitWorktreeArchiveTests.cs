using System.IO;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The copy of a commit an isolated verification runs on (ADR-0017), made by the hardened git layer: the objects of the
/// project's repository and nothing of its configuration, hooks or attributes, with only regular files and folders.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeArchiveTests : GitManagerTestBase
{
    /// <summary>A new, empty folder next to the repository (never inside it), deleted with it.</summary>
    private string NewDestination()
    {
        var path = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "copia-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    private string[] Tree(string commit) =>
        [.. Repo.Run("-c", "core.quotepath=false", "ls-tree", "-r", "--name-only", commit).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_commit_made_in_a_worktree_is_copied_exactly_from_the_objects_of_the_project_repository()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "src/Calc/Más.cs", "public static class Mas { }\n");

        // What the worktree holds but the commit does not never reaches the copy.
        WriteIn(lease, "sin-confirmar.txt", "no está en el commit\n");
        var before = Repo.Snapshot();
        var destination = NewDestination();

        await manager.ArchiveCommitAsync(Repo.Root, commit, destination, Ct);

        Assert.Equal(Tree(commit), Files(destination));
        Assert.Equal("public static class Mas { }\n", File.ReadAllText(Path.Combine(destination, "src", "Calc", "Más.cs")));
        Assert.False(Path.Exists(Path.Combine(destination, ".git")));
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task Git_archive_runs_with_an_explicit_private_git_dir_over_the_project_objects_no_hooks_and_the_end_of_options_before_the_commit()
    {
        var runner = new RecordingProcessRunner(new ProcessRunner());
        string? alternates = null;
        runner.AfterRun = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "archive")
            {
                // The private repository only lives while the command runs.
                var gitDirectory = spec.Arguments.Single(a => a.StartsWith("--git-dir=", StringComparison.Ordinal))["--git-dir=".Length..];
                alternates = File.ReadAllText(Path.Combine(gitDirectory, "objects", "info", "alternates")).Trim();
            }
        };
        var commit = Repo.Head;

        await NewManager(runner).ArchiveCommitAsync(Repo.Root, commit, NewDestination(), Ct);

        var archive = Assert.Single(runner.Specs, s => RecordingProcessRunner.SubCommand(s) == "archive");
        var arguments = archive.Arguments;
        var gitDir = arguments.Single(a => a.StartsWith("--git-dir=", StringComparison.Ordinal))["--git-dir=".Length..];
        Assert.False(Path.GetFullPath(gitDir).StartsWith(Path.GetFullPath(Repo.Root), StringComparison.OrdinalIgnoreCase), gitDir);
        Assert.True(SamePath(Path.Combine(Repo.Root, ".git", "objects"), alternates!), alternates);
        Assert.Contains("core.hooksPath=" + Path.Combine(Path.GetFullPath(Repo.WorktreeRoot), ".no-hooks"), arguments);
        Assert.Equal(["--end-of-options", commit], arguments.TakeLast(2));
        Assert.Null(archive.Environment!["GIT_DIR"]);
        Assert.Equal("0", archive.Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task The_attributes_of_the_commit_neither_drop_nor_rewrite_files_and_no_filter_of_the_repository_runs()
    {
        var marker = Path.Combine(Path.GetTempPath(), "as-filter-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        Repo.Run("config", "filter.evil.smudge", $"echo pwned > \"{marker.Replace('\\', '/')}\"; cat");
        Repo.Write(".gitattributes", "*.txt filter=evil\noculto.txt export-ignore\nsustituido.txt export-subst\n");
        Repo.Write("oculto.txt", "una prueba que el agente quería esconder\n");
        Repo.Write("sustituido.txt", "$Format:%H$\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "atributos");
        try
        {
            // Without the private repository git archive honours all three: the filter runs, oculto.txt is dropped, the
            // placeholder is expanded.
            var control = Path.Combine(NewDestination(), "normal.tar");
            Repo.Run("archive", "--format=tar", "-o", control, "HEAD");
            Assert.True(File.Exists(marker), "The repository filter did not run in the control archive, so the test proves nothing.");
            File.Delete(marker);
            var destination = NewDestination();

            await NewManager().ArchiveCommitAsync(Repo.Root, Repo.Head, destination, Ct);

            Assert.False(File.Exists(marker), "A filter driver of the repository ran while the commit was copied.");
            Assert.Equal("una prueba que el agente quería esconder\n", File.ReadAllText(Path.Combine(destination, "oculto.txt")));
            Assert.Equal("$Format:%H$\n", File.ReadAllText(Path.Combine(destination, "sustituido.txt")));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task The_end_of_line_settings_of_the_repository_apply_as_in_a_checkout()
    {
        Repo.Write("texto.txt", "uno\ndos\n");
        Repo.Run("add", "texto.txt");
        Repo.Run("commit", "-q", "-m", "texto");
        Repo.Run("config", "core.autocrlf", "true");
        var destination = NewDestination();

        await NewManager().ArchiveCommitAsync(Repo.Root, Repo.Head, destination, Ct);

        Assert.Equal("uno\r\ndos\r\n", File.ReadAllText(Path.Combine(destination, "texto.txt")));
    }

    [Fact]
    public async Task A_commit_with_a_symbolic_link_is_refused()
    {
        var target = Path.Combine(NewDestination(), "objetivo.txt");
        File.WriteAllText(target, Path.Combine(Repo.Root, "README.md"));
        var blob = Repo.Run("hash-object", "-w", target).Trim();
        Repo.Run("update-index", "--add", "--cacheinfo", "120000," + blob + ",enlace");
        Repo.Run("commit", "-q", "-m", "enlace");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => NewManager().ArchiveCommitAsync(Repo.Root, Repo.Head, NewDestination(), Ct));

        Assert.Contains("enlace", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("0123456789abcdef")]
    [InlineData("--output=fuera.tar")]
    [InlineData("zz23456789abcdef0123456789abcdef01234567")]
    public async Task A_revision_that_is_not_a_full_object_id_is_refused_before_git_runs(string commit)
    {
        var runner = new RecordingProcessRunner(new ProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => NewManager(runner).ArchiveCommitAsync(Repo.Root, commit, NewDestination(), Ct));

        Assert.Empty(runner.Specs);
    }

    [Fact]
    public async Task A_commit_the_repository_does_not_hold_is_refused_and_nothing_is_written()
    {
        var destination = NewDestination();

        await Assert.ThrowsAsync<GitCommandException>(() => NewManager().ArchiveCommitAsync(Repo.Root, "0123456789abcdef0123456789abcdef01234567", destination, Ct));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task A_worktree_is_never_taken_for_the_repository_whatever_its_gitfile_says()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "nuevo.txt", "x\n");

        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ArchiveCommitAsync(lease.Path, commit, NewDestination(), Ct));
    }

    [Fact]
    public async Task A_folder_outside_every_repository_is_refused()
    {
        var plain = NewDestination();

        await Assert.ThrowsAsync<GitCommandException>(() => NewManager().ArchiveCommitAsync(plain, Repo.Head, NewDestination(), Ct));
    }

    [Fact]
    public async Task The_copy_is_only_written_into_an_empty_folder_outside_the_repository()
    {
        var manager = NewManager();
        var inside = Path.Combine(Repo.Root, "copia");
        Directory.CreateDirectory(inside);
        var full = NewDestination();
        File.WriteAllText(Path.Combine(full, "previo.txt"), "ya estaba\n");

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ArchiveCommitAsync(Repo.Root, Repo.Head, inside, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ArchiveCommitAsync(Repo.Root, Repo.Head, full, Ct));

        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
        Assert.Equal(["previo.txt"], Files(full));
    }
}
