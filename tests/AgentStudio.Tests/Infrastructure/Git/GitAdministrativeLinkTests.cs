using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// H-03: git writes the administrative folder of a worktree, a ref and its reflog through a junction planted under the
/// common directory, and <c>worktree remove</c> deletes through one. Measured with git 2.45.2 (ADR-0012): a junction at
/// <c>worktrees</c> receives the new administrative folder; one in a hidden ref's folder receives the ref; one in
/// <c>logs/refs/heads/agentstudio</c> receives the run branch's reflog and loses a file to <c>update-ref -d</c>; and an
/// administrative folder turned into a junction gets a commit's HEAD and index, and is emptied by
/// <c>worktree remove --force --force</c>. Every such write is refused BEFORE git runs, so each test checks the victim
/// folder and the commands that ran, not only the exception: the old code threw too, after git had written.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitAdministrativeLinkTests : GitManagerTestBase
{
    private string CommonDirectory => Path.Combine(Repo.Root, ".git");

    private string RunBranch => "refs/heads/agentstudio/run-" + TestRun.Value;

    private static bool Ran(IEnumerable<AgentStudio.Application.Execution.ProcessSpec> specs, string subCommand, string? argument = null) =>
        specs.Any(s => RecordingProcessRunner.SubCommand(s) == subCommand && (argument is null || s.Arguments.Contains(argument)));

    private string ChildOfBase() =>
        Repo.Run("commit-tree", "-p", Repo.BaseCommit, "-m", "hijo", Repo.BaseCommit + "^{tree}").Trim();

    // ---- worktree add -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_junction_in_place_of_the_worktrees_folder_is_refused_before_git_writes_through_it()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var victim = NewVictim("otra-administracion");
        var junction = Path.Combine(CommonDirectory, "worktrees");
        CreateJunction(junction, victim);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.False(Ran(recorder.Specs, "worktree", "add"), "git worktree add ran before the link check, so it wrote through the junction.");
            Assert.False(Ran(recorder.Specs, "update-ref"), "a ref was written for a worktree that was refused.");
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    /// <summary>
    /// git would skip a junction at the exact folder it creates (its <c>mkdir</c> fails and it takes <c>T11</c>, measured
    /// with git 2.45.2), but the junction is planted and the ownership check reads through every folder there, so the
    /// worktree is refused before <c>worktree add</c>.
    /// </summary>
    [Fact]
    public async Task A_junction_at_the_administrative_folder_git_would_create_is_refused_before_worktree_add()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var victim = NewVictim("otra-administracion");
        Directory.CreateDirectory(Path.Combine(CommonDirectory, "worktrees"));
        var junction = Path.Combine(CommonDirectory, "worktrees", "T1");
        CreateJunction(junction, victim);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.False(Ran(recorder.Specs, "worktree", "add"), "git worktree add ran although a junction sits where it creates the administrative folder.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.Empty(ListWorktrees().Skip(1));
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    // ---- hidden refs --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_junction_in_the_hidden_refs_of_the_run_is_refused_before_the_worktree_or_a_ref_is_written()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var victim = NewVictim("otras-refs");
        Directory.CreateDirectory(Path.Combine(CommonDirectory, "refs", "agentstudio"));
        var junction = Path.Combine(CommonDirectory, "refs", "agentstudio", TestRun.Value);
        CreateJunction(junction, victim);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.False(Ran(recorder.Specs, "update-ref"), "a hidden ref was written through the junction.");
            Assert.False(Ran(recorder.Specs, "worktree", "add"), "the worktree was created although its refs cannot be written safely.");
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    [Fact]
    public async Task A_junction_planted_in_the_hidden_refs_after_creation_is_refused_before_a_commit_moves_the_ref_through_it()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var refsOfRun = Path.Combine(CommonDirectory, "refs", "agentstudio", TestRun.Value);
        var victim = NewVictim("otras-refs");

        // The refs of the run move to the victim and a junction takes their place: they still resolve, through the link.
        ReplaceWithJunction(refsOfRun, victim);
        try
        {
            var start = recorder.Specs.Count;
            WriteIn(lease, "nuevo.txt", "x\n");

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(Repo.BaseCommit, File.ReadAllText(Path.Combine(victim, "T1")).Trim());
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "update-ref"), "the commit moved the hidden ref through the junction.");
            Assert.False(Ran(after, "add") || Ran(after, "commit"), "the commit started although its ref cannot be moved safely.");
        }
        finally
        {
            RemoveJunction(refsOfRun);
        }
    }

    [Fact]
    public async Task The_older_ref_writers_refuse_a_ref_reached_through_a_junction()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var child = ChildOfBase();
        var victim = NewVictim("otras-refs");

        // The run branch exists, but its folder is a junction: a merge would move it, and creating another branch would
        // write a new file, both in the victim.
        File.WriteAllText(Path.Combine(victim, "run-" + TestRun.Value), Repo.BaseCommit + "\n");
        var junction = Path.Combine(CommonDirectory, "refs", "heads", "agentstudio");
        CreateJunction(junction, victim);
        try
        {
            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.MergeIntoAsync(Repo.Root, RunBranch, Repo.BaseCommit, child, "Integra", Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRefAsync(Repo.Root, "refs/heads/agentstudio/run-20261015-ffffff", Repo.BaseCommit, Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(["run-" + TestRun.Value], Directory.EnumerateFileSystemEntries(victim).Select(Path.GetFileName));
            Assert.Equal(Repo.BaseCommit + "\n", File.ReadAllText(Path.Combine(victim, "run-" + TestRun.Value)));
            Assert.False(Ran(recorder.Specs, "update-ref"), "a ref was written through the junction.");
            Assert.False(Ran(recorder.Specs, "commit-tree"), "the merge started although its target cannot be moved safely.");
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    // ---- reflogs ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_junction_in_the_reflog_folder_of_the_run_branches_is_refused_before_git_writes_or_deletes_through_it()
    {
        // git keeps a reflog for every branch (core.logAllRefUpdates defaults to true outside a bare repository).
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var child = ChildOfBase();
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var logs = Path.Combine(CommonDirectory, "logs", "refs", "heads", "agentstudio");
        var reflog = "run-" + TestRun.Value;
        Assert.True(File.Exists(Path.Combine(logs, reflog)), "git did not write the run branch's reflog; the measurement this test relies on changed.");
        var victim = NewVictim("otros-logs");
        ReplaceWithJunction(logs, victim);
        var before = File.ReadAllText(Path.Combine(victim, reflog));
        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, child, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeleteRefAsync(Repo.Root, RunBranch, Repo.BaseCommit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRunBranchAsync(Repo.Root, new AgentStudio.Domain.Orchestration.RunId("20261015-ffffff"), Repo.BaseCommit, Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllText(Path.Combine(victim, reflog)));
            Assert.Equal([reflog], Directory.EnumerateFileSystemEntries(victim).Select(Path.GetFileName));
            Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", RunBranch).Trim());
            Assert.False(Ran(recorder.Specs.Skip(start), "update-ref"), "a ref whose reflog is reached through a junction was written.");
        }
        finally
        {
            RemoveJunction(logs);
        }
    }

    [Fact]
    public async Task With_every_ref_logged_a_junction_in_the_reflog_folder_of_the_hidden_refs_is_refused_before_anything_is_written()
    {
        // The repository's configuration can make git keep a reflog for the hidden refs too (measured with git 2.45.2).
        Repo.Run("config", "core.logAllRefUpdates", "always");
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var victim = NewVictim("otros-logs");
        Directory.CreateDirectory(Path.Combine(CommonDirectory, "logs", "refs", "agentstudio"));
        var junction = Path.Combine(CommonDirectory, "logs", "refs", "agentstudio", TestRun.Value);
        CreateJunction(junction, victim);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.False(Ran(recorder.Specs, "update-ref"), "a hidden ref was written with its reflog reached through a junction.");
            Assert.False(Ran(recorder.Specs, "worktree", "add"));
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    /// <summary>
    /// git appends to a reflog in place, so through a hard link (no privilege needed) it appends to the other file too
    /// (measured with git 2.45.2). A reflog with more than one name is refused before the ref moves.
    /// </summary>
    [Fact]
    public async Task A_hard_link_at_the_reflog_of_the_run_branch_is_refused_before_git_appends_through_it()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var child = ChildOfBase();
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var reflog = Path.Combine(CommonDirectory, "logs", "refs", "heads", "agentstudio", "run-" + TestRun.Value);
        var victim = Path.Combine(NewVictim("otro-repositorio"), "precioso.txt");
        File.WriteAllText(victim, "precioso\n");
        File.Delete(reflog);
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        links.HardLink(reflog, victim);
        var start = recorder.Specs.Count;

        var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, child, Ct));

        Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("precioso\n", File.ReadAllText(victim));
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", RunBranch).Trim());
        Assert.False(Ran(recorder.Specs.Skip(start), "update-ref"), "the run branch moved with its reflog hard-linked to another file.");
    }

    // ---- the administrative folder of an existing worktree ---------------------------------------------------------

    [Fact]
    public async Task A_junction_in_place_of_a_worktrees_administrative_folder_is_refused_and_nothing_is_written_or_deleted_through_it()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var admin = lease.AdminDirectory;
        var victim = NewVictim("otra-administracion");
        ReplaceWithJunction(admin, victim);
        File.WriteAllText(Path.Combine(victim, "precioso.txt"), "del usuario\n");
        try
        {
            var start = recorder.Specs.Count;
            WriteIn(lease, "nuevo.txt", "x\n");

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: cambios", Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CheckoutAsync(lease, Repo.BaseCommit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1")),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.RemoveAsync(lease, discardChanges: true, Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(victim, "precioso.txt")));
            Assert.Equal(Repo.BaseCommit, File.ReadAllText(Path.Combine(victim, "HEAD")).Trim());
            Assert.True(Directory.Exists(lease.Path), "the worktree was moved aside although its registration cannot be dropped safely.");
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "worktree", "remove"), "git worktree remove ran on an administrative folder reached through a junction.");
            Assert.False(Ran(after, "add") || Ran(after, "commit") || Ran(after, "reset") || Ran(after, "update-ref"));
        }
        finally
        {
            RemoveJunction(admin);
        }
    }

    // ---- X-02: the files inside the administrative folder of an owned worktree ------------------------------------
    // Measured with git 2.45.2, in a worktree's administrative folder: `add` replaces index by a rename; `commit` writes
    // COMMIT_EDITMSG in place, replaces HEAD and index by a rename and appends to logs/HEAD; `reset --hard` replaces HEAD,
    // index and ORIG_HEAD by a rename and appends to logs/HEAD; `clean` and `status` write nothing there. Through a hard
    // link (no privilege), commit overwrote the other file of COMMIT_EDITMSG and appended to that of logs/HEAD, and reset to
    // that of logs/HEAD; through a junction at logs, both appended outside; and `worktree remove --force --force` emptied
    // the folder a junction at logs points to. Every such file and folder is checked before the command that writes it.

    public static TheoryData<string, string> FilesEachCommandWrites() => new()
    {
        { "commit", "COMMIT_EDITMSG" },
        { "commit", "logs/HEAD" },
        { "commit", "HEAD" },
        { "commit", "index" },
        { "reset", "HEAD" },
        { "reset", "index" },
        { "reset", "ORIG_HEAD" },
        { "reset", "logs/HEAD" },
    };

    private static Task RunAsync(GitWorktreeManager manager, WorktreeLease lease, string command, string baseCommit) =>
        command == "commit" ? manager.CommitAllAsync(lease, "T1: cambios", Ct) : manager.CheckoutAsync(lease, baseCommit, Ct);

    [Theory]
    [MemberData(nameof(FilesEachCommandWrites))]
    public async Task A_hard_link_at_a_file_a_command_writes_in_the_administrative_folder_is_refused_before_the_command_runs(string command, string file)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "previo.txt", "previo\n");
        WriteIn(lease, "nuevo.txt", "x\n");

        // A second name for a file outside the repository, made with mklink /H (no privilege). It holds what git had there,
        // so git works as usual and only where it writes shows.
        var planted = Path.Combine(lease.AdminDirectory, file.Replace('/', Path.DirectorySeparatorChar));
        var victim = Path.Combine(NewVictim("otro-repositorio"), "precioso.bin");
        File.WriteAllBytes(victim, File.Exists(planted) ? File.ReadAllBytes(planted) : "precioso\n"u8.ToArray());
        var before = File.ReadAllBytes(victim);
        File.Delete(planted);
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        links.HardLink(planted, victim);
        var start = recorder.Specs.Count;

        var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => RunAsync(manager, lease, command, Repo.BaseCommit));

        Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(victim));
        var after = recorder.Specs.Skip(start).ToList();
        Assert.False(Ran(after, "add") || Ran(after, "commit") || Ran(after, "reset"), $"git wrote in the administrative folder with {file} hard-linked to a file outside the repository.");
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("reset")]
    public async Task A_junction_in_place_of_the_logs_folder_of_an_owned_worktree_is_refused_before_git_appends_through_it(string command)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "previo.txt", "previo\n");
        WriteIn(lease, "nuevo.txt", "x\n");
        var logs = Path.Combine(lease.AdminDirectory, "logs");
        var victim = NewVictim("otros-logs");
        ReplaceWithJunction(logs, victim);
        var before = File.ReadAllText(Path.Combine(victim, "HEAD"));
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => RunAsync(manager, lease, command, Repo.BaseCommit));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(Path.Combine(victim, "HEAD")));
            Assert.Equal(["HEAD"], Directory.EnumerateFileSystemEntries(victim).Select(Path.GetFileName));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "add") || Ran(after, "commit") || Ran(after, "reset"), "git ran with the logs folder of the worktree reached through a junction.");
        }
        finally
        {
            RemoveJunction(logs);
        }
    }

    [Fact]
    public async Task A_junction_inside_the_administrative_folder_is_refused_before_the_worktree_is_removed_and_nothing_behind_it_is_deleted()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var logs = Path.Combine(lease.AdminDirectory, "logs");
        var victim = NewVictim("otros-logs");
        ReplaceWithJunction(logs, victim);
        File.WriteAllText(Path.Combine(victim, "precioso.txt"), "del usuario\n");
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.RemoveAsync(lease, discardChanges: true, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(victim, "precioso.txt")));
            Assert.True(Directory.Exists(lease.Path), "the worktree was moved aside although its registration cannot be dropped safely.");
            Assert.False(Ran(recorder.Specs.Skip(start), "worktree", "remove"), "git worktree remove ran with a junction inside the administrative folder, and it deletes through one.");
        }
        finally
        {
            RemoveJunction(logs);
        }
    }

    [Fact]
    public async Task A_junction_inside_the_administrative_folder_of_a_vanished_worktree_is_refused_before_its_registration_is_dropped()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var logs = Path.Combine(lease.AdminDirectory, "logs");
        var victim = NewVictim("otros-logs");
        ReplaceWithJunction(logs, victim);
        File.WriteAllText(Path.Combine(victim, "precioso.txt"), "del usuario\n");

        // The worktree's folder is gone (a crash, a manual delete): EnsureAsync drops a registration like that.
        DeleteDirectory(lease.Path);
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(victim, "precioso.txt")));
            Assert.False(Ran(recorder.Specs.Skip(start), "worktree", "remove"), "git worktree remove ran with a junction inside the administrative folder.");
        }
        finally
        {
            RemoveJunction(logs);
        }
    }

    // ---- P-02: the object store and the ref storage ------------------------------------------------------------------
    // Measured with git 2.45.2: `add` wrote the loose object of a new file through a junction at objects/<xx> (the fan-out
    // folder of its id) and through one at objects itself; with core.bigFileThreshold=1, which the repository can set, it
    // streamed the file into a pack written through a junction at objects/pack. objects/info received nothing from `add`,
    // but it is checked too. One listing of objects/ checks every fan-out folder (0.3 ms with all 256 of them).

    private static string Listing(string folder) =>
        string.Join(",", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal));

    public static TheoryData<string, bool> ObjectStoreFolders() => new()
    {
        { "objects", false },
        { "objects/pack", true },
        { "objects/info", false },
        { "fan-out", false },
    };

    [Theory]
    [MemberData(nameof(ObjectStoreFolders))]
    public async Task A_junction_in_the_object_store_is_refused_before_a_commit_writes_objects_through_it(string where, bool bigFileThreshold)
    {
        if (bigFileThreshold)
        {
            Repo.Run("config", "core.bigFileThreshold", "1");
        }

        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "nuevo.txt", "contenido del agente " + Guid.NewGuid().ToString("N") + "\n");
        var blob = Repo.Run("hash-object", Path.Combine(lease.Path, "nuevo.txt")).Trim();
        var folder = where == "fan-out"
            ? Path.Combine(CommonDirectory, "objects", blob[..2])
            : Path.Combine(CommonDirectory, where.Replace('/', Path.DirectorySeparatorChar));
        var victim = NewVictim("otros-objetos");
        if (Directory.Exists(folder))
        {
            ReplaceWithJunction(folder, victim);
        }
        else
        {
            CreateJunction(folder, victim);
        }

        var before = Listing(victim);
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, Listing(victim));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "add") || Ran(after, "commit"), $"git wrote objects with {where} reached through a junction.");
        }
        finally
        {
            RemoveJunction(folder);
        }
    }

    [Fact]
    public async Task A_junction_in_the_object_store_is_refused_before_a_merge_writes_a_tree_or_a_commit()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var t1 = await EnsureAsync(manager, "T1");
        var t2 = await EnsureAsync(manager, "T2");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        var c2 = await CommitInAsync(manager, t2, "dos.txt", "2\n");
        var task = new TaskId("I1");
        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, task, Repo.BaseCommit, Ct);
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);

        // Any fan-out folder: the ids of the merged tree and commit are not known before git writes them.
        var folder = Path.Combine(CommonDirectory, "objects", "ff");
        var victim = NewVictim("otros-objetos");
        if (Directory.Exists(folder))
        {
            ReplaceWithJunction(folder, victim);
        }
        else
        {
            CreateJunction(folder, victim);
        }

        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, task, candidate, c1, "Integra T1", Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.MergeIntoAsync(Repo.Root, RunBranch, Repo.BaseCommit, c2, "Integra T2", Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "merge-tree") || Ran(after, "commit-tree"), "a merge wrote objects with a fan-out folder reached through a junction.");
            Assert.Equal(candidate, Repo.Run("rev-parse", $"refs/agentstudio/{TestRun.Value}/_candidate/I1").Trim());
        }
        finally
        {
            RemoveJunction(folder);
        }
    }

    /// <summary>
    /// With the reftable backend (git 2.45 and later) the refs live in <c>reftable/</c>, not in loose files, and HEAD only
    /// reads <c>ref: refs/heads/.invalid</c> (measured with git 2.45.2: <c>worktree add</c> and <c>update-ref</c> worked,
    /// and no ref file appeared). Every check of a ref, a reflog or a HEAD file assumes the files backend, so such a
    /// repository is refused before anything runs on it.
    /// </summary>
    [Fact]
    public async Task A_repository_whose_refs_are_kept_in_reftable_is_refused_before_anything_is_written()
    {
        var root = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "reftable");
        Directory.CreateDirectory(root);
        TempGitRepo.Git(root, "init", "-q", "-b", "main", "--ref-format=reftable");
        TempGitRepo.Git(root, "config", "user.name", "Agent Studio Tests");
        TempGitRepo.Git(root, "config", "user.email", "tests@localhost");
        File.WriteAllText(Path.Combine(root, "README.md"), "# reftable\n");
        TempGitRepo.Git(root, "add", "-A");
        TempGitRepo.Git(root, "commit", "-q", "-m", "base");
        var head = TempGitRepo.Git(root, "rev-parse", "HEAD").Trim();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);

        var refusals = new InvalidOperationException[]
        {
            await Assert.ThrowsAsync<UnsupportedRepositoryException>(() => manager.InspectAsync(root, Ct)),
            await Assert.ThrowsAsync<UnsupportedRepositoryException>(() => manager.EnsureAsync(new WorktreeRequest(new ProjectContext(ProjectId, root), TestRun, "T1", head), Ct)),
            await Assert.ThrowsAsync<UnsupportedRepositoryException>(() => manager.EnsureRunBranchAsync(root, TestRun, head, Ct)),
        };

        Assert.All(refusals, refusal => Assert.Contains("reftable", refusal.Message, StringComparison.Ordinal));
        Assert.False(Ran(recorder.Specs, "worktree", "add"), "a worktree was added to a repository whose refs cannot be checked.");
        Assert.False(Ran(recorder.Specs, "update-ref"), "a ref was written in a repository whose refs cannot be checked.");
        Assert.Equal(string.Empty, TempGitRepo.Git(root, "for-each-ref", "--format=%(refname)", "refs/agentstudio/", "refs/heads/agentstudio/").Trim());
    }

    // ---- G-01: the default storage of git-lfs -------------------------------------------------------------------------
    // Measured with git-lfs 3.5.1 (git 2.45.2), offline: the clean filter of `git add` writes the object into
    // <common>/lfs/objects/<aa>/<bb>/<oid> through <common>/lfs/tmp, also from a worktree with an explicit --git-dir; a smudge
    // whose object is missing writes lfs/logs/<time>.log and creates the fan-out folders. A junction at lfs, at
    // lfs/objects, at a first-level fan-out folder (objects/<aa>) or at a second-level one (objects/<aa>/<bb>) received the
    // object, outside the repository. `git status` runs the clean filter too, on a same-size change whose stat is dirty, and
    // git-lfs stored the new object. So every content command checks the storage before git runs.

    private static bool GitLfsAvailable => new ExecutableLocator().Find("git-lfs") is not null;

    private string Lfs => Path.Combine(CommonDirectory, "lfs");

    /// <summary>git-lfs installed in the repository and <c>*.dat</c> tracked by it; the base commit has no lfs file, so creating a worktree runs no filter.</summary>
    private string CommitLfsTracking()
    {
        Repo.Run("lfs", "install", "--local");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "seguimiento lfs");
        return Repo.Head;
    }

    /// <summary>Plants a junction at <paramref name="folder"/> to <paramref name="victim"/>, moving what was there.</summary>
    private static void PlantJunction(string folder, string victim)
    {
        if (Directory.Exists(folder))
        {
            ReplaceWithJunction(folder, victim);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            CreateJunction(folder, victim);
        }
    }

    [Fact]
    public async Task Junctions_at_the_git_lfs_objects_and_tmp_folders_are_refused_before_git_add_runs_the_lfs_filter()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var head = CommitLfsTracking();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "big.dat", "contenido del agente " + Guid.NewGuid().ToString("N") + "\n");
        var objects = Path.Combine(Lfs, "objects");
        var tmp = Path.Combine(Lfs, "tmp");
        var victimObjects = NewVictim("otros-objetos-lfs");
        var victimTmp = NewVictim("otro-tmp-lfs");
        PlantJunction(objects, victimObjects);
        PlantJunction(tmp, victimTmp);
        var before = (Listing(victimObjects), Listing(victimTmp));
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: datos", Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, (Listing(victimObjects), Listing(victimTmp)));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "add") || Ran(after, "commit"), "git add ran the lfs filter with the git-lfs storage reached through junctions.");
        }
        finally
        {
            RemoveJunction(objects);
            RemoveJunction(tmp);
        }
    }

    public static TheoryData<string> GitLfsFolders() => ["lfs", "objects", "fan-out-1", "fan-out-2", "tmp", "logs"];

    [Theory]
    [MemberData(nameof(GitLfsFolders))]
    public async Task A_junction_in_the_git_lfs_storage_is_refused_before_git_add_runs_the_lfs_filter(string where)
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var head = CommitLfsTracking();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "big.dat", "contenido del agente " + Guid.NewGuid().ToString("N") + "\n");

        // git-lfs names the object by the SHA-256 of what it cleans: the file as written (no end-of-line conversion here).
        var oid = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(lease.Path, "big.dat"))));
        var folder = where switch
        {
            "lfs" => Lfs,
            "fan-out-1" => Path.Combine(Lfs, "objects", oid[..2]),
            "fan-out-2" => Path.Combine(Lfs, "objects", oid[..2], oid[2..4]),
            _ => Path.Combine(Lfs, where),
        };
        var victim = NewVictim("otro-almacen-lfs");
        PlantJunction(folder, victim);
        var before = Listing(victim);
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: datos", Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, Listing(victim));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "add") || Ran(after, "commit"), $"git add ran the lfs filter with {where} reached through a junction.");
        }
        finally
        {
            RemoveJunction(folder);
        }
    }

    /// <summary>
    /// The case only a check before every content command catches: `git status` runs the clean filter of git-lfs on a
    /// same-size change whose stat is dirty, and git-lfs stores the new object (measured with git-lfs 3.5.1, also with
    /// optional locks off). Inspecting the user's repository runs such a status.
    /// </summary>
    [Fact]
    public async Task A_junction_in_the_git_lfs_storage_is_refused_before_a_status_runs_the_lfs_filter()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        CommitLfsTracking();
        Repo.Write("big.dat", "uno\n");
        Repo.Run("add", "--", "big.dat");
        Repo.Run("commit", "-q", "-m", "fichero lfs");
        var file = Path.Combine(Repo.Root, "big.dat");
        File.WriteAllText(file, "dos\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-3));
        var objects = Path.Combine(Lfs, "objects");
        var victim = NewVictim("otros-objetos-lfs");
        PlantJunction(objects, victim);
        var before = Listing(victim);
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(Repo.Root, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, Listing(victim));
            Assert.False(Ran(recorder.Specs, "status"), "git status ran the lfs filter with the git-lfs objects reached through a junction.");
        }
        finally
        {
            RemoveJunction(objects);
        }
    }

    [Fact]
    public async Task A_junction_in_place_of_the_administrative_folder_of_a_vanished_worktree_is_refused_before_its_registration_is_dropped()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var admin = lease.AdminDirectory;
        var victim = NewVictim("otra-administracion");
        ReplaceWithJunction(admin, victim);
        File.WriteAllText(Path.Combine(victim, "precioso.txt"), "del usuario\n");

        // The worktree's folder is gone (a crash, a manual delete): EnsureAsync drops a registration like that.
        DeleteDirectory(lease.Path);
        try
        {
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => EnsureAsync(manager, "T1"));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(victim, "precioso.txt")));
            Assert.False(Ran(recorder.Specs.Skip(start), "worktree", "remove"), "git worktree remove ran on an administrative folder reached through a junction.");
        }
        finally
        {
            RemoveJunction(admin);
        }
    }
}
