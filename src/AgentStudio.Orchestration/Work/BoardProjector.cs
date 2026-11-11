using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Projects the board of a milestone from the verified log (docs/16 D-19, F9-2, ADR-0038). It reads only, and every column comes from the
/// pure rules of <see cref="BoardRules"/>; nothing here decides where a card goes.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What is projected.</b> The requirements of the frozen set the milestone works against now, and the tasks of the plans of the runs
/// linked to it with their follow-ups (<c>RunStarted.Follows</c>, transitively). Each run is replayed through the domain
/// (<see cref="RunStateReplayer"/>), as the inspection of a run does.</item>
/// <item><b>A verdict counts</b> only when the log announced it valid (<c>VerdictIssued.Valid</c>) and the store holds it with the same decision
/// and as valid: the one that judged the requirement (an assessment of it), the last. A verdict a person challenged is set aside (in review)
/// until its revision debate closes resolved: if it confirms the decision, the verdict stands; if it withdraws an approval, the decision
/// of the revision stands; if it turns an unapproved verdict towards an approval, the card stays in review (a person accepts that, D-40).
/// A revision that is not closed, aborted, decided by a person or without valid verdicts leaves it in review.</item>
/// <item><b>When several runs cover a requirement</b> the last one started decides: a follow-up replaces what an earlier run left.</item>
/// <item>A log that does not verify throws <see cref="DecisionLogIntegrityException"/>. A milestone with problems has its
/// <see cref="MilestoneView.Problems"/> and no cards: nothing is projected from a state the domain does not allow.</item>
/// </list>
/// </remarks>
public sealed class BoardProjector(
    IDecisionRecorder recorder,
    IMilestoneService milestones,
    IBacklogService backlog,
    IRequirementStore requirementStore,
    IRunRegistry runs,
    IPlanStore plans,
    IVerdictStore verdicts) : IBoardProjector
{
    public async Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var head = entries.Count == 0 ? DecisionLog.GenesisHash : entries[^1].Hash;
        var view = await milestones.GetAsync(project, milestone, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Milestone '{milestone.Value}' was never opened in this project.", nameof(milestone));
        if (view.Problems.Count > 0)
        {
            return new MilestoneBoard(milestone, entries.Count, head, [], []);
        }

        var set = await requirementStore.FindAsync(project.ProjectId, view.CurrentRequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The requirement set {view.CurrentRequirementSetHash} of milestone '{milestone.Value}' is not stored.");
        var storedVerdicts = (await verdicts.ListAsync(project.ProjectId, null, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var facts = new List<RunFacts>();
        foreach (var run in LinkedRuns(view, entries))
        {
            if (await FactsOfAsync(project.ProjectId, run, entries, storedVerdicts, cancellationToken).ConfigureAwait(false) is { } found)
            {
                facts.Add(found);
            }
        }

        var requirements = new List<BoardCard>();
        foreach (var requirement in set.Requirements)
        {
            var deciding = facts.LastOrDefault(f => f.Covering(requirement.Id).Count > 0);
            if (deciding is null)
            {
                requirements.Add(new BoardCard(BoardCardKind.Requirement, requirement.Id.Value, requirement.Statement, BoardRules.ColumnOf(new RequirementBoardFacts([], false, false, null, false, false)), null, null));
                continue;
            }

            var covering = deciding.Covering(requirement.Id);
            var verdict = deciding.VerdictFor(requirement.Id);
            var column = BoardRules.ColumnOf(new RequirementBoardFacts(
                [.. covering.Select(t => t.Status)],
                deciding.Ended,
                deciding.Failed,
                verdict?.Facts,
                verdict?.Challenged ?? false,
                deciding.Delivered));
            requirements.Add(new BoardCard(BoardCardKind.Requirement, requirement.Id.Value, requirement.Statement, column, deciding.Run, ReasonOfRequirement(column, covering, deciding, verdict)));
        }

        var tasks = new List<BoardCard>();
        foreach (var run in facts)
        {
            foreach (var task in run.Tasks)
            {
                if (BoardRules.ColumnOf(new TaskBoardFacts(task.Status, run.StandingDecision)) is not { } column)
                {
                    continue;
                }

                tasks.Add(new BoardCard(BoardCardKind.Task, task.Id.Value, task.Title, column, run.Run, ReasonOfTask(column, task, run)));
            }
        }

        return new MilestoneBoard(milestone, entries.Count, head, requirements, tasks);
    }

    public async Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var items = await backlog.ListAsync(project, cancellationToken).ConfigureAwait(false);
        return [.. items.Select(e => new BoardCard(BoardCardKind.Requirement, e.Item.Id.Value, e.Item.Statement, BoardColumn.Backlog, null, null))];
    }

    /// <summary>The runs linked to the milestone and those that follow them, in the order they started.</summary>
    private static List<string> LinkedRuns(MilestoneView view, IReadOnlyList<DecisionEntry> entries)
    {
        var linked = view.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var starts = entries
            .Select(e => RunLog.Payload(e) as RunStartedPayload)
            .Where(p => p is not null && !string.IsNullOrEmpty(p.RunId))
            .Select(p => p!)
            .DistinctBy(p => p.RunId)
            .ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var start in starts.Where(s => s.Follows is { } follows && linked.Contains(follows) && !linked.Contains(s.RunId)))
            {
                linked.Add(start.RunId);
                changed = true;
            }
        }

        return [.. starts.Where(s => linked.Contains(s.RunId)).Select(s => s.RunId), .. linked.Except(starts.Select(s => s.RunId))];
    }

    private async Task<RunFacts?> FactsOfAsync(string projectId, string run, IReadOnlyList<DecisionEntry> log, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts, CancellationToken cancellationToken)
    {
        var entries = RunLog.Of(log, run);
        var started = entries.Select(e => e.Payload).OfType<RunStartedPayload>().FirstOrDefault();
        var record = await runs.FindAsync(projectId, new RunId(run), cancellationToken).ConfigureAwait(false);
        if (started is null || record is null)
        {
            // Linked, but never started: nothing of it is on the board yet.
            return null;
        }

        var plan = await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The plan {started.PlanHash} that run {run} started with is not stored.");
        var replay = RunStateReplayer.Replay(plan, record.CreatedAt, RunResumer.Events(entries), started.PlanHash);
        if (!replay.IsResumable)
        {
            throw new InvalidOperationException($"The log of run {run} is not replayed by the domain: {string.Join(" ", replay.Problems)}");
        }

        var specs = plan.Tasks.ToDictionary(t => t.Id);
        var tasks = replay.Graph.Tasks
            .Where(t => specs.ContainsKey(t.Id))
            .Select(t => new TaskFacts(t.Id, specs[t.Id].Title, t.Status, specs[t.Id].Covers, BlockedKind(entries, t.Id)))
            .ToList();
        var finished = entries.Select(e => e.Payload).OfType<RunFinishedPayload>().LastOrDefault();
        var delivered = entries.Select(e => e.Payload).OfType<IntegrationPerformedPayload>().Any(p => p.IsDelivery && p.ResultCommit is not null && p.ConflictPaths.Count == 0);

        // The valid verdicts of the run, oldest first: valid in the log and in the store, with the same decision.
        var judged = new List<(VerdictIssuedPayload Issued, Verdict Verdict)>();
        foreach (var issued in entries.Select(e => e.Payload).OfType<VerdictIssuedPayload>())
        {
            if (issued.Valid && storedVerdicts.TryGetValue(issued.VerdictId, out var stored) && stored.Valid && stored.Verdict.Decision == issued.Decision)
            {
                judged.Add((issued, stored.Verdict));
            }
        }

        var standing = judged.Select(j => (j.Issued, j.Verdict, Standing: Standing(log, j.Issued.VerdictId, j.Verdict.Decision))).ToList();
        var lastStanding = standing.LastOrDefault(s => !s.Standing.Challenged);
        return new RunFacts(run, tasks, finished is not null, finished?.Phase is RunPhase.Failed or RunPhase.Cancelled, delivered, standing, lastStanding.Issued is null ? null : lastStanding.Standing.Decision);
    }

    /// <summary>What a person's challenge of a verdict leaves of it (docs/15 D-38 to D-40), from the verified log: see the class.</summary>
    private static StandingOf Standing(IReadOnlyList<DecisionEntry> log, string verdict, VerdictDecision decision)
    {
        string? revision = null;
        foreach (var entry in log)
        {
            switch (RunLog.Payload(entry))
            {
                case VerdictChallengedPayload challenged when string.Equals(challenged.Verdict, verdict, StringComparison.Ordinal):
                    revision = challenged.RevisionDebate;
                    break;
                case DebateOpenedPayload opened when string.Equals(opened.Challenges, verdict, StringComparison.Ordinal):
                    revision = opened.RunId;
                    break;
            }
        }

        if (revision is null)
        {
            return new StandingOf(decision, false);
        }

        var payloads = log.Select(RunLog.Payload).ToList();
        if (payloads.OfType<DebateClosedPayload>().LastOrDefault(c => string.Equals(c.RunId, revision, StringComparison.Ordinal)) is not { Outcome: DebateOutcome.Resolved, Verdicts.Count: > 0 } closed)
        {
            return new StandingOf(decision, true);
        }

        var decisions = new HashSet<VerdictDecision>();
        foreach (var id in closed.Verdicts)
        {
            if (payloads.OfType<VerdictIssuedPayload>().LastOrDefault(v => string.Equals(v.VerdictId, id, StringComparison.Ordinal)) is not { Valid: true } issued)
            {
                return new StandingOf(decision, true);
            }

            decisions.Add(issued.Decision);
        }

        if (decisions.Count != 1)
        {
            return new StandingOf(decision, true);
        }

        var revised = decisions.Single();
        var approved = decision == VerdictDecision.Approved;
        return (approved, revised == VerdictDecision.Approved) switch
        {
            (true, true) => new StandingOf(decision, false),
            (true, false) => new StandingOf(revised, false),
            (false, true) => new StandingOf(decision, true),
            _ => new StandingOf(revised, false),
        };
    }

    private static BlockKind? BlockedKind(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, TaskId task) =>
        entries.Select(e => e.Payload).OfType<TaskStatusChangedPayload>().LastOrDefault(p => p.Task == task && p.To == WorkTaskStatus.Blocked) is { } blocked
            ? BlockReason.Parse(blocked.BlockReason)?.Kind
            : null;

    private static string? ReasonOfRequirement(BoardColumn column, IReadOnlyList<TaskFacts> covering, RunFacts run, RequirementVerdict? verdict) => column switch
    {
        BoardColumn.InProgress => covering.Select(ReasonOfActive).FirstOrDefault(r => r is not null),
        BoardColumn.InReview when verdict is { Challenged: true } => BoardCardReasons.Challenged,
        BoardColumn.InReview when verdict?.Facts.Decision == VerdictDecision.EscalateToHuman => BoardCardReasons.EscalatedToPerson,
        BoardColumn.InReview when verdict?.Facts.Decision == VerdictDecision.Unresolved => BoardCardReasons.Unresolved,
        BoardColumn.Returned when verdict is null && run.Failed => BoardCardReasons.RunFailed,
        _ => null,
    };

    private static string? ReasonOfTask(BoardColumn column, TaskFacts task, RunFacts run) => column switch
    {
        BoardColumn.InProgress => ReasonOfActive(task),
        BoardColumn.Returned when task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled && run.Failed => BoardCardReasons.RunFailed,
        _ => null,
    };

    private static string? ReasonOfActive(TaskFacts task) => task.Status switch
    {
        WorkTaskStatus.AwaitingApproval => BoardCardReasons.AwaitingApproval,
        WorkTaskStatus.Blocked => task.Block switch
        {
            BlockKind.AwaitingAnswer => BoardCardReasons.AwaitingAnswer,
            BlockKind.RateLimitedUntil => BoardCardReasons.WaitingForQuota,
            BlockKind.AwaitingHuman => BoardCardReasons.AwaitingApproval,
            _ => BoardCardReasons.Blocked,
        },
        _ => null,
    };

    private sealed record StandingOf(VerdictDecision Decision, bool Challenged);

    private sealed record TaskFacts(TaskId Id, string Title, WorkTaskStatus Status, IReadOnlyList<RequirementId> Covers, BlockKind? Block);

    private sealed record RequirementVerdict(BoardVerdictFacts Facts, bool Challenged);

    private sealed record RunFacts(
        string Run,
        IReadOnlyList<TaskFacts> Tasks,
        bool Ended,
        bool Failed,
        bool Delivered,
        IReadOnlyList<(VerdictIssuedPayload Issued, Verdict Verdict, StandingOf Standing)> Verdicts,
        VerdictDecision? StandingDecision)
    {
        public IReadOnlyList<TaskFacts> Covering(RequirementId requirement) => [.. Tasks.Where(t => t.Covers.Contains(requirement))];

        /// <summary>The last valid verdict that assessed the requirement, else the last valid one, with what it says of it.</summary>
        public RequirementVerdict? VerdictFor(RequirementId requirement)
        {
            foreach (var (_, verdict, standing) in Verdicts.Reverse())
            {
                if (verdict.Assessments.FirstOrDefault(a => a.Requirement == requirement) is { } assessment)
                {
                    return new RequirementVerdict(new BoardVerdictFacts(standing.Decision, assessment.Status), standing.Challenged);
                }
            }

            return Verdicts.Count == 0 ? null : new RequirementVerdict(new BoardVerdictFacts(Verdicts[^1].Standing.Decision, null), Verdicts[^1].Standing.Challenged);
        }
    }
}
