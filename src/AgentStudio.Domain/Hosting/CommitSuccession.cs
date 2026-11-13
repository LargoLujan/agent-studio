namespace AgentStudio.Domain.Hosting;

/// <summary>
/// What became of a commit on the remote (docs/21 D-28): the pull request, how it was merged, the commits it left on the base branch, whether the
/// head of the pull request was the commit the judge saw, and the commits that revert the merge. A projection: built from the observations of the log.
/// </summary>
public sealed record CommitSuccessor(PullRequestRef PullRequest, RemoteMergeMode Mode, IReadOnlyList<string> ResultCommits, bool HeadWasJudgedCommit, IReadOnlyList<string> RevertedBy);

/// <summary>
/// Where a commit of the remote comes from and what became of a commit of the system (docs/21 D-28), as a pure projection of the merged snapshots:
/// nothing is stored. A squash leaves one result for every original; a merge commit leaves the merge commit; a rebase leaves one result per original, in
/// order, when the counts match (and the whole list otherwise). A commit that no merged pull request names has no successor.
/// </summary>
/// <remarks>The ruling of the cases the observation cannot decide (a truncated list, an unknown mode) is completed by WP4; this is the shape and the common cases.</remarks>
public sealed class CommitSuccession
{
    private readonly Dictionary<string, CommitSuccessor> _successors;
    private readonly Dictionary<string, IReadOnlyList<string>> _originals;

    private CommitSuccession(Dictionary<string, CommitSuccessor> successors, Dictionary<string, IReadOnlyList<string>> originals)
    {
        _successors = successors;
        _originals = originals;
    }

    /// <summary>Nothing merged: no commit has a successor.</summary>
    public static CommitSuccession Empty { get; } = new([], []);

    /// <summary>
    /// The projection of the merged snapshots. Of several observations of the same pull request the latest (<see cref="RemoteSnapshot.UpdatedAt"/>) counts.
    /// <paramref name="judgedCommitOf"/> gives the commit the judge saw for the run of a pull request, or null when the log does not say.
    /// </summary>
    public static CommitSuccession From(IEnumerable<RemoteSnapshot> mergedSnapshots, Func<PullRequestRef, string?> judgedCommitOf)
    {
        ArgumentNullException.ThrowIfNull(mergedSnapshots);
        ArgumentNullException.ThrowIfNull(judgedCommitOf);
        var successors = new Dictionary<string, CommitSuccessor>(StringComparer.Ordinal);
        var originals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var latest = mergedSnapshots
            .Where(s => s.State == PullRequestState.Merged)
            .GroupBy(s => s.PullRequest.TraceId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(s => s.UpdatedAt).Last());
        foreach (var snapshot in latest)
        {
            var mode = snapshot.MergeMode ?? RemoteMergeMode.Unknown;
            var results = snapshot.ResultCommits.Count > 0 ? snapshot.ResultCommits : snapshot.MergeCommit is { } merge ? [merge] : [];
            var judged = judgedCommitOf(snapshot.PullRequest);
            var head = judged is not null && string.Equals(judged, snapshot.HeadCommit, StringComparison.Ordinal);
            var oneToOne = mode == RemoteMergeMode.Rebase && results.Count == snapshot.OriginalCommits.Count;
            for (var i = 0; i < snapshot.OriginalCommits.Count; i++)
            {
                IReadOnlyList<string> mine = oneToOne ? [results[i]] : results;
                successors[snapshot.OriginalCommits[i]] = new CommitSuccessor(snapshot.PullRequest, mode, mine, head, snapshot.RevertCommits);
            }

            for (var i = 0; i < results.Count; i++)
            {
                originals[results[i]] = oneToOne ? [snapshot.OriginalCommits[i]] : snapshot.OriginalCommits;
            }
        }

        return new CommitSuccession(successors, originals);
    }

    /// <summary>What became of an original commit, or null when no merged pull request names it.</summary>
    public CommitSuccessor? SuccessorOf(string originalCommit) => _successors.GetValueOrDefault(originalCommit);

    /// <summary>The original commits a commit of the remote comes from; empty when it comes from none the log knows.</summary>
    public IReadOnlyList<string> OriginalsOf(string resultCommit) => _originals.GetValueOrDefault(resultCommit) ?? [];
}
