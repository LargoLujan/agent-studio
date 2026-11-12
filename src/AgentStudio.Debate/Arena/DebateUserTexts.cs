using AgentStudio.Application.Localization;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The texts of a debate the person reads and that are not anchored (docs/17 D-47, ADR-0054 addendum of 2026-11-12): a code
/// and its arguments, painted by the desktop in the language of the person (<c>Text_</c> + the code, in <c>DebateStrings</c>).
/// What the debate anchors (the reasons of <c>DebateRoundClosed</c> and <c>DebateClosed</c>, the approval texts, the
/// requirements and the prompts) is not here: it stays as it is (D-48). The messages of the exceptions are in English.
/// </summary>
public static class DebateUserTexts
{
    // ---- refusals of a running debate ----
    public const string JudgmentTooEarly = "debate.judgment-too-early";
    public const string NotWaitingRetry = "debate.not-waiting-retry";
    public const string ClosedEvidence = "debate.closed-evidence";
    public const string ClosedTests = "debate.closed-tests";
    public const string NoTestCommit = "debate.no-test-commit";
    public const string NoTrustProfile = "debate.no-trust-profile";
    public const string NoIsolatedStep = "debate.no-isolated-step";
    public const string NotWaitingDecision = "debate.not-waiting-decision";
    public const string AlreadyClosed = "debate.already-closed";
    public const string DecisionNotApproved = "debate.decision-not-approved";
    public const string CannotJudgeNow = "debate.cannot-judge-now";

    // ---- refusals of the coordinator ----
    public const string ProposalOutdated = "debate.proposal-outdated";
    public const string CannotOpen = "debate.cannot-open";
    public const string OpeningNotApproved = "debate.opening-not-approved";
    public const string OpeningInconsistent = "debate.opening-inconsistent";
    public const string ResumeNotApproved = "debate.resume-not-approved";
    public const string AlreadyRunning = "debate.already-running";
    public const string NotFound = "debate.not-found";
    public const string AlreadyFinished = "debate.already-finished";
    public const string CannotResume = "debate.cannot-resume";
    public const string ProjectBusy = "debate.project-busy";
    public const string NoProvider = "debate.no-provider";

    // ---- why a debate waits for the person (DebateAwaiting.Texts) ----
    public const string AwaitJuryDisagrees = "debate.await.jury-disagrees";
    public const string AwaitNeedsPerson = "debate.await.needs-person";
    public const string AwaitVerdictsUnusable = "debate.await.verdicts-unusable";
    public const string AwaitNoVerdict = "debate.await.no-verdict";
    public const string TurnNoBudget = "debate.turn.no-budget";
    public const string TurnInvalidAnswer = "debate.turn.invalid-answer";
    public const string TurnLimitResets = "debate.turn.limit-resets";
    public const string TurnLimitUnknown = "debate.turn.limit-unknown";
    public const string TurnOverloaded = "debate.turn.overloaded";
    public const string TurnEndedBadly = "debate.turn.ended-badly";

    // ---- why the proposal of a debate cannot open (DebateProposal.ProblemTexts) ----
    public const string ProblemParticipantMissing = "debate.problem.participant-missing";
    public const string ProblemParticipantRole = "debate.problem.participant-role";
    public const string ProblemNoProvider = "debate.problem.no-provider";
    public const string ProblemJudgeMissing = "debate.problem.judge-missing";
    public const string ProblemJudgeRole = "debate.problem.judge-role";
    public const string ProblemOrchestratorRequirements = "debate.problem.orchestrator-requirements";
    public const string ProblemNoArrangement = "debate.problem.no-arrangement";
    public const string ProblemSessionRefused = "debate.problem.session-refused";
    public const string ProblemNoRoundFits = "debate.problem.no-round-fits";
    public const string ProblemRunUnnamed = "debate.problem.run-unnamed";
    public const string ProblemRunNotInLog = "debate.problem.run-not-in-log";
    public const string ProblemNoCandidateCommit = "debate.problem.no-candidate-commit";
    public const string ProblemAuditRequirements = "debate.problem.audit-requirements";
    public const string ProblemUnknownSubject = "debate.problem.unknown-subject";
    public const string ProblemVerdictUnnamed = "debate.problem.verdict-unnamed";
    public const string ProblemVerdictNotStored = "debate.problem.verdict-not-stored";
    public const string ProblemDebateMissing = "debate.problem.debate-missing";
    public const string ProblemAuditMissing = "debate.problem.audit-missing";

    // ---- the notes of the estimate of a debate (DebateEstimate.NoteTexts) ----
    public const string NoteUncommitted = "debate.note.uncommitted";
    public const string NoteMaxRunsOneRound = "debate.note.max-runs-one-round";
    public const string NoteMaxRuns = "debate.note.max-runs";
    public const string NoteTokensPerTurn = "debate.note.tokens-per-turn";
    public const string NoteTokenLimit = "debate.note.token-limit";
    public const string NoteEstimated = "debate.note.estimated";

    // ---- what the reading, the replay and the room of a debate found (DebateSummary.ProblemTexts and DebateSnapshot.ProblemTexts) ----
    public const string ProblemLogUnverified = "debate.problem.log-unverified";
    public const string ProblemNotOpened = "debate.problem.not-opened";
    public const string ProblemHeaderMismatch = "debate.problem.header-mismatch";
    public const string ProblemQuestionUnreadable = "debate.problem.question-unreadable";
    public const string ProblemRequirementsMissing = "debate.problem.requirements-missing";
    public const string ProblemContributionUnreadable = "debate.problem.contribution-unreadable";
    public const string ProblemEntryUnreadable = "debate.problem.entry-unreadable";
    public const string ProblemReplay = "debate.problem.replay";
    public const string ProblemFinalFigures = "debate.problem.final-figures";
    public const string ProblemStopped = "debate.problem.stopped";
    public const string ProblemNothingToJudge = "debate.problem.nothing-to-judge";
    public const string ProblemCopyNotDeleted = "debate.problem.copy-not-deleted";

    // ---- refusals, problems and notes of the challenge of a verdict ----
    public const string ChallengeNotRevision = "debate.challenge.not-revision";
    public const string ChallengeCannotOpen = "debate.challenge.cannot-open";
    public const string ChallengeProposalOutdated = "debate.challenge.proposal-outdated";
    public const string ChallengeVerdictNotStored = "debate.challenge.verdict-not-stored";
    public const string ChallengeNothingToAccept = "debate.challenge.nothing-to-accept";
    public const string ChallengeNotAwaitingAcceptance = "debate.challenge.not-awaiting-acceptance";
    public const string ChallengeAcceptanceNotAnswered = "debate.challenge.acceptance-not-answered";
    public const string ChallengeProblemLogBroken = "debate.challenge.problem.log-broken";
    public const string ChallengeProblemReasonsMissing = "debate.challenge.problem.reasons-missing";
    public const string ChallengeProblemVerdictNotFound = "debate.challenge.problem.verdict-not-found";
    public const string ChallengeProblemNotAnnounced = "debate.challenge.problem.not-announced";
    public const string ChallengeProblemUnitNotStarted = "debate.challenge.problem.unit-not-started";
    public const string ChallengeProblemAlreadyChallenged = "debate.challenge.problem.already-challenged";
    public const string ChallengeProblemRequirementsMissing = "debate.challenge.problem.requirements-missing";
    public const string ChallengeProblemNotInherited = "debate.challenge.problem.not-inherited";
    public const string ChallengeProblemJudgeOnExcluded = "debate.challenge.problem.judge-on-excluded";
    public const string ChallengeProblemJuryNeeded = "debate.challenge.problem.jury-needed";
    public const string ChallengeNoteSameJudge = "debate.challenge.note.same-judge";
    public const string ChallengeNoteNobodyCanJudge = "debate.challenge.note.nobody-can-judge";

    /// <summary>A resume refused because the log does not replay: the technical detail (in English, with no path) is the second argument.</summary>
    public const string CannotResumeDetail = "debate.cannot-resume-detail";

    /// <summary>Every code of this class: the desktop has a text for each of them in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        JudgmentTooEarly, NotWaitingRetry, ClosedEvidence, ClosedTests, NoTestCommit, NoTrustProfile, NoIsolatedStep, NotWaitingDecision,
        AlreadyClosed, DecisionNotApproved, CannotJudgeNow, ProposalOutdated, CannotOpen, OpeningNotApproved, OpeningInconsistent, ResumeNotApproved,
        AlreadyRunning, NotFound, AlreadyFinished, CannotResume, ProjectBusy, NoProvider, AwaitJuryDisagrees, AwaitNeedsPerson,
        AwaitVerdictsUnusable, AwaitNoVerdict, TurnNoBudget, TurnInvalidAnswer, TurnLimitResets, TurnLimitUnknown, TurnOverloaded, TurnEndedBadly,
        ProblemParticipantMissing, ProblemParticipantRole, ProblemNoProvider, ProblemJudgeMissing, ProblemJudgeRole, ProblemOrchestratorRequirements,
        ProblemNoArrangement, ProblemSessionRefused, ProblemNoRoundFits, ProblemRunUnnamed, ProblemRunNotInLog, ProblemNoCandidateCommit,
        ProblemAuditRequirements, ProblemUnknownSubject, ProblemVerdictUnnamed, ProblemVerdictNotStored, ProblemDebateMissing, ProblemAuditMissing,
        NoteUncommitted, NoteMaxRunsOneRound, NoteMaxRuns, NoteTokensPerTurn, NoteTokenLimit, NoteEstimated, ProblemLogUnverified, ProblemNotOpened,
        ProblemHeaderMismatch, ProblemQuestionUnreadable, ProblemRequirementsMissing, ProblemContributionUnreadable, ProblemEntryUnreadable,
        ProblemReplay, ProblemFinalFigures, ProblemStopped, ProblemNothingToJudge, ProblemCopyNotDeleted, ChallengeNotRevision, ChallengeCannotOpen,
        ChallengeProposalOutdated, ChallengeVerdictNotStored, ChallengeNothingToAccept, ChallengeNotAwaitingAcceptance,
        ChallengeAcceptanceNotAnswered, ChallengeProblemLogBroken, ChallengeProblemReasonsMissing, ChallengeProblemVerdictNotFound,
        ChallengeProblemNotAnnounced, ChallengeProblemUnitNotStarted, ChallengeProblemAlreadyChallenged, ChallengeProblemRequirementsMissing,
        ChallengeProblemNotInherited, ChallengeProblemJudgeOnExcluded, ChallengeProblemJuryNeeded, ChallengeNoteSameJudge,
        ChallengeNoteNobodyCanJudge, CannotResumeDetail,
    ];

    /// <summary>A refusal: the English <paramref name="message"/> for logs and tests, and the coded text for the person.</summary>
    internal static UserTextException Refusal(string code, string message, params object[] arguments) =>
        new(UserText.Of(code, arguments), message);
}
