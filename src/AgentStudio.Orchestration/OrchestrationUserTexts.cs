using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;

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

    // ---- the reason of a cancellation, as a piece of «Cancelada por ti: {0}» (E) ----
    public const string NoReason = "run.no-reason";
    public const string CancelledFromMonitor = "run.cancelled-from-monitor";

    // ---- delivery: why a run is not delivered, how a delivery ended (DeliveryReadiness.ReasonText, DeliveryResult.MessageText) ----
    public const string DeliveryLogNotIntact = "delivery.log-not-intact";
    public const string DeliveryRunNotFinished = "delivery.run-not-finished";
    public const string DeliveryRunNotSucceeded = "delivery.run-not-succeeded";
    public const string DeliveryNoApprovedIntegration = "delivery.no-approved-integration";
    public const string DeliveryVerdictMissing = "delivery.verdict-missing";
    public const string DeliveryVerdictNotApproved = "delivery.verdict-not-approved";
    public const string DeliveryVerdictNotApprovedInvalid = "delivery.verdict-not-approved-invalid";
    public const string DeliveryStoreNotValidApproval = "delivery.store-not-valid-approval";
    public const string DeliveryVerdictSuperseded = "delivery.verdict-superseded";
    public const string DeliveryStoreDiffers = "delivery.store-differs";
    public const string DeliveryIntegrationAfterVerdict = "delivery.integration-after-verdict";
    public const string DeliveryIntegrationNotJudged = "delivery.integration-not-judged";
    public const string DeliveryShownUnknown = "delivery.shown-unknown";
    public const string DeliveryShownDiffers = "delivery.shown-differs";
    public const string DeliveryReviewOfTask = "delivery.review-of-task";
    public const string DeliveryReviewWithoutTask = "delivery.review-without-task";
    public const string DeliveryJudgeCodeUnknown = "delivery.judge-code-unknown";
    public const string DeliveryJudgeElsewhere = "delivery.judge-elsewhere";
    public const string DeliveryReplanPending = "delivery.replan-pending";
    public const string DeliveryReplanByAuditor = "delivery.replan-by-auditor";
    public const string DeliveryReplanByJudge = "delivery.replan-by-judge";
    public const string DeliveryReplanByOther = "delivery.replan-by-other";
    public const string DeliveryReplanNoFindings = "delivery.replan-no-findings";
    public const string DeliveryReady = "delivery.ready";
    public const string DeliveryReadyRevised = "delivery.ready-revised";
    public const string DeliveryRevisionConfirmed = "delivery.revision-confirmed";
    public const string DeliveryRevisionAccepted = "delivery.revision-accepted";
    public const string DeliveryRevisionOpenUnannounced = "delivery.revision-open-unannounced";
    public const string DeliveryRevisionChainTooLong = "delivery.revision-chain-too-long";
    public const string DeliveryRevisionNoneDecides = "delivery.revision-none-decides";
    public const string DeliveryVerdictNotValidApproval = "delivery.verdict-not-valid-approval";
    public const string DeliveryRevisionWithdraws = "delivery.revision-withdraws";
    public const string DeliveryRevisionNotApproves = "delivery.revision-not-approves";
    public const string DeliveryRevisionUnacceptedConfirms = "delivery.revision-unaccepted-confirms";
    public const string DeliveryRevisionUnacceptedTurns = "delivery.revision-unaccepted-turns";
    public const string DeliveryStoreUnannouncedSupersession = "delivery.store-unannounced-supersession";
    public const string DeliveryRevisionNotOpened = "delivery.revision-not-opened";
    public const string DeliveryRevisionPending = "delivery.revision-pending";
    public const string DeliveryRevisionDecidedByUser = "delivery.revision-decided-by-user";
    public const string DeliveryRevisionAborted = "delivery.revision-aborted";
    public const string DeliveryRevisionNeedsPerson = "delivery.revision-needs-person";
    public const string DeliveryRevisionInconsistent = "delivery.revision-inconsistent";
    public const string DeliveryInconsistentOpeningOtherVerdict = "delivery.inconsistent-opening-other-verdict";
    public const string DeliveryInconsistentOpeningOtherRequirements = "delivery.inconsistent-opening-other-requirements";
    public const string DeliveryInconsistentCopyOtherCommit = "delivery.inconsistent-copy-other-commit";
    public const string DeliveryInconsistentClosedTwice = "delivery.inconsistent-closed-twice";
    public const string DeliveryInconsistentNoVerdicts = "delivery.inconsistent-no-verdicts";
    public const string DeliveryInconsistentVerdictMissing = "delivery.inconsistent-verdict-missing";
    public const string DeliveryInconsistentVerdictBefore = "delivery.inconsistent-verdict-before";
    public const string DeliveryInconsistentVerdictInvalid = "delivery.inconsistent-verdict-invalid";
    public const string DeliveryInconsistentVerdictOtherRequirements = "delivery.inconsistent-verdict-other-requirements";
    public const string DeliveryInconsistentVerdictNotSuperseding = "delivery.inconsistent-verdict-not-superseding";
    public const string DeliveryInconsistentStoreNoConfirm = "delivery.inconsistent-store-no-confirm";
    public const string DeliveryInconsistentVerdictsDisagree = "delivery.inconsistent-verdicts-disagree";
    public const string DeliveryInconsistentChainRepeated = "delivery.inconsistent-chain-repeated";
    public const string DeliveryDecisionApproved = "delivery.decision-approved";
    public const string DeliveryDecisionChangesRequested = "delivery.decision-changes-requested";
    public const string DeliveryDecisionRejected = "delivery.decision-rejected";
    public const string DeliveryDecisionEscalate = "delivery.decision-escalate";
    public const string DeliveryDecisionUnresolved = "delivery.decision-unresolved";
    public const string DeliveryHeadDetached = "delivery.head-detached";
    public const string DeliveryBranchMissing = "delivery.branch-missing";
    public const string DeliveryBranchMoved = "delivery.branch-moved";
    public const string DeliveryAlreadyContained = "delivery.already-contained";
    public const string DeliveryTargetAdvanced = "delivery.target-advanced";
    public const string DeliveryConfigRefused = "delivery.config-refused";
    public const string DeliveryDenied = "delivery.denied";
    public const string DeliveryDeniedWithComment = "delivery.denied-with-comment";
    public const string DeliveryApprovalMismatch = "delivery.approval-mismatch";
    public const string DeliveryRepositoryChanged = "delivery.repository-changed";
    public const string DeliveryRepositoryChangedElsewhere = "delivery.repository-changed-elsewhere";
    public const string DeliveryDelivered = "delivery.delivered";
    public const string DeliveryUnrecorded = "delivery.unrecorded";
    public const string DeliveryDirtyPaths = "delivery.dirty-paths";
    public const string DeliveryConflicts = "delivery.conflicts";
    public const string DeliveryMergeIncomplete = "delivery.merge-incomplete";
    public const string DeliveryGitFailed = "delivery.git-failed";

    // ---- the reasons of the workers and of a retry (TaskStatusChangedEvent.ReasonText) ----
    public const string WorkerCancelledBeforeStart = "worker.cancelled-before-start";
    public const string WorkerLaunchFailed = "worker.launch-failed";
    public const string WorkerProviderCancelled = "worker.provider-cancelled";
    public const string WorkerAgentEndedStatus = "worker.agent-ended-status";
    public const string WorkerReportInvalid = "worker.report-invalid";
    public const string WorkerAgentAsks = "worker.agent-asks";
    public const string WorkerOutsideScope = "worker.outside-scope";
    public const string WorkerVerificationCancelled = "worker.verification-cancelled";
    public const string WorkerVerificationTimeout = "worker.verification-timeout";
    public const string WorkerVerificationFailed = "worker.verification-failed";
    public const string WorkerCancelledByUser = "worker.cancelled-by-user";
    public const string WorkerAbandoned = "worker.abandoned";
    public const string WorkerTimedOut = "worker.timed-out";
    public const string WorkerSignedOut = "worker.signed-out";
    public const string WorkerRateLimitedUntil = "worker.rate-limited-until";
    public const string WorkerRateLimitedNoReset = "worker.rate-limited-no-reset";
    public const string WorkerStepSucceeded = "worker.step-succeeded";
    public const string WorkerStepFailed = "worker.step-failed";
    public const string WorkerStepSkipped = "worker.step-skipped";
    public const string WorkerSourcesMoved = "worker.sources-moved";
    public const string WorkerIntegrationTimeout = "worker.integration-timeout";
    public const string WorkerIntegrationConflict = "worker.integration-conflict";
    public const string WorkerIntegrationVerificationFailed = "worker.integration-verification-failed";
    public const string WorkerReviewTimeout = "worker.review-timeout";
    public const string WorkerJudgeApproved = "worker.judge-approved";
    public const string WorkerJudgeChangesRequested = "worker.judge-changes-requested";
    public const string WorkerJudgeRejected = "worker.judge-rejected";
    public const string WorkerJudgeNeedsPerson = "worker.judge-needs-person";
    public const string WorkerJudgeLimitUntil = "worker.judge-limit-until";
    public const string WorkerJudgeLimitNoReset = "worker.judge-limit-no-reset";
    public const string WorkerApprovalFailed = "worker.approval-failed";
    public const string WorkerBriefLogUnverified = "worker.brief-log-unverified";
    public const string WorkerUpstreamTampered = "worker.upstream-tampered";
    public const string TaskRetry = "task.retry";

    // ---- more reasons of the workers ----
    public const string WorkerIntegrated = "worker.integrated";
    public const string WorkerToolSilent = "worker.tool-silent";
    public const string WorkerToolTimeout = "worker.tool-timeout";
    public const string WorkerSystemError = "worker.system-error";
    public const string WorkerApproved = "worker.approved";
    public const string WorkerDenied = "worker.denied";
    public const string WorkerDeniedWithComment = "worker.denied-with-comment";

    // ---- resuming by itself, the wait for the usage limit, the check before relaunching and the inspection of a log ----
    public const string ResumeNoConsent = "resume.no-consent";
    public const string ResumeNotHalfway = "resume.not-halfway";
    public const string ResumePaused = "resume.paused";
    public const string ResumeLeftOnExit = "resume.left-on-exit";
    public const string ResumeSessionEnded = "resume.session-ended";
    public const string ResumeWaitingLimit = "resume.waiting-limit";
    public const string ResumeUnrequestedClose = "resume.unrequested-close";
    public const string ResumeFailedBefore = "resume.failed-before";
    public const string ResumeSuspended = "resume.suspended";
    public const string ResumeFailed = "resume.failed";
    public const string ResumeStoppedByError = "resume.stopped-by-error";
    public const string ResumeNotRegistered = "resume.not-registered";
    public const string QuotaWhoJudge = "quota.who-judge";
    public const string QuotaWhoOverload = "quota.who-overload";
    public const string QuotaWhoPlain = "quota.who-plain";
    public const string QuotaWait = "quota.wait";
    public const string QuotaWaitEstimated = "quota.wait-estimated";
    public const string QuotaUntil = "quota.until";
    public const string QuotaNoReset = "quota.no-reset";
    public const string QuotaPassedReset = "quota.passed-reset";
    public const string WorkerNotSignedIn = "worker.not-signed-in";
    public const string WorkerLimitStillOnUntil = "worker.limit-still-on-until";
    public const string WorkerLimitStillOnNoReset = "worker.limit-still-on-no-reset";
    public const string InspectLogNotReplayable = "inspect.log-not-replayable";
    public const string InspectProblemUnknownTasks = "inspect.problem-unknown-tasks";
    public const string InspectProblemNotRebuilt = "inspect.problem-not-rebuilt";
    public const string InspectProblemDetail = "inspect.problem-detail";

    // ---- the planner, the notice of the search in the history, the review of the order and the chat of preparation ----
    public const string PlannerCorrectedUnreadable = "planner.corrected-unreadable";
    public const string PlannerCorrectedInvalid = "planner.corrected-invalid";
    public const string HistoryNotThePlanner = "history.not-the-planner";
    public const string HistoryDisabled = "history.disabled";
    public const string HistoryNoMemory = "history.no-memory";
    public const string HistoryNotReadOnly = "history.not-read-only";
    public const string HistoryEmpty = "history.empty";
    public const string HistoryNoMcp = "history.no-mcp";
    public const string HistoryFailed = "history.failed";
    public const string OrderNoNotes = "order.no-notes";
    public const string OrderSchemaMismatch = "order.schema-mismatch";
    public const string ChatNoAnswer = "chat.no-answer";
    public const string ChatSchemaMismatch = "chat.schema-mismatch";
    public const string ChatAnswerNull = "chat.answer-null";

    // ---- the memory that does not fit or does not verify, the user's repository that changed under a step, the evidence of an integration ----
    public const string TaskNoIntegration = "task.no-integration";
    public const string WorkerMemoryTooSmall = "worker.memory-too-small";
    public const string MemoryDetailFigures = "worker.memory-detail-figures";
    public const string MemoryDetailUnknown = "worker.memory-detail-unknown";
    public const string WorkerMemoryUnverified = "worker.memory-unverified";
    public const string WorkerMemoryUnverifiedEntry = "worker.memory-unverified-entry";
    public const string WorkerRepositoryBeforeLaunch = "worker.repository-before-launch";
    public const string WorkerRepositoryChanged = "worker.repository-changed";
    public const string RepositoryWhileAgent = "worker.repository-when-while-agent";
    public const string RepositoryDuringVerification = "worker.repository-when-during-verification";
    public const string RepositoryDuringIntegration = "worker.repository-when-during-integration";
    public const string RepositoryBeforeIntegration = "worker.repository-when-before-integration";
    public const string RepositoryDuringReview = "worker.repository-when-during-review";
    public const string RepositoryBeforeReview = "worker.repository-when-before-review";
    public const string RepositoryDuringSteps = "worker.repository-when-during-steps";
    public const string RepositoryDuringStepsStopped = "worker.repository-when-during-steps-stopped";
    public const string RepositoryBeforeSteps = "worker.repository-when-before-steps";
    public const string StepsOne = "worker.steps-one";
    public const string StepsMany = "worker.steps-many";
    public const string WorkerEvidenceTampered = "worker.evidence-tampered";
    public const string WorkerEvidenceUnanchored = "worker.evidence-unanchored";

    /// <summary>«El registro de decisiones no está íntegro» without the number of an entry (the log does not say which).</summary>
    public const string DeliveryLogNotIntactUnknown = "delivery.log-not-intact-unknown";

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
        NoReason, CancelledFromMonitor,
        DeliveryLogNotIntact, DeliveryRunNotFinished, DeliveryRunNotSucceeded, DeliveryNoApprovedIntegration,
        DeliveryVerdictMissing, DeliveryVerdictNotApproved, DeliveryVerdictNotApprovedInvalid, DeliveryStoreNotValidApproval,
        DeliveryVerdictSuperseded, DeliveryStoreDiffers, DeliveryIntegrationAfterVerdict, DeliveryIntegrationNotJudged,
        DeliveryShownUnknown, DeliveryShownDiffers, DeliveryReviewOfTask, DeliveryReviewWithoutTask, DeliveryJudgeCodeUnknown,
        DeliveryJudgeElsewhere, DeliveryReplanPending, DeliveryReplanByAuditor, DeliveryReplanByJudge, DeliveryReplanByOther,
        DeliveryReplanNoFindings, DeliveryReady, DeliveryReadyRevised, DeliveryRevisionConfirmed, DeliveryRevisionAccepted,
        DeliveryRevisionOpenUnannounced, DeliveryRevisionChainTooLong, DeliveryRevisionNoneDecides,
        DeliveryVerdictNotValidApproval, DeliveryRevisionWithdraws, DeliveryRevisionNotApproves,
        DeliveryRevisionUnacceptedConfirms, DeliveryRevisionUnacceptedTurns, DeliveryStoreUnannouncedSupersession,
        DeliveryRevisionNotOpened, DeliveryRevisionPending, DeliveryRevisionDecidedByUser, DeliveryRevisionAborted,
        DeliveryRevisionNeedsPerson, DeliveryRevisionInconsistent, DeliveryInconsistentOpeningOtherVerdict,
        DeliveryInconsistentOpeningOtherRequirements, DeliveryInconsistentCopyOtherCommit, DeliveryInconsistentClosedTwice,
        DeliveryInconsistentNoVerdicts, DeliveryInconsistentVerdictMissing, DeliveryInconsistentVerdictBefore,
        DeliveryInconsistentVerdictInvalid, DeliveryInconsistentVerdictOtherRequirements,
        DeliveryInconsistentVerdictNotSuperseding, DeliveryInconsistentStoreNoConfirm, DeliveryInconsistentVerdictsDisagree,
        DeliveryInconsistentChainRepeated, DeliveryDecisionApproved, DeliveryDecisionChangesRequested, DeliveryDecisionRejected,
        DeliveryDecisionEscalate, DeliveryDecisionUnresolved, DeliveryHeadDetached, DeliveryBranchMissing, DeliveryBranchMoved,
        DeliveryAlreadyContained, DeliveryTargetAdvanced, DeliveryConfigRefused, DeliveryDenied, DeliveryDeniedWithComment,
        DeliveryApprovalMismatch, DeliveryRepositoryChanged, DeliveryRepositoryChangedElsewhere, DeliveryDelivered,
        DeliveryUnrecorded, DeliveryDirtyPaths, DeliveryConflicts, DeliveryMergeIncomplete, DeliveryGitFailed,
        WorkerCancelledBeforeStart, WorkerLaunchFailed, WorkerProviderCancelled, WorkerAgentEndedStatus, WorkerReportInvalid,
        WorkerAgentAsks, WorkerOutsideScope, WorkerVerificationCancelled, WorkerVerificationTimeout, WorkerVerificationFailed,
        WorkerCancelledByUser, WorkerAbandoned, WorkerTimedOut, WorkerSignedOut, WorkerRateLimitedUntil,
        WorkerRateLimitedNoReset, WorkerStepSucceeded, WorkerStepFailed, WorkerStepSkipped, WorkerSourcesMoved,
        WorkerIntegrationTimeout, WorkerIntegrationConflict, WorkerIntegrationVerificationFailed, WorkerReviewTimeout,
        WorkerJudgeApproved, WorkerJudgeChangesRequested, WorkerJudgeRejected, WorkerJudgeNeedsPerson, WorkerJudgeLimitUntil,
        WorkerJudgeLimitNoReset, WorkerApprovalFailed, WorkerBriefLogUnverified, WorkerUpstreamTampered, TaskRetry,
        WorkerIntegrated, WorkerToolSilent, WorkerToolTimeout, WorkerSystemError, WorkerApproved, WorkerDenied,
        WorkerDeniedWithComment,
        ResumeNoConsent, ResumeNotHalfway, ResumePaused, ResumeLeftOnExit, ResumeSessionEnded, ResumeWaitingLimit,
        ResumeUnrequestedClose, ResumeFailedBefore, ResumeSuspended, ResumeFailed, ResumeStoppedByError, ResumeNotRegistered,
        QuotaWhoJudge, QuotaWhoOverload, QuotaWhoPlain, QuotaWait, QuotaWaitEstimated, QuotaUntil, QuotaNoReset,
        QuotaPassedReset, WorkerNotSignedIn, WorkerLimitStillOnUntil, WorkerLimitStillOnNoReset, InspectLogNotReplayable,
        InspectProblemUnknownTasks, InspectProblemNotRebuilt, InspectProblemDetail,
        PlannerCorrectedUnreadable, PlannerCorrectedInvalid, HistoryNotThePlanner, HistoryDisabled, HistoryNoMemory,
        HistoryNotReadOnly, HistoryEmpty, HistoryNoMcp, HistoryFailed, OrderNoNotes, OrderSchemaMismatch, ChatNoAnswer,
        ChatSchemaMismatch, ChatAnswerNull,
        TaskNoIntegration, WorkerMemoryTooSmall, MemoryDetailFigures, MemoryDetailUnknown, WorkerMemoryUnverified, WorkerMemoryUnverifiedEntry, WorkerRepositoryBeforeLaunch, WorkerRepositoryChanged, RepositoryWhileAgent, RepositoryDuringVerification, RepositoryDuringIntegration, RepositoryBeforeIntegration, RepositoryDuringReview, RepositoryBeforeReview, RepositoryDuringSteps, RepositoryDuringStepsStopped, RepositoryBeforeSteps, StepsOne, StepsMany, WorkerEvidenceTampered, WorkerEvidenceUnanchored, DeliveryLogNotIntactUnknown,
    ];

    /// <summary>
    /// Why the mandatory requirements do not fit next to the memory, as a piece of «No se lanza …: {0}»: the figures when the exception
    /// has them, and the plain fact when it does not (its message is for the logs and stays in English).
    /// </summary>
    public static UserText MemoryTooSmall(ContextBudgetTooSmallException problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.Required > 0 ? UserText.Of(MemoryDetailFigures, problem.Required, problem.Budget) : UserText.Of(MemoryDetailUnknown);
    }

    /// <summary>«el paso build», «los pasos build y test»: the steps a sentence is about, as a piece.</summary>
    public static UserText Steps(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Count == 1 ? UserText.Of(StepsOne, ids[0]) : UserText.Of(StepsMany, List(ids));
    }

    /// <summary>
    /// How a run ended, as a piece of a sentence: the word that the desktop already has for each phase (the memory writes them in
    /// lower case, <c>memory.phase.*</c>), so a phase is never shown by the name of its type.
    /// </summary>
    public static UserText Phase(RunPhase phase) => UserText.Of(
        "memory.phase." + string.Concat(phase.ToString().Select((c, i) => char.IsUpper(c) ? (i == 0 ? char.ToLowerInvariant(c).ToString() : "-" + char.ToLowerInvariant(c)) : c.ToString())));

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
