using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Memory.Journal;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Providers.Live;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Composition;

/// <summary>
/// Criterion F2-5 with the real thing: the real Claude Code adapter and the real daily journal, composed through
/// <see cref="AgentProviderPipeline"/> with a limit of one. They spend subscription quota, so they need AGENTSTUDIO_PROVIDER_TESTS=1.
/// The offline twin of this scenario (fake CLI, same assertions) is <see cref="ProviderConcurrencyScenarioTests"/>.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class ProviderConcurrencyLiveTests
{
    private const string Prompt = "Responde exactamente con la palabra OK y nada más.";

    // A real run lasts several seconds (CLI startup + model), so the first one is still alive when this has elapsed, and
    // a limiter that does not work would have started the second one (journal entry + spawn take milliseconds) long before.
    private static readonly TimeSpan QueuedProbe = TimeSpan.FromSeconds(0.75);

    [Fact]
    public async Task Two_simultaneous_real_claude_runs_with_limit_one_never_overlap_and_are_both_journaled()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var projectRoot = temp.CreateDirectory("project");
        var workingDirectory = temp.CreateDirectory("work");

        // Exactly the production chain: Concurrency(Journaling(adapter)); only the runner is wrapped, to observe the processes.
        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        var adapter = new ClaudeCodeAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
        var journal = new MarkdownDailyJournalFactory(new ProjectStorageFiles(), TimeProvider.System).ForProject(projectRoot);
        var provider = AgentProviderPipeline.Compose(adapter, journal, TimeProvider.System, maxConcurrent: 1);
        Assert.Equal(1, Assert.IsType<ConcurrencyLimitedAgentProvider>(provider).Limit);
        Assert.True(AgentProviderPipeline.IsJournaled(provider));

        AgentRunRequest RequestFor(string label) =>
            Request(
                prompt: Prompt,
                model: "haiku",
                budget: new ExecutionBudget(2, TimeSpan.FromMinutes(3)),
                workingDirectory: workingDirectory) with { Purpose = $"F2-5 probe {label}" };

        ConcurrencyResult result;
        try
        {
            result = await ConcurrencyScenario.RunTwoAtOnceAsync(provider, recorder, RequestFor, QueuedProbe, token);
        }
        finally
        {
            for (var i = 0; i < recorder.Processes.Count; i++)
            {
                LiveTestSupport.SaveCapture(recorder.Processes[i], $"f2-5-run-{i + 1}");
            }
        }

        // Evidence first, so it survives a failing assertion below.
        LiveTestSupport.Note($"F2-5 second StartAsync still queued {QueuedProbe.TotalSeconds} s after the first run started: {result.SecondStartWasQueued}");
        foreach (var process in result.Processes.OrderBy(p => p.RequestedAt))
        {
            LiveTestSupport.Note($"F2-5 pid {process.ProcessId}: requested {process.RequestedAt:O}, started {process.StartedAt:O}, exited {process.ExitedAt:O}, exit {process.ExitCode}");
        }

        foreach (var run in result.Runs)
        {
            LiveTestSupport.Note($"F2-5 run: SessionStarted {run.SessionStartedAt:O}, RunFinished {run.FinishedAt:O}, status {run.Outcome.Status}");
        }

        foreach (var line in JournalFiles.ReadLines(projectRoot))
        {
            LiveTestSupport.Note("F2-5 journal: " + line);
        }

        foreach (var cost in result.Runs.SelectMany(r => r.Events.OfType<UsageReported>()).Select(u => u.Usage.CostUsd.Value))
        {
            LiveTestSupport.Note($"F2-5 estimated cost reported by the CLI: {cost} USD");
        }

        Assert.All(result.Runs, run =>
        {
            Assert.True(run.Outcome.Status == AgentRunStatus.Succeeded, run.Outcome.Error);
            Assert.Equal(0, run.Outcome.ExitCode);
            Assert.Contains("OK", run.Outcome.FinalText, StringComparison.Ordinal);
        });

        ConcurrencyScenario.AssertNeverOverlapped(result);
        ConcurrencyScenario.AssertJournaledInOrder(projectRoot, "F2-5 probe A", "F2-5 probe B");
    }
}
