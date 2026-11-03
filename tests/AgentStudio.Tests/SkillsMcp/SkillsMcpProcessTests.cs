using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Skills;
using AgentStudio.SkillsMcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AgentStudio.Tests.SkillsMcp.SkillsMcpSamples;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// The real <c>AgentStudio.SkillsMcp.exe</c>, as Claude Code launches it: over stdio, with the absolute path of its
/// snapshot as the only argument and an empty folder as its working directory. It is Agent Studio's own process, not a
/// provider CLI, so it spends no quota; every process started here is ended by the test. The answers go through the
/// official MCP client of the SDK, so the protocol is the one a client speaks, not the one the server assumes.
/// </summary>
public sealed class SkillsMcpProcessTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "as mcp proceso " + Guid.NewGuid().ToString("N")[..12])).FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Copied next to the tests by the project reference (docs/04 section 16.9).</summary>
    internal static string ServerExecutable => Path.Combine(AppContext.BaseDirectory, "AgentStudio.SkillsMcp.exe");

    public void Dispose() => TestSupport.TempFolders.Delete(_root);

    [Fact]
    public async Task The_executable_serves_its_snapshot_over_stdio_and_writes_nothing()
    {
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(run, "work")).FullName;
        var snapshot = Path.Combine(run, "snapshot.json");
        await File.WriteAllTextAsync(snapshot, SkillCatalogSnapshotJson.Write(Snapshot()), Ct);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(snapshot, Ct));

        // Started without a shell, as a client that spawns the executable does. (The SDK's own StdioClientTransport wraps
        // the command in cmd.exe on Windows, which splits a path with spaces.)
        using var process = Start([SkillsMcpNames.SnapshotArgument, snapshot], work);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        IList<McpClientTool> tools;
        CallToolResult result;
        int exit;
        try
        {
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream), cancellationToken: Ct))
            {
                tools = await client.ListToolsAsync(cancellationToken: Ct);
                result = await client.CallToolAsync(SkillsMcpNames.SearchTool, new Dictionary<string, object?> { ["query"] = "wpf" }, cancellationToken: Ct);
            }

            // Closing its stdin ends the session: the server stops on its own. The client may have closed it already.
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            await process.WaitForExitAsync(Ct).WaitAsync(ProcessTimeout, Ct);
            exit = process.ExitCode;
            Assert.Equal(string.Empty, await stderr);
        }
        finally
        {
            Stop(process);
        }

        Assert.Equal(Program.Completed, exit);
        Assert.Equal([SkillsMcpNames.RecommendTool, SkillsMcpNames.SearchTool], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("wpf-mvvm", string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)), StringComparison.Ordinal);

        // Nothing written: the working directory stays empty and the run folder holds only its snapshot, unchanged.
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal([snapshot, work], Directory.EnumerateFileSystemEntries(run).Order(StringComparer.Ordinal));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(snapshot, Ct)));
    }

    [Fact]
    public async Task Without_its_snapshot_the_executable_refuses_to_start_and_says_why_on_stderr()
    {
        var (exit, stdout, stderr) = await RunAsync([]);

        Assert.Equal(Program.UsageError, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(SkillsMcpNames.SnapshotArgument, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_snapshot_that_does_not_read_the_executable_refuses_to_start()
    {
        var invalid = Path.Combine(_root, "invalid.json");
        await File.WriteAllTextAsync(invalid, "{\"schema\":\"otra\"}", Ct);

        var (exit, stdout, stderr) = await RunAsync([SkillsMcpNames.SnapshotArgument, invalid]);

        Assert.Equal(Program.SnapshotError, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
    }

    /// <summary>Runs the executable with stdin closed at once; it is killed if it does not end within the timeout.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] arguments)
    {
        using var process = Start(arguments, _root);
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(Ct);
            var stderr = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct).WaitAsync(ProcessTimeout, Ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>The server with redirected stdio and no window or shell; each argument is passed as it is.</summary>
    private static Process Start(string[] arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(ServerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)!;
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
    }
}
