using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Git;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Real git-lfs installs its hooks (<c>pre-push</c>, <c>post-checkout</c>, <c>post-commit</c>, <c>post-merge</c>) into the
/// <c>core.hooksPath</c> it is given every time its filter starts (measured with git-lfs 3.5.1: <c>filter-process</c>, and
/// the single-file <c>clean</c> and <c>smudge</c>, creating the folder when it is missing), and git then runs a hook written
/// that way at the end of the same command: with an empty folder as the hooks path, the smudge of a <c>git merge
/// --ff-only</c> wrote <c>post-merge</c> and git ran it (<c>git lfs post-merge</c> made the lockable file read-only). No
/// option of git-lfs stops it. So the hooks path Agent Studio gives git is an empty FILE: git finds no hook below a file,
/// and git-lfs cannot create its folder there, so it installs nothing and its filter goes on (ADR-0012). These tests take
/// a real git-lfs repository through every internal command, with logging hooks of the user and a lockable lfs file as
/// sentinels: git-lfs's hooks make a lockable file read-only when they run.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitLfsHooksTests : GitManagerTestBase
{
    private const string Message = "Agent Studio: entrega de la ejecución";

    private const string PointerHeader = "version https://git-lfs.github.com/spec/v1";

    // Every hook git-lfs installs, and the other ones the internal commands could run (commit, merge, update-ref, index writes).
    private static readonly string[] SentinelHooks =
    [
        "pre-push", "post-checkout", "post-commit", "post-merge", "pre-commit", "prepare-commit-msg", "commit-msg",
        "pre-merge-commit", "reference-transaction", "post-index-change", "post-rewrite", "pre-auto-gc",
    ];

    private static bool GitLfsAvailable => new ExecutableLocator().Find("git-lfs") is not null;

    private string HooksPath => Path.Combine(Repo.WorktreeRoot, ".no-hooks");

    private string UserHooks => Path.Combine(Repo.Root, ".git", "hooks");

    private string UserHookLog => Path.Combine(Repo.Root, ".git", HookScripts.LogName);

    private string LfsObjects => Path.Combine(Repo.Root, ".git", "lfs", "objects");

    private static bool IsReadOnly(string file) => new FileInfo(file).IsReadOnly;

    private int LfsObjectCount() =>
        Directory.Exists(LfsObjects) ? Directory.EnumerateFiles(LfsObjects, "*", SearchOption.AllDirectories).Count() : 0;

    /// <summary>Every file under <paramref name="folder"/> with the SHA-256 of its content, sorted.</summary>
    private static string Listing(string folder) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
                .Order(StringComparer.Ordinal));

    private static ApprovedDelivery Approved(string root, string commit, string head, DeliveryMode mode = DeliveryMode.FastForwardOnly)
    {
        var binding = DeliveryApproval.Bind(root, "main", mode, commit, head);
        var request = new ApprovalRequest("deliver-integration", "Entregar la ejecución", RiskLevel.Critical, null, [], ProjectId, TestRun.Value, Binding: binding);
        var decision = new ApprovalDecision(true, "usuario", DateTimeOffset.UtcNow, null, binding);
        return new ApprovedDelivery(root, "main", head, commit, mode, Message, request, decision);
    }

    /// <summary>
    /// The user's repository as <c>git lfs install --local</c> leaves it, with <c>*.dat</c> tracked by git-lfs and
    /// <c>*.lck</c> tracked and lockable, and the given lfs files committed on <c>main</c>.
    /// </summary>
    private string CommitLfsRepository(params (string Path, string Content)[] files)
    {
        Repo.Run("lfs", "install", "--local");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n*.lck filter=lfs diff=lfs merge=lfs -text lockable\n");
        foreach (var (path, content) in files)
        {
            Repo.Write(path, content);
        }

        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "repositorio git-lfs");
        return Repo.Head;
    }

    /// <summary>
    /// Replaces every hook of the user, the ones <c>git lfs install</c> wrote included, by one that leaves a log: from here
    /// on, a hook of the user that runs is seen. Only git commands that run no hook are made by the test afterwards.
    /// </summary>
    private void PlantUserSentinelHooks()
    {
        foreach (var hook in SentinelHooks)
        {
            Repo.Write(".git/hooks/" + hook, HookScripts.Logging(hook));
        }
    }

    /// <summary>Removes whatever is at the hooks path (the file, or a folder an earlier version made), so a test can plant something there.</summary>
    private void ClearHooksPath()
    {
        if (Directory.Exists(HooksPath))
        {
            Directory.Delete(HooksPath, recursive: true);
        }

        File.Delete(HooksPath);
    }

    /// <summary>The hooks path git receives is an empty regular file: no link, no folder, nothing written into it.</summary>
    private void AssertHooksPathIsTheEmptyFile(string step)
    {
        var folder = new DirectoryInfo(HooksPath);
        Assert.False(
            folder.Exists,
            $"After {step}, '{HooksPath}' is a folder holding [{(folder.Exists ? string.Join(", ", folder.EnumerateFileSystemInfos().Select(e => e.Name)) : string.Empty)}]: git-lfs could install its hooks there.");
        var file = new FileInfo(HooksPath);
        Assert.True(file.Exists, $"After {step}, '{HooksPath}' does not exist.");
        Assert.False(file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null, $"After {step}, '{HooksPath}' is a link.");
        Assert.Equal(0, file.Length);
    }

    // ---- the whole cycle -------------------------------------------------------------------------------------------

    /// <summary>
    /// A git-lfs repository through every internal command a run makes, each of which runs the lfs filter here: the status
    /// of <c>InspectAsync</c> (a same-size change of the user's lfs file, G-01), the checkout of a new worktree, the add and
    /// the commit of the agent's lfs files, the checkout of <c>_integration</c>, the integration (<c>merge-tree</c>,
    /// <c>commit-tree</c>, <c>update-ref</c>), the delivery (<c>git merge --ff-only</c>, which smudges into the user's
    /// working tree) and the cleanup. The content goes through git-lfs (pointers in the commits, the real content in every
    /// working tree), no hook runs, and the hooks path stays the empty file.
    /// </summary>
    [Fact]
    public async Task A_git_lfs_repository_goes_through_every_internal_command_and_no_hook_runs()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        const string userContent = "contenido grande del usuario\n";
        var head = CommitLfsRepository(("big.dat", userContent));
        PlantUserSentinelHooks();
        var userHooks = Listing(UserHooks);
        var manager = NewManager();
        Assert.False(File.Exists(HooksPath) || Directory.Exists(HooksPath), "The hooks path exists before the first command.");

        // As a run starts: the user's lfs file has a same-size change and an older time, so the status of InspectAsync
        // runs the clean filter, which stores the new content as an lfs object, before any worktree exists.
        var userFile = Path.Combine(Repo.Root, "big.dat");
        var objects = LfsObjectCount();
        File.WriteAllText(userFile, userContent.ToUpperInvariant());
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddMinutes(-3));
        Assert.True((await manager.InspectAsync(Repo.Root, Ct)).HasUncommittedChanges);
        Assert.True(LfsObjectCount() > objects, "The status of InspectAsync did not run git-lfs; the measurement this test relies on changed.");
        AssertHooksPathIsTheEmptyFile("InspectAsync");

        // The user puts the file back; its time stays older than the index, so every status cleans it again.
        File.WriteAllText(userFile, userContent);
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddMinutes(-3));

        // A worktree: its checkout (reset --hard) smudges the lfs file.
        var t1 = await EnsureAsync(manager, "T1", head);
        Assert.Equal(userContent, File.ReadAllText(Path.Combine(t1.Path, "big.dat")));
        AssertHooksPathIsTheEmptyFile("EnsureAsync");

        // The agent changes the lfs file and adds a lockable one: add and commit run the clean filter.
        WriteIn(t1, "big.dat", "contenido grande del agente\n");
        WriteIn(t1, "planos/plano.lck", "plano bloqueable del agente\n");
        var commit = await manager.CommitAllAsync(t1, "T1: datos lfs", Ct);
        Assert.True(commit.Committed);
        Assert.StartsWith(PointerHeader, Repo.Run("cat-file", "-p", commit.Commit + ":big.dat"), StringComparison.Ordinal);
        Assert.StartsWith(PointerHeader, Repo.Run("cat-file", "-p", commit.Commit + ":planos/plano.lck"), StringComparison.Ordinal);

        // git-lfs's post-commit makes a newly committed lockable file read-only when it runs.
        Assert.False(IsReadOnly(Path.Combine(t1.Path, "planos", "plano.lck")), "A git-lfs hook ran after the commit of the worktree.");
        AssertHooksPathIsTheEmptyFile("CommitAllAsync");

        // The integration of the task, and _integration checked out on its result (reset --hard and clean).
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, head, Ct);
        var task = new TaskId("I1");
        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, task, head, Ct);
        var merged = (await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, task, candidate, commit.Commit, "Integra T1", Ct)).ResultCommit!;
        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, head, merged, Ct);
        var integration = await EnsureAsync(manager, "_integration", head);
        await manager.CheckoutAsync(integration, merged, Ct);
        Assert.Equal("contenido grande del agente\n", File.ReadAllText(Path.Combine(integration.Path, "big.dat")));
        Assert.Equal("plano bloqueable del agente\n", File.ReadAllText(Path.Combine(integration.Path, "planos", "plano.lck")));
        AssertHooksPathIsTheEmptyFile("the integration");

        // The delivery: the fast-forward smudges both files into the user's working tree.
        Assert.Empty(await manager.FindDeliveryRefusalsAsync(Repo.Root, head, merged, Ct));
        var delivery = await manager.DeliverToUserBranchAsync(Approved(Repo.Root, merged, head), Ct);
        Assert.Equal(UserBranchDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(merged, Repo.Head);
        Assert.Equal("contenido grande del agente\n", File.ReadAllText(userFile));
        var userLockable = Path.Combine(Repo.Root, "planos", "plano.lck");
        Assert.Equal("plano bloqueable del agente\n", File.ReadAllText(userLockable));

        // git-lfs's post-merge makes every lockable file read-only when it runs.
        Assert.False(IsReadOnly(userLockable), "A git-lfs hook ran during the delivery.");
        AssertHooksPathIsTheEmptyFile("the delivery");

        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(t1, discardChanges: false, Ct));
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(integration, discardChanges: false, Ct));
        AssertHooksPathIsTheEmptyFile("the cleanup");

        Assert.False(File.Exists(UserHookLog), "A hook of the user ran: " + (File.Exists(UserHookLog) ? File.ReadAllText(UserHookLog) : string.Empty));
        Assert.Equal(userHooks, Listing(UserHooks));
    }

    // ---- the measured gap: a hook git-lfs writes during a command runs at its end ----------------------------------

    /// <summary>
    /// With an empty folder as the hooks path, the smudge of the delivery's <c>git merge --ff-only</c> installed git-lfs's
    /// hooks into it and git ran <c>post-merge</c> at the end of that same merge (measured with git-lfs 3.5.1 and git
    /// 2.45.2), before any check before the command could see it; with a merge commit, the <c>commit</c> after
    /// <c>merge --no-commit</c> found the folder filled and failed closed with the merge in progress. Here the delivered
    /// commit, made by the user's own git, brings the repository's first lfs files, so no internal command ran git-lfs
    /// before the delivery.
    /// </summary>
    [Theory]
    [InlineData(DeliveryMode.FastForwardOnly)]
    [InlineData(DeliveryMode.MergeCommit)]
    public async Task A_delivery_that_brings_lfs_files_runs_no_hook_git_lfs_writes_during_the_merge(DeliveryMode mode)
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var head = CommitLfsRepository();
        Repo.Run("checkout", "-q", "-b", "lado");
        Repo.Write("big.dat", "contenido grande\n");
        Repo.Write("planos/plano.lck", "plano bloqueable\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "ficheros lfs");
        var delivered = Repo.Head;
        Repo.Run("checkout", "-q", "main");
        PlantUserSentinelHooks();
        var manager = NewManager();

        // A run made its worktree on the base before it delivers; the base has no lfs file, so no filter ran yet.
        await EnsureAsync(manager, "T1", head);
        Assert.Empty(await manager.FindDeliveryRefusalsAsync(Repo.Root, head, delivered, Ct));

        var error = await Record.ExceptionAsync(() => manager.DeliverToUserBranchAsync(Approved(Repo.Root, delivered, head, mode), Ct));

        // First what matters: no hook ran. git-lfs's post-merge and post-commit make the lockable file read-only when they run.
        var userLockable = Path.Combine(Repo.Root, "planos", "plano.lck");
        Assert.True(File.Exists(userLockable), "The delivery did not write the lockable file: " + error);
        Assert.False(IsReadOnly(userLockable), "A hook git-lfs wrote during the delivery's merge ran at its end.");
        Assert.Null(error);
        if (mode == DeliveryMode.FastForwardOnly)
        {
            Assert.Equal(delivered, Repo.Head);
        }
        else
        {
            Assert.Equal([head, delivered], Repo.Run("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Skip(1));
        }

        Assert.False(File.Exists(Path.Combine(Repo.Root, ".git", "MERGE_HEAD")), "The delivery left a merge in progress.");
        Assert.Equal("contenido grande\n", File.ReadAllText(Path.Combine(Repo.Root, "big.dat")));
        AssertHooksPathIsTheEmptyFile("the delivery");
        Assert.False(File.Exists(UserHookLog), "A hook of the user ran.");
    }

    /// <summary>
    /// Control for the sentinels above, and the measurement the design rests on, with plain git and the same setup: with an
    /// empty FOLDER as <c>core.hooksPath</c>, the merge's smudge installs git-lfs's four hooks into it and git runs
    /// <c>post-merge</c>, which makes the lockable file read-only; with an empty FILE, the same merge installs nothing and
    /// runs nothing, and the content is the same. Pinned to the installed git-lfs (3.5.1 when measured): re-measure on upgrade.
    /// </summary>
    [Fact]
    public void Control_git_lfs_fills_an_empty_hooks_folder_during_a_merge_and_git_runs_its_post_merge_but_not_below_an_empty_file()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var head = CommitLfsRepository();
        Repo.Run("checkout", "-q", "-b", "lado");
        Repo.Write("planos/plano.lck", "plano bloqueable\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "fichero bloqueable");
        var delivered = Repo.Head;
        Repo.Run("checkout", "-q", "main");
        var lockable = Path.Combine(Repo.Root, "planos", "plano.lck");
        var folder = NewVictim("carpeta-de-hooks");
        var file = Path.Combine(NewVictim("fichero-de-hooks"), ".no-hooks");
        File.WriteAllBytes(file, []);

        TempGitRepo.Git(Repo.Root, "-c", "core.hooksPath=" + folder, "merge", "-q", "--ff-only", delivered);

        Assert.Equal(["post-checkout", "post-commit", "post-merge", "pre-push"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(IsReadOnly(lockable), "Control: git did not run the post-merge git-lfs wrote during the merge; the sentinel of these tests would see nothing.");

        File.SetAttributes(lockable, FileAttributes.Normal);
        TempGitRepo.Git(Repo.Root, "-c", "core.hooksPath=" + file, "reset", "-q", "--hard", head);
        TempGitRepo.Git(Repo.Root, "-c", "core.hooksPath=" + file, "merge", "-q", "--ff-only", delivered);

        Assert.Equal("plano bloqueable\n", File.ReadAllText(lockable));
        Assert.False(IsReadOnly(lockable), "A hook ran with an empty file as core.hooksPath.");
        Assert.Equal(0, new FileInfo(file).Length);
    }

    // ---- the hooks path ------------------------------------------------------------------------------------------

    /// <summary>
    /// The first command that can start git-lfs may come before any worktree exists (the status of <c>InspectAsync</c>), and
    /// a missing hooks path is a folder git-lfs creates and fills (measured). So that command finds the empty file in place:
    /// git-lfs runs (it stores the object) and writes no hook anywhere, also not into the user's hooks folder.
    /// </summary>
    [Fact]
    public async Task The_first_command_that_can_start_git_lfs_finds_the_empty_file_in_place()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        const string content = "contenido grande del usuario\n";
        CommitLfsRepository(("big.dat", content));
        PlantUserSentinelHooks();
        var userHooks = Listing(UserHooks);
        var userFile = Path.Combine(Repo.Root, "big.dat");
        File.WriteAllText(userFile, content.ToUpperInvariant());
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddMinutes(-3));
        var objects = LfsObjectCount();

        await NewManager().InspectAsync(Repo.Root, Ct);

        Assert.True(LfsObjectCount() > objects, "git-lfs did not run; the measurement this test relies on changed.");
        AssertHooksPathIsTheEmptyFile("InspectAsync");
        Assert.Equal(userHooks, Listing(UserHooks));
        Assert.False(Directory.Exists(Path.Combine(Repo.Root, ".git", "lfs", "logs")), "git-lfs logged an error.");
    }

    /// <summary>
    /// Agent Studio never creates the hooks file inside the repository a command works on: with the worktree root inside
    /// it (a configuration mistake <c>EnsureAsync</c> refuses too), a command that can start git-lfs is refused and
    /// nothing is created.
    /// </summary>
    [Fact]
    public async Task The_hooks_file_is_never_created_inside_the_repository_a_command_works_on()
    {
        var inside = Path.Combine(Repo.Root, "wt");
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(inside));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InspectAsync(Repo.Root, Ct));

        Assert.Contains("inside the repository", refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(inside), "A folder was created inside the user's repository.");
    }

    /// <summary>
    /// The empty folder an earlier version of Agent Studio used as the hooks path is replaced by the empty file before the
    /// first command that can start git-lfs, which would otherwise install its hooks into it.
    /// </summary>
    [Fact]
    public async Task The_empty_hooks_folder_of_an_earlier_version_is_replaced_by_the_empty_file()
    {
        Directory.CreateDirectory(HooksPath);
        var manager = NewManager();

        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "nuevo.txt", "x\n");

        AssertHooksPathIsTheEmptyFile("EnsureAsync");
    }

    /// <summary>
    /// A hooks folder that holds anything (git-lfs's own hooks, as an earlier version let it install them, or what an agent
    /// with a shell planted) is never emptied nor replaced: every command is refused, and the folder stays as it is.
    /// </summary>
    [Fact]
    public async Task A_hooks_folder_that_holds_anything_is_never_replaced_and_stops_every_command()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        ClearHooksPath();
        Directory.CreateDirectory(HooksPath);
        File.WriteAllText(Path.Combine(HooksPath, "post-merge"), HookScripts.Logging("post-merge"));
        WriteIn(lease, "nuevo.txt", "x\n");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.Contains("must be an empty file", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(["post-merge"], Directory.EnumerateFileSystemEntries(HooksPath).Select(Path.GetFileName));
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    /// <summary>
    /// The hooks file with content was written by someone else: it is refused like a folder with a hook. Its content does
    /// not reach git (nothing is below a file), but the hooks path is an empty file or nothing runs.
    /// </summary>
    [Fact]
    public async Task A_hooks_file_with_content_stops_every_command()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        ClearHooksPath();
        File.WriteAllText(HooksPath, "#!/bin/sh\nexit 0\n");
        WriteIn(lease, "nuevo.txt", "x\n");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.Contains("must be an empty file", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    /// <summary>A junction at the hooks path, to a folder with a hook, is refused before git runs.</summary>
    [Fact]
    public async Task A_junction_at_the_hooks_path_stops_every_command()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var victim = NewVictim("hooks-de-fuera");
        File.WriteAllText(Path.Combine(victim, "post-commit"), HookScripts.Logging("post-commit"));
        ClearHooksPath();
        CreateJunction(HooksPath, victim);
        WriteIn(lease, "nuevo.txt", "x\n");

        try
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(victim)!, HookScripts.LogName)), "The hook behind the junction ran.");
            Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
        }
        finally
        {
            RemoveJunction(HooksPath);
        }
    }
}
