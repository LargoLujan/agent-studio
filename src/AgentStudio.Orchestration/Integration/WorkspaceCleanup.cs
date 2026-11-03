using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Integration;

/// <summary>
/// Removes the worktrees and refs of one run (docs/13 section 5, ADR-0012). What can be removed without losing work goes
/// without asking; anything destructive goes through <see cref="IApprovalGate"/> first, in a single request.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only the run's own worktrees (owned and locked for that run) and refs are touched: the hidden refs under
/// <c>refs/agentstudio/&lt;run&gt;/</c> and the branch <c>agentstudio/run-&lt;run&gt;</c>. Nothing else is ever deleted,
/// even if git listed it.</item>
/// <item>A clean worktree is removed (its commits stay reachable from its refs). One with uncommitted changes is only
/// discarded after approval. One still in use is kept, with its refs, and the rest is cleaned.</item>
/// <item>A ref holds unintegrated work when its commit is neither in the user's branch (HEAD) nor, for a task ref, in
/// the run's branch. Deleting such refs needs approval; a run branch that was not delivered is one of them.</item>
/// <item>Refs are deleted as a compare-and-swap against the commit they had when listed: one that moved is kept.</item>
/// <item>The refs of a worktree that is kept stay with it.</item>
/// <item>Every git operation goes through <see cref="IWorktreeManager"/>, the hardened git layer: it refuses refs reached
/// through a link, so a junction planted among the run's refs stops the cleanup instead of deleting a user's branch.</item>
/// </list>
/// </remarks>
public sealed class WorkspaceCleanup : IWorkspaceCleanup
{
    public const string ApprovalAction = "discard-run-work";

    private readonly IWorktreeManager _worktrees;
    private readonly IApprovalGate _approvals;

    public WorkspaceCleanup(IWorktreeManager worktrees, IApprovalGate approvals)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(approvals);

        _worktrees = worktrees;
        _approvals = approvals;
    }

    public async Task<CleanupReport> CleanAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var branch = IntegrationNames.BranchRef(run);
        var state = await _worktrees.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false);

        var removedWorktrees = new List<string>();
        var kept = new List<string>();
        var keptNames = new HashSet<string>(StringComparer.Ordinal);
        var dirty = new List<WorktreeLease>();

        var leases = (await _worktrees.ListOwnedAsync(project, cancellationToken).ConfigureAwait(false))
            .Where(l => l.Run == run)
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var lease in leases)
        {
            switch (await TryRemoveAsync(lease, discardChanges: false, cancellationToken).ConfigureAwait(false))
            {
                case RemovalOutcome.RefusedDirty:
                    dirty.Add(lease);
                    break;
                case null:
                    kept.Add(lease.Path);
                    keptNames.Add(lease.Name);
                    break;
                default:
                    removedWorktrees.Add(lease.Path);
                    break;
            }
        }

        var refs = (await _worktrees.ListRunRefsAsync(state.Root, run, cancellationToken).ConfigureAwait(false))
            .Where(r => IntegrationNames.BelongsToRun(r.Name, run))
            .ToList();
        var integration = refs.FirstOrDefault(r => r.Name == branch)?.Commit;
        var unintegrated = new List<RefEntry>();
        foreach (var entry in refs)
        {
            if (!await IsIntegratedAsync(state, entry, branch, integration, cancellationToken).ConfigureAwait(false))
            {
                unintegrated.Add(entry);
            }
        }

        var approved = true;
        if (dirty.Count > 0 || unintegrated.Count > 0)
        {
            var decision = await _approvals.RequestAsync(
                new ApprovalRequest(
                    ApprovalAction,
                    Describe(run, dirty, unintegrated),
                    RiskLevel.Destructive,
                    RequestedBy: null,
                    AffectedPaths: [.. dirty.Select(l => l.Path), .. unintegrated.Select(r => r.Name)],
                    ProjectId: project.ProjectId,
                    RunId: run.Value),
                cancellationToken).ConfigureAwait(false);
            approved = decision.Approved;
        }

        foreach (var lease in dirty)
        {
            if (approved && await TryRemoveAsync(lease, discardChanges: true, cancellationToken).ConfigureAwait(false) is RemovalOutcome.Removed or RemovalOutcome.AlreadyRemoved)
            {
                removedWorktrees.Add(lease.Path);
            }
            else
            {
                kept.Add(lease.Path);
                keptNames.Add(lease.Name);
            }
        }

        var removedRefs = new List<string>();
        foreach (var entry in refs)
        {
            var keep = (!approved && unintegrated.Contains(entry)) || BelongsToKeptWorktree(entry.Name, run, keptNames);
            if (!keep && await _worktrees.DeleteRefAsync(state.Root, entry.Name, entry.Commit, cancellationToken).ConfigureAwait(false))
            {
                removedRefs.Add(entry.Name);
            }
            else
            {
                kept.Add(entry.Name);
            }
        }

        return new CleanupReport(removedWorktrees, removedRefs, kept);
    }

    /// <summary>The outcome, or null when the worktree is in use (the git layer throws <see cref="IOException"/> and removes nothing).</summary>
    private async Task<RemovalOutcome?> TryRemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken)
    {
        try
        {
            return await _worktrees.RemoveAsync(lease, discardChanges, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<bool> IsIntegratedAsync(RepositoryState state, RefEntry entry, string branch, string? integration, CancellationToken cancellationToken)
    {
        if (await ContainsAsync(state.Root, state.HeadCommit, entry.Commit, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // A task's work also survives in the run's branch, unless that branch is the ref being judged.
        return entry.Name != branch
            && integration is not null
            && await ContainsAsync(state.Root, integration, entry.Commit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ContainsAsync(string repoRoot, string head, string commit, CancellationToken cancellationToken) =>
        string.Equals(head, commit, StringComparison.OrdinalIgnoreCase)
        || await _worktrees.IsAncestorAsync(repoRoot, commit, head, cancellationToken).ConfigureAwait(false);

    // refs/agentstudio/<run>/<name> and refs/agentstudio/<run>/_start/<name> belong to the worktree <name>.
    private static bool BelongsToKeptWorktree(string refName, RunId run, IReadOnlySet<string> keptNames) =>
        keptNames.Count > 0
        && refName.StartsWith(IntegrationNames.RunRefPrefix(run), StringComparison.Ordinal)
        && keptNames.Contains(refName[(refName.LastIndexOf('/') + 1)..]);

    private static string Describe(RunId run, IReadOnlyList<WorktreeLease> dirty, IReadOnlyList<RefEntry> unintegrated)
    {
        var parts = new List<string>();
        if (dirty.Count > 0)
        {
            parts.Add($"se perderán los cambios sin confirmar de {dirty.Count} worktree(s) ({string.Join(", ", dirty.Select(l => l.Name))})");
        }

        if (unintegrated.Count > 0)
        {
            parts.Add($"se borrarán {unintegrated.Count} ref(s) con trabajo que no está en tu rama ni en la integración ({string.Join(", ", unintegrated.Select(r => r.Name))})");
        }

        return $"Limpiar la ejecución {run.Value}: {string.Join("; y ", parts)}.";
    }
}
