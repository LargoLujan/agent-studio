using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Providers.Live;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Composition;

/// <summary>
/// The offline twin of <see cref="ProviderConcurrencyLiveTests"/> (criterion F2-5): the same production chain (the real
/// Claude adapter, the real journal, <see cref="AgentProviderPipeline"/>) and the same assertions, but the CLI is a fake
/// that lives for a second. It costs no quota and proves that the live test can tell a serialised run from an overlapping one.
/// </summary>
public sealed class ProviderConcurrencyScenarioTests
{
    // The probe must fit well inside the lifetime (the first run is still alive when it is taken) and be long enough for
    // an unlimited second start to have completed; both margins are generous so a loaded machine cannot flip the result.
    private static readonly TimeSpan FakeCliLifetime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan QueuedProbe = TimeSpan.FromMilliseconds(500);

    private sealed class FakeClaudeLocator : IExecutableLocator
    {
        public string? Find(string command) => @"C:\fake\claude.exe";
    }

    /// <summary>A "claude" that prints an init line, lives for a while, prints a result line and exits with 0.</summary>
    private sealed class FakeClaudeRunner(TimeSpan lifetime) : IStreamingProcessRunner
    {
        private int _lastProcessId = 5000;

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult<IProcessSession>(new FakeSession(Interlocked.Increment(ref _lastProcessId), lifetime));
    }

    private sealed class FakeSession : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _output = Channel.CreateUnbounded<ProcessOutputLine>();

        public FakeSession(int processId, TimeSpan lifetime)
        {
            ProcessId = processId;
            Exited = LiveAsync(lifetime);
        }

        public int ProcessId { get; }

        public IAsyncEnumerable<ProcessOutputLine> Output => _output.Reader.ReadAllAsync();

        public Task<int> Exited { get; }

        public Task KillAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<int> LiveAsync(TimeSpan lifetime)
        {
            var session = Guid.NewGuid().ToString();
            Emit($$$"""{"type":"system","subtype":"init","session_id":"{{{session}}}","model":"claude-haiku-4-5"}""");
            await Task.Delay(lifetime);
            Emit($$$"""{"type":"result","subtype":"success","is_error":false,"result":"OK","session_id":"{{{session}}}","total_cost_usd":0.001,"usage":{"input_tokens":1,"output_tokens":1}}""");
            _output.Writer.TryComplete();
            return 0;
        }

        private void Emit(string json) =>
            _output.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardOutput, json, DateTimeOffset.UtcNow));
    }

    private static AgentRunRequest RequestFor(string label) =>
        Request(
            prompt: "Responde exactamente con la palabra OK y nada más.",
            model: "haiku",
            budget: new ExecutionBudget(2, TimeSpan.FromMinutes(1)),
            workingDirectory: @"C:\work") with { Purpose = $"F2-5 probe {label}" };

    private static (IAgentProvider Provider, RecordingProcessRunner Recorder) Compose(string projectRoot, int maxConcurrent, TimeSpan? lifetime = null)
    {
        var recorder = new RecordingProcessRunner(new FakeClaudeRunner(lifetime ?? FakeCliLifetime));
        var adapter = new ClaudeCodeAgentProvider(new FakeClaudeLocator(), recorder, TimeProvider.System, TestLocalData.RunFolders);
        var journal = new MarkdownDailyJournalFactory(new ProjectStorageFiles(), TimeProvider.System).ForProject(projectRoot);
        return (AgentProviderPipeline.Compose(adapter, journal, TimeProvider.System, maxConcurrent), recorder);
    }

    [Fact]
    public async Task With_a_limit_of_one_the_two_processes_never_overlap_and_both_runs_are_journaled()
    {
        using var temp = new TempDirectory();
        var (provider, recorder) = Compose(temp.Path, maxConcurrent: 1);

        var result = await ConcurrencyScenario.RunTwoAtOnceAsync(provider, recorder, RequestFor, QueuedProbe, TestContext.Current.CancellationToken);

        Assert.All(result.Runs, run => Assert.Equal(AgentRunStatus.Succeeded, run.Outcome.Status));
        ConcurrencyScenario.AssertNeverOverlapped(result);
        ConcurrencyScenario.AssertJournaledInOrder(temp.Path, "F2-5 probe A", "F2-5 probe B");
    }

    [Fact]
    public async Task With_a_limit_of_two_the_same_scenario_sees_the_overlap_and_the_assertion_fails()
    {
        using var temp = new TempDirectory();
        var (provider, recorder) = Compose(temp.Path, maxConcurrent: 2);

        var result = await ConcurrencyScenario.RunTwoAtOnceAsync(provider, recorder, RequestFor, QueuedProbe, TestContext.Current.CancellationToken);

        Assert.False(result.SecondStartWasQueued);
        var (first, second) = (result.Processes[0], result.Processes[1]);
        Assert.True(first.OverlapsWith(second), "Two processes alive at the same time must be reported as overlapping.");
        Assert.True(result.Runs[1].SessionStartedAt < result.Runs[0].FinishedAt);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ConcurrencyScenario.AssertNeverOverlapped(result));
    }

    [Fact]
    public async Task The_recording_runner_keeps_the_spec_the_raw_lines_and_the_exit_of_each_process()
    {
        using var temp = new TempDirectory();
        var (provider, recorder) = Compose(temp.Path, maxConcurrent: 1, lifetime: TimeSpan.FromMilliseconds(100));
        var request = RequestFor("A");

        await using var run = await provider.StartAsync(request, TestContext.Current.CancellationToken);
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        var process = Assert.Single(recorder.Processes);
        Assert.Equal(@"C:\fake\claude.exe", process.Spec.FileName);
        Assert.DoesNotContain(process.Spec.Arguments, argument => argument.Contains("Responde", StringComparison.Ordinal));
        Assert.Equal(request.Prompt, process.Spec.StandardInput);
        Assert.Equal(2, process.StandardOutput.Count);
        Assert.True(ClaudeInitEvent.TryParse(process.StandardOutput[0], out var init));
        Assert.Equal("claude-haiku-4-5", init.Model);
        Assert.Equal(0, process.ExitCode);
        Assert.True(process.ExitedAt >= process.StartedAt);
        Assert.True(process.StartedAt >= process.RequestedAt);
    }
}
