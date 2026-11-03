using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-34: the service of the auditor reads the run from the stores, audits it and records what is new: an
/// <c>AuditAlertRaised</c> per finding (actor <c>Auditor</c>, <c>auditor-orquestador</c>) and the <c>ReplanRequested</c>.
/// It never writes on a log that does not verify, and a second pass writes nothing.
/// </summary>
public sealed class OrchestratorAuditServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectContext Project => new(AuditWorld.Project, @"C:\repo");

    private static OrchestratorAuditService Service(PreparedRun run) => new(run.Recorder, run.Runs, run.Plans, run.Requirements, run.Evidence);

    private static IReadOnlyList<DecisionEntry> ByAuditor(PreparedRun run) => [.. run.Entries.Where(e => e.Actor == OrchestratorAudit.Auditor)];

    [Fact]
    public async Task A_run_kept_to_the_protocol_is_reported_clean_and_nothing_is_recorded()
    {
        var run = await PreparedRun.CleanAsync();
        var before = run.Entries.Count;

        var report = await Service(run).AuditRunAsync(Project, Run, Ct);

        Assert.True(report.Log.IsValid);
        Assert.Empty(report.Findings);
        Assert.Empty(report.Recorded);
        Assert.False(report.ReplanRequested);
        Assert.Equal(before, run.Entries.Count);
    }

    [Fact]
    public async Task A_high_finding_is_recorded_as_an_alert_of_the_auditor_and_a_request_to_replan()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);

        var report = await Service(run).AuditRunAsync(Project, Run, Ct);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(OrchestratorAudit.ApprovalMissingCode, finding.Code);
        Assert.True(report.ReplanRequested);
        var written = ByAuditor(run);
        Assert.Equal([.. written.Select(e => e.Sequence)], report.Recorded);
        Assert.Equal([DecisionKind.AuditAlertRaised, DecisionKind.ReplanRequested], written.Select(e => e.Kind));
        var alert = DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(written[0].PayloadJson);
        Assert.Equal((finding.Code, finding.Message, Run.Value), (alert.Code, alert.Message, alert.RunId));
        var replan = DecisionPayloadSerializer.Deserialize<ReplanRequestedPayload>(written[1].PayloadJson);
        Assert.Equal([OrchestratorAudit.ApprovalMissingCode], replan.Findings);
        Assert.Equal(Run.Value, replan.RunId);
        Assert.Null(replan.Verdict);
    }

    [Fact]
    public async Task A_second_pass_records_nothing_and_still_says_the_run_has_a_request_to_replan()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        var first = await Service(run).AuditRunAsync(Project, Run, Ct);

        var second = await Service(run).AuditRunAsync(Project, Run, Ct);

        Assert.Equal(first.Findings.Select(f => f.Message), second.Findings.Select(f => f.Message));
        Assert.Empty(second.Recorded);
        Assert.True(second.ReplanRequested);
        Assert.Equal(2, ByAuditor(run).Count);
    }

    [Fact]
    public async Task A_medium_finding_is_recorded_without_a_request_to_replan()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.LaunchAsync("T1", instructions: false);
        await run.EndAttemptAsync("T1");

        var report = await Service(run).AuditRunAsync(Project, Run, Ct);

        Assert.Equal(Severity.Medium, Assert.Single(report.Findings).Severity);
        Assert.Single(report.Recorded);
        Assert.False(report.ReplanRequested);
    }

    [Fact]
    public async Task On_a_log_that_does_not_verify_the_report_is_the_verification_and_nothing_is_written()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        var edited = run.Entries.First(e => e.Kind == DecisionKind.TaskAssigned && e.PayloadJson.Contains("ejecutor", StringComparison.Ordinal));
        run.Store.Tamper(AuditWorld.Project, edited.Sequence, e => e with { PayloadJson = e.PayloadJson.Replace("ejecutor", "juez", StringComparison.Ordinal) });
        var before = run.Entries.Count;

        var report = await Service(run).AuditRunAsync(Project, Run, Ct);

        Assert.False(report.Log.IsValid);
        Assert.Empty(report.Findings);
        Assert.Empty(report.Recorded);
        Assert.False(report.ReplanRequested);
        Assert.Equal(before, run.Entries.Count);
    }

    [Fact]
    public async Task Evidence_the_store_cannot_open_is_a_finding_not_a_failure()
    {
        var run = await PreparedRun.CleanAsync();
        var prompt = run.Entries.Select(AuditedPayload).OfType<InstructionsIssuedPayload>().Single().PromptEvidenceId;
        run.Evidence.Unreadable.Add(prompt);

        var report = await Service(run).AuditRunAsync(Project, Run, Ct);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(OrchestratorAudit.InstructionsCode, finding.Code);
        Assert.Contains("cannot be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_the_log_never_started_cannot_be_audited()
    {
        var run = await PreparedRun.CleanAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(run).AuditRunAsync(Project, new RunId("20261019-ffffff"), Ct));
    }

    private static DecisionPayload? AuditedPayload(DecisionEntry entry) => DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
}
