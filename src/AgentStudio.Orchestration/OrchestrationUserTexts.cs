using AgentStudio.Application.Localization;

namespace AgentStudio.Orchestration;

/// <summary>
/// The texts of the orchestration the person reads and that are not anchored (docs/17 D-47, ADR-0054 addendum of 2026-11-12): a code
/// and its arguments, painted by the desktop in the language of the person (<c>Text_</c> + the code, in <c>OrchestratorStrings</c>).
/// What the engine anchors or sends (the first notice of a run, the approved warning, the summary of <c>RunFinished</c>, the
/// <c>Reason</c> of a budget verdict, the prompts) is not changed: it stays as it is (D-48), and a code is written next to it. The
/// messages of the exceptions are in English.
/// </summary>
public static class OrchestrationUserTexts
{
    // ---- an enumeration of names, as a piece of other texts ----
    /// <summary>«{0} y {1}»: the head of the list, joined with commas, and its last item.</summary>
    public const string ListAnd = "plan.list.and";

    /// <summary>«{0}, {1}»: the head of a list of pieces and the next one.</summary>
    public const string ListComma = "plan.list.comma";

    // ---- the delivery warnings of a plan (A) ----
    public const string DeliveryNotChecked = "plan.delivery.not-checked";
    public const string DeliveryNeverOne = "plan.delivery.never-one";
    public const string DeliveryNeverMany = "plan.delivery.never-many";
    public const string DeliveryMaybeOne = "plan.delivery.maybe-one";
    public const string DeliveryMaybeMany = "plan.delivery.maybe-many";
    public const string DeliveryReasonBranch = "plan.delivery.reason-branch";
    public const string DeliveryReasonTiming = "plan.delivery.reason-timing";
    public const string DeliveryBranchYes = "plan.delivery.branch-yes";
    public const string DeliveryBranchNo = "plan.delivery.branch-no";
    public const string DeliveryBranchOther = "plan.delivery.branch-other";
    public const string DeliveryHow = "plan.delivery.how";

    // ---- a step of the verification profile and the warning about writers at medium integrity (B) ----
    public const string StepRequires = "verification.step-requires";
    public const string StepWithoutIsolation = "verification.step-without-isolation";
    public const string MediumOpening = "verification.medium.opening";
    public const string MediumTask = "verification.medium.task";
    public const string MediumMeaning = "verification.medium.meaning";
    public const string MediumReasonUnknownAgent = "verification.medium.reason-unknown-agent";
    public const string MediumReasonNoAgent = "verification.medium.reason-no-agent";
    public const string MediumReasonShell = "verification.medium.reason-shell";
    public const string MediumReasonCodex = "verification.medium.reason-codex";
    public const string MediumReasonUnmeasuredCli = "verification.medium.reason-unmeasured-cli";
    public const string MediumReasonUnmeasuredClaude = "verification.medium.reason-unmeasured-claude";
    public const string MediumReasonToolsBeyond = "verification.medium.reason-tools-beyond";
    public const string MediumReasonPermissionsBeyond = "verification.medium.reason-permissions-beyond";
    public const string MediumReasonUnknownStep = "verification.medium.reason-unknown-step";
    public const string MediumReasonStepWithoutIsolation = "verification.medium.reason-step-without-isolation";

    // ---- how a run stands or ended (C): RunResult.SummaryText and RunPhaseChangedEvent.DetailText ----
    /// <summary>«{0} {1}»: sentences one after another.</summary>
    public const string ListSpace = "plan.list.space";

    public const string VerdictSucceeded = "run.verdict.succeeded";
    public const string VerdictCancelledAfterStop = "run.verdict.cancelled-after-stop";
    public const string VerdictCancelledByUser = "run.verdict.cancelled-by-user";
    public const string VerdictReviewRequired = "run.verdict.review-required";
    public const string VerdictTestsRequired = "run.verdict.tests-required";
    public const string VerdictUntestedCommit = "run.verdict.untested-commit";
    public const string VerdictUntestedNoCommit = "run.verdict.untested-no-commit";
    public const string VerdictStoppedRepository = "run.verdict.stopped-repository";
    public const string VerdictStoppedSourceMoved = "run.verdict.stopped-source-moved";
    public const string VerdictStoppedEvidence = "run.verdict.stopped-evidence";
    public const string VerdictFailed = "run.verdict.failed";
    public const string VerdictNoRetries = "run.verdict.no-retries";
    public const string VerdictUnreachable = "run.verdict.unreachable";
    public const string VerdictBudgetSpent = "run.verdict.budget-spent";
    public const string VerdictInterrupted = "run.verdict.interrupted";
    public const string VerdictAwaitingHuman = "run.verdict.awaiting-human";
    public const string VerdictBlockedLaunch = "run.verdict.blocked-launch";
    public const string VerdictWaitingQuota = "run.verdict.waiting-quota";
    public const string VerdictWaitingQuotaEstimated = "run.verdict.waiting-quota-estimated";
    public const string VerdictPausedChecks = "run.verdict.paused-checks";
    public const string VerdictPausedLong = "run.verdict.paused-long";
    public const string VerdictPaused = "run.verdict.paused";
    public const string VerdictDeadlock = "run.verdict.deadlock";

    // ---- why a task is where it is (E): TaskStatusChangedEvent.ReasonText ----
    public const string TaskAttemptLaunched = "task.attempt-launched";
    public const string TaskAttemptStarted = "task.attempt-started";
    public const string TaskEvaluatingCondition = "task.evaluating-condition";
    public const string TaskRetryByUser = "task.retry-by-user";
    public const string TaskQuotaWaitEnded = "task.quota-wait-ended";
    public const string TaskAwaitingApproval = "task.awaiting-approval";
    public const string TaskNodeUnavailable = "task.node-unavailable";
    public const string TaskNoProvider = "task.no-provider";
    public const string TaskCancelledByUser = "task.cancelled-by-user";
    public const string TaskInterrupted = "task.interrupted";
    public const string TaskJudgeApprovedResumed = "task.judge-approved-resumed";
    public const string TaskVerdictNeedsPersonResumed = "task.verdict-needs-person-resumed";
    public const string TaskQuestionAnsweredBeforeClose = "task.question-answered-before-close";
    public const string TaskResumedByUser = "task.resumed-by-user";
    public const string TaskResumedByConsent = "task.resumed-by-consent";

    // ---- what the monitor is told of a run read back from the log or resumed (E) ----
    public const string VerdictInterruptedInLog = "run.verdict.interrupted-in-log";
    public const string RunResumedConsentQuota = "run.resumed-consent-quota";
    public const string RunResumedConsentClose = "run.resumed-consent-close";
    public const string RunResumedConsentSession = "run.resumed-consent-session";
    public const string RunResumedFromLog = "run.resumed-from-log";
    public const string RunPausedByYou = "run.paused-by-you";
    public const string RunResumedByYou = "run.resumed-by-you";
    public const string RunPausedBy = "run.paused-by";
    public const string RunResumedBy = "run.resumed-by";
    public const string NoticeUncommittedChanges = "run.notice-uncommitted-changes";

    // ---- why the orchestrator did not give a plan (F): PlanProposal.ErrorText ----
    public const string PlannerSchemaTooBig = "planner.schema-too-big";
    public const string PlannerNoProvider = "planner.no-provider";
    public const string PlannerMemoryTooSmall = "planner.memory-too-small";
    public const string PlannerRepositoryChanged = "planner.repository-changed";
    public const string PlannerCancelled = "planner.cancelled";
    public const string PlannerLaunchFailed = "planner.launch-failed";
    public const string PlannerEnded = "planner.ended";

    // ---- the budget of the project and of the milestone (D): BudgetGateVerdict.Text ----
    public const string BudgetNameProjectDay = "project.budget-name-project-day";
    public const string BudgetNameProjectWeek = "project.budget-name-project-week";
    public const string BudgetNameProjectMonth = "project.budget-name-project-month";
    public const string BudgetNameProjectTotal = "project.budget-name-project-total";
    public const string BudgetNameMilestoneDay = "project.budget-name-milestone-day";
    public const string BudgetNameMilestoneWeek = "project.budget-name-milestone-week";
    public const string BudgetNameMilestoneMonth = "project.budget-name-milestone-month";
    public const string BudgetNameMilestoneTotal = "project.budget-name-milestone-total";
    public const string BudgetExceededTokens = "project.budget-exceeded-tokens";
    public const string BudgetExceededTokensUnknown = "project.budget-exceeded-tokens-unknown";
    public const string BudgetExceededRuns = "project.budget-exceeded-runs";
    public const string BudgetPartial = "project.budget-partial";
    public const string BudgetRaise = "project.budget-raise";
    public const string BudgetUnreadable = "project.budget-unreadable";
    public const string BudgetWhyNoUsage = "project.budget-why-no-usage";
    public const string BudgetWhyUntrusted = "project.budget-why-untrusted";
    public const string BudgetWhyFailed = "project.budget-why-failed";

    /// <summary>Every code of this class: the desktop has a text for each of them in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ListAnd, ListComma,
        DeliveryNotChecked, DeliveryNeverOne, DeliveryNeverMany, DeliveryMaybeOne, DeliveryMaybeMany, DeliveryReasonBranch,
        DeliveryReasonTiming, DeliveryBranchYes, DeliveryBranchNo, DeliveryBranchOther, DeliveryHow,
        StepRequires, StepWithoutIsolation, MediumOpening, MediumTask, MediumMeaning, MediumReasonUnknownAgent, MediumReasonNoAgent,
        MediumReasonShell, MediumReasonCodex, MediumReasonUnmeasuredCli, MediumReasonUnmeasuredClaude, MediumReasonToolsBeyond,
        MediumReasonPermissionsBeyond, MediumReasonUnknownStep, MediumReasonStepWithoutIsolation,
        ListSpace, VerdictSucceeded, VerdictCancelledAfterStop, VerdictCancelledByUser, VerdictReviewRequired, VerdictTestsRequired,
        VerdictUntestedCommit, VerdictUntestedNoCommit, VerdictStoppedRepository, VerdictStoppedSourceMoved, VerdictStoppedEvidence,
        VerdictFailed, VerdictNoRetries, VerdictUnreachable, VerdictBudgetSpent, VerdictInterrupted, VerdictAwaitingHuman,
        VerdictBlockedLaunch, VerdictWaitingQuota, VerdictWaitingQuotaEstimated, VerdictPausedChecks, VerdictPausedLong, VerdictPaused,
        VerdictDeadlock,
        TaskAttemptLaunched, TaskAttemptStarted, TaskEvaluatingCondition, TaskRetryByUser, TaskQuotaWaitEnded, TaskAwaitingApproval,
        TaskNodeUnavailable, TaskNoProvider, TaskCancelledByUser, TaskInterrupted, TaskJudgeApprovedResumed,
        TaskVerdictNeedsPersonResumed, TaskQuestionAnsweredBeforeClose, TaskResumedByUser, TaskResumedByConsent, VerdictInterruptedInLog,
        RunResumedConsentQuota, RunResumedConsentClose, RunResumedConsentSession, RunResumedFromLog, RunPausedByYou, RunResumedByYou,
        RunPausedBy, RunResumedBy, NoticeUncommittedChanges, PlannerSchemaTooBig, PlannerNoProvider, PlannerMemoryTooSmall,
        PlannerRepositoryChanged, PlannerCancelled, PlannerLaunchFailed, PlannerEnded,
        BudgetNameProjectDay, BudgetNameProjectWeek, BudgetNameProjectMonth, BudgetNameProjectTotal, BudgetNameMilestoneDay,
        BudgetNameMilestoneWeek, BudgetNameMilestoneMonth, BudgetNameMilestoneTotal, BudgetExceededTokens, BudgetExceededTokensUnknown,
        BudgetExceededRuns, BudgetPartial, BudgetRaise, BudgetUnreadable, BudgetWhyNoUsage, BudgetWhyUntrusted, BudgetWhyFailed,
    ];

    /// <summary>The sentences one after another as a single text; the one itself when there is one.</summary>
    public static UserText Sentences(IReadOnlyList<UserText> sentences)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        if (sentences.Count == 0)
        {
            throw new ArgumentException("There is at least one sentence.", nameof(sentences));
        }

        return sentences.Skip(1).Aggregate(sentences[0], (head, next) => UserText.Of(ListSpace, head, next));
    }

    /// <summary>
    /// An enumeration as one argument: the item itself when there is one, and «a», «a y b», «a, b y c» as a piece (the desktop puts
    /// the «y» in its language) otherwise. Never empty.
    /// </summary>
    public static object List(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => UserText.Of(ListAnd, string.Join(", ", items.Take(items.Count - 1)), items[^1]),
        };
    }

    /// <summary>The same as <see cref="List(IReadOnlyList{string})"/> for pieces that are coded texts of their own.</summary>
    public static object List(IReadOnlyList<UserText> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => UserText.Of(ListAnd, items.Take(items.Count - 1).Skip(1).Aggregate(items[0], (head, next) => UserText.Of(ListComma, head, next)), items[^1]),
        };
    }
}
