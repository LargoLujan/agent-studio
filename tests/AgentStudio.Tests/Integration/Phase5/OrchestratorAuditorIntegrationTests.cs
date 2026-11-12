using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// F5-6, the auditor of the orchestrator (docs/15 D-33 to D-35 and D-37, decision 6(a); ADR-0031), on the log of a real run
/// of the engine on SQLite, followed by the monitor of the desktop as the app follows it (its review composed with the real
/// auditor, verdicts, challenges, approval gate and delivery). When the run ends the auditor finds nothing in what the engine
/// wrote, and the run can be delivered. Then the log is tampered with on purpose, the second way of D-37: a chain that still
/// verifies, written with the real recorder, that breaks the protocol. Audited again, the finding is an alert of the auditor
/// and a request to replan, which holds the delivery until the person dismisses it from the monitor (<c>replan.dismiss</c>).
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class OrchestratorAuditorIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The free-form payload of a <c>PermissionDenied</c>: the task whose agent asked, and for what.</summary>
    private sealed record DeniedWrite(TaskId Task, string Path) : DecisionPayload;

    [Fact]
    public async Task On_a_real_run_tampered_on_purpose_the_auditor_raises_an_alert_and_asks_to_replan_which_holds_the_delivery_until_the_monitor_dismisses_it()
    {
        using var repo = Phase5Runs.Repo();
        var person = new ScriptedApprovalPrompt();
        var clis = new Phase5Clis();
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(Phase4Paths.Beside(repo.Root), person, clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        var auditor = app.Get<IOrchestratorAuditService>();
        var delivery = app.Get<IDeliveryService>();
        var monitor = new RunMonitorViewModel(TimeProvider.System, new InlineSynchronizationContext(), delivery)
        {
            Review = new RunReviewServices(auditor, app.Get<IVerdictStore>(), app.Get<IVerdictChallenges>(), app.Get<IApprovalGate>(), app.Get<IRequirementStore>()),
        };

        // A real run, followed by the monitor: when it ends, the auditor checks its log (D-34) before the delivery is asked.
        await using var run = await app.Orchestrator.StartAsync(await app.RunRequestAsync("Añade la función y que el juez la revise.", Requirements(), Phase5Runs.Reviewed(), Phase5Runs.Profile()), Ct);
        monitor.Attach(run, project);
        var result = await run.Completion.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));
        await monitor.Listening.WaitAsync(Phase5Runs.Guard, Ct);
        await monitor.AuditCheck.WaitAsync(Phase5Runs.Guard, Ct);
        await monitor.DeliveryCheck.WaitAsync(Phase5Runs.Guard, Ct);

        // On what the real engine wrote the auditor finds nothing (no false positive), writes nothing, and the run can be delivered.
        Assert.True(monitor.AuditFindings.Count == 0, string.Join("\n", monitor.AuditFindings.Select(f => $"{f.Code}: {f.Message}")));
        Assert.StartsWith(AgentStudio.Desktop.Localization.Texts.Get("Orchestrator_Mon_AuditClean"), monitor.AuditSummary, StringComparison.Ordinal);
        Assert.False(monitor.ReplanPending);
        Assert.True(monitor.DeliverCommand.CanExecute(null), monitor.DeliveryReadiness?.Reason);
        Assert.DoesNotContain(await app.PayloadsAsync<AuditAlertPayload>(run.Id.Value), a => a.Entry.Actor == OrchestratorAudit.Auditor);
        Assert.Empty(await app.PayloadsAsync<ReplanRequestedPayload>(run.Id.Value));

        // Tampered on purpose with the real recorder (D-37): the chain still verifies, but an agent of the run was denied a write.
        var tampered = await app.Get<IDecisionRecorder>().RecordAsync(
            project.ProjectId,
            new Actor(ActorKind.System, "agent-studio"),
            DecisionKind.PermissionDenied,
            "Escritura denegada a T1 en .git/config",
            new DeniedWrite(new TaskId("T1"), ".git/config") { RunId = run.Id.Value },
            cancellationToken: Ct);
        Assert.True((await app.LoadLogAsync()).Verification.IsValid);

        // «Auditar de nuevo»: one finding, of high severity, with its alert and the request to replan, both signed by the auditor in the run.
        await monitor.AuditAgainCommand.ExecuteAsync(null);
        await monitor.DeliveryCheck.WaitAsync(Phase5Runs.Guard, Ct);
        var row = Assert.Single(monitor.AuditFindings);
        Assert.Equal(OrchestratorAudit.PermissionsCode, row.Code);
        Assert.True(monitor.ReplanPending);
        Assert.False(monitor.DeliverCommand.CanExecute(null));
        Assert.Contains("replanificar", monitor.DeliveryReadiness!.Reason, StringComparison.Ordinal);

        var alert = Assert.Single(await app.PayloadsAsync<AuditAlertPayload>(run.Id.Value), a => a.Entry.Actor == OrchestratorAudit.Auditor);
        Assert.Equal(OrchestratorAudit.PermissionsCode, alert.Payload.Code);
        Assert.Equal([new TaskId("T1")], alert.Payload.Tasks);
        Assert.Contains(tampered.Sequence.ToString(CultureInfo.InvariantCulture), alert.Payload.Message, StringComparison.Ordinal);
        var (requested, replan) = Assert.Single(await app.PayloadsAsync<ReplanRequestedPayload>(run.Id.Value));
        Assert.Equal(OrchestratorAudit.Auditor, requested.Actor);
        Assert.Equal([OrchestratorAudit.PermissionsCode], replan.Findings);
        Assert.Equal([new TaskId("T1")], replan.Tasks);
        Assert.True(requested.Sequence > alert.Entry.Sequence);

        // The delivery service says the same, and asks nobody; a pass of the auditor again records nothing (idempotent).
        var held = await delivery.CheckAsync(project, run.Id, Ct);
        Assert.False(held.Ready, held.Reason);
        Assert.Contains(OrchestratorAudit.PermissionsCode, held.Reason, StringComparison.Ordinal);
        var asked = person.Requests.Count;
        Assert.False((await delivery.DeliverAsync(project, run.Id, DeliveryMode.FastForwardOnly, Ct)).Delivered);
        Assert.Equal(asked, person.Requests.Count);
        var again = await auditor.AuditRunAsync(project, run.Id, Ct);
        Assert.Empty(again.Recorded);
        Assert.True(again.ReplanRequested);

        // «Descartar la petición»: the monitor asks the gate (replan.dismiss, in the run), and the person's yes releases the delivery.
        await monitor.DismissReplanCommand.ExecuteAsync(null);
        await monitor.DeliveryCheck.WaitAsync(Phase5Runs.Guard, Ct);
        var dismissal = Assert.Single(person.Requests.Skip(asked));
        Assert.Equal((DeliveryService.ReplanDismissalAction, project.ProjectId, run.Id.Value), (dismissal.Action, dismissal.ProjectId, dismissal.RunId));
        Assert.Contains(
            await app.PayloadsAsync<ApprovalAnsweredPayload>(run.Id.Value),
            a => a.Entry.Kind == DecisionKind.ApprovalGranted && a.Payload.Action == DeliveryService.ReplanDismissalAction && a.Entry.Sequence > requested.Sequence);
        Assert.False(monitor.ReplanPending);
        Assert.True(monitor.DeliverCommand.CanExecute(null), monitor.DeliveryReadiness?.Reason);
        Assert.True((await delivery.CheckAsync(project, run.Id, Ct)).Ready);

        // The findings stay in the log, and a pass after the dismissal asks nothing again.
        var after = await auditor.AuditRunAsync(project, run.Id, Ct);
        Assert.Empty(after.Recorded);
        Assert.Single(after.Findings);
        Assert.True((await delivery.CheckAsync(project, run.Id, Ct)).Ready);
        var log = await app.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }
}
