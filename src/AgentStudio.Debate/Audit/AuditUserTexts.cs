using AgentStudio.Application.Localization;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// The texts of the audit the person reads and that are not anchored (docs/17 D-47, ADR-0054 addendum of 2026-11-12): a code and its
/// arguments, painted by the desktop in the language of the person (<c>Text_</c> + the code, in <c>AuditStrings</c>). The sentences
/// the audit records (<c>ProjectAuditFinished.Summary</c>, the escalation of the system, the approval text, the dossier and the
/// request) stay as they are (D-48): these codes travel next to them.
/// </summary>
public static class AuditUserTexts
{
    // ---- why the preparation of an audit cannot judge (ProjectAuditProposal.ProblemTexts) ----
    public const string ProblemLogBroken = "audit.problem.log-broken";
    public const string ProblemRequirementsMissing = "audit.problem.requirements-missing";
    public const string ProblemRepositoryUnreadable = "audit.problem.repository-unreadable";
    public const string ProblemNoTrustProfile = "audit.problem.no-trust-profile";
    public const string ProblemProfileUnusable = "audit.problem.profile-unusable";
    public const string ProblemJudgeMissing = "audit.problem.judge-missing";
    public const string ProblemJudgeWrongRole = "audit.problem.judge-wrong-role";
    public const string ProblemJudgeInvalid = "audit.problem.judge-invalid";
    public const string ProblemJudgeProducer = "audit.problem.judge-producer";

    // ---- why an audit ended without a judge's verdict (AuditReport.EscalationText) ----
    public const string EscalationLogBroken = "audit.escalation.log-broken";
    public const string EscalationCannotLaunch = "audit.escalation.cannot-launch";
    public const string EscalationNotApproved = "audit.escalation.not-approved";
    public const string EscalationCopyFailed = "audit.escalation.copy-failed";
    public const string EscalationLogBrokeMidway = "audit.escalation.log-broke-midway";
    public const string EscalationJudgeExcluded = "audit.escalation.judge-excluded";
    public const string EscalationJudgeLimit = "audit.escalation.judge-limit";
    public const string EscalationJudgeFailed = "audit.escalation.judge-failed";
    public const string EscalationJudgeUnreadable = "audit.escalation.judge-unreadable";

    // ---- refusals ----
    public const string ProposalOutdated = "audit.proposal-outdated";
    public const string ProjectBusy = "audit.project-busy";

    /// <summary>Every code of this class: the desktop has a text for each of them in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ProblemLogBroken, ProblemRequirementsMissing, ProblemRepositoryUnreadable, ProblemNoTrustProfile, ProblemProfileUnusable,
        ProblemJudgeMissing, ProblemJudgeWrongRole, ProblemJudgeInvalid, ProblemJudgeProducer, EscalationLogBroken,
        EscalationCannotLaunch, EscalationNotApproved, EscalationCopyFailed, EscalationLogBrokeMidway, EscalationJudgeExcluded,
        EscalationJudgeLimit, EscalationJudgeFailed, EscalationJudgeUnreadable, ProposalOutdated, ProjectBusy,
    ];
}
