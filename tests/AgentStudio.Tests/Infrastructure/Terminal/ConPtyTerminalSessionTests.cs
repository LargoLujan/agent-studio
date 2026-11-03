using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Terminal;

namespace AgentStudio.Tests.Infrastructure.Terminal;

/// <summary>Real ConPTY sessions with real shells: nothing here is faked.</summary>
[Trait("Category", "ConPty")]
public sealed class ConPtyTerminalSessionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly ConPtyTerminalLauncher Launcher = new();

    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static ITerminalSession Start(string shell, params string[] arguments) =>
        Launcher.Start(new TerminalStartInfo(shell, arguments, Path.GetTempPath(), 120, 30));

    private static async Task<string> CollectAsync(ITerminalSession session, Func<string, bool>? stopWhen = null)
    {
        var text = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Limit);
        await foreach (var chunk in session.Output.WithCancellation(timeout.Token))
        {
            text.Append(chunk);
            if (stopWhen?.Invoke(text.ToString()) == true)
            {
                break;
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task Cmd_echo_produces_output_and_exit_code_zero()
    {
        await using var session = Start(Cmd, "/d", "/c", "echo hola");

        var output = await CollectAsync(session);
        var code = await session.Exited.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Contains("hola", output);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Interactive_cmd_returns_the_exit_code_it_is_told()
    {
        await using var session = Start(Cmd, "/d", "/k");

        await session.WriteAsync("exit 3\r", TestContext.Current.CancellationToken);
        var code = await session.Exited.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(3, code);
    }

    [Fact]
    public async Task Pwsh_output_keeps_non_ascii_characters_as_utf8()
    {
        var pwsh = new ExecutableLocator().Find("pwsh");
        Assert.NotNull(pwsh);
        await using var session = Start(pwsh, "-NoProfile", "-Command", "Write-Output 'ñandú'");

        var output = await CollectAsync(session);
        var code = await session.Exited.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Contains("ñandú", output);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Interactive_session_echoes_input_written_later()
    {
        await using var session = Start(Cmd, "/d", "/k");

        await session.WriteAsync("echo marcador-" + "123\r", TestContext.Current.CancellationToken);
        var output = await CollectAsync(session, text => text.Contains("marcador-123", StringComparison.Ordinal)
            && text.IndexOf("marcador-123", StringComparison.Ordinal) != text.LastIndexOf("marcador-123", StringComparison.Ordinal));

        Assert.Contains("marcador-123", output);
    }

    [Fact]
    public async Task KillAsync_ends_a_long_running_process_quickly()
    {
        await using var session = Start(Cmd, "/d", "/c", "ping -n 30 127.0.0.1");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await session.KillAsync();
        var code = await session.Exited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}");
        Assert.Equal(-1, code);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(session.ProcessId).Id.ToString());
    }

    [Fact]
    public async Task Resize_on_a_live_session_does_not_throw_and_changes_the_size()
    {
        await using var session = Start(Cmd, "/d", "/k");

        session.Resize(80, 24);
        session.Resize(200, 50);
        await session.WriteAsync("mode con\r", TestContext.Current.CancellationToken);
        // "mode con" labels are localised (Columns/Columnas), so only the "label: value" shape is matched.
        var output = await CollectAsync(session, text => Regex.IsMatch(text, @":\s+200\b") && Regex.IsMatch(text, @":\s+50\b"));

        Assert.Matches(@":\s+200\b", output);
        Assert.Matches(@":\s+50\b", output);
    }

    [Fact]
    public async Task Resize_and_write_after_the_session_ended_are_safe()
    {
        var session = Start(Cmd, "/d", "/c", "exit 0");
        _ = await session.Exited.WaitAsync(Limit, TestContext.Current.CancellationToken);

        session.Resize(100, 40);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WriteAsync("x", TestContext.Current.CancellationToken));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_kills_the_process_tree_and_does_not_leak()
    {
        var session = Start(Cmd, "/d", "/k", "ping -n 60 127.0.0.1");
        var processId = session.ProcessId;
        await Task.Delay(500, TestContext.Current.CancellationToken);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(session.Exited.IsCompleted);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        await session.DisposeAsync(); // idempotent
    }

    [Fact]
    public void Starting_a_missing_shell_throws_a_clear_error()
    {
        var ex = Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() =>
            Start(Path.Combine(Path.GetTempPath(), "no-such-shell-" + Guid.NewGuid().ToString("N") + ".exe")));

        Assert.Contains("Cannot start", ex.Message);
    }

    [Fact]
    public async Task Closing_the_job_kills_the_shell_and_a_grandchild_it_started()
    {
        var token = TestContext.Current.CancellationToken;
        var pwsh = new ExecutableLocator().Find("pwsh");
        Assert.NotNull(pwsh);
        var job = new JobObject();
        var launcher = new ConPtyTerminalLauncher(job);
        ITerminalSession? session = null;
        try
        {
            session = launcher.Start(new TerminalStartInfo(
                pwsh,
                ["-NoProfile", "-Command", "$p = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -PassThru -WindowStyle Hidden; 'GRANDCHILD=' + $p.Id + ';'; Start-Sleep 60"],
                Path.GetTempPath(),
                120,
                30));

            var output = await CollectAsync(session, text => Regex.IsMatch(text, @"GRANDCHILD=\d+;"));
            var grandchildPid = int.Parse(Regex.Match(output, @"GRANDCHILD=(\d+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            using var grandchild = Process.GetProcessById(grandchildPid);
            Assert.False(grandchild.HasExited);

            // Simulates Agent Studio dying: the OS kills everything in the job.
            job.Dispose();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            await grandchild.WaitForExitAsync(limit.Token);
            await session.Exited.WaitAsync(Limit, token);
            Assert.True(grandchild.HasExited);
        }
        finally
        {
            job.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }
}
