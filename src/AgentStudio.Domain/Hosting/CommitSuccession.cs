namespace AgentStudio.Domain.Hosting;

/// <summary>
/// What became of a commit on the remote (docs/21 D-28): the pull request, how it was merged, the commits it left on the base branch, whether the
/// head of the pull request was the commit the judge saw, and the commits that revert the merge. A projection: built from the observations of the log.
/// </summary>
/// <param name="ModeIsDeduced">
/// The mode is a deduction, and is shown as one: it is a fact only when Agent Studio itself merged with that method (the log has the operation); a merge made
/// outside is deduced from the commits (D-27), and «squash o rebase» and «desconocido» are never a fact. The default is the careful one.
/// </param>
/// <param name="CommitsTruncated">
/// The platform listed up to 250 commits of the pull request (D-28): the list of originals is incomplete, a commit of the pull request may not be in it, and the
/// pairing of a rebase is not trusted. It is said, never hidden.
/// </param>
/// <param name="RevertedBy">The commits that revert the merge, deduced from the standard line of git in their message (D-27): shown as «posible reversión».</param>
public sealed record CommitSuccessor(
    PullRequestRef PullRequest,
    RemoteMergeMode Mode,
    IReadOnlyList<string> ResultCommits,
    bool HeadWasJudgedCommit,
    IReadOnlyList<string> RevertedBy,
    bool ModeIsDeduced = true,
    bool CommitsTruncated = false);

/// <summary>
/// The stable codes with which a step of the chain says what it is (the desktop has the text of each, ADR-0054): the mode of a merge, a revert and a deployment.
/// </summary>
public static class RemoteTraceTitles
{
    public const string RevertedBy = "remote.reverted-by";
    public const string Deployed = "remote.deployed";

    /// <summary>The code of a merge mode: <c>remote.merge.squash</c>, <c>remote.merge.rebase</c>, <c>remote.merge.merge-commit</c>, <c>remote.merge.squash-or-rebase</c> or <c>remote.merge.unknown</c>.</summary>
    public static string MergeMode(RemoteMergeMode mode) => mode switch
    {
        RemoteMergeMode.MergeCommit => "remote.merge.merge-commit",
        RemoteMergeMode.Squash => "remote.merge.squash",
        RemoteMergeMode.Rebase => "remote.merge.rebase",
        RemoteMergeMode.SquashOrRebase => "remote.merge.squash-or-rebase",
        _ => "remote.merge.unknown",
    };
}

/// <summary>
/// Where a commit of the remote comes from and what became of a commit of the system (docs/21 D-28), as a pure projection of the observed snapshots: nothing is
/// stored. A squash leaves one result for every original; a merge commit leaves the merge commit; a rebase leaves one result per original, in order, when the
/// counts match and the list of originals is complete (and the whole list otherwise); an unknown mode leaves the whole list, since nothing says which result is
/// which. A commit that no merged pull request names has no successor.
/// </summary>
/// <remarks>
/// <b>Idempotent and independent of order.</b> Of several observations of the same pull request the final one counts (<see cref="RemoteSnapshotOrder"/>), whatever
/// order they come in and however often they repeat; and when two merged pull requests name the same commit the one merged later wins (then by identifier), so the
/// result does not depend on the order of the input either.
/// </remarks>
public sealed class CommitSuccession
{
    private readonly Dictionary<string, CommitSuccessor> _successors;
    private readonly Dictionary<string, IReadOnlyList<string>> _originals;
    private readonly HashSet<string> _truncatedResults;
    private readonly Dictionary<string, CommitSuccessor> _byPullRequest;

    private CommitSuccession(Dictionary<string, CommitSuccessor> successors, Dictionary<string, IReadOnlyList<string>> originals, HashSet<string> truncatedResults, Dictionary<string, CommitSuccessor> byPullRequest)
    {
        _successors = successors;
        _originals = originals;
        _truncatedResults = truncatedResults;
        _byPullRequest = byPullRequest;
    }

    /// <summary>Nothing merged: no commit has a successor.</summary>
    public static CommitSuccession Empty { get; } = new([], [], [], []);

    /// <summary>
    /// The projection of the observed snapshots (the merged ones count; any other is left out). <paramref name="judgedCommitOf"/> gives the commit the judge saw
    /// for the run of a pull request, or null when the log does not say. The mode is deduced everywhere, as nothing says Agent Studio merged it.
    /// </summary>
    public static CommitSuccession From(IEnumerable<RemoteSnapshot> mergedSnapshots, Func<PullRequestRef, string?> judgedCommitOf) =>
        From(mergedSnapshots, judgedCommitOf, _ => null);

    /// <summary>
    /// As <see cref="From(IEnumerable{RemoteSnapshot}, Func{PullRequestRef, string?})"/>, with the method Agent Studio itself merged the pull request with, when the
    /// log has the operation (<paramref name="mergedByAppWith"/>): only then is the mode a fact and not a deduction.
    /// </summary>
    public static CommitSuccession From(IEnumerable<RemoteSnapshot> mergedSnapshots, Func<PullRequestRef, string?> judgedCommitOf, Func<PullRequestRef, MergeMethod?> mergedByAppWith)
    {
        ArgumentNullException.ThrowIfNull(mergedSnapshots);
        ArgumentNullException.ThrowIfNull(judgedCommitOf);
        ArgumentNullException.ThrowIfNull(mergedByAppWith);
        var successors = new Dictionary<string, CommitSuccessor>(StringComparer.Ordinal);
        var originals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var truncatedResults = new HashSet<string>(StringComparer.Ordinal);
        var byPullRequest = new Dictionary<string, CommitSuccessor>(StringComparer.Ordinal);
        var latest = mergedSnapshots
            .GroupBy(s => s.PullRequest.TraceId, StringComparer.Ordinal)
            .Select(g => RemoteSnapshotOrder.Latest(g))
            .Where(s => s.State == PullRequestState.Merged)
            .OrderBy(s => s.UpdatedAt)
            .ThenBy(s => s.PullRequest.TraceId, StringComparer.Ordinal);
        foreach (var snapshot in latest)
        {
            var mode = snapshot.MergeMode ?? RemoteMergeMode.Unknown;
            var results = IntegrationByPullRequest.ResultCommitsOf(snapshot);
            var head = IntegrationByPullRequest.HeadIsJudgedCommit(snapshot, judgedCommitOf(snapshot.PullRequest));
            var deduced = !(mergedByAppWith(snapshot.PullRequest) is { } method && ModeOf(method) == mode) || mode is RemoteMergeMode.SquashOrRebase or RemoteMergeMode.Unknown;
            var truncated = snapshot.CommitsTruncated;
            byPullRequest[snapshot.PullRequest.TraceId] = new CommitSuccessor(snapshot.PullRequest, mode, results, head, snapshot.RevertCommits, deduced, truncated);

            // One result per original, in order, only when the platform listed every original and the counts say it was a rebase: nothing else tells which is which.
            var oneToOne = mode == RemoteMergeMode.Rebase && !truncated && results.Count > 0 && results.Count == snapshot.OriginalCommits.Count;
            for (var i = 0; i < snapshot.OriginalCommits.Count; i++)
            {
                IReadOnlyList<string> mine = oneToOne ? [results[i]] : results;
                successors[snapshot.OriginalCommits[i]] = new CommitSuccessor(snapshot.PullRequest, mode, mine, head, snapshot.RevertCommits, deduced, truncated);
            }

            for (var i = 0; i < results.Count; i++)
            {
                originals[results[i]] = oneToOne ? [snapshot.OriginalCommits[i]] : snapshot.OriginalCommits;
                if (truncated)
                {
                    truncatedResults.Add(results[i]);
                }
                else
                {
                    truncatedResults.Remove(results[i]);
                }
            }
        }

        return new CommitSuccession(successors, originals, truncatedResults, byPullRequest);
    }

    /// <summary>What became of an original commit, or null when no merged pull request names it.</summary>
    public CommitSuccessor? SuccessorOf(string originalCommit) => _successors.GetValueOrDefault(originalCommit);

    /// <summary>What the merge of a pull request left, whatever its originals (the list can be empty, or truncated); null when the pull request is not merged.</summary>
    public CommitSuccessor? OfPullRequest(PullRequestRef pullRequest) => _byPullRequest.GetValueOrDefault(pullRequest.TraceId);

    /// <summary>The original commits a commit of the remote comes from; empty when it comes from none the log knows.</summary>
    public IReadOnlyList<string> OriginalsOf(string resultCommit) => _originals.GetValueOrDefault(resultCommit) ?? [];

    /// <summary>
    /// Whether the originals of a commit of the remote are an incomplete list because the platform listed up to 250 commits of the pull request (D-28): the list
    /// is what is known, not all of it.
    /// </summary>
    public bool OriginalsAreTruncated(string resultCommit) => _truncatedResults.Contains(resultCommit);

    private static RemoteMergeMode ModeOf(MergeMethod method) => method switch
    {
        MergeMethod.Squash => RemoteMergeMode.Squash,
        MergeMethod.Rebase => RemoteMergeMode.Rebase,
        _ => RemoteMergeMode.MergeCommit,
    };
}
