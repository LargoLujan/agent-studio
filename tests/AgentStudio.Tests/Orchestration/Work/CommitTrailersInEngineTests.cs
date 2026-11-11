using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

// The namespace is not ...Orchestration.Work: the tests of the engine call Work(...) from OrchestrationFixtures, and a namespace of that name would shadow it.
namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-30 and F9-3: the commits the system makes of an attempt carry the trailers of <see cref="CommitTrailers"/>, which read
/// back as the run, the task, the attempt and the agent that produced it. They are an aid to infer, never the source.
/// </summary>
public sealed class CommitTrailersInEngineTests
{
    [Fact]
    public async Task The_commit_of_an_attempt_carries_the_run_the_task_the_attempt_and_the_agent()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"), Work("T2").After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var messages = harness.Worktrees.CommitMessages;
        Assert.Equal(2, messages.Count);
        foreach (var task in plan.Tasks)
        {
            var message = Assert.Single(messages, m => m.StartsWith(task.Id.Value + ": ", StringComparison.Ordinal));
            var trailers = CommitTrailers.Read(message);
            Assert.NotNull(trailers);
            Assert.Equal(run.Id, trailers.Run);
            Assert.Equal(task.Id, trailers.Task);
            Assert.Equal(1, trailers.Attempt);
            Assert.Equal(task.Agent, trailers.Agent);
            Assert.EndsWith("\n", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_subject_of_the_commit_is_still_the_task_and_its_attempt()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var message = Assert.Single(harness.Worktrees.CommitMessages);
        Assert.StartsWith("T1: ", message, StringComparison.Ordinal);
        Assert.Contains("(intento 1)\n\nAgent-Studio-Run: ", message, StringComparison.Ordinal);
    }
}
