using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Orchestration.Fakes;

namespace AgentStudio.Tests.Integration.Phase9;

/// <summary>
/// F9-8 (docs/16 D-05, ADR-0040) with the real engine of the app and its real gate: with a budget the engine does not launch the attempt that
/// would pass it and says why, the task waits for the person (it is not failed), and nothing reaches the CLI. A milestone's budget is part of
/// its immutable, anchored header: raising it is a new milestone, so the budget that is raised and retried is the project's, which the app keeps
/// in its local folder and the gate of the engine reads on every launch.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class MilestoneBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IOrchestrationRun> StartAsync(AppWorld world, string task, RunId? follows = null)
    {
        var request = (await world.App.RunRequestAsync("Una tarea más.", AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures.Requirements(), Phase9Runs.Unreviewed(task), Phase9Runs.NoChecks)) with { Follows = follows };
        return await world.App.Orchestrator.StartAsync(request, Ct);
    }

    private static async Task WaitAwaitingAsync(AppWorld world, IOrchestrationRun run)
    {
        // A run that ends instead (it was not held) must say so at once, with its log, and not make the test wait for the guard.
        try
        {
            await Phase3Checks.WaitUntilAsync(() => run.Snapshot().Phase == RunPhase.AwaitingHuman || run.Completion.IsCompleted, "the run waiting for the person", TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            var snapshot = run.Snapshot();
            Assert.Fail($"{snapshot.Phase}, runs used {snapshot.AgentRunsUsed}; " + string.Join(", ", snapshot.Tasks.Select(t => $"{t.Id} {t.Status} {t.BlockReason}")) + "\n" + AgentStudio.Tests.Integration.Phase4.Phase4Checks.Describe(await world.App.LoadLogAsync()));
        }

        if (run.Snapshot().Phase != RunPhase.AwaitingHuman)
        {
            Assert.Fail(AgentStudio.Tests.Integration.Phase4.Phase4Checks.Describe(await run.Completion) + "\n" + AgentStudio.Tests.Integration.Phase4.Phase4Checks.Describe(await world.App.LoadLogAsync()));
        }
    }

    [Fact]
    public async Task The_engine_does_not_launch_the_attempt_that_would_pass_the_budget_of_the_milestone_the_task_waits_for_the_person_and_raising_the_budget_of_the_project_and_retrying_goes_on()
    {
        await using var world = await AppWorld.OpenAsync(clis: new Phase5Clis { Executor = Phase9Runs.Executor });
        var project = world.App.Project;

        // A first run, with one attempt, linked to a milestone that allows one agent run in all.
        var first = await Phase9Runs.RunAsync(world, Phase9Runs.Unreviewed("T2"), Phase9Runs.NoChecks);
        var milestone = await Phase9Runs.OpenMilestoneAsync(world, budget: new WorkBudget(null, 1, BudgetPeriod.Total));
        await Phase9Runs.LinkRunAsync(world, milestone.Header.Id, first.Id);
        var launched = world.Clis.Launches.Count;

        // ---- the follow-up belongs to the milestone: its attempt would be the second, and it is not launched.
        await using (var followUp = await StartAsync(world, "T3", first.Id))
        {
            await WaitAwaitingAsync(world, followUp);
            Assert.Equal(RunPhase.AwaitingHuman, followUp.Snapshot().Phase); // not Failed: raising the budget and retrying goes on
            Assert.Equal(WorkTaskStatus.Blocked, followUp.Snapshot().View("T3").Status);
            Assert.Equal("AwaitingHuman", followUp.Snapshot().View("T3").BlockReason);
            Assert.Equal(0, followUp.Snapshot().AgentRunsUsed);
            Assert.Equal(launched, world.Clis.Launches.Count); // nothing reached a CLI, so no quota was spent
            var alert = Assert.Single(await world.App.PayloadsAsync<AuditAlertPayload>(followUp.Id.Value), a => a.Payload.Code == ProjectBudgetGate.ExceededCode).Payload;
            Assert.Contains(milestone.Header.Id.Value, alert.Message, StringComparison.Ordinal);
            Assert.Contains("ejecuciones de agente", alert.Message, StringComparison.Ordinal);

            // Raising the budget of the project does not lift the one of the milestone: its header is anchored, and it still applies.
            await world.Get<IProjectBudgetStore>().SaveAsync(project, new WorkBudget(null, 100, BudgetPeriod.Total), Ct);
            await followUp.RetryBlockedAsync(new TaskId("T3"));
            var blocked = 0;
            for (var i = 0; i < 300 && blocked < 2; i++)
            {
                blocked = (await world.App.PayloadsAsync<TaskStatusChangedPayload>(followUp.Id.Value)).Count(p => p.Payload.To == WorkTaskStatus.Blocked);
                await Task.Delay(100, Ct);
            }

            Assert.Equal(2, blocked); // the retry was read again by the gate, and the milestone still holds the task
            Assert.Equal(launched, world.Clis.Launches.Count);
            Assert.Equal("AwaitingHuman", followUp.Snapshot().View("T3").BlockReason);
        }

        // ---- a run that belongs to no milestone is held by the budget of the project, which the person can raise.
        await world.Get<IProjectBudgetStore>().SaveAsync(project, new WorkBudget(null, 1, BudgetPeriod.Total), Ct);
        await using var alone = await StartAsync(world, "T4");
        await WaitAwaitingAsync(world, alone);
        Assert.Equal("AwaitingHuman", alone.Snapshot().View("T4").BlockReason);
        Assert.Equal(launched, world.Clis.Launches.Count);
        Assert.Single(await world.App.PayloadsAsync<AuditAlertPayload>(alone.Id.Value), a => a.Payload.Code == ProjectBudgetGate.ExceededCode);

        await world.Get<IProjectBudgetStore>().SaveAsync(project, new WorkBudget(null, 10, BudgetPeriod.Total), Ct);
        await alone.RetryBlockedAsync(new TaskId("T4"));
        var result = await alone.Completion.WaitAsync(Phase9Runs.Guard, Ct);
        Assert.True(result.Phase == RunPhase.Succeeded, AgentStudio.Tests.Integration.Phase4.Phase4Checks.Describe(result));
        Assert.Equal(launched + 1, world.Clis.Launches.Count);

        var log = await world.App.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }
}
