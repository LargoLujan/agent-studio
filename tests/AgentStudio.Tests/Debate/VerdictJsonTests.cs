using System.Text.Json.Nodes;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

public sealed class VerdictJsonTests
{
    private static readonly JudgeIdentity SystemJudge = new(new AgentId("juez"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-codex"));
    private static readonly DateTimeOffset IssuedAt = T0.AddMinutes(5);

    /// <summary>A verdict as the judge writes it for <see cref="Dossier"/>: R1 satisfied by the green system test run E1.</summary>
    private static JsonObject Answer()
    {
        var dossier = Dossier();
        return JsonNode.Parse($$"""
            {
              "assessments": [
                { "requirement": "R1", "status": "Satisfied", "evidence": ["E1"], "rationale": "El test E1 prueba R1." },
                { "requirement": "R2", "status": "NotVerifiable", "evidence": [], "rationale": "Nadie lo revisó." }
              ],
              "evidenceExamined": ["E1", "E2"],
              "findings": [
                { "category": "OutOfScopeChange", "severity": "Low", "description": "Toca el README.", "evidence": ["E2"], "decisionRefs": [4, 7] }
              ],
              "pendingObjections": ["Revisar los nombres"],
              "risks": ["Desbordamiento"],
              "correctiveActions": [],
              "confidence": { "level": "High", "justification": "Tests del sistema en verde." },
              "requiresHumanIntervention": false,
              "decision": "Approved",
              "summary": "Cumple R1.",
              "requirementSetHash": "{{dossier.Requirements.ContentHash}}",
              "decisionLogHeadHash": "{{dossier.DecisionLogHeadHash}}"
            }
            """)!.AsObject();
    }

    private static Verdict Parse(JsonNode answer) => VerdictJson.Parse(answer.ToJsonString(), SystemJudge, IssuedAt);

    private static IReadOnlyList<VerdictViolationCode> Codes(Verdict verdict) =>
        VerdictValidator.Validate(verdict, Dossier()).Select(v => v.Code).ToList();

    [Fact]
    public void A_complete_answer_becomes_the_domain_verdict_with_the_identity_and_time_the_system_gives()
    {
        var verdict = Parse(Answer());

        Assert.Equal(VerdictDecision.Approved, verdict.Decision);
        Assert.Equal("Cumple R1.", verdict.Summary);
        Assert.Equal([(Rid("R1"), AssessmentStatus.Satisfied), (Rid("R2"), AssessmentStatus.NotVerifiable)], verdict.Assessments.Select(a => (a.Requirement, a.Status)));
        Assert.Equal([Eid("E1")], verdict.Assessments[0].Evidence);
        Assert.Equal("El test E1 prueba R1.", verdict.Assessments[0].Rationale);
        Assert.Equal([Eid("E1"), Eid("E2")], verdict.EvidenceExamined);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal((FindingCategory.OutOfScopeChange, Severity.Low, "Toca el README."), (finding.Category, finding.Severity, finding.Description));
        Assert.Equal([4L, 7L], finding.DecisionRefs);
        Assert.Equal(["Revisar los nombres"], verdict.PendingObjections);
        Assert.Equal(["Desbordamiento"], verdict.Risks);
        Assert.Empty(verdict.CorrectiveActions);
        Assert.Equal(new Confidence(ConfidenceLevel.High, "Tests del sistema en verde."), verdict.Confidence);
        Assert.False(verdict.RequiresHumanIntervention);
        Assert.Equal(SystemJudge, verdict.Judge);
        Assert.Equal(IssuedAt, verdict.IssuedAt);
        Assert.Empty(Codes(verdict));
    }

    [Theory]
    [InlineData("assessments")]
    [InlineData("evidenceExamined")]
    [InlineData("findings")]
    [InlineData("pendingObjections")]
    [InlineData("risks")]
    [InlineData("correctiveActions")]
    [InlineData("confidence")]
    public void A_member_the_judge_left_null_stays_null_and_the_validator_reports_a_malformed_verdict(string member)
    {
        var answer = Answer();
        answer[member] = null;

        var verdict = Parse(answer);

        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(verdict));
    }

    [Fact]
    public void A_member_the_judge_left_out_is_null_as_well()
    {
        var answer = Answer();
        answer.Remove("risks");

        var verdict = Parse(answer);

        Assert.Null(verdict.Risks);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(verdict));
    }

    [Fact]
    public void Null_texts_and_hashes_stay_null_instead_of_being_filled_in()
    {
        var answer = Answer();
        answer["summary"] = null;
        answer["requirementSetHash"] = null;
        answer["confidence"]!["justification"] = null;

        var verdict = Parse(answer);

        Assert.Null(verdict.Summary);
        Assert.Null(verdict.RequirementSetHash);
        Assert.Null(verdict.Confidence.Justification);
        Assert.Contains(VerdictViolationCode.RequirementSetMismatch, Codes(verdict));
        Assert.Contains(VerdictViolationCode.MissingConfidenceJustification, Codes(verdict));
    }

    [Fact]
    public void An_assessment_without_a_requirement_stays_as_a_null_item()
    {
        var answer = Answer();
        answer["assessments"]![1]!["requirement"] = null;

        var verdict = Parse(answer);

        Assert.Equal(2, verdict.Assessments.Count);
        Assert.Null(verdict.Assessments[1]);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(verdict));
    }

    [Fact]
    public void A_blank_evidence_id_makes_that_list_malformed_instead_of_being_dropped()
    {
        var answer = Answer();
        answer["assessments"]![0]!["evidence"] = new JsonArray("E1", "");

        var verdict = Parse(answer);

        Assert.Null(verdict.Assessments[0].Evidence);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, Codes(verdict));
    }

    [Fact]
    public void Enum_values_are_matched_by_name_never_by_number()
    {
        var answer = Answer();
        answer["assessments"]![0]!["status"] = "0";

        Assert.Null(Parse(answer).Assessments[0]);

        answer["decision"] = "0";
        Assert.Throws<JudgeOutputException>(() => Parse(answer));
    }

    [Fact]
    public void A_judge_cannot_state_its_own_identity_because_unknown_members_are_rejected()
    {
        var answer = Answer();
        answer["judge"] = new JsonObject { ["agent"] = "juez", ["vendor"] = "anthropic" };

        var ex = Assert.Throws<JudgeOutputException>(() => Parse(answer));
        Assert.Contains("judge", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_members_inside_an_item_are_rejected_too()
    {
        var answer = Answer();
        answer["confidence"]!["score"] = 0.9;

        Assert.Throws<JudgeOutputException>(() => Parse(answer));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("requiresHumanIntervention")]
    public void An_answer_without_a_decision_or_the_human_flag_is_not_a_verdict(string member)
    {
        var answer = Answer();
        answer[member] = null;

        Assert.Throws<JudgeOutputException>(() => Parse(answer));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("no es JSON")]
    [InlineData("""{"decision":"Approved","decision":"Rejected"}""")]
    public void Text_that_is_not_one_json_object_is_not_a_verdict(string text)
    {
        Assert.Throws<JudgeOutputException>(() => VerdictJson.Parse(text, SystemJudge, IssuedAt));
    }

    [Fact]
    public void A_verdict_written_back_as_json_reads_the_same_without_identity_or_time()
    {
        var original = Parse(Answer());

        var json = VerdictJson.ToJson(original);
        var reread = VerdictJson.Parse(json, SystemJudge, IssuedAt);

        Assert.DoesNotContain("issuedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.Decision, reread.Decision);
        Assert.Equal(original.Summary, reread.Summary);
        Assert.Equal(original.Assessments.Select(a => (a.Requirement, a.Status, a.Rationale)), reread.Assessments.Select(a => (a.Requirement, a.Status, a.Rationale)));
        Assert.Equal(original.Findings[0].DecisionRefs, reread.Findings[0].DecisionRefs);
        Assert.Equal(original.Confidence, reread.Confidence);
        Assert.Equal(original.DecisionLogHeadHash, reread.DecisionLogHeadHash);
    }

    /// <summary>
    /// The verdict a judge is shown in its correction round, as Phase 3 writes it: a verdict without a resolution (every
    /// verdict outside a debate, and every one stored before Phase 5) is written byte for byte as before.
    /// </summary>
    [Fact]
    public void A_verdict_without_a_resolution_is_written_exactly_as_in_phase_3()
    {
        var verdict = Parse(Answer());
        var dossier = Dossier();

        var json = VerdictJson.ToJson(verdict);

        Assert.Equal(
            "{\n  \"assessments\": [\n    {\n      \"requirement\": \"R1\",\n      \"status\": \"Satisfied\",\n      \"evidence\": [\n        \"E1\"\n      ],\n"
            + "      \"rationale\": \"El test E1 prueba R1.\"\n    },\n    {\n      \"requirement\": \"R2\",\n      \"status\": \"NotVerifiable\",\n      \"evidence\": [],\n"
            + "      \"rationale\": \"Nadie lo revisó.\"\n    }\n  ],\n  \"evidenceExamined\": [\n    \"E1\",\n    \"E2\"\n  ],\n  \"findings\": [\n    {\n"
            + "      \"category\": \"OutOfScopeChange\",\n      \"severity\": \"Low\",\n      \"description\": \"Toca el README.\",\n      \"evidence\": [\n        \"E2\"\n      ],\n"
            + "      \"decisionRefs\": [\n        4,\n        7\n      ]\n    }\n  ],\n  \"pendingObjections\": [\n    \"Revisar los nombres\"\n  ],\n  \"risks\": [\n"
            + "    \"Desbordamiento\"\n  ],\n  \"correctiveActions\": [],\n  \"confidence\": {\n    \"level\": \"High\",\n    \"justification\": \"Tests del sistema en verde.\"\n  },\n"
            + "  \"requiresHumanIntervention\": false,\n  \"decision\": \"Approved\",\n  \"summary\": \"Cumple R1.\",\n"
            + $"  \"requirementSetHash\": \"{dossier.Requirements.ContentHash}\",\n  \"decisionLogHeadHash\": \"{dossier.DecisionLogHeadHash}\"\n}}",
            json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_malformed_verdict_can_still_be_written_back_with_its_nulls()
    {
        var answer = Answer();
        answer["assessments"]![1]!["requirement"] = null;
        answer["risks"] = null;
        var malformed = Parse(answer);

        var reread = VerdictJson.Parse(VerdictJson.ToJson(malformed), SystemJudge, IssuedAt);

        Assert.Null(reread.Risks);
        Assert.Null(reread.Assessments[1]);
    }

    // ---- the resolution of a debate (docs/15 D-25) ----

    /// <summary>A free debate between P1 and P2 whose requirements are the judge's own assessment: the dossier of <see cref="DebateAnswer"/>.</summary>
    private static JudgeDossier DebateDossier() => Dossier() with { Debate = new DebateDossierContext(AgentStudio.Domain.Debate.DebateMode.Free, ["P1", "P2"], Choice: true) };

    /// <summary>The same answer as a judge of a debate gives it: P2 chosen, after comparing both proposals.</summary>
    private static JsonObject DebateAnswer()
    {
        var answer = Answer();
        answer["resolution"] = new JsonObject
        {
            ["comparison"] = new JsonArray(
                new JsonObject { ["proposal"] = "P1", ["strengths"] = "Sencilla.", ["weaknesses"] = "No escala." },
                new JsonObject { ["proposal"] = "P2", ["strengths"] = "Escala.", ["weaknesses"] = "Más código." }),
            ["chosenProposal"] = "P2",
        };
        return answer;
    }

    private static Verdict ParseDebate(JsonNode answer) => VerdictJson.ParseDebate(answer.ToJsonString(), SystemJudge, IssuedAt);

    private static IReadOnlyList<VerdictViolationCode> DebateCodes(Verdict verdict) =>
        VerdictValidator.Validate(verdict, DebateDossier()).Select(v => v.Code).ToList();

    [Fact]
    public void The_answer_of_a_debate_judge_carries_the_proposal_it_chose_and_the_comparison()
    {
        var verdict = ParseDebate(DebateAnswer());

        Assert.Equal("P2", verdict.Resolution!.ChosenProposal);
        Assert.Equal(
            [new ProposalComparison("P1", "Sencilla.", "No escala."), new ProposalComparison("P2", "Escala.", "Más código.")],
            verdict.Resolution.Comparison);
        Assert.Equal(VerdictDecision.Approved, verdict.Decision);
        Assert.Equal(SystemJudge, verdict.Judge);
        Assert.Empty(DebateCodes(verdict));
    }

    [Fact]
    public void The_sentinel_ninguna_is_read_as_no_proposal_chosen()
    {
        var answer = DebateAnswer();
        answer["resolution"]!["chosenProposal"] = VerdictJson.NoProposal;
        answer["decision"] = "Rejected";
        answer["correctiveActions"] = new JsonArray("Rehacer las dos propuestas");

        var verdict = ParseDebate(answer);

        Assert.Equal("ninguna", VerdictJson.NoProposal);
        Assert.Null(verdict.Resolution!.ChosenProposal);
        Assert.Equal(2, verdict.Resolution.Comparison.Count);
        Assert.Empty(DebateCodes(verdict));
    }

    [Fact]
    public void Approving_a_free_debate_with_no_proposal_chosen_is_reported_by_the_validator()
    {
        var answer = DebateAnswer();
        answer["resolution"]!["chosenProposal"] = VerdictJson.NoProposal;

        Assert.Contains(VerdictViolationCode.DebateResolutionMissing, DebateCodes(ParseDebate(answer)));
    }

    [Fact]
    public void A_proposal_that_is_not_of_the_debate_is_read_as_given_so_the_validator_reports_it()
    {
        var answer = DebateAnswer();
        answer["resolution"]!["chosenProposal"] = "P7";

        var verdict = ParseDebate(answer);

        Assert.Equal("P7", verdict.Resolution!.ChosenProposal);
        Assert.Contains(VerdictViolationCode.DebateResolutionUnknown, DebateCodes(verdict));
    }

    [Fact]
    public void The_review_of_a_run_still_refuses_a_resolution_because_its_schema_has_none()
    {
        var ex = Assert.Throws<JudgeOutputException>(() => Parse(DebateAnswer()));

        Assert.Contains("resolution", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_of_phase_3_read_as_a_debate_answer_has_no_resolution_and_reads_the_same()
    {
        var phase3 = Parse(Answer());

        var debate = ParseDebate(Answer());

        Assert.Null(phase3.Resolution);
        Assert.Null(debate.Resolution);
        Assert.Equal(VerdictJson.ToJson(phase3), VerdictJson.ToJson(debate));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("number")]
    public void A_resolution_whose_chosen_proposal_cannot_be_read_is_not_a_verdict_because_null_would_mean_none(string how)
    {
        var answer = DebateAnswer();
        var resolution = answer["resolution"]!.AsObject();
        switch (how)
        {
            case "null":
                resolution["chosenProposal"] = null;
                break;
            case "missing":
                resolution.Remove("chosenProposal");
                break;
            default:
                resolution["chosenProposal"] = 2;
                break;
        }

        var ex = Assert.Throws<JudgeOutputException>(() => ParseDebate(answer));

        Assert.Contains("chosenProposal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolution_left_null_or_written_as_another_type_stays_null()
    {
        var answer = DebateAnswer();
        answer["resolution"] = null;
        Assert.Null(ParseDebate(answer).Resolution);

        answer["resolution"] = "P2";
        Assert.Null(ParseDebate(answer).Resolution);
    }

    [Fact]
    public void A_comparison_left_null_stays_null_and_the_validator_reports_a_malformed_verdict()
    {
        var answer = DebateAnswer();
        answer["resolution"]!["comparison"] = null;

        var verdict = ParseDebate(answer);

        Assert.Null(verdict.Resolution!.Comparison);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, DebateCodes(verdict));
    }

    [Theory]
    [InlineData("proposal")]
    [InlineData("strengths")]
    [InlineData("weaknesses")]
    public void A_comparison_that_cannot_be_read_stays_as_a_null_item_instead_of_being_dropped(string member)
    {
        var answer = DebateAnswer();
        answer["resolution"]!["comparison"]![1]![member] = null;

        var verdict = ParseDebate(answer);

        Assert.Equal(2, verdict.Resolution!.Comparison.Count);
        Assert.Null(verdict.Resolution.Comparison[1]);
        Assert.Contains(VerdictViolationCode.MalformedVerdict, DebateCodes(verdict));
    }

    [Fact]
    public void Unknown_members_of_the_resolution_and_of_a_comparison_are_rejected()
    {
        var answer = DebateAnswer();
        answer["resolution"]!["winner"] = "P1";
        Assert.Throws<JudgeOutputException>(() => ParseDebate(answer));

        answer = DebateAnswer();
        answer["resolution"]!["comparison"]![0]!["score"] = 9;
        Assert.Throws<JudgeOutputException>(() => ParseDebate(answer));
    }

    [Fact]
    public void A_debate_verdict_written_back_as_json_reads_the_same_resolution()
    {
        var chosen = ParseDebate(DebateAnswer());
        var none = chosen with { Resolution = chosen.Resolution! with { ChosenProposal = null } };

        var rereadChosen = VerdictJson.ParseDebate(VerdictJson.ToJson(chosen), SystemJudge, IssuedAt);
        var rereadNone = VerdictJson.ParseDebate(VerdictJson.ToJson(none), SystemJudge, IssuedAt);

        Assert.Equal("P2", rereadChosen.Resolution!.ChosenProposal);
        Assert.Equal(chosen.Resolution!.Comparison, rereadChosen.Resolution.Comparison);
        Assert.Contains("\"chosenProposal\": \"ninguna\"", VerdictJson.ToJson(none), StringComparison.Ordinal);
        Assert.Null(rereadNone.Resolution!.ChosenProposal);
    }

    [Fact]
    public void The_resolution_is_written_after_the_analysis_and_before_the_decision()
    {
        var json = JsonNode.Parse(VerdictJson.ToJson(ParseDebate(DebateAnswer())))!.AsObject();

        var order = json.Select(p => p.Key).ToList();
        Assert.True(order.IndexOf("confidence") < order.IndexOf("resolution"), string.Join(",", order));
        Assert.True(order.IndexOf("resolution") < order.IndexOf("decision"), string.Join(",", order));
        Assert.Equal(["comparison", "chosenProposal"], json["resolution"]!.AsObject().Select(p => p.Key));
    }
}
