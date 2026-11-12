using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Git;

// Phase 3 contracts (docs/13 sections 3.4 and 5). The implementation talks to git.exe, always with
// GIT_TERMINAL_PROMPT=0 and the message of a commit on standard input. Nothing here touches the user's HEAD, index,
// branches or working tree, and nothing pushes, except IWorktreeManager.DeliverToUserBranchAsync: the approved
// delivery, the only operation that moves the user's branch.

/// <summary>What the system knows about the user's repository before it starts: its root, where HEAD is, whether it is dirty.</summary>
/// <param name="HasUncommittedChanges">
/// Whether the user has changes of their own that are not committed (staged, unstaged or untracked). What Agent Studio itself
/// writes in the project's <c>.agentstudio</c> folder (<see cref="Projects.AgentStudioFiles.IsAgentStudioStatusLine"/>) does
/// not count: the journal and the memory export, which the user commits when they choose (ADR-0008, ADR-0022), and the
/// database, the evidence, the temporary files, the canvas layout and the verification profile, which <c>info/exclude</c>
/// keeps out of git. None of them is work of the user's that a run, a debate or an audit leaves out by starting from the
/// last commit; Agent Studio writes them, and reads its verification profile from the project root on purpose.
/// </param>
public sealed record RepositoryState(string Root, string HeadCommit, string? HeadBranch, bool HasUncommittedChanges, Version GitVersion);

/// <summary>A request for the worktree of a task (<c>T1</c>) or of the system (<c>_base</c>, <c>_integration</c>), on <see cref="BaseCommit"/>.</summary>
public sealed record WorktreeRequest(ProjectContext Project, RunId Run, string Name, string BaseCommit);

/// <summary>A worktree Agent Studio created and owns: it lives under the worktree root, and git holds a lock whose reason names the run.</summary>
/// <param name="RepositoryRoot">Root of the user's repository the worktree belongs to.</param>
/// <param name="AdminDirectory">
/// The worktree's administrative directory inside the repository (<c>.git/worktrees/&lt;name&gt;</c>). Every git command
/// that changes something runs with explicit <c>--git-dir</c> and <c>--work-tree</c> derived from the lease, never from
/// the <c>.git</c> file inside the worktree, which an agent can rewrite.
/// </param>
public sealed record WorktreeLease(string Path, string Name, string RefName, string BaseCommit, RunId Run, string RepositoryRoot, string AdminDirectory);

/// <summary>
/// What an agent run must leave untouched in the user's repository: HEAD, the local branches and tags, the stash, the
/// index and the working tree status. The engine captures it before and after each agent run
/// (<see cref="IWorktreeManager.CaptureUserRepositoryStateAsync"/>) and fails the task, with an audit alert, if
/// <see cref="Compare"/> finds a difference.
/// </summary>
/// <param name="HeadCommit">The commit HEAD resolves to.</param>
/// <param name="HeadRef">The branch HEAD is on (<c>refs/heads/main</c>), or null when HEAD is detached.</param>
/// <param name="Branches"><c>refs/heads/*</c> with the commit each points at, except the branches the system owns (<c>agentstudio/*</c>).</param>
/// <param name="Tags"><c>refs/tags/*</c> with the object each points at.</param>
/// <param name="StashCommit">The commit <c>refs/stash</c> points at, or null when there is no stash.</param>
/// <param name="IndexHash">A hash of the staged content (the index).</param>
/// <param name="PorcelainStatus">The lines of <c>git status --porcelain</c>.</param>
/// <param name="Contents">
/// The content of each path the status lists, by the path as its line gives it (a folder ends with '/'). A file that was
/// already modified can change again without changing its line: only its content shows it. Null in a capture that did
/// not take them.
/// </param>
/// <param name="GitFiles">
/// The repository's configuration and every file of its hooks folder, which git reads or runs and no status line shows,
/// by path (relative to the root of the working tree with '/' separators, or absolute outside it). Null in a capture
/// that did not take them.
/// </param>
/// <param name="HiddenTracked">
/// The content of the tracked paths no status line can show (R3-04), by path (relative to the root of the working tree
/// with '/' separators, a folder ending with '/'): each file the index flags assume-unchanged or skip-worktree, whose
/// changes in the working tree git does not look for, and the folder of each submodule checked out (a gitlink whose
/// folder has a <c>.git</c>), which the status ignores. Compared like <paramref name="GitFiles"/>, whatever the status
/// says. Null in a capture that did not take them.
/// </param>
public sealed record UserRepositoryState(
    string HeadCommit,
    string? HeadRef,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyDictionary<string, string> Tags,
    string? StashCommit,
    string IndexHash,
    IReadOnlyList<string> PorcelainStatus,
    IReadOnlyDictionary<string, FileFingerprint>? Contents = null,
    IReadOnlyDictionary<string, FileFingerprint>? GitFiles = null,
    IReadOnlyDictionary<string, FileFingerprint>? HiddenTracked = null)
{
    /// <summary>
    /// Lists, in a fixed order (HEAD, branches, tags, stash, index, status, contents, git files, hidden tracked paths),
    /// what is different in <paramref name="after"/> compared with <paramref name="before"/>, with the entries that could
    /// not be verified (indeterminate) listed last. Empty means the user's repository is as it was. Ref names are compared
    /// case-sensitively, and the order refs or status lines were listed in does not matter.
    /// </summary>
    /// <remarks>
    /// The content of a path is compared only when both captures kept the same status line for it: a line that changed
    /// is already a difference, and a line a caller left out of both captures (the engine leaves out
    /// <c>.agentstudio/</c>) leaves its content out too. Git files and hidden tracked paths are compared whatever the
    /// status says: a path only one capture has was created or deleted. A comparison made by sizes and modification
    /// times, not by the bytes, says it is approximate. A path whose content neither capture could verify (too large for
    /// the per-file cap, beyond the capture budget, or unreadable) and whose metadata is the same in both is listed last as
    /// indeterminate; here it blocks like any difference, because a same-length swap that restored the timestamps would be
    /// invisible.
    /// </remarks>
    public static IReadOnlyList<string> Compare(UserRepositoryState before, UserRepositoryState after)
    {
        var (differences, indeterminate) = CompareInDetail(before, after);
        return indeterminate.Count == 0 ? differences : [.. differences, .. indeterminate.Select(u => u.Description)];
    }

    /// <summary>
    /// As <see cref="Compare"/>, but keeps the differences apart from the paths whose content neither capture could
    /// verify. Both empty means the repository is as it was; a caller that fails closed treats either list as blocking,
    /// which is why <see cref="Compare"/> concatenates them.
    /// </summary>
    /// <remarks>
    /// A path is only indeterminate when both captures could not verify its content and both read the same metadata
    /// for it (<see cref="FileFingerprint.Approximation"/>): metadata that differs is a difference, a path one capture
    /// verified and the other did not is a difference (the same content is verified the same way), and a path whose
    /// metadata could not be read either is a difference. So what a caller may weigh as indeterminate is only a change
    /// that leaves every size and modification time as it was (ADR-0012).
    /// </remarks>
    public static (IReadOnlyList<string> Differences, IReadOnlyList<UnverifiedPath> Indeterminate) CompareInDetail(UserRepositoryState before, UserRepositoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var indeterminate = new List<UnverifiedPath>();
        var differences = new List<string>();
        if (!string.Equals(before.HeadRef, after.HeadRef, StringComparison.Ordinal))
        {
            if (before.HeadRef is null)
            {
                differences.Add($"HEAD was attached to branch '{after.HeadRef}' (it was detached).");
            }
            else if (after.HeadRef is null)
            {
                differences.Add($"HEAD was detached (it was on branch '{before.HeadRef}').");
            }
            else
            {
                differences.Add($"HEAD switched from branch '{before.HeadRef}' to '{after.HeadRef}'.");
            }
        }

        if (!string.Equals(before.HeadCommit, after.HeadCommit, StringComparison.Ordinal))
        {
            differences.Add($"HEAD moved from {before.HeadCommit} to {after.HeadCommit}.");
        }

        CompareRefs("Branch", before.Branches, after.Branches, differences);
        CompareRefs("Tag", before.Tags, after.Tags, differences);

        if (!string.Equals(before.StashCommit, after.StashCommit, StringComparison.Ordinal))
        {
            differences.Add($"Stash changed from {before.StashCommit ?? "(none)"} to {after.StashCommit ?? "(none)"}.");
        }

        if (!string.Equals(before.IndexHash, after.IndexHash, StringComparison.Ordinal))
        {
            differences.Add($"Index changed from {before.IndexHash} to {after.IndexHash}.");
        }

        differences.AddRange(before.PorcelainStatus.Except(after.PorcelainStatus, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(line => $"Status line removed: {line}"));
        differences.AddRange(after.PorcelainStatus.Except(before.PorcelainStatus, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(line => $"Status line added: {line}"));

        CompareContents(before, after, differences, indeterminate);
        CompareByPath(PathKind.GitFile, before.GitFiles, after.GitFiles, differences, indeterminate);
        CompareByPath(PathKind.HiddenTracked, before.HiddenTracked, after.HiddenTracked, differences, indeterminate);
        return (differences, indeterminate);
    }

    private const string ApproximateNote = " (approximate: by sizes and modification times, not by content)";

    private static void CompareContents(UserRepositoryState before, UserRepositoryState after, List<string> differences, List<UnverifiedPath> indeterminate)
    {
        if (before.Contents is null || after.Contents is null)
        {
            return;
        }

        var kept = before.PorcelainStatus.Intersect(after.PorcelainStatus, StringComparer.Ordinal);
        foreach (var path in kept.SelectMany(StatusPaths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var had = before.Contents.TryGetValue(path, out var was);
            var has = after.Contents.TryGetValue(path, out var now);
            if (had != has)
            {
                differences.Add($"Content of '{path}' changed.");
            }
            else if (had)
            {
                Report(Compare(was!, now!), path, PathKind.Status, was!, now!, differences, indeterminate);
            }
        }
    }

    /// <summary>
    /// Compares a part of the captures that keeps every path it finds, whatever the status says (the git files, the
    /// hidden tracked paths): a path only <paramref name="before"/> has is gone, one only <paramref name="after"/> has is
    /// new, and one both have is compared by its fingerprints. A hidden tracked path also comes and goes with its flag in
    /// the index or the checkout of its submodule, and the message says so.
    /// </summary>
    private static void CompareByPath(PathKind kind, IReadOnlyDictionary<string, FileFingerprint>? before, IReadOnlyDictionary<string, FileFingerprint>? after, List<string> differences, List<UnverifiedPath> indeterminate)
    {
        if (before is null || after is null)
        {
            return;
        }

        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var existed = before.TryGetValue(name, out var was);
            var exists = after.TryGetValue(name, out var now);
            if (existed && !exists)
            {
                differences.Add(kind == PathKind.HiddenTracked
                    ? $"{Subject(kind, name)} disappeared: it was deleted, or the status shows it again."
                    : $"{Subject(kind, name)} was deleted.");
            }
            else if (!existed && exists)
            {
                differences.Add(kind == PathKind.HiddenTracked
                    ? $"{Subject(kind, name)} appeared: it was created, or the status no longer shows it."
                    : $"{Subject(kind, name)} was created.");
            }
            else
            {
                Report(Compare(was!, now!), name, kind, was!, now!, differences, indeterminate);
            }
        }
    }

    /// <summary>Which part of a capture a path comes from, which names it in what the comparison says.</summary>
    private enum PathKind
    {
        /// <summary>A path of the status (<see cref="Contents"/>).</summary>
        Status,

        /// <summary>A configuration or hook file (<see cref="GitFiles"/>).</summary>
        GitFile,

        /// <summary>A tracked path no status line can show (<see cref="HiddenTracked"/>).</summary>
        HiddenTracked,
    }

    private static string Subject(PathKind kind, string path) => kind switch
    {
        PathKind.GitFile => $"Git file '{path}'",
        PathKind.HiddenTracked => $"Hidden tracked path '{path}'",
        _ => $"Content of '{path}'",
    };

    /// <summary>Routes the comparison of one path to the differences or the indeterminate list, with the reason that could not be verified.</summary>
    private static void Report(FingerprintComparison comparison, string path, PathKind kind, FileFingerprint before, FileFingerprint after, List<string> differences, List<UnverifiedPath> indeterminate)
    {
        var subject = Subject(kind, path);
        var reason = after.Indeterminate ?? before.Indeterminate;
        switch (comparison)
        {
            case FingerprintComparison.Exact:
                differences.Add($"{subject} changed.");
                break;
            case FingerprintComparison.Approximate:
                differences.Add($"{subject} changed{ApproximateNote}.");
                break;
            case FingerprintComparison.VerifiedOnlyOnce:
                differences.Add($"{subject} changed: one capture verified its content and the other could not ({reason}), which the same content never does.");
                break;
            case FingerprintComparison.Unreadable:
                differences.Add($"{subject} could not be checked: not even its sizes and modification times could be read ({reason ?? "no metadata"}).");
                break;
            case FingerprintComparison.Indeterminate:
                indeterminate.Add(new UnverifiedPath(path, kind == PathKind.GitFile, reason!));
                break;
            default:
                break;
        }
    }

    private enum FingerprintComparison
    {
        Same,
        Exact,
        Approximate,
        VerifiedOnlyOnce,
        Unreadable,
        Indeterminate,
    }

    /// <summary>
    /// How two fingerprints of the same path compare. Without metadata in either, nothing can be compared: unreadable.
    /// One verified and the other not: a difference. Neither verified: by their metadata, indeterminate when it is the
    /// same. Both verified: by the hash when both have one, by the approximation otherwise.
    /// </summary>
    private static FingerprintComparison Compare(FileFingerprint before, FileFingerprint after)
    {
        if (before.Approximation.Length == 0 || after.Approximation.Length == 0)
        {
            return FingerprintComparison.Unreadable;
        }

        var wasVerified = before.Indeterminate is null;
        var isVerified = after.Indeterminate is null;
        if (wasVerified != isVerified)
        {
            return FingerprintComparison.VerifiedOnlyOnce;
        }

        if (!wasVerified)
        {
            return string.Equals(before.Approximation, after.Approximation, StringComparison.Ordinal) ? FingerprintComparison.Indeterminate : FingerprintComparison.Approximate;
        }

        if (before.Sha256 is not null && after.Sha256 is not null)
        {
            return string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal) ? FingerprintComparison.Same : FingerprintComparison.Exact;
        }

        return string.Equals(before.Approximation, after.Approximation, StringComparison.Ordinal) ? FingerprintComparison.Same : FingerprintComparison.Approximate;
    }

    /// <summary>The paths of a status line, <c>XY path</c> or, for a rename or a copy, <c>XY original -&gt; path</c>.</summary>
    private static IEnumerable<string> StatusPaths(string line)
    {
        if (line.Length < 4)
        {
            yield break;
        }

        var paths = line[3..];
        var arrow = paths.IndexOf(" -> ", StringComparison.Ordinal);
        if (line[..2].IndexOfAny(['R', 'C']) >= 0 && arrow > 0)
        {
            yield return paths[..arrow];
            yield return paths[(arrow + 4)..];
        }
        else
        {
            yield return paths;
        }
    }

    private static void CompareRefs(string kind, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, List<string> differences)
    {
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var existed = before.TryGetValue(name, out var was);
            var exists = after.TryGetValue(name, out var now);
            if (existed && !exists)
            {
                differences.Add($"{kind} '{name}' was deleted (it was at {was}).");
            }
            else if (!existed && exists)
            {
                differences.Add($"{kind} '{name}' was created at {now}.");
            }
            else if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                differences.Add($"{kind} '{name}' moved from {was} to {now}.");
            }
        }
    }
}

/// <summary>
/// What a capture of the user's repository knows about the content of one path. <see cref="Sha256"/> is the hash of its
/// bytes when they were read (a file, or a whole untracked or ignored folder or submodule, within the capture budget);
/// <see cref="Approximation"/> is the metadata: the size and modification time of a file, the target of a link, or a
/// digest of the metadata of every entry of a folder. It is empty only when not even the metadata could be read, and
/// then the path can never be compared (<see cref="UserRepositoryState.CompareInDetail"/> reports it as a difference).
/// <see cref="Indeterminate"/>, when set, says the content could not be verified (too large for the per-file cap, beyond
/// the capture budget, or unreadable) and gives the reason.
/// </summary>
public sealed record FileFingerprint(string? Sha256, string Approximation, string? Indeterminate = null);

/// <summary>
/// A path whose content neither capture could verify, with the same metadata in both: a change, if there was one, kept
/// every size and modification time. Whether that blocks is the caller's decision; failing closed, it does.
/// </summary>
/// <param name="Path">The path as the capture keys it: a status path (a folder ends with '/'), a git file or a hidden tracked path.</param>
/// <param name="IsGitFile">A git file (configuration or hook), neither a path the status lists nor a tracked path hidden from it.</param>
/// <param name="Reason">Why the content could not be verified.</param>
public sealed record UnverifiedPath(string Path, bool IsGitFile, string Reason)
{
    public string Description =>
        $"{(IsGitFile ? $"Git file '{Path}'" : $"Content of '{Path}'")} is indeterminate ({Reason}): its content could not be verified, so a change that kept its sizes and modification times cannot be ruled out.";
}

/// <summary>
/// The result of a merge made without a worktree (<c>git merge-tree</c>). A merge that created a commit has
/// <see cref="Merged"/>; a source that was already part of the target has <see cref="AlreadyMerged"/> (a no-op); a
/// conflict has neither flag and lists <see cref="ConflictPaths"/>. A conflict is data, never a half-made merge.
/// </summary>
public sealed record MergeAttempt(bool Merged, bool AlreadyMerged, string? ResultCommit, IReadOnlyList<string> ConflictPaths, string Messages);

/// <summary>
/// The system commit of a task attempt. <see cref="Committed"/> is false when there was nothing to commit or the attempt
/// had already been committed; <see cref="Commit"/> is then the commit that was already there.
/// </summary>
public sealed record CommitResult(bool Committed, string Commit);

public enum RemovalOutcome
{
    Removed,

    /// <summary>The directory was already gone; nothing was done.</summary>
    AlreadyRemoved,

    /// <summary>The worktree has uncommitted changes and discarding them was not requested.</summary>
    RefusedDirty,
}

/// <summary>A ref of the Agent Studio namespace and the commit it points at.</summary>
public sealed record RefEntry(string Name, string Commit);

/// <summary>
/// Why a delivery is refused (ADR-0012, N-01/R3-05): the user's repository configures a callback that the delivery merge,
/// which now runs like any other internal command, would run with a program the repository or a worktree controls.
/// <see cref="Key"/> is the configuration key (<c>filter.&lt;driver&gt;.clean</c>, say). <see cref="Path"/> is where git
/// read it (the origin), and <see cref="Reason"/> says why it is refused, naming the offending word but never the whole
/// value. The result no longer depends on the delivered diff: an in-repository callback is refused whether or not the
/// diff touches it.
/// </summary>
public sealed record DeliveryRefusal(string Key, string Path, string? Reason = null);

/// <summary>
/// A delivery the user approved through <c>IApprovalGate</c>, as a critical action: move the branch checked out in the
/// user's repository from <see cref="ExpectedHead"/> to <see cref="Commit"/> (or merge <see cref="Commit"/> into it).
/// <see cref="IWorktreeManager.DeliverToUserBranchAsync"/> refuses one that does not carry the request that was asked,
/// at <see cref="RiskLevel.Critical"/> and bound to exactly these fields (<see cref="DeliveryApproval"/>), and an
/// explicit yes from someone named that answers that request; and it accepts each approval only once.
/// </summary>
/// <param name="RepositoryRoot">The user's repository (the project root), never a worktree.</param>
/// <param name="TargetBranch">The branch that was checked out when the delivery was approved (<c>main</c> or <c>refs/heads/main</c>).</param>
/// <param name="ExpectedHead">The commit that branch was at when the delivery was approved: if HEAD moved or switched branch since, nothing is done.</param>
/// <param name="Commit">The approved commit of the run's branch, never a branch name.</param>
/// <param name="Message">The message of the merge commit (<see cref="DeliveryMode.MergeCommit"/>); it reaches git on standard input.</param>
/// <param name="Request">The request <c>IApprovalGate</c> was asked.</param>
/// <param name="Decision">Its answer.</param>
public sealed record ApprovedDelivery(
    string RepositoryRoot,
    string TargetBranch,
    string ExpectedHead,
    string Commit,
    DeliveryMode Mode,
    string Message,
    ApprovalRequest Request,
    ApprovalDecision Decision);

/// <summary>
/// Binds the approval of a delivery to exactly what it approves (ADR-0012): the repository, the target branch, the
/// mode, the commit and the expected head, with a fresh nonce, so two approvals of the same delivery are two different
/// tokens. <c>DeliveryService</c> puts the binding in the <see cref="ApprovalRequest"/>, the gate answers with it
/// (<see cref="ApprovalDecision.Answers"/>), and <see cref="IWorktreeManager.DeliverToUserBranchAsync"/> recomputes it
/// from the <see cref="ApprovedDelivery"/> before any git command runs, and accepts it only once.
/// </summary>
public static class DeliveryApproval
{
    private const string Version = "delivery-v1";

    /// <summary>A new binding for this delivery (each call has its own nonce).</summary>
    public static string Bind(string repositoryRoot, string targetBranch, DeliveryMode mode, string commit, string expectedHead)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return $"{Version}:{nonce}:{Digest(nonce, repositoryRoot, targetBranch, mode, commit, expectedHead)}";
    }

    /// <summary>True when <paramref name="binding"/> was made for exactly the delivery <paramref name="delivery"/> describes.</summary>
    public static bool Matches(string? binding, ApprovedDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var parts = binding?.Split(':');
        if (parts is not [Version, { Length: 32 } nonce, { Length: ContentHash.HexLength } digest])
        {
            return false;
        }

        var expected = Digest(nonce, delivery.RepositoryRoot, delivery.TargetBranch, delivery.Mode, delivery.Commit, delivery.ExpectedHead);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(digest));
    }

    private static string Digest(string nonce, string? repositoryRoot, string? targetBranch, DeliveryMode mode, string? commit, string? expectedHead) =>
        ContentHash.Sha256Hex(ContentHash.Canonical(
            Version,
            nonce,
            repositoryRoot,
            targetBranch,
            mode.ToString(),
            commit?.ToLowerInvariant(),
            expectedHead?.ToLowerInvariant()));
}

public enum UserBranchDeliveryStatus
{
    /// <summary>The user's branch now contains the commit.</summary>
    Delivered,

    /// <summary>HEAD is no longer on the approved branch at the approved commit: nothing was done.</summary>
    RepositoryChanged,

    /// <summary>
    /// The user's working tree has changes, untracked or ignored files in paths the delivery changes (git overwrites an
    /// ignored file without a word): nothing was done.
    /// </summary>
    DirtyPaths,

    /// <summary>A merge commit would have conflicts: nothing was done, nothing with conflict markers reached the user's tree.</summary>
    Conflicts,

    /// <summary>
    /// git merged into the user's working tree and index, but the commit failed (usually a hook of the user refused it):
    /// the branch did not move and the merge is in progress (<c>git commit</c> finishes it, <c>git merge --abort</c> undoes it).
    /// </summary>
    IncompleteMerge,

    /// <summary>git refused or failed (it aborts by itself, for example when the delivery is not a fast-forward); see the output.</summary>
    GitFailed,
}

/// <param name="HeadCommit">The commit the user's HEAD is at afterwards.</param>
/// <param name="Paths">The dirty or conflicting paths that stopped the delivery.</param>
/// <param name="Output">What git said.</param>
public sealed record UserBranchDelivery(UserBranchDeliveryStatus Status, string HeadCommit, IReadOnlyList<string> Paths, string Output)
{
    public bool Delivered => Status == UserBranchDeliveryStatus.Delivered;
}

/// <summary>
/// Worktrees outside the repository, hidden refs (<c>refs/agentstudio/&lt;run&gt;/&lt;task&gt;</c>) and merges by
/// <c>merge-tree</c> plus an expected-value <c>update-ref</c>, so every operation is idempotent. Only worktrees the
/// system owns are ever changed or removed, and every command that changes one takes its repository and administrative
/// directory from the <see cref="WorktreeLease"/> (explicit <c>--git-dir</c> and <c>--work-tree</c>), never from the
/// <c>.git</c> file inside the worktree.
/// </summary>
/// <remarks>
/// The ref operations (<see cref="ResolveCommitAsync"/> to <see cref="DeleteRefAsync"/>), <see cref="FindHookPathsAsync"/>,
/// <see cref="FindDeliveryRefusalsAsync"/>, <see cref="DeliverToUserBranchAsync"/>, the candidate of an integration (<see cref="PrepareCandidateAsync"/> to
/// <see cref="FastForwardRunBranchAsync"/>) and <see cref="ArchiveCommitAsync"/> came later. Their default bodies refuse (<see cref="NotSupportedException"/>)
/// so an implementation written before them still compiles; the git implementation has all of them. Only refs of the
/// Agent Studio namespace (<c>refs/agentstudio/**</c> and the <c>agentstudio/**</c> branches) are created, moved or
/// deleted, always as a compare-and-swap that never follows a symbolic ref.
/// <para>
/// A git implementation walks the repository's git directory before every command that can write into it (a content
/// command such as the status of <see cref="InspectAsync"/> or of <see cref="CaptureUserRepositoryStateAsync"/>, a ref or an
/// object write) and before a delivery, and refuses a link planted anywhere in it (ADR-0012, S-01 to S-03): those
/// operations then fail instead of running git.
/// </para>
/// </remarks>
public interface IWorktreeManager
{
    Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Captures the state of the user's own repository (read-only). The engine compares a capture taken before an agent
    /// run with one taken after it and fails the task if anything changed.
    /// </summary>
    Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken);

    /// <summary>Creates the worktree, or adopts it if it exists with the same base and lock reason; a mismatch fails closed.</summary>
    Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken);

    /// <summary>Commits everything in the worktree as "Agent Studio" (the agent never commits). The message goes on standard input.</summary>
    Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken);

    Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken);

    /// <summary>Merges <paramref name="source"/> into <paramref name="targetRef"/> if it still points at <paramref name="expectedTarget"/>.</summary>
    Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken);

    /// <summary>Moves an owned worktree back to <paramref name="commit"/> (reset and clean). Refuses any other worktree.</summary>
    Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken);

    /// <summary>Removes an owned worktree. One with uncommitted changes is refused unless <paramref name="discardChanges"/> is set (which callers ask the user for).</summary>
    Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken);

    /// <summary>
    /// The commit <paramref name="revision"/> points at, or null when it does not exist. Only a full object id or a ref
    /// of the Agent Studio namespace is accepted (anything else is refused before git runs), so nothing can be read as
    /// an option, a range or a reflog expression.
    /// </summary>
    Task<string?> ResolveCommitAsync(string repoRoot, string revision, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not resolve refs.");

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="descendant"/> or one of its ancestors. Same revisions as <see cref="ResolveCommitAsync"/>.</summary>
    Task<bool> IsAncestorAsync(string repoRoot, string ancestor, string descendant, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not check ancestry.");

    /// <summary>
    /// The run's branch, <c>agentstudio/run-&lt;run&gt;</c> (the target of the integration and the source of the
    /// delivery): created at <paramref name="baseCommit"/> when it does not exist. Returns the commit it points at, which
    /// is the base the first time and wherever the integration took it afterwards; whether it still descends from the
    /// base is the caller's check.
    /// </summary>
    Task<string> EnsureRunBranchAsync(string repoRoot, RunId run, string baseCommit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not create run branches.");

    /// <summary>The run's branch and every hidden ref of the run (<c>refs/agentstudio/&lt;run&gt;/**</c>), with the commit each points at, sorted by name.</summary>
    Task<IReadOnlyList<RefEntry>> ListRunRefsAsync(string repoRoot, RunId run, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not list run refs.");

    /// <summary>
    /// Deletes a ref of the Agent Studio namespace if it still points at <paramref name="expectedCommit"/>. False when it
    /// moved or was already gone (nothing is deleted then). A symbolic ref is deleted itself, never the ref it names.
    /// </summary>
    Task<bool> DeleteRefAsync(string repoRoot, string refName, string expectedCommit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not delete refs.");

    /// <summary>
    /// Historic member, kept for source compatibility. The delivery no longer runs the user's hooks (R3-05), so the
    /// approval no longer names changed hook files, and the git implementation no longer provides this. Do not call it.
    /// </summary>
    Task<IReadOnlyList<string>> FindHookPathsAsync(string repoRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not look for hook paths.");

    /// <summary>
    /// What <see cref="DeliverToUserBranchAsync"/> would refuse before spending an approval: the delivery merge runs like
    /// any other internal command (R3-05), so this returns the allowlist classifier's refusals for the user's effective
    /// configuration (an in-repository filter, merge driver, credential helper and the like). Read-only; the same check
    /// the merge repeats, because the repository can change in between. Empty when nothing would be refused.
    /// <paramref name="head"/> and <paramref name="commit"/> are validated as full object ids of commits the repository
    /// holds, but the result does not depend on the diff between them. A file the merge would write reached through a link
    /// (the branch, its reflog, <c>ORIG_HEAD</c>, the merge files) fails with an exception instead, also before any
    /// approval is asked (H-03, ADR-0012), and so does a link anywhere in the git directory (S-01 to S-03).
    /// </summary>
    Task<IReadOnlyList<DeliveryRefusal>> FindDeliveryRefusalsAsync(string repoRoot, string head, string commit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not look for what a delivery would refuse.");

    /// <summary>
    /// The only operation that changes the user's branch: the approved delivery (ADR-0012). Without the approval it
    /// carries, bound to this very delivery and not used before, nothing runs. It does nothing either when HEAD left
    /// the approved branch or commit, or when the working tree has changes, untracked or ignored files in the paths the
    /// delivery changes. Otherwise
    /// <see cref="DeliveryMode.FastForwardOnly"/> is <c>git merge --ff-only</c>, and <see cref="DeliveryMode.MergeCommit"/>
    /// merges without conflicts or does nothing. The user's hooks, identity and signing apply; git aborts by itself when
    /// the delivery would overwrite something.
    /// </summary>
    Task<UserBranchDelivery> DeliverToUserBranchAsync(ApprovedDelivery delivery, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not deliver to the user's branch.");

    /// <summary>
    /// The candidate of an integration task, <c>refs/agentstudio/&lt;run&gt;/_candidate/&lt;task&gt;</c>: the hidden ref the
    /// integration merges into and verifies, so the run's branch only ever points at verified integrations (ADR-0012). It
    /// is created at <paramref name="startCommit"/> (the head of the run's branch) when it does not exist; kept when
    /// <paramref name="startCommit"/> is already part of it (what an earlier attempt of the task merged, which a resumed
    /// integration reuses); otherwise it is moved back to <paramref name="startCommit"/>, as a compare-and-swap that never
    /// follows a symbolic ref. Returns the commit it points at. The run's branch is never touched.
    /// </summary>
    Task<string> PrepareCandidateAsync(string repoRoot, RunId run, TaskId task, string startCommit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not prepare integration candidates.");

    /// <summary>
    /// Merges <paramref name="source"/> into the candidate of the integration task (<see cref="PrepareCandidateAsync"/>) if
    /// it still points at <paramref name="expectedCandidate"/>: <c>merge-tree</c> without a worktree, a merge commit and a
    /// compare-and-swap of the candidate, as <see cref="MergeIntoAsync"/> does. The target comes from the run and the task,
    /// never from a ref name, so a merge can never move the run's branch. A source already in the candidate is a no-op
    /// (<see cref="MergeAttempt.AlreadyMerged"/>, with the same commit); a conflict is reported and nothing is written.
    /// </summary>
    Task<MergeAttempt> MergeIntoCandidateAsync(string repoRoot, RunId run, TaskId task, string expectedCandidate, string source, string message, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not merge into integration candidates.");

    /// <summary>
    /// Moves the run's branch, <c>agentstudio/run-&lt;run&gt;</c>, forward from <paramref name="expectedHead"/> to
    /// <paramref name="newHead"/>: the only way an integration moves it, once its candidate was verified. A compare-and-swap
    /// that never follows a symbolic ref. It is refused, and nothing moves, when <paramref name="newHead"/> does not descend
    /// from <paramref name="expectedHead"/> or when the branch is no longer at <paramref name="expectedHead"/> (it moved
    /// meanwhile). A branch already at <paramref name="newHead"/> is left as it is: an earlier call completed.
    /// </summary>
    Task FastForwardRunBranchAsync(string repoRoot, RunId run, string expectedHead, string newHead, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not fast-forward run branches.");

    /// <summary>
    /// Writes the tree of <paramref name="commit"/>, exactly as committed, into the empty folder
    /// <paramref name="destination"/>: the copy an isolated verification runs on (ADR-0017). The objects are read from the
    /// project's repository at <paramref name="repoRoot"/>, never through the <c>.git</c> file of a worktree, which an
    /// agent can rewrite to name an object store of its own. No configuration, hook, filter or attribute of the repository
    /// or of the commit changes the copy, only its end-of-line settings (as in a checkout); only regular files and folders
    /// are written. The commit must be a full object id the repository holds. When it fails, the folder may hold part of
    /// the tree: the caller removes it.
    /// </summary>
    Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This worktree manager does not copy commits.");
}

/// <summary>One branch to merge: the hidden ref of a task, in the deterministic merge order.</summary>
public sealed record IntegrationSource(TaskId Task, string Ref);

/// <summary>
/// Merges the sources, in the order given, into the candidate of the integration task, verifies it in the
/// <c>_integration</c> worktree and, only when every step passed, fast-forwards the run's branch to it.
/// </summary>
public sealed record IntegrationRequest(
    ProjectContext Project,
    RunId Run,
    TaskId Task,
    int Attempt,
    string BaseCommit,
    IReadOnlyList<IntegrationSource> Sources,
    IReadOnlyList<VerificationStep> Verification);

/// <summary>A conflict is a failure with evidence (<see cref="ConflictPaths"/>); it is never resolved automatically.</summary>
/// <param name="ResultCommit">
/// The candidate the merges produced. When <see cref="Succeeded"/>, it was verified and the run's branch now points at it;
/// otherwise it is the candidate that failed its verification, or the partial head of a conflict, which only the
/// candidate ref holds: the run's branch did not move.
/// </param>
/// <param name="EvidenceRecords">
/// The evidence of <see cref="Evidence"/>, in the same order, exactly as the evidence store returned each record when the
/// integrator captured it. The engine anchors these (and those of <see cref="Verification"/>) in the decision log, never a
/// row read back from the store: an id with no record in either is not anchored and fails the integration. Null from an
/// integrator that does not hand them back.
/// </param>
public sealed record IntegrationResult(
    bool Succeeded,
    string? ResultCommit,
    IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<VerificationResult> Verification,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyList<Domain.Evidence.EvidenceRecord>? EvidenceRecords = null);

public interface IIntegrator
{
    Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken);
}

public enum DeliveryMode
{
    /// <summary>Move the user's branch forward to the integration commit; git refuses if it would overwrite anything.</summary>
    FastForwardOnly,

    /// <summary>Merge the integration commit with a merge commit (hooks and signing of the user apply).</summary>
    MergeCommit,
}

/// <summary>
/// The outcome of a delivery. <see cref="Message"/> is the Spanish sentence; <see cref="MessageText"/> is the same as a coded text
/// for the screen (ADR-0054 addendum of 2026-11-12), null when the producer gives none: the screen then shows the sentence.
/// </summary>
public sealed record DeliveryResult(bool Delivered, string? ResultCommit, string TargetBranch, string Message, UserText? MessageText = null);

/// <summary>
/// Whether a run may be delivered, from its decision log and its verdicts. <see cref="Commit"/> is the integration commit
/// an independent judge approved (the only one a delivery takes), or null when the run may not be delivered;
/// <see cref="Reason"/> says why, for the person.
/// </summary>
public sealed record DeliveryReadiness(bool Ready, string? Commit, string Reason, UserText? ReasonText = null);

/// <summary>Delivers the integration branch to the user's own branch. It always asks <c>IApprovalGate</c> first (critical action).</summary>
public interface IDeliveryService
{
    /// <summary>
    /// Read-only and without git: the run may be delivered only when its decision log verifies, it ended
    /// <see cref="RunPhase.Succeeded"/>, and a valid Approved verdict (in the log and in the verdict store) reviewed the
    /// integration it ended with; since Phase 5, a revision that supersedes it confirms the approval (one that turns a
    /// rejection into an approval needs the user's acceptance), and no challenge nor replan request of the orchestrator's
    /// auditor is pending (docs/15, ADR-0012 addendum). <see cref="DeliverAsync"/> checks this first.
    /// </summary>
    Task<DeliveryReadiness> CheckAsync(ProjectContext project, RunId run, CancellationToken cancellationToken);

    Task<DeliveryResult> DeliverAsync(ProjectContext project, RunId run, DeliveryMode mode, CancellationToken cancellationToken);
}

public sealed record CleanupReport(IReadOnlyList<string> RemovedWorktrees, IReadOnlyList<string> RemovedRefs, IReadOnlyList<string> Kept);

/// <summary>Removes the worktrees and hidden refs of a run. Anything with work not yet integrated goes through <c>IApprovalGate</c>.</summary>
public interface IWorkspaceCleanup
{
    Task<CleanupReport> CleanAsync(ProjectContext project, RunId run, CancellationToken cancellationToken);
}
