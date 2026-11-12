using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Verification;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using DebateReplies = AgentStudio.Tests.Debate.Fakes.ScriptedDebateProvider;

namespace AgentStudio.Tests.Integration.Phase9;

/// <summary>One collection for every integration test of Phase 9: each one starts real runs with real processes, so they run one after another.</summary>
[CollectionDefinition(Name)]
public sealed class Phase9Collection
{
    public const string Name = "Phase 9 integration";
}

/// <summary>The helpers the integration tests of Phase 9 share: scripted executors that report usage, and runs of the engine.</summary>
internal static class Phase9Runs
{
    // What every executor attempt reports, so the consumption of a milestone has figures to add up.
    public const long ExecutorInput = 1_000;
    public const long ExecutorOutput = 100;

    public static readonly TimeSpan Guard = Phase5Runs.Guard;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The executor of every task: T1 writes the file the check looks for, any other task its own file; each reports its usage.</summary>
    public static Func<AgentRunRequest, int, AgentScript> Executor { get; } = (request, _) =>
    {
        var script = request.TaskId == "T1"
            ? AgentScript.Completed(("src/feature.txt", "función nueva\n"))
            : AgentScript.Completed(($"src/{request.TaskId}/a.txt", "a\n"));
        return script with { Events = [new UsageReported(DateTimeOffset.UtcNow, DebateReplies.Usage(ExecutorInput, ExecutorOutput))] };
    };

    public static VerificationProfile NoChecks { get; } = new("sin verificación", []);

    /// <summary>One task that covers both requirements (the mandatory one must be covered) and has neither verification nor review: it ends with no verdict.</summary>
    public static PlanDefinition Unreviewed(string task) => Plan(Work(task).Covering("RQ-01", "RQ-02"));

    public static async Task<RunResult> RunAsync(AppWorld world, PlanDefinition plan, VerificationProfile profile, RunId? follows = null, string request = "Que se haga lo que pide el hito.")
    {
        var run = (await world.App.RunRequestAsync(request, Requirements(), plan, profile)) with { Follows = follows };
        await using var started = await world.App.Orchestrator.StartAsync(run, Ct);
        var result = await started.Completion.WaitAsync(Guard, Ct);
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(await world.App.LoadLogAsync()));
        return result;
    }

    public static Task<MilestoneView> LinkRunAsync(AppWorld world, MilestoneId milestone, RunId run) =>
        world.Get<IMilestoneService>().LinkAsync(world.App.Project, milestone, new MilestoneTarget(MilestoneTargetKind.Run, run.Value), "manuel", Ct);

    public static Task<MilestoneView> OpenMilestoneAsync(AppWorld world, string name = "Primera entrega", WorkBudget? budget = null) =>
        world.Get<IMilestoneService>().OpenAsync(new OpenMilestoneRequest(world.App.Project, name, "Que multiplique y que quede documentado.", Requirements(), budget, "manuel"), Ct);
}
