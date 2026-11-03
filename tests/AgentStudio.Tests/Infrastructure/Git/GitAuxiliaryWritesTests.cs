using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Application.Security;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// G-05: git writes more than the index, the refs and the objects when the repository's configuration asks for it, and
/// none of those destinations was checked for links. Measured with git 2.45.2 under the per-command configuration of
/// Agent Studio: with <c>core.splitIndex=true</c> every index write left <c>sharedindex.*</c> in the git directory (the
/// administrative folder of a worktree, the user's <c>.git</c> in a delivery); with <c>rerere.enabled=true</c>, or with an
/// <c>rr-cache</c> folder planted (rerere is then on by default), every <c>commit</c> wrote <c>MERGE_RR</c> and created
/// <c>rr-cache</c>; with <c>core.untrackedCache=true</c> the index gained the untracked cache. So every internal command
/// turns them off with <c>-c</c>, and the tests check the folders, not only the arguments.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitAuxiliaryWritesTests : GitManagerTestBase
{
    private const string Message = "Agent Studio: entrega de la ejecución";

    private static readonly string[] AuxiliaryWritesOff =
    [
        "rerere.enabled=false",
        "rerere.autoUpdate=false",
        "core.splitIndex=false",
        "core.untrackedCache=false",
        "fetch.writeCommitGraph=false",
        "gc.writeCommitGraph=false",
    ];

    private string CommonDirectory => Path.Combine(Repo.Root, ".git");

    private static string Names(string folder, string pattern) =>
        string.Join(",", Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal) : []);

    private static bool HasIndexExtension(string index, string signature) =>
        Encoding.ASCII.GetString(File.ReadAllBytes(index)).Contains(signature, StringComparison.Ordinal);

    /// <summary>What a run does to a worktree: create it, commit in it, and check it out again.</summary>
    private async Task<WorktreeLease> WorktreeCycleAsync(GitWorktreeManager manager)
    {
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "nuevo.txt", "contenido del agente\n");
        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);
        await CommitInAsync(manager, lease, "otro.txt", "más contenido\n");
        return lease;
    }

    /// <summary>A merge commit delivered into main, approved for exactly that delivery, after a commit of the user's own.</summary>
    private async Task<UserBranchDelivery> DeliverMergeCommitAsync(GitWorktreeManager manager, string commit, string head)
    {
        var binding = DeliveryApproval.Bind(Repo.Root, "main", DeliveryMode.MergeCommit, commit, head);
        var request = new ApprovalRequest("deliver-integration", "Entregar la ejecución", RiskLevel.Critical, null, [], ProjectId, TestRun.Value, Binding: binding);
        var decision = new ApprovalDecision(true, "usuario", DateTimeOffset.UtcNow, null, binding);
        return await manager.DeliverToUserBranchAsync(new ApprovedDelivery(Repo.Root, "main", head, commit, DeliveryMode.MergeCommit, Message, request, decision), Ct);
    }

    private string UserCommit()
    {
        Repo.Write("user.txt", "trabajo del usuario\n");
        Repo.Run("add", "--", "user.txt");
        Repo.Run("commit", "-q", "-m", "trabajo del usuario");
        return Repo.Head;
    }

    [Fact]
    public async Task Every_internal_command_turns_off_the_auxiliary_writes()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);

        await manager.InspectAsync(Repo.Root, Ct);
        var lease = await WorktreeCycleAsync(manager);
        await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);
        var commit = (await manager.CommitAllAsync(lease, "nada", Ct)).Commit;
        var head = UserCommit();
        Assert.Equal(UserBranchDeliveryStatus.Delivered, (await DeliverMergeCommitAsync(manager, commit, head)).Status);

        Assert.NotEmpty(recorder.Specs);
        Assert.All(recorder.Specs, spec =>
        {
            foreach (var setting in AuxiliaryWritesOff)
            {
                Assert.True(
                    spec.Arguments.Zip(spec.Arguments.Skip(1)).Any(pair => pair.First == "-c" && pair.Second == setting),
                    $"git {RecordingProcessRunner.SubCommand(spec)} ran without -c {setting}.");
            }
        });
    }

    [Fact]
    public async Task A_split_index_the_repository_asks_for_leaves_no_shared_index_in_a_worktree()
    {
        Repo.Run("config", "core.splitIndex", "true");
        var manager = NewManager();

        var lease = await WorktreeCycleAsync(manager);

        Assert.Equal(string.Empty, Names(lease.AdminDirectory, "sharedindex.*"));
        Assert.Equal(string.Empty, Names(CommonDirectory, "sharedindex.*"));
        Assert.False(HasIndexExtension(Path.Combine(lease.AdminDirectory, "index"), "link"), "the worktree's index was written as a split index.");
    }

    [Fact]
    public async Task Rerere_the_repository_turns_on_writes_nothing_on_a_commit_of_a_worktree()
    {
        Repo.Run("config", "rerere.enabled", "true");
        var manager = NewManager();

        var lease = await WorktreeCycleAsync(manager);

        Assert.Equal(string.Empty, Names(lease.AdminDirectory, "MERGE_RR*"));
        Assert.False(Directory.Exists(Path.Combine(CommonDirectory, "rr-cache")), "a commit of the worktree created rr-cache.");
    }

    [Fact]
    public async Task A_planted_rr_cache_folder_does_not_turn_rerere_on_for_a_commit_of_a_worktree()
    {
        // Without rerere.enabled, git turns rerere on when the folder exists.
        Directory.CreateDirectory(Path.Combine(CommonDirectory, "rr-cache"));
        var manager = NewManager();

        var lease = await WorktreeCycleAsync(manager);

        Assert.Equal(string.Empty, Names(lease.AdminDirectory, "MERGE_RR*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(CommonDirectory, "rr-cache")));
    }

    [Fact]
    public async Task An_untracked_cache_the_repository_asks_for_is_not_written_into_a_worktrees_index()
    {
        Repo.Run("config", "core.untrackedCache", "true");
        var manager = NewManager();

        var lease = await WorktreeCycleAsync(manager);

        Assert.False(HasIndexExtension(Path.Combine(lease.AdminDirectory, "index"), "UNTR"), "the worktree's index holds the untracked cache.");
    }

    [Fact]
    public async Task A_delivery_writes_no_shared_index_rerere_state_or_untracked_cache_into_the_users_git_directory()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "nuevo.txt", "contenido del agente\n");
        var head = UserCommit();

        // Planted before the step: the user's repository asks for all three.
        Repo.Run("config", "core.splitIndex", "true");
        Repo.Run("config", "rerere.enabled", "true");
        Repo.Run("config", "core.untrackedCache", "true");

        var outcome = await DeliverMergeCommitAsync(manager, commit, head);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal(string.Empty, Names(CommonDirectory, "sharedindex.*"));
        Assert.Equal(string.Empty, Names(CommonDirectory, "MERGE_RR*"));
        Assert.False(Directory.Exists(Path.Combine(CommonDirectory, "rr-cache")), "the delivery created rr-cache in the user's repository.");
        var index = Path.Combine(CommonDirectory, "index");
        Assert.False(HasIndexExtension(index, "UNTR") || HasIndexExtension(index, "link"), "the delivery wrote the untracked cache or a split index into the user's index.");
    }

    /// <summary>
    /// A guard, not a red test: <c>gc.auto=0</c> and <c>maintenance.auto=false</c> already kept every internal command from
    /// writing a commit-graph or a multi-pack-index (measured with git 2.45.2), and <c>gc.writeCommitGraph=false</c> and
    /// <c>fetch.writeCommitGraph=false</c> are a second line. The control at the end shows that this configuration does make
    /// a plain git commit write them.
    /// </summary>
    [Fact]
    public async Task Gc_and_maintenance_the_repository_asks_for_write_no_commit_graph_or_multi_pack_index()
    {
        Repo.Run("repack", "-q");
        Repo.Run("-c", "gc.auto=0", "-c", "maintenance.auto=false", "commit", "-q", "--allow-empty", "-m", "otro paquete");
        Repo.Run("repack", "-q");
        foreach (var (key, value) in new[]
        {
            ("gc.auto", "1"), ("gc.autoPackLimit", "1"), ("gc.writeCommitGraph", "true"), ("fetch.writeCommitGraph", "true"),
            ("maintenance.auto", "true"), ("maintenance.gc.enabled", "false"),
            ("maintenance.commit-graph.enabled", "true"), ("maintenance.commit-graph.auto", "1"),
            ("maintenance.incremental-repack.enabled", "true"), ("maintenance.incremental-repack.auto", "1"),
        })
        {
            Repo.Run("config", key, value);
        }

        var info = Path.Combine(CommonDirectory, "objects", "info");
        var pack = Path.Combine(CommonDirectory, "objects", "pack");
        var packs = Names(pack, "*.pack");
        var manager = NewManager();

        await WorktreeCycleAsync(manager);

        Assert.Equal(string.Empty, Names(info, "commit-graph*"));
        Assert.Equal(string.Empty, Names(pack, "multi-pack-index*"));
        Assert.Equal(packs, Names(pack, "*.pack"));

        // Control: the same configuration makes a plain git commit run gc and maintenance, which write them.
        Repo.Run("commit", "-q", "--allow-empty", "-m", "control");
        Assert.NotEqual(string.Empty, Names(info, "commit-graph*"));
    }
}
