using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly ProcessRunner _runner = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "as-runner-" + Guid.NewGuid().ToString("N"));

    public ProcessRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<ProcessResult> Cmd(string script, CancellationToken cancellationToken) =>
        _runner.RunAsync(new ProcessSpec("cmd.exe", ["/c", script]), cancellationToken);

    [Fact]
    public async Task Returns_exit_code()
    {
        var result = await Cmd("exit 7", TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Captures_stdout_and_stderr_separately()
    {
        var result = await Cmd("echo hola& echo error 1>&2", TestContext.Current.CancellationToken);

        Assert.Equal("hola", result.StandardOutput.Trim());
        Assert.Equal("error", result.StandardError.Trim());
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Refuses_to_keep_standard_input_open_since_it_would_wait_for_the_timeout()
    {
        var spec = new ProcessSpec("cmd.exe", ["/c", "set /p a="], StandardInput: "uno\n", KeepStandardInputOpen: true);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync(spec, TestContext.Current.CancellationToken));

        Assert.Contains("streaming session", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_standard_input()
    {
        var spec = new ProcessSpec("cmd.exe", ["/c", "more"], StandardInput: "texto desde stdin\r\n");

        var result = await _runner.RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.Contains("texto desde stdin", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sets_and_removes_environment_variables()
    {
        Environment.SetEnvironmentVariable("AS_TEST_REMOVE", "present");
        try
        {
            var spec = new ProcessSpec(
                "cmd.exe",
                ["/c", "echo [%AS_TEST_SET%][%AS_TEST_REMOVE%]"],
                Environment: new Dictionary<string, string?> { ["AS_TEST_SET"] = "valor", ["AS_TEST_REMOVE"] = null });

            var result = await _runner.RunAsync(spec, TestContext.Current.CancellationToken);

            Assert.Contains("[valor][%AS_TEST_REMOVE%]", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AS_TEST_REMOVE", null);
        }
    }

    [Fact]
    public async Task Honours_working_directory()
    {
        var spec = new ProcessSpec("cmd.exe", ["/c", "cd"], WorkingDirectory: _dir);

        var result = await _runner.RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(_dir), result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task Kills_the_tree_and_reports_timeout()
    {
        var spec = new ProcessSpec("cmd.exe", ["/c", "echo antes& ping -n 30 127.0.0.1"], Timeout: TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var result = await _runner.RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}");
        Assert.Contains("antes", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_throws_and_kills_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var spec = new ProcessSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1"]);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(spec, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}");
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("a%b")]
    [InlineData("a\"b")]
    [InlineData("a!b")]
    [InlineData("a(b")]
    [InlineData("a)b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public async Task Rejects_cmd_metacharacters_for_batch_files(string argument)
    {
        var script = Path.Combine(_dir, "shim.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho ok\r\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.RunAsync(new ProcessSpec(script, [argument]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_cmd_metacharacters_when_the_batch_file_name_has_trailing_dots_or_spaces()
    {
        var script = Path.Combine(_dir, "shim.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho ok\r\n", TestContext.Current.CancellationToken);

        // Windows strips trailing dots and spaces, so "shim.cmd." still runs shim.cmd through cmd.exe.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.RunAsync(new ProcessSpec(script + ".", ["a&calc"]), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.RunAsync(new ProcessSpec(script + " . ", ["a&calc"]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Runs_batch_files_with_safe_arguments()
    {
        var script = Path.Combine(_dir, "shim.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho recibido %1\r\n", TestContext.Current.CancellationToken);

        var result = await _runner.RunAsync(new ProcessSpec(script, ["--version"]), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("recibido --version", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allows_metacharacters_for_non_batch_executables()
    {
        var result = await _runner.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", "echo", "a b&c"]), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("a b&c", result.StandardOutput, StringComparison.Ordinal);
    }
}
