using System.IO;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using JudgeData = AgentStudio.Tests.Debate.Fakes.PromptData;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// F5-5, «Auditar proyecto completo» (docs/15 D-28 to D-31, ADR-0030), on the real modules: the commit HEAD of a temporary git
/// repository, the trusted profile the user saved in <c>.agentstudio/verification.json</c>, the real verifier (a PowerShell
/// script of the repository run at Low integrity on its own copy of HEAD), SQLite, and Codex's judge (scripted) reading an
/// index dossier in a read-only copy of HEAD. The log already has a run of Claude that failed: Anthropic produced code
/// (rule P, with the declared authors), and the report lists that run among the pending problems. Then the log is edited
/// behind its triggers: no judge is launched and nothing is written.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ProjectAuditIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_audit_of_the_whole_project_verifies_head_reports_evidence_and_pending_problems_and_with_a_broken_log_launches_no_judge()
    {
        // HEAD has the feature the check looks for: the isolated verification of HEAD passes.
        using var repo = Phase5Runs.Repo();
        repo.Write("src/feature.txt", "función de la base\n");
        repo.Run("add", "-A");
        repo.Run("commit", "-q", "-m", "La función");
        var head = repo.Head;
        var paths = Phase4Paths.Beside(repo.Root);
        var person = new ScriptedApprovalPrompt();
        var clis = new Phase5Clis { Executor = (_, _) => AgentScript.Failing("El CLI se cayó a mitad de la tarea.") };
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(paths, person, clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        var audits = app.Get<IProjectAuditService>();

        // The trusted profile: the isolated check, and a step without isolation that an audit never runs (D-27).
        var marker = Path.Combine(repo.Root, "no-debe-existir.txt");
        var profile = new VerificationProfile(
            "auditoría",
            [
                Phase3Repos.ScriptStep("check", "Comprobar la función", "check.ps1", TimeSpan.FromMinutes(3)),
                new VerificationStep("sin-aislar", "Paso con tus permisos", "pwsh", ["-NoProfile", "-NonInteractive", "-Command", "Set-Content -LiteralPath no-debe-existir.txt -Value x"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1), ProcessIsolation.None),
            ]);
        await app.Get<IVerificationProfileSource>().SaveAsync(project, profile, Ct);

        // A pending problem the report must list: a run of Claude whose executor failed every attempt.
        var failed = await Phase5Runs.RunAsync(app, Plan(Work("T1", scope: ["src/"])), "Añade otra función.");
        Assert.True(failed.Phase == RunPhase.Failed, Phase4Checks.Describe(failed));

        // Rule P (D-28, never waived): Claude's judge cannot judge what Anthropic produced and signs.
        var request = new ProjectAuditRequest(project, Requirements().ContentHash, Phase5Roster.CodexJudge, [VendorId.Anthropic]);
        var claude = await audits.PrepareAsync(request with { Judge = Phase5Roster.ClaudeJudge }, Ct);
        Assert.False(claude.CanJudge);
        Assert.Contains(claude.Problems, p => p.Contains("La regla de los productores no se dispensa", StringComparison.Ordinal));

        // Preparing writes nothing; it shows HEAD, the producers of the log and the authors declared, what is skipped and the estimate.
        var entries = (await app.LoadLogAsync()).Entries.Count;
        var proposal = await audits.PrepareAsync(request, Ct);
        Assert.True(proposal.CanJudge, string.Join(" | ", proposal.Problems));
        Assert.Equal((head, Phase5Roster.CodexJudge), (proposal.HeadCommit, proposal.Judge!.Id));
        Assert.Equal([VendorId.Anthropic], proposal.ProducerVendors);
        Assert.Equal(["sin-aislar"], proposal.SkippedSteps);
        Assert.Equal((ProjectAuditService.EstimatedJudgeTokens, MetricOrigin.EstimatedBySystem), (proposal.EstimatedTokens.Value!.Value, proposal.EstimatedTokens.Origin));
        Assert.Equal(entries, (await app.LoadLogAsync()).Entries.Count);

        var report = await audits.AuditAsync(proposal, null, Ct);

        // The report: an approval of a valid verdict on an intact log, about HEAD.
        Assert.True(report.IsTrustworthy, report.Verdict.Summary + " | " + string.Join(" | ", report.ValidationIssues.Select(v => v.Message)));
        Assert.Equal((AuditScope.WholeProject, head, VerdictDecision.Approved, ReviewDisposition.Approved, true, false), (report.Scope, report.HeadCommit, report.Verdict.Decision, report.Disposition, report.DecisionLogIntact, report.Corrected));
        Assert.Equal(VendorId.OpenAI, report.Verdict.Judge.Model.Vendor);

        // Its evidence: the isolated verification of HEAD by the real verifier, captured by the system and bound to HEAD;
        // the step without isolation never ran.
        var tests = Assert.Single(report.Verification, e => e.Kind == EvidenceKind.TestRun);
        Assert.Equal((EvidenceOrigin.CapturedBySystem, head), (tests.Origin, tests.SubjectCommit));
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 0, Skipped: 0, ExitCode: 0), tests.TestRun);
        Assert.DoesNotContain(report.Verification, e => e.Kind == EvidenceKind.CommandOutput);
        Assert.False(File.Exists(marker), "A step without isolation ran in the audit.");
        var verdict = Assert.Single(await app.Get<IVerdictStore>().ListAsync(project.ProjectId, report.Audit.Value, Ct));
        Assert.Equal((report.VerdictId, true, AuditScope.WholeProject), (verdict.Id, verdict.Valid, verdict.Scope));
        Assert.Contains(tests.Id, verdict.Verdict.Assessments.Single(a => a.Requirement.Value == "RQ-01").Evidence);

        // The pending problems, from the log: the run that failed.
        var pending = Assert.Single(report.Pending, p => p.Code == PendingProblems.RunFailedCode);
        Assert.Equal(failed.Id.Value, pending.Source);

        // The log of the audit, in order: the approval, the request, the anchored header, the verification, the judge and the end.
        var unit = report.Audit.Value;
        Assert.StartsWith(AuditIds.Prefix, unit, StringComparison.Ordinal);
        var log = (await Phase5Checks.EntriesOfAsync(app, unit)).Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson))).ToList();
        int First(Func<(DecisionEntry Entry, DecisionPayload? Payload), bool> what) => log.FindIndex(e => what(e));
        int[] order =
        [
            First(e => e.Payload is ApprovalAnsweredPayload { Action: ProjectAuditService.ApprovalAction, Approved: true }),
            First(e => e.Payload is EvidenceRegisteredPayload { Kind: EvidenceKind.Document } r && r.Title == ProjectAuditService.RequestTitle),
            First(e => e.Payload is ProjectAuditStartedPayload),
            First(e => e.Payload is EvidenceRegisteredPayload { Kind: EvidenceKind.TestRun }),
            First(e => e.Payload is EvidenceRegisteredPayload r && r.Title == ProjectAuditService.IndexTitle),
            First(e => e.Payload is AgentRunStartedPayload { Purpose: AgentRunPurpose.ProjectAudit }),
            First(e => e.Payload is VerdictIssuedPayload),
            First(e => e.Payload is ProjectAuditFinishedPayload),
        ];
        Assert.True(order.All(i => i >= 0) && order.SequenceEqual(order.Order()), "The audit's entries are not in order: " + string.Join(", ", order) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));
        var started = (ProjectAuditStartedPayload)log[order[2]].Payload!;
        var header = (await app.Get<IProjectAuditRegistry>().FindAsync(project.ProjectId, report.Audit, Ct))!;
        Assert.Equal(DebateHeaderHash.Of(header), started.HeaderHash);
        Assert.Equal((head, Requirements().ContentHash, profile.ContentHash), (started.HeadCommit, started.RequirementSetHash, started.VerificationProfileHash));
        Assert.Equal([VendorId.Anthropic], started.ProducerVendors);
        Assert.Equal([VendorId.Anthropic], started.DeclaredAuthors);
        var launch = (AgentRunStartedPayload)log[order[5]].Payload!;
        Assert.Equal(("juez-juez-codex", ProviderId.CodexCli, VendorId.OpenAI), (launch.Task!.Value.Value, launch.Provider, launch.Vendor));
        var finished = (ProjectAuditFinishedPayload)log[order[7]].Payload!;
        Assert.Equal([report.VerdictId!], finished.Verdicts);
        Assert.Equal((ReviewDisposition.Approved, true), (finished.Disposition, finished.Trustworthy));

        // The judge read an index of the project (R8) in a read-only copy of HEAD, which is gone now; its run is in the journal.
        var judge = Assert.Single(clis.LaunchesOf(unit));
        var index = JudgeData.Dossier(judge.Prompt);
        Assert.Equal((ProjectDossierBuilder.Format, head), (index.GetProperty("format").GetString(), index.GetProperty("headCommit").GetString()));
        Assert.Contains(index.GetProperty("pendingProblems").EnumerateArray(), p => p.GetProperty("code").GetString() == PendingProblems.RunFailedCode);
        Assert.Equal(["sin-aislar"], index.GetProperty("skippedSteps").EnumerateArray().Select(s => s.GetString()));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(paths.Root, "audits")), Path.GetFullPath(judge.WorkingDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(judge.WorkingDirectory), "The copy of HEAD the judge read was not removed.");
        Assert.False(judge.Permissions.AllowFileWrites || judge.Permissions.AllowShell || judge.Permissions.AllowNetwork);
        Assert.Contains(Phase5Checks.JournalLines(repo.Root), l => l.Contains("#auditoria", StringComparison.Ordinal) && l.Contains(unit, StringComparison.Ordinal));

        // ---- The log is edited behind its triggers (D-37, the first kind): the chain breaks.
        long rows;
        long headers;
        var database = app.Get<ProjectDatabases>().Get(project.ProjectId).DatabasePath;
        using (var connection = SqliteSchema.Open(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            PersistenceTestData.Exec(connection, "DROP TRIGGER decision_log_no_update");
            PersistenceTestData.Exec(connection, "UPDATE decision_log SET summary = 'Entrada falsificada' WHERE sequence = 3");
            PersistenceTestData.Exec(connection, "CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END;");
            rows = PersistenceTestData.Scalar(connection, "SELECT COUNT(*) FROM decision_log");
            headers = PersistenceTestData.Scalar(connection, "SELECT COUNT(*) FROM project_audits");
        }

        Assert.False((await app.LoadLogAsync()).Verification.IsValid);
        var launches = clis.Launches.Count;
        var asked = person.Requests.Count;

        // Preparing says so, and auditing, even with the proposal approved before, launches no judge and writes nothing.
        var broken = await audits.PrepareAsync(request, Ct);
        Assert.False(broken.CanJudge);
        Assert.Contains(broken.Problems, p => p.Contains("no está íntegro", StringComparison.Ordinal));
        var unjudged = await audits.AuditAsync(proposal, null, Ct);
        Assert.Equal((false, (string?)null, false, VerdictDecision.EscalateToHuman), (unjudged.DecisionLogIntact, unjudged.VerdictId, unjudged.IsTrustworthy, unjudged.Verdict.Decision));
        Assert.Contains("entrada 3", unjudged.Verdict.Summary, StringComparison.Ordinal);
        Assert.Empty(unjudged.Verification);
        Assert.Contains(unjudged.Pending, p => p.Code == PendingProblems.RunFailedCode && p.Source == failed.Id.Value);
        Assert.Equal((launches, asked), (clis.Launches.Count, person.Requests.Count));
        using (var connection = SqliteSchema.Open(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            Assert.Equal(rows, PersistenceTestData.Scalar(connection, "SELECT COUNT(*) FROM decision_log"));
            Assert.Equal(headers, PersistenceTestData.Scalar(connection, "SELECT COUNT(*) FROM project_audits"));
        }
    }
}
