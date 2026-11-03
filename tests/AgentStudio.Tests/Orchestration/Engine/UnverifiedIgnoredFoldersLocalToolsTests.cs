using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The engine's guard over the real capture of a real repository (<see cref="GitWorktreeManager"/>, a small budget):
/// what git and the capture really say of an ignored folder past the budget, and of a file past the per-file cap.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class UnverifiedIgnoredFoldersLocalToolsTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    /// <summary>The engine with every port faked but the capture of the user's repository, which is the real one of <see cref="_repo"/>.</summary>
    private EngineHarness Harness(CaptureBudget budget)
    {
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot, CaptureBudget: budget));
        var harness = new EngineHarness();
        harness.Worktrees.CaptureFrom = ct => manager.CaptureUserRepositoryStateAsync(_repo.Root, ct);
        return harness;
    }

    [Fact]
    public async Task A_real_ignored_folder_past_the_capture_budget_does_not_block_a_run_that_cannot_write_there()
    {
        // A budget of one file: cache/ holds two, so neither capture can verify it, and both say so the same way. The
        // per-file cap stays above git's sample hooks, which are git files and would block whoever runs.
        File.AppendAllText(Path.Combine(_repo.Root, ".git", "info", "exclude"), "cache/\n");
        _repo.Write("cache/uno.bin", "0123456789\n");
        _repo.Write("cache/dos.bin", "0123456789\n");
        using var harness = Harness(new CaptureBudget(64 * 1024, 64, 1));

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.True(result.Phase == RunPhase.Succeeded, harness.Describe());
        Assert.Empty(harness.Alerts("user-repository.changed"));
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.capture-indeterminate"));
        Assert.Contains("'cache/' (the capture budget ran out inside this folder)", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_modified_tracked_file_past_the_per_file_cap_blocks_any_run()
    {
        // Past an 8 KiB per-file cap (git's sample hooks stay below it): the user's own work, never relaxed.
        _repo.Write("README.md", new string('x', 9000) + "\n");
        using var harness = Harness(new CaptureBudget(8 * 1024, 1024 * 1024, 100));

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(harness.Claude.Starts);
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.EndsWith(
            "Content of 'README.md' is indeterminate (larger than the 8192-byte per-file cap): its content could not be verified, so a change that kept its sizes and modification times cannot be ruled out.",
            alert.Message,
            StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}
