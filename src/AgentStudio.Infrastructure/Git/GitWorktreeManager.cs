using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// <see cref="IWorktreeManager"/> over git.exe (docs/13 sections 3.4 and 5). Worktrees live outside the repository,
/// detached and locked; the system makes every commit and merge, the agent never does; merges happen without a
/// worktree (<c>merge-tree</c>, <c>commit-tree</c>, compare-and-swap <c>update-ref</c>).
/// </summary>
/// <remarks>
/// <para>
/// What Agent Studio changes in the user's repository is limited to the object database, <c>refs/agentstudio/**</c>,
/// the <c>agentstudio/**</c> branches and the administration of its own worktrees. The user's working tree, HEAD,
/// index and branches are never touched, except by <see cref="DeliverToUserBranchAsync"/>: the approved delivery, the
/// only command that runs with the user's hooks.
/// </para>
/// <para>
/// The agent controls every file of its worktree, the <c>.git</c> file included, and with a shell it can plant links
/// and refs. So: the repository always comes from the project (<see cref="WorktreeLease.RepositoryRoot"/>), never from
/// the worktree; ownership is proven by that repository (its worktree list, the <c>agent-studio:...</c> lock reason and
/// the administrative folder whose <c>gitdir</c> points back at the worktree), otherwise the operation is refused with
/// <see cref="WorktreeOwnershipException"/>; every command on a worktree runs with explicit <c>--git-dir</c> and
/// <c>--work-tree</c>, so the <c>.git</c> file is ignored; no hook runs; refs are written with <c>--no-deref</c> and
/// compare-and-swap, and only inside the Agent Studio namespace; and git never deletes or resets through a link.
/// Mutating operations on one repository are serialised inside this instance.
/// </para>
/// </remarks>
public sealed class GitWorktreeManager : IWorktreeManager
{
    public const string CommitAuthorName = "Agent Studio";
    public const string CommitAuthorEmail = "agent-studio@localhost";

    private const string AgentsExclude = ":(exclude).agents";

    // Folder under the worktree root where a worktree is moved before it is deleted.
    private const string TrashFolderName = ".trash";

    // `-c` options go before the sub-command. Values carry no quotes: the arguments are passed as a list. author.* and
    // committer.* win over user.*, so all of them are set.
    private static readonly string[] CommitIdentity =
    [
        "-c", "user.name=" + CommitAuthorName,
        "-c", "user.email=" + CommitAuthorEmail,
        "-c", "author.name=" + CommitAuthorName,
        "-c", "author.email=" + CommitAuthorEmail,
        "-c", "committer.name=" + CommitAuthorName,
        "-c", "committer.email=" + CommitAuthorEmail,
        "-c", "commit.gpgsign=false",
    ];

    // The delivery keeps the user's identity (it is the user's merge) but never signs. Placed after --git-dir/--work-tree
    // in the argument list so the content gate still reads those when it inspects the configuration (R3-05).
    private static readonly string[] NoSigning =
    [
        "-c", "commit.gpgsign=false",
        "-c", "tag.gpgsign=false",
    ];

    // The approvals of deliveries already spent, process-wide: one approval moves the user's branch once, whatever
    // instance it reaches. Each binding has its own nonce, so a new approval of the same delivery is a new entry.
    private static readonly ConcurrentDictionary<string, byte> SpentApprovals = new(StringComparer.Ordinal);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GitCommandRunner _git;
    private readonly string _worktreeRoot;
    private readonly CaptureBudget _captureBudget;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private Version? _gitVersion;

    public GitWorktreeManager(IProcessRunner runner, IExecutableLocator locator, GitWorktreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorktreeRoot);

        _worktreeRoot = GitNaming.NormalizePath(options.WorktreeRoot);
        _captureBudget = options.CaptureBudget ?? CaptureBudget.Default;
        _git = new GitCommandRunner(runner, locator, options);
    }

    /// <summary>The oldest git with <c>merge-tree --write-tree</c>.</summary>
    public static Version MinimumGitVersion { get; } = new(2, 38, 0);

    /// <summary>The folder that holds every worktree.</summary>
    public string WorktreeRoot => _worktreeRoot;

    public async Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var version = await GetGitVersionAsync(ct).ConfigureAwait(false);
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        var root = repository.Root;

        var head = (await _git.RunCheckedAsync(root, ["rev-parse", "--verify", "HEAD"], ct).ConfigureAwait(false)).StandardOutput.Trim();

        // Exit code 1 means a detached HEAD.
        var branchResult = await _git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, ct).ConfigureAwait(false);
        if (branchResult.ExitCode > 1)
        {
            branchResult.EnsureSuccess("symbolic-ref");
        }

        var branch = branchResult.ExitCode == 0 ? branchResult.StandardOutput.Trim() : null;

        // Untracked files one by one, so the files Agent Studio keeps in the project's .agentstudio folder (its journal, its
        // memory export) are told apart from the user's: only the user's own changes make the repository dirty.
        var status = await _git.RunCheckedAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], ct, scope: ScopeOf(repository)).ConfigureAwait(false);
        var project = ProjectPrefix(root, repoRoot);
        var dirty = GitPorcelain.ParseStatusLines(status.StandardOutput).Any(line => !AgentStudioFiles.IsAgentStudioStatusLine(line, project));
        return new RepositoryState(root, head, branch, dirty, version);
    }

    /// <summary>Where the project at <paramref name="projectRoot"/> is in the repository at <paramref name="root"/>: <c>apps/calc/</c>, or empty for the root itself.</summary>
    private static string ProjectPrefix(string root, string projectRoot)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(projectRoot)).Replace('\\', '/');
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? string.Empty : relative.TrimEnd('/') + "/";
    }

    /// <summary>
    /// Captures the user's own repository, read-only (no command refreshes its index): HEAD, the branches but Agent
    /// Studio's <c>agentstudio/*</c>, the tags, the stash, a hash of what is staged, the status lines (ignored entries
    /// included) with the content of every path they list, and the files git reads or runs from the repository: its
    /// configuration and its hooks. The engine compares a capture taken before an agent run with one taken after it
    /// (<see cref="UserRepositoryState.Compare"/>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The index is hashed through <c>git ls-files --stage -v</c> (mode, object id, stage, path and the
    /// assume-unchanged/skip-worktree flags), not as a file: git rewrites the file to refresh its stat cache on a plain
    /// <c>git status</c>, which changes nothing staged.</item>
    /// <item>Every untracked file is listed (<c>--untracked-files=all</c>), so a file added inside an untracked folder
    /// shows, and so is every ignored one (<c>--ignored=matching</c>); a folder ignored as a whole (<c>bin/</c>) is one
    /// entry.</item>
    /// <item>Each listed file is hashed (SHA-256), and so is the content of every file inside an untracked or ignored
    /// folder, so a further edit of a file that was already modified, or a same-length swap inside a folder, shows too
    /// (N-07), within <see cref="CaptureBudget"/>. A file over the per-file cap, one past the budget and one that cannot
    /// be read are marked indeterminate instead of trusted by size and time; their metadata is still compared, for a
    /// folder that of every entry in it (R3-02). Links are never followed.</item>
    /// <item>So are the tracked paths no status line can show (R3-04, <see cref="UserRepositoryState.HiddenTracked"/>),
    /// found in the same <c>ls-files</c>: the files flagged assume-unchanged or skip-worktree, against the per-file cap,
    /// and the folder of each submodule checked out, like an ignored folder and within the same budget. No git runs
    /// inside a submodule.</item>
    /// <item>The configuration is <c>config</c> (and <c>config.worktree</c>) of the git directory and of the common
    /// directory, byte for byte; the hooks are the files of the user's <c>core.hooksPath</c> (Agent Studio's own
    /// override never counts) or, without one, of <c>hooks</c> in the common directory.</item>
    /// </list>
    /// </remarks>
    public async Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        var root = repository.Root;

        // Exit code 1 means a detached HEAD.
        var headRef = await _git.RunAsync(root, ["symbolic-ref", "--quiet", "HEAD"], null, ct).ConfigureAwait(false);
        if (headRef.ExitCode > 1)
        {
            headRef.EnsureSuccess("symbolic-ref");
        }

        var headCommit = await TryReadRefAsync(root, "HEAD", ct).ConfigureAwait(false);
        var stash = await TryReadRefAsync(root, "refs/stash", ct).ConfigureAwait(false);

        var refs = await _git.RunCheckedAsync(root, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)", "refs/heads/", "refs/tags/"], ct).ConfigureAwait(false);
        var branches = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 2 || fields[0].StartsWith("refs/heads/agentstudio/", StringComparison.Ordinal))
            {
                continue;
            }

            // A symbolic ref is shown with the ref it names, so turning a branch into one is a change too.
            var value = fields.Length > 2 && fields[2].Length > 0 ? $"{fields[1]} -> {fields[2]}" : fields[1];
            var target = fields[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? branches : tags;
            target[fields[0]] = value;
        }

        var index = await _git.RunCheckedAsync(root, ["ls-files", "-z", "--stage", "-v"], ct).ConfigureAwait(false);
        var status = await _git.RunCheckedAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=all"], ct, scope: ScopeOf(repository)).ConfigureAwait(false);
        var (contents, hidden) = await RepositoryFingerprints.OfPathsAsync(
            root, GitPorcelain.ParseStatusPaths(status.StandardOutput), HiddenFromStatus(root, index.StandardOutput), _captureBudget, ct).ConfigureAwait(false);

        return new UserRepositoryState(
            headCommit ?? string.Empty,
            headRef.ExitCode == 0 ? headRef.StandardOutput.Trim() : null,
            branches,
            tags,
            stash,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(index.StandardOutput))),
            GitPorcelain.ParseStatusLines(status.StandardOutput),
            contents,
            await CaptureGitFilesAsync(repository, ct).ConfigureAwait(false),
            hidden);
    }

    /// <summary>
    /// The tracked paths no status line can show (R3-04), from the entries of <c>git ls-files -z --stage -v</c>
    /// (<c>tag mode object stage&lt;TAB&gt;path</c>): each file the index flags assume-unchanged (a lowercase tag) or
    /// skip-worktree (<c>S</c>), whose changes in the working tree git does not look for, and, ending with '/', the folder
    /// of each submodule checked out (a gitlink, mode 160000, whose folder has a <c>.git</c>), which the status ignores.
    /// Only the index of the user's repository is read: git never runs inside a submodule, where its own configuration
    /// could run a program (N-01).
    /// </summary>
    private static List<string> HiddenFromStatus(string root, string stagedEntries)
    {
        var paths = new List<string>();
        foreach (var entry in stagedEntries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 2 || entry[1] != ' ')
            {
                continue;
            }

            var path = entry[(tab + 1)..];
            if (entry.AsSpan(2).StartsWith("160000 ", StringComparison.Ordinal))
            {
                var gitEntry = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar), ".git");
                if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                {
                    paths.Add(path + "/");
                }
            }
            else if (char.IsLower(entry[0]) || entry[0] == 'S')
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// The repository's configuration files (<c>config</c> and <c>config.worktree</c> of the git directory and of the
    /// common directory) and the files of its effective hooks folder, by <see cref="RepositoryFingerprints.DisplayPath"/>.
    /// A folder of the hooks folder that cannot be listed is kept too, by its path ending with '/', without metadata: it
    /// always blocks (R3-03).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, FileFingerprint>> CaptureGitFilesAsync(UserRepository repository, CancellationToken ct)
    {
        var hooks = await ReadUserHooksPathAsync(repository, ct).ConfigureAwait(false) ?? Path.Combine(repository.CommonDirectory, "hooks");
        var (hookFiles, unlisted) = RepositoryFingerprints.HookFiles(hooks, repository.Root);
        var candidates = new[]
            {
                Path.Combine(repository.GitDirectory, "config"),
                Path.Combine(repository.GitDirectory, "config.worktree"),
                Path.Combine(repository.CommonDirectory, "config"),
                Path.Combine(repository.CommonDirectory, "config.worktree"),
            }
            .Concat(hookFiles)
            .Select(GitNaming.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // The configuration and hooks have no total budget, only the per-file cap: they are few and git reads them all.
        var perFile = RepositoryFingerprints.Budget.PerFileOnly(_captureBudget.MaxFileBytes);
        var files = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            if (await RepositoryFingerprints.OfFileAsync(file, perFile, ct).ConfigureAwait(false) is { } fingerprint)
            {
                files[RepositoryFingerprints.DisplayPath(repository.Root, file)] = fingerprint;
            }
        }

        foreach (var folder in unlisted)
        {
            files[RepositoryFingerprints.DisplayPath(repository.Root, GitNaming.NormalizePath(folder)) + "/"] = RepositoryFingerprints.Unlisted;
        }

        return files;
    }

    /// <summary>
    /// Creates the worktree, or returns the existing one when it matches the request (same lock reason, same base) and
    /// its creation finished. Anything else found at the path fails closed with <see cref="WorktreeMismatchException"/>.
    /// </summary>
    public async Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        GitNaming.ValidateProjectId(request.Project.ProjectId);
        GitNaming.ValidateRun(request.Run);
        GitNaming.ValidateName(request.Name);
        var requestedBase = GitNaming.NormalizeObjectId(request.BaseCommit, nameof(request));

        await RequireSupportedGitAsync(ct).ConfigureAwait(false);
        var repoRoot = await ResolveRepositoryRootAsync(request.Project.RootPath, ct).ConfigureAwait(false);
        var baseCommit = await RequireCommitAsync(repoRoot, requestedBase, ct).ConfigureAwait(false);

        var path = GitNaming.WorktreePath(_worktreeRoot, repoRoot, request.Run, request.Name);
        if (GitNaming.SamePath(path, repoRoot) || GitNaming.IsUnder(path, repoRoot))
        {
            throw new InvalidOperationException($"The worktree root '{_worktreeRoot}' is inside the repository '{repoRoot}': worktrees must live outside the repository.");
        }

        var reason = GitNaming.LockReason(request.Project.ProjectId, request.Run, request.Name);
        var tipRef = GitNaming.TipRef(request.Run, request.Name);
        var startRef = GitNaming.StartRef(request.Run, request.Name);

        using var gate = await AcquireGateAsync(repoRoot, ct).ConfigureAwait(false);
        _git.CreateHooksPath();
        RequireNoLinkOnTheWay(path);

        var repository = await OpenRepositoryAsync(repoRoot, ct).ConfigureAwait(false);

        // H-03: git writes a ref, and its reflog, through a link planted on the way to it (measured with git 2.45.2), so
        // the refs of this worktree are checked before anything is written, the worktree included.
        RequireNoLinkOnRefPath(repository.CommonDirectory, tipRef);
        RequireNoLinkOnRefPath(repository.CommonDirectory, startRef);

        var registered = repository.LinkedAt(path);
        if (registered.Count > 1)
        {
            throw new WorktreeMismatchException($"'{path}' is registered more than once in '{repoRoot}'; refusing to use it.");
        }

        if (registered.Count == 1)
        {
            var existing = registered[0];
            if (!existing.Locked || !string.Equals(existing.LockReason, reason, StringComparison.Ordinal))
            {
                throw new WorktreeMismatchException(
                    $"A worktree is registered at '{path}' but it is not the one requested (lock reason '{existing.LockReason}', expected '{reason}').");
            }

            if (Directory.Exists(path))
            {
                var owned = VerifyRegistration(repository, path, request.Run, request.Name);

                // P-01: a reused worktree is still detached, or a later commit or reset would move the branch HEAD names.
                RequireDetachedHead(owned);
                await VerifyCompleteAsync(owned, baseCommit, startRef, ct).ConfigureAwait(false);
                await CreateRefIfMissingAsync(ScopeOf(repository), tipRef, owned.Head ?? baseCommit, ct).ConfigureAwait(false);
                return Lease(owned, baseCommit);
            }

            // Registered and locked by us, but the directory is gone (crash, manual delete): drop that registration and
            // nothing else. `worktree prune` would also drop the user's own worktrees whose folder is missing for a moment.
            await RequireRecordedBaseAsync(repoRoot, startRef, baseCommit, path, ct).ConfigureAwait(false);
            await DropRegistrationAsync(repoRoot, repository.CommonDirectory, existing.Path, ct).ConfigureAwait(false);
        }
        else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new WorktreeMismatchException(
                $"'{path}' already exists and is not a registered worktree; refusing to reuse it. If it is what an interrupted removal left, remove it with RemoveAsync(discardChanges: true).");
        }
        else
        {
            await RequireRecordedBaseAsync(repoRoot, startRef, baseCommit, path, ct).ConfigureAwait(false);
        }

        // H-03: `worktree add` writes the new administrative folder under <common>/worktrees, so that folder is checked
        // before git runs (the check of the folder git created, in VerifyRegistration, comes after git has written).
        RequireWorktreesFolderWithoutLinks(repository.CommonDirectory, request.Name);

        // A-02: create the worktree WITHOUT checking out. `worktree add`'s own checkout runs in the new worktree's
        // GIT_DIR/GIT_WORK_TREE context, where an `includeIf "gitdir:.../worktrees/..."` or a `config.worktree` the
        // parent never sees can be active (measured with git 2.45.2) and pull in a filter that never went through
        // GitCallbacks. So the checkout is deferred to CheckoutBaseIntoNewWorktreeAsync, which inspects that same
        // context and then runs a `reset --hard` through the content gate.
        // -c submodule.recurse=false: a later recurse must not run a child git inside a submodule the base registers.
        await _git.RunCheckedAsync(repoRoot, ["-c", "submodule.recurse=false", "worktree", "add", "--no-checkout", "--detach", "--lock", "--reason", reason, path, baseCommit], ct, scope: ScopeOf(repository))
            .ConfigureAwait(false);

        // VerifyRegistration also refuses an administrative folder reached through a link (step 2 of A-02).
        var created = await OpenRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        var newWorktree = VerifyRegistration(created, path, request.Run, request.Name);
        await CheckoutBaseIntoNewWorktreeAsync(repoRoot, newWorktree, baseCommit, ct).ConfigureAwait(false);

        // The start ref is written once the worktree is complete (checked out); a worktree without it is never reused.
        await CreateRefIfMissingAsync(ScopeOf(created), startRef, baseCommit, ct).ConfigureAwait(false);
        await CreateRefIfMissingAsync(ScopeOf(created), tipRef, baseCommit, ct).ConfigureAwait(false);
        return Lease(newWorktree, baseCommit);
    }

    /// <summary>
    /// H-03: before <c>worktree add</c>, the folder git creates the administrative folder in
    /// (<c>&lt;common&gt;/worktrees</c>) and the one it tries first (<c>&lt;common&gt;/worktrees/&lt;name&gt;</c>, the base
    /// name of the worktree's path), if they exist, must not be links and must resolve under the common directory.
    /// Measured with git 2.45.2: with a junction at <c>worktrees</c>, <c>worktree add</c> wrote the new administrative
    /// folder (<c>HEAD</c>, <c>gitdir</c>, <c>commondir</c>, <c>locked</c>, <c>logs</c>) through it, outside the
    /// repository. A junction at <c>worktrees/&lt;name&gt;</c> makes git skip to <c>&lt;name&gt;1</c> (its <c>mkdir</c>
    /// fails), but it is planted, and the ownership check reads every folder there, so it is refused too.
    /// </summary>
    private static void RequireWorktreesFolderWithoutLinks(string commonDirectory, string name)
    {
        var worktrees = Path.Combine(commonDirectory, "worktrees");
        var first = Path.Combine(worktrees, name);
        if (WorktreeFileSystem.HasLinkBelow(commonDirectory, first)
            || !ResolvesUnder(commonDirectory, worktrees)
            || !ResolvesUnder(commonDirectory, first))
        {
            throw new WorktreeOwnershipException(
                $"The administrative folder '{first}' of a new worktree is reached through a link under '{commonDirectory}', or resolves outside it; refusing to create the worktree.");
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> does not exist, or when its final path (every link resolved) is under the final
    /// path of <paramref name="commonDirectory"/>, or is that path when <paramref name="orSame"/>; a path Windows cannot
    /// resolve fails closed.
    /// </summary>
    private static bool ResolvesUnder(string commonDirectory, string path, bool orSame = false) =>
        WorktreeFileSystem.ResolvesUnder(commonDirectory, path, orSame);

    /// <summary>
    /// The administrative folder of a worktree must not be reached through a link, nor hold one: git writes the
    /// worktree's HEAD, index and reflog there on every commit and checkout, and <c>worktree remove</c> deletes it
    /// recursively. Measured with git 2.45.2 (H-03): with that folder turned into a junction, a commit wrote HEAD and the
    /// index through it, and <c>worktree remove --force --force</c> emptied the folder it points at, outside the
    /// repository. And X-02: with a junction at <c>logs</c> inside it, a commit and a reset appended to <c>logs/HEAD</c>
    /// outside, and <c>worktree remove --force --force</c> emptied the folder the junction points to.
    /// </summary>
    private static void RequireAdminDirectoryWithoutLinks(string commonDirectory, string adminDirectory, string worktreePath)
    {
        if (WorktreeFileSystem.HasLinkBelow(commonDirectory, adminDirectory))
        {
            throw new WorktreeOwnershipException(
                $"The administrative folder '{adminDirectory}' of '{worktreePath}' is reached through a link under '{commonDirectory}'; refusing to touch it.");
        }

        if (WorktreeFileSystem.FindLinkInside(adminDirectory) is { } link)
        {
            throw new WorktreeOwnershipException(
                $"The administrative folder '{adminDirectory}' of '{worktreePath}' holds a link ('{link}'), so what git writes or deletes there is reached through a link; refusing to touch it.");
        }
    }

    // What git writes in the administrative folder of a worktree (measured with git 2.45.2, X-02): `add` replaces index by
    // a rename; `commit` writes COMMIT_EDITMSG in place, replaces HEAD and index by a rename and appends to logs/HEAD;
    // `reset --hard` replaces HEAD, index and ORIG_HEAD by a rename and appends to logs/HEAD. `clean` and `status` write
    // nothing there. Through a hard link, a commit overwrote the other file of COMMIT_EDITMSG and appended to that of
    // logs/HEAD, and a reset to that of logs/HEAD; a rename leaves the other file alone, but a second name is refused too.
    private static readonly string[] CommitWrites = ["index", "COMMIT_EDITMSG", "HEAD", Path.Combine("logs", "HEAD")];

    private static readonly string[] ResetWrites = ["HEAD", "index", "ORIG_HEAD", Path.Combine("logs", "HEAD")];

    /// <summary>
    /// X-02: before a command writes in the administrative folder of an owned worktree, each file of
    /// <paramref name="names"/> it writes there must be reached without a link and have a single name (the per-file
    /// check of the delivery). The folder itself, and every link inside it, were already refused by
    /// <see cref="RequireAdminDirectoryWithoutLinks"/> when the worktree was verified.
    /// </summary>
    private static void RequireAdminFilesWithoutLinks(OwnedWorktree owned, string[] names, string writer) =>
        RequireGitFilesWithoutLinks(owned.AdminDirectory, owned.CommonDirectory, names, writer);

    /// <summary>
    /// P-02: before git writes objects (<c>add</c>, <c>commit</c>, <c>merge-tree</c>, <c>commit-tree</c>, the delivery
    /// merge), the object store must be reached without a link: <c>objects</c>, <c>objects/pack</c> and
    /// <c>objects/info</c> resolve under the common directory, and no entry of <c>objects</c> (every fan-out folder among
    /// them) is a link. Measured with git 2.45.2: through a junction at <c>objects/&lt;xx&gt;</c> or at <c>objects</c>,
    /// <c>add</c> wrote the loose object outside the repository; with <c>core.bigFileThreshold=1</c>, which the repository
    /// can set, it wrote a pack through a junction at <c>objects/pack</c>. One listing of <c>objects</c>, reading only the
    /// attributes it returns, checks all of its folders (about 0.3 ms with the 256 fan-out folders; asking each folder for
    /// its link target took 20 ms).
    /// </summary>
    private static void RequireObjectStoreWithoutLinks(string commonDirectory)
    {
        var objects = Path.Combine(commonDirectory, "objects");
        var link = WorktreeFileSystem.HasLinkBelow(commonDirectory, objects) ? objects
            : Directory.Exists(objects) ? new DirectoryInfo(objects).EnumerateFileSystemInfos().FirstOrDefault(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint))?.FullName
            : null;
        if (link is not null
            || !ResolvesUnder(commonDirectory, objects)
            || !ResolvesUnder(commonDirectory, Path.Combine(objects, "pack"))
            || !ResolvesUnder(commonDirectory, Path.Combine(objects, "info")))
        {
            throw new WorktreeOwnershipException(
                $"The object store '{objects}' is reached through a link ('{link ?? objects}') or resolves outside '{commonDirectory}'; refusing to write objects through it.");
        }
    }

    /// <summary>
    /// P-01: Agent Studio creates its worktrees detached (<c>--detach</c>), so HEAD must still be detached at a commit when
    /// a worktree is reused and right before every commit or reset in it. Measured with git 2.45.2: with
    /// <c>ref: refs/heads/feature</c> written in the administrative HEAD, or with HEAD a symbolic link reading
    /// <c>refs/heads/feature</c>, the worktree's <c>commit</c> and <c>reset --hard</c> moved that branch of the user. HEAD
    /// is read from its file, without git: it must be no link and hold a full object id.
    /// </summary>
    private static void RequireDetachedHead(OwnedWorktree owned)
    {
        var head = Path.Combine(owned.AdminDirectory, "HEAD");
        var value = WorktreeFileSystem.HasLinkBelow(owned.AdminDirectory, head) ? null : ReadFirstLine(head)?.Trim();
        if (value is null || !GitNaming.IsObjectId(value))
        {
            throw new WorktreeOwnershipException(
                $"HEAD of the worktree '{owned.Path}' is not detached at a commit ('{head}' names a branch, is a link or cannot be read): a commit or a reset there would move what it names, and Agent Studio creates its worktrees detached; refusing to touch it.");
        }
    }

    /// <summary>
    /// Each file of <paramref name="names"/> in <paramref name="gitDirectory"/>, when it exists, must be no link (nor any
    /// folder on the way to it below <paramref name="gitDirectory"/>), have a single name, and sit in a folder that
    /// resolves under <paramref name="commonDirectory"/>; otherwise <see cref="WorktreeOwnershipException"/> is thrown
    /// before git runs. git appends to a reflog and rewrites <c>COMMIT_EDITMSG</c> and the merge files in place, so through
    /// a hard link it writes the other file too, and through a link on the way it writes outside the repository (H-03,
    /// X-02).
    /// </summary>
    private static void RequireGitFilesWithoutLinks(string gitDirectory, string commonDirectory, IEnumerable<string> names, string writer)
    {
        foreach (var name in names)
        {
            var file = Path.Combine(gitDirectory, name);
            if (WorktreeFileSystem.HasLinkBelow(gitDirectory, file)
                || WorktreeFileSystem.HasOtherNames(file)
                || !ResolvesUnder(commonDirectory, Path.GetDirectoryName(file)!, orSame: true))
            {
                throw new WorktreeOwnershipException(
                    $"'{file}', which {writer} writes, is reached through a link (a junction, a symbolic link or a second name of the file) or resolves outside '{commonDirectory}'; refusing to run it.");
            }
        }
    }

    /// <summary>
    /// Steps 3 and 4 of A-02: inspect the new worktree's EFFECTIVE configuration exactly as its checkout resolves it
    /// (explicit <c>--git-dir</c>/<c>--work-tree</c> and the worktree as working directory, so an
    /// <c>includeIf "gitdir:"</c> and a <c>config.worktree</c> the parent never sees are included), through the same
    /// allowlist; then check out the base commit with a <c>reset --hard</c> that goes through the content gate again in
    /// that same context (defence in depth, and against a race between the two). A refused worktree is dropped so none
    /// is left half-created; an interrupted creation (no index) is reported incomplete by <see cref="VerifyCompleteAsync"/>.
    /// </summary>
    private async Task CheckoutBaseIntoNewWorktreeAsync(string repoRoot, OwnedWorktree owned, string baseCommit, CancellationToken ct)
    {
        var dirFlags = new[] { "--git-dir=" + owned.AdminDirectory, "--work-tree=" + owned.Path };
        var refusals = await _git.ClassifyRepositoryCallbacksAsync(owned.Path, dirFlags, ScopeOf(owned), ct).ConfigureAwait(false);
        if (refusals.Count > 0)
        {
            await DropRegistrationAsync(repoRoot, owned.CommonDirectory, owned.ListedPath, ct).ConfigureAwait(false);
            var first = refusals[0];
            var more = refusals.Count > 1 ? $" (and {refusals.Count - 1} more)" : string.Empty;
            throw new UnsafeGitCallbackException(
                $"Refusing to check out the worktree '{owned.Path}': the configuration key '{first.Key}' ({first.Origin}) would run a program the repository or a worktree controls, because {first.Reason}{more}. "
                + "An includeIf \"gitdir:\" or a config.worktree that only the new worktree's context sees can pull one in (A-02); a git-lfs-style program installed outside the repository is allowed.",
                refusals);
        }

        // X-02 and P-01: what the reset writes in the new administrative folder, and HEAD as `worktree add --detach` left it;
        // only a concurrent process could have touched them.
        RequireAdminFilesWithoutLinks(owned, ResetWrites, "the checkout of the new worktree");
        RequireDetachedHead(owned);

        try
        {
            // reset --hard populates the working tree (running smudge filters) and is itself inspected in this same
            // context by the content gate. --no-recurse-submodules: a reset must not run a child git inside a submodule.
            await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "reset", "--hard", "--quiet", "--no-recurse-submodules", baseCommit, "--"), ct, scope: ScopeOf(owned))
                .ConfigureAwait(false);
        }
        catch (UnsafeGitCallbackException)
        {
            // The gate refused between the inspection above and the reset (a config planted in the race): leave no
            // half-created worktree behind.
            await DropRegistrationAsync(repoRoot, owned.CommonDirectory, owned.ListedPath, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stages everything except <c>.agents/</c> and commits it as "Agent Studio" (message on standard input, no hooks,
    /// no signature). Nothing to commit is not an error: <see cref="CommitResult.Committed"/> is false. Either way the
    /// hidden ref of the worktree follows HEAD, which keeps its commits alive.
    /// </summary>
    /// <exception cref="StaleRefException">The hidden ref moved while the commit was being made; it is left as it is.</exception>
    public async Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateMessage(message);

        var owned = await RequireOwnedAsync(lease, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(owned.RepositoryRoot, ct).ConfigureAwait(false);

        // H-03: the hidden ref follows HEAD at the end, so a link on the way to it (or to its reflog) is refused before
        // anything is staged or committed.
        RequireNoLinkOnRefPath(owned.CommonDirectory, lease.RefName);

        // X-02: what `add` and `commit` write in the administrative folder, checked before anything is staged; P-01: HEAD
        // still detached, so the commit cannot move a branch; and P-02: the object store both write into.
        RequireAdminFilesWithoutLinks(owned, CommitWrites, "a commit of the worktree");
        RequireDetachedHead(owned);
        RequireObjectStoreWithoutLinks(owned.CommonDirectory);

        var tipBefore = await TryReadRefAsync(owned.RepositoryRoot, lease.RefName, ct).ConfigureAwait(false);
        await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "add", "-A", "--", ".", AgentsExclude), ct, scope: ScopeOf(owned)).ConfigureAwait(false);
        await RequireNoEmbeddedRepositoryAsync(owned, ct).ConfigureAwait(false);

        // Exit code 1 means there are staged changes. No external diff or textconv of the repository runs (N-01), and no
        // child git of a submodule either (N-01).
        var staged = await _git.RunAsync(owned.Path, InWorktree(owned, "diff", "--cached", "--quiet", "--exit-code", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all"), null, ct).ConfigureAwait(false);
        if (staged.ExitCode > 1)
        {
            staged.EnsureSuccess("diff --cached");
        }

        var committed = staged.ExitCode == 1;
        if (committed)
        {
            // X-02, P-01 and P-02: and again right before the commit writes them.
            RequireAdminFilesWithoutLinks(owned, CommitWrites, "a commit of the worktree");
            RequireDetachedHead(owned);
            RequireObjectStoreWithoutLinks(owned.CommonDirectory);
            await _git.RunCheckedAsync(
                owned.Path,
                [.. InWorktree(owned), .. CommitIdentity, "commit", "--no-verify", "--quiet", "-F", "-"],
                ct,
                standardInput: WithTrailingNewline(message),
                scope: ScopeOf(owned)).ConfigureAwait(false);
        }

        var head = (await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "rev-parse", "--verify", "HEAD^{commit}"), ct).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(head, tipBefore, StringComparison.OrdinalIgnoreCase))
        {
            await CompareAndSwapRefAsync(ScopeOf(owned), lease.RefName, head, tipBefore, ct).ConfigureAwait(false);
        }

        return new CommitResult(committed, head);
    }

    /// <summary>
    /// Refuses (<see cref="EmbeddedRepositoryException"/>) when the staged tree holds a gitlink whose directory has a
    /// <c>.git</c> on disk: an embedded repository the agent planted. A later git command could spawn a child git inside
    /// it with configuration the agent controls (N-01), and a gitlink the system never verified has no place in an
    /// integration. An uninitialised submodule of the base commit (a gitlink with no <c>.git</c> on disk) is left alone.
    /// </summary>
    private async Task RequireNoEmbeddedRepositoryAsync(OwnedWorktree owned, CancellationToken ct)
    {
        var staged = await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "ls-files", "-z", "--stage"), ct).ConfigureAwait(false);
        var embedded = new List<string>();
        foreach (var record in staged.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0 || !record.StartsWith("160000 ", StringComparison.Ordinal))
            {
                continue;
            }

            var path = record[(tab + 1)..];
            var directory = Path.Combine(owned.Path, path.Replace('/', Path.DirectorySeparatorChar));
            var gitEntry = Path.Combine(directory, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
            {
                embedded.Add(path);
            }
        }

        if (embedded.Count > 0)
        {
            throw new EmbeddedRepositoryException(
                $"The worktree '{owned.Path}' holds embedded git repositories that Agent Studio refuses to commit (a later git command could run their configuration with the user's permissions): {string.Join(", ", embedded)}.",
                embedded);
        }
    }

    /// <summary>Paths that differ between two commits, repository relative with '/' separators, sorted. Renames count as a delete plus an add.</summary>
    public async Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken ct)
    {
        var root = RequireRepositoryDirectory(repoRoot);
        var left = GitNaming.ValidateRevision(from, nameof(from));
        var right = GitNaming.ValidateRevision(to, nameof(to));

        var result = await _git.RunCheckedAsync(
            root,
            ["diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all", left, right, "--"],
            ct).ConfigureAwait(false);

        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Writes the patch between two commits, byte for byte. git writes it to a temporary file (so non-UTF-8 content and
    /// large diffs survive) and the file is copied to <paramref name="destination"/>.
    /// </summary>
    public async Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var root = RequireRepositoryDirectory(repoRoot);
        var left = GitNaming.ValidateRevision(from, nameof(from));
        var right = GitNaming.ValidateRevision(to, nameof(to));

        var temporary = Path.Combine(Path.GetTempPath(), "agentstudio-diff-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            // Fixed prefixes and options: the evidence must not depend on the user's diff.* configuration.
            await _git.RunCheckedAsync(
                root,
                [
                    "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--ignore-submodules=all",
                    "--src-prefix=a/", "--dst-prefix=b/", "--output=" + temporary, left, right, "--",
                ],
                ct).ConfigureAwait(false);

            var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    /// <summary>
    /// Merges <paramref name="source"/> into the ref <paramref name="targetRef"/> (under <c>refs/agentstudio/</c> or an
    /// <c>agentstudio/</c> branch) without a worktree: <c>merge-tree --write-tree</c>, a merge commit with both parents
    /// (the message travels on standard input) and a compare-and-swap <c>update-ref</c> against
    /// <paramref name="expectedTarget"/>. Idempotent: a source that is already part of the target returns
    /// <see cref="MergeAttempt.AlreadyMerged"/>. A conflict is reported with its paths and nothing is written to any ref,
    /// index or working tree; it is never resolved here.
    /// </summary>
    /// <exception cref="StaleRefException">The target is not at <paramref name="expectedTarget"/> (also when it moved during the merge).</exception>
    public async Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken ct)
    {
        ValidateMessage(message);
        var target = GitNaming.NormalizeAgentStudioRef(targetRef, nameof(targetRef));
        var expected = GitNaming.NormalizeObjectId(expectedTarget, nameof(expectedTarget));
        var sourceId = GitNaming.NormalizeObjectId(source, nameof(source));

        await RequireSupportedGitAsync(ct).ConfigureAwait(false);
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(root, ct).ConfigureAwait(false);

        // H-03: the target is moved at the end, so a link on the way to it (or to its reflog) is refused before merging; and
        // P-02: merge-tree writes the merged tree, and commit-tree the commit, into the object store.
        var repository = await OpenUserRepositoryAsync(root, ct).ConfigureAwait(false);
        var commonDirectory = repository.CommonDirectory;
        RequireNoLinkOnRefPath(commonDirectory, target);
        RequireObjectStoreWithoutLinks(commonDirectory);

        var sourceCommit = await RequireCommitAsync(root, sourceId, ct).ConfigureAwait(false);
        var expectedCommit = await RequireCommitAsync(root, expected, ct).ConfigureAwait(false);
        var current = await TryResolveRefAsync(root, target, ct).ConfigureAwait(false)
            ?? throw new GitCommandException($"The target ref '{target}' does not exist.", 1, string.Empty);

        // Idempotency: a previous call may have completed (even if its caller never learned about it).
        if (await IsAncestorCoreAsync(root, sourceCommit, current, ct).ConfigureAwait(false))
        {
            return new MergeAttempt(false, true, current, [], string.Empty);
        }

        if (!string.Equals(current, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleRefException(target, expectedCommit, current);
        }

        // Exit code 1 means conflicts, but git also uses it for errors such as an unknown revision: only an output that
        // starts with a tree id is a merge result.
        var merge = await _git.RunAsync(
            root,
            ["merge-tree", "--write-tree", "--name-only", "--messages", "-z", expectedCommit, sourceCommit],
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        if (merge.ExitCode > 1 || !GitPorcelain.TryParseMergeTree(merge.StandardOutput, out var parsed) || parsed is null)
        {
            throw new GitCommandException($"git merge-tree failed with exit code {merge.ExitCode}: {merge.StandardError.Trim()}", merge.ExitCode, merge.StandardError);
        }

        if (merge.ExitCode == 1)
        {
            return new MergeAttempt(false, false, null, parsed.ConflictedPaths, parsed.Messages);
        }

        RequireObjectStoreWithoutLinks(commonDirectory);
        var commit = (await _git.RunCheckedAsync(
            root,
            [.. CommitIdentity, "commit-tree", parsed.TreeId, "-p", expectedCommit, "-p", sourceCommit],
            ct,
            standardInput: WithTrailingNewline(message),
            scope: ScopeOf(repository)).ConfigureAwait(false)).StandardOutput.Trim();

        await CompareAndSwapRefAsync(ScopeOf(repository), target, commit, expectedCommit, ct).ConfigureAwait(false);
        return new MergeAttempt(true, false, commit, [], parsed.Messages);
    }

    /// <summary>
    /// Puts an owned worktree exactly on <paramref name="commit"/>: detached HEAD, tracked files reset, untracked and
    /// ignored files (nested repositories included) removed, but <c>.agents/</c>. Whatever the worktree contained is
    /// discarded. Links inside the worktree are removed first, as links: git writes and deletes through them.
    /// </summary>
    public async Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var target = GitNaming.NormalizeObjectId(commit, nameof(commit));

        var owned = await RequireOwnedAsync(lease, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(owned.RepositoryRoot, ct).ConfigureAwait(false);

        var resolved = await RequireCommitAsync(owned.RepositoryRoot, target, ct).ConfigureAwait(false);
        WorktreeFileSystem.RemoveLinks(owned.Path);

        // X-02: what the reset writes in the administrative folder (`clean` writes nothing there, measured); P-01: HEAD
        // still detached, so the reset cannot move a branch.
        RequireAdminFilesWithoutLinks(owned, ResetWrites, "a reset of the worktree");
        RequireDetachedHead(owned);

        // --no-recurse-submodules: an agent can register an active submodule, and then reset would run a child git inside
        // it with the submodule's configuration (measured with git 2.45.2). clean -x removes any nested repository.
        await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "reset", "--hard", "--quiet", "--no-recurse-submodules", resolved, "--"), ct, scope: ScopeOf(owned)).ConfigureAwait(false);
        await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "clean", "-f", "-f", "-d", "-x", "--quiet", "-e", ".agents/"), ct, scope: ScopeOf(owned)).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes an owned worktree (its refs are kept). With uncommitted changes (ignoring <c>.agents/</c>) nothing is
    /// removed and <see cref="RemovalOutcome.RefusedDirty"/> is returned, unless <paramref name="discardChanges"/> is set:
    /// the approval for that belongs to the caller.
    /// </summary>
    /// <remarks>
    /// The folder is first moved aside in one rename, which either moves everything or nothing (a file in use stops
    /// it); only then does git drop the registration, and Agent Studio deletes the folder without following links.
    /// What an interrupted removal left (a folder at this worktree's path that git no longer knows) is deleted too,
    /// but only when discarding: its state cannot be checked.
    /// </remarks>
    /// <exception cref="IOException">The worktree is in use; nothing was removed and it stays registered and locked.</exception>
    public async Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var path = RequireLeasePath(lease);
        if (!Directory.Exists(path))
        {
            return RemovalOutcome.AlreadyRemoved;
        }

        var repoRoot = RequireLeaseRepository(lease);
        RequireNoLinkOnTheWay(path);
        RequireLeaseNames(lease, path);

        using var gate = await AcquireGateAsync(repoRoot, ct).ConfigureAwait(false);
        var repository = await OpenRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        if (repository.LinkedAt(path).Count == 0)
        {
            return await RemoveLeftoverAsync(repository, lease, path, discardChanges, ct).ConfigureAwait(false);
        }

        var owned = VerifyRegistration(repository, path, lease.Run, lease.Name);
        RequireSameAdministrativeDirectory(lease, owned);

        if (!discardChanges)
        {
            var status = await _git.RunCheckedAsync(owned.Path, InWorktree(owned, "status", "--porcelain=v1", "-z", "--ignore-submodules=all", "--", ".", AgentsExclude), ct, scope: ScopeOf(owned))
                .ConfigureAwait(false);
            if (status.StandardOutput.Length > 0)
            {
                return RemovalOutcome.RefusedDirty;
            }
        }

        var aside = await MoveAsideAsync(owned.Path, ct).ConfigureAwait(false);

        // The folder is gone, so git only deletes the registration and its lock (it never sees a link to follow), and
        // VerifyRegistration already refused an administrative folder reached through a link.
        await DropRegistrationAsync(owned.RepositoryRoot, owned.CommonDirectory, owned.ListedPath, ct).ConfigureAwait(false);
        await TryDeleteAsideAsync(aside).ConfigureAwait(false);
        TryDeleteEmptyParents(owned.Path);
        return RemovalOutcome.Removed;
    }

    /// <summary>
    /// The worktrees of <paramref name="project"/> that Agent Studio owns: registered in the project's repository, at the
    /// path Agent Studio uses for them and locked with <c>agent-studio:&lt;projectId&gt;:&lt;run&gt;:&lt;name&gt;</c>.
    /// <see cref="WorktreeLease.BaseCommit"/> comes from the hidden start ref; for a worktree without one it is the
    /// current HEAD.
    /// </summary>
    public async Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        GitNaming.ValidateProjectId(project.ProjectId);

        var repoRoot = await ResolveRepositoryRootAsync(project.RootPath, ct).ConfigureAwait(false);
        var repository = await OpenRepositoryAsync(repoRoot, ct).ConfigureAwait(false);

        var refs = await _git.RunCheckedAsync(repoRoot, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/agentstudio/"], ct).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[line[(separator + 1)..]] = line[..separator];
            }
        }

        var leases = new List<WorktreeLease>();
        foreach (var entry in repository.Entries.Skip(1))
        {
            var path = GitNaming.NormalizePath(entry.Path);
            if (!GitNaming.IsUnder(path, _worktreeRoot)
                || !Directory.Exists(path)
                || !GitNaming.TryParseLockReason(entry.LockReason, out var parts)
                || !string.Equals(parts.ProjectId, project.ProjectId, StringComparison.Ordinal))
            {
                continue;
            }

            var run = new RunId(parts.Run);
            var administrative = FindAdministrativeDirectories(repository.CommonDirectory, path);
            if (!GitNaming.SamePath(path, GitNaming.WorktreePath(_worktreeRoot, repoRoot, run, parts.Name)) || administrative.Count != 1)
            {
                continue;
            }

            var baseCommit = values.TryGetValue(GitNaming.StartRef(run, parts.Name), out var recorded) ? recorded : entry.Head ?? string.Empty;
            leases.Add(new WorktreeLease(path, parts.Name, GitNaming.TipRef(run, parts.Name), baseCommit, run, repoRoot, administrative[0]));
        }

        return leases.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Creates a ref under the Agent Studio namespace (for example the integration branch <c>agentstudio/run-&lt;run&gt;</c>)
    /// at <paramref name="commit"/> if it does not exist, and returns the commit it points to. An existing ref at another
    /// commit fails closed. Not part of <see cref="IWorktreeManager"/>: the integrator creates its target with
    /// <see cref="EnsureRunBranchAsync"/>.
    /// </summary>
    public async Task<string> EnsureRefAsync(string repoRoot, string refName, string commit, CancellationToken ct)
    {
        var name = GitNaming.NormalizeAgentStudioRef(refName, nameof(refName));
        var requested = GitNaming.NormalizeObjectId(commit, nameof(commit));

        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(root, ct).ConfigureAwait(false);

        // H-03: a ref reached through a link (or whose reflog is) is refused before it is written.
        var repository = await OpenUserRepositoryAsync(root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository.CommonDirectory, name);

        var resolved = await RequireCommitAsync(root, requested, ct).ConfigureAwait(false);
        var existing = await TryResolveRefAsync(root, name, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateRefIfMissingAsync(ScopeOf(repository), name, resolved, ct).ConfigureAwait(false);
            return resolved;
        }

        if (!string.Equals(existing, resolved, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorktreeMismatchException($"The ref '{name}' already exists at {existing}, not at {resolved}.");
        }

        return existing;
    }

    // ---- refs of the Agent Studio namespace -----------------------------------------------------------------

    /// <summary>
    /// The commit a full object id or a ref of the Agent Studio namespace points at, or null when it does not exist.
    /// Anything else is refused before git runs (<see cref="GitNaming.NormalizeReadableRevision"/>).
    /// </summary>
    public async Task<string?> ResolveCommitAsync(string repoRoot, string revision, CancellationToken ct)
    {
        var name = GitNaming.NormalizeReadableRevision(revision, nameof(revision));
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        return await TryResolveCommitAsync(repository, name, ct).ConfigureAwait(false);
    }

    /// <summary><c>merge-base --is-ancestor</c>: exit code 0 is yes and 1 is no; anything else, such as an unknown commit, is an error.</summary>
    public async Task<bool> IsAncestorAsync(string repoRoot, string ancestor, string descendant, CancellationToken ct)
    {
        var left = GitNaming.NormalizeReadableRevision(ancestor, nameof(ancestor));
        var right = GitNaming.NormalizeReadableRevision(descendant, nameof(descendant));
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        return await IsAncestorInAsync(repository, left, right, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The run's branch (<see cref="GitNaming.RunBranchRef"/>): created at <paramref name="baseCommit"/>, as a
    /// compare-and-swap from "missing" that never follows a symbolic ref, when it does not exist; otherwise left where
    /// it is. Returns the commit it points at.
    /// </summary>
    public async Task<string> EnsureRunBranchAsync(string repoRoot, RunId run, string baseCommit, CancellationToken ct)
    {
        GitNaming.ValidateRun(run);
        var requested = GitNaming.NormalizeObjectId(baseCommit, nameof(baseCommit));
        var branch = GitNaming.RunBranchRef(run);

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, branch);
        if (await TryResolveCommitAsync(repository, branch, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var resolved = await TryResolveCommitAsync(repository, requested, ct).ConfigureAwait(false)
            ?? throw new GitCommandException($"Commit {requested} does not exist in '{repository.Root}'.", 1, string.Empty);

        // The all-zero id as the old value means "create only if it does not exist".
        var create = await _git.RunAsync(
            repository.Root,
            InRepository(repository, "update-ref", "--no-deref", "--end-of-options", branch, resolved, ZeroId(resolved)),
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        var current = await TryResolveCommitAsync(repository, branch, ct).ConfigureAwait(false);
        if (current is null)
        {
            create.EnsureSuccess("update-ref");
            throw new GitCommandException($"The branch '{branch}' could not be created.", create.ExitCode, create.StandardError);
        }

        // Created now, or by someone else a moment ago: either way it is where the caller finds it.
        return current;
    }

    /// <summary>
    /// The run's branch and the hidden refs of the run, as <c>for-each-ref</c> lists them, sorted by name. Fails closed
    /// (<see cref="WorktreeOwnershipException"/>) when one of them, or a folder on the way to it, is a link.
    /// </summary>
    public async Task<IReadOnlyList<RefEntry>> ListRunRefsAsync(string repoRoot, RunId run, CancellationToken ct)
    {
        GitNaming.ValidateRun(run);
        var prefix = GitNaming.RunRefPrefix(run);
        var branch = GitNaming.RunBranchRef(run);

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, prefix.TrimEnd('/'));
        RequireNoLinkOnRefPath(repository, branch);

        var listing = await _git.RunCheckedAsync(
            repository.Root,
            InRepository(repository, "for-each-ref", "--format=%(objectname) %(refname)", "--end-of-options", prefix, branch),
            ct).ConfigureAwait(false);

        var refs = new List<RefEntry>();
        foreach (var line in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var commit = line[..separator];
            var name = line[(separator + 1)..];

            // git applies the patterns; this keeps a surprising answer from ever reaching a delete.
            if (!GitNaming.IsObjectId(commit)
                || !(string.Equals(name, branch, StringComparison.Ordinal) || name.StartsWith(prefix, StringComparison.Ordinal))
                || !IsWellFormedAgentStudioRef(name))
            {
                continue;
            }

            RequireNoLinkOnRefPath(repository, name);
            refs.Add(new RefEntry(name, commit.ToLowerInvariant()));
        }

        return refs.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Deletes a ref of the Agent Studio namespace with <c>update-ref --no-deref -d &lt;ref&gt; &lt;expected&gt;</c>: a
    /// compare-and-swap that deletes a symbolic ref itself, never the ref it names. False when the ref moved or was
    /// already gone. A ref reached through a link is refused (<see cref="WorktreeOwnershipException"/>).
    /// </summary>
    public async Task<bool> DeleteRefAsync(string repoRoot, string refName, string expectedCommit, CancellationToken ct)
    {
        var name = GitNaming.NormalizeAgentStudioRef(refName, nameof(refName));
        var expected = GitNaming.NormalizeObjectId(expectedCommit, nameof(expectedCommit));

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, name);

        var delete = await _git.RunAsync(repository.Root, InRepository(repository, "update-ref", "--no-deref", "-d", "--end-of-options", name, expected), null, ct, ScopeOf(repository))
            .ConfigureAwait(false);
        if (delete.ExitCode == 0)
        {
            return true;
        }

        // git exits with 1 both when the ref moved and when it is already gone: neither deletes anything.
        var current = await TryResolveCommitAsync(repository, name, ct).ConfigureAwait(false);
        if (current is null || !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        delete.EnsureSuccess("update-ref");
        return false;
    }

    // ---- the copy of a commit for an isolated verification -----------------------------------------------------

    /// <summary>
    /// Writes the tree of <paramref name="commit"/> into the empty folder <paramref name="destination"/> (ADR-0017): the
    /// copy an isolated verification runs on.
    /// <list type="bullet">
    /// <item>The objects come from the project's repository as this layer finds it from <paramref name="repoRoot"/>
    /// (never a folder of the worktree root), never from the <c>.git</c> file of a worktree, which an agent can rewrite to
    /// name an object store of its own: git checks the hash of the commit it archives, not that of every blob it reads.</item>
    /// <item><c>git archive</c> runs with an explicit <c>--git-dir</c> naming a private, empty repository whose only objects
    /// are those of the project's common directory (an alternate), with the environment and the settings of every command
    /// of this layer (no hooks) and <c>--end-of-options</c> before the commit. So no configuration, hook or filter of the
    /// repository runs, and its <c>info/attributes</c> turns off <c>export-ignore</c>, <c>export-subst</c>, <c>filter</c>
    /// and <c>ident</c>: the copy is the commit, not what its attributes would make of it. Only the end-of-line settings of
    /// the repository (<c>core.autocrlf</c>, <c>core.eol</c>) apply, as in a checkout.</item>
    /// <item>The archive goes to a temporary file and <see cref="TarSnapshotExtractor"/> writes it: only regular files and
    /// folders, inside the destination. The commit the archive recorded must be the one asked.</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">The commit is not a full object id, or the destination is not an empty folder outside the repository; git did not run.</exception>
    /// <exception cref="GitCommandException">The repository does not hold the commit, or git failed.</exception>
    /// <exception cref="InvalidDataException">The tree holds what a copy refuses (a link, a device, a path out of the folder), or the archive is of another commit.</exception>
    public async Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken ct)
    {
        var requested = GitNaming.NormalizeObjectId(commit, nameof(commit));
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var target = GitNaming.NormalizePath(destination);
        if (!Directory.Exists(target) || Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new ArgumentException($"The copy of a commit is written into an existing, empty folder, and '{target}' is not one.", nameof(destination));
        }

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        if (GitNaming.SamePath(target, repository.Root) || GitNaming.IsUnder(target, repository.Root)
            || GitNaming.SamePath(target, repository.CommonDirectory) || GitNaming.IsUnder(target, repository.CommonDirectory))
        {
            throw new ArgumentException($"The copy of a commit is never written inside the repository '{repository.Root}'.", nameof(destination));
        }

        var resolved = await TryResolveCommitAsync(repository, requested, ct).ConfigureAwait(false)
            ?? throw new GitCommandException($"Commit {requested} does not exist in '{repository.Root}'.", 1, string.Empty);
        var endOfLine = await ReadEndOfLineSettingsAsync(repository, ct).ConfigureAwait(false);

        var work = Path.Combine(Path.GetTempPath(), "agentstudio-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var gitDirectory = CreateArchiveRepository(work, repository, resolved);
            var archive = Path.Combine(work, "commit.tar");
            await _git.RunCheckedAsync(
                work,
                ["--git-dir=" + gitDirectory, .. endOfLine, "archive", "--format=tar", "--output=" + archive, "--end-of-options", resolved],
                ct,
                scope: ScopeOf(repository)).ConfigureAwait(false);

            var snapshot = await Task.Run(
                () =>
                {
                    using var tar = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                    return TarSnapshotExtractor.Extract(tar, target, TarSnapshotLimits.Default);
                },
                ct).ConfigureAwait(false);
            if (!string.Equals(snapshot.Comment, resolved, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"git archive recorded commit '{snapshot.Comment}' instead of {resolved}; the copy is not that commit.");
            }
        }
        finally
        {
            TryDeleteTree(work);
        }
    }

    /// <summary>The effective <c>core.autocrlf</c> and <c>core.eol</c> of the repository, as <c>-c</c> options, so the copy has the line endings a checkout has.</summary>
    private async Task<string[]> ReadEndOfLineSettingsAsync(UserRepository repository, CancellationToken ct)
    {
        var result = await _git.RunAsync(repository.Root, InRepository(repository, "config", "--get-regexp", "--end-of-options", @"^core\.(autocrlf|eol)$"), null, ct)
            .ConfigureAwait(false);
        if (result.ExitCode > 1)
        {
            result.EnsureSuccess("config");
        }

        var options = new List<string>();
        foreach (var line in result.ExitCode == 0 ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [])
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            var value = parts.Length == 2 ? parts[1].ToLowerInvariant() : "true";
            var setting = parts[0].ToLowerInvariant() switch
            {
                "core.autocrlf" => value switch
                {
                    "true" or "yes" or "on" or "1" => "core.autocrlf=true",
                    "false" or "no" or "off" or "0" or "" => "core.autocrlf=false",
                    "input" => "core.autocrlf=input",
                    _ => null,
                },
                "core.eol" => value is "lf" or "crlf" or "native" ? "core.eol=" + value : null,
                _ => null,
            };
            if (setting is not null)
            {
                options.Add("-c");
                options.Add(setting);
            }
        }

        return [.. options];
    }

    /// <summary>
    /// An empty bare repository whose only objects are those of the project's common directory: git reads no configuration,
    /// hook or attribute of the project's repository through it, and its own attributes make the copy exact.
    /// </summary>
    private static string CreateArchiveRepository(string work, UserRepository repository, string commit)
    {
        var path = Path.Combine(work, "git");
        Directory.CreateDirectory(Path.Combine(path, "objects", "info"));
        Directory.CreateDirectory(Path.Combine(path, "refs"));
        Directory.CreateDirectory(Path.Combine(path, "info"));
        var sha256 = commit.Length == 64;
        File.WriteAllText(Path.Combine(path, "HEAD"), "ref: refs/heads/main\n", Utf8NoBom);
        File.WriteAllText(
            Path.Combine(path, "config"),
            $"[core]\n\trepositoryformatversion = {(sha256 ? 1 : 0)}\n\tbare = true\n" + (sha256 ? "[extensions]\n\tobjectformat = sha256\n" : string.Empty),
            Utf8NoBom);
        File.WriteAllText(Path.Combine(path, "objects", "info", "alternates"), Path.Combine(repository.CommonDirectory, "objects").Replace('\\', '/') + "\n", Utf8NoBom);
        File.WriteAllText(Path.Combine(path, "info", "attributes"), "* -export-ignore -export-subst -filter -ident\n", Utf8NoBom);
        return path;
    }

    // ---- the candidate of an integration and the fast-forward of the run's branch -------------------------------

    /// <summary>
    /// The candidate of an integration task (<see cref="GitNaming.CandidateRef"/>): created at <paramref name="startCommit"/>
    /// when missing, kept when it already holds it (it is that commit or descends from it: the merges of an earlier attempt),
    /// and otherwise moved back to it. Every write is a compare-and-swap that never follows a symbolic ref, and a candidate
    /// reached through a link is refused (<see cref="WorktreeOwnershipException"/>). The run's branch is never touched.
    /// </summary>
    /// <exception cref="StaleRefException">The candidate moved while it was being prepared.</exception>
    public async Task<string> PrepareCandidateAsync(string repoRoot, RunId run, TaskId task, string startCommit, CancellationToken ct)
    {
        GitNaming.ValidateRun(run);
        GitNaming.ValidateTask(task);
        var start = GitNaming.NormalizeObjectId(startCommit, nameof(startCommit));
        var candidate = GitNaming.CandidateRef(run, task);

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, candidate);

        var resolved = await RequireCommitInAsync(repository, start, ct).ConfigureAwait(false);
        var (value, current) = await ReadRefForSwapAsync(repository, candidate, ct).ConfigureAwait(false);
        if (current is not null
            && (string.Equals(current, resolved, StringComparison.OrdinalIgnoreCase) || await IsAncestorInAsync(repository, resolved, current, ct).ConfigureAwait(false)))
        {
            return current;
        }

        await SwapRefAsync(repository, candidate, resolved, value, ct).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Merges <paramref name="source"/> into the candidate of an integration task without a worktree, as
    /// <see cref="MergeIntoAsync"/> does for any ref, with the hardening of the newer ref operations: every command names
    /// the git directory and ends its options before any revision, a candidate reached through a link is refused, and the
    /// target comes from the run and the task, so a merge never moves the run's branch. A source already in the candidate
    /// is a no-op with the same commit; a conflict is reported with its paths and nothing is written.
    /// </summary>
    /// <exception cref="StaleRefException">The candidate is not at <paramref name="expectedCandidate"/>: missing, moved, or moved during the merge.</exception>
    public async Task<MergeAttempt> MergeIntoCandidateAsync(string repoRoot, RunId run, TaskId task, string expectedCandidate, string source, string message, CancellationToken ct)
    {
        ValidateMessage(message);
        GitNaming.ValidateRun(run);
        GitNaming.ValidateTask(task);
        var expected = GitNaming.NormalizeObjectId(expectedCandidate, nameof(expectedCandidate));
        var sourceId = GitNaming.NormalizeObjectId(source, nameof(source));
        var candidate = GitNaming.CandidateRef(run, task);

        await RequireSupportedGitAsync(ct).ConfigureAwait(false);
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, candidate);

        // P-02: merge-tree writes the merged tree, and commit-tree the commit, into the object store.
        RequireObjectStoreWithoutLinks(repository.CommonDirectory);

        var sourceCommit = await RequireCommitInAsync(repository, sourceId, ct).ConfigureAwait(false);
        var expectedCommit = await RequireCommitInAsync(repository, expected, ct).ConfigureAwait(false);
        var (value, current) = await ReadRefForSwapAsync(repository, candidate, ct).ConfigureAwait(false);
        if (value is null || current is null)
        {
            throw new StaleRefException(candidate, expectedCommit, value);
        }

        // Idempotency: an earlier attempt may have merged it already, even if it never learned about it.
        if (await IsAncestorInAsync(repository, sourceCommit, current, ct).ConfigureAwait(false))
        {
            return new MergeAttempt(false, true, current, [], string.Empty);
        }

        if (!string.Equals(current, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleRefException(candidate, expectedCommit, current);
        }

        // Exit code 1 means conflicts, but git also uses it for errors such as an unknown revision: only an output that
        // starts with a tree id is a merge result.
        var merge = await _git.RunAsync(
            repository.Root,
            InRepository(repository, "merge-tree", "--write-tree", "--name-only", "--messages", "-z", "--end-of-options", expectedCommit, sourceCommit),
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        if (merge.ExitCode > 1 || !GitPorcelain.TryParseMergeTree(merge.StandardOutput, out var parsed) || parsed is null)
        {
            throw new GitCommandException($"git merge-tree failed with exit code {merge.ExitCode}: {merge.StandardError.Trim()}", merge.ExitCode, merge.StandardError);
        }

        if (merge.ExitCode == 1)
        {
            return new MergeAttempt(false, false, null, parsed.ConflictedPaths, parsed.Messages);
        }

        RequireObjectStoreWithoutLinks(repository.CommonDirectory);
        var created = await _git.RunCheckedAsync(
            repository.Root,
            [.. InRepository(repository), .. CommitIdentity, "commit-tree", "-p", expectedCommit, "-p", sourceCommit, "--end-of-options", parsed.TreeId],
            ct,
            standardInput: WithTrailingNewline(message),
            scope: ScopeOf(repository)).ConfigureAwait(false);
        var commit = created.StandardOutput.Trim();
        if (!GitNaming.IsObjectId(commit))
        {
            throw new GitCommandException($"git commit-tree answered '{commit}', which is not a commit id.", created.ExitCode, created.StandardError);
        }

        commit = commit.ToLowerInvariant();
        await SwapRefAsync(repository, candidate, commit, value, ct).ConfigureAwait(false);
        return new MergeAttempt(true, false, commit, [], parsed.Messages);
    }

    /// <summary>
    /// Moves the run's branch (<see cref="GitNaming.RunBranchRef"/>) forward from <paramref name="expectedHead"/> to
    /// <paramref name="newHead"/>: the only way an integration moves it, once its candidate was verified (ADR-0012). It is
    /// refused before anything is written when <paramref name="newHead"/> does not descend from
    /// <paramref name="expectedHead"/> (<see cref="NotFastForwardException"/>) or the branch is not at
    /// <paramref name="expectedHead"/> (<see cref="StaleRefException"/>), and the compare-and-swap catches a branch that
    /// moves after those checks. A branch already at <paramref name="newHead"/> is left as it is: an earlier call completed.
    /// A branch reached through a link is refused (<see cref="WorktreeOwnershipException"/>).
    /// </summary>
    public async Task FastForwardRunBranchAsync(string repoRoot, RunId run, string expectedHead, string newHead, CancellationToken ct)
    {
        GitNaming.ValidateRun(run);
        var expected = GitNaming.NormalizeObjectId(expectedHead, nameof(expectedHead));
        var target = GitNaming.NormalizeObjectId(newHead, nameof(newHead));
        var branch = GitNaming.RunBranchRef(run);

        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);
        RequireNoLinkOnRefPath(repository, branch);

        var from = await RequireCommitInAsync(repository, expected, ct).ConfigureAwait(false);
        var to = await RequireCommitInAsync(repository, target, ct).ConfigureAwait(false);
        var (value, current) = await ReadRefForSwapAsync(repository, branch, ct).ConfigureAwait(false);
        if (string.Equals(current, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!await IsAncestorInAsync(repository, from, to, ct).ConfigureAwait(false))
        {
            throw new NotFastForwardException(branch, from, to);
        }

        if (value is null || !string.Equals(current, from, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleRefException(branch, from, current);
        }

        await SwapRefAsync(repository, branch, to, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What a ref holds: the object it points at, unpeeled (the old value of a compare-and-swap), and the commit that object
    /// is. Both are null when the ref does not exist; the commit is null when it is not a commit.
    /// </summary>
    private async Task<(string? Value, string? Commit)> ReadRefForSwapAsync(UserRepository repository, string refName, CancellationToken ct)
    {
        var result = await _git.RunAsync(repository.Root, InRepository(repository, "rev-parse", "--verify", "--quiet", "--end-of-options", refName), null, ct)
            .ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return (null, null);
        }

        result.EnsureSuccess("rev-parse");
        var value = result.StandardOutput.Trim();
        if (!GitNaming.IsObjectId(value))
        {
            throw new GitCommandException($"git rev-parse answered '{value}', which is not an object id.", result.ExitCode, result.StandardError);
        }

        return (value.ToLowerInvariant(), await TryResolveCommitAsync(repository, refName, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>update-ref --no-deref</c> of a ref of the Agent Studio namespace (already checked for links) from
    /// <paramref name="expected"/> to <paramref name="value"/>; a null <paramref name="expected"/> means it must not exist.
    /// </summary>
    /// <exception cref="StaleRefException">The ref no longer holds <paramref name="expected"/>.</exception>
    private async Task SwapRefAsync(UserRepository repository, string refName, string value, string? expected, CancellationToken ct)
    {
        var name = GitNaming.NormalizeAgentStudioRef(refName, nameof(refName));
        RequireNoLinkOnRefPath(repository, name);
        var update = await _git.RunAsync(
            repository.Root,
            InRepository(repository, "update-ref", "--no-deref", "--end-of-options", name, value, expected ?? ZeroId(value)),
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        if (update.ExitCode == 0)
        {
            return;
        }

        // A ref that moved is a stale expectation; anything else is a real failure.
        var (now, _) = await ReadRefForSwapAsync(repository, name, ct).ConfigureAwait(false);
        if (!string.Equals(now, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleRefException(name, expected ?? ZeroId(value), now);
        }

        update.EnsureSuccess("update-ref");
    }

    private async Task<string> RequireCommitInAsync(UserRepository repository, string objectId, CancellationToken ct) =>
        await TryResolveCommitAsync(repository, objectId, ct).ConfigureAwait(false)
            ?? throw new GitCommandException($"Commit {objectId} does not exist in '{repository.Root}'.", 1, string.Empty);

    /// <summary>
    /// What <see cref="DeliverToUserBranchAsync"/> would refuse before spending an approval: the same allowlist
    /// classifier the delivery merge runs through (R3-05), applied to the user's effective configuration. Read-only and
    /// without the repository's gate. <paramref name="head"/> and <paramref name="commit"/> are validated as object ids
    /// the repository holds (so the caller fails the same way the delivery would), but the result does not depend on the
    /// diff: an in-repository callback is refused whether or not the delivered diff touches it.
    /// </summary>
    /// <exception cref="WorktreeOwnershipException">
    /// The git directory holds a link anywhere, a file with a second name outside the object stores, or a folder that
    /// cannot be listed (the walk of S-01 to S-03); and, as a second line, a file the merge writes (the branch HEAD is on and
    /// its reflog, the index, <c>logs/HEAD</c>, <c>ORIG_HEAD</c>, the merge files), the object store or the git-lfs storage
    /// is reached through a link (H-03, P-02, G-01, G-04): refused here, before an approval is spent, as it is again right
    /// before the merge.
    /// </exception>
    public async Task<IReadOnlyList<DeliveryRefusal>> FindDeliveryRefusalsAsync(string repoRoot, string head, string commit, CancellationToken ct)
    {
        var expectedHead = GitNaming.NormalizeObjectId(head, nameof(head));
        var delivered = GitNaming.NormalizeObjectId(commit, nameof(commit));
        var repository = await OpenUserRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        await RequireCommitInAsync(repository, expectedHead, ct).ConfigureAwait(false);
        await RequireCommitInAsync(repository, delivered, ct).ConfigureAwait(false);

        // The delivery goes into the branch HEAD is on: DeliveryService asks for exactly that branch.
        RequireDeliveryWithoutLinks(repository, (await ReadUserHeadAsync(repository, ct).ConfigureAwait(false)).Branch);

        var refusals = await _git.ClassifyRepositoryCallbacksAsync(repository.Root, InWorkingTree(repository), ScopeOf(repository), ct).ConfigureAwait(false);
        return [.. refusals.Select(r => new DeliveryRefusal(r.Key, r.Origin, r.Reason))];
    }

    // What the delivery merge writes in the git directory besides the branch and its reflog (measured with git 2.45.2): a
    // fast-forward appends to logs/HEAD and writes ORIG_HEAD; `merge --no-ff --no-commit` writes MERGE_HEAD, MERGE_MSG,
    // MERGE_MODE and AUTO_MERGE, and its `commit -F -` writes COMMIT_EDITMSG. Both write the index through its lock file,
    // which follows a symbolic link (G-04, measured: `merge --ff-only` and `merge --no-ff --no-commit` wrote the index of a
    // file outside the repository through `.git/index` as a symbolic link; through a hard link, the rename left the other
    // file as it was, and a second name is refused anyway).
    private static readonly string[] DeliveryGitFiles =
        ["index", "ORIG_HEAD", "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "AUTO_MERGE", "COMMIT_EDITMSG", Path.Combine("logs", "HEAD")];

    /// <summary>
    /// H-03 for the delivery: nothing the merge writes may be reached through a link. Measured with git 2.45.2: through a
    /// junction in <c>logs/refs/heads</c> or in a parent folder of the branch (<c>refs/heads/feature</c> for
    /// <c>feature/x</c>), a fast-forward wrote the reflog or the branch outside the repository; through a hard link, git
    /// appended to <c>logs/HEAD</c> and to the branch's reflog, and overwrote <c>MERGE_MSG</c>, <c>MERGE_MODE</c> and
    /// <c>COMMIT_EDITMSG</c>; and the index through a symbolic link (G-04). Since S-01 to S-03 the walk of the whole git
    /// directory runs first (<see cref="WorktreeFileSystem.RequireGitDirectoriesWithoutLinks"/>), before the approval and
    /// before each command of the merge, and what follows is its second line: the branch (when HEAD is on one) goes through
    /// <see cref="RequireNoLinkOnRefPath(string, string)"/>, and each file of <see cref="DeliveryGitFiles"/>, when it
    /// exists, must be no link, have a single name, and sit in a folder that resolves under the common directory.
    /// </summary>
    private static void RequireDeliveryWithoutLinks(UserRepository repository, string? targetBranch)
    {
        WorktreeFileSystem.RequireGitDirectoriesWithoutLinks(repository.CommonDirectory, repository.GitDirectory);

        if (targetBranch is not null)
        {
            RequireNoLinkOnRefPath(repository.CommonDirectory, targetBranch);
        }

        RequireGitFilesWithoutLinks(repository.GitDirectory, repository.CommonDirectory, DeliveryGitFiles, "the delivery merge");

        // P-02: a merge commit writes its tree and its commit into the object store.
        RequireObjectStoreWithoutLinks(repository.CommonDirectory);

        // G-01: the merge runs the git-lfs smudge of every delivered lfs file, which writes into the git-lfs storage. The
        // content gate checks it again before the merge itself; this call is what the check before the approval runs.
        WorktreeFileSystem.RequireLfsStorageWithoutLinks(repository.CommonDirectory);
    }

    // ---- delivery to the user's branch ------------------------------------------------------------------------

    /// <summary>
    /// The approved delivery into the user's branch (ADR-0012), the only operation that changes it. In this order, under
    /// the repository's gate: the approval (otherwise <see cref="DeliveryNotApprovedException"/>, before anything runs);
    /// HEAD still on the approved branch at the approved commit (otherwise
    /// <see cref="UserBranchDeliveryStatus.RepositoryChanged"/>); for a merge commit, no conflict (otherwise
    /// <see cref="UserBranchDeliveryStatus.Conflicts"/>); no change, untracked or ignored file of the user in a path the
    /// delivery changes (otherwise <see cref="UserBranchDeliveryStatus.DirtyPaths"/>); then git. Nothing autostashes,
    /// resets or forces: git aborts by itself.
    /// </summary>
    /// <remarks>
    /// The merge runs like any other content command (R3-05): the same allowlist classifier (so an in-repository callback
    /// is refused with <see cref="UnsafeGitCallbackException"/> before anything is delivered), <c>core.hooksPath</c>
    /// pointed at the owned empty file so NO hook runs (neither the repository's nor one git-lfs would install during the
    /// merge), and <c>commit.gpgsign=false</c>/<c>tag.gpgsign=false</c>
    /// so it is not signed. The user's identity (<c>user.name</c>/<c>user.email</c>) stays, because it is the user's merge.
    /// <see cref="DeliveryMode.FastForwardOnly"/> is <c>git merge --ff-only --no-autostash</c>.
    /// <see cref="DeliveryMode.MergeCommit"/> is <c>git merge --no-ff --no-commit --no-autostash</c> and then
    /// <c>git commit -F -</c>: <c>git merge -F -</c> does not read standard input, and the message never goes on a command
    /// line. When the commit fails, the merge stays in progress and that is what is reported. Every command names the
    /// repository's git directory and working tree. Right before the merge, what it writes (the branch and its reflog, the
    /// index, <c>logs/HEAD</c>, <c>ORIG_HEAD</c>, the merge files) must be reached without a link, or
    /// <see cref="WorktreeOwnershipException"/> is thrown and nothing runs (H-03, G-04); and again right before the
    /// <c>commit</c> of a merge commit and before a <c>merge --abort</c>, which write them too: a link found there leaves
    /// the merge in progress.
    /// </remarks>
    public async Task<UserBranchDelivery> DeliverToUserBranchAsync(ApprovedDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        RequireApproval(delivery);
        if (!Enum.IsDefined(delivery.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(delivery), delivery.Mode, "Unknown delivery mode.");
        }

        var expectedHead = GitNaming.NormalizeObjectId(delivery.ExpectedHead, nameof(delivery));
        var commit = GitNaming.NormalizeObjectId(delivery.Commit, nameof(delivery));
        var target = TargetBranchRef(delivery.TargetBranch);
        if (delivery.Mode == DeliveryMode.MergeCommit)
        {
            ValidateMessage(delivery.Message);
        }

        await RequireSupportedGitAsync(ct).ConfigureAwait(false);
        var repository = await OpenUserRepositoryAsync(delivery.RepositoryRoot, ct).ConfigureAwait(false);
        using var gate = await AcquireGateAsync(repository.Root, ct).ConfigureAwait(false);

        var head = await ReadUserHeadAsync(repository, ct).ConfigureAwait(false);
        if (!string.Equals(head.Branch, target, StringComparison.Ordinal) || !string.Equals(head.Commit, expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome(
                UserBranchDeliveryStatus.RepositoryChanged,
                head,
                [],
                $"HEAD is on {head.Branch ?? "no branch"} at {head.Commit ?? "no commit"}, not on {target} at {expectedHead}.");
        }

        var source = await TryResolveCommitAsync(repository, commit, ct).ConfigureAwait(false)
            ?? throw new GitCommandException($"Commit {commit} does not exist in '{repository.Root}'.", 1, string.Empty);
        if (await IsAncestorInAsync(repository, source, expectedHead, ct).ConfigureAwait(false))
        {
            return Outcome(UserBranchDeliveryStatus.Delivered, head, [], $"{target} already contains {source}.");
        }

        // A merge of the user's own that is not concluded is theirs: nothing is run, and it is never reported as ours.
        if (await TryResolveCommitAsync(repository, "MERGE_HEAD", ct).ConfigureAwait(false) is not null)
        {
            return Outcome(UserBranchDeliveryStatus.GitFailed, head, [], "A merge is already in progress in the user's working tree (MERGE_HEAD exists); nothing was run.");
        }

        return delivery.Mode == DeliveryMode.FastForwardOnly
            ? await FastForwardAsync(repository, target, expectedHead, source, ct).ConfigureAwait(false)
            : await MergeCommitAsync(repository, target, expectedHead, source, delivery.Message, ct).ConfigureAwait(false);
    }

    private async Task<UserBranchDelivery> FastForwardAsync(UserRepository repository, string target, string head, string source, CancellationToken ct)
    {
        var dirty = await FindDirtyPathsAsync(repository, head, source, ct).ConfigureAwait(false);
        if (dirty.Count > 0)
        {
            return new UserBranchDelivery(UserBranchDeliveryStatus.DirtyPaths, head, dirty, DirtyMessage);
        }

        // HEAD could have left the approved branch or commit since the first check (a concurrent checkout): re-read it
        // immediately before the merge, so `git merge --ff-only` never fast-forwards a branch other than the approved one.
        if (await ReadRepositoryChangedAsync(repository, target, head, ct).ConfigureAwait(false) is { } changed)
        {
            return changed;
        }

        // H-03: what the merge writes, checked again right before it (the preflight ran before the approval).
        RequireDeliveryWithoutLinks(repository, target);

        var merge = await _git.RunAsync(repository.Root, InWorkingTree(repository, [.. NoSigning, "merge", "--ff-only", "--no-autostash", "--end-of-options", source]), null, ct, ScopeOf(repository))
            .ConfigureAwait(false);
        return await ConcludeAsync(repository, target, source, merge, ct).ConfigureAwait(false);
    }

    private async Task<UserBranchDelivery> MergeCommitAsync(UserRepository repository, string target, string head, string source, string message, CancellationToken ct)
    {
        // Nothing with conflict markers may reach the user's working tree: a conflict stops the delivery before git merge.
        // P-02: the preview already writes the merged tree into the object store.
        RequireObjectStoreWithoutLinks(repository.CommonDirectory);
        var preview = await _git.RunAsync(
            repository.Root,
            InRepository(repository, "merge-tree", "--write-tree", "--name-only", "--messages", "-z", "--end-of-options", head, source),
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        if (preview.ExitCode > 1 || !GitPorcelain.TryParseMergeTree(preview.StandardOutput, out var parsed) || parsed is null)
        {
            throw new GitCommandException($"git merge-tree failed with exit code {preview.ExitCode}: {preview.StandardError.Trim()}", preview.ExitCode, preview.StandardError);
        }

        if (preview.ExitCode == 1)
        {
            return new UserBranchDelivery(UserBranchDeliveryStatus.Conflicts, head, parsed.ConflictedPaths, parsed.Messages);
        }

        // What the merge writes into the working tree is what differs between HEAD and the merged tree.
        var dirty = await FindDirtyPathsAsync(repository, head, parsed.TreeId, ct).ConfigureAwait(false);
        if (dirty.Count > 0)
        {
            return new UserBranchDelivery(UserBranchDeliveryStatus.DirtyPaths, head, dirty, DirtyMessage);
        }

        // Re-read HEAD immediately before the merge, so a concurrent checkout never merges into another branch (as in the
        // fast-forward path). The first parent of the resulting commit is checked below to be the approved head.
        if (await ReadRepositoryChangedAsync(repository, target, head, ct).ConfigureAwait(false) is { } changed)
        {
            return changed;
        }

        // H-03: what the merge and its commit write, checked again right before them (the preflight ran before the approval).
        RequireDeliveryWithoutLinks(repository, target);

        var merge = await _git.RunAsync(
            repository.Root,
            InWorkingTree(repository, [.. NoSigning, "merge", "--no-ff", "--no-commit", "--no-autostash", "--end-of-options", source]),
            null,
            ct,
            ScopeOf(repository)).ConfigureAwait(false);
        if (merge.ExitCode != 0)
        {
            return await ConcludeAsync(repository, target, source, merge, ct).ConfigureAwait(false);
        }

        // The staged merge is committed only while HEAD is still the approved branch at the approved commit; otherwise the
        // in-progress merge is aborted so nothing is committed onto another branch.
        if (await ReadRepositoryChangedAsync(repository, target, head, ct).ConfigureAwait(false) is { } moved)
        {
            await TryAbortMergeAsync(repository, target, ct).ConfigureAwait(false);
            return moved;
        }

        // G-04: the commit writes the index, the branch, its reflog, logs/HEAD and COMMIT_EDITMSG again, so they are checked
        // again right before it. A link that appeared after the merge leaves the merge in progress, committed through nothing.
        RequireDeliveryWithoutLinks(repository, target);
        var commit = await _git.RunAsync(repository.Root, InWorkingTree(repository, [.. NoSigning, "commit", "--quiet", "-F", "-"]), WithTrailingNewline(message), ct, ScopeOf(repository))
            .ConfigureAwait(false);
        var both = new GitResult(commit.ExitCode, merge.StandardOutput + commit.StandardOutput, merge.StandardError + commit.StandardError);
        return await ConcludeAsync(repository, target, source, both, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Null when HEAD is still the symbolic ref <paramref name="target"/> at <paramref name="expectedHead"/>; otherwise a
    /// <see cref="UserBranchDeliveryStatus.RepositoryChanged"/> outcome, so the caller stops before touching any branch.
    /// </summary>
    private async Task<UserBranchDelivery?> ReadRepositoryChangedAsync(UserRepository repository, string target, string expectedHead, CancellationToken ct)
    {
        var head = await ReadUserHeadAsync(repository, ct).ConfigureAwait(false);
        if (string.Equals(head.Branch, target, StringComparison.Ordinal) && string.Equals(head.Commit, expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Outcome(
            UserBranchDeliveryStatus.RepositoryChanged,
            head,
            [],
            $"HEAD moved to {head.Branch ?? "no branch"} at {head.Commit ?? "no commit"} before the merge, not {target} at {expectedHead}; nothing was delivered.");
    }

    /// <summary>
    /// Aborts a merge left in progress by the delivery (best effort); the working tree returns to HEAD. The abort writes the
    /// index and the working tree, so what the delivery writes is checked again first (G-04): through a link, nothing runs
    /// and the merge stays in progress.
    /// </summary>
    private async Task TryAbortMergeAsync(UserRepository repository, string target, CancellationToken ct)
    {
        if (await TryResolveCommitAsync(repository, "MERGE_HEAD", ct).ConfigureAwait(false) is not null)
        {
            RequireDeliveryWithoutLinks(repository, target);
            await _git.RunAsync(repository.Root, InWorkingTree(repository, "merge", "--abort"), null, ct, ScopeOf(repository)).ConfigureAwait(false);
        }
    }

    private const string DirtyMessage = "The working tree has changes, untracked or ignored files in paths the delivery changes.";

    /// <summary>What git left: delivered only when the approved branch is checked out and contains the commit; a merge still in progress is reported as such.</summary>
    private async Task<UserBranchDelivery> ConcludeAsync(UserRepository repository, string target, string source, GitResult last, CancellationToken ct)
    {
        var output = (last.StandardOutput + "\n" + last.StandardError).Trim();
        var after = await ReadUserHeadAsync(repository, ct).ConfigureAwait(false);
        if (last.ExitCode == 0
            && after.Commit is not null
            && string.Equals(after.Branch, target, StringComparison.Ordinal)
            && await IsAncestorInAsync(repository, source, after.Commit, ct).ConfigureAwait(false))
        {
            return Outcome(UserBranchDeliveryStatus.Delivered, after, [], output);
        }

        if (await TryResolveCommitAsync(repository, "MERGE_HEAD", ct).ConfigureAwait(false) is not null)
        {
            return Outcome(UserBranchDeliveryStatus.IncompleteMerge, after, [], output);
        }

        return Outcome(
            UserBranchDeliveryStatus.GitFailed,
            after,
            [],
            last.ExitCode == 0 ? $"git finished without an error, but {target} does not contain {source}. {output}".Trim() : output);
    }

    /// <summary>
    /// The paths, among those that differ between <paramref name="head"/> and <paramref name="result"/> (what the
    /// delivery writes into the working tree), that the user's working tree has changed, untracked or ignored: git
    /// refuses the first two by itself but overwrites an ignored file without a word. Read-only (no index refresh).
    /// </summary>
    private async Task<IReadOnlyList<string>> FindDirtyPathsAsync(UserRepository repository, string head, string result, CancellationToken ct)
    {
        var diff = await _git.RunCheckedAsync(
            repository.Root,
            InRepository(repository, "diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all", "--end-of-options", head, result, "--"),
            ct).ConfigureAwait(false);
        var delivered = diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (delivered.Length == 0)
        {
            return [];
        }

        var status = await _git.RunCheckedAsync(
            repository.Root,
            InWorkingTree(repository, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=all"),
            ct,
            scope: ScopeOf(repository)).ConfigureAwait(false);
        var entries = GitPorcelain.ParseStatusPaths(status.StandardOutput);
        return delivered.Where(path => entries.Any(entry => Overlaps(path, entry))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    // Windows compares names without case. An entry that ends with '/' is a whole folder (ignored, --ignored=matching);
    // a file and a folder of the same name collide too.
    private static bool Overlaps(string path, string entry)
    {
        var name = entry.TrimEnd('/');
        return string.Equals(path, name, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nothing runs without the approval of exactly this delivery: the request that was asked, at critical risk and bound
    /// to this repository, target branch, mode, commit and expected head (<see cref="DeliveryApproval"/>); an explicit yes
    /// from someone named that answers that request; and an approval never used before in this process, which this call
    /// spends. Asking belongs to the caller (<c>DeliveryService</c>, through <c>IApprovalGate</c>).
    /// </summary>
    private static void RequireApproval(ApprovedDelivery delivery)
    {
        if (delivery.Request is null || delivery.Decision is null)
        {
            throw new DeliveryNotApprovedException("A delivery to the user's branch needs the approval request and its answer; nothing was run.");
        }

        if (!delivery.Decision.Approved || string.IsNullOrWhiteSpace(delivery.Decision.DecidedBy))
        {
            throw new DeliveryNotApprovedException("The delivery to the user's branch was not approved by someone named; nothing was run.");
        }

        if (delivery.Request.Risk != RiskLevel.Critical)
        {
            throw new DeliveryNotApprovedException($"A delivery to the user's branch is a critical action, but it was asked as {delivery.Request.Risk}; nothing was run.");
        }

        if (!DeliveryApproval.Matches(delivery.Request.Binding, delivery))
        {
            throw new DeliveryNotApprovedException(
                "The approval was not asked for this delivery (repository, branch, mode, commit and expected head must be the approved ones); nothing was run.");
        }

        if (!string.Equals(delivery.Decision.Answers, delivery.Request.Binding, StringComparison.Ordinal))
        {
            throw new DeliveryNotApprovedException("The yes does not answer the request that was asked for this delivery; nothing was run.");
        }

        if (!SpentApprovals.TryAdd(delivery.Request.Binding!, 0))
        {
            throw new DeliveryNotApprovedException("This approval was already used for a delivery; a new delivery needs a new approval. Nothing was run.");
        }
    }

    private static string TargetBranchRef(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch, nameof(ApprovedDelivery.TargetBranch));
        return branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;
    }

    private static UserBranchDelivery Outcome(UserBranchDeliveryStatus status, UserHead head, IReadOnlyList<string> paths, string output) =>
        new(status, head.Commit ?? string.Empty, paths, output);

    // ---- the user's repository --------------------------------------------------------------------------------

    /// <summary>
    /// The user's repository, found from the project: the root of its working tree, its git directory (HEAD and the
    /// index of that working tree) and its common directory (the refs). Never anything inside the worktree root, so
    /// never a worktree whose <c>.git</c> file an agent rewrote. Every command on it names the git directory explicitly.
    /// </summary>
    private sealed record UserRepository(string Root, string GitDirectory, string CommonDirectory);

    /// <summary>The branch HEAD is on (<c>refs/heads/main</c>; null when detached) and the commit it resolves to (null when unborn).</summary>
    private sealed record UserHead(string? Branch, string? Commit);

    private async Task<UserRepository> OpenUserRepositoryAsync(string repoRoot, CancellationToken ct)
    {
        var directory = RequireRepositoryDirectory(repoRoot);
        var refFormat = await RefFormatQueryAsync(ct).ConfigureAwait(false);
        var paths = await _git.RunAsync(directory, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir", .. refFormat], null, ct)
            .ConfigureAwait(false);
        var lines = paths.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.ExitCode != 0 || lines.Length != 3 + refFormat.Length)
        {
            throw new GitCommandException($"'{directory}' is not inside the working tree of a git repository.", paths.ExitCode, paths.StandardError);
        }

        RequireFilesRefStorage(directory, refFormat, lines);
        WorktreeFileSystem.RequireDotGitWithoutLink(lines[0]);
        var repository = new UserRepository(GitNaming.NormalizePath(lines[0]), GitNaming.NormalizePath(lines[1]), GitNaming.NormalizePath(lines[2]));
        RequireOutsideWorktreeRoot(repository.Root);
        RequireOutsideWorktreeRoot(repository.GitDirectory);
        RequireOutsideWorktreeRoot(repository.CommonDirectory);
        return repository;
    }

    private static string[] InRepository(UserRepository repository, params string[] arguments) =>
        ["--git-dir=" + repository.GitDirectory, .. arguments];

    // The repository a content command works on (X-01): its callbacks may not run a program of the project's working tree
    // or of its common directory, from whatever folder the command runs. Its git directory is walked with the common one
    // (S-01 to S-03): the project may itself be a linked worktree.
    private static GitScope ScopeOf(UserRepository repository) => new(repository.Root, repository.CommonDirectory, repository.GitDirectory);

    private static GitScope ScopeOf(Repository repository) => new(repository.Root, repository.CommonDirectory);

    private static GitScope ScopeOf(OwnedWorktree owned) => new(owned.RepositoryRoot, owned.CommonDirectory);

    private static string[] InWorkingTree(UserRepository repository, params string[] arguments) =>
        ["--git-dir=" + repository.GitDirectory, "--work-tree=" + repository.Root, .. arguments];

    /// <summary>The commit a validated revision points at, or null when it does not exist. An answer that is not an object id is an error.</summary>
    private async Task<string?> TryResolveCommitAsync(UserRepository repository, string revision, CancellationToken ct)
    {
        var result = await _git.RunAsync(repository.Root, InRepository(repository, "rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}"), null, ct)
            .ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return null;
        }

        result.EnsureSuccess("rev-parse");
        var commit = result.StandardOutput.Trim();
        return GitNaming.IsObjectId(commit)
            ? commit.ToLowerInvariant()
            : throw new GitCommandException($"git rev-parse answered '{commit}', which is not a commit id.", result.ExitCode, result.StandardError);
    }

    private async Task<bool> IsAncestorInAsync(UserRepository repository, string ancestor, string descendant, CancellationToken ct)
    {
        var result = await _git.RunAsync(repository.Root, InRepository(repository, "merge-base", "--is-ancestor", "--end-of-options", ancestor, descendant), null, ct)
            .ConfigureAwait(false);
        if (result.ExitCode > 1)
        {
            result.EnsureSuccess("merge-base");
        }

        return result.ExitCode == 0;
    }

    private async Task<UserHead> ReadUserHeadAsync(UserRepository repository, CancellationToken ct)
    {
        // Exit code 1 means a detached HEAD.
        var branch = await _git.RunAsync(repository.Root, InRepository(repository, "symbolic-ref", "--quiet", "HEAD"), null, ct).ConfigureAwait(false);
        if (branch.ExitCode > 1)
        {
            branch.EnsureSuccess("symbolic-ref");
        }

        var commit = await TryResolveCommitAsync(repository, "HEAD", ct).ConfigureAwait(false);
        return new UserHead(branch.ExitCode == 0 ? branch.StandardOutput.Trim() : null, commit);
    }

    /// <summary>
    /// Refuses a ref whose loose file or reflog, or a folder on the way to either below the common directory, is a link
    /// (junction or symbolic link). An agent with a shell can turn <c>refs/agentstudio/&lt;run&gt;</c> into a junction to
    /// <c>refs/heads</c>: every branch of the user then looks like a hidden ref of the run, and git would write or delete
    /// the branch file through the link. The reflog (<c>logs/&lt;ref&gt;</c>) is written with the ref: git keeps one for
    /// every branch by default, and for every ref with <c>core.logAllRefUpdates=always</c>, which the repository can set.
    /// Measured with git 2.45.2 (H-03): through a junction in <c>logs/refs/heads/agentstudio</c>, <c>update-ref</c>
    /// created and appended reflog files outside the repository, and <c>update-ref -d</c> deleted one. git appends to the
    /// reflog in place, so a reflog with another name (a hard link) is refused too: through one, git appended to the other
    /// file (measured). The ref itself is replaced by a rename, which a hard link does not follow.
    /// </summary>
    private static void RequireNoLinkOnRefPath(string commonDirectory, string refName)
    {
        var relative = refName.Replace('/', Path.DirectorySeparatorChar);
        var reflog = Path.Combine(commonDirectory, "logs", relative);
        if (WorktreeFileSystem.HasLinkBelow(commonDirectory, Path.Combine(commonDirectory, relative))
            || WorktreeFileSystem.HasLinkBelow(commonDirectory, reflog)
            || WorktreeFileSystem.HasOtherNames(reflog))
        {
            throw new WorktreeOwnershipException($"The ref '{refName}' or its reflog is reached through a link under '{commonDirectory}'; refusing to touch it.");
        }
    }

    private static void RequireNoLinkOnRefPath(UserRepository repository, string refName) =>
        RequireNoLinkOnRefPath(repository.CommonDirectory, refName);

    private static bool IsWellFormedAgentStudioRef(string name)
    {
        try
        {
            return string.Equals(GitNaming.NormalizeAgentStudioRef(name, nameof(name)), name, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or WorktreeOwnershipException)
        {
            return false;
        }
    }

    /// <summary>
    /// The full path of the folder the user's <c>core.hooksPath</c> names (a relative value is relative to the root of
    /// the working tree, as git resolves it), or null when it is not set. Every command gets Agent Studio's own override
    /// (scope "command"), so the user's value is the last one of any other scope. Used by the capture of the user's
    /// repository (the delivery no longer runs the user's hooks, R3-05).
    /// </summary>
    private async Task<string?> ReadUserHooksPathAsync(UserRepository repository, CancellationToken ct)
    {
        var result = await _git.RunAsync(
            repository.Root,
            InRepository(repository, "config", "-z", "--type=path", "--show-scope", "--get-all", "--end-of-options", "core.hooksPath"),
            null,
            ct).ConfigureAwait(false);
        if (result.ExitCode > 1)
        {
            result.EnsureSuccess("config");
        }

        var configured = GitPorcelain.ParseScopedConfigValues(result.StandardOutput)
            .LastOrDefault(v => !string.Equals(v.Scope, "command", StringComparison.Ordinal))
            .Value;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return GitNaming.NormalizePath(Path.IsPathRooted(configured) ? configured : Path.Combine(repository.Root, configured));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ---- ownership -------------------------------------------------------------------------------------------

    /// <summary>The user's repository as git sees it from its own working tree root.</summary>
    private sealed record Repository(string Root, string CommonDirectory, IReadOnlyList<WorktreeListEntry> Entries)
    {
        /// <summary>The linked worktrees registered at <paramref name="path"/> (never the first entry, the main working tree).</summary>
        public IReadOnlyList<WorktreeListEntry> LinkedAt(string path) =>
            Entries.Skip(1).Where(e => GitNaming.SamePath(e.Path, path)).ToArray();
    }

    /// <param name="ListedPath">The path exactly as <c>git worktree list</c> prints it.</param>
    private sealed record OwnedWorktree(
        string RepositoryRoot,
        string CommonDirectory,
        string AdminDirectory,
        string Path,
        string ListedPath,
        LockReasonParts Parts,
        string? Head);

    /// <summary>
    /// The guard every mutating worktree operation goes through. The repository is the lease's (the project's), never
    /// the one the worktree's <c>.git</c> file names. The worktree is owned only if it is under the worktree root and
    /// reached without links, that repository lists it as a linked worktree locked with our reason, it is at the path
    /// Agent Studio derives for it, and exactly one administrative folder points back at it. The lease must agree
    /// with all of that.
    /// </summary>
    private async Task<OwnedWorktree> RequireOwnedAsync(WorktreeLease lease, CancellationToken ct)
    {
        var path = RequireLeasePath(lease);
        var repoRoot = RequireLeaseRepository(lease);
        if (!Directory.Exists(path))
        {
            throw new WorktreeOwnershipException($"'{path}' does not exist.");
        }

        RequireNoLinkOnTheWay(path);
        RequireLeaseNames(lease, path);

        var repository = await OpenRepositoryAsync(repoRoot, ct).ConfigureAwait(false);
        var owned = VerifyRegistration(repository, path, lease.Run, lease.Name);
        RequireSameAdministrativeDirectory(lease, owned);
        return owned;
    }

    private string RequireLeasePath(WorktreeLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.Path))
        {
            throw new WorktreeOwnershipException("The lease has no path.");
        }

        var path = GitNaming.NormalizePath(lease.Path);
        if (!GitNaming.IsUnder(path, _worktreeRoot))
        {
            throw new WorktreeOwnershipException($"'{path}' is not inside the Agent Studio worktree root; refusing to touch it.");
        }

        return path;
    }

    private string RequireLeaseRepository(WorktreeLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.RepositoryRoot))
        {
            throw new WorktreeOwnershipException("The lease does not name the repository its worktree belongs to.");
        }

        var root = GitNaming.NormalizePath(lease.RepositoryRoot);
        RequireOutsideWorktreeRoot(root);
        if (!Directory.Exists(root))
        {
            throw new WorktreeOwnershipException($"The repository '{root}' of the lease does not exist.");
        }

        return root;
    }

    private static void RequireLeaseNames(WorktreeLease lease, string path)
    {
        GitNaming.ValidateRun(lease.Run);
        GitNaming.ValidateName(lease.Name);
        if (!string.Equals(lease.RefName, GitNaming.TipRef(lease.Run, lease.Name), StringComparison.Ordinal))
        {
            throw new WorktreeOwnershipException($"The lease does not describe the worktree at '{path}' (ref '{lease.RefName}').");
        }
    }

    private static void RequireSameAdministrativeDirectory(WorktreeLease lease, OwnedWorktree owned)
    {
        if (string.IsNullOrWhiteSpace(lease.AdminDirectory) || !GitNaming.SamePath(lease.AdminDirectory, owned.AdminDirectory))
        {
            throw new WorktreeOwnershipException(
                $"The lease does not describe the worktree at '{owned.Path}' (git's administrative folder is '{owned.AdminDirectory}', the lease says '{lease.AdminDirectory}').");
        }
    }

    private void RequireOutsideWorktreeRoot(string path)
    {
        if (GitNaming.SamePath(path, _worktreeRoot) || GitNaming.IsUnder(path, _worktreeRoot))
        {
            throw new WorktreeOwnershipException(
                $"'{path}' is inside the Agent Studio worktree root '{_worktreeRoot}': a repository must be the user's project, never a worktree; refusing to use it.");
        }
    }

    private void RequireNoLinkOnTheWay(string path)
    {
        if (WorktreeFileSystem.HasLinkBelow(_worktreeRoot, path))
        {
            throw new WorktreeOwnershipException($"'{path}' is a link or is reached through one; refusing to touch it.");
        }
    }

    private async Task<Repository> OpenRepositoryAsync(string repoRoot, CancellationToken ct)
    {
        var refFormat = await RefFormatQueryAsync(ct).ConfigureAwait(false);
        var paths = await _git.RunAsync(repoRoot, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir", .. refFormat], null, ct).ConfigureAwait(false);
        var lines = paths.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.ExitCode != 0 || lines.Length != 2 + refFormat.Length)
        {
            throw new WorktreeOwnershipException($"'{repoRoot}' is not a git repository; refusing to use it.");
        }

        RequireFilesRefStorage(repoRoot, refFormat, lines);
        WorktreeFileSystem.RequireDotGitWithoutLink(lines[0]);
        if (!GitNaming.SamePath(lines[0], repoRoot))
        {
            throw new WorktreeOwnershipException($"'{repoRoot}' is not the root of its git working tree ('{lines[0]}'); refusing to use it.");
        }

        var listing = await _git.RunCheckedAsync(repoRoot, ["worktree", "list", "--porcelain", "-z"], ct).ConfigureAwait(false);
        return new Repository(GitNaming.NormalizePath(repoRoot), GitNaming.NormalizePath(lines[1]), GitPorcelain.ParseWorktreeList(listing.StandardOutput));
    }

    private OwnedWorktree VerifyRegistration(Repository repository, string path, RunId run, string name)
    {
        var registered = repository.LinkedAt(path);
        if (registered.Count == 0)
        {
            throw new WorktreeOwnershipException($"'{path}' is not a linked worktree of '{repository.Root}'; refusing to touch it.");
        }

        if (registered.Count > 1)
        {
            throw new WorktreeOwnershipException($"'{path}' is registered more than once in '{repository.Root}'; refusing to touch it.");
        }

        var entry = registered[0];
        if (!entry.Locked || !GitNaming.TryParseLockReason(entry.LockReason, out var parts))
        {
            throw new WorktreeOwnershipException($"'{path}' is a worktree but it is not locked by Agent Studio; refusing to touch it.");
        }

        if (!string.Equals(parts.Run, run.Value, StringComparison.Ordinal) || !string.Equals(parts.Name, name, StringComparison.Ordinal))
        {
            throw new WorktreeOwnershipException($"The lease does not describe the worktree at '{path}' (git reports run '{parts.Run}', name '{parts.Name}').");
        }

        var expected = GitNaming.WorktreePath(_worktreeRoot, repository.Root, run, name);
        if (!GitNaming.SamePath(path, expected))
        {
            throw new WorktreeOwnershipException($"The lease does not describe the worktree at '{path}': Agent Studio creates it at '{expected}'.");
        }

        var administrative = FindAdministrativeDirectories(repository.CommonDirectory, path);
        if (administrative.Count != 1)
        {
            throw new WorktreeOwnershipException(
                $"The administrative folder of '{path}' cannot be identified under '{repository.CommonDirectory}' ({administrative.Count} candidates); refusing to touch it.");
        }

        // Every operation on an owned worktree comes through here, and git writes or deletes in this folder (H-03).
        var admin = administrative[0];
        RequireAdminDirectoryWithoutLinks(repository.CommonDirectory, admin, path);
        if (!string.Equals(ReadFirstLine(Path.Combine(admin, "locked")), entry.LockReason, StringComparison.Ordinal))
        {
            throw new WorktreeOwnershipException($"The lock of '{path}' in '{admin}' is not Agent Studio's; refusing to touch it.");
        }

        var commonDirectory = ReadFirstLine(Path.Combine(admin, "commondir"));
        if (commonDirectory is null || !GitNaming.SamePath(Path.Combine(admin, commonDirectory), repository.CommonDirectory))
        {
            throw new WorktreeOwnershipException($"'{admin}' does not belong to the repository '{repository.Root}'; refusing to touch it.");
        }

        return new OwnedWorktree(repository.Root, repository.CommonDirectory, admin, GitNaming.NormalizePath(path), entry.Path, parts, entry.Head);
    }

    /// <summary>The folders under <c>&lt;common dir&gt;/worktrees</c> whose <c>gitdir</c> file points at <c>&lt;path&gt;/.git</c>.</summary>
    private static IReadOnlyList<string> FindAdministrativeDirectories(string commonDirectory, string worktreePath)
    {
        var folder = Path.Combine(commonDirectory, "worktrees");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var gitFile = Path.Combine(worktreePath, ".git");
        var found = new List<string>();
        foreach (var candidate in Directory.EnumerateDirectories(folder))
        {
            // Absolute, or relative to the administrative folder with worktree.useRelativePaths (git 2.48 and later).
            var recorded = ReadFirstLine(Path.Combine(candidate, "gitdir"));
            if (!string.IsNullOrEmpty(recorded) && GitNaming.SamePath(Path.Combine(candidate, recorded), gitFile))
            {
                found.Add(GitNaming.NormalizePath(candidate));
            }
        }

        return found;
    }

    /// <summary>
    /// A worktree is reused only if its creation finished: HEAD resolves, git wrote its index, and the start ref
    /// (written after <c>worktree add</c> returned) records the requested base.
    /// </summary>
    private async Task VerifyCompleteAsync(OwnedWorktree owned, string baseCommit, string startRef, CancellationToken ct)
    {
        var head = await _git.RunAsync(owned.Path, InWorktree(owned, "rev-parse", "--verify", "--quiet", "HEAD^{commit}"), null, ct).ConfigureAwait(false);
        var recordedBase = await TryResolveRefAsync(owned.RepositoryRoot, startRef, ct).ConfigureAwait(false);
        if (head.ExitCode != 0 || !File.Exists(Path.Combine(owned.AdminDirectory, "index")) || recordedBase is null)
        {
            throw new WorktreeMismatchException(
                $"The worktree '{owned.Path}' is incomplete: its creation was interrupted. Remove it with RemoveAsync(discardChanges: true) and create it again.");
        }

        if (!string.Equals(recordedBase, baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorktreeMismatchException($"The worktree '{owned.Path}' was created at {recordedBase}, not at the requested {baseCommit}.");
        }
    }

    /// <summary>A start ref that records another base means the name was already used in this run for another base.</summary>
    private async Task RequireRecordedBaseAsync(string repoRoot, string startRef, string baseCommit, string path, CancellationToken ct)
    {
        var recorded = await TryResolveRefAsync(repoRoot, startRef, ct).ConfigureAwait(false);
        if (recorded is not null && !string.Equals(recorded, baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorktreeMismatchException($"The worktree '{path}' was created at {recorded}, not at the requested {baseCommit}.");
        }
    }

    private static WorktreeLease Lease(OwnedWorktree owned, string baseCommit)
    {
        var run = new RunId(owned.Parts.Run);
        return new WorktreeLease(owned.Path, owned.Parts.Name, GitNaming.TipRef(run, owned.Parts.Name), baseCommit, run, owned.RepositoryRoot, owned.AdminDirectory);
    }

    /// <summary>Global options that make git ignore the worktree's <c>.git</c> file, which the agent can rewrite.</summary>
    private static string[] InWorktree(OwnedWorktree owned, params string[] arguments) =>
        ["--git-dir=" + owned.AdminDirectory, "--work-tree=" + owned.Path, .. arguments];

    // ---- removal ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A folder git no longer knows at this worktree's path: what an interrupted removal left (git dropped the
    /// registration and the lock but could not delete every file). It is Agent Studio's only if it is exactly where
    /// Agent Studio creates this worktree and no administrative folder still points at it.
    /// </summary>
    private async Task<RemovalOutcome> RemoveLeftoverAsync(Repository repository, WorktreeLease lease, string path, bool discardChanges, CancellationToken ct)
    {
        var expected = GitNaming.WorktreePath(_worktreeRoot, repository.Root, lease.Run, lease.Name);
        if (!GitNaming.SamePath(path, expected) || FindAdministrativeDirectories(repository.CommonDirectory, path).Count > 0)
        {
            throw new WorktreeOwnershipException(
                $"'{path}' is not a linked worktree of '{repository.Root}', nor what is left of an Agent Studio worktree; refusing to touch it.");
        }

        // Its state can no longer be checked, so it counts as uncommitted work.
        if (!discardChanges)
        {
            return RemovalOutcome.RefusedDirty;
        }

        try
        {
            await WorktreeFileSystem.RetryWhileInUseAsync(() => WorktreeFileSystem.DeleteTree(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"What is left of the worktree '{path}' is in use and was not deleted completely. Close what uses it and try again.", ex);
        }

        TryDeleteEmptyParents(path);
        return RemovalOutcome.Removed;
    }

    /// <summary>Renames the worktree folder into the trash folder: all or nothing, retried while a file is in use.</summary>
    private async Task<string> MoveAsideAsync(string path, CancellationToken ct)
    {
        var trash = Path.Combine(_worktreeRoot, TrashFolderName);
        var destination = Path.Combine(trash, Guid.NewGuid().ToString("N"));
        try
        {
            // The trash folder is (re)created on every attempt: a removal in another repository may have just emptied it.
            await WorktreeFileSystem.RetryWhileInUseAsync(
                () =>
                {
                    Directory.CreateDirectory(trash);
                    if (WorktreeFileSystem.IsLink(new DirectoryInfo(trash)))
                    {
                        throw new WorktreeOwnershipException($"'{trash}' is a link; refusing to move a worktree into it.");
                    }

                    Directory.Move(path, destination);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The worktree '{path}' is in use (a file is open or a process runs inside it); nothing was removed and it stays registered. Close what uses it and try again.",
                ex);
        }

        return destination;
    }

    /// <summary>
    /// Drops the registration of a worktree whose folder no longer exists. The path goes exactly as git lists it, so
    /// git finds the entry without resolving folders that may be gone too. <c>worktree remove --force --force</c> deletes
    /// the administrative folder recursively, and through a junction it emptied the folder the junction points at
    /// (measured with git 2.45.2, H-03): so <c>&lt;common&gt;/worktrees</c> and every administrative folder of the
    /// worktree must be reached without a link, or nothing runs.
    /// </summary>
    private async Task DropRegistrationAsync(string repoRoot, string commonDirectory, string listedPath, CancellationToken ct)
    {
        var worktrees = Path.Combine(commonDirectory, "worktrees");
        if (WorktreeFileSystem.HasLinkBelow(commonDirectory, worktrees))
        {
            throw new WorktreeOwnershipException(
                $"'{worktrees}' is reached through a link under '{commonDirectory}'; refusing to drop the registration of '{listedPath}' through it.");
        }

        foreach (var admin in FindAdministrativeDirectories(commonDirectory, GitNaming.NormalizePath(listedPath)))
        {
            RequireAdminDirectoryWithoutLinks(commonDirectory, admin, listedPath);
        }

        await _git.RunCheckedAsync(repoRoot, ["worktree", "remove", "--force", "--force", listedPath], ct).ConfigureAwait(false);
    }

    // Best effort: what cannot be deleted now stays in the trash folder and goes with the next removal.
    private async Task TryDeleteAsideAsync(string aside)
    {
        try
        {
            if (Directory.Exists(aside))
            {
                await WorktreeFileSystem.RetryWhileInUseAsync(() => WorktreeFileSystem.DeleteTree(aside), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var trash = Path.Combine(_worktreeRoot, TrashFolderName);
        try
        {
            foreach (var leftover in Directory.EnumerateFileSystemEntries(trash))
            {
                WorktreeFileSystem.DeleteTree(leftover);
            }

            Directory.Delete(trash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- git helpers -----------------------------------------------------------------------------------------

    private async Task<string> ResolveRepositoryRootAsync(string path, CancellationToken ct)
    {
        var full = RequireRepositoryDirectory(path);
        var refFormat = await RefFormatQueryAsync(ct).ConfigureAwait(false);
        var result = await _git.RunAsync(full, ["rev-parse", "--show-toplevel", .. refFormat], null, ct).ConfigureAwait(false);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 || lines.Length != 1 + refFormat.Length)
        {
            throw new GitCommandException($"'{full}' is not inside a git repository.", result.ExitCode, result.StandardError);
        }

        RequireFilesRefStorage(full, refFormat, lines);
        var root = GitNaming.NormalizePath(lines[0]);
        RequireOutsideWorktreeRoot(root);
        return root;
    }

    /// <summary>The oldest git with the reftable backend, and with <c>rev-parse --show-ref-format</c> (2.45).</summary>
    private static readonly Version RefStorageGitVersion = new(2, 45, 0);

    /// <summary>
    /// P-02: what <c>rev-parse</c> is also asked, so it prints the backend the repository keeps its refs in: with git 2.45
    /// or later, <c>--show-ref-format</c> (measured with git 2.45.2: <c>files</c>, or <c>reftable</c> in a repository made
    /// with <c>--ref-format=reftable</c>, also from one of its worktrees). An older git has no reftable backend and refuses
    /// a repository that names one (<c>extensions.refStorage</c> needs repository format 1, whose unknown extensions git
    /// refuses), so nothing is asked; not measured with such a git. The version is asked once per instance.
    /// </summary>
    private async Task<string[]> RefFormatQueryAsync(CancellationToken ct) =>
        await GetGitVersionAsync(ct).ConfigureAwait(false) >= RefStorageGitVersion ? ["--show-ref-format"] : [];

    /// <summary>
    /// P-02: refuses (<see cref="UnsupportedRepositoryException"/>) a repository whose refs are not kept as files, the
    /// last line of a <c>rev-parse</c> that was asked <see cref="RefFormatQueryAsync"/>. With the reftable backend the
    /// refs live in <c>reftable/</c> and HEAD reads <c>ref: refs/heads/.invalid</c> (measured with git 2.45.2), so every
    /// check of a ref, a reflog, a HEAD file or an administrative file, which assumes the files backend, checks nothing.
    /// </summary>
    private static void RequireFilesRefStorage(string repository, string[] refFormat, string[] lines)
    {
        if (refFormat.Length == 0 || string.Equals(lines[^1], "files", StringComparison.Ordinal))
        {
            return;
        }

        throw new UnsupportedRepositoryException(
            $"The repository '{repository}' keeps its refs in the '{lines[^1]}' format (extensions.refStorage), and Agent Studio checks refs, reflogs and HEAD as files, so it only works with the files backend; refusing to use it.");
    }

    private async Task<string> RequireCommitAsync(string repoRoot, string objectId, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", objectId + "^{commit}"], null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException($"Commit {objectId} does not exist in '{repoRoot}'.", result.ExitCode, result.StandardError);
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>The commit a ref points to, or null when the ref does not exist.</summary>
    private async Task<string?> TryResolveRefAsync(string repoRoot, string refName, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", refName + "^{commit}"], null, ct).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => result.StandardOutput.Trim(),
            1 => null,
            _ => result.EnsureSuccess("rev-parse").StandardOutput,
        };
    }

    /// <summary>The object a ref points to, unpeeled (the old value of a compare-and-swap), or null when it does not exist.</summary>
    private async Task<string?> TryReadRefAsync(string repoRoot, string refName, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", refName], null, ct).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => result.StandardOutput.Trim(),
            1 => null,
            _ => result.EnsureSuccess("rev-parse").StandardOutput,
        };
    }

    private async Task<bool> IsAncestorCoreAsync(string repoRoot, string ancestor, string descendant, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoRoot, ["merge-base", "--is-ancestor", ancestor, descendant], null, ct).ConfigureAwait(false);
        if (result.ExitCode > 1)
        {
            result.EnsureSuccess("merge-base");
        }

        return result.ExitCode == 0;
    }

    /// <summary>
    /// Moves a ref (refs under the Agent Studio namespace only) from <paramref name="expectedOld"/> (null: it must not
    /// exist) to <paramref name="newValue"/>. <c>--no-deref</c>: a symbolic ref planted in our namespace is replaced,
    /// never followed to the branch it names. <c>--no-deref</c> does nothing for a link in a parent folder, so a ref
    /// whose file or reflog is reached through one under the common directory of <paramref name="scope"/> is refused
    /// first (H-03), and the command walks that git directory before it runs (S-02).
    /// </summary>
    /// <exception cref="StaleRefException">The ref is not at <paramref name="expectedOld"/>.</exception>
    private async Task CompareAndSwapRefAsync(GitScope scope, string refName, string newValue, string? expectedOld, CancellationToken ct)
    {
        var repoRoot = scope.RepositoryRoot;
        var name = GitNaming.NormalizeAgentStudioRef(refName, nameof(refName));
        RequireNoLinkOnRefPath(scope.CommonDirectory, name);
        var update = await _git.RunAsync(repoRoot, ["update-ref", "--no-deref", name, newValue, expectedOld ?? ZeroId(newValue)], null, ct, scope).ConfigureAwait(false);
        if (update.ExitCode == 0)
        {
            return;
        }

        // A moved ref is a stale expectation; anything else is a real failure.
        var actual = await TryReadRefAsync(repoRoot, name, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expectedOld, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleRefException(name, expectedOld ?? ZeroId(newValue), actual);
        }

        update.EnsureSuccess("update-ref");
    }

    /// <summary>
    /// Creates a ref under the Agent Studio namespace at <paramref name="value"/> unless it exists. A ref whose file or
    /// reflog is reached through a link under the common directory of <paramref name="scope"/> is refused first (H-03),
    /// and the command walks that git directory before it runs (S-02).
    /// </summary>
    private async Task CreateRefIfMissingAsync(GitScope scope, string refName, string value, CancellationToken ct)
    {
        var repoRoot = scope.RepositoryRoot;
        var name = GitNaming.NormalizeAgentStudioRef(refName, nameof(refName));
        RequireNoLinkOnRefPath(scope.CommonDirectory, name);

        // The all-zero id as the old value means "create only if it does not exist".
        var result = await _git.RunAsync(repoRoot, ["update-ref", "--no-deref", name, value, ZeroId(value)], null, ct, scope).ConfigureAwait(false);
        if (result.ExitCode != 0 && await TryResolveRefAsync(repoRoot, name, ct).ConfigureAwait(false) is null)
        {
            result.EnsureSuccess("update-ref");
        }
    }

    private static string ZeroId(string objectId) => new('0', objectId.Length);

    private async Task<Version> GetGitVersionAsync(CancellationToken ct)
    {
        if (_gitVersion is { } known)
        {
            return known;
        }

        var result = await _git.RunCheckedAsync(Path.GetTempPath(), ["--version"], ct).ConfigureAwait(false);
        var version = GitPorcelain.ParseVersion(result.StandardOutput);
        _gitVersion = version;
        return version;
    }

    private async Task RequireSupportedGitAsync(CancellationToken ct)
    {
        var version = await GetGitVersionAsync(ct).ConfigureAwait(false);
        if (version < MinimumGitVersion)
        {
            throw new GitUnavailableException($"git {version} is too old: Agent Studio needs git {MinimumGitVersion} or later (merge-tree --write-tree).");
        }
    }

    // ---- small helpers ---------------------------------------------------------------------------------------

    private async Task<IDisposable> AcquireGateAsync(string repoRoot, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(GitNaming.NormalizePath(repoRoot), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new GateReleaser(gate);
    }

    private sealed class GateReleaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>An existing folder that can be a repository root: never the worktree root or anything inside it.</summary>
    private string RequireRepositoryDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = GitNaming.NormalizePath(path);
        RequireOutsideWorktreeRoot(full);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The directory '{full}' does not exist.");
        }

        return full;
    }

    private static string? ReadFirstLine(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var text = File.ReadAllText(file);
            var end = text.IndexOf('\n', StringComparison.Ordinal);
            return (end < 0 ? text : text[..end]).TrimEnd('\r');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A commit message cannot contain NUL characters.", nameof(message));
        }
    }

    private static string WithTrailingNewline(string message) =>
        message.EndsWith('\n') ? message : message + "\n";

    private void TryDeleteEmptyParents(string worktreePath)
    {
        try
        {
            for (var directory = Path.GetDirectoryName(worktreePath);
                directory is not null && GitNaming.IsUnder(directory, _worktreeRoot);
                directory = Path.GetDirectoryName(directory))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // Best effort: an empty folder left behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Best effort: what is left is a temporary folder of Agent Studio, outside every repository and worktree.
    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                WorktreeFileSystem.DeleteTree(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
