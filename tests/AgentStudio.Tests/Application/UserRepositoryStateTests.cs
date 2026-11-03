using AgentStudio.Application.Git;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Application;

public sealed class UserRepositoryStateTests
{
    private const string C1 = "1111111111111111111111111111111111111111";
    private const string C2 = "2222222222222222222222222222222222222222";
    private const string C3 = "3333333333333333333333333333333333333333";
    private const string Index1 = "aaaa";
    private const string Index2 = "bbbb";

    private static UserRepositoryState State(
        string head = C1,
        string? headRef = "refs/heads/main",
        IEnumerable<(string Name, string Id)>? branches = null,
        IEnumerable<(string Name, string Id)>? tags = null,
        string? stash = null,
        string index = Index1,
        IEnumerable<string>? status = null) =>
        new(
            head,
            headRef,
            (branches ?? [("refs/heads/main", C1)]).ToDictionary(b => b.Name, b => b.Id),
            (tags ?? []).ToDictionary(t => t.Name, t => t.Id),
            stash,
            index,
            [.. status ?? []]);

    private static IReadOnlyList<string> Compare(UserRepositoryState before, UserRepositoryState after) => UserRepositoryState.Compare(before, after);

    // ---- nothing changed ----

    [Fact]
    public void Two_captures_of_an_untouched_repository_have_no_differences()
    {
        var before = State(tags: [("refs/tags/v1", C1)], stash: C2, status: [" M src/a.cs", "?? notes.txt"]);
        var after = State(tags: [("refs/tags/v1", C1)], stash: C2, status: [" M src/a.cs", "?? notes.txt"]);

        Assert.Empty(Compare(before, after));
    }

    [Fact]
    public void The_order_in_which_refs_and_status_lines_were_listed_does_not_matter()
    {
        var before = State(branches: [("refs/heads/main", C1), ("refs/heads/dev", C2)], status: ["A  b.cs", " M a.cs"]);
        var after = State(branches: [("refs/heads/dev", C2), ("refs/heads/main", C1)], status: [" M a.cs", "A  b.cs"]);

        Assert.Empty(Compare(before, after));
    }

    // ---- HEAD ----

    [Fact]
    public void A_moved_HEAD_is_reported_with_both_commits()
    {
        var difference = Assert.Single(Compare(State(head: C1), State(head: C2)));

        Assert.StartsWith("HEAD", difference, StringComparison.Ordinal);
        Assert.Contains(C1, difference, StringComparison.Ordinal);
        Assert.Contains(C2, difference, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_branch_or_detaching_or_attaching_HEAD_is_reported()
    {
        var switched = Assert.Single(Compare(State(headRef: "refs/heads/main"), State(headRef: "refs/heads/dev")));
        var detached = Assert.Single(Compare(State(headRef: "refs/heads/main"), State(headRef: null)));
        var attached = Assert.Single(Compare(State(headRef: null), State(headRef: "refs/heads/main")));

        Assert.Contains("refs/heads/dev", switched, StringComparison.Ordinal);
        Assert.Contains("detached", detached, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("detached", attached, StringComparison.OrdinalIgnoreCase);
        Assert.All(new[] { switched, detached, attached }, d => Assert.StartsWith("HEAD", d, StringComparison.Ordinal));
    }

    // ---- branches and tags ----

    [Fact]
    public void A_created_a_deleted_and_a_moved_branch_are_each_reported_by_name()
    {
        var before = State(branches: [("refs/heads/main", C1), ("refs/heads/old", C1), ("refs/heads/moving", C1)]);
        var after = State(branches: [("refs/heads/main", C1), ("refs/heads/new", C2), ("refs/heads/moving", C2)]);

        var differences = Compare(before, after);

        Assert.Equal(3, differences.Count);
        Assert.Contains(differences, d => d.Contains("refs/heads/new", StringComparison.Ordinal) && d.Contains("created", StringComparison.Ordinal) && d.Contains(C2, StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("refs/heads/old", StringComparison.Ordinal) && d.Contains("deleted", StringComparison.Ordinal) && d.Contains(C1, StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("refs/heads/moving", StringComparison.Ordinal) && d.Contains("moved", StringComparison.Ordinal) && d.Contains(C1, StringComparison.Ordinal) && d.Contains(C2, StringComparison.Ordinal));
        Assert.All(differences, d => Assert.StartsWith("Branch", d, StringComparison.Ordinal));
    }

    [Fact]
    public void Tags_are_compared_like_branches()
    {
        var before = State(tags: [("refs/tags/v1", C1), ("refs/tags/gone", C1)]);
        var after = State(tags: [("refs/tags/v1", C2), ("refs/tags/new", C1)]);

        var differences = Compare(before, after);

        Assert.Equal(3, differences.Count);
        Assert.All(differences, d => Assert.StartsWith("Tag", d, StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("refs/tags/v1", StringComparison.Ordinal) && d.Contains("moved", StringComparison.Ordinal));
    }

    [Fact]
    public void Ref_names_are_case_sensitive()
    {
        var differences = Compare(State(branches: [("refs/heads/Main", C1)]), State(branches: [("refs/heads/main", C1)]));

        Assert.Equal(2, differences.Count);
    }

    // ---- stash, index, working tree ----

    [Theory]
    [InlineData(null, "2222222222222222222222222222222222222222")]
    [InlineData("1111111111111111111111111111111111111111", null)]
    [InlineData("1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222")]
    public void A_created_dropped_or_changed_stash_is_reported(string? before, string? after)
    {
        var difference = Assert.Single(Compare(State(stash: before), State(stash: after)));

        Assert.StartsWith("Stash", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_index_is_reported()
    {
        var difference = Assert.Single(Compare(State(index: Index1), State(index: Index2)));

        Assert.StartsWith("Index", difference, StringComparison.Ordinal);
        Assert.Contains(Index1, difference, StringComparison.Ordinal);
        Assert.Contains(Index2, difference, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_lines_that_appear_or_disappear_are_reported()
    {
        var before = State(status: [" M src/a.cs", "?? old.txt"]);
        var after = State(status: [" M src/a.cs", "?? new.txt"]);

        var differences = Compare(before, after);

        Assert.Equal(2, differences.Count);
        Assert.Contains(differences, d => d.Contains("removed", StringComparison.Ordinal) && d.Contains("?? old.txt", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("added", StringComparison.Ordinal) && d.Contains("?? new.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.Contains("src/a.cs", StringComparison.Ordinal));
    }

    // ---- several at once ----

    [Fact]
    public void Every_difference_is_listed_in_a_fixed_order()
    {
        var before = State(status: [" M a.cs", " M c.cs"]) with
        {
            Contents = Files(("c.cs", Exact("aaaa"))),
            GitFiles = Files((".git/config", Exact("cccc"))),
            HiddenTracked = Files(("h.cs", Exact("eeee"))),
        };
        var after = State(
            head: C2,
            headRef: null,
            branches: [("refs/heads/main", C2)],
            tags: [("refs/tags/t", C2)],
            stash: C3,
            index: Index2,
            status: ["?? b.cs", " M c.cs"]) with
        {
            Contents = Files(("c.cs", Exact("bbbb"))),
            GitFiles = Files((".git/config", Exact("dddd"))),
            HiddenTracked = Files(("h.cs", Exact("ffff"))),
        };

        var differences = Compare(before, after);

        Assert.Equal(["HEAD", "HEAD", "Branch", "Tag", "Stash", "Index", "Status", "Status", "Content", "Git", "Hidden"], differences.Select(d => d.Split(' ')[0]));
        Assert.Equal(differences, Compare(before, after));
    }

    // ---- contents of the listed paths, configuration and hooks ----

    private static FileFingerprint Exact(string sha256, string approximation = "size:4;mtime:1") => new(sha256, approximation);

    private static FileFingerprint Approximate(string approximation) => new(null, approximation);

    private static Dictionary<string, FileFingerprint> Files(params (string Path, FileFingerprint Fingerprint)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Fingerprint, StringComparer.Ordinal);

    [Fact]
    public void A_file_whose_content_changed_under_the_same_status_line_is_reported()
    {
        var before = State(status: [" M src/a.cs", "?? notas.txt"]) with { Contents = Files(("src/a.cs", Exact("aaaa")), ("notas.txt", Exact("1111"))) };
        var after = State(status: [" M src/a.cs", "?? notas.txt"]) with { Contents = Files(("src/a.cs", Exact("bbbb")), ("notas.txt", Exact("1111"))) };

        Assert.Equal(["Content of 'src/a.cs' changed."], Compare(before, after));
    }

    [Fact]
    public void Content_is_only_compared_for_the_status_lines_both_captures_kept()
    {
        // The engine leaves the lines under .agentstudio/ out of both captures: their content must stay out with them.
        var before = State(status: []) with { Contents = Files((".agentstudio/bitacora/2026-10-16.md", Exact("aaaa"))) };
        var after = State(status: []) with { Contents = Files((".agentstudio/bitacora/2026-10-16.md", Exact("bbbb"))) };

        Assert.Empty(Compare(before, after));
    }

    [Fact]
    public void A_path_whose_status_line_changed_is_reported_by_its_lines_and_not_again_by_its_content()
    {
        var before = State(status: [" M a.cs"]) with { Contents = Files(("a.cs", Exact("aaaa"))) };
        var after = State(status: ["MM a.cs"]) with { Contents = Files(("a.cs", Exact("bbbb"))) };

        Assert.Equal(["Status line removed:  M a.cs", "Status line added: MM a.cs"], Compare(before, after));
    }

    [Fact]
    public void The_renamed_path_of_a_rename_line_is_compared()
    {
        var before = State(status: ["R  viejo.cs -> nuevo.cs"]) with { Contents = Files(("nuevo.cs", Exact("aaaa"))) };
        var after = State(status: ["R  viejo.cs -> nuevo.cs"]) with { Contents = Files(("nuevo.cs", Exact("bbbb"))) };

        Assert.Equal(["Content of 'nuevo.cs' changed."], Compare(before, after));
    }

    [Fact]
    public void Hashes_decide_when_both_sides_have_one_and_otherwise_the_approximation_does_and_says_so()
    {
        var path = new[] { "?? f.bin" };

        // The same bytes touched later: no change.
        Assert.Empty(Compare(
            State(status: path) with { Contents = Files(("f.bin", Exact("aaaa", "size:4;mtime:1"))) },
            State(status: path) with { Contents = Files(("f.bin", Exact("aaaa", "size:4;mtime:2"))) }));

        // Too large (or unreadable) on one side: by size and time, which did not change.
        Assert.Empty(Compare(
            State(status: path) with { Contents = Files(("f.bin", Exact("aaaa", "size:4;mtime:1"))) },
            State(status: path) with { Contents = Files(("f.bin", Approximate("size:4;mtime:1"))) }));

        Assert.Equal(
            ["Content of 'f.bin' changed (approximate: by sizes and modification times, not by content)."],
            Compare(
                State(status: path) with { Contents = Files(("f.bin", Approximate("size:9;mtime:1"))) },
                State(status: path) with { Contents = Files(("f.bin", Approximate("size:10;mtime:2"))) }));
    }

    private static FileFingerprint Indeterminate(string reason, string approximation = "size:9;mtime:1") => new(null, approximation, reason);

    [Fact]
    public void A_path_neither_capture_verified_with_the_same_metadata_is_indeterminate_not_a_difference()
    {
        var path = new[] { "?? big.bin" };
        var before = State(status: path) with { Contents = Files(("big.bin", Indeterminate("larger than the per-file cap"))) };
        var after = State(status: path) with { Contents = Files(("big.bin", Indeterminate("larger than the per-file cap"))) };

        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        Assert.Empty(differences);
        var entry = Assert.Single(indeterminate);
        Assert.Equal("big.bin", entry.Path);
        Assert.False(entry.IsGitFile);
        Assert.Equal("larger than the per-file cap", entry.Reason);
        Assert.Contains("indeterminate", entry.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The metadata of a path its content was not verified for is still compared (R3-06): a file added to an ignored
    /// folder, or one resized or touched, changes it, and that is a difference whatever a caller decides about what is
    /// only indeterminate.
    /// </summary>
    [Fact]
    public void A_path_neither_capture_verified_whose_metadata_changed_is_a_difference()
    {
        var path = new[] { "!! cache/" };
        var before = State(status: path) with { Contents = Files(("cache/", Indeterminate("beyond the capture budget", "folder:2;digest:aaaa"))) };
        var after = State(status: path) with { Contents = Files(("cache/", Indeterminate("beyond the capture budget", "folder:3;digest:bbbb"))) };

        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        Assert.Equal(["Content of 'cache/' changed (approximate: by sizes and modification times, not by content)."], differences);
        Assert.Empty(indeterminate);
    }

    /// <summary>The same content is verified the same way, so a path one capture verified and the other did not changed (R3-06).</summary>
    [Fact]
    public void A_path_one_capture_verified_and_the_other_did_not_is_a_difference()
    {
        var path = new[] { "!! cache/" };
        var verified = State(status: path) with { Contents = Files(("cache/", Exact("aaaa", "folder:2;digest:aaaa"))) };
        var unverified = State(status: path) with { Contents = Files(("cache/", Indeterminate("beyond the capture budget", "folder:2;digest:aaaa"))) };

        foreach (var (before, after) in new[] { (verified, unverified), (unverified, verified) })
        {
            var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

            var difference = Assert.Single(differences);
            Assert.StartsWith("Content of 'cache/' changed: one capture verified its content and the other could not", difference, StringComparison.Ordinal);
            Assert.Empty(indeterminate);
        }
    }

    /// <summary>A path whose metadata could not be read either (a folder that cannot be listed, R3-03) can never be compared, so it always blocks.</summary>
    [Fact]
    public void A_path_without_metadata_in_either_capture_is_a_difference()
    {
        var path = new[] { "!! locked/" };
        var listed = State(status: path) with { Contents = Files(("locked/", Indeterminate("beyond the capture budget", "folder:2;digest:aaaa"))) };
        var unlisted = State(status: path) with { Contents = Files(("locked/", Indeterminate("the folder could not be listed", string.Empty))) };

        foreach (var (before, after) in new[] { (listed, unlisted), (unlisted, unlisted) })
        {
            var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

            var difference = Assert.Single(differences);
            Assert.StartsWith("Content of 'locked/' could not be checked", difference, StringComparison.Ordinal);
            Assert.Empty(indeterminate);
        }
    }

    [Fact]
    public void Compare_lists_plain_differences_first_and_indeterminate_entries_last_so_it_stays_non_empty_and_blocks()
    {
        var before = State(status: [" M a.cs", "?? big.bin"]) with { Contents = Files(("a.cs", Exact("aaaa")), ("big.bin", Indeterminate("unreadable"))) };
        var after = State(status: [" M a.cs", "?? big.bin"]) with { Contents = Files(("a.cs", Exact("bbbb")), ("big.bin", Indeterminate("unreadable"))) };

        var flat = UserRepositoryState.Compare(before, after);

        Assert.Equal(2, flat.Count);
        Assert.StartsWith("Content of 'a.cs' changed", flat[0], StringComparison.Ordinal);
        Assert.Contains("indeterminate", flat[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_git_file_that_became_indeterminate_is_a_difference_and_one_indeterminate_in_both_is_marked_as_a_git_file()
    {
        var verified = State() with { GitFiles = Files((".git/config", Exact("c1"))) };
        var unverified = State() with { GitFiles = Files((".git/config", Indeterminate("larger than the per-file cap"))) };

        var (became, none) = UserRepositoryState.CompareInDetail(verified, unverified);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(unverified, unverified);

        Assert.StartsWith("Git file '.git/config' changed: one capture verified", Assert.Single(became), StringComparison.Ordinal);
        Assert.Empty(none);
        Assert.Empty(differences);
        var entry = Assert.Single(indeterminate);
        Assert.Equal(".git/config", entry.Path);
        Assert.True(entry.IsGitFile);
        Assert.StartsWith("Git file '.git/config' is indeterminate", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_files_that_appear_disappear_or_change_are_reported_by_name()
    {
        var before = State() with { GitFiles = Files((".git/config", Exact("c1")), (".git/hooks/pre-commit", Exact("h1")), (".git/hooks/viejo", Exact("v1"))) };
        var after = State() with { GitFiles = Files((".git/config", Exact("c2")), (".git/hooks/pre-commit", Exact("h1")), (".git/hooks/post-merge", Exact("p1"))) };

        Assert.Equal(
            ["Git file '.git/config' changed.", "Git file '.git/hooks/post-merge' was created.", "Git file '.git/hooks/viejo' was deleted."],
            Compare(before, after));
    }

    /// <summary>
    /// R3-04: the tracked paths the status cannot show (files flagged assume-unchanged or skip-worktree, submodules) are
    /// compared like git files, whatever the status says: by name, appeared, disappeared or changed. One comes and goes
    /// with its flag in the index too, which the message names.
    /// </summary>
    [Fact]
    public void Hidden_tracked_paths_that_appear_disappear_or_change_are_reported_by_name()
    {
        var before = State() with { HiddenTracked = Files(("a.txt", Exact("a1")), ("sub/", Exact("s1", "folder:2 entries;metadata:m1")), ("viejo.txt", Exact("v1"))) };
        var after = State() with { HiddenTracked = Files(("a.txt", Exact("a2")), ("sub/", Exact("s1", "folder:2 entries;metadata:m2")), ("nuevo.txt", Exact("n1"))) };

        Assert.Equal(
            [
                "Hidden tracked path 'a.txt' changed.",
                "Hidden tracked path 'nuevo.txt' appeared: it was created, or the status no longer shows it.",
                "Hidden tracked path 'viejo.txt' disappeared: it was deleted, or the status shows it again.",
            ],
            Compare(before, after));
    }

    /// <summary>
    /// R3-04: like a git file, a hidden tracked path one capture verified and the other did not changed, and one neither
    /// verified with the same metadata is indeterminate. It is not a git file.
    /// </summary>
    [Fact]
    public void A_hidden_tracked_path_that_could_not_be_verified_is_compared_by_its_metadata()
    {
        var verified = State() with { HiddenTracked = Files(("sub/", Exact("s1", "folder:2 entries;metadata:m1"))) };
        var unverified = State() with { HiddenTracked = Files(("sub/", Indeterminate("the capture budget ran out inside this folder", "folder:2 entries;metadata:m1"))) };

        var (became, none) = UserRepositoryState.CompareInDetail(verified, unverified);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(unverified, unverified);

        Assert.StartsWith("Hidden tracked path 'sub/' changed: one capture verified", Assert.Single(became), StringComparison.Ordinal);
        Assert.Empty(none);
        Assert.Empty(differences);
        var entry = Assert.Single(indeterminate);
        Assert.Equal("sub/", entry.Path);
        Assert.False(entry.IsGitFile);
        Assert.Equal("the capture budget ran out inside this folder", entry.Reason);
    }

    [Fact]
    public void A_capture_that_did_not_take_contents_or_git_files_has_nothing_to_compare_there()
    {
        var full = State(status: [" M a.cs"]) with
        {
            Contents = Files(("a.cs", Exact("aaaa"))),
            GitFiles = Files((".git/config", Exact("c1"))),
            HiddenTracked = Files(("h.cs", Exact("h1"))),
        };

        Assert.Empty(Compare(State(status: [" M a.cs"]), full));
        Assert.Empty(Compare(full, State(status: [" M a.cs"])));
    }

    [Fact]
    public void Swapping_the_states_swaps_created_with_deleted_and_nothing_else()
    {
        var a = State(branches: [("refs/heads/main", C1), ("refs/heads/x", C2)], status: ["?? a"]);
        var b = State(branches: [("refs/heads/main", C1)], status: ["?? b"]);

        var forward = Compare(a, b);
        var backward = Compare(b, a);

        Assert.Equal(forward.Count, backward.Count);
        Assert.Contains(forward, d => d.Contains("deleted", StringComparison.Ordinal));
        Assert.Contains(backward, d => d.Contains("created", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_of_a_random_state_has_no_differences_and_every_kind_of_change_is_detected()
    {
        var random = new Random(26101510);
        for (var i = 0; i < 300; i++)
        {
            var branches = Enumerable.Range(0, random.Next(1, 5)).Select(n => ($"refs/heads/b{n}", random.Next(2) == 0 ? C1 : C2)).ToList();
            var status = Enumerable.Range(0, random.Next(0, 4)).Select(n => random.Next(2) == 0 ? $" M f{n}.cs" : $"?? f{n}.cs").ToList();
            var stash = random.Next(2) == 0 ? C3 : null;
            var state = State(branches: branches, status: status, stash: stash);
            var copy = State(branches: branches.AsEnumerable().Reverse(), status: status.AsEnumerable().Reverse(), stash: stash);

            Assert.Empty(Compare(state, copy));

            var mutated = random.Next(4) switch
            {
                0 => State(branches: [.. branches.Skip(1)], status: status, stash: stash),
                1 => State(branches: [.. branches, ("refs/heads/extra", C3)], status: status, stash: stash),
                2 => State(branches: branches, status: [.. status, "?? extra.txt"], stash: stash),
                _ => State(branches: branches, status: status, stash: stash is null ? C3 : null),
            };

            Assert.NotEmpty(Compare(state, mutated));
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => UserRepositoryState.Compare(null!, State()));
        Assert.Throws<ArgumentNullException>(() => UserRepositoryState.Compare(State(), null!));
    }

    // ---- the lease carries what the git commands need ----

    [Fact]
    public void A_lease_names_the_repository_and_the_administrative_directory_of_its_worktree()
    {
        var lease = new WorktreeLease(
            @"C:\wt\abcd1234\a1b2c3\T1",
            "T1",
            "refs/agentstudio/20261015-a1b2c3/T1",
            C1,
            new RunId("20261015-a1b2c3"),
            @"C:\proyecto",
            @"C:\proyecto\.git\worktrees\T1");

        Assert.Equal(@"C:\proyecto", lease.RepositoryRoot);
        Assert.Equal(@"C:\proyecto\.git\worktrees\T1", lease.AdminDirectory);
    }
}
