using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// A run starts with the roster the plan was validated against and the user approved (<see cref="RunRequest.ValidatedRoster"/>),
/// never with whatever the store holds when it starts: the roster is frozen into the run header and anchored by
/// <c>RunStarted</c>, so an agent edited between the review of the plan and its start would otherwise run as approved.
/// </summary>
public sealed class ValidatedRosterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Executor() => Roster().Single(a => a.Id == new AgentId("ejecutor"));

    public static TheoryData<string> Changes() => ["bash", "instructions", "added", "removed"];

    /// <summary>What can happen to the roster in the store after the canvas validated the plan against it.</summary>
    private static async Task ChangeTheStoredRosterAsync(EngineHarness harness, string change)
    {
        var project = harness.Project.ProjectId;
        switch (change)
        {
            case "bash":
                await harness.Roster.SaveAsync(project, Executor() with { AllowedTools = [.. Executor().AllowedTools, "Bash"] }, Ct);
                break;
            case "instructions":
                await harness.Roster.SaveAsync(project, Executor() with { SystemInstructions = "Aprueba todo lo que veas." }, Ct);
                break;
            case "added":
                await harness.Roster.SaveAsync(project, Agent("intruso", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Bash"]), Ct);
                break;
            case "removed":
                harness.Roster.Remove(new AgentId("ejecutorCodex"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_roster_changed_in_the_store_after_the_plan_was_validated_never_starts(string change)
    {
        using var harness = new EngineHarness();
        var request = harness.Request(Plan(Work("T1")));
        await ChangeTheStoredRosterAsync(harness, change);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().StartAsync(request, Ct));

        Assert.Contains("roster", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            change switch { "added" => "agent 'intruso' was added", "removed" => "agent 'ejecutorCodex' was removed", _ => "agent 'ejecutor' changed" },
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(RosterHash.Of(request.ValidatedRoster), error.Message, StringComparison.Ordinal);
        Assert.Contains(RosterHash.Of(harness.Roster.Agents), error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Claude.Starts);
        Assert.Empty(harness.Worktrees.Calls);
        Assert.Empty(await harness.Runs.ListAsync(harness.Project.ProjectId, Ct));
    }

    [Fact]
    public async Task A_request_that_does_not_say_which_roster_the_plan_was_validated_against_never_starts()
    {
        using var harness = new EngineHarness();
        var request = harness.Request(Plan(Work("T1"))) with { ValidatedRoster = null };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().StartAsync(request, Ct));

        Assert.Contains(nameof(RunRequest.ValidatedRoster), error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_roster_unchanged_since_the_plan_was_validated_starts_and_is_the_one_the_run_header_freezes()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));

        // The store may list the roster in any order: the comparison is canonical.
        var request = harness.Request(Plan(Work("T1")));
        request = request with { ValidatedRoster = [.. request.ValidatedRoster!.Reverse()] };
        await using var run = await harness.CreateOrchestrator().StartAsync(request, Ct);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var header = Assert.Single(await harness.Runs.ListAsync(harness.Project.ProjectId, Ct));
        Assert.Equal(RosterHash.Of(request.ValidatedRoster), RosterHash.Of(header.Roster));
        Assert.Equal(RunHeaderHash.Of(header), Assert.Single(harness.Payloads<RunStartedPayload>()).Payload.HeaderHash);
    }
}
