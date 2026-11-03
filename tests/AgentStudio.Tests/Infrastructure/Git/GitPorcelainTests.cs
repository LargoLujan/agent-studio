using AgentStudio.Infrastructure.Git;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Pure parsers of git's machine-readable output. The samples are real outputs captured with git 2.45.2
/// (docs/investigacion/2026-10-15-spikes-fase-3.md), so they need no git to run.
/// </summary>
public sealed class GitPorcelainTests
{
    private const string Tree = "8ff46eac06e50ef535303dcbec4141eaf140058b";

    [Theory]
    [InlineData("git version 2.45.2.windows.1", 2, 45, 2)]
    [InlineData("git version 2.38.0\n", 2, 38, 0)]
    [InlineData("git version 2.50", 2, 50, 0)]
    [InlineData("git version 2.51.0.rc1", 2, 51, 0)]
    public void ParseVersion_reads_the_first_three_numbers(string output, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), GitPorcelain.ParseVersion(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not git")]
    [InlineData("git version")]
    public void ParseVersion_rejects_unrecognised_output(string output)
    {
        Assert.Throws<FormatException>(() => GitPorcelain.ParseVersion(output));
    }

    [Fact]
    public void ParseWorktreeList_reads_the_main_tree_and_a_locked_detached_worktree()
    {
        const string output =
            "worktree C:/repo\0HEAD 57c427ac7dc83bf1e674ff765445ad90382d7deb\0branch refs/heads/main\0\0" +
            "worktree C:/wt/abcd1234/a1b2c3/T1\0HEAD 57c427ac7dc83bf1e674ff765445ad90382d7deb\0detached\0" +
            "locked agent-studio:11111111-2222-3333-4444-555555555555:20261015-a1b2c3:T1\0\0";

        var entries = GitPorcelain.ParseWorktreeList(output);

        Assert.Equal(2, entries.Count);
        Assert.Equal("C:/repo", entries[0].Path);
        Assert.Equal("refs/heads/main", entries[0].Branch);
        Assert.False(entries[0].Locked);
        Assert.False(entries[0].Detached);
        Assert.Equal("C:/wt/abcd1234/a1b2c3/T1", entries[1].Path);
        Assert.True(entries[1].Detached);
        Assert.True(entries[1].Locked);
        Assert.Equal("agent-studio:11111111-2222-3333-4444-555555555555:20261015-a1b2c3:T1", entries[1].LockReason);
        Assert.Equal("57c427ac7dc83bf1e674ff765445ad90382d7deb", entries[1].Head);
        Assert.Null(entries[1].Branch);
    }

    [Fact]
    public void ParseWorktreeList_keeps_a_lock_reason_with_quotes_and_newlines_raw()
    {
        // The line based format quotes it ("a\"b\nc"); -z does not.
        const string output = "worktree C:/repo\0HEAD 57c4\0branch refs/heads/main\0\0worktree C:/wt/T5\0HEAD 57c4\0detached\0locked a\"b\nc\0\0";

        var entries = GitPorcelain.ParseWorktreeList(output);

        Assert.Equal("a\"b\nc", entries[1].LockReason);
    }

    [Fact]
    public void ParseWorktreeList_handles_locked_without_reason_prunable_bare_and_empty_output()
    {
        const string output =
            "worktree C:/bare\0bare\0\0" +
            "worktree C:/wt/A\0HEAD 57c4\0detached\0locked\0\0" +
            "worktree C:/wt/B\0HEAD 57c4\0detached\0prunable gitdir file points to non-existent location\0\0";

        var entries = GitPorcelain.ParseWorktreeList(output);

        Assert.True(entries[0].Bare);
        Assert.True(entries[1].Locked);
        Assert.Null(entries[1].LockReason);
        Assert.True(entries[2].Prunable);
        Assert.False(entries[2].Locked);
        Assert.Empty(GitPorcelain.ParseWorktreeList(string.Empty));
    }

    [Fact]
    public void TryParseMergeTree_reads_a_clean_merge_with_messages()
    {
        const string output = Tree + "\0\0" + "1\0shared.txt\0Auto-merging\0Auto-merging shared.txt\n\0";

        Assert.True(GitPorcelain.TryParseMergeTree(output, out var result));

        Assert.NotNull(result);
        Assert.Equal(Tree, result.TreeId);
        Assert.Empty(result.ConflictedPaths);
        Assert.Equal("Auto-merging shared.txt", result.Messages);
    }

    [Fact]
    public void TryParseMergeTree_reads_conflicted_paths_and_messages()
    {
        const string output =
            "69ce9a35be521deff07b102f256f41af3fc12d2d\0shared.txt\0docs/guía de uso.md\0\0" +
            "1\0shared.txt\0Auto-merging\0Auto-merging shared.txt\n\0" +
            "1\0shared.txt\0CONFLICT (contents)\0CONFLICT (content): Merge conflict in shared.txt\n\0";

        Assert.True(GitPorcelain.TryParseMergeTree(output, out var result));

        Assert.NotNull(result);
        Assert.Equal("69ce9a35be521deff07b102f256f41af3fc12d2d", result.TreeId);
        Assert.Equal(["shared.txt", "docs/guía de uso.md"], result.ConflictedPaths);
        Assert.Equal("Auto-merging shared.txt\nCONFLICT (content): Merge conflict in shared.txt", result.Messages);
    }

    [Fact]
    public void TryParseMergeTree_lists_a_path_once_and_accepts_a_tree_without_messages()
    {
        Assert.True(GitPorcelain.TryParseMergeTree(Tree + "\0a.txt\0a.txt\0\0", out var withDuplicate));
        Assert.Equal(["a.txt"], withDuplicate!.ConflictedPaths);

        Assert.True(GitPorcelain.TryParseMergeTree(Tree + "\0", out var bare));
        Assert.Equal(Tree, bare!.TreeId);
        Assert.Empty(bare.ConflictedPaths);
        Assert.Equal(string.Empty, bare.Messages);
    }

    [Fact]
    public void TryParseMergeTree_keeps_unstructured_messages_as_free_text()
    {
        const string output = Tree + "\0\0Auto-merging shared.txt\nCONFLICT (content): Merge conflict in shared.txt\n\0";

        Assert.True(GitPorcelain.TryParseMergeTree(output, out var result));

        Assert.Equal("Auto-merging shared.txt\nCONFLICT (content): Merge conflict in shared.txt", result!.Messages);
    }

    [Fact]
    public void ParseStatusLines_turns_the_z_format_into_unquoted_porcelain_lines()
    {
        // A rename lists the new path and then the old one; paths with spaces, accents or quotes are never quoted with -z.
        const string output = " M README.md\0R  docs/nuevo nombre.md\0docs/guía de uso.md\0?? \"comillas\".txt\0A  src/a.cs\0";

        var lines = GitPorcelain.ParseStatusLines(output);

        Assert.Equal([" M README.md", "R  docs/guía de uso.md -> docs/nuevo nombre.md", "?? \"comillas\".txt", "A  src/a.cs"], lines);
        Assert.Empty(GitPorcelain.ParseStatusLines(string.Empty));
    }

    [Fact]
    public void ParseStatusPaths_gives_every_path_of_the_z_format_both_sides_of_a_rename_and_ignored_folders_with_their_slash()
    {
        // From status --porcelain=v1 -z --untracked-files=all --ignored=matching: an ignored folder ends with '/'.
        const string output = " M README.md\0R  docs/nuevo nombre.md\0docs/guía de uso.md\0?? nueva/a b.txt\0!! bin/\0!! a.log\0";

        var paths = GitPorcelain.ParseStatusPaths(output);

        Assert.Equal(["README.md", "docs/nuevo nombre.md", "docs/guía de uso.md", "nueva/a b.txt", "bin/", "a.log"], paths);
        Assert.Empty(GitPorcelain.ParseStatusPaths(string.Empty));
    }

    [Fact]
    public void ParseScopedConfigValues_reads_scope_and_value_pairs_in_the_order_git_gives_them()
    {
        // config -z --show-scope --get-all: lowest precedence first; Agent Studio's own -c override is the "command" scope.
        const string output = "global\0C:/Users/u/hooks\0local\0tools/hooks\0command\0C:/AgentStudio/wt/.no-hooks\0";

        var values = GitPorcelain.ParseScopedConfigValues(output);

        Assert.Equal([("global", "C:/Users/u/hooks"), ("local", "tools/hooks"), ("command", "C:/AgentStudio/wt/.no-hooks")], values);
        Assert.Empty(GitPorcelain.ParseScopedConfigValues(string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("merge-tree: no-existe - not something we can merge")]
    [InlineData("fatal: Not a valid object name no-existe")]
    [InlineData("8ff46eac")]
    [InlineData("8FF46EAC06E50EF535303DCBEC4141EAF140058B")]
    public void TryParseMergeTree_rejects_output_that_does_not_start_with_a_tree_id(string output)
    {
        // git exits with 1 both for conflicts and for "not something we can merge": only a tree id makes it a merge result.
        Assert.False(GitPorcelain.TryParseMergeTree(output, out var result));
        Assert.Null(result);
    }
}
