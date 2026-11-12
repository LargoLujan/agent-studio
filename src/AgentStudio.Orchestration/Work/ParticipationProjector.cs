using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Tracing;
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
/// <item><b>Publishing account:</b> Phase 10; always «no consta» until a remote operation is recorded.</item>
/// <item><b>Reviewer:</b> the judge of each valid verdict (its identity is the one the store holds for the verdict the log announced). A review the log records as skipped is an
/// entry with no one (<see cref="ParticipationSources.SkippedReview"/>) and its reason, so a review that was not made is never attributed.</item>
/// <item><b>Approver:</b> each person whose approval the log records as granted (<c>DecidedBy</c>), with what they approved.</item>
/// <item><b>Integrator:</b> the local delivery that left a commit. The remote's <c>merged_by</c> is Phase 10.</item>
/// </list>
/// A task is a scope of its run: what has no task (a verdict of the run, the delivery) counts for every task of it.
/// </remarks>
public sealed class ParticipationProjector(IDecisionRecorder recorder, IVerdictStore verdicts) : IParticipationProjector
{
    private const string SystemGit = "Agent Studio";

    public async Task<ParticipationView> ProjectAsync(ProjectContext project, TraceRef subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(subject);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        string? onlyTask = null;
        List<string> runs;
        switch (subject.Kind)
        {
            case TraceNodeKind.Run:
                runs = [subject.Id];
                break;
            case TraceNodeKind.Task:
                var slash = subject.Id.IndexOf('/', StringComparison.Ordinal);
                runs = [subject.Id[..slash]];
                onlyTask = subject.Id[(slash + 1)..];
                break;
            case TraceNodeKind.Milestone:
                runs = WorkLog.RunsOfMilestone(entries, subject.Id);
                break;
            default:
                throw new ArgumentException($"Participation is projected for a task, a run or a milestone, not for a {subject.Kind}.", nameof(subject));
        }

        var inScope = runs.ToHashSet(StringComparer.Ordinal);
        bool Mine(string? run, string? task) => run is not null && inScope.Contains(run) && (onlyTask is null || task is null || string.Equals(task, onlyTask, StringComparison.Ordinal));

        var all = new List<ParticipationEntry>();
        var storedVerdicts = (await verdicts.ListAsync(project.ProjectId, null, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Producing agents, by attempt.
        var launches = entries.Select(RunLog.Payload).OfType<AgentRunStartedPayload>()
            .Where(p => p.Task is not null && (p.Purpose is null or AgentRunPurpose.Executor) && Mine(p.RunId, p.Task?.Value))
            .ToList();
        all.AddRange(launches.GroupBy(l => l.Agent).Select(g =>
            new ParticipationEntry(ParticipationRole.ProducingAgent, g.Key.Value, $"{g.First().Provider} {g.Last().Model ?? "-"}, {g.Count()} intento(s)", ParticipationSources.Attempt)));

        // Git author and committer: the system's own commits.
        if (entries.Select(RunLog.Payload).OfType<EvidenceRegisteredPayload>().Any(e => e.Kind == EvidenceKind.CodeDiff && e.Origin == EvidenceOrigin.CapturedBySystem && e.Attempt is not null && Mine(e.RunId, e.Task?.Value)))
        {
            all.Add(new ParticipationEntry(ParticipationRole.GitAuthorAndCommitter, SystemGit, "commits internos de los intentos", ParticipationSources.Commit));
        }

        // Reviewer: each valid verdict's judge, and each review skipped.
        foreach (var issued in entries.Select(RunLog.Payload).OfType<VerdictIssuedPayload>().Where(v => v.Valid && Mine(v.RunId, v.Task?.Value)))
        {
            if (storedVerdicts.TryGetValue(issued.VerdictId, out var stored) && stored.Valid && stored.Verdict.Decision == issued.Decision)
            {
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
        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.ApprovalGranted && e.Actor.Kind == ActorKind.User))
        {
            if (RunLog.Payload(entry) is ApprovalAnsweredPayload { Approved: true } answer && Mine(answer.RunId, answer.Task?.Value))
            {
                all.Add(new ParticipationEntry(ParticipationRole.Approver, answer.DecidedBy, answer.Action, ParticipationSources.Approval));
            }
        }

        // Integrator: the local delivery that left a commit.
        foreach (var delivery in entries.Select(RunLog.Payload).OfType<IntegrationPerformedPayload>().Where(p => p.IsDelivery && p.ResultCommit is not null && p.ConflictPaths.Count == 0 && Mine(p.RunId, p.Task?.Value)))
        {
            all.Add(new ParticipationEntry(ParticipationRole.Integrator, SystemGit, $"entrega a {delivery.TargetRef} en {delivery.ResultCommit}", ParticipationSources.LocalDelivery));
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
