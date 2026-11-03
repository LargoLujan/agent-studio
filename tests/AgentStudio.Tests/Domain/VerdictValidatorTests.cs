using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class VerdictValidatorTests
{
    private static IReadOnlyList<VerdictViolationCode> Codes(Verdict verdict, JudgeDossier dossier, JudgePolicy? policy = null) =>
        VerdictValidator.Validate(verdict, dossier, policy).Select(v => v.Code).ToList();

    private static void AssertOnly(VerdictViolationCode expected, Verdict verdict, JudgeDossier dossier, JudgePolicy? policy = null) =>
        Assert.Equal([expected], Codes(verdict, dossier, policy));

    private static RequirementAssessment Assess(string req, AssessmentStatus status, params string[] evidence) =>
        new(Rid(req), status, evidence.Select(Eid).ToList(), "because");

    private static Verdict CitingOnly(JudgeDossier dossier, string evidenceId) =>
        ValidVerdict(dossier) with
        {
            Assessments = dossier.Requirements.Requirements.Select(r => Assess(r.Id.Value, AssessmentStatus.Satisfied, evidenceId)).ToList(),
        };

    [Fact]
    public void A_valid_approval_has_no_violations()
    {
        var dossier = Dossier();

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void RequirementSetMismatch()
    {
        var dossier = Dossier();
        AssertOnly(VerdictViolationCode.RequirementSetMismatch, ValidVerdict(dossier) with { RequirementSetHash = HashA }, dossier);
    }

    [Fact]
    public void DecisionLogMismatch()
    {
        var dossier = Dossier();
        AssertOnly(VerdictViolationCode.DecisionLogMismatch, ValidVerdict(dossier) with { DecisionLogHeadHash = HashA }, dossier);
    }

    [Fact]
    public void RequirementNotAssessed()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier);
        verdict = verdict with { Assessments = verdict.Assessments.Where(a => a.Requirement != Rid("R2")).ToList() };

        AssertOnly(VerdictViolationCode.RequirementNotAssessed, verdict, dossier);
    }

    [Fact]
    public void UnknownRequirementAssessed()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier);
        verdict = verdict with { Assessments = [.. verdict.Assessments, Assess("R99", AssessmentStatus.Satisfied, "E1")] };

        AssertOnly(VerdictViolationCode.UnknownRequirementAssessed, verdict, dossier);
    }

    [Fact]
    public void DuplicateAssessment()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier);
        verdict = verdict with { Assessments = [.. verdict.Assessments, Assess("R2", AssessmentStatus.Satisfied, "E1")] };

        AssertOnly(VerdictViolationCode.DuplicateAssessment, verdict, dossier);
    }

    [Fact]
    public void EvidenceNotInDossier()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier);
        verdict = verdict with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E1", "GHOST"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
            EvidenceExamined = [Eid("E1"), Eid("E2"), Eid("GHOST")],
        };

        Assert.Contains(VerdictViolationCode.EvidenceNotInDossier, Codes(verdict, dossier));
    }

    [Fact]
    public void EvidenceNotInDossier_also_applies_to_findings()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Findings = [new Finding(FindingCategory.Other, Severity.Low, "x", [Eid("GHOST")], [])],
        };

        Assert.Contains(VerdictViolationCode.EvidenceNotInDossier, Codes(verdict, dossier));
    }

    [Fact]
    public void EvidenceCitedButNotExamined()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { EvidenceExamined = [Eid("E2")] };

        // E1 is cited by both assessments, so it is reported once per assessment.
        Assert.Equal([VerdictViolationCode.EvidenceCitedButNotExamined], Codes(verdict, dossier).Distinct());
    }

    [Fact]
    public void SatisfiedWithoutEvidence()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E1"), Assess("R2", AssessmentStatus.Satisfied)],
        };

        AssertOnly(VerdictViolationCode.SatisfiedWithoutEvidence, verdict, dossier);
    }

    [Theory]
    [InlineData(AssessmentStatus.NotSatisfied)]
    [InlineData(AssessmentStatus.NotVerifiable)]
    public void ApprovedWithUnmetMandatoryRequirement(AssessmentStatus status)
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", status, "E1"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
        };

        AssertOnly(VerdictViolationCode.ApprovedWithUnmetMandatoryRequirement, verdict, dossier);
    }

    [Fact]
    public void An_unmet_desirable_requirement_does_not_block_approval()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E1"), Assess("R2", AssessmentStatus.NotSatisfied)],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Fact]
    public void An_unmet_mandatory_requirement_does_not_block_a_ChangesRequested_verdict()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier, VerdictDecision.ChangesRequested) with
        {
            Assessments = [Assess("R1", AssessmentStatus.NotSatisfied, "E1"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Fact]
    public void ApprovedOnAgentClaimsOnly_the_agent_saying_the_tests_pass_never_suffices()
    {
        var claim = OtherEvidence("CLAIM", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement);
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green), claim]);
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "CLAIM"), Assess("R2", AssessmentStatus.Satisfied, "E1")],
        };

        Assert.Contains(VerdictViolationCode.ApprovedOnAgentClaimsOnly, Codes(verdict, dossier));
    }

    [Fact]
    public void An_agent_reported_test_run_is_not_objective_evidence_either()
    {
        var reported = TestRunEvidence("REPORTED", Green, origin: EvidenceOrigin.ReportedByAgent);
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green), reported]);
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "REPORTED"), Assess("R2", AssessmentStatus.Satisfied, "E1")],
        };

        Assert.Contains(VerdictViolationCode.ApprovedOnAgentClaimsOnly, Codes(verdict, dossier));
    }

    [Fact]
    public void Agent_claims_next_to_objective_evidence_are_acceptable()
    {
        var claim = OtherEvidence("CLAIM", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement);
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green), claim]);
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "CLAIM", "E1"), Assess("R2", AssessmentStatus.Satisfied, "E1")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Fact]
    public void Agent_claims_are_enough_for_a_desirable_requirement_that_is_not_mandatory()
    {
        var claim = OtherEvidence("CLAIM", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement);
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green), claim]);
        var verdict = ValidVerdict(dossier) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E1"), Assess("R2", AssessmentStatus.Satisfied, "CLAIM")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Fact]
    public void ApprovedWithFailingTests()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Red), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman)]);

        // Citing the failing run also trips the newer evidence rules; this test is about the latest-run rule.
        Assert.Contains(VerdictViolationCode.ApprovedWithFailingTests, Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void A_run_that_executed_no_test_counts_as_failing()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", new TestRunSummary(0, 0, 0, 0)), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman)]);

        Assert.Contains(VerdictViolationCode.ApprovedWithFailingTests, Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void A_failure_followed_by_a_passing_system_run_does_not_block()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("Z9", Red, T0), // later id, earlier capture
            TestRunEvidence("A1", Green, T0.AddHours(1)),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman),
        ]);

        Assert.Empty(Codes(ApprovedCiting(dossier, ("R1", ["A1"]), ("R2", ["E2"])), dossier));
    }

    [Fact]
    public void A_passing_run_followed_by_a_failure_blocks_whatever_the_list_order()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("A1", Red, T0.AddHours(1)), // listed first but captured last
            TestRunEvidence("Z9", Green, T0),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman),
        ]);

        Assert.Contains(VerdictViolationCode.ApprovedWithFailingTests, Codes(CitingOnly(dossier, "E2"), dossier));
    }

    [Fact]
    public void A_failing_run_reported_by_the_agent_is_ignored_and_a_passing_one_reported_by_the_agent_does_not_count()
    {
        // Only system-captured runs count: the agent's claim of a failure neither blocks nor does its claim of success help.
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("E1", Green, T0),
            TestRunEvidence("A1", Red, T0.AddHours(1), EvidenceOrigin.ReportedByAgent),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman),
        ]);

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void ApprovedWithoutTestRun_when_a_mandatory_criterion_is_an_automated_test()
    {
        var dossier = Dossier(evidence: [OtherEvidence("E1", EvidenceOrigin.CapturedBySystem), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman)]);

        Assert.Contains(VerdictViolationCode.ApprovedWithoutTestRun, Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void A_test_run_reported_only_by_the_agent_does_not_replace_a_system_run()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green, origin: EvidenceOrigin.ReportedByAgent), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman)]);

        var codes = Codes(ValidVerdict(dossier), dossier);

        Assert.Contains(VerdictViolationCode.ApprovedWithoutTestRun, codes);
    }

    [Fact]
    public void No_test_run_is_needed_when_no_mandatory_criterion_is_an_automated_test()
    {
        var dossier = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.HumanReview)), Req("R2", RequirementPriority.Desirable, "y", Crit("c2", VerificationMethod.AutomatedTest))],
            evidence: [OtherEvidence("E1", EvidenceOrigin.ProvidedByHuman)]);

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void ApprovedWithCriticalFinding()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Findings = [new Finding(FindingCategory.SecurityIssue, Severity.Critical, "rce", [Eid("E1")], [])],
        };

        AssertOnly(VerdictViolationCode.ApprovedWithCriticalFinding, verdict, dossier);
    }

    [Fact]
    public void A_non_critical_finding_does_not_block_approval()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with
        {
            Findings = [new Finding(FindingCategory.Other, Severity.High, "meh", [], [])],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingConfidenceJustification(string justification)
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { Confidence = new Confidence(ConfidenceLevel.High, justification) };

        AssertOnly(VerdictViolationCode.MissingConfidenceJustification, verdict, dossier);
    }

    [Theory]
    [InlineData(VerdictDecision.EscalateToHuman)]
    [InlineData(VerdictDecision.Unresolved)]
    public void HumanInterventionNotFlagged(VerdictDecision decision)
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier, decision) with { RequiresHumanIntervention = false };

        AssertOnly(VerdictViolationCode.HumanInterventionNotFlagged, verdict, dossier);
        Assert.Empty(Codes(verdict with { RequiresHumanIntervention = true }, dossier));
    }

    [Theory]
    [InlineData(VerdictDecision.ChangesRequested)]
    [InlineData(VerdictDecision.Rejected)]
    public void MissingCorrectiveActions(VerdictDecision decision)
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier, decision) with { CorrectiveActions = [] };

        AssertOnly(VerdictViolationCode.MissingCorrectiveActions, verdict, dossier);
        Assert.Empty(Codes(verdict with { CorrectiveActions = ["do x"] }, dossier));
    }

    [Fact]
    public void JudgeNotIndependent_when_the_judge_vendor_is_excluded()
    {
        var dossier = Dossier(excludedVendors: [VendorId.OpenAI]);

        AssertOnly(VerdictViolationCode.JudgeNotIndependent, ValidVerdict(dossier), dossier);
    }

    [Fact]
    public void Vendor_independence_can_be_switched_off_by_policy()
    {
        var dossier = Dossier(excludedVendors: [VendorId.OpenAI]);

        Assert.Empty(Codes(ValidVerdict(dossier), dossier, new JudgePolicy(false)));
    }

    [Fact]
    public void The_default_policy_requires_independence()
    {
        Assert.True(JudgePolicy.Default.RequireVendorIndependence);
        Assert.True(new JudgePolicy().RequireVendorIndependence);
    }

    [Fact]
    public void A_non_approval_verdict_does_not_trigger_the_approval_rules()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Red), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman)]);

        var verdict = ValidVerdict(dossier, VerdictDecision.Rejected) with
        {
            Assessments = [Assess("R1", AssessmentStatus.NotSatisfied, "E1"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { RequirementSetHash = HashA, DecisionLogHeadHash = HashA, Confidence = new Confidence(ConfidenceLevel.Low, "") };

        var codes = Codes(verdict, dossier);

        Assert.Contains(VerdictViolationCode.RequirementSetMismatch, codes);
        Assert.Contains(VerdictViolationCode.DecisionLogMismatch, codes);
        Assert.Contains(VerdictViolationCode.MissingConfidenceJustification, codes);
    }

    // ---- evidence must fit the criterion ----

    [Fact]
    public void SatisfiedByFailingTestRun_whatever_the_decision()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Red), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote)]);
        var verdict = ValidVerdict(dossier, VerdictDecision.ChangesRequested) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E1"), Assess("R2", AssessmentStatus.NotSatisfied)],
        };

        AssertOnly(VerdictViolationCode.SatisfiedByFailingTestRun, verdict, dossier);
    }

    [Fact]
    public void SatisfiedByFailingTestRun_does_not_apply_to_unmet_assessments_citing_a_failing_run()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Red), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote)]);
        var verdict = ValidVerdict(dossier, VerdictDecision.ChangesRequested) with
        {
            Assessments = [Assess("R1", AssessmentStatus.NotSatisfied, "E1"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    private static Verdict ApprovedCiting(JudgeDossier dossier, params (string Req, string[] Evidence)[] cites) =>
        ValidVerdict(dossier) with
        {
            Assessments = cites.Select(c => Assess(c.Req, AssessmentStatus.Satisfied, c.Evidence)).ToList(),
        };

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_automated_test_needs_a_successful_objective_test_run()
    {
        var dossier = Dossier();
        var verdict = ApprovedCiting(dossier, ("R1", ["E2"]), ("R2", ["E2"]));

        var violations = VerdictValidator.Validate(verdict, dossier);

        var single = Assert.Single(violations);
        Assert.Equal(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion, single.Code);
        Assert.Contains("R1", single.Message, StringComparison.Ordinal);
        Assert.Contains("c1", single.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_a_test_run_reported_by_the_agent_does_not_count()
    {
        var reported = TestRunEvidence("REPORTED", Green, origin: EvidenceOrigin.ReportedByAgent);
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green), reported]);

        Assert.Empty(Codes(ApprovedCiting(dossier, ("R1", ["REPORTED", "E1"]), ("R2", ["E1"])), dossier));
        Assert.Contains(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion, Codes(ApprovedCiting(dossier, ("R1", ["REPORTED"]), ("R2", ["E1"])), dossier));
    }

    [Theory]
    [InlineData(EvidenceKind.CommandOutput, EvidenceOrigin.CapturedBySystem, true)]
    [InlineData(EvidenceKind.BuildLog, EvidenceOrigin.CapturedBySystem, true)]
    [InlineData(EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, false)]
    [InlineData(EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, false)]
    [InlineData(EvidenceKind.CommandOutput, EvidenceOrigin.ReportedByAgent, false)]
    public void ApprovedWithoutEvidenceForCriterion_command_check(EvidenceKind kind, EvidenceOrigin origin, bool accepted)
    {
        var dossier = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.CommandCheck))],
            evidence: [OtherEvidence("E1", origin, kind)]);

        var codes = Codes(ValidVerdict(dossier), dossier);

        Assert.Equal(!accepted, codes.Contains(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion));
    }

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_command_check_accepts_an_objective_test_run()
    {
        var dossier = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.CommandCheck))],
            evidence: [TestRunEvidence("E1", Green)]);

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Theory]
    [InlineData(EvidenceOrigin.ProvidedByHuman, true)]
    [InlineData(EvidenceOrigin.CapturedBySystem, false)]
    [InlineData(EvidenceOrigin.ReportedByAgent, false)]
    public void ApprovedWithoutEvidenceForCriterion_human_review_needs_evidence_provided_by_a_human(EvidenceOrigin origin, bool accepted)
    {
        var dossier = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.HumanReview))],
            evidence: [OtherEvidence("E1", origin, EvidenceKind.HumanNote)]);

        var codes = Codes(ValidVerdict(dossier), dossier);

        Assert.Equal(!accepted, codes.Contains(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion));
    }

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_judge_assessment_adds_no_requirement_and_each_criterion_is_reported()
    {
        var judgeOnly = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.JudgeAssessment))],
            evidence: [OtherEvidence("E1", EvidenceOrigin.CapturedBySystem)]);
        Assert.Empty(Codes(ValidVerdict(judgeOnly), judgeOnly));

        var several = Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "x", Crit("c1", VerificationMethod.HumanReview), Crit("c2", VerificationMethod.CommandCheck), Crit("c3", VerificationMethod.JudgeAssessment))],
            evidence: [OtherEvidence("E1", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement)]);
        var violations = VerdictValidator.Validate(ValidVerdict(several), several)
            .Where(v => v.Code == VerdictViolationCode.ApprovedWithoutEvidenceForCriterion).ToList();

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Message.Contains("c1", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Message.Contains("c2", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_is_only_checked_for_approvals()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier, VerdictDecision.ChangesRequested) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "E2"), Assess("R2", AssessmentStatus.Satisfied, "E2")],
        };

        Assert.Empty(Codes(verdict, dossier));
    }

    // ---- malformed verdicts (they come from the judge's JSON) ----

    [Theory]
    [InlineData("Assessments")]
    [InlineData("EvidenceExamined")]
    [InlineData("PendingObjections")]
    [InlineData("Risks")]
    [InlineData("Findings")]
    [InlineData("CorrectiveActions")]
    [InlineData("Confidence")]
    [InlineData("Judge")]
    public void MalformedVerdict_when_a_required_member_is_null(string member)
    {
        var dossier = Dossier();
        var valid = ValidVerdict(dossier);
        var verdict = member switch
        {
            "Assessments" => valid with { Assessments = null! },
            "EvidenceExamined" => valid with { EvidenceExamined = null! },
            "PendingObjections" => valid with { PendingObjections = null! },
            "Risks" => valid with { Risks = null! },
            "Findings" => valid with { Findings = null! },
            "CorrectiveActions" => valid with { CorrectiveActions = null! },
            "Confidence" => valid with { Confidence = null! },
            _ => valid with { Judge = null! },
        };

        var codes = Codes(verdict, dossier);

        Assert.Contains(VerdictViolationCode.MalformedVerdict, codes);
    }

    [Theory]
    [InlineData("Summary-null")]
    [InlineData("Summary-blank")]
    [InlineData("Rationale-null")]
    [InlineData("Description-null")]
    [InlineData("DecisionRefs-null")]
    public void MalformedVerdict_when_a_text_or_reference_member_is_missing(string member)
    {
        var dossier = Dossier();
        var valid = ValidVerdict(dossier, VerdictDecision.ChangesRequested);
        var finding = new Finding(FindingCategory.Other, Severity.Low, "a finding", [], []);
        var verdict = member switch
        {
            "Summary-null" => valid with { Summary = null! },
            "Summary-blank" => valid with { Summary = "   " },
            "Rationale-null" => valid with { Assessments = [valid.Assessments[0] with { Rationale = null! }, .. valid.Assessments.Skip(1)] },
            "Description-null" => valid with { Findings = [finding with { Description = null! }] },
            _ => valid with { Findings = [finding with { DecisionRefs = null! }] },
        };

        var codes = Codes(verdict, dossier);

        Assert.Contains(VerdictViolationCode.MalformedVerdict, codes);
    }

    [Fact]
    public void MalformedVerdict_null_collections_are_treated_as_empty_by_the_other_rules()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { Assessments = null!, Findings = null!, EvidenceExamined = null! };

        var codes = Codes(verdict, dossier);

        Assert.Contains(VerdictViolationCode.MalformedVerdict, codes);
        Assert.Contains(VerdictViolationCode.RequirementNotAssessed, codes);
    }

    [Fact]
    public void MalformedVerdict_null_confidence_and_judge_skip_the_rules_that_use_them()
    {
        var dossier = Dossier(excludedVendors: [VendorId.OpenAI]);
        var verdict = ValidVerdict(dossier) with { Confidence = null!, Judge = null! };

        var codes = Codes(verdict, dossier);

        Assert.DoesNotContain(VerdictViolationCode.MissingConfidenceJustification, codes);
        Assert.DoesNotContain(VerdictViolationCode.JudgeNotIndependent, codes);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, codes);
    }

    [Fact]
    public void MalformedVerdict_when_an_assessment_or_finding_has_null_evidence()
    {
        var dossier = Dossier();
        var withAssessment = ValidVerdict(dossier) with
        {
            Assessments = [new RequirementAssessment(Rid("R1"), AssessmentStatus.Satisfied, null!, "x"), Assess("R2", AssessmentStatus.Satisfied, "E1")],
        };
        var withFinding = ValidVerdict(dossier) with
        {
            Findings = [new Finding(FindingCategory.Other, Severity.Low, "x", null!, [])],
        };

        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(withAssessment, dossier));
        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(withFinding, dossier));
    }

    // ---- consistency of the human flag ----

    [Fact]
    public void ApprovedPendingHumanIntervention()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { RequiresHumanIntervention = true };

        AssertOnly(VerdictViolationCode.ApprovedPendingHumanIntervention, verdict, dossier);
    }

    // ---- ties and duplicates in the dossier ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApprovedWithFailingTests_when_the_latest_system_runs_tie_and_one_failed(bool failingFirst)
    {
        var failing = TestRunEvidence("F1", Red, T0.AddHours(1));
        var passing = TestRunEvidence("P1", Green, T0.AddHours(1));
        var human = OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote);
        var dossier = Dossier(evidence: failingFirst ? [failing, passing, human] : [passing, failing, human]);
        var verdict = ApprovedCiting(dossier, ("R1", ["P1"]), ("R2", ["E2"]));

        AssertOnly(VerdictViolationCode.ApprovedWithFailingTests, verdict, dossier);
    }

    [Fact]
    public void Duplicate_evidence_ids_in_the_dossier_keep_the_first_and_do_not_throw()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("E1", Green, T0.AddHours(1)),
            TestRunEvidence("E1", Red, T0),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ]);

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    // ---- the material must be intact, and the tests must be those of the commit under review ----

    [Fact]
    public void ApprovedWithIntegrityProblems()
    {
        var dossier = Dossier() with { IntegrityProblems = ["Evidence 'E1': its content cannot be read; it proves nothing."] };

        AssertOnly(VerdictViolationCode.ApprovedWithIntegrityProblems, ValidVerdict(dossier), dossier);
    }

    [Theory]
    [InlineData(VerdictDecision.ChangesRequested)]
    [InlineData(VerdictDecision.Rejected)]
    [InlineData(VerdictDecision.EscalateToHuman)]
    [InlineData(VerdictDecision.Unresolved)]
    public void Integrity_problems_leave_a_verdict_that_does_not_approve_valid(VerdictDecision decision)
    {
        var dossier = Dossier() with { IntegrityProblems = ["The run header names another plan; the log is used."] };

        Assert.Empty(Codes(ValidVerdict(dossier, decision), dossier));
    }

    [Fact]
    public void ApprovedWithoutTestRunForCommit_when_the_only_passing_run_verified_another_commit()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green, commit: CommitA), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote)])
            with { CandidateCommit = CommitB };

        Assert.Contains(VerdictViolationCode.ApprovedWithoutTestRunForCommit, Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void ApprovedWithoutTestRunForCommit_when_the_passing_run_names_no_commit()
    {
        var dossier = Dossier() with { CandidateCommit = CommitB };

        Assert.Contains(VerdictViolationCode.ApprovedWithoutTestRunForCommit, Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void A_passing_system_run_of_the_commit_under_review_supports_approval()
    {
        var dossier = Dossier(evidence: [TestRunEvidence("E1", Green, commit: CommitB), OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote)])
            with { CandidateCommit = CommitB };

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void The_latest_run_of_the_commit_under_review_decides_not_a_later_run_of_another_commit()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("E1", Green, T0, commit: CommitB),
            TestRunEvidence("STALE", Red, T0.AddHours(1), commit: CommitA),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ]) with { CandidateCommit = CommitB };

        Assert.Empty(Codes(ValidVerdict(dossier), dossier));
    }

    [Fact]
    public void A_later_failing_run_of_the_commit_under_review_blocks_approval()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("E1", Green, T0, commit: CommitB),
            TestRunEvidence("LATER", Red, T0.AddHours(1), commit: CommitB),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ]) with { CandidateCommit = CommitB };

        AssertOnly(VerdictViolationCode.ApprovedWithFailingTests, ValidVerdict(dossier), dossier);
    }

    [Fact]
    public void ApprovedWithoutEvidenceForCriterion_when_the_cited_test_run_verified_another_commit()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("OLD", Green, T0, commit: CommitA),
            TestRunEvidence("E1", Green, T0.AddHours(1), commit: CommitB),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ]) with { CandidateCommit = CommitB };

        AssertOnly(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion, CitingOnly(dossier, "OLD"), dossier);
    }

    [Fact]
    public void Without_a_commit_under_review_the_latest_system_run_still_decides()
    {
        var dossier = Dossier(evidence:
        [
            TestRunEvidence("E1", Green, T0, commit: CommitB),
            TestRunEvidence("LATER", Red, T0.AddHours(1), commit: CommitA),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ]);

        Assert.Contains(VerdictViolationCode.ApprovedWithFailingTests, Codes(ValidVerdict(dossier), dossier));
    }

    // ---- debates (docs/15 D-25): the resolution, and the judge's own assessment in the modes that choose ----

    private static readonly IReadOnlyList<string> Proposals = ["P1", "P2"];

    /// <summary>
    /// The dossier of a debate: one mandatory requirement whose criteria are given (by default one, assessed by the judge)
    /// and the two contributions as evidence, which are claims of agents (<c>AgentStatement</c>, <c>ReportedByAgent</c>).
    /// </summary>
    private static JudgeDossier DebateDossier(DebateMode mode = DebateMode.Free, bool choice = true, params AcceptanceCriterion[] criteria) =>
        Dossier(
            requirements: [Req("R1", RequirementPriority.Mandatory, "the best design", criteria.Length == 0 ? [Crit("c1", VerificationMethod.JudgeAssessment)] : criteria)],
            evidence: [OtherEvidence("C1", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement), OtherEvidence("C2", EvidenceOrigin.ReportedByAgent, EvidenceKind.AgentStatement)])
        with
        {
            Debate = new DebateDossierContext(mode, Proposals, choice),
        };

    /// <summary>A verdict on <see cref="DebateDossier"/> that satisfies R1 citing the first contribution and chooses <paramref name="chosen"/>.</summary>
    private static Verdict ChoiceVerdict(JudgeDossier dossier, string? chosen = "P1", VerdictDecision decision = VerdictDecision.Approved) =>
        ValidVerdict(dossier, decision) with
        {
            Assessments = [Assess("R1", AssessmentStatus.Satisfied, "C1")],
            Resolution = new DebateResolution(chosen, [new ProposalComparison("P1", "clara", "cara"), new ProposalComparison("P2", "barata", "frágil")]),
        };

    [Fact]
    public void A_choice_debate_is_approved_on_the_judges_own_assessment_of_the_contributions()
    {
        var dossier = DebateDossier();

        Assert.Empty(Codes(ChoiceVerdict(dossier), dossier));
    }

    [Theory]
    [InlineData(DebateMode.Free, VerdictDecision.Approved)]
    [InlineData(DebateMode.Free, VerdictDecision.ChangesRequested)]
    [InlineData(DebateMode.ArchitectureReview, VerdictDecision.Approved)]
    [InlineData(DebateMode.ArchitectureReview, VerdictDecision.ChangesRequested)]
    public void DebateResolutionMissing_when_a_free_or_architecture_debate_approves_or_asks_for_changes_without_choosing(DebateMode mode, VerdictDecision decision)
    {
        var dossier = DebateDossier(mode);

        AssertOnly(VerdictViolationCode.DebateResolutionMissing, ChoiceVerdict(dossier, chosen: null, decision), dossier);
        AssertOnly(VerdictViolationCode.DebateResolutionMissing, ChoiceVerdict(dossier, decision: decision) with { Resolution = null }, dossier);
    }

    [Theory]
    [InlineData(DebateMode.Free, VerdictDecision.Rejected)]
    [InlineData(DebateMode.ArchitectureReview, VerdictDecision.Rejected)]
    [InlineData(DebateMode.Adversarial, VerdictDecision.Approved)]
    [InlineData(DebateMode.Tribunal, VerdictDecision.Approved)]
    public void DebateResolutionMissing_does_not_apply_to_a_rejection_nor_to_the_other_modes(DebateMode mode, VerdictDecision decision)
    {
        var dossier = DebateDossier(mode);

        Assert.Empty(Codes(ChoiceVerdict(dossier, chosen: null, decision), dossier));
    }

    [Fact]
    public void DebateResolutionUnknown_when_the_chosen_proposal_is_not_of_this_debate()
    {
        var dossier = DebateDossier();

        AssertOnly(VerdictViolationCode.DebateResolutionUnknown, ChoiceVerdict(dossier, chosen: "P9"), dossier);
    }

    [Fact]
    public void DebateResolutionUnknown_when_a_compared_proposal_is_not_of_this_debate()
    {
        var dossier = DebateDossier();
        var verdict = ChoiceVerdict(dossier) with
        {
            Resolution = new DebateResolution("P1", [new ProposalComparison("P1", "clara", "cara"), new ProposalComparison("C1", "es una contribución", "no una propuesta")]),
        };

        AssertOnly(VerdictViolationCode.DebateResolutionUnknown, verdict, dossier);
    }

    /// <summary>The review of a run has no proposals: a verdict that names one speaks of a debate that is not this one.</summary>
    [Fact]
    public void DebateResolutionUnknown_when_the_verdict_of_a_run_review_names_a_proposal()
    {
        var dossier = Dossier();
        var verdict = ValidVerdict(dossier) with { Resolution = new DebateResolution("P1", []) };

        AssertOnly(VerdictViolationCode.DebateResolutionUnknown, verdict, dossier);
        Assert.Empty(Codes(ValidVerdict(dossier) with { Resolution = new DebateResolution(null, []) }, dossier));
    }

    [Fact]
    public void MalformedVerdict_when_the_resolution_has_no_comparison_or_a_null_one()
    {
        var dossier = DebateDossier();

        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(ChoiceVerdict(dossier) with { Resolution = new DebateResolution("P1", null!) }, dossier));
        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(ChoiceVerdict(dossier) with { Resolution = new DebateResolution("P1", [null!]) }, dossier));
    }

    [Fact]
    public void The_judges_own_assessment_is_exempt_only_when_every_criterion_of_the_requirement_is_one()
    {
        var dossier = DebateDossier(criteria: [Crit("c1", VerificationMethod.JudgeAssessment), Crit("c2", VerificationMethod.HumanReview)]);

        var codes = Codes(ChoiceVerdict(dossier), dossier);

        Assert.Contains(VerdictViolationCode.ApprovedOnAgentClaimsOnly, codes);
        Assert.Contains(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion, codes);
    }

    /// <summary>In the modes that review a run, and in the challenge of one, the rules are exactly those of Phase 3.</summary>
    [Fact]
    public void The_judges_own_assessment_is_not_exempt_in_a_debate_that_reviews_work()
    {
        var dossier = DebateDossier(DebateMode.ImplementationAudit, choice: false);

        AssertOnly(VerdictViolationCode.ApprovedOnAgentClaimsOnly, ChoiceVerdict(dossier, chosen: null), dossier);
    }

    [Fact]
    public void The_judges_own_assessment_is_not_exempt_in_the_review_of_a_run()
    {
        var dossier = DebateDossier() with { Debate = null };

        AssertOnly(VerdictViolationCode.ApprovedOnAgentClaimsOnly, ValidVerdict(dossier) with { Assessments = [Assess("R1", AssessmentStatus.Satisfied, "C1")] }, dossier);
    }

    [Fact]
    public void An_objective_criterion_still_needs_its_objective_evidence_in_a_choice_debate()
    {
        var dossier = DebateDossier(criteria: [Crit("c1", VerificationMethod.HumanReview)]);

        var codes = Codes(ChoiceVerdict(dossier), dossier);

        Assert.Contains(VerdictViolationCode.ApprovedOnAgentClaimsOnly, codes);
        Assert.Contains(VerdictViolationCode.ApprovedWithoutEvidenceForCriterion, codes);
    }

    [Fact]
    public void Satisfying_a_requirement_still_needs_evidence_in_a_choice_debate()
    {
        var dossier = DebateDossier();
        var verdict = ChoiceVerdict(dossier) with { Assessments = [Assess("R1", AssessmentStatus.Satisfied)] };

        Assert.Contains(VerdictViolationCode.SatisfiedWithoutEvidence, Codes(verdict, dossier));
    }

    [Fact]
    public void Every_violation_code_has_a_test_in_this_class()
    {
        var covered = typeof(VerdictValidatorTests).GetMethods().Select(m => m.Name).ToHashSet();
        var missing = Enum.GetValues<VerdictViolationCode>().Select(c => c.ToString()).Where(n => !covered.Any(m => m.StartsWith(n, StringComparison.Ordinal))).ToList();

        Assert.Empty(missing);
    }
}
