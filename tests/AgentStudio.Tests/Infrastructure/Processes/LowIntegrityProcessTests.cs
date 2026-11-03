using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// Real processes started with <see cref="ProcessIsolation.LowIntegrity"/> (ADR-0017): the token is Low, the standard
/// streams behave as without isolation, Windows refuses the writes outside Low folders, only the standard handles are
/// inherited, and the Job Object of the session kills the whole tree. Needs no administrator rights.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class LowIntegrityProcessTests
{
    private const string LowLabel = "\"S-1-16-4096\"";

    private readonly StreamingProcessRunner _runner = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ProcessSpec Isolated(string fileName, params string[] arguments) =>
        new(fileName, arguments, Isolation: ProcessIsolation.LowIntegrity);

    private static ProcessSpec Pwsh(string script, ProcessIsolation isolation = ProcessIsolation.LowIntegrity) =>
        new("pwsh", ["-NoProfile", "-NonInteractive", "-Command", script], Isolation: isolation);

    private static async Task<List<ProcessOutputLine>> ReadAllAsync(IProcessSession session)
    {
        var lines = new List<ProcessOutputLine>();
        await foreach (var line in session.Output.WithCancellation(Token))
        {
            lines.Add(line);
        }

        return lines;
    }

    private static async Task<string> FirstLineAsync(IProcessSession session)
    {
        await foreach (var line in session.Output.WithCancellation(Token))
        {
            if (line.Stream == ProcessStream.StandardOutput && line.Text.Length > 0)
            {
                return line.Text;
            }
        }

        throw new InvalidOperationException("The process ended without writing a line.");
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

    // The grandchild writes into a pipe of its own, so the first line on stdout is always its process id.
    private const string SpawnsPing =
        "$i = [System.Diagnostics.ProcessStartInfo]::new('ping', '-n 60 127.0.0.1'); $i.UseShellExecute = $false; $i.RedirectStandardOutput = $true; "
        + "$p = [System.Diagnostics.Process]::Start($i); [Console]::Out.WriteLine($p.Id); [Console]::Out.Flush(); Start-Sleep -Seconds 60";

    [Fact]
    public async Task An_isolated_process_runs_with_a_Low_integrity_token_and_one_without_isolation_does_not()
    {
        await using var isolated = await _runner.StartAsync(Isolated("whoami.exe", "/groups", "/fo", "csv", "/nh"), Token);
        var isolatedGroups = string.Join('\n', (await ReadAllAsync(isolated)).Select(l => l.Text));
        await using var plain = await _runner.StartAsync(new ProcessSpec("whoami.exe", ["/groups", "/fo", "csv", "/nh"]), Token);
        var plainGroups = string.Join('\n', (await ReadAllAsync(plain)).Select(l => l.Text));

        Assert.Equal(0, await isolated.Exited);
        Assert.Contains(LowLabel, isolatedGroups, StringComparison.Ordinal);
        Assert.DoesNotContain(LowLabel, plainGroups, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_isolated_process_streams_stdout_and_stderr_in_order_and_reports_its_exit_code()
    {
        await using var session = await _runner.StartAsync(Isolated("cmd.exe", "/c", "echo uno&echo dos&echo error>&2&echo tres&exit 7"), Token);

        var lines = await ReadAllAsync(session);

        Assert.Equal(["uno", "dos", "tres"], lines.Where(l => l.Stream == ProcessStream.StandardOutput).Select(l => l.Text));
        Assert.Equal(["error"], lines.Where(l => l.Stream == ProcessStream.StandardError).Select(l => l.Text));
        Assert.Equal(7, await session.Exited);
    }

    [Fact]
    public async Task An_isolated_process_reads_its_standard_input()
    {
        await using var session = await _runner.StartAsync(
            new ProcessSpec("findstr", ["^"], StandardInput: "hola ñandú\nsegunda\n", Isolation: ProcessIsolation.LowIntegrity), Token);

        var lines = await ReadAllAsync(session);

        Assert.Equal(["hola ñandú", "segunda"], lines.Select(l => l.Text));
        Assert.Equal(0, await session.Exited);
    }

    [Fact]
    public async Task An_isolated_process_cannot_write_into_a_Medium_folder_but_can_write_into_a_Low_one()
    {
        var medium = Directory.CreateTempSubdirectory("as-lowil-medium-").FullName;

        // A folder created under LocalLow inherits the Low label Windows put on it: nothing is labelled by the test.
        var low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "as-lowil-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(low);
        try
        {
            var target = Path.Combine(medium, "pwned.txt");
            await using var denied = await _runner.StartAsync(Isolated("cmd.exe", "/c", "copy", "/y", "NUL", target), Token);
            await ReadAllAsync(denied);
            await using var allowed = await _runner.StartAsync(Isolated("cmd.exe", "/c", "copy", "/y", "NUL", Path.Combine(low, "written.txt")), Token);
            await ReadAllAsync(allowed);

            Assert.NotEqual(0, await denied.Exited);
            Assert.False(File.Exists(target), "Windows let a Low process write into a Medium folder.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(medium));
            Assert.Equal(0, await allowed.Exited);
            Assert.True(File.Exists(Path.Combine(low, "written.txt")));
        }
        finally
        {
            Directory.Delete(medium, recursive: true);
            Directory.Delete(low, recursive: true);
        }
    }

    [Fact]
    public async Task An_isolated_process_inherits_only_its_standard_handles()
    {
        // An inheritable handle of this process: a child started without isolation inherits it and reads the secret
        // through it; the isolated child must not even have it.
        const string ReadsTheInheritedPipe =
            "'INICIO'; try { $c = [System.IO.Pipes.AnonymousPipeClientStream]::new([System.IO.Pipes.PipeDirection]::In, $env:AS_PIPE_HANDLE); "
            + "'LEIDO:' + [System.IO.StreamReader]::new($c).ReadLine() } catch { 'SIN-HANDLE' }";

        async Task<string> RunAsync(ProcessIsolation isolation)
        {
            using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            // The timeout only guards against the handle number naming some other pipe of the child.
            var spec = Pwsh(ReadsTheInheritedPipe, isolation) with
            {
                Environment = new Dictionary<string, string?> { ["AS_PIPE_HANDLE"] = pipe.GetClientHandleAsString() },
                Timeout = TimeSpan.FromSeconds(60),
            };
            await using var session = await _runner.StartAsync(spec, Token);
            await using (var writer = new StreamWriter(pipe, leaveOpen: true))
            {
                await writer.WriteLineAsync("secreto".AsMemory(), Token);
            }

            pipe.DisposeLocalCopyOfClientHandle();
            var output = string.Join('\n', (await ReadAllAsync(session)).Select(l => l.Text));
            await session.Exited;
            return output;
        }

        var plain = await RunAsync(ProcessIsolation.None);
        var isolated = await RunAsync(ProcessIsolation.LowIntegrity);

        Assert.Contains("LEIDO:secreto", plain, StringComparison.Ordinal);
        Assert.Contains("INICIO", isolated, StringComparison.Ordinal);
        Assert.DoesNotContain("secreto", isolated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Killing_an_isolated_session_kills_the_process_and_its_grandchild()
    {
        await using var session = await _runner.StartAsync(Pwsh(SpawnsPing), Token);
        var grandchild = int.Parse(await FirstLineAsync(session), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(IsRunning(grandchild));

        await session.KillAsync();

        Assert.Equal(-1, await session.Exited);
        Assert.True(await WaitUntilGoneAsync(session.ProcessId, TimeSpan.FromSeconds(10)), "The isolated process survived the kill.");
        Assert.True(await WaitUntilGoneAsync(grandchild, TimeSpan.FromSeconds(10)), "Its grandchild survived the kill.");
    }

    [Fact]
    public async Task Disposing_an_isolated_session_closes_its_job_and_the_whole_tree_dies()
    {
        var session = await _runner.StartAsync(Pwsh(SpawnsPing), Token);
        var child = session.ProcessId;
        var grandchild = int.Parse(await FirstLineAsync(session), System.Globalization.CultureInfo.InvariantCulture);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(await WaitUntilGoneAsync(child, TimeSpan.FromSeconds(10)), "The isolated process survived the session.");
        Assert.True(await WaitUntilGoneAsync(grandchild, TimeSpan.FromSeconds(10)), "Its grandchild survived the session.");
    }

    [Fact]
    public async Task A_timeout_kills_an_isolated_tree_and_exits_with_minus_one()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var session = await _runner.StartAsync(
            new ProcessSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1"], Timeout: TimeSpan.FromSeconds(1), Isolation: ProcessIsolation.LowIntegrity), Token);

        Assert.Equal(-1, await session.Exited);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task The_runner_that_waits_for_the_end_isolates_as_well()
    {
        var result = await new ProcessRunner().RunAsync(Isolated("whoami.exe", "/groups", "/fo", "csv", "/nh"), Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(LowLabel, result.StandardOutput, StringComparison.Ordinal);
    }
}
