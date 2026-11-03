using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Providers.Live;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The confinement of Claude Code to its worktree is measured with one exact profile (ADR-0018, «Medición»,
/// <see cref="ClaudeWriteConfinementLiveTests"/>; 2.1.290 is confined with the <c>dontAsk</c> launch and its path rules,
/// it was not in <c>acceptEdits</c> with bare file tools). A version may only be listed as confined after that measurement
/// passes with the profile the engine really gives a writing executor, so a change of what it gets (a shell, network, an
/// extra directory, another tool) makes this fail: the measurement has to be made again.
/// </summary>
public sealed class MeasuredExecutorProfileTests
{
    [Fact]
    public async Task A_default_executor_in_a_task_that_writes_gets_exactly_the_profile_whose_confinement_was_measured()
    {
        using var harness = new EngineHarness();

        // The default executor as the app creates it; its review policy does not change its permissions, and relaxed it
        // lets the one-task plan run without a review.
        var executor = DefaultAgentRoster.Create().Single(a => a.Id == DefaultAgentRoster.Executor) with { Review = new ReviewPolicy(false, false) };
        await harness.Roster.SaveAsync(harness.Project.ProjectId, executor, TestContext.Current.CancellationToken);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);

        var given = Assert.Single(harness.Claude.Starts).Request.Permissions;
        var measured = MeasuredConfinement.ExecutorProfile;
        Assert.Equal(measured.AllowFileWrites, given.AllowFileWrites);
        Assert.Equal(measured.AllowShell, given.AllowShell);
        Assert.Equal(measured.AllowNetwork, given.AllowNetwork);
        Assert.Equal(measured.AllowedTools, given.AllowedTools);
        Assert.Equal(measured.DeniedTools, given.DeniedTools);
        Assert.Equal(measured.AdditionalDirectories, given.AdditionalDirectories);
    }
}
