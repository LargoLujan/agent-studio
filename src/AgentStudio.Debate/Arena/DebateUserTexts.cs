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

    /// <summary>Every code of this class: the desktop has a text for each of them in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        JudgmentTooEarly, NotWaitingRetry, ClosedEvidence, ClosedTests, NoTestCommit, NoTrustProfile, NoIsolatedStep, NotWaitingDecision,
        AlreadyClosed, DecisionNotApproved, CannotJudgeNow, ProposalOutdated, OpeningNotApproved, OpeningInconsistent, ResumeNotApproved,
        AlreadyRunning, NotFound, AlreadyFinished, CannotResume, ProjectBusy, NoProvider, AwaitJuryDisagrees, AwaitNeedsPerson,
        AwaitVerdictsUnusable, AwaitNoVerdict, TurnNoBudget, TurnInvalidAnswer, TurnLimitResets, TurnLimitUnknown, TurnOverloaded, TurnEndedBadly,
    ];

    /// <summary>A refusal: the English <paramref name="message"/> for logs and tests, and the coded text for the person.</summary>
    internal static UserTextException Refusal(string code, string message, params object[] arguments) =>
        new(UserText.Of(code, arguments), message);
}
