using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Who took part in a task, a run or a milestone, by the six roles that are never confused (docs/16 D-32, F9-5, ADR-0039), from the verified log. A role
/// with no source is <see cref="ParticipationEntry.NotRecorded"/> («no consta») and is never deduced. It reads only.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Producing agent:</b> each agent that launched as an executor, from the attempts of the log (provider, model and attempts in the detail).</item>
/// <item><b>Git author and committer:</b> «Agent Studio», only when the log has a diff the system committed for an attempt. The identity of the person in a delivery is not in the
/// log, so it is not said.</item>
/// <item><b>Publishing account:</b> Phase 10 (docs/21 D-30): the <c>login</c> that stayed in the <c>HostingOperationPerformed</c> that pushed the branch or opened the pull request of
/// the run, if the platform answered; «no consta» otherwise.</item>
/// <item><b>Reviewer:</b> besides the judge, the people who approved the pull request of the run, from its final observation (the snapshot names only the approvers; the
/// others are counted, not named). The judge of each valid verdict (its identity is the one the store holds for the verdict the log announced). A review the log records as skipped is an
/// entry with no one (<see cref="ParticipationSources.SkippedReview"/>) and its reason, so a review that was not made is never attributed.</item>
/// <item><b>Approver:</b> each person whose approval the log records as granted (<c>DecidedBy</c>), with what they approved.</item>
/// <item><b>Integrator:</b> the local delivery that left a commit and, in Phase 10, <c>merged_by</c> of the final observation of the pull request of the run when it was merged.
/// The remote entries are read as data (one that does not validate counts for nothing), and a pull request with no run is attributed to no one.</item>
/// </list>
/// A task is a scope of its run: what has no task (the delivery) counts for every task of it. A verdict counts for a task by the rule the board and the chain share
/// (<see cref="VerdictReach"/>): the verdicts that judged it, among them the one issued on the review task of the run if it assessed a requirement the task covers.
/// </remarks>
public sealed class ParticipationProjector(IDecisionRecorder recorder, IVerdictStore verdicts, IPlanStore plans) : IParticipationProjector
{
    private const string SystemGit = "Agent Studio";

    public async Task<ParticipationView> ProjectAsync(ProjectContext project, TraceRef subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(subject);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var parsed = WorkLog.Parse(entries);
        var payloads = parsed.Where(p => p.Payload is not null).Select(p => p.Payload!).ToList();
        string? onlyTask = null;
        List<string> runs;
        switch (subject.Kind)
        {
            case TraceNodeKind.Run:
                runs = [subject.Id];
                break;
            case TraceNodeKind.Task:
                var slash = subject.Id.IndexOf('/',StringComparison.Ordinal);
                if (slash <= 0)
                {
                    throw new ArgumentException($"The task {subject.Id} is not named as run/task.", nameof(subject));
                }

                runs = [subject.Id[..slash]];
                onlyTask = subject.Id[(slash + 1)..];
                break;
            case TraceNodeKind.Milestone:
                runs = WorkLog.RunsOfMilestone(parsed,subject.Id);
                break;
            default:
                throw new ArgumentException($"Participation is projected for a task, a run or a milestone, not for a {subject.Kind}.", nameof(subject));
        }

        var inScope = runs.ToHashSet(StringComparer.Ordinal);
        bool Mine(string? run, string? task) => run is not null && inScope.Contains(run) && (onlyTask is null || task is null || string.Equals(task, onlyTask, StringComparison.Ordinal));

        var all = new List<ParticipationEntry>();
        var storedVerdicts = (await verdicts.ListAsync(project.ProjectId, null, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Producing agents, by attempt.
        var launches = payloads.OfType<AgentRunStartedPayload>()
            .Where(p => p.Task is not null && (p.Purpose is null or AgentRunPurpose.Executor) && Mine(p.RunId, p.Task?.Value))
            .ToList();
        all.AddRange(launches.GroupBy(l => l.Agent).Select(g =>
            new ParticipationEntry(ParticipationRole.ProducingAgent, g.Key.Value, $"{g.First().Provider} {g.Last().Model ?? "-"}, {g.Count()} intento(s)", ParticipationSources.Attempt)));

        // Git author and committer: the system's own commits.
        if (payloads.OfType<EvidenceRegisteredPayload>().Any(e => e.Kind == EvidenceKind.CodeDiff && e.Origin == EvidenceOrigin.CapturedBySystem && e.Attempt is not null && Mine(e.RunId, e.Task?.Value)))
        {
            all.Add(new ParticipationEntry(ParticipationRole.GitAuthorAndCommitter, SystemGit, "commits internos de los intentos", ParticipationSources.Commit));
        }

        // Reviewer: each valid verdict's judge, and each review skipped.
        var planCache = new Dictionary<string, PlanDefinition?>(StringComparer.Ordinal);
        foreach (var issued in payloads.OfType<VerdictIssuedPayload>().Where(v => v.Valid && v.RunId is not null && inScope.Contains(v.RunId)))
        {
            if (storedVerdicts.TryGetValue(issued.VerdictId, out var stored) && stored.Valid && stored.Verdict.Decision == issued.Decision)
            {
                // The rule of the board and the chain (VerdictReach): for a task, the verdicts that judged it, including the one issued on the review task of the run.
                if (onlyTask is not null && !await JudgedAsync(project.ProjectId, issued, stored.Verdict, onlyTask, payloads, planCache, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // A revision verdict that turned towards an approval nobody accepted yet (VerdictReach) is the approval of nobody.
                if (issued.Decision == VerdictDecision.Approved && VerdictReach.Revises(parsed, stored, storedVerdicts) is { } revised
                    && VerdictReach.Standing(parsed, revised, storedVerdicts).Challenged)
                {
                    continue;
                }

                var judge = stored.Verdict.Judge;
                var line = $"{judge.Model.Vendor.Value} {judge.Model.Model ?? "-"}: {issued.Decision}";
                all.Add(new ParticipationEntry(ParticipationRole.Reviewer, judge.Agent.Value, line, ParticipationSources.Verdict));
            }
        }

        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.ReviewSkipped))
        {
            var (run, task) = FreeForm(entry);
            if (Mine(run, task))
            {
                all.Add(new ParticipationEntry(ParticipationRole.Reviewer, null, entry.Summary, ParticipationSources.SkippedReview));
            }
        }

        // Approver: the people who said yes.
        foreach (var (entry, payload) in parsed.Where(p => p.Entry.Kind == DecisionKind.ApprovalGranted && p.Entry.Actor.Kind == ActorKind.User))
        {
            if (payload is ApprovalAnsweredPayload { Approved: true } answer && Mine(answer.RunId, answer.Task?.Value))
            {
                all.Add(new ParticipationEntry(ParticipationRole.Approver, answer.DecidedBy, answer.Action, ParticipationSources.Approval));
            }
        }

        // Integrator: the local delivery that left a commit.
        foreach (var delivery in payloads.OfType<IntegrationPerformedPayload>().Where(p => p.IsDelivery && p.ResultCommit is not null && p.ConflictPaths.Count == 0 && Mine(p.RunId, p.Task?.Value)))
        {
            all.Add(new ParticipationEntry(ParticipationRole.Integrator, SystemGit, $"entrega a {delivery.TargetRef} en {delivery.ResultCommit}", ParticipationSources.LocalDelivery));
        }

        // The remote roles (docs/21 D-30), from the entries of the remote the log holds (read as data: one that does not validate counts for nothing). What has no task
        // (a pull request is the run's) counts for every task of the run, as the delivery does. A role without a source stays «no consta».
        var remote = RemoteLog.Read(parsed);

        // Publishing account: the login that stayed in the operation that pushed the branch or opened the pull request, if the platform answered.
        foreach (var operation in remote.Operations.Where(o => o.Payload.Operation is HostingOperationKind.PushRunBranch or HostingOperationKind.OpenPullRequest
            && o.Payload.Outcome == HostingOutcome.Ok && o.Payload.Account is not null && Mine(o.Payload.Run, null)))
        {
            var payload = operation.Payload;
            var pullRequest = payload.PullRequest is { } number ? $"#{number}" : null;
            all.Add(new ParticipationEntry(ParticipationRole.PublishingAccount, payload.Account, $"{payload.Host}/{payload.Owner}/{payload.Repository}{pullRequest}", ParticipationSources.RemotePublication));
        }

        foreach (var observed in remote.PullRequests.Where(p => p.Run is not null && Mine(p.Run, null)))
        {
            // Reviewer: the people who approved (the snapshot names no one else: the others are only counted).
            foreach (var approver in observed.Latest.Reviews.Approvers)
            {
                all.Add(new ParticipationEntry(ParticipationRole.Reviewer, approver.Value, $"{observed.PullRequest.TraceId}: {ReviewVerdict.Approved}", ParticipationSources.RemoteReview));
            }

            // Integrator: who merged, from the observation of the merge.
            if (observed.Latest is { State: PullRequestState.Merged, MergedBy: { } mergedBy } merged)
            {
                all.Add(new ParticipationEntry(ParticipationRole.Integrator, mergedBy.Value, $"fusión de {observed.PullRequest.TraceId} en {merged.MergeCommit ?? merged.HeadCommit}", ParticipationSources.RemoteMerge));
            }
        }

        var present = all.Select(e => e.Role).ToHashSet();
        foreach (var role in Enum.GetValues<ParticipationRole>().Where(r => !present.Contains(r)))
        {
            all.Add(ParticipationEntry.NotRecorded(role));
        }

        // The same who for the same role and detail once; the roles in the order of D-32.
        var distinct = all.DistinctBy(e => (e.Role, e.Who, e.Detail, e.Source)).OrderBy(e => e.Role).ToList();
        return new ParticipationView(subject, distinct);
    }

    /// <summary>Whether the verdict judged the task by <see cref="VerdictReach"/>, from the plan of its run; without that plan only its own task counts (none: the run).</summary>
    private async Task<bool> JudgedAsync(string projectId, VerdictIssuedPayload issued, Verdict verdict, string task, IReadOnlyList<DecisionPayload> payloads, Dictionary<string, PlanDefinition?> cache, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(issued.RunId!, out var plan))
        {
            var started = payloads.OfType<RunStartedPayload>().FirstOrDefault(p => string.Equals(p.RunId, issued.RunId, StringComparison.Ordinal));
            cache[issued.RunId!] = plan = started is null ? null : await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false);
        }

        var spec = plan?.Tasks.FirstOrDefault(t => string.Equals(t.Id.Value, task, StringComparison.Ordinal));
        if (spec is null)
        {
            return issued.Task is not { } one || string.Equals(one.Value, task, StringComparison.Ordinal);
        }

        var onOrdinaryTask = issued.Task is { } issuedOn && plan!.Tasks.Any(t => t.Id == issuedOn && t.Kind != WorkTaskKind.Review);
        return VerdictReach.Judges(verdict, issued.Task, onOrdinaryTask, spec.Id, spec.Covers);
    }

    /// <summary>The run and the task of an entry with a free-form payload (<c>ReviewSkipped</c>).</summary>
    private static (string? Run, string? Task) FreeForm(DecisionEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? Text(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Text("runId"), Text("task"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
