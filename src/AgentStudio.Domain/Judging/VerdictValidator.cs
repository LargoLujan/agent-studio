using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Domain.Judging;

public enum VerdictViolationCode
{
    RequirementSetMismatch,
    DecisionLogMismatch,
    RequirementNotAssessed,
    UnknownRequirementAssessed,
    DuplicateAssessment,
    EvidenceNotInDossier,
    EvidenceCitedButNotExamined,
    SatisfiedWithoutEvidence,
    ApprovedWithUnmetMandatoryRequirement,
    ApprovedOnAgentClaimsOnly,
    ApprovedWithFailingTests,
    ApprovedWithoutTestRun,
    ApprovedWithCriticalFinding,
    MissingConfidenceJustification,
    HumanInterventionNotFlagged,
    MissingCorrectiveActions,
    JudgeNotIndependent,
    SatisfiedByFailingTestRun,
    ApprovedWithoutEvidenceForCriterion,
    MalformedVerdict,
    ApprovedPendingHumanIntervention,

    // Appended: verdicts and the log store these codes by name, and nothing may shift the values before them.

    /// <summary>The system found the material itself untrustworthy (<see cref="JudgeDossier.IntegrityProblems"/>): no approval.</summary>
    ApprovedWithIntegrityProblems,

    /// <summary>No passing, system-captured test run verified the commit under review (<see cref="JudgeDossier.CandidateCommit"/>).</summary>
    ApprovedWithoutTestRunForCommit,

    // Phase 5 (debates, docs/15 D-25), appended.

    /// <summary>A free debate or an architecture review is approved, or changes are asked for, without choosing a proposal.</summary>
    DebateResolutionMissing,

    /// <summary>The proposal the verdict chose, or one it compares, is not a proposal of this debate.</summary>
    DebateResolutionUnknown,
}

public sealed record VerdictViolation(VerdictViolationCode Code, string Message);

public sealed record JudgePolicy(bool RequireVendorIndependence = true)
{
    public static readonly JudgePolicy Default = new();
}

/// <summary>
/// Checks that a verdict is internally consistent and backed by the dossier. A verdict with violations is not
/// accepted: Agent Studio asks the judge to redo it or escalates to the user. This is what turns the judge's
/// opinion into a verifiable verdict, and it is where "a majority of agents cannot replace technical tests"
/// is enforced: approval is impossible while the latest system-captured test run fails.
/// </summary>
public static class VerdictValidator
{
    public static IReadOnlyList<VerdictViolation> Validate(Verdict verdict, JudgeDossier dossier, JudgePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(dossier);
        policy ??= JudgePolicy.Default;

        var violations = new List<VerdictViolation>();
        void Add(VerdictViolationCode code, string message) => violations.Add(new VerdictViolation(code, message));

        // 0. The verdict comes from the judge's JSON, so any member can be null. Report it once and carry on with
        // empty collections instead of throwing.
        var malformed = FindMalformedMembers(verdict);
        if (malformed.Count > 0)
        {
            Add(VerdictViolationCode.MalformedVerdict, $"The verdict is malformed: {string.Join(", ", malformed)}.");
        }

        var allAssessments = NormalizedAssessments(verdict);
        var findings = (verdict.Findings ?? []).Where(f => f is not null).Select(f => f.Evidence is null ? f with { Evidence = [] } : f).ToList();
        var examinedIds = verdict.EvidenceExamined ?? [];

        // 1. The verdict must be about exactly the material in the dossier.
        if (verdict.RequirementSetHash != dossier.Requirements.ContentHash)
        {
            Add(VerdictViolationCode.RequirementSetMismatch, "The verdict refers to a different requirement set.");
        }

        if (verdict.DecisionLogHeadHash != dossier.DecisionLogHeadHash)
        {
            Add(VerdictViolationCode.DecisionLogMismatch, "The verdict refers to a different state of the decision log.");
        }

        // 2. Every requirement assessed exactly once, and only known requirements.
        var assessed = allAssessments.GroupBy(a => a.Requirement).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (requirement, assessments) in assessed)
        {
            if (dossier.Requirements.Find(requirement) is null)
            {
                Add(VerdictViolationCode.UnknownRequirementAssessed, $"Requirement '{requirement}' is not in the frozen set.");
            }

            if (assessments.Count > 1)
            {
                Add(VerdictViolationCode.DuplicateAssessment, $"Requirement '{requirement}' is assessed {assessments.Count} times.");
            }
        }

        foreach (var requirement in dossier.Requirements.Requirements.Where(r => !assessed.ContainsKey(r.Id)))
        {
            Add(VerdictViolationCode.RequirementNotAssessed, $"Requirement '{requirement.Id}' was not assessed.");
        }

        // 3. No phantom evidence: everything cited exists in the dossier and was declared as examined.
        var evidenceById = dossier.Evidence.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var examined = examinedIds.ToHashSet();
        var cited = allAssessments.SelectMany(a => a.Evidence)
            .Concat(findings.SelectMany(f => f.Evidence))
            .Concat(examinedIds)
            .Distinct();
        foreach (var id in cited.Where(id => !evidenceById.ContainsKey(id)))
        {
            Add(VerdictViolationCode.EvidenceNotInDossier, $"Evidence '{id}' does not exist in the dossier.");
        }

        foreach (var assessment in allAssessments)
        {
            foreach (var id in assessment.Evidence.Where(id => !examined.Contains(id)))
            {
                Add(VerdictViolationCode.EvidenceCitedButNotExamined, $"Evidence '{id}' is cited for '{assessment.Requirement}' but not listed as examined.");
            }

            if (assessment.Status == AssessmentStatus.Satisfied && assessment.Evidence.Count == 0)
            {
                Add(VerdictViolationCode.SatisfiedWithoutEvidence, $"Requirement '{assessment.Requirement}' is marked satisfied without evidence.");
            }

            if (assessment.Status == AssessmentStatus.Satisfied)
            {
                var failing = assessment.Evidence
                    .Where(evidenceById.ContainsKey)
                    .Select(id => evidenceById[id])
                    .Where(e => e is { Kind: EvidenceKind.TestRun, TestRun.Succeeded: false })
                    .Select(e => e.Id)
                    .ToList();
                if (failing.Count > 0)
                {
                    Add(VerdictViolationCode.SatisfiedByFailingTestRun, $"Requirement '{assessment.Requirement}' is marked satisfied citing failing test run(s): {string.Join(", ", failing)}.");
                }
            }
        }

        // 4. Approval rules.
        if (verdict.Decision == VerdictDecision.Approved)
        {
            CheckApproval(verdict, findings, dossier, assessed, evidenceById, Add);
        }

        // 5. Form of the verdict.
        if (verdict.Confidence is not null && string.IsNullOrWhiteSpace(verdict.Confidence.Justification))
        {
            Add(VerdictViolationCode.MissingConfidenceJustification, "The confidence level must be justified.");
        }

        if (verdict.Decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved && !verdict.RequiresHumanIntervention)
        {
            Add(VerdictViolationCode.HumanInterventionNotFlagged, $"A '{verdict.Decision}' verdict must request human intervention.");
        }

        if (verdict.Decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected && (verdict.CorrectiveActions ?? []).Count == 0)
        {
            Add(VerdictViolationCode.MissingCorrectiveActions, $"A '{verdict.Decision}' verdict must list corrective actions.");
        }

        // 6. Independence from the vendors that produced the work.
        if (policy.RequireVendorIndependence && verdict.Judge?.Model is not null && dossier.ExcludedVendors.Contains(verdict.Judge.Model.Vendor))
        {
            Add(VerdictViolationCode.JudgeNotIndependent, $"The judge runs on vendor '{verdict.Judge.Model.Vendor}', which also produced the work under review.");
        }

        // 7. The resolution of a debate (docs/15 D-25).
        CheckResolution(verdict, dossier, Add);

        return violations;
    }

    /// <summary>
    /// A free debate or an architecture review that is approved, or asked to change, says which proposal; and every
    /// proposal a verdict names (chosen or compared) is one of the debate. The review of a run has no proposals.
    /// </summary>
    private static void CheckResolution(Verdict verdict, JudgeDossier dossier, Action<VerdictViolationCode, string> add)
    {
        if (dossier.Debate is { Mode: DebateMode.Free or DebateMode.ArchitectureReview } choosing
            && verdict.Decision is VerdictDecision.Approved or VerdictDecision.ChangesRequested
            && verdict.Resolution?.ChosenProposal is null)
        {
            add(VerdictViolationCode.DebateResolutionMissing, $"A '{verdict.Decision}' verdict of a {choosing.Mode} debate must choose one of its proposals.");
        }

        if (verdict.Resolution is not { } resolution)
        {
            return;
        }

        var proposals = dossier.Debate?.Proposals ?? [];
        var named = new List<string>();
        if (resolution.ChosenProposal is { } chosen)
        {
            named.Add(chosen);
        }

        named.AddRange((resolution.Comparison ?? []).Where(c => c?.Proposal is not null).Select(c => c.Proposal));
        foreach (var unknown in named.Where(p => !proposals.Contains(p, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            add(
                VerdictViolationCode.DebateResolutionUnknown,
                dossier.Debate is null
                    ? $"The verdict names the proposal '{unknown}', but the dossier is not about a debate."
                    : $"The proposal '{unknown}' is not one of this debate ({string.Join(", ", proposals)}).");
        }
    }

    private static List<string> FindMalformedMembers(Verdict verdict)
    {
        var problems = new List<string>();
        void Check(bool isNull, string name)
        {
            if (isNull)
            {
                problems.Add(name);
            }
        }

        Check(verdict.Assessments is null, nameof(Verdict.Assessments));
        Check(verdict.EvidenceExamined is null, nameof(Verdict.EvidenceExamined));
        Check(verdict.PendingObjections is null, nameof(Verdict.PendingObjections));
        Check(verdict.Risks is null, nameof(Verdict.Risks));
        Check(verdict.Findings is null, nameof(Verdict.Findings));
        Check(verdict.CorrectiveActions is null, nameof(Verdict.CorrectiveActions));
        Check(verdict.Confidence is null, nameof(Verdict.Confidence));
        Check(verdict.Judge?.Model is null, nameof(Verdict.Judge));
        Check(verdict.Assessments?.Any(a => a is null || a.Evidence is null) == true, "Assessments[].Evidence");
        Check(verdict.Findings?.Any(f => f is null || f.Evidence is null) == true, "Findings[].Evidence");

        // An explanation-free verdict cannot be reviewed by a human, so missing texts count as malformed too.
        Check(string.IsNullOrWhiteSpace(verdict.Summary), nameof(Verdict.Summary));
        Check(verdict.Assessments?.Any(a => a is not null && a.Rationale is null) == true, "Assessments[].Rationale");
        Check(verdict.Findings?.Any(f => f is not null && f.Description is null) == true, "Findings[].Description");
        Check(verdict.Findings?.Any(f => f is not null && f.DecisionRefs is null) == true, "Findings[].DecisionRefs");

        // A resolution is optional (null outside the debates), but one that is there must be whole.
        Check(verdict.Resolution is { Comparison: null }, "Resolution.Comparison");
        Check(verdict.Resolution?.Comparison?.Any(c => c is null || c.Proposal is null) == true, "Resolution.Comparison[]");
        return problems;
    }

    private static List<RequirementAssessment> NormalizedAssessments(Verdict verdict) =>
        (verdict.Assessments ?? []).Where(a => a is not null).Select(a => a.Evidence is null ? a with { Evidence = [] } : a).ToList();

    private static void CheckApproval(
        Verdict verdict,
        IReadOnlyList<Finding> findings,
        JudgeDossier dossier,
        Dictionary<RequirementId, List<RequirementAssessment>> assessed,
        Dictionary<EvidenceId, EvidenceRecord> evidenceById,
        Action<VerdictViolationCode, string> add)
    {
        // Nothing the material shows can support an approval once the system found the material itself untrustworthy.
        if (dossier.IntegrityProblems.Count > 0)
        {
            add(
                VerdictViolationCode.ApprovedWithIntegrityProblems,
                $"The system found {dossier.IntegrityProblems.Count} integrity problem(s) in the dossier (systemChecks): it cannot be approved.");
        }

        var candidate = dossier.CandidateCommit;
        foreach (var requirement in dossier.Requirements.Mandatory)
        {
            if (!assessed.TryGetValue(requirement.Id, out var assessments))
            {
                continue; // Already reported as not assessed.
            }

            var assessment = assessments[0];
            if (assessment.Status != AssessmentStatus.Satisfied)
            {
                add(VerdictViolationCode.ApprovedWithUnmetMandatoryRequirement, $"Mandatory requirement '{requirement.Id}' is {assessment.Status}.");
                continue;
            }

            var supporting = assessment.Evidence.Where(evidenceById.ContainsKey).Select(id => evidenceById[id]).ToList();
            if (supporting.Count > 0 && !supporting.Any(e => e.IsObjective) && !IsJudgesOwnAssessment(requirement, dossier))
            {
                add(VerdictViolationCode.ApprovedOnAgentClaimsOnly, $"Mandatory requirement '{requirement.Id}' is supported only by agent statements.");
            }

            foreach (var criterion in requirement.Criteria)
            {
                if (!CriterionIsBacked(criterion.Method, supporting, candidate))
                {
                    add(
                        VerdictViolationCode.ApprovedWithoutEvidenceForCriterion,
                        $"Mandatory requirement '{requirement.Id}', criterion '{criterion.Id}' ({criterion.Method}): the cited evidence does not fit the method"
                        + (candidate is null ? "." : $"; builds and test runs count only if they verified the commit under review ({candidate})."));
                }
            }
        }

        var testRuns = dossier.Evidence
            .Where(e => e is { Kind: EvidenceKind.TestRun, Origin: EvidenceOrigin.CapturedBySystem, TestRun: not null })
            .OrderBy(e => e.CapturedAt)
            .ToList();

        if (candidate is not null)
        {
            // Tests of another commit say nothing about this one, however recent or green they are.
            var ofCandidate = testRuns.Where(e => OfCommit(e, candidate)).ToList();
            if (ofCandidate.Count == 0)
            {
                add(VerdictViolationCode.ApprovedWithoutTestRunForCommit, $"No system-captured test run verified the commit under review ({candidate}); runs of other commits do not count.");
            }
            else
            {
                CheckLatestRuns(ofCandidate, $" of the commit under review ({candidate})", add);
            }
        }
        else
        {
            var needsTests = dossier.Requirements.Mandatory
                .SelectMany(r => r.Criteria)
                .Any(c => c.Method == VerificationMethod.AutomatedTest);

            if (testRuns.Count == 0)
            {
                if (needsTests)
                {
                    add(VerdictViolationCode.ApprovedWithoutTestRun, "Mandatory criteria require automated tests but no system-captured test run exists.");
                }
            }
            else
            {
                CheckLatestRuns(testRuns, string.Empty, add);
            }
        }

        if (verdict.RequiresHumanIntervention)
        {
            add(VerdictViolationCode.ApprovedPendingHumanIntervention, "An approval cannot also request human intervention.");
        }

        if (findings.Any(f => f.Severity == Severity.Critical))
        {
            add(VerdictViolationCode.ApprovedWithCriticalFinding, "A verdict with critical findings cannot approve.");
        }
    }

    /// <summary>
    /// In a debate that chooses (docs/15 D-25) there is no code to test: a requirement whose every criterion is the judge's
    /// own assessment is assessed on the contributions, which are its very material, so citing them is not "agent claims
    /// only". Everywhere else (the review of a run, the debates that review work, their challenges) the rule of Phase 3 holds.
    /// </summary>
    private static bool IsJudgesOwnAssessment(Requirement requirement, JudgeDossier dossier) =>
        dossier.Debate is { Choice: true }
        && requirement.Criteria.Count > 0
        && requirement.Criteria.All(c => c.Method == VerificationMethod.JudgeAssessment);

    /// <summary>Runs captured at the same instant are all "the latest": none of them may have failed.</summary>
    private static void CheckLatestRuns(List<EvidenceRecord> runs, string ofWhat, Action<VerdictViolationCode, string> add)
    {
        var latestAt = runs[^1].CapturedAt;
        var failing = runs.Where(e => e.CapturedAt == latestAt && !e.TestRun!.Succeeded).Select(e => e.Id).ToList();
        if (failing.Count > 0)
        {
            add(VerdictViolationCode.ApprovedWithFailingTests, $"The latest system-captured test run(s){ofWhat} ({string.Join(", ", failing.Select(id => $"'{id}'"))}) did not succeed.");
        }
    }

    /// <summary>
    /// Whether the cited evidence fits the method of a criterion. With a commit under review, the builds, commands and
    /// test runs the system captured count only if they verified that commit.
    /// </summary>
    private static bool CriterionIsBacked(VerificationMethod method, IReadOnlyList<EvidenceRecord> cited, string? candidate) => method switch
    {
        VerificationMethod.AutomatedTest => cited.Any(e => e is { Kind: EvidenceKind.TestRun, IsObjective: true, TestRun.Succeeded: true } && OfCandidate(e, candidate)),
        VerificationMethod.CommandCheck => cited.Any(e => e.IsObjective && e.Kind is EvidenceKind.CommandOutput or EvidenceKind.BuildLog or EvidenceKind.TestRun && OfCandidate(e, candidate)),
        VerificationMethod.HumanReview => cited.Any(e => e.Origin == EvidenceOrigin.ProvidedByHuman),
        _ => true,
    };

    private static bool OfCandidate(EvidenceRecord evidence, string? candidate) => candidate is null || OfCommit(evidence, candidate);

    private static bool OfCommit(EvidenceRecord evidence, string commit) => string.Equals(evidence.SubjectCommit, commit, StringComparison.Ordinal);
}
