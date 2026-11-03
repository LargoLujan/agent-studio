using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class SqliteVerdictStoreTests : IDisposable
{
    private const string Run = "20261015-a1b2c3";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredVerdict Valid(string id = "v1", string? run = Run, string? supersedes = null) =>
        new(id, ValidVerdict(Dossier()), run, "T3", AuditScope.CurrentPlan, true, [], supersedes);

    /// <summary>What a judge's JSON can turn into: missing members and missing list items stay null.</summary>
    private static Verdict Malformed() =>
        ValidVerdict(Dossier()) with
        {
            Summary = null!,
            Assessments = [null!, new RequirementAssessment(Rid("R1"), AssessmentStatus.Satisfied, null!, null!)],
            EvidenceExamined = null!,
            PendingObjections = ["objeción", null!],
            Findings = [new Finding(FindingCategory.TestsNotRun, Severity.High, "sin tests", null!, [3, 7])],
            Confidence = null!,
            CorrectiveActions = null!,
            Judge = new JudgeIdentity(Aid("juez"), null!),
            RequirementSetHash = null!,
            DecisionLogHeadHash = null!,
        };

    [Fact]
    public async Task A_valid_verdict_is_listed_after_reopening_exactly_as_it_was_appended()
    {
        var verdict = Valid();
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            Assert.Equal("v1", await first.Verdicts.AppendAsync(first.ProjectId, verdict, Ct));
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var listed = Assert.Single(await second.Verdicts.ListAsync(second.ProjectId, Run, Ct));

        Assert.Equal(Shape(verdict), Shape(listed));
    }

    [Fact]
    public async Task A_malformed_verdict_is_kept_exactly_as_the_judge_gave_it()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var dossier = Dossier();
        var verdict = Malformed();
        var issues = VerdictValidator.Validate(verdict, dossier);
        var stored = new StoredVerdict("v-malo", verdict, Run, "T3", AuditScope.CurrentPlan, false, issues, null);

        await instance.Verdicts.AppendAsync(instance.ProjectId, stored, Ct);
        var listed = Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, null, Ct));

        Assert.Equal(Shape(stored), Shape(listed));
        Assert.False(listed.Valid);
        Assert.Contains(listed.Issues, i => i.Code == VerdictViolationCode.MalformedVerdict);
        Assert.Equal(Shape(issues), Shape(VerdictValidator.Validate(listed.Verdict, dossier)));
    }

    [Fact]
    public async Task A_correction_points_at_the_verdict_it_supersedes_and_both_stay()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v1") with { Valid = false }, Ct);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v2", supersedes: "v1"), Ct);

        var listed = await instance.Verdicts.ListAsync(instance.ProjectId, Run, Ct);

        Assert.Equal(["v1", "v2"], listed.Select(v => v.Id));
        Assert.Null(listed[0].Supersedes);
        Assert.Equal("v1", listed[1].Supersedes);
    }

    [Fact]
    public async Task A_correction_of_a_verdict_that_does_not_exist_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v2", supersedes: "no-existe"), Ct));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verdicts_are_listed_by_run_in_the_order_they_were_issued()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v1"), Ct);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v2", run: "20261015-otra00"), Ct);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v3"), Ct);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid("v4", run: null) with { Scope = AuditScope.WholeProject }, Ct);

        Assert.Equal(["v1", "v3"], (await instance.Verdicts.ListAsync(instance.ProjectId, Run, Ct)).Select(v => v.Id));
        Assert.Equal(["v1", "v2", "v3", "v4"], (await instance.Verdicts.ListAsync(instance.ProjectId, null, Ct)).Select(v => v.Id));
        Assert.Equal(AuditScope.WholeProject, (await instance.Verdicts.ListAsync(instance.ProjectId, null, Ct))[3].Scope);
    }

    [Fact]
    public async Task A_verdict_without_an_id_gets_one()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var id = await instance.Verdicts.AppendAsync(instance.ProjectId, Valid(string.Empty), Ct);

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, Run, Ct)).Id);
    }

    [Fact]
    public async Task Appending_the_same_verdict_again_is_harmless_but_other_content_under_its_id_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid(), Ct);

        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instance.Verdicts.AppendAsync(instance.ProjectId, Valid() with { Verdict = ValidVerdict(Dossier(), VerdictDecision.Rejected) }, Ct));

        var listed = Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, null, Ct));
        Assert.Equal(VerdictDecision.Approved, listed.Verdict.Decision);
    }

    // ---- Phase 5 (docs/15 D-25 and section 6): the resolution of a debate in payload_json ----

    private const string Debate = "deb-20261019-a1b2c3";

    /// <summary>A verdict of the judge of a debate: its resolution, the debate as its run and the judge's task (D-44).</summary>
    private static StoredVerdict DebateVerdict(DebateResolution? resolution, string id = "v-debate") =>
        new(id, ValidVerdict(Dossier()) with { Resolution = resolution }, Debate, "juez-juez-revision", AuditScope.Debate, true, [], null);

    private static DebateResolution Resolution() => new(
        "P2",
        [new ProposalComparison("P1", "Sencilla «y» barata", "No escala"), new ProposalComparison("P2", "Escala", "Más \"cara\"\ny más lenta")]);

    [Fact]
    public async Task A_debate_verdict_with_its_resolution_is_listed_after_reopening_exactly_as_it_was_appended()
    {
        var verdict = DebateVerdict(Resolution());
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await first.Verdicts.AppendAsync(first.ProjectId, verdict, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var listed = Assert.Single(await second.Verdicts.ListAsync(second.ProjectId, Debate, Ct));

        Assert.Equal(Shape(verdict), Shape(listed));
        Assert.Equal(AuditScope.Debate, listed.Scope);
        Assert.Equal("P2", listed.Verdict.Resolution?.ChosenProposal);
        Assert.Equal(["P1", "P2"], listed.Verdict.Resolution!.Comparison.Select(c => c.Proposal));
    }

    public static TheoryData<string> ResolutionsAsTheJudgeGaveThem => ["ninguna", "no-comparison", "null-items", "null-members"];

    [Theory]
    [MemberData(nameof(ResolutionsAsTheJudgeGaveThem))]
    public async Task A_resolution_is_kept_exactly_as_the_judge_gave_it(string shape)
    {
        // «ninguna» is a chosen proposal of null; a missing comparison or a null item is MalformedVerdict for the
        // validator (docs/04 section 17.1), so the store keeps them as they came, never repaired.
        var resolution = shape switch
        {
            "ninguna" => new DebateResolution(null, [new ProposalComparison("P1", "a", "b")]),
            "no-comparison" => new DebateResolution("P1", null!),
            "null-items" => new DebateResolution(null, [null!, new ProposalComparison("P1", "a", "b")]),
            "null-members" => new DebateResolution("P1", [new ProposalComparison(null!, "a", null!)]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var verdict = DebateVerdict(resolution) with { Valid = false };
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        await instance.Verdicts.AppendAsync(instance.ProjectId, verdict, Ct);
        var listed = Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, Debate, Ct));

        Assert.NotNull(listed.Verdict.Resolution);
        Assert.Equal(Shape(verdict), Shape(listed));
        await instance.Verdicts.AppendAsync(instance.ProjectId, verdict, Ct);
    }

    [Fact]
    public async Task The_resolution_is_stored_after_the_members_of_phase_3_and_a_verdict_without_one_has_no_trace_of_it()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, DebateVerdict(Resolution()), Ct);
        await instance.Verdicts.AppendAsync(instance.ProjectId, Valid(), Ct);

        using var connection = instance.Connect();
        var withResolution = Text(connection, "SELECT payload_json FROM verdicts WHERE id = 'v-debate'");
        var without = Text(connection, "SELECT payload_json FROM verdicts WHERE id = 'v1'");

        Assert.EndsWith(
            ""","resolution":{"chosenProposal":"P2","comparison":[{"proposal":"P1","strengths":"Sencilla «y» barata","weaknesses":"No escala"},{"proposal":"P2","strengths":"Escala","weaknesses":"Más \"cara\"\ny más lenta"}]}}""",
            withResolution);
        Assert.DoesNotContain("resolution", without, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM verdicts WHERE id = 'v-debate' AND scope = 'Debate' AND debate_id IS NULL"));
    }

    [Fact]
    public async Task A_verdict_stored_before_phase_5_is_read_without_a_resolution_and_is_still_written_as_the_same_text()
    {
        // payload_json exactly as the store wrote it in Phase 3 and 4, by hand: no resolution member.
        const string Stored =
            """{"decision":"Approved","summary":"Cumple R1","assessments":[{"requirement":"R1","status":"Satisfied","evidence":["ev-tests"],"rationale":"Los tests del commit pasan"}],"evidenceExamined":["ev-tests"],"pendingObjections":[],"risks":[],"findings":[],"confidence":{"level":"High","justification":"Tests capturados por el sistema"},"correctiveActions":[],"requiresHumanIntervention":false,"judge":{"agent":"juez","model":{"provider":"codex-cli","vendor":"openai","model":"gpt-x"}},"requirementSetHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","decisionLogHeadHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","issuedAt":"2026-01-01T10:00:00+00:00"}""";
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        using (var connection = instance.Connect())
        {
            Exec(connection, $"""
                INSERT INTO verdicts(id, project_id, decision, summary, confidence_level, confidence_justification, requires_human,
                    judge_agent_id, judge_provider_id, judge_vendor_id, judge_model, requirement_set_hash, decision_log_head_hash,
                    payload_json, issued_at, run_id, task_id, scope, valid, validation_json, supersedes_verdict_id)
                VALUES ('v-fase3', '{instance.ProjectId}', 'Approved', 'Cumple R1', 'High', 'Tests capturados por el sistema', 0,
                    'juez', 'codex-cli', 'openai', 'gpt-x', '{HashA}', '{HashB}',
                    '{Stored}', '2026-01-01T10:00:00.0000000+00:00', '{Run}', 'revision', 'CurrentPlan', 1, '[]', NULL);
                """);
        }

        var listed = Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, Run, Ct));

        Assert.Null(listed.Verdict.Resolution);
        Assert.Equal(VerdictDecision.Approved, listed.Verdict.Decision);
        Assert.Equal(Eid("ev-tests"), Assert.Single(listed.Verdict.EvidenceExamined));
        Assert.Equal(new JudgeIdentity(Aid("juez"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-x")), listed.Verdict.Judge);

        // Appending what was read writes nothing and is not refused: the store gives it exactly the stored text.
        await instance.Verdicts.AppendAsync(instance.ProjectId, listed, Ct);
        using var check = instance.Connect();
        Assert.Equal(Stored, Text(check, "SELECT payload_json FROM verdicts WHERE id = 'v-fase3'"));
    }

    [Fact]
    public async Task A_resolution_under_the_id_of_a_verdict_without_one_is_other_content_and_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, DebateVerdict(null), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => instance.Verdicts.AppendAsync(instance.ProjectId, DebateVerdict(Resolution()), Ct));

        Assert.Null(Assert.Single(await instance.Verdicts.ListAsync(instance.ProjectId, Debate, Ct)).Verdict.Resolution);
    }

    [Fact]
    public async Task The_columns_used_for_filtering_mirror_the_verdict()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var verdict = Valid();

        await instance.Verdicts.AppendAsync(instance.ProjectId, verdict, Ct);

        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"""
            SELECT COUNT(*) FROM verdicts
            WHERE id = 'v1' AND decision = 'Approved' AND confidence_level = 'High' AND requires_human = 0
              AND judge_agent_id = 'judge' AND judge_vendor_id = 'openai' AND requirement_set_hash = '{verdict.Verdict.RequirementSetHash}'
              AND decision_log_head_hash = '{verdict.Verdict.DecisionLogHeadHash}' AND run_id = '{Run}' AND task_id = 'T3'
              AND scope = 'CurrentPlan' AND valid = 1 AND validation_json = '[]' AND supersedes_verdict_id IS NULL
            """));
    }
}
