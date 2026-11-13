using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Application.Judging;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
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
/// until its revision debate closes resolved (the rules of ChallengeLog, read by ChallengeReader): if it confirms the decision, the verdict stands; if it withdraws an approval, the decision
/// of the revision stands; if it turns an unapproved verdict towards an approval, the card stays in review (a person accepts that, D-40). The card of a task follows the last verdict
/// that judged it, the same way (<see cref="VerdictReach"/>).
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
        var parsed = WorkLog.Parse(entries);
        var view = await milestones.GetAsync(project, milestone, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Milestone '{milestone.Value}' was never opened in this project.", nameof(milestone));
        if (view.Problems.Count > 0)
        {
            return new MilestoneBoard(milestone, entries.Count, head, [], []);
        }

        var set = await requirementStore.FindAsync(project.ProjectId, view.CurrentRequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The requirement set {view.CurrentRequirementSetHash} of milestone '{milestone.Value}' is not stored.");
        var storedVerdicts = (await verdicts.ListAsync(project.ProjectId, null, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // The pull requests the log observed (docs/21 D-29), read once from the log already parsed: a project without a remote has none, and nothing below changes for it.
        var remote = RemoteLogReading.Read(parsed);
        var judgedCommits = new Dictionary<string, string?>(StringComparer.Ordinal);
        var facts = new List<RunFacts>();
        foreach (var run in LinkedRuns(view, parsed))
        {
            if (await FactsOfAsync(project.ProjectId, run, parsed, entries, remote, judgedCommits, storedVerdicts, cancellationToken).ConfigureAwait(false) is { } found)
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

            // «Integrado» by pull request is asked of the verdict that decides THIS requirement: what was approved for it is what the merge must have carried (D-29).
            var (delivered, mergedUnjudged, byPullRequest) = deciding.DeliveryFor(verdict);
            var column = BoardRules.ColumnOf(new RequirementBoardFacts(
                [.. covering.Select(t => t.Status)],
                deciding.Ended,
                deciding.Failed,
                verdict?.Facts,
                verdict?.Challenged ?? false,
                delivered));
            requirements.Add(new BoardCard(BoardCardKind.Requirement, requirement.Id.Value, requirement.Statement, column, deciding.Run, ReasonOfRequirement(column, covering, deciding, verdict, mergedUnjudged), column == BoardColumn.Integrated ? byPullRequest : null));
        }

        var tasks = new List<BoardCard>();
        foreach (var run in facts)
        {
            foreach (var task in run.Tasks)
            {
                if (BoardRules.ColumnOf(run.FactsOf(task)) is not { } column)
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
    private static List<string> LinkedRuns(MilestoneView view, IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> parsed) =>
        WorkLog.Followed(parsed, view.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id).ToHashSet(StringComparer.Ordinal));

    private async Task<RunFacts?> FactsOfAsync(string projectId, string run, IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, IReadOnlyList<DecisionEntry> entriesOfLog, RemoteLogReading remote, Dictionary<string, string?> judgedCommits, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts, CancellationToken cancellationToken)
    {
        var entries = log
            .Where(item => item.Payload is not null && string.Equals(item.Payload.RunId, run, StringComparison.Ordinal))
            .Select(item => (item.Entry, Payload: item.Payload!))
            .ToList();
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
            .Select(t => new TaskFacts(t.Id, specs[t.Id].Title, t.Status, specs[t.Id].Covers, BlockedKind(entries, t.Id), specs[t.Id].Kind == WorkTaskKind.Review))
            .ToList();
        var finished = entries.Select(e => e.Payload).OfType<RunFinishedPayload>().LastOrDefault();
        var delivered = entries.Select(e => e.Payload).OfType<IntegrationPerformedPayload>().Any(p => p.IsDelivery && p.ResultCommit is not null && p.ConflictPaths.Count == 0);

        // The valid verdicts of the run, oldest first: valid in the log and in the store, with the same decision.
        var judged = new List<(VerdictIssuedPayload Issued, StoredVerdict Stored)>();
        foreach (var issued in entries.Select(e => e.Payload).OfType<VerdictIssuedPayload>())
        {
            // A verdict of a revision never competes (VerdictReach.Revises): its decision reaches the card through the standing of the verdict it revises.
            if (issued.Valid && storedVerdicts.TryGetValue(issued.VerdictId, out var stored) && stored.Valid && stored.Verdict.Decision == issued.Decision
                && VerdictReach.Revises(log, stored, storedVerdicts) is null)
            {
                judged.Add((issued, stored));
            }
        }

        var standing = judged.Select(j => (j.Issued, j.Stored.Verdict, Standing: VerdictReach.Standing(log, j.Stored, storedVerdicts))).ToList();

        // The merged pull requests of the run (D-29): whether each integrates a requirement is decided per requirement, by the verdict that decides it (RunFacts.DeliveryFor).
        var pullRequests = remote.OfRun(run).Where(p => p.Latest.State == PullRequestState.Merged).ToList();
        return new RunFacts(run, tasks, finished is not null, finished?.Phase is RunPhase.Failed or RunPhase.Cancelled, delivered, standing, pullRequests, entriesOfLog, judgedCommits);
    }

    private static BlockKind? BlockedKind(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, TaskId task) =>
        entries.Select(e => e.Payload).OfType<TaskStatusChangedPayload>().LastOrDefault(p => p.Task == task && p.To == WorkTaskStatus.Blocked) is { } blocked
            ? BlockReason.Parse(blocked.BlockReason)?.Kind
            : null;

    private static string? ReasonOfRequirement(BoardColumn column, IReadOnlyList<TaskFacts> covering, RunFacts run, RequirementVerdict? verdict, bool mergedUnjudged) => column switch
    {
        BoardColumn.InProgress => covering.Select(ReasonOfActive).FirstOrDefault(r => r is not null),
        BoardColumn.InReview when verdict is { Challenged: true } => BoardCardReasons.Challenged,
        BoardColumn.InReview when verdict?.Facts.Decision == VerdictDecision.EscalateToHuman => BoardCardReasons.EscalatedToPerson,
        BoardColumn.InReview when verdict?.Facts.Decision == VerdictDecision.Unresolved => BoardCardReasons.Unresolved,
        BoardColumn.Returned when verdict is null && run.Failed => BoardCardReasons.RunFailed,

        // Done, and its pull request was merged with something other than what the judge saw (D-29): it does not count as integrated, and says why.
        BoardColumn.Done when mergedUnjudged => BoardCardReasons.MergedWithUnjudgedChanges,
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

    private sealed record TaskFacts(TaskId Id, string Title, WorkTaskStatus Status, IReadOnlyList<RequirementId> Covers, BlockKind? Block, bool IsReview);

    private sealed record RequirementVerdict(BoardVerdictFacts Facts, bool Challenged, VerdictIssuedPayload Issued);

    private sealed record RunFacts(
        string Run,
        IReadOnlyList<TaskFacts> Tasks,
        bool Ended,
        bool Failed,
        bool Delivered,
        IReadOnlyList<(VerdictIssuedPayload Issued, Verdict Verdict, VerdictStanding Standing)> Verdicts,
        IReadOnlyList<ObservedPullRequest> MergedPullRequests,
        IReadOnlyList<DecisionEntry> Log,
        Dictionary<string, string?> JudgedCommits)
    {
        /// <summary>
        /// Whether the requirement is delivered, and whether a pull request of the run was merged without counting as its integration. The local delivery counts as it always did; a
        /// merged pull request counts when <see cref="IntegrationByPullRequest"/> says so for the commit the judge saw when it issued the verdict that decides the requirement (none: null).
        /// <c>ByPullRequest</c> is the <c>TraceId</c> of the (first, by identifier) pull request that integrated it, or null when the local delivery did or nothing did.
        /// </summary>
        public (bool Delivered, bool MergedUnjudged, string? ByPullRequest) DeliveryFor(RequirementVerdict? verdict)
        {
            if (MergedPullRequests.Count == 0)
            {
                return (Delivered, false, null);
            }

            // The commit the judge saw is a fact of the verdict: worked out once for each verdict in the whole projection (it reads every integration of the log).
            string? judgedCommit = null;
            if (verdict is not null && !JudgedCommits.TryGetValue(verdict.Issued.VerdictId, out judgedCommit))
            {
                JudgedCommits[verdict.Issued.VerdictId] = judgedCommit = RemoteJudgement.JudgedCommit(Log, Run, verdict.Issued);
            }

            var counts = MergedPullRequests.Select(p => (p, Integration: IntegrationByPullRequest.Of(p.Latest, judgedCommit))).ToList();
            var carrier = counts.FirstOrDefault(c => c.Integration == PullRequestIntegration.Integrated).p;
            var integrated = Delivered || carrier is not null;
            return (integrated, !integrated && counts.Any(c => c.Integration == PullRequestIntegration.MergedWithUnjudgedChanges), Delivered ? null : carrier?.PullRequest.TraceId);
        }

        public IReadOnlyList<TaskFacts> Covering(RequirementId requirement) => [.. Tasks.Where(t => t.Covers.Contains(requirement))];

        /// <summary>
        /// What the last valid verdict that judged the task says of it, by the same rule as for a requirement (<see cref="VerdictReach"/>), or null when none did or the one that did is
        /// set aside by a challenge: the card of a completed task then waits in review, as the card of its requirement does.
        /// </summary>
        public TaskBoardFacts FactsOf(TaskFacts task)
        {
            foreach (var (issued, verdict, standing) in Verdicts.Reverse())
            {
                var onOrdinaryTask = issued.Task is { } one && Tasks.Any(t => t.Id == one && !t.IsReview);
                if (task.Covers.Count == 0 ? VerdictReach.JudgesUncovered(issued.Task, onOrdinaryTask, task.Id) : VerdictReach.Judges(verdict, issued.Task, onOrdinaryTask, task.Id, task.Covers))
                {
                    if (standing.Challenged)
                    {
                        return new TaskBoardFacts(task.Status, null);
                    }

                    // What the verdict says of each requirement the task covers, as the card of that requirement reads it (VerdictFor): the task follows its requirements.
                    IReadOnlyList<AssessmentStatus?> assessed = task.Covers.Count == 0
                        ? []
                        : verdict.Assessments.Count == 0
                            ? [null]
                            : [.. verdict.Assessments.Where(a => task.Covers.Contains(a.Requirement)).Select(a => (AssessmentStatus?)a.Status)];
                    return new TaskBoardFacts(task.Status, standing.Decision, assessed);
                }
            }

            return new TaskBoardFacts(task.Status, null);
        }

        /// <summary>The last valid verdict that judged the requirement (<see cref="VerdictReach"/>, the rule the chain and the participation share), with what it says of it.</summary>
        public RequirementVerdict? VerdictFor(RequirementId requirement)
        {
            var covering = Covering(requirement);
            foreach (var (issued, verdict, standing) in Verdicts.Reverse())
            {
                var onOrdinaryTask = issued.Task is { } one && Tasks.Any(t => t.Id == one && !t.IsReview);
                if (covering.Any(t => VerdictReach.Judges(verdict, issued.Task, onOrdinaryTask, t.Id, requirement)))
                {
                    return new RequirementVerdict(new BoardVerdictFacts(standing.Decision, verdict.Assessments.FirstOrDefault(a => a.Requirement == requirement)?.Status), standing.Challenged, issued);
                }
            }

            return null;
        }
    }
}