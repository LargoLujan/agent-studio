using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

public sealed partial class GitHubHostingProvider
{
    private static readonly IReadOnlyDictionary<ObservedResource, string> NoETags = new Dictionary<ObservedResource, string>();

    private static readonly ChecksSummary NoChecks = new(ChecksRollup.None, 0, 0, 0, false);

    private static readonly ReviewsSummary NoReviews = new(0, 0, 0, []);

    /// <summary>How far back from the last time the pull request was read the commits of the base branch are asked for: the platform gives no date of the merge, only that of the last update.</summary>
    internal static readonly TimeSpan RevertWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// One round of observation (docs/21 D-23 and D-24). What is asked depends on what was last recorded: a pull request that is open or in draft, the pull request, its reviews, and the checks
    /// and the statuses of its head (with the commits and the timeline only when the head changed); one just merged or closed, also its commits, its timeline and, if merged, the commit of the merge;
    /// one merged earlier, only the commits of the base branch since the merge and the deployments of the commit. Each read is conditional with the ETag of that resource and of no other: a 304 of
    /// the pull request says nothing of its checks. Every request that answers 304 is composed from the last snapshot; when all of them do, the answer is <see cref="HostingOutcome.NotModified"/> with no
    /// snapshot. The cursor that comes back holds the ETags of this round only if the whole round went well; a failure returns no cursor, so nothing advances. The texts for the screen are only those
    /// this round read.
    /// </summary>
    public async Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (await RequireAsync<PullRequestObservation>(HostingCapability.ReadRepository, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        var last = cursor?.LastSnapshot is { } known && known.PullRequest.Equals(pullRequest) ? known : null;

        // With a cursor but no snapshot there is nothing to compose a 304 from: everything is asked without condition.
        var round = new Round(this, pullRequest, last, last is null || cursor is null ? NoETags : cursor.ETags, ct);
        return last?.State == PullRequestState.Merged
            ? await round.ObserveMergedAsync(capabilities).ConfigureAwait(false)
            : await round.ObserveAsync(capabilities).ConfigureAwait(false);
    }

    private sealed class Round(GitHubHostingProvider provider, PullRequestRef pullRequest, RemoteSnapshot? last, IReadOnlyDictionary<ObservedResource, string> before, CancellationToken ct)
    {
        private readonly Dictionary<ObservedResource, string> _after = new(before);
        private int _asked;
        private int _notModified;

        private RemoteRepository Repository => pullRequest.Repository;

        private Task<Step> SendAsync(GitHubTarget target) => provider.SendAsync(target, null, null, ct);

        /// <summary>Asks one resource, with its own ETag when it is conditional. Only an answer that held the resource moves its ETag.</summary>
        private async Task<Step> AskAsync(ObservedResource resource, GitHubTarget target, bool conditional)
        {
            var tag = conditional && last is not null && before.TryGetValue(resource, out var known) ? known : null;
            var step = await provider.SendAsync(target, null, tag, ct).ConfigureAwait(false);
            _asked++;
            if (step.Outcome == HostingOutcome.NotModified)
            {
                _notModified++;
            }
            else if (step.IsOk)
            {
                if (step.Http!.ETag is { } fresh)
                {
                    _after[resource] = fresh;
                }
                else
                {
                    _after.Remove(resource);
                }
            }

            return step;
        }

        private static HostingResult<PullRequestObservation> NotModified() => new(HostingOutcome.NotModified);

        private HostingResult<PullRequestObservation> Done(RemoteSnapshot snapshot, RemoteDisplayTexts display)
        {
            try
            {
                snapshot.EnsureValid();
            }
            catch (ArgumentException)
            {
                return Invalid<PullRequestObservation>();
            }

            return new HostingResult<PullRequestObservation>(HostingOutcome.Ok, new PullRequestObservation(snapshot, new ObservationCursor(new Dictionary<ObservedResource, string>(_after), snapshot), display));
        }

        // ---- A pull request that is not merged (or is not known yet) ----

        internal async Task<HostingResult<PullRequestObservation>> ObserveAsync(HostingCapabilities capabilities)
        {
            var read = await AskAsync(ObservedResource.PullRequest, GitHubApiPath.PullRequest(GitHubEndpoint.PullRequest, pullRequest), conditional: true).ConfigureAwait(false);
            PullRequestFacts facts;
            if (read.Outcome == HostingOutcome.NotModified && last is not null)
            {
                facts = new PullRequestFacts(last.State, last.HeadCommit, last.BaseBranch, last.MergeCommit, last.MergedBy, last.UpdatedAt, null, "unknown");
            }
            else if (read.IsOk)
            {
                if (GitHubReaders.PullRequest(read.Http!.Body) is not { } parsed)
                {
                    return Invalid<PullRequestObservation>();
                }

                facts = parsed;
            }
            else
            {
                return read.Fail<PullRequestObservation>();
            }

            var transition = last is null || last.State != facts.State;
            var headChanged = last is null || !string.Equals(last.HeadCommit, facts.Head, StringComparison.Ordinal);
            var open = facts.State is PullRequestState.Open or PullRequestState.Draft;
            var failingNames = new List<string>();

            // Reviews and checks only matter while the pull request is open.
            var reviews = last?.Reviews ?? NoReviews;
            var checks = last?.Checks ?? NoChecks;
            if (open && capabilities.Has(HostingCapability.ReadReviews))
            {
                var step = await AskAsync(ObservedResource.Reviews, GitHubApiPath.Reviews(pullRequest), conditional: true).ConfigureAwait(false);
                if (step.IsOk)
                {
                    if (GitHubReviewReader.Read(step.Http!.Body) is not { } reading)
                    {
                        return Invalid<PullRequestObservation>();
                    }

                    reviews = reading;
                }
                else if (step.Outcome != HostingOutcome.NotModified)
                {
                    return step.Fail<PullRequestObservation>();
                }
            }

            if (open && capabilities.Has(HostingCapability.ReadChecks))
            {
                var outcome = await ReadChecksAsync(facts.Head, conditional: !headChanged, checks, failingNames).ConfigureAwait(false);
                if (outcome.Failure is { } failure)
                {
                    return failure;
                }

                checks = outcome.Checks!;
            }

            // The commits and the timeline: when the head changed, and once when the pull request was merged or closed.
            var originals = last?.OriginalCommits ?? [];
            var commitsTruncated = last?.CommitsTruncated ?? false;
            var forcePushes = last?.ForcePushes ?? 0;
            var timelineTruncated = last?.TimelineTruncated ?? false;
            var headExists = last?.HeadBranchExists ?? true;
            IReadOnlyList<CommitInfo> commitInfos = [];
            if (last is null || headChanged || transition)
            {
                var commits = await ReadCommitsAsync().ConfigureAwait(false);
                if (commits.Failure is { } commitsFailure)
                {
                    return commitsFailure;
                }

                commitInfos = commits.Infos!;
                originals = [.. commitInfos.Select(c => c.Sha)];
                commitsTruncated = commits.Truncated;

                var timeline = await ReadTimelineAsync().ConfigureAwait(false);
                if (timeline.Failure is { } timelineFailure)
                {
                    return timelineFailure;
                }

                forcePushes = timeline.ForcePushes;
                timelineTruncated = timeline.Truncated;
                headExists = timeline.HeadExists;
            }

            string? mergeCommit = null;
            RemoteMergeMode? mergeMode = null;
            IReadOnlyList<string> results = [];
            if (facts.State == PullRequestState.Merged)
            {
                mergeCommit = facts.MergeCommit;
                var merge = await DeduceMergeAsync(mergeCommit!, commitInfos).ConfigureAwait(false);
                if (merge.Failure is { } mergeFailure)
                {
                    return mergeFailure;
                }

                mergeMode = merge.Mode;
                results = merge.Results!;
            }

            // Everything asked answered 304: nothing is new.
            if (_asked > 0 && _notModified == _asked)
            {
                return NotModified();
            }

            var snapshot = new RemoteSnapshot(
                pullRequest,
                facts.State,
                facts.Head,
                facts.Base,
                headExists,
                mergeCommit,
                mergeMode,
                facts.State == PullRequestState.Merged ? facts.MergedBy : null,
                originals,
                results,
                commitsTruncated,
                checks,
                reviews,
                forcePushes,
                timelineTruncated,
                last?.RevertCommits ?? [],
                last?.Deployment,
                facts.UpdatedAt);
            return Done(snapshot, new RemoteDisplayTexts(facts.Title, failingNames, null));
        }

        private async Task<(HostingResult<PullRequestObservation>? Failure, ChecksSummary? Checks)> ReadChecksAsync(string head, bool conditional, ChecksSummary lastChecks, List<string> failingNames)
        {
            var runs = await AskAsync(ObservedResource.CheckRuns, GitHubApiPath.CheckRuns(Repository, head), conditional).ConfigureAwait(false);
            var statuses = await AskAsync(ObservedResource.CombinedStatus, GitHubApiPath.CombinedStatus(Repository, head), conditional).ConfigureAwait(false);
            if (!runs.IsOk && runs.Outcome != HostingOutcome.NotModified)
            {
                return (runs.Fail<PullRequestObservation>(), null);
            }

            if (!statuses.IsOk && statuses.Outcome != HostingOutcome.NotModified)
            {
                return (statuses.Fail<PullRequestObservation>(), null);
            }

            if (runs.Outcome == HostingOutcome.NotModified && statuses.Outcome == HostingOutcome.NotModified)
            {
                return (null, lastChecks);
            }

            // Both lists make one summary: when only one answered 304 the other changed, and the one that did not change is asked again, whole.
            if (runs.Outcome == HostingOutcome.NotModified)
            {
                runs = await AskAsync(ObservedResource.CheckRuns, GitHubApiPath.CheckRuns(Repository, head), conditional: false).ConfigureAwait(false);
                _notModified--;
            }
            else if (statuses.Outcome == HostingOutcome.NotModified)
            {
                statuses = await AskAsync(ObservedResource.CombinedStatus, GitHubApiPath.CombinedStatus(Repository, head), conditional: false).ConfigureAwait(false);
                _notModified--;
            }

            if (!runs.IsOk || !statuses.IsOk)
            {
                return ((runs.IsOk ? statuses : runs).Fail<PullRequestObservation>(), null);
            }

            if (GitHubReaders.CheckRuns(runs.Http!.Body) is not { } runReading || GitHubReaders.CombinedStatus(statuses.Http!.Body) is not { } statusReading)
            {
                return (Invalid<PullRequestObservation>(), null);
            }

            var passing = runReading.Passing + statusReading.Passing;
            var failing = runReading.Failing + statusReading.Failing;
            var pending = runReading.Pending + statusReading.Pending;
            failingNames.AddRange(runReading.FailingNames.Concat(statusReading.FailingNames).Take(20));
            var rollup = failing > 0 ? ChecksRollup.Failing : pending > 0 ? ChecksRollup.Pending : passing > 0 ? ChecksRollup.Passing : ChecksRollup.None;
            return (null, new ChecksSummary(rollup, passing, failing, pending, runReading.Truncated || statusReading.Truncated));
        }

        /// <summary>The commits of the pull request, up to three pages of a hundred; what still comes full after the third is truncated.</summary>
        private async Task<(HostingResult<PullRequestObservation>? Failure, IReadOnlyList<CommitInfo>? Infos, bool Truncated)> ReadCommitsAsync()
        {
            var all = new List<CommitInfo>();
            for (var page = 1; page <= GitHubApiPath.MaxPages; page++)
            {
                var step = await SendAsync(GitHubApiPath.PullRequestCommits(pullRequest, page)).ConfigureAwait(false);
                if (!step.IsOk)
                {
                    return (step.Fail<PullRequestObservation>(), null, false);
                }

                if (GitHubReaders.Commits(step.Http!.Body) is not { } commits)
                {
                    return (Invalid<PullRequestObservation>(), null, false);
                }

                all.AddRange(commits);
                if (commits.Count < GitHubApiPath.PageSize)
                {
                    return (null, all, false);
                }
            }

            return (null, all, true);
        }

        private async Task<(HostingResult<PullRequestObservation>? Failure, int ForcePushes, bool Truncated, bool HeadExists)> ReadTimelineAsync()
        {
            var forcePushes = 0;
            var exists = true;
            for (var page = 1; page <= GitHubApiPath.MaxPages; page++)
            {
                var step = await SendAsync(GitHubApiPath.PullRequestTimeline(pullRequest, page)).ConfigureAwait(false);
                if (!step.IsOk)
                {
                    return (step.Fail<PullRequestObservation>(), 0, false, true);
                }

                if (GitHubReaders.Timeline(step.Http!.Body) is not { } timeline)
                {
                    return (Invalid<PullRequestObservation>(), 0, false, true);
                }

                foreach (var item in timeline.Events)
                {
                    switch (item)
                    {
                        case TimelineEvent.ForcePushed:
                            forcePushes++;
                            break;
                        case TimelineEvent.HeadRefDeleted:
                            exists = false;
                            break;
                        case TimelineEvent.HeadRefRestored:
                            exists = true;
                            break;
                    }
                }

                if (timeline.Items < GitHubApiPath.PageSize)
                {
                    return (null, forcePushes, false, exists);
                }
            }

            return (null, forcePushes, true, exists);
        }

        /// <summary>
        /// How the pull request was merged, deduced from real commits (docs/21 D-27): two parents is a merge commit; one parent whose author, date of authoring and message are those of the
        /// next to last commit of the pull request is a rebase; one parent that differs is a squash; with one commit only, squash or rebase. A rebase leaves as many commits as the pull request had,
        /// which are read from the base branch; any other mode leaves the commit of the merge.
        /// </summary>
        private async Task<(HostingResult<PullRequestObservation>? Failure, RemoteMergeMode Mode, IReadOnlyList<string>? Results)> DeduceMergeAsync(string mergeCommit, IReadOnlyList<CommitInfo> originals)
        {
            var single = await SendAsync(GitHubApiPath.Commit(Repository, mergeCommit)).ConfigureAwait(false);
            if (!single.IsOk)
            {
                return (single.Fail<PullRequestObservation>(), RemoteMergeMode.Unknown, null);
            }

            if (GitHubReaders.SingleCommit(single.Http!.Body) is not { } merged)
            {
                return (Invalid<PullRequestObservation>(), RemoteMergeMode.Unknown, null);
            }

            if (merged.Parents.Count >= 2)
            {
                return (null, RemoteMergeMode.MergeCommit, [mergeCommit]);
            }

            if (merged.Parents.Count == 0 || originals.Count == 0)
            {
                return (null, RemoteMergeMode.Unknown, [mergeCommit]);
            }

            if (originals.Count == 1)
            {
                return (null, RemoteMergeMode.SquashOrRebase, [mergeCommit]);
            }

            var parent = await SendAsync(GitHubApiPath.Commit(Repository, merged.Parents[0])).ConfigureAwait(false);
            if (!parent.IsOk)
            {
                return (parent.Fail<PullRequestObservation>(), RemoteMergeMode.Unknown, null);
            }

            if (GitHubReaders.SingleCommit(parent.Http!.Body) is not { } previous)
            {
                return (Invalid<PullRequestObservation>(), RemoteMergeMode.Unknown, null);
            }

            var penultimate = originals[^2];
            var rebased = previous.Info.AuthorName is not null && previous.Info.AuthorDate is not null && previous.Info.Message is not null
                && string.Equals(previous.Info.AuthorName, penultimate.AuthorName, StringComparison.Ordinal)
                && string.Equals(previous.Info.AuthorDate, penultimate.AuthorDate, StringComparison.Ordinal)
                && string.Equals(previous.Info.Message, penultimate.Message, StringComparison.Ordinal);
            if (!rebased)
            {
                return (null, RemoteMergeMode.Squash, [mergeCommit]);
            }

            var count = Math.Min(originals.Count, GitHubApiPath.PageSize);
            var list = await SendAsync(GitHubApiPath.CommitsUpTo(Repository, mergeCommit, count)).ConfigureAwait(false);
            if (!list.IsOk)
            {
                return (list.Fail<PullRequestObservation>(), RemoteMergeMode.Unknown, null);
            }

            var left = GitHubReaders.Commits(list.Http!.Body);
            if (left is null || left.Count != count || !string.Equals(left[0].Sha, mergeCommit, StringComparison.Ordinal))
            {
                return (null, RemoteMergeMode.Rebase, [mergeCommit]);
            }

            return (null, RemoteMergeMode.Rebase, [.. left.Select(c => c.Sha).Reverse()]);
        }

        // ---- A pull request that was merged earlier: the base branch and the deployments ----

        internal async Task<HostingResult<PullRequestObservation>> ObserveMergedAsync(HostingCapabilities capabilities)
        {
            var merged = last!;
            var mergeCommits = new HashSet<string>(merged.ResultCommits, StringComparer.Ordinal);
            if (merged.MergeCommit is { } mergeCommit)
            {
                mergeCommits.Add(mergeCommit);
            }

            var reverts = merged.RevertCommits;
            var since = merged.UpdatedAt - RevertWindow;
            var baseStep = await AskAsync(ObservedResource.BaseCommits, GitHubApiPath.CommitsSince(Repository, merged.BaseBranch, since), conditional: true).ConfigureAwait(false);
            if (baseStep.IsOk)
            {
                if (GitHubReaders.Reverts(baseStep.Http!.Body, mergeCommits) is not { } found)
                {
                    return Invalid<PullRequestObservation>();
                }

                // Only grows: what was seen stays seen.
                reverts = [.. merged.RevertCommits, .. found.Where(f => !merged.RevertCommits.Contains(f, StringComparer.Ordinal))];
            }
            else if (baseStep.Outcome != HostingOutcome.NotModified)
            {
                return baseStep.Fail<PullRequestObservation>();
            }

            var deployment = merged.Deployment;
            string? environment = null;
            if (capabilities.Has(HostingCapability.ReadDeployments) && merged.MergeCommit is { } deployed)
            {
                var step = await AskAsync(ObservedResource.Deployments, GitHubApiPath.Deployments(Repository, deployed), conditional: true).ConfigureAwait(false);
                if (step.IsOk)
                {
                    if (GitHubReaders.Deployments(step.Http!.Body) is not { } entries)
                    {
                        return Invalid<PullRequestObservation>();
                    }

                    // A read that shows none after one was seen is an old replica: the one seen stays.
                    if (entries.Count > 0)
                    {
                        var newest = entries.MaxBy(e => e.Id)!;
                        var statuses = await SendAsync(GitHubApiPath.DeploymentStatuses(Repository, newest.Id)).ConfigureAwait(false);
                        if (!statuses.IsOk)
                        {
                            return statuses.Fail<PullRequestObservation>();
                        }

                        if (GitHubReaders.DeploymentStateOf(statuses.Http!.Body) is not { } state)
                        {
                            return Invalid<PullRequestObservation>();
                        }

                        deployment = new RemoteDeployment(newest.Id, deployed, state);
                        environment = newest.Environment.Length == 0 ? null : newest.Environment;
                    }
                }
                else if (step.Outcome != HostingOutcome.NotModified)
                {
                    return step.Fail<PullRequestObservation>();
                }
            }

            if (_asked > 0 && _notModified == _asked)
            {
                return NotModified();
            }

            return Done(merged with { RevertCommits = reverts, Deployment = deployment }, new RemoteDisplayTexts(null, [], environment));
        }
    }
}
