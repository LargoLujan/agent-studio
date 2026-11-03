using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Processes;

public sealed class StreamingProcessRunnerTests
{
    private readonly StreamingProcessRunner _runner = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ProcessSpec Cmd(string script, TimeSpan? timeout = null, string? stdin = null) =>
        new("cmd.exe", ["/c", script], Timeout: timeout, StandardInput: stdin);

    private static async Task<List<ProcessOutputLine>> ReadAllAsync(IProcessSession session)
    {
        var lines = new List<ProcessOutputLine>();
        await foreach (var line in session.Output.WithCancellation(Token))
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (!IsRunning(pid))
            {
                return true;
            }

            await Task.Delay(50, Token);
        }

        return !IsRunning(pid);
    }

    [Fact]
    public async Task Streams_stdout_and_stderr_lines_in_order()
    {
        await using var session = await _runner.StartAsync(Cmd("echo uno&echo dos&echo error>&2&echo tres"), Token);

        var lines = await ReadAllAsync(session);

        Assert.Equal(["uno", "dos", "tres"], lines.Where(l => l.Stream == ProcessStream.StandardOutput).Select(l => l.Text));
        Assert.Equal(["error"], lines.Where(l => l.Stream == ProcessStream.StandardError).Select(l => l.Text));
        Assert.All(lines, l => Assert.True(l.At > DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(0, await session.Exited);
    }

    [Fact]
    public async Task Keeps_a_final_line_without_newline()
    {
        await using var session = await _runner.StartAsync(
            new ProcessSpec("pwsh", ["-NoProfile", "-Command", "[Console]::Out.Write(\"a`nultima\")"]), Token);

        var lines = await ReadAllAsync(session);

        Assert.Equal(["a", "ultima"], lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Feeds_standard_input_to_the_process()
    {
        await using var session = await _runner.StartAsync(
            new ProcessSpec("findstr", ["^"], StandardInput: "hola ñandú\nsegunda\n"), Token);

        var lines = await ReadAllAsync(session);

        Assert.Equal(["hola ñandú", "segunda"], lines.Select(l => l.Text));
        Assert.Equal(0, await session.Exited);
    }

    [Fact]
    public async Task Closes_standard_input_after_writing_it_by_default()
    {
        // Two reads and one line: with the input closed, the second read gets its end and the process ends.
        await using var session = await _runner.StartAsync(Cmd("set /p a= & set /p b=", TimeSpan.FromSeconds(30), "uno\n"), Token);

        var exited = await Task.WhenAny(session.Exited, Task.Delay(TimeSpan.FromSeconds(20), Token));

        Assert.Same(session.Exited, exited);
        Assert.NotEqual(-1, await session.Exited);
    }

    [Fact]
    public async Task Keeps_standard_input_open_when_asked_until_the_session_ends_the_tree()
    {
        var spec = Cmd("set /p a= & set /p b=", TimeSpan.FromSeconds(30), "uno\n") with { KeepStandardInputOpen = true };
        var session = await _runner.StartAsync(spec, Token);
        var pid = session.ProcessId;

        // The second read waits for more input: the process is still there.
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        Assert.False(session.Exited.IsCompleted);

        await session.DisposeAsync();

        Assert.True(session.Exited.IsCompleted);
        Assert.Equal(-1, await session.Exited);
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task Reports_the_exit_code()
    {
        await using var session = await _runner.StartAsync(Cmd("exit 7"), Token);

        Assert.Equal(7, await session.Exited);
    }

    [Fact]
    public async Task Timeout_kills_the_tree_and_exits_with_minus_one()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var session = await _runner.StartAsync(Cmd("ping -n 30 127.0.0.1", TimeSpan.FromSeconds(1)), Token);

        var code = await session.Exited;

        Assert.Equal(-1, code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task KillAsync_is_idempotent()
    {
        await using var session = await _runner.StartAsync(Cmd("ping -n 30 127.0.0.1"), Token);

        await session.KillAsync();
        await session.KillAsync();

        Assert.Equal(-1, await session.Exited);
        Assert.False(IsRunning(session.ProcessId));
    }

    [Fact]
    public async Task DisposeAsync_kills_the_process_tree()
    {
        var session = await _runner.StartAsync(Cmd("ping -n 30 127.0.0.1"), Token);
        var pid = session.ProcessId;
        Assert.True(IsRunning(pid));

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(await WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelling_the_start_token_after_start_does_not_kill_the_process()
    {
        using var cts = new CancellationTokenSource();
        await using var session = await _runner.StartAsync(Cmd("ping -n 3 127.0.0.1 >nul& exit 3"), cts.Token);

        await cts.CancelAsync();

        Assert.Equal(3, await session.Exited);
    }

    [Fact]
    public async Task An_already_cancelled_token_prevents_the_start()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.StartAsync(Cmd("exit 0"), cts.Token));
    }

    [Fact]
    public async Task Streams_100000_lines_in_order_with_a_slow_consumer()
    {
        await using var session = await _runner.StartAsync(
            new ProcessSpec("pwsh", ["-NoProfile", "-Command", "1..100000 | % { $_ }"]), Token);

        await Task.Delay(1500, Token); // Let the backlog build before reading.
        var lines = await ReadAllAsync(session);

        Assert.Equal(100000, lines.Count);
        Assert.Equal(Enumerable.Range(1, 100000).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), lines.Select(l => l.Text));
    }

    [Fact]
    public async Task A_giant_line_is_discarded_with_a_marker_and_following_lines_still_arrive()
    {
        var runner = new StreamingProcessRunner(maxLineLength: 100_000);
        const string Script = "[Console]::Out.Write('a' * 3000000); [Console]::Out.WriteLine(); 'before'; [Console]::Out.Write('b' * 150000); [Console]::Out.WriteLine(); 'tail'";
        await using var session = await runner.StartAsync(
            new ProcessSpec("pwsh", ["-NoProfile", "-Command", Script]), Token);

        var lines = await ReadAllAsync(session);

        Assert.DoesNotContain(lines, l => l.Text.Length > 100_000);
        var markers = lines.Where(l => l.Stream == ProcessStream.StandardError && l.Text.StartsWith("[agent-studio]", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, markers.Count);
        Assert.Contains("3000000", markers[0].Text, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Text == "before");
        Assert.Contains(lines, l => l.Text == "tail");
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u001bb")]
    public async Task Rejects_control_characters_in_arguments_of_native_executables(string argument)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.StartAsync(new ProcessSpec("cmd.exe", ["/c", argument]), Token));
    }

    [Fact]
    public async Task Rejects_control_characters_in_the_executable_path()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.StartAsync(new ProcessSpec("cmd\0.exe", []), Token));
    }

    [Fact]
    public async Task Rejects_cmd_metacharacters_in_batch_file_arguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.StartAsync(new ProcessSpec("tool.cmd", ["a&b"]), Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.StartAsync(new ProcessSpec("tool.cmd. ", ["a|b"]), Token));
    }
}
