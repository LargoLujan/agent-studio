using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Codex.AppServer;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Providers.Codex;

/// <summary>
/// docs/14 D-15 and F4-4: the quota of Codex is read only with the user's opt-in, from <c>codex app-server</c> with a
/// closed list of three constant messages, in an empty folder of the run, with a 20 s limit, and the process tree is ended
/// once the answer arrives. Nothing that spends a stored reset or sends an email is ever written.
/// </summary>
public sealed class CodexAppServerQuotaReaderTests
{
    private const string Codex = @"C:\tools\codex\codex.exe";

    private static readonly string[] ExpectedMessages =
    [
        """{"method":"initialize","id":1,"params":{"clientInfo":{"name":"agent_studio","title":"Agent Studio","version":"1.2.3"}}}""",
        """{"method":"initialized","params":{}}""",
        """{"method":"account/rateLimits/read","id":2,"params":{"excludeResetCreditDetails":true}}""",
    ];

    private const string Answer = """{"id":2,"result":{"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1792066800},"secondary":null,"planType":"plus"},"accountId":"acct-SINTETICO"}}""";

    private sealed class Locator(string? path) : IExecutableLocator
    {
        public List<string> Asked { get; } = [];

        public string? Find(string command)
        {
            Asked.Add(command);
            return path;
        }
    }

    /// <summary>Answers <c>codex --version</c>, the only command the probe rule allows besides the read.</summary>
    private sealed class VersionRunner(string output = "codex-cli 0.160.0", int exit = 0) : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return Task.FromResult(new ProcessResult(exit, output, string.Empty, TimeSpan.FromMilliseconds(5), false));
        }
    }

    private sealed class Session(IEnumerable<string> stdout, bool hang) : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _lines = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Kills { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Whether the working directory existed and was empty when the process was started.</summary>
        public bool StartedInEmptyFolder { get; set; }

        public int ProcessId => 42;

        public IAsyncEnumerable<ProcessOutputLine> Output => PumpAsync();

        public Task<int> Exited => _exited.Task;

        public Task KillAsync()
        {
            Kills++;
            _lines.Writer.TryComplete();
            _exited.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _lines.Writer.TryComplete();
            _exited.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<ProcessOutputLine> PumpAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _lines.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardError, "WARN codex_core: a log line with C:\\Users\\someone", DateTimeOffset.UnixEpoch));
            foreach (var line in stdout)
            {
                _lines.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardOutput, line, DateTimeOffset.UnixEpoch));
            }

            if (!hang)
            {
                _lines.Writer.TryComplete();
                _exited.TrySetResult(0);
            }

            await foreach (var line in _lines.Reader.ReadAllAsync(ct))
            {
                yield return line;
            }
        }
    }

    private sealed class Runner(Session session) : IStreamingProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            session.StartedInEmptyFolder = spec.WorkingDirectory is { } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
            return Task.FromResult<IProcessSession>(session);
        }
    }

    private sealed class World
    {
        public World(IEnumerable<string>? stdout = null, bool hang = false, bool optedIn = true, string? codex = Codex, string version = "codex-cli 0.160.0")
        {
            Session = new Session(stdout ?? [Answer], hang);
            Runner = new Runner(Session);
            Locator = new Locator(codex);
            Versions = new VersionRunner(version);
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
            OptedIn = optedIn;
            Reader = new CodexAppServerQuotaReader(Locator, Runner, Versions, () => OptedIn, Clock, clientVersion: "1.2.3");
        }

        public Session Session { get; }

        public Runner Runner { get; }

        public Locator Locator { get; }

        public VersionRunner Versions { get; }

        public FakeTimeProvider Clock { get; }

        public bool OptedIn { get; set; }

        public CodexAppServerQuotaReader Reader { get; }

        public ProcessSpec Spec => Assert.Single(Runner.Specs);
    }

    [Fact]
    public async Task Without_the_opt_in_nothing_is_launched_nor_looked_up()
    {
        var world = new World(optedIn: false);

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.NotOptedIn, result.Status);
        Assert.Empty(world.Runner.Specs);
        Assert.Empty(world.Versions.Specs);
        Assert.Empty(world.Locator.Asked);
        Assert.Equal(ProviderId.CodexCli, world.Reader.Provider);
    }

    [Fact]
    public async Task It_writes_exactly_the_three_messages_in_order_and_nothing_that_spends_or_sends()
    {
        var world = new World();

        await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        var written = world.Spec.StandardInput!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExpectedMessages, written);
        Assert.DoesNotContain("rateLimitResetCredit/consume", world.Spec.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("sendAddCreditsNudgeEmail", world.Spec.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("supportsLunaReserve", world.Spec.StandardInput, StringComparison.Ordinal);

        // Measured on 0.160.0: at the end of its input the app-server exits without answering the read.
        Assert.True(world.Spec.KeepStandardInputOpen);
    }

    [Fact]
    public async Task The_app_server_runs_natively_over_stdio_in_an_empty_folder_of_the_run_that_is_removed_after()
    {
        var world = new World();

        await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        var spec = world.Spec;
        Assert.Equal(Codex, spec.FileName);
        Assert.Equal(["app-server", "--listen", "stdio://"], spec.Arguments);
        Assert.True(world.Session.StartedInEmptyFolder);
        Assert.True(LiveFolderUnder(spec.WorkingDirectory!, RunTempFolder.BaseDirectory), spec.WorkingDirectory);
        Assert.False(Directory.Exists(spec.WorkingDirectory));
        Assert.NotNull(spec.Timeout);
    }

    [Fact]
    public async Task The_answer_is_read_by_the_whitelist_and_the_process_tree_is_ended()
    {
        var world = new World(stdout: ["""{"id":1,"result":{"userAgent":"x"}}""", Answer], hang: true);

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.Read, result.Status);
        var window = Assert.Single(result.Observations);
        Assert.Equal(("codex/primary", 0.25), (window.Window, window.Utilization.Value!.Value));
        Assert.Equal(world.Clock.GetUtcNow(), window.ObservedAt);
        Assert.Equal("plus", result.PlanType);
        Assert.Empty(result.Warnings);
        Assert.True(world.Session.Kills > 0 || world.Session.Disposed);
        Assert.True(world.Session.Disposed);
    }

    [Fact]
    public async Task Without_an_answer_in_twenty_seconds_it_is_missing_and_the_tree_is_ended()
    {
        var world = new World(stdout: ["""{"id":1,"result":{"userAgent":"x"}}"""], hang: true);

        var reading = world.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => world.Runner.Specs.Count == 1);
        world.Clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(reading.IsCompleted);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await reading;

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("20 s", result.Reason, StringComparison.Ordinal);
        Assert.True(world.Session.Disposed);
    }

    [Fact]
    public async Task An_app_server_that_ends_without_answering_is_missing()
    {
        var world = new World(stdout: ["""{"id":1,"result":{"userAgent":"x"}}"""]);

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("sin responder", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_an_api_key_the_quota_is_not_exposed()
    {
        var world = new World(stdout: ["""{"id":2,"error":{"code":-32600,"message":"chatgpt authentication required to read rate limits"}}"""]);

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal((QuotaReadStatus.NotExposed, "no expuesto: Codex usa API key"), (result.Status, result.Reason));
    }

    [Fact]
    public async Task A_version_without_a_tested_answer_is_read_with_a_warning()
    {
        var world = new World(version: "codex-cli 0.161.0");

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.Read, result.Status);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Versión de Codex no probada", warning, StringComparison.Ordinal);
        Assert.Contains("0.161.0", warning, StringComparison.Ordinal);
        Assert.Equal(["--version"], Assert.Single(world.Versions.Specs).Arguments);
    }

    [Fact]
    public async Task Without_codex_installed_it_is_missing_and_nothing_starts()
    {
        var world = new World(codex: null);

        var result = await world.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("no está instalado", result.Reason, StringComparison.Ordinal);
        Assert.Empty(world.Runner.Specs);
    }

    [Fact]
    public async Task An_app_server_that_cannot_start_is_missing_with_the_reason_and_leaves_no_folder()
    {
        var world = new World();
        var failing = new FailingRunner();
        var reader = new CodexAppServerQuotaReader(world.Locator, failing, world.Versions, () => true, world.Clock, clientVersion: "1.2.3");

        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("no se pudo arrancar", result.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(failing.Folder));
    }

    private sealed class FailingRunner : IStreamingProcessRunner
    {
        public string? Folder { get; private set; }

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Folder = spec.WorkingDirectory;
            throw new System.ComponentModel.Win32Exception(2, "El sistema no puede encontrar el archivo especificado.");
        }
    }

    [Fact]
    public async Task The_caller_can_cancel_and_the_tree_is_ended()
    {
        var world = new World(stdout: [], hang: true);
        using var cancel = new CancellationTokenSource();

        var reading = world.Reader.ReadAsync(cancel.Token);
        await WaitUntilAsync(() => world.Runner.Specs.Count == 1);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(world.Session.Disposed);
    }

    private static bool LiveFolderUnder(string folder, string root) =>
        Path.GetFullPath(folder).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition did not hold in time");
    }
}
