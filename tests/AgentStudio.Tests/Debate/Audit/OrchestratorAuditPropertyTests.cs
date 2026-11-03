using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-37 over the runs the engine really records: random plans the validator accepts, driven to their end by the
/// engine with every port faked and every outcome scripted from a seed (<see cref="RandomWorld"/>: retries, failures,
/// conditions and skips, merge points, integrations and conflicts, reviews of every disposition, approvals given and
/// denied, cancellations, the run budget). Audited at every rest and at the end, a run kept to the protocol gives no
/// finding. The only finding such a world can give is one the auditor must give: the scripted agent of a read-only task
/// that writes anyway (a CLI confines its writes; the scripted one does not), which is a change without permission.
/// </summary>
public sealed class OrchestratorAuditPropertyTests
{
    private const int Worlds = 120;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_runs_the_engine_records_give_no_finding_but_the_writes_of_read_only_tasks()
    {
        // Two sources: the worlds stay the same whatever the driver drew for the rests of the ones before (their number
        // depends on how the work interleaved).
        var worlds = new Random(26101901);
        var driver = new Random(26101902);
        var (audits, succeeded, cancelled, readOnlyWrites) = (0, 0, 0, 0);
        for (var i = 0; i < Worlds; i++)
        {
            var world = RandomWorld.Generate(worlds);
            using var harness = new EngineHarness(world.ClaudeLimit, world.CodexLimit);
            world.Wire(harness);
            await using var run = await harness.StartAsync(world.Plan, world.Options);
            var context = $"world {i}\n{world.Describe()}";
            var result = await DriveAsync(harness, run, driver, async () =>
            {
                audits++;
                readOnlyWrites += Check(await AuditAsync(harness, run.Id), world, context + "\n(at rest)", harness);
            });

            audits++;
            readOnlyWrites += Check(await AuditAsync(harness, run.Id), world, context, harness);
            succeeded += result.Phase == RunPhase.Succeeded ? 1 : 0;
            cancelled += result.Phase == RunPhase.Cancelled ? 1 : 0;
        }

        var coverage = $"audits {audits}, succeeded {succeeded}, cancelled {cancelled}, writes of read-only tasks found {readOnlyWrites}";
        TestContext.Current.TestOutputHelper?.WriteLine(coverage);
        Assert.True(audits > Worlds && succeeded > 20 && cancelled > 0, coverage);
    }

    /// <summary>What the property allows, found on the engine's own records: a read-only task whose agent wrote anyway.</summary>
    [Fact]
    public async Task The_write_of_a_read_only_task_is_found_on_the_records_the_engine_leaves()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T2", 1, new AgentScript { Files = new Dictionary<string, string> { ["fuera/T2.txt"] = "fuera" } });
        var plan = OrchestrationFixtures.Plan(
            OrchestrationFixtures.Work("T1"),
            OrchestrationFixtures.Work("T2", workspace: WorkspaceMode.ReadOnly).After(OrchestrationFixtures.On("T1")) with { MaxRetries = 0 });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);
        var outcome = await AuditAsync(harness, run.Id);

        Assert.Equal(RunPhase.Failed, result.Phase);
        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.PermissionsCode, finding.Code);
        Assert.Equal([new TaskId("T2")], finding.Tasks);
        Assert.Contains("fuera/T2.txt", finding.Message, StringComparison.Ordinal);
        Assert.Equal(2, finding.Entries.Count); // the diff and the attempt record of the same attempt
    }

    /// <summary>Every finding is the write of a read-only task; returns how many there were.</summary>
    private static int Check(OrchestratorAuditOutcome outcome, RandomWorld world, string context, EngineHarness harness)
    {
        Assert.True(outcome.Log.IsValid, $"{context}: {outcome.Log.Problem}");
        var unexpected = outcome.Findings
            .Where(f => f.Code != OrchestratorAudit.PermissionsCode || f.Tasks.Any(t => world.Specs[t].Workspace != WorkspaceMode.ReadOnly))
            .ToList();
        Assert.True(unexpected.Count == 0, $"{context}\n{string.Join("\n", unexpected.Select(f => $"{f.Code}: {f.Message}"))}\n{harness.Describe()}");
        return outcome.Findings.Count;
    }

    /// <summary>A run waits for a person when what is left is blocked and nothing runs or waits for an approval.</summary>
    private static bool Resting(RunSnapshot snapshot) =>
        snapshot.Phase is RunPhase.Blocked or RunPhase.AwaitingHuman
        && snapshot.Tasks.Any(t => t.Status == WorkTaskStatus.Blocked)
        && !snapshot.Tasks.Any(t => t.Status is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval);

    /// <summary>Runs to the end; when the run waits for a person, audits it there and retries what is blocked, or cancels it.</summary>
    private static async Task<RunResult> DriveAsync(EngineHarness harness, IOrchestrationRun run, Random random, Func<Task> atRest)
    {
        for (var interventions = 0; ; interventions++)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!run.Completion.IsCompleted && !Resting(run.Snapshot()))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The run neither ended nor rested.\n{RunTestSupport.Describe(run.Snapshot())}\n{harness.Describe()}");
                }

                await Task.Delay(2, Ct);
            }

            if (run.Completion.IsCompleted)
            {
                return await run.Completion;
            }

            await atRest();
            if (interventions >= 10 || random.Next(100) < 25)
            {
                await run.CancelAsync("fin de la prueba");
                return await run.CompleteAsync(harness);
            }

            var blocked = run.Snapshot().Tasks.Where(t => t.Status == WorkTaskStatus.Blocked).Select(t => t.Id).ToList();
            foreach (var task in blocked)
            {
                try
                {
                    await run.RetryBlockedAsync(task);
                }
                catch (InvalidOperationException) when (run.Completion.IsCompleted || run.Snapshot().View(task.Value).Status != WorkTaskStatus.Blocked)
                {
                    // An earlier retry already moved the run on; the engine rightly refuses this one.
                }
            }

            if (run.Completion.IsCompleted)
            {
                return await run.Completion;
            }

            await harness.WaitUntilAsync(run, s => run.Completion.IsCompleted || s.Tasks.Where(t => blocked.Contains(t.Id)).All(t => t.Status != WorkTaskStatus.Blocked), "unblocked");
        }
    }

    private static async Task<OrchestratorAuditOutcome> AuditAsync(EngineHarness harness, RunId run)
    {
        var project = harness.Project.ProjectId;
        var log = await harness.Decisions.LoadAsync(project, Ct);
        var started = RunLog.RunStarted(RunLog.EntriesOf(log.Entries, run.Value)) ?? throw new InvalidOperationException($"Run {run} did not start.");
        var contents = new Dictionary<EvidenceId, ReadOnlyMemory<byte>>();
        foreach (var wanted in OrchestratorAudit.EvidenceToRead(log.Entries, run))
        {
            await using var stream = await harness.Evidence.OpenContentAsync(project, wanted.Evidence, Ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            contents[wanted.Evidence] = buffer.ToArray();
        }

        return OrchestratorAudit.Audit(new OrchestratorAuditInput(
            run,
            log,
            await harness.Runs.FindAsync(project, run, Ct),
            await harness.Plans.FindAsync(project, started.PlanHash, Ct),
            await harness.RequirementStore.FindAsync(project, started.RequirementSetHash, Ct),
            contents));
    }
}
