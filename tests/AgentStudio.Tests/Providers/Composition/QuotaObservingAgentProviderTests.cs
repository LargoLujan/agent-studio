using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability;
using AgentStudio.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Composition;

/// <summary>
/// docs/14 D-14: every <c>QuotaReported</c> of any run, auxiliary ones included, goes to the user's quota record. It is
/// written by a decorator of the provider pipeline, inside the journal, whose pump reads every event even when the caller
/// reads none; the record only shows, so a failure to write it never touches the run.
/// </summary>
public sealed class QuotaObservingAgentProviderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Observations : IQuotaObservations
    {
        public bool Fail { get; init; }

        public List<QuotaObservation> Appended { get; } = [];

        public Task AppendAsync(IReadOnlyList<QuotaObservation> observations, CancellationToken ct)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }

            lock (Appended)
            {
                Appended.AddRange(observations);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QuotaObservation>> LatestAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<QuotaObservation>>([.. Appended]);
    }

    private sealed class Journal : IPromptJournal
    {
        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct) => Task.FromResult(new JournalEntryId(new DateOnly(2026, 10, 18), 1));

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Run(IReadOnlyList<AgentEvent> events) : IAgentRun
    {
        private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RunId => "run";

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion => _completion.Task;

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<AgentEvent> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }

            _completion.TrySetResult(events.OfType<RunFinished>().LastOrDefault()?.Outcome ?? new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, "s", null));
        }
    }

    private sealed class Adapter(IReadOnlyList<AgentEvent> events) : IAgentProvider
    {
        public ProviderId Provider => ProviderId.ClaudeCode;

        public VendorId Vendor => VendorId.Anthropic;

        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false, true, true, 2);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) => Task.FromResult<IAgentRun>(new Run(events));
    }

    private static AgentRunRequest Request() =>
        new(
            new AgentDefinition(
                new AgentId("recomendador"),
                "Recomendador",
                AgentRole.Critic,
                new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"),
                string.Empty,
                [],
                [],
                [],
                new ExecutionBudget(4, TimeSpan.FromMinutes(5)),
                1,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            "¿Qué skills?",
            "C:/runs/aux",
            new PermissionProfile(false, false, false, [], [], []));

    /// <summary>The events the real parser makes of the capture <c>01-texto</c> (its <c>rate_limit_event</c> included).</summary>
    private static List<AgentEvent> CaptureEvents()
    {
        var parser = new ClaudeStreamJsonParser(new Memory.FakeClock(At));
        return [.. FixtureLines("01-texto.jsonl").SelectMany(parser.Parse)];
    }

    [Fact]
    public async Task The_windows_claude_announces_in_a_run_reach_the_quota_record_even_when_nobody_reads_the_events()
    {
        var observations = new Observations();
        var provider = AgentProviderPipeline.Compose(new Adapter(CaptureEvents()), new Journal(), new Memory.FakeClock(At), quota: observations);

        await using (var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken))
        {
            await run.Completion;
        }

        Assert.Equal(
            [
                (ProviderId.ClaudeCode, "five_hour", 0.47, DateTimeOffset.FromUnixTimeSeconds(1792066800)),
                (ProviderId.ClaudeCode, "seven_day", 0.25, DateTimeOffset.FromUnixTimeSeconds(1792350000)),
            ],
            observations.Appended.Select(o => (o.Provider, o.Window, o.Utilization.Value!.Value, o.ResetsAt!.Value)));
        Assert.All(observations.Appended, o => Assert.Equal(MetricOrigin.ReportedByProvider, o.Utilization.Origin));
        Assert.All(observations.Appended, o => Assert.Equal(At, o.ObservedAt));
        Assert.All(observations.Appended, o => Assert.Equal("claude rate_limit_event", o.Source));

        // The CLI does not say how long a window lasts nor gives it a display name: nothing is made up.
        Assert.All(observations.Appended, o => Assert.Null(o.Length));
        Assert.All(observations.Appended, o => Assert.Null(o.Label));
    }

    [Fact]
    public async Task A_quota_record_that_cannot_be_written_never_touches_the_run()
    {
        var events = CaptureEvents();
        var provider = AgentProviderPipeline.Compose(new Adapter(events), new Journal(), new Memory.FakeClock(At), quota: new Observations { Fail = true });

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var seen = new List<AgentEvent>();
        await foreach (var e in run.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
            seen.Add(e);
        }

        Assert.Equal(events, seen);
        Assert.Equal(AgentRunStatus.Succeeded, (await run.Completion).Status);
    }

    [Fact]
    public void Every_provider_of_a_project_goes_through_the_journal_and_the_quota_record()
    {
        using var temp = new TempDirectory();
        var sut = new ProjectAgentProviders(new NoExecutables(), new NoProcesses(), new Journals(), TimeProvider.System, new Observations());

        var providers = sut.For(temp.Path);

        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p), $"{p.Provider} skips the journal"));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsQuotaObserved(p), $"{p.Provider} skips the quota record"));
    }

    [Fact]
    public void Without_a_quota_record_the_pipeline_is_the_journal_and_the_limit_as_before()
    {
        var provider = AgentProviderPipeline.Compose(new Adapter([]), new Journal(), TimeProvider.System);

        Assert.True(AgentProviderPipeline.IsJournaled(provider));
        Assert.False(AgentProviderPipeline.IsQuotaObserved(provider));
    }

    [Fact]
    public async Task The_codex_quota_reader_of_the_container_asks_the_opt_in_and_launches_nothing_without_it()
    {
        var asked = 0;
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IExecutableLocator>(new NoExecutables())
            .AddSingleton<IStreamingProcessRunner>(new NoProcesses())
            .AddSingleton<IProcessRunner>(new NoCommands())
            .AddCodexQuotaReader(_ => () =>
            {
                asked++;
                return false;
            })
            .BuildServiceProvider();

        var reader = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IProviderQuotaReader>(services);
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ProviderId.CodexCli, QuotaReadStatus.NotOptedIn, 1), (reader.Provider, result.Status, asked));
    }

    [Fact]
    public async Task With_the_observability_of_WP2_both_quota_readers_coexist_and_a_run_keeps_its_quota_in_its_record()
    {
        using var temp = new TempDirectory();
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IExecutableLocator>(new NoExecutables())
            .AddSingleton<IStreamingProcessRunner>(new NoProcesses())
            .AddSingleton<IProcessRunner>(new NoCommands())
            .AddSingleton<IPromptJournalFactory>(new Journals())
            .AddAgentStudioObservability(new AgentStudio.Observability.Quota.QuotaLogOptions { FilePath = Path.Combine(temp.Path, "quota.jsonl") })
            .AddCodexQuotaReader(_ => () => false)
            .AddAgentStudioProviders()
            .BuildServiceProvider();

        // Both readers are there, one per provider: nothing hides the other. A consumer picks the Codex one by its provider
        // (a single GetService would return only the last one registered).
        var readers = services.GetServices<IProviderQuotaReader>().ToList();
        Assert.Equal([ProviderId.CodexCli, ProviderId.GeminiCli], readers.Select(r => r.Provider).OrderBy(p => p.Value, StringComparer.Ordinal));
        Assert.IsType<AgentStudio.Providers.Cli.Codex.AppServer.CodexAppServerQuotaReader>(Assert.Single(readers, r => r.Provider == ProviderId.CodexCli));

        // The providers of a project keep the quota their runs announce in the record of WP2.
        Assert.All(services.GetRequiredService<IProjectAgentProviders>().For(temp.Path), p => Assert.True(AgentProviderPipeline.IsQuotaObserved(p), $"{p.Provider} skips the quota record"));
        var record = services.GetRequiredService<IQuotaObservations>();
        var provider = AgentProviderPipeline.Compose(new Adapter(CaptureEvents()), new Journal(), new Memory.FakeClock(At), quota: record);
        await using (var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken))
        {
            await run.Completion;
        }

        var latest = await record.LatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [("five_hour", 0.47), ("seven_day", 0.25)],
            latest.Where(o => o.Provider == ProviderId.ClaudeCode).OrderBy(o => o.Window, StringComparer.Ordinal).Select(o => (o.Window, o.Utilization.Value!.Value)));
    }

    private sealed class NoCommands : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No command should run without the opt-in.");
    }

    private sealed class Journals : IPromptJournalFactory
    {
        public IPromptJournal ForProject(string projectRoot) => new Journal();
    }

    private sealed class NoExecutables : IExecutableLocator
    {
        public string? Find(string command) => null;
    }

    private sealed class NoProcesses : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No process should start while composing providers.");
    }
}
