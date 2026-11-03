using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>What the judge of a whole-project audit is asked (docs/15 D-30): the rubric, where the copy is and the index as data.</summary>
public sealed class ProjectAuditPromptTests
{
    private const string Audit = "aud-20261019-0a0b0c";
    private const string Index = """{"format":"agent-studio/project-dossier/1","headCommit":"abc"}""";
    private const string Copy = @"C:\audits\proyecto\aud-20261019-0a0b0c\snapshot";

    private static readonly JudgeIdentity Codex = new(Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private static JudgeDossier Dossier() =>
        new("Auditoría del proyecto completo", Requirements(), string.Empty, [], new string('d', 64), [], [], new HashSet<VendorId> { VendorId.Anthropic }) { CandidateCommit = CommitOf("head") };

    [Fact]
    public void The_evaluation_carries_the_rubric_the_copy_and_the_index_between_the_data_delimiters()
    {
        var prompt = new ProjectAuditPrompt(Audit, Index).Evaluation(Dossier(), null, Copy);

        var data = prompt[(prompt.IndexOf(JudgePrompt.DataOpen, StringComparison.Ordinal) + JudgePrompt.DataOpen.Length)..prompt.IndexOf(JudgePrompt.DataClose, StringComparison.Ordinal)];
        Assert.Equal(Index, data.Trim());
        Assert.Contains("whole-project audit", prompt, StringComparison.Ordinal);
        Assert.Contains(Copy, prompt, StringComparison.Ordinal);
        Assert.Contains("systemChecks", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Answer with the verdict JSON only." + Environment.NewLine, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_correction_names_the_broken_rules_and_the_hashes_and_repeats_the_material_only_in_a_new_session(bool resumed)
    {
        var dossier = Dossier();
        var previous = ScriptedJudge.From(JudgeAnswers.Approval(dossier, new EvidenceId("EV-1")), Codex);
        var violations = new[] { new VerdictViolation(VerdictViolationCode.EvidenceNotInDossier, "EV-1 is not in the dossier.") };

        var prompt = new ProjectAuditPrompt(Audit, Index).Correction(dossier, previous, violations, resumed, null, Copy);

        Assert.Contains("EV-1 is not in the dossier.", prompt, StringComparison.Ordinal);
        Assert.Contains(dossier.Requirements.ContentHash, prompt, StringComparison.Ordinal);
        Assert.Contains(dossier.DecisionLogHeadHash, prompt, StringComparison.Ordinal);
        Assert.Equal(!resumed, prompt.Contains(JudgePrompt.DataOpen, StringComparison.Ordinal));
        Assert.Equal(!resumed, prompt.Contains("<previous_verdict>", StringComparison.Ordinal));
    }

    [Fact]
    public void It_answers_with_the_verdict_schema_and_reads_the_answer_with_the_identity_the_system_gives()
    {
        var dossier = Dossier();
        var brief = new ProjectAuditPrompt(Audit, Index);
        var at = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        var verdict = brief.Read(JudgeAnswers.Approval(dossier, new EvidenceId("EV-1")).ToJsonString(), Codex, at);

        Assert.Equal(VerdictSchema.Build(dossier.Requirements), brief.Schema(dossier));
        Assert.Equal((VerdictDecision.Approved, Codex, at), (verdict.Decision, verdict.Judge, verdict.IssuedAt));
        Assert.Equal(("Auditoría del proyecto completo (juez)", Audit), (brief.Purpose, brief.DebateId));
        Assert.Equal(["#auditoria"], brief.Tags);
    }
}
