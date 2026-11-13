namespace AgentStudio.Domain.Hosting;

/// <summary>What a pull request counts for as the delivery of a run (docs/21 D-29).</summary>
public enum PullRequestIntegration
{
    /// <summary>It was not merged (open, draft or closed): it delivers nothing.</summary>
    NotMerged,

    /// <summary>It was merged and the head it was merged with is the commit the judge saw: it counts as «integrado».</summary>
    Integrated,

    /// <summary>
    /// It was merged, but not with the commit the judge saw (somebody pushed after the verdict and it was merged on the web), or the log does not say what the
    /// judge saw: it does not count as «integrado». The card stays in «hecho» with the reason, and the report of the milestone lists it as a deviation.
    /// </summary>
    MergedWithUnjudgedChanges,
}

/// <summary>
/// The rule of D-29, in one place and pure: when a merged pull request counts as the integration of a run, and when a commit is «desplegado». The board, the
/// view of the pull request and the succession of commits all ask here, so to change the rule (a person may want another one) this is the only place to edit.
/// </summary>
/// <remarks>
/// <b>Today's rule.</b> A pull request integrates a run only if it was merged and its head, as it stood when it was merged, is the commit the judge saw (the dossier
/// of the verdict that decided). If the head was another one, or the log does not say what the judge saw, it does not: nothing reaches «integrado» without a
/// verdict of approval and a valid one <i>of that very commit</i>. A revert never takes «integrado» away by itself: it proposes reopening the milestone (D-27).
/// </remarks>
public static class IntegrationByPullRequest
{
    /// <summary>What the pull request counts for, from its latest observation and the commit the judge saw (null: the log does not say).</summary>
    public static PullRequestIntegration Of(RemoteSnapshot latest, string? judgedCommit)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (latest.State != PullRequestState.Merged)
        {
            return PullRequestIntegration.NotMerged;
        }

        return HeadIsJudgedCommit(latest, judgedCommit) ? PullRequestIntegration.Integrated : PullRequestIntegration.MergedWithUnjudgedChanges;
    }

    /// <summary>Whether the head of the pull request is the commit the judge saw; false when the log does not say which it was.</summary>
    public static bool HeadIsJudgedCommit(RemoteSnapshot snapshot, string? judgedCommit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return judgedCommit is not null && string.Equals(snapshot.HeadCommit, judgedCommit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The commits a merge left on the base branch: the results the platform listed, or else the merge commit; empty for what is not merged.</summary>
    public static IReadOnlyList<string> ResultCommitsOf(RemoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != PullRequestState.Merged)
        {
            return [];
        }

        return snapshot.ResultCommits.Count > 0 ? snapshot.ResultCommits : snapshot.MergeCommit is { } merge ? [merge] : [];
    }

    /// <summary>
    /// Whether the platform says a commit the merge left was deployed successfully (D-29): a deployment in the state <c>Succeeded</c> of one of the result commits.
    /// Without that data the state does not exist: there is no «no desplegado».
    /// </summary>
    public static bool IsDeployed(RemoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Deployment is { State: DeploymentState.Succeeded } deployment
            && ResultCommitsOf(snapshot).Contains(deployment.Commit, StringComparer.Ordinal);
    }
}

/// <summary>
/// Which of several observations of the same pull request is the final state, independently of the order they come in (docs/21 D-23): the log may repeat an
/// observation or, read by a reader that does not sort, hand them out of order, and the projection must not change. A total order on content: merged is terminal,
/// then the later <c>UpdatedAt</c>; the platform does not move <c>updated_at</c> for a revert or a deployment, so on a tie what only grows decides (force pushes,
/// reverts, the id of the deployment, the commits), and the fingerprint settles what is left.
/// </summary>
public static class RemoteSnapshotOrder
{
    /// <summary>The final observation of the pull request among <paramref name="snapshots"/>, which must all be of the same one.</summary>
    public static RemoteSnapshot Latest(IEnumerable<RemoteSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        RemoteSnapshot? best = null;
        foreach (var snapshot in snapshots)
        {
            if (best is null || Compare(snapshot, best) > 0)
            {
                best = snapshot;
            }
        }

        return best ?? throw new ArgumentException("There is no observation to choose from.", nameof(snapshots));
    }

    /// <summary>Positive when <paramref name="a"/> is later than <paramref name="b"/>.</summary>
    public static int Compare(RemoteSnapshot a, RemoteSnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var order = (a.State == PullRequestState.Merged).CompareTo(b.State == PullRequestState.Merged);
        if (order == 0)
        {
            order = a.UpdatedAt.CompareTo(b.UpdatedAt);
        }

        if (order == 0)
        {
            order = a.ForcePushes.CompareTo(b.ForcePushes);
        }

        if (order == 0)
        {
            order = a.RevertCommits.Count.CompareTo(b.RevertCommits.Count);
        }

        if (order == 0)
        {
            order = (a.Deployment?.Id ?? -1).CompareTo(b.Deployment?.Id ?? -1);
        }

        if (order == 0)
        {
            order = (a.OriginalCommits.Count + a.ResultCommits.Count).CompareTo(b.OriginalCommits.Count + b.ResultCommits.Count);
        }

        return order != 0 ? order : string.CompareOrdinal(a.Fingerprint, b.Fingerprint);
    }
}
