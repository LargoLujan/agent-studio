using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The engine on a history no anchor recognises at the project's location (ADR-0010, addendum of 2026-10-18): a folder
/// moved or copied, or a log written on another machine. It plans, starts, resumes and lists nothing, and appends nothing
/// to the log, until a person accepts the history as a new origin.
/// </summary>
public sealed class UnanchoredHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unanchored_history_blocks_planning_starting_resuming_and_listing_until_a_person_accepts_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);
        var written = harness.Entries.Count;

        // The same log, now checked against the anchors of this machine, where it has none.
        var anchors = new MemoryDecisionLogAnchors();
        var recorder = new DecisionRecorder(harness.LogStore, harness.Time, anchors);
        var engine = new Orchestrator(
            recorder, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees,
            harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement);

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.ListRunsAsync(harness.Project, Ct));
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.StartAsync(harness.Request(Plan(Work("T2"))), Ct));
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.ResumeAsync(harness.Project, first.Id, Ct));
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => engine.PlanAsync(harness.PlanningRequest(), null, Ct));
        Assert.Equal(written, harness.Entries.Count);
        Assert.Empty(anchors.Written);
        Assert.Empty(harness.Claude.Starts.Skip(1));

        var history = (await recorder.LoadAsync(harness.Project.ProjectId, Ct)).Unanchored!;
        var binding = history.Binding(harness.Project.ProjectId);
        await recorder.AcceptAsNewOriginAsync(
            harness.Project.ProjectId,
            new ApprovalRequest(UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical, null, [harness.RepositoryRoot], harness.Project.ProjectId, Binding: binding),
            new ApprovalDecision(true, "user", harness.Time.GetUtcNow(), null, binding),
            Ct);

        var runs = await engine.ListRunsAsync(harness.Project, Ct);
        Assert.Equal(first.Id, Assert.Single(runs).Id);
        Assert.Equal(written + 3, harness.Entries.Count);
    }
}
