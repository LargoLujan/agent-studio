using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The engine and the delivery read the whole log at their critical moments (ADR-0010, addendum of 2026-11-04): starting
/// a run, resuming it, planning and checking a delivery ask for <see cref="LogLoad.Full"/>, so a change no load from the
/// tail can see is found there. Listing and inspecting runs, which launch nothing on the log, read it from its tail.
/// </summary>
public sealed class CriticalLogLoadsTests : IAsyncLifetime
{
    private static readonly RunId Run = new("20261104-a1b2c3");

    private readonly EngineHarness _harness = new();
    private readonly LoadSpyRecorder _spy;
    private readonly Orchestrator _engine;

    /// <summary>
    /// The harness's log, read through a recorder whose anchors know nothing of it: once the log has an entry
    /// (<see cref="InitializeAsync"/>) every load is verified and refused, so each call stops right after the load it asked for.
    /// </summary>
    public CriticalLogLoadsTests()
    {
        _spy = new LoadSpyRecorder(new DecisionRecorder(_harness.LogStore, _harness.Time, new MemoryDecisionLogAnchors()));
        _engine = new Orchestrator(
            _spy, _harness.Evidence, _harness.Plans, _harness.Runs, _harness.RequirementStore, _harness.Roster, _harness.Providers, _harness.Worktrees,
            _harness.Verification, _harness.Integrator, _harness.Review, _harness.Approvals, _harness.Skills, _harness.Paths, _harness.Time, _harness.WriteConfinement);
    }

    public async ValueTask InitializeAsync() =>
        await _harness.Decisions.RecordAsync(
            _harness.Project.ProjectId,
            new Actor(ActorKind.System, "agent-studio"),
            DecisionKind.AuditAlertRaised,
            "Una alerta",
            new AuditAlertPayload("prueba", [], "Una alerta") { RunId = Run.Value },
            cancellationToken: Ct);

    public ValueTask DisposeAsync()
    {
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Starting_a_run_reads_the_whole_log()
    {
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => _engine.StartAsync(_harness.Request(Plan(Work("T1"))), Ct));

        Assert.Equal([LogLoad.Full], _spy.Loads);
        Assert.Empty(_harness.Claude.Starts);
    }

    [Fact]
    public async Task Resuming_a_run_reads_the_whole_log()
    {
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => _engine.ResumeAsync(_harness.Project, Run, Ct));

        Assert.Equal([LogLoad.Full], _spy.Loads);
        Assert.Empty(_harness.Claude.Starts);
    }

    [Fact]
    public async Task Planning_reads_the_whole_log()
    {
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => _engine.PlanAsync(_harness.PlanningRequest(), null, Ct));

        Assert.Equal([LogLoad.Full], _spy.Loads);
        Assert.Empty(_harness.Claude.Starts);
    }

    [Fact]
    public async Task Checking_a_delivery_reads_the_whole_log()
    {
        var delivery = new DeliveryService(_harness.Worktrees, _harness.Approvals, _spy, new FakeVerdictStore());

        var readiness = await delivery.CheckAsync(_harness.Project, Run, Ct);

        Assert.False(readiness.Ready);
        Assert.Contains("no está íntegro", readiness.Reason, StringComparison.Ordinal);
        Assert.Equal([LogLoad.Full], _spy.Loads);
    }

    [Fact]
    public async Task Listing_and_inspecting_runs_read_the_log_from_its_tail()
    {
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => _engine.ListRunsAsync(_harness.Project, Ct));
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => _engine.InspectAsync(_harness.Project, Run, Ct));

        Assert.Equal([LogLoad.Incremental, LogLoad.Incremental], _spy.Loads);
    }
}
