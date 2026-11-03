using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-34: what a pass leaves to record. A finding is recorded once (same code and message), and a request to
/// replan is due when there is a high or critical finding the auditor has not asked about yet.
/// </summary>
public sealed class OrchestratorAuditRecordingTests
{
    private static int AuditorEntries(PreparedRun run, DecisionKind kind) =>
        run.Entries.Count(e => e.Kind == kind && e.Actor == OrchestratorAudit.Auditor);

    /// <summary>The findings as comparable text: a record compares its lists by reference.</summary>
    private static IReadOnlyList<string> Describe(IEnumerable<OrchestratorFinding> findings) =>
        [.. findings.Select(f => $"{f.Code}|{f.Severity}|{string.Join(",", f.Tasks)}|{string.Join(",", f.Entries)}|{f.Message}")];

    [Fact]
    public async Task Two_passes_over_the_same_run_record_one_alert_and_one_request()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);

        var first = await run.AuditAsync();
        await run.RecordOutcomeAsync(first);
        var second = await run.AuditAsync();
        await run.RecordOutcomeAsync(second);

        Assert.Single(first.NewFindings);
        Assert.NotNull(first.Replan);
        Assert.Equal(Describe(first.Findings), Describe(second.Findings));
        Assert.Empty(second.NewFindings);
        Assert.Null(second.Replan);
        Assert.False(second.HasSomethingToRecord);
        Assert.Equal(1, AuditorEntries(run, DecisionKind.AuditAlertRaised));
        Assert.Equal(1, AuditorEntries(run, DecisionKind.ReplanRequested));
    }

    [Fact]
    public async Task A_high_finding_requests_a_replan_with_its_code_and_tasks()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1", tests: false);
        await run.ReviewAsync("R1");
        await run.FinishAsync();
        await run.DeliverAsync(approve: false);

        var outcome = await run.AuditAsync();

        var replan = Assert.IsType<ReplanRequestedPayload>(outcome.Replan);
        Assert.Equal([OrchestratorAudit.ApprovalMissingCode, OrchestratorAudit.IntegrationUnverifiedCode], replan.Findings);
        Assert.Equal([Tid("I1")], replan.Tasks);
        Assert.Contains(Run.Value, replan.Message, StringComparison.Ordinal);
        Assert.Contains(OrchestratorAudit.ApprovalMissingCode, replan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Medium_findings_alone_request_no_replan()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.LaunchAsync("T1", instructions: false);
        await run.EndAttemptAsync("T1");

        var outcome = await run.AuditAsync();

        var finding = Assert.Single(outcome.NewFindings);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Null(outcome.Replan);
    }

    [Fact]
    public async Task A_request_lost_after_its_alerts_were_recorded_is_asked_for_again()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        var first = await run.AuditAsync();
        await run.RecordOutcomeAsync(first with { Replan = null }); // the process died before the request was written

        var second = await run.AuditAsync();

        Assert.Empty(second.NewFindings);
        Assert.NotNull(second.Replan);
    }

    [Fact]
    public async Task A_new_high_finding_after_a_request_asks_again()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        await run.RecordOutcomeAsync(await run.AuditAsync());
        await run.DeliverAsync(approve: false);

        var second = await run.AuditAsync();

        var added = Assert.Single(second.NewFindings);
        Assert.Equal([run.Last(DecisionKind.IntegrationPerformed)], added.Entries);
        Assert.NotNull(second.Replan);
    }

    [Fact]
    public void The_alert_of_a_finding_carries_its_code_tasks_and_message_in_the_run()
    {
        var finding = new OrchestratorFinding(OrchestratorAudit.PermissionsCode, Severity.High, [Tid("T1")], [42], "message");

        var alert = OrchestratorAudit.AlertOf(finding, Run);

        Assert.Equal(OrchestratorAudit.PermissionsCode, alert.Code);
        Assert.Equal([Tid("T1")], alert.Tasks);
        Assert.Equal("message", alert.Message);
        Assert.Equal(Run.Value, alert.RunId);
        Assert.Equal(new Actor(ActorKind.Auditor, "auditor-orquestador"), OrchestratorAudit.Auditor);
    }

    [Fact]
    public async Task An_alert_of_another_actor_with_the_same_code_and_message_does_not_count_as_recorded()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        var finding = Assert.Single((await run.AuditAsync()).Findings);
        await run.RecordAsync(PreparedRun.System, DecisionKind.AuditAlertRaised, "Otra alerta", OrchestratorAudit.AlertOf(finding, Run));

        var outcome = await run.AuditAsync();

        Assert.Single(outcome.NewFindings);
    }

    [Fact]
    public async Task The_auditor_never_counts_its_own_entries_as_part_of_the_run_it_audits()
    {
        var run = await PreparedRun.CleanAsync();
        await run.TransitionAsync("T1", WorkTaskStatus.Completed, WorkTaskStatus.Pending); // a replay finding
        var first = await run.AuditAsync();
        await run.RecordOutcomeAsync(first);

        var second = await run.AuditAsync();

        Assert.NotEmpty(first.Findings);
        Assert.Equal(Describe(first.Findings), Describe(second.Findings));
        Assert.Empty(second.NewFindings);
    }
}
