using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;

namespace AgentStudio.Observability.Usage;

/// <summary>Where the orchestration run of an attempt stands, as far as its destination goes (docs/16 D-34).</summary>
public enum ActivityRunState
{
    /// <summary>Still going, paused, waiting or interrupted: nothing is settled.</summary>
    Open,

    /// <summary>It reached <c>Succeeded</c> and nothing has been delivered to the person's branch (yet).</summary>
    Undelivered,

    /// <summary>It was delivered (<c>IntegrationPerformed</c> with <c>IsDelivery</c>).</summary>
    Delivered,

    /// <summary>It ended <c>Failed</c>, <c>Cancelled</c> or <c>Blocked</c>: its work is not going anywhere.</summary>
    Dead,
}

/// <summary>What the log says of the orchestration run an attempt belongs to.</summary>
/// <param name="State">Where the run stands.</param>
/// <param name="FollowsConflict">The run follows another whose integration ended in a conflict (a follow-up opened to resolve it).</param>
public sealed record ActivityRunFacts(ActivityRunState State, bool FollowsConflict = false);

/// <summary>What the log says of the task of an attempt.</summary>
/// <param name="Status">The last status of the task; null when it has none recorded.</param>
/// <param name="ChangedPaths">
/// The paths the attempt is known to have changed, or, when only the plan is known, the write scope of the task; null when neither is. The
/// rule is <see cref="ActivityPaths.OfDiff"/> either way.
/// </param>
/// <param name="Merged">The work of the task reached an integration (a merge with a commit): the attempt left a commit that went somewhere.</param>
public sealed record ActivityTaskFacts(WorkTaskStatus? Status, IReadOnlyList<string>? ChangedPaths, bool Merged);

/// <summary>Everything the classification of one attempt reads.</summary>
/// <param name="Purpose">What the attempt was for as its start recorded it; null for a start of before Phase 4.</param>
/// <param name="HasTask">The attempt belongs to a task.</param>
/// <param name="Attempt">The number of the attempt of its task, from 1.</param>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Run">The orchestration run of the attempt; null when its run is not one (a debate, an audit, an auxiliary run).</param>
/// <param name="Task">The task of the attempt; null for the planner, the judge and every unit that has no task.</param>
public sealed record ActivityInput(AgentRunPurpose? Purpose, bool HasTask, int Attempt, AttemptOutcome Outcome, ActivityRunFacts? Run, ActivityTaskFacts? Task);

/// <summary>
/// The classification of the activity of an attempt (docs/16 D-34, F9-6, ADR-0040): pure and deterministic, no clock, no store, and no
/// question to any agent. Every attempt gets one value on each of three axes, so it counts once in each and the groups of an axis add up
/// to the total.
/// </summary>
/// <remarks>
/// <para><b>What.</b> The purpose decides, except for an executor: the planner, the order review and the chat of preparation are
/// <see cref="ActivityKind.Coordination"/>; the participants of a debate, the recommendation of skills, the analysis of consumption and the
/// help are <see cref="ActivityKind.Analysis"/>; the judges, the audits and the challenges are <see cref="ActivityKind.Review"/>; the daily
/// summary and the parts of a document for clients are <see cref="ActivityKind.Documentation"/>. An executor of a run that follows one
/// that ended in a conflict is <see cref="ActivityKind.Conflicts"/>; any other executor is told by <see cref="ActivityPaths.OfDiff"/>.
/// A start of before Phase 4 records no purpose: with a task it is an executor, without one the planner.</para>
/// <para><b>Order.</b> The first attempt of a task or a retry (number above 1; the correction of the planner is a retry).</para>
/// <para><b>Destination.</b> From the run: in a delivered run, the work of a completed task (and the attempts of the run itself, which
/// have no task) is <see cref="AttemptFate.Kept"/> and that of any other task <see cref="AttemptFate.Discarded"/>; in a dead run everything
/// is discarded; in any other, a task that failed, was cancelled or skipped is discarded and the rest is <see cref="AttemptFate.Pending"/>.
/// A unit that is not an orchestration run (a debate, an audit, an auxiliary run) has no integration to reach: what ended well is kept
/// and what did not, discarded. <see cref="ActivityClass.HasCommit"/> is whether the work of the task reached an integration.</para>
/// </remarks>
public static class ActivityClassifier
{
    /// <summary>The value of an attempt on the three axes.</summary>
    public static ActivityClass Classify(ActivityInput input, IReadOnlyList<string>? testPaths = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new ActivityClass(KindOf(input, testPaths), input.Attempt > 1 ? AttemptOrder.Retry : AttemptOrder.First, FateOf(input), input.Task?.Merged ?? false);
    }

    /// <summary>The first axis.</summary>
    public static ActivityKind KindOf(ActivityInput input, IReadOnlyList<string>? testPaths = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var purpose = input.Purpose ?? (input.HasTask ? AgentRunPurpose.Executor : AgentRunPurpose.Planner);
        return purpose switch
        {
            AgentRunPurpose.Planner or AgentRunPurpose.OrderReview or AgentRunPurpose.PreparationChat => ActivityKind.Coordination,
            AgentRunPurpose.DebateParticipant or AgentRunPurpose.SkillRecommendation or AgentRunPurpose.ConsumptionAnalysis or AgentRunPurpose.Help => ActivityKind.Analysis,
            AgentRunPurpose.Judge or AgentRunPurpose.DebateJudge or AgentRunPurpose.ProjectAudit => ActivityKind.Review,
            AgentRunPurpose.DailySummary or AgentRunPurpose.ClientDocumentDraft => ActivityKind.Documentation,
            _ => input.Run is { FollowsConflict: true } ? ActivityKind.Conflicts : ActivityPaths.OfDiff(input.Task?.ChangedPaths ?? [], testPaths),
        };
    }

    /// <summary>The third axis.</summary>
    public static AttemptFate FateOf(ActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Run is not { } run)
        {
            return input.Outcome == AttemptOutcome.Succeeded ? AttemptFate.Kept : AttemptFate.Discarded;
        }

        var status = input.Task?.Status;
        return run.State switch
        {
            ActivityRunState.Delivered => input.Task is null || status == WorkTaskStatus.Completed ? AttemptFate.Kept : AttemptFate.Discarded,
            ActivityRunState.Dead => AttemptFate.Discarded,
            _ => status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled or WorkTaskStatus.Skipped ? AttemptFate.Discarded : AttemptFate.Pending,
        };
    }
}
