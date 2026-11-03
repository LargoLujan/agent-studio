using System.Globalization;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// The attack of N-04 against the real stores: the log and its anchor stay intact while the row of an evidence is
/// rewritten behind the triggers. What decides an approval (the commit a test run verified, its counters, its date) is
/// anchored in the log by <c>EvidenceRegistered</c> version 2, so the rewritten row never reaches the validator as proof.
/// </summary>
public sealed class EvidenceAnchoringAttackTests : IDisposable
{
    private static readonly Actor System = new(ActorKind.System, "agent-studio");
    private readonly TempProjectFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task An_old_green_test_run_retargeted_to_the_candidate_commit_behind_the_trigger_cannot_support_an_approval()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedRunAsync(instance);
        await instance.Runs.CreateAsync(header, Ct);
        var run = header.Id.Value;

        async Task RecordAsync(DecisionKind kind, DecisionPayload payload)
        {
            instance.Clock.Advance(TimeSpan.FromSeconds(1));
            await instance.Recorder.RecordAsync(instance.ProjectId, System, kind, kind.ToString(), payload, cancellationToken: Ct);
        }

        async Task<EvidenceRecord> TestRunAsync(TestRunSummary summary, string commit)
        {
            instance.Clock.Advance(TimeSpan.FromMinutes(1));
            using var content = new MemoryStream(Bytes($"<TestRun commit=\"{commit}\"/>"));
            var record = await instance.Evidence.CaptureAsync(
                instance.ProjectId,
                new EvidenceDraft(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test", "agent-studio:verification", summary, RunId: run, TaskId: "T3", Attempt: 1, MediaType: "text/plain", SubjectCommit: commit),
                content,
                Ct);
            await RecordAsync(DecisionKind.EvidenceRegistered, EvidenceRegisteredPayload.Anchoring(record, run, Tid("T3"), 1));
            return record;
        }

        await RecordAsync(DecisionKind.RunStarted, new RunStartedPayload(header.PlanHash, header.RequirementSetHash, header.BaseCommit, header.BaseRef, header.Verification.ContentHash, [], null, RunHeaderHash.Of(header)) { RunId = run, V = RunStartedPayload.HeaderVersion });
        var request = Assert.Single(await instance.Evidence.ListByRunAsync(instance.ProjectId, run, Ct));
        await RecordAsync(DecisionKind.EvidenceRegistered, EvidenceRegisteredPayload.Anchoring(request, run, null, null));
        var old = await TestRunAsync(Green, CommitA);
        await TestRunAsync(Red, CommitB);
        await RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(Tid("T3"), "agentstudio/run-" + run, [$"refs/agentstudio/{run}/T1"], CommitB, [], false) { RunId = run });

        var builder = new JudgeDossierBuilder(instance.Recorder, instance.Runs, instance.Plans, instance.Requirements, instance.Evidence, new JudgingOptions(Path.Combine(_folder.DataDirectory, "dossiers-de-prueba")));
        var forRun = new DossierRequest(instance.ProjectId, AuditScope.CurrentPlan, run);
        var honest = await builder.BuildAsync(forRun, Ct);
        Assert.Empty(honest.IntegrityProblems);
        Assert.Contains(VerdictValidator.Validate(Approval(honest, old.Id), honest), v => v.Code == VerdictViolationCode.ApprovedWithFailingTests);

        // Without the trigger the old green run becomes the latest run of the commit under review; then the trigger is back.
        var later = T0.AddHours(1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER evidence_no_update");
            Exec(connection, $"UPDATE evidence SET subject_commit = '{CommitB}', test_passed = 12, test_failed = 0, test_exit_code = 0, captured_at = '{later}' WHERE id = '{old.Id.Value}'");
            Exec(connection, "CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence BEGIN SELECT RAISE(ABORT, 'evidence is append-only'); END;");
        }

        var tampered = await builder.BuildAsync(forRun, Ct);

        Assert.True((await instance.Recorder.LoadAsync(instance.ProjectId, Ct)).Verification.IsValid);
        Assert.DoesNotContain(old.Id, tampered.Evidence.Select(e => e.Id));
        var problem = Assert.Single(tampered.IntegrityProblems);
        Assert.Contains(old.Id.Value, problem, StringComparison.Ordinal);
        Assert.Contains("subjectCommit", problem, StringComparison.Ordinal);
        Assert.Contains(VerdictValidator.Validate(Approval(tampered, old.Id), tampered), v => v.Code == VerdictViolationCode.ApprovedWithIntegrityProblems);
    }

    /// <summary>An approval that satisfies every mandatory requirement with <paramref name="proof"/>, echoing the dossier's hashes.</summary>
    private static Verdict Approval(JudgeDossier dossier, EvidenceId proof) => new(
        VerdictDecision.Approved,
        "Cumple los requisitos obligatorios.",
        [.. dossier.Requirements.Requirements.Select(r => r.Priority == RequirementPriority.Mandatory
            ? new RequirementAssessment(r.Id, AssessmentStatus.Satisfied, [proof], "La ejecución de pruebas del sistema lo prueba.")
            : new RequirementAssessment(r.Id, AssessmentStatus.NotVerifiable, [], "No hay evidencia objetiva."))],
        [proof],
        [],
        [],
        [],
        new Confidence(ConfidenceLevel.High, "Tests del sistema en verde."),
        [],
        false,
        Judge(),
        dossier.Requirements.ContentHash,
        dossier.DecisionLogHeadHash,
        T0);
}
