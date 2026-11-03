using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/14 D-19: the announcement and the review of a memory entry carry an empty run id, so they are never part of a
/// run. Replay and the judge's dossier select entries by run, and leave them out without seeing anything wrong.
/// </summary>
public sealed class MemoryEntriesOutsideRunsTests
{
    private static readonly Actor User = new(ActorKind.User, "manuel");

    private static readonly DossierRequest ForRun = new(JudgingWorld.Project, AuditScope.CurrentPlan, JudgingWorld.Run.Value);

    private static MemoryRecordedPayload Announcement() =>
        MemoryRecordedPayload.Announcing("mem-1", MemoryKind.Decision, MemoryOrigin.System, HashA, $"registro:{JudgingWorld.Run.Value}#3");

    private static async Task RecordMemoryAsync(JudgingWorld world)
    {
        await world.RecordAsync(JudgingWorld.System, DecisionKind.MemoryRecorded, "Memoria anunciada: decisión", Announcement());
        await world.RecordAsync(User, DecisionKind.MemoryReviewed, "Memoria aceptada", MemoryReviewedPayload.Deciding("mem-1", MemoryReview.Accepted, "manuel", null));
    }

    private static JudgeDossierBuilder Builder(JudgingWorld world) =>
        new(world.Recorder, world.Runs, world.Plans, world.Requirements, world.Evidence, new JudgingOptions(world.DossierRoot));

    [Fact]
    public async Task The_entries_of_a_run_never_include_the_memory_entries()
    {
        using var world = await JudgingWorld.CreateAsync();
        var before = RunLog.EntriesOf((await world.LoadLogAsync()).Entries, JudgingWorld.Run.Value);

        await RecordMemoryAsync(world);
        var log = (await world.LoadLogAsync()).Entries;
        var after = RunLog.EntriesOf(log, JudgingWorld.Run.Value);

        Assert.Equal(before.Select(e => e.Hash), after.Select(e => e.Hash));
        Assert.Equal(2, log.Count(e => e.Kind is DecisionKind.MemoryRecorded or DecisionKind.MemoryReviewed));
        Assert.DoesNotContain(after, e => e.Kind is DecisionKind.MemoryRecorded or DecisionKind.MemoryReviewed);
        Assert.Throws<ArgumentException>(() => RunLog.EntriesOf(log, DecisionPayload.NoRun));
    }

    [Fact]
    public async Task Replaying_a_run_reads_the_same_events_with_memory_entries_in_the_log()
    {
        using var world = await JudgingWorld.CreateAsync();
        var problems = new List<string>();
        var before = RunLog.ReplayEvents(RunLog.EntriesOf((await world.LoadLogAsync()).Entries, JudgingWorld.Run.Value), problems);

        await RecordMemoryAsync(world);
        var after = RunLog.ReplayEvents(RunLog.EntriesOf((await world.LoadLogAsync()).Entries, JudgingWorld.Run.Value), problems);

        Assert.Empty(problems);
        Assert.NotEmpty(before);
        Assert.Equal(before.Select(e => e.ToString()), after.Select(e => e.ToString()));
    }

    [Fact]
    public async Task The_dossier_of_a_run_leaves_the_memory_entries_out_and_finds_nothing_wrong()
    {
        using var world = await JudgingWorld.CreateAsync();
        var before = await Builder(world).BuildPackageAsync(ForRun, TestContext.Current.CancellationToken);

        await RecordMemoryAsync(world);
        var after = await Builder(world).BuildPackageAsync(ForRun, TestContext.Current.CancellationToken);

        Assert.Equal(before.Dossier.Decisions.Select(e => e.Hash), after.Dossier.Decisions.Select(e => e.Hash));
        Assert.Equal(before.Dossier.Tasks.Select(t => (t.Id, t.Status)), after.Dossier.Tasks.Select(t => (t.Id, t.Status)));
        Assert.Equal(before.Dossier.Evidence.Select(e => e.Id), after.Dossier.Evidence.Select(e => e.Id));
        Assert.Equal(before.Dossier.ExcludedVendors, after.Dossier.ExcludedVendors);
        Assert.Empty(after.Files.SystemChecks);
        Assert.Empty(after.Dossier.IntegrityProblems);

        // The head is that of the whole log, memory included: the verdict echoes the log it was given.
        Assert.Equal((await world.LoadLogAsync()).HeadHash, after.Dossier.DecisionLogHeadHash);
    }

    [Fact]
    public async Task An_interrupted_run_resumes_by_replay_with_memory_entries_recorded_after_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await first.CompleteAsync(harness)).Phase);

        var system = new Actor(ActorKind.System, "agent-studio");
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, system, DecisionKind.MemoryRecorded, "Memoria anunciada: decisión", Announcement(), cancellationToken: ct);
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, User, DecisionKind.MemoryReviewed, "Memoria aceptada", MemoryReviewedPayload.Deciding("mem-1", MemoryReview.Accepted, "manuel", null), cancellationToken: ct);

        var restarted = harness.CreateOrchestrator();
        var listed = Assert.Single(await restarted.ListRunsAsync(harness.Project, ct));
        Assert.Equal((first.Id, RunPhase.Interrupted), (listed.Id, listed.Phase));
        await using var resumed = await restarted.ResumeAsync(harness.Project, first.Id, ct);
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("run.not-resumable"));
        Assert.True((await harness.Decisions.LoadAsync(harness.Project.ProjectId, ct)).Verification.IsValid);
    }
}
