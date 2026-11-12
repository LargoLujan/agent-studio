using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// The names of what a verdict says (docs/03 §6 and docs/15 D-42), shared by the card of a run, of a debate and of an
/// audit: decisions with their tone, confidence, the status of each requirement, findings and their severity, the rules of
/// the validator, where a challenge stands and the disposition of a review. Every text comes from
/// <c>Resources/DebateStrings.resx</c> in the language in use (docs/17 D-47, ADR-0054). The run monitor uses some of them, so
/// the signatures do not change.
/// </summary>
public static class VerdictTexts
{
    /// <summary>The text of <c>prefix + value</c>; a value the resources do not know (a new one) is shown as its name, never as <c>[key]</c>.</summary>
    private static string Named(string prefix, Enum value) =>
        Texts.Current.Has(prefix + value) ? Texts.Get(prefix + value) : value.ToString();

    public static string Decision(VerdictDecision decision) => Named("Verdict_Decision_", decision);

    public static Tone ToneOf(VerdictDecision decision) => decision switch
    {
        VerdictDecision.Approved => Tone.Success,
        VerdictDecision.ChangesRequested or VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved => Tone.Warning,
        VerdictDecision.Rejected => Tone.Error,
        _ => Tone.Neutral,
    };

    public static string Confidence(ConfidenceLevel level) => Named("Verdict_Confidence_", level);

    public static string Status(AssessmentStatus status) => Named("Verdict_Status_", status);

    /// <summary>The glyph of a requirement's status; the word goes next to it, so the glyph is never the only cue.</summary>
    public static string Glyph(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Satisfied => "✔",
        AssessmentStatus.NotSatisfied => "✖",
        _ => "?",
    };

    public static Tone ToneOf(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Satisfied => Tone.Success,
        AssessmentStatus.NotSatisfied => Tone.Error,
        _ => Tone.Warning,
    };

    public static string Category(FindingCategory category) => Named("Verdict_Category_", category);

    public static string Severity(Severity severity) => Named("Verdict_Severity_", severity);

    public static Tone ToneOf(Severity severity) => severity switch
    {
        Domain.Judging.Severity.Low => Tone.Neutral,
        Domain.Judging.Severity.Medium => Tone.Warning,
        _ => Tone.Error,
    };

    /// <summary>What a rule of the validator found, in the language in use (the validator's own message, in English, goes with it as detail).</summary>
    public static string Violation(VerdictViolationCode code) => Named("Verdict_Violation_", code);

    public static string Challenge(ChallengeStatus status) => Named("Verdict_Challenge_", status);

    public static Tone ToneOf(ChallengeStatus status) => status switch
    {
        ChallengeStatus.None => Tone.Neutral,
        ChallengeStatus.Confirmed or ChallengeStatus.Accepted or ChallengeStatus.DecidedByUser => Tone.Success,
        ChallengeStatus.Revoked => Tone.Error,
        _ => Tone.Warning,
    };

    public static string Disposition(ReviewDisposition disposition) => Named("Verdict_Disposition_", disposition);

    public static Tone ToneOf(ReviewDisposition disposition) => disposition switch
    {
        ReviewDisposition.Approved => Tone.Success,
        ReviewDisposition.Rejected => Tone.Error,
        _ => Tone.Warning,
    };
}
