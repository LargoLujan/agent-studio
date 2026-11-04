using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// A change to the log that leaves the stamp of the store as it was (<see cref="StampBlindChange"/>) is found at the next
/// critical moment, on the real stores and the real anchor (ADR-0010, addendum of 2026-11-04): the delivery is not ready, the
/// dossier of the judge says the log does not verify, and neither a run starts nor a planning begins. Until then an
/// ordinary load serves the log it verified, without the change.
/// </summary>
public sealed class CriticalMomentsTests : IDisposable
{
    private const string RestoreNoUpdate = "CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END;";

    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    /// <summary>The critical moments a change is looked for at.</summary>
    public enum Moment
    {
        Delivery,
        JudgeDossier,
        RunStart,
        Planning,
    }

    public static TheoryData<StampBlindChange, Moment> Cases()
    {
        var cases = new TheoryData<StampBlindChange, Moment>();
        foreach (var change in Enum.GetValues<StampBlindChange>())
        {
            foreach (var moment in Enum.GetValues<Moment>())
            {
                cases.Add(change, moment);
            }
        }

        return cases;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_change_the_stamp_cannot_see_is_found_at_the_next_critical_moment(StampBlindChange change, Moment moment)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root, anchorFolder: _anchors);
        var header = await StartedRunAsync(instance);
        var builder = new JudgeDossierBuilder(instance.Recorder, instance.Runs, instance.Plans, instance.Requirements, instance.Evidence, new JudgingOptions(Path.Combine(_folder.DataDirectory, "dossiers-de-prueba")));
        var forRun = new DossierRequest(instance.ProjectId, AuditScope.CurrentPlan, header.Id.Value);
        Assert.Empty((await builder.BuildAsync(forRun, Ct)).IntegrityProblems);

        StampBlindTampering.Apply(instance, change, "DROP TRIGGER decision_log_no_update;", "UPDATE decision_log SET summary = 'Cambiada' WHERE sequence = 2;", RestoreNoUpdate);
        var unseen = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        Assert.True(unseen.Verification.IsValid);
        Assert.Equal(nameof(DecisionKind.EvidenceRegistered), unseen.Entries[1].Summary);

        using var harness = new EngineHarness();
        var engine = new Orchestrator(
            instance.Recorder, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees,
            harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement);
        switch (moment)
        {
            case Moment.Delivery:
                var readiness = await new DeliveryService(harness.Worktrees, harness.Approvals, instance.Recorder, instance.Verdicts).CheckAsync(instance.Project, header.Id, Ct);
                Assert.False(readiness.Ready);
                Assert.Contains("no está íntegro (entrada 2)", readiness.Reason, StringComparison.Ordinal);
                break;
            case Moment.JudgeDossier:
                Assert.Contains((await builder.BuildAsync(forRun, Ct)).IntegrityProblems, p => p.StartsWith("The decision log does not verify (entry 2)", StringComparison.Ordinal));
                break;
            case Moment.RunStart:
                var start = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.StartAsync(harness.Request(Plan(Work("T1"))) with { Project = instance.Project }, Ct));
                Assert.Equal(2, start.Verification.FirstInvalidSequence);
                break;
            case Moment.Planning:
                var planning = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.PlanAsync(harness.PlanningRequest() with { Project = instance.Project }, null, Ct));
                Assert.Equal(2, planning.Verification.FirstInvalidSequence);
                break;
        }

        // Found once, the change is what every load sees from then on, and nothing is written on it.
        var after = await instance.Recorder.LoadAsync(instance.ProjectId, Ct);
        Assert.False(after.Verification.IsValid);
        Assert.Equal(2, after.Verification.FirstInvalidSequence);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => instance.Recorder.RecordAsync(instance.ProjectId, System, DecisionKind.AuditAlertRaised, "no", new AuditAlertPayload("prueba", [], "no") { RunId = header.Id.Value }, cancellationToken: Ct));
        Assert.Empty(harness.Claude.Starts);
    }

    /// <summary>A run as the engine leaves it once started: its header stored, <c>RunStarted</c> anchoring it and the original request registered, then two more entries.</summary>
    private static async Task<RunRecord> StartedRunAsync(ProjectInstance instance)
    {
        var header = await SeedRunAsync(instance);
        await instance.Runs.CreateAsync(header, Ct);
        var run = header.Id.Value;

        async Task RecordAsync(DecisionKind kind, DecisionPayload payload)
        {
            instance.Clock.Advance(TimeSpan.FromSeconds(1));
            await instance.Recorder.RecordAsync(instance.ProjectId, System, kind, kind.ToString(), payload, cancellationToken: Ct);
        }

        await RecordAsync(
            DecisionKind.RunStarted,
            new RunStartedPayload(
                header.PlanHash,
                header.RequirementSetHash,
                header.BaseCommit,
                header.BaseRef,
                header.Verification.ContentHash,
                [.. header.Roster.Select(a => new RosterEntry(a.Id, a.Role, a.Model.Provider, a.Model.Vendor))],
                null,
                RunHeaderHash.Of(header))
            {
                RunId = run,
                V = RunStartedPayload.HeaderVersion,
            });
        var request = Assert.Single(await instance.Evidence.ListByRunAsync(instance.ProjectId, run, Ct));
        await RecordAsync(DecisionKind.EvidenceRegistered, EvidenceRegisteredPayload.Anchoring(request, run, null, null));
        await RecordAsync(DecisionKind.AuditAlertRaised, new AuditAlertPayload("prueba", [], "Una alerta") { RunId = run });
        await RecordAsync(DecisionKind.AuditAlertRaised, new AuditAlertPayload("prueba", [], "Otra alerta") { RunId = run });
        return header;
    }
}
