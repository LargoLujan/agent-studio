using System.IO;
using AgentStudio.Infrastructure.Git;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>System commits, changed paths and diffs.</summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeCommitTests : GitManagerTestBase
{
    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    // ---- CommitAll -------------------------------------------------------------------------------------------

    [Fact]
    public async Task CommitAll_commits_as_Agent_Studio_with_the_message_taken_from_stdin()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "hola.txt", "hola\n");

        // Quotes, shell metacharacters, accents and a trailer: none of it may be interpreted by anything.
        const string message = "Añade \"hola\"; echo $PATH | type %TEMP% & > x\n\nCuerpo con acentos: integración válida.\nAgent-Studio-Attempt: T1/1\n";

        var result = await manager.CommitAllAsync(lease, message, Ct);

        Assert.True(result.Committed);
        Assert.Equal(Repo.Run("-C", lease.Path, "rev-parse", "HEAD").Trim(), result.Commit);
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", result.Commit + "^").Trim());
        Assert.Equal(message.TrimEnd(), Repo.Run("log", "-1", "--format=%B", result.Commit).TrimEnd());
        Assert.Equal(
            "Agent Studio|agent-studio@localhost|Agent Studio|agent-studio@localhost",
            Repo.Run("log", "-1", "--format=%an|%ae|%cn|%ce", result.Commit).Trim());
        Assert.Equal("hola.txt", Repo.Run("diff-tree", "--no-commit-id", "--name-only", "-r", result.Commit).Trim());

        // The hidden ref follows the commit.
        Assert.Equal(result.Commit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task CommitAll_does_not_run_hooks_and_does_not_sign()
    {
        // Every hook the layer's commands could trigger. --no-verify only skips pre-commit and commit-msg; the others
        // (and a user hook can do anything, network included) must not run either. The three that can abort a commit
        // fail; the rest only leave a trace.
        var hooks = Path.Combine(Repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hook in new[] { "pre-commit", "commit-msg", "prepare-commit-msg" })
        {
            File.WriteAllText(Path.Combine(hooks, hook), HookScripts.Failing(hook));
        }

        foreach (var hook in new[] { "post-commit", "post-checkout", "post-index-change", "reference-transaction" })
        {
            File.WriteAllText(Path.Combine(hooks, hook), HookScripts.Logging(hook));
        }

        var log = Path.Combine(Repo.Root, ".git", HookScripts.LogName);

        // A signer configured but never invoked: the layer forces commit.gpgsign=false, so gpg.program is not run. It
        // points at git-lfs, a real program outside the repository (so the callback allowlist accepts it, R3-01) that
        // would fail if it were ever run as gpg.
        Repo.Run("config", "commit.gpgsign", "true");
        Repo.Run("config", "gpg.program", "git-lfs");

        // Control: the hooks are live for plain git commands in exactly this setup, so the test below proves something.
        Repo.Run("update-ref", "refs/agentstudio/control", Repo.BaseCommit);
        Assert.True(File.Exists(log), "Control: the reference-transaction hook did not run.");
        File.Delete(log);

        var manager = NewManager();
        var integration = await EnsureAsync(manager, "_integration");
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "hola.txt", "hola\n");

        Repo.Run("-C", lease.Path, "add", "-A");
        var control = TempGitRepo.TryGit(lease.Path, "commit", "-q", "-m", "control");
        Assert.NotEqual(0, control.ExitCode);
        Assert.True(File.Exists(log), "Control: a plain commit did not run the hooks.");
        File.Delete(log);

        var result = await manager.CommitAllAsync(lease, "mensaje", Ct);
        var merge = await manager.MergeIntoAsync(Repo.Root, integration.RefName, Repo.BaseCommit, result.Commit, "Integra T1", Ct);
        await manager.CheckoutAsync(integration, merge.ResultCommit!, Ct);
        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);
        await manager.RemoveAsync(lease, discardChanges: true, Ct);

        Assert.True(result.Committed);
        Assert.Equal("N", Repo.Run("log", "-1", "--format=%G?", result.Commit).Trim());
        Assert.False(File.Exists(log), "Hooks ran: " + (File.Exists(log) ? File.ReadAllText(log) : string.Empty));
    }

    [Fact]
    public async Task Commits_ignore_author_and_committer_identities_from_the_configuration()
    {
        Repo.Run("config", "author.name", "Autor Configurado");
        Repo.Run("config", "author.email", "autor@example.invalid");
        Repo.Run("config", "committer.name", "Confirmador Configurado");
        Repo.Run("config", "committer.email", "confirmador@example.invalid");
        var manager = NewManager();
        var integration = await EnsureAsync(manager, "_integration");
        var lease = await EnsureAsync(manager, "T1");

        var commit = await CommitInAsync(manager, lease, "hola.txt", "hola\n");
        var merge = await manager.MergeIntoAsync(Repo.Root, integration.RefName, Repo.BaseCommit, commit, "Integra T1", Ct);

        foreach (var id in new[] { commit, merge.ResultCommit! })
        {
            Assert.Equal(
                "Agent Studio|agent-studio@localhost|Agent Studio|agent-studio@localhost",
                Repo.Run("log", "-1", "--format=%an|%ae|%cn|%ce", id).Trim());
        }
    }

    [Fact]
    public async Task CommitAll_skips_when_there_is_nothing_to_commit()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        var untouched = await manager.CommitAllAsync(lease, "nada que confirmar", Ct);

        Assert.False(untouched.Committed);
        Assert.Equal(Repo.BaseCommit, untouched.Commit);

        var commit = await CommitInAsync(manager, lease, "a.txt", "a\n");
        var again = await manager.CommitAllAsync(lease, "otra vez", Ct);

        Assert.False(again.Committed);
        Assert.Equal(commit, again.Commit);
        Assert.Equal("2", Repo.Run("rev-list", "--count", commit).Trim());
        Assert.Equal(commit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task CommitAll_never_commits_the_agents_directory()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, ".agents/skills/skill.md", "habilidad aprovisionada");
        WriteIn(lease, "real.txt", "trabajo real\n");

        var result = await manager.CommitAllAsync(lease, "con habilidades", Ct);

        Assert.True(result.Committed);
        var tree = Repo.Run("ls-tree", "-r", "--name-only", result.Commit);
        Assert.Contains("real.txt", tree, StringComparison.Ordinal);
        Assert.DoesNotContain(".agents", tree, StringComparison.Ordinal);

        // Only skills in the worktree: there is nothing to commit.
        var onlySkills = await EnsureAsync(manager, "T2");
        WriteIn(onlySkills, ".agents/skills/skill.md", "habilidad aprovisionada");

        var skipped = await manager.CommitAllAsync(onlySkills, "solo habilidades", Ct);

        Assert.False(skipped.Committed);
        Assert.Equal(Repo.BaseCommit, skipped.Commit);
        Assert.True(File.Exists(Path.Combine(onlySkills.Path, ".agents", "skills", "skill.md")));
    }

    [Fact]
    public async Task CommitAll_records_deletions_and_keeps_the_commit_alive_through_its_ref()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        File.Delete(Path.Combine(lease.Path, "shared.txt"));

        var result = await manager.CommitAllAsync(lease, "borra shared", Ct);

        Assert.True(result.Committed);
        Assert.DoesNotContain("shared.txt", Repo.Run("ls-tree", "-r", "--name-only", result.Commit), StringComparison.Ordinal);

        await manager.RemoveAsync(lease, discardChanges: false, Ct);
        Assert.Equal(result.Commit, Repo.Run("rev-parse", "--verify", lease.RefName + "^{commit}").Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("con\0nulo")]
    public async Task CommitAll_rejects_empty_messages_and_messages_with_NUL(string message)
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "hola.txt", "hola\n");

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CommitAllAsync(lease, message, Ct));

        Assert.Equal(Repo.BaseCommit, Repo.Run("-C", lease.Path, "rev-parse", "HEAD").Trim());
    }

    // ---- ChangedPaths ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangedPaths_lists_added_modified_and_deleted_paths_sorted_and_unquoted()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "README.md", "cambiado\n");
        File.Delete(Path.Combine(lease.Path, "shared.txt"));
        WriteIn(lease, "src/nuevo é.txt", "x\n");
        var commit = (await manager.CommitAllAsync(lease, "cambios", Ct)).Commit;

        var paths = await manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, commit, Ct);

        Assert.Equal(["README.md", "shared.txt", "src/nuevo é.txt"], paths);
    }

    [Fact]
    public async Task ChangedPaths_reports_a_rename_as_a_delete_plus_an_add()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        Directory.CreateDirectory(Path.Combine(lease.Path, "docs"));
        File.Move(Path.Combine(lease.Path, "README.md"), Path.Combine(lease.Path, "docs", "README.md"));
        var commit = (await manager.CommitAllAsync(lease, "mueve el README", Ct)).Commit;

        var paths = await manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, commit, Ct);

        Assert.Equal(["README.md", "docs/README.md"], paths);
    }

    [Fact]
    public async Task ChangedPaths_is_empty_between_a_commit_and_itself()
    {
        var paths = await NewManager().ChangedPathsAsync(Repo.Root, Repo.BaseCommit, Repo.BaseCommit, Ct);

        Assert.Empty(paths);
    }

    [Theory]
    [InlineData("--output=evil.txt")]
    [InlineData("-p")]
    [InlineData("a..b")]
    [InlineData("HEAD@{1}")]
    [InlineData("")]
    public async Task ChangedPaths_rejects_arguments_that_could_be_read_as_options_or_ranges(string revision)
    {
        var manager = NewManager();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ChangedPathsAsync(Repo.Root, revision, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, revision, Ct));
        Assert.False(File.Exists(Path.Combine(Repo.Root, "evil.txt")));
    }

    // ---- WriteDiff -------------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteDiff_writes_the_patch_byte_for_byte()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "hola.txt", "hola\n");

        // Text that is not valid UTF-8 ("café" in Latin-1) and a binary file: both must reach the stream untouched.
        File.WriteAllBytes(Path.Combine(lease.Path, "latin1.txt"), [0x63, 0x61, 0x66, 0xE9, 0x0A]);
        File.WriteAllBytes(Path.Combine(lease.Path, "blob.bin"), [0x00, 0xFF, 0xFE, 0x80, 0x01]);
        var commit = (await manager.CommitAllAsync(lease, "añade ficheros", Ct)).Commit;

        using var buffer = new MemoryStream();
        await manager.WriteDiffAsync(Repo.Root, Repo.BaseCommit, commit, buffer, Ct);
        var patch = buffer.ToArray();

        Assert.True(Contains(patch, "diff --git a/hola.txt b/hola.txt"u8));
        Assert.True(Contains(patch, "+hola\n"u8));
        Assert.True(Contains(patch, new byte[] { (byte)'+', 0x63, 0x61, 0x66, 0xE9, 0x0A }), "Latin-1 bytes were altered.");
        Assert.True(Contains(patch, "GIT binary patch"u8));
    }

    [Fact]
    public async Task WriteDiff_does_not_depend_on_the_diff_configuration_of_the_user()
    {
        Repo.Run("config", "diff.noprefix", "true");
        Repo.Run("config", "diff.mnemonicPrefix", "true");
        // A real external diff tool outside the repository (git-lfs), which the allowlist accepts (R3-01) but which the
        // layer's --no-ext-diff never invokes: if it did, the output would not be a plain git diff.
        Repo.Run("config", "diff.external", "git-lfs");
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "nuevo.txt", "x\n");

        using var buffer = new MemoryStream();
        await manager.WriteDiffAsync(Repo.Root, Repo.BaseCommit, commit, buffer, Ct);

        Assert.True(Contains(buffer.ToArray(), "diff --git a/nuevo.txt b/nuevo.txt"u8));
    }

    [Fact]
    public async Task WriteDiff_is_empty_between_a_commit_and_itself()
    {
        using var buffer = new MemoryStream();

        await NewManager().WriteDiffAsync(Repo.Root, Repo.BaseCommit, Repo.BaseCommit, buffer, Ct);

        Assert.Equal(0, buffer.Length);
    }
}
