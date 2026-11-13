using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HostingStanding = AgentStudio.Application.Hosting.VerdictStanding;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The pull requests of a project as the screen «Repositorio y cambios › PR» reads them (docs/21 D-28 and D-29): from the verified log, one view for each pull request
/// the log observed or that Agent Studio opened and has not observed yet, with the last recorded snapshot, the commit the judge saw, whether the head is that commit
/// and the standing of the verdict of its run. It reads only: no network, no git, no write. A log that does not verify throws <see cref="DecisionLogIntegrityException"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What is read as data.</b> Every remote entry is validated when it is read; one that does not validate is left out (<see cref="RemoteLogReading"/>), and the
/// projection goes on. Of the observations of a pull request only the final one counts, whatever the order or the repetitions.</item>
/// <item><b>The standing of the verdict</b> and the commit the judge saw come from <see cref="RemoteJudgement.VerdictInForce"/> (the rule of <see cref="VerdictReach"/>), once for
/// each run: approved is <see cref="HostingStanding.Approved"/>; rejected or with changes requested, <see cref="HostingStanding.Rejected"/>; challenged, escalated or unresolved,
/// <see cref="HostingStanding.InReview"/>. Null when the run has no valid verdict, or the pull request has no run.</item>
/// <item><b>The texts of the remote</b> (<see cref="PullRequestView.Display"/>) are the cache's, shown as data; without the cache (it may be deleted) there are none.
/// What is waiting to synchronise comes from the mailbox (<see cref="IRemoteSync.Pending"/>); without one, nothing.</item>
/// <item><b>The cost:</b> it is a read of the log of its own, because this screen has no other that reads it (the board, the chain and the participation share theirs).</item>
/// </list>
/// </remarks>
public sealed class PullRequestProjector(
    IDecisionRecorder recorder,
    IVerdictStore verdicts,
    IRemoteSyncStore? cache = null,
    IRemoteSync? sync = null) : IPullRequestProjector
{
    public async Task<IReadOnlyList<PullRequestView>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, ct).ConfigureAwait(false);
        var parsed = WorkLog.Parse(entries);
        var remote = RemoteLogReading.Read(parsed);

        // The pull requests Agent Studio opened and has not observed yet: they are in the list too, without a snapshot. An attempt to open one that did not work (the platform
        // refused, there was no session…) is not a pull request to show.
        var views = remote.PullRequests.Select(p => (p.PullRequest, Run: p.Run, Observed: (ObservedPullRequest?)p)).ToList();
        var known = views.Select(v => v.PullRequest.TraceId).ToHashSet(StringComparer.Ordinal);
        foreach (var opened in remote.Operations.Where(o => o.Payload is { Operation: HostingOperationKind.OpenPullRequest, Outcome: HostingOutcome.Ok, PullRequest: not null }))
        {
            var payload = opened.Payload;
            var pullRequest = new PullRequestRef(RemoteRepository.TryCreateGitHub(payload.Owner, payload.Repository)!, payload.PullRequest!.Value);
            if (known.Add(pullRequest.TraceId))
            {
                views.Add((pullRequest, payload.Run, null));
            }
        }

        if (views.Count == 0)
        {
            return [];
        }

        var storedVerdicts = (await verdicts.ListAsync(project.ProjectId, null, ct).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var followed = cache is null
            ? []
            : (await cache.LoadAsync(project.ProjectId, ct).ConfigureAwait(false)).Followed.GroupBy(f => f.PullRequest.TraceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var pending = sync?.Pending(project.ProjectId) ?? [];

        // The verdict in force is a fact of the run, not of the pull request: it is worked out once for each run, however many pull requests it has.
        var byRun = new Dictionary<string, (string? JudgedCommit, HostingStanding? Standing)>(StringComparer.Ordinal);
        var result = new List<PullRequestView>();
        foreach (var (pullRequest, run, observed) in views.OrderBy(v => v.PullRequest.Repository.Id, StringComparer.Ordinal).ThenBy(v => v.PullRequest.Number))
        {
            (string? JudgedCommit, HostingStanding? Standing) verdict = default;
            if (run is not null && !byRun.TryGetValue(run, out verdict))
            {
                byRun[run] = verdict = RemoteJudgement.VerdictInForce(parsed, entries, run, storedVerdicts);
            }

            var (judgedCommit, standing) = verdict;
            var snapshot = observed?.Latest;
            result.Add(new PullRequestView(
                pullRequest,
                run is null ? null : SafeRun(run),
                snapshot,
                judgedCommit,
                snapshot is not null && IntegrationByPullRequest.HeadIsJudgedCommit(snapshot, judgedCommit),
                standing,
                followed.TryGetValue(pullRequest.TraceId, out var cached) ? cached.Display : RemoteDisplayTexts.None,
                observed?.ObservedAt,
                [.. pending.Where(p => p.Ticket.PullRequest is { } ticket && string.Equals(ticket.TraceId, pullRequest.TraceId, StringComparison.Ordinal))]));
        }

        return result;
    }

    private static RunId? SafeRun(string run)
    {
        try
        {
            return new RunId(run);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The registration of the projections of the remote (docs/21 §10.6): the view of the pull requests. The lead calls it from the composition.</summary>
public static class HostingProjectionsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPullRequestProjector"/>. The cache (<see cref="IRemoteSyncStore"/>) and the mailbox (<see cref="IRemoteSync"/>) are optional for it: without
    /// them the view has no texts of the remote and nothing pending. The board, the chain, the participation, the annotator and the timeline need nothing new:
    /// they are registered already and read the remote entries of the log by themselves.
    /// </summary>
    public static IServiceCollection AddAgentStudioHostingProjections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPullRequestProjector, PullRequestProjector>();
        return services;
    }
}
