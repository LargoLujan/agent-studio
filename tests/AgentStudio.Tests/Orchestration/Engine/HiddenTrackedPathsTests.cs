using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// R3-04: the capture also keeps the tracked paths no status line can show (files flagged assume-unchanged or
/// skip-worktree, submodules checked out). One it cannot verify blocks like any difference, also in a run that cannot
/// write to the user's repository at medium integrity: the engine only ever lets through an ignored folder of the status
/// (ADR-0012), and a submodule or a hidden file is never one.
/// </summary>
public sealed class HiddenTrackedPathsTests
{
    /// <summary>What the capture says of a folder past its budget.</summary>
    private static readonly FileFingerprint OutOfBudget = new(null, "folder:9000 entries;metadata:aaaa", "the capture budget ran out inside this folder");

    [Fact]
    public async Task A_submodule_the_capture_cannot_verify_blocks_also_a_run_that_cannot_write_there()
    {
        using var harness = new EngineHarness();
        harness.Worktrees.UserState = harness.Worktrees.UserState with
        {
            PorcelainStatus = ["!! bin/"],
            Contents = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal) { ["bin/"] = OutOfBudget },
            HiddenTracked = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal) { ["sub/"] = OutOfBudget },
        };

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        // The ignored folder is let through, as in any run that cannot write there; the submodule is not.
        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(harness.Claude.Starts);
        var (_, blocked) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Contains("Content of 'sub/' is indeterminate (the capture budget ran out inside this folder)", blocked.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'bin/'", blocked.Message, StringComparison.Ordinal);
        Assert.Contains("'bin/'", Assert.Single(harness.Alerts("user-repository.capture-indeterminate")).Payload.Message, StringComparison.Ordinal);
    }
}
