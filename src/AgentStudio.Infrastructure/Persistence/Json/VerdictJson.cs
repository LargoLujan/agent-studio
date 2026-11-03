using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>
/// A verdict exactly as the judge gave it, in <c>verdicts.payload_json</c>. A verdict comes from the judge's JSON, so
/// any member, and any item of a list, may be null (<see cref="VerdictValidator"/> reports it as malformed); an
/// invalid verdict is kept as it is, never repaired, so every member here can hold a null and gives it back.
/// </summary>
/// <param name="Resolution">
/// The resolution of the judge of a debate (docs/15 D-25), last and only when there is one: a null member is left out, so
/// a verdict outside the debates is written as the same text as before Phase 5, and one stored then reads with none.
/// </param>
internal sealed record VerdictJson(
    VerdictDecision Decision,
    string? Summary,
    IReadOnlyList<AssessmentJson?>? Assessments,
    IReadOnlyList<string?>? EvidenceExamined,
    IReadOnlyList<string?>? PendingObjections,
    IReadOnlyList<string?>? Risks,
    IReadOnlyList<FindingJson?>? Findings,
    ConfidenceJson? Confidence,
    IReadOnlyList<string?>? CorrectiveActions,
    bool RequiresHumanIntervention,
    JudgeJson? Judge,
    string? RequirementSetHash,
    string? DecisionLogHeadHash,
    DateTimeOffset IssuedAt,
    ResolutionJson? Resolution = null)
{
    public static VerdictJson From(Verdict verdict) => new(
        verdict.Decision,
        verdict.Summary,
        verdict.Assessments?.Select(a => a is null ? null : AssessmentJson.From(a)).ToList(),
        verdict.EvidenceExamined?.Select(e => e.Value).ToList(),
        verdict.PendingObjections?.ToList(),
        verdict.Risks?.ToList(),
        verdict.Findings?.Select(f => f is null ? null : FindingJson.From(f)).ToList(),
        verdict.Confidence is null ? null : new ConfidenceJson(verdict.Confidence.Level, verdict.Confidence.Justification),
        verdict.CorrectiveActions?.ToList(),
        verdict.RequiresHumanIntervention,
        verdict.Judge is null ? null : new JudgeJson(verdict.Judge.Agent.Value, verdict.Judge.Model is null ? null : ModelJson.From(verdict.Judge.Model)),
        verdict.RequirementSetHash,
        verdict.DecisionLogHeadHash,
        verdict.IssuedAt,
        verdict.Resolution is null ? null : ResolutionJson.From(verdict.Resolution));

    public Verdict ToDomain() => new(
        Decision,
        Summary!,
        Assessments?.Select(a => a?.ToDomain()!).ToList()!,
        EvidenceExamined?.Select(Ids.Evidence).ToList()!,
        PendingObjections?.ToList()!,
        Risks?.ToList()!,
        Findings?.Select(f => f?.ToDomain()!).ToList()!,
        Confidence is null ? null! : new Confidence(Confidence.Level, Confidence.Justification!),
        CorrectiveActions?.ToList()!,
        RequiresHumanIntervention,
        Judge is null ? null! : new JudgeIdentity(Ids.Agent(Judge.Agent), Judge.Model?.ToDomain()!),
        RequirementSetHash!,
        DecisionLogHeadHash!,
        IssuedAt)
    {
        Resolution = Resolution?.ToDomain(),
    };
}

/// <summary>
/// The resolution of the judge of a debate as it gave it (docs/15 D-25): the chosen proposal, null for «ninguna», and the
/// comparison, whose list and items may be missing (the validator reports them as malformed), kept as they came.
/// </summary>
internal sealed record ResolutionJson(string? ChosenProposal, IReadOnlyList<ProposalComparisonJson?>? Comparison)
{
    public static ResolutionJson From(DebateResolution resolution) =>
        new(resolution.ChosenProposal, resolution.Comparison?.Select(c => c is null ? null : new ProposalComparisonJson(c.Proposal, c.Strengths, c.Weaknesses)).ToList());

    public DebateResolution ToDomain() =>
        new(ChosenProposal, Comparison?.Select(c => c is null ? null! : new ProposalComparison(c.Proposal!, c.Strengths!, c.Weaknesses!)).ToList()!);
}

internal sealed record ProposalComparisonJson(string? Proposal, string? Strengths, string? Weaknesses);

internal sealed record AssessmentJson(string? Requirement, AssessmentStatus Status, IReadOnlyList<string?>? Evidence, string? Rationale)
{
    public static AssessmentJson From(RequirementAssessment assessment) =>
        new(assessment.Requirement.Value, assessment.Status, assessment.Evidence?.Select(e => e.Value).ToList(), assessment.Rationale);

    public RequirementAssessment ToDomain() =>
        new(Ids.Requirement(Requirement), Status, Evidence?.Select(Ids.Evidence).ToList()!, Rationale!);
}

internal sealed record FindingJson(FindingCategory Category, Severity Severity, string? Description, IReadOnlyList<string?>? Evidence, IReadOnlyList<long>? DecisionRefs)
{
    public static FindingJson From(Finding finding) =>
        new(finding.Category, finding.Severity, finding.Description, finding.Evidence?.Select(e => e.Value).ToList(), finding.DecisionRefs?.ToList());

    public Finding ToDomain() =>
        new(Category, Severity, Description!, Evidence?.Select(Ids.Evidence).ToList()!, DecisionRefs?.ToList()!);
}

internal sealed record ConfidenceJson(ConfidenceLevel Level, string? Justification);

internal sealed record JudgeJson(string? Agent, ModelJson? Model);

/// <summary>A violation found by <see cref="VerdictValidator"/>, as kept in <c>verdicts.validation_json</c>.</summary>
internal sealed record ViolationJson(VerdictViolationCode Code, string Message)
{
    public static ViolationJson From(VerdictViolation violation) => new(violation.Code, violation.Message);

    public VerdictViolation ToDomain() => new(Code, Message);
}

/// <summary>Ids written by the judge may be missing (a default struct, with a null value); they are read back the same way.</summary>
internal static class Ids
{
    public static EvidenceId Evidence(string? value) => value is null ? default : new EvidenceId(value);

    public static RequirementId Requirement(string? value) => value is null ? default : new RequirementId(value);

    public static AgentId Agent(string? value) => value is null ? default : new AgentId(value);
}
