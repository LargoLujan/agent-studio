using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.SkillsMcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AgentStudio.Tests.SkillsMcp.ProjectHistorySamples;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// The real <c>AgentStudio.SkillsMcp.exe</c> with the history of a project (docs/17 D-06, F12-9), as a CLI launches it for
/// the planner: over stdio, with the absolute path of the history snapshot (<c>--history</c>), alone or with the skills
/// catalog, and an empty folder as its working directory. It is Agent Studio's own process, not a provider CLI, so it
/// spends no quota; every process started here is ended by the test. The server reads its snapshots and writes nothing.
/// </summary>
public sealed class ProjectHistoryProcessTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "as historia proceso " + Guid.NewGuid().ToString("N")[..12])).FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestSupport.TempFolders.Delete(_root);

    [Fact]
    public async Task The_executable_serves_the_history_alone_over_stdio_and_writes_nothing()
    {
        var run = Directory.CreateDirectory(Path.Combine(_root, "run ñ")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(run, "work")).FullName;
        var history = Path.Combine(run, "history.json");
        await File.WriteAllTextAsync(history, ProjectHistorySnapshotJson.Write(WithVectors()), Ct);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(history, Ct));

        var (tools, text, exit, stderr) = await ServeAsync([ProjectHistoryNames.SnapshotArgument, history], work, ProjectHistoryNames.Tool, new Dictionary<string, object?> { ["query"] = "división entre cero", ["top"] = 3 });

        Assert.Equal(Program.Completed, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal([ProjectHistoryNames.Tool], tools);
        Assert.Contains(DivideByZero.Id, text, StringComparison.Ordinal);
        Assert.Contains(Plan.Id, text, StringComparison.Ordinal);

        // Nothing written: the working directory stays empty and the run folder holds only its snapshot, unchanged.
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal([history, work], Directory.EnumerateFileSystemEntries(run).Order(StringComparer.Ordinal));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(history, Ct)));
    }

    [Fact]
    public async Task With_the_catalog_and_the_history_the_executable_offers_the_three_tools()
    {
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(run, "work")).FullName;
        var history = Path.Combine(run, "history.json");
        var skills = Path.Combine(run, "snapshot.json");
        await File.WriteAllTextAsync(history, ProjectHistorySnapshotJson.Write(WithoutVectors()), Ct);
        await File.WriteAllTextAsync(skills, SkillCatalogSnapshotJson.Write(SkillsMcpSamples.Snapshot()), Ct);

        var (tools, text, exit, _) = await ServeAsync(
            [ProjectHistoryNames.SnapshotArgument, history, SkillsMcpNames.SnapshotArgument, skills],
            work,
            ProjectHistoryNames.Tool,
            new Dictionary<string, object?> { ["query"] = "worktrees" });

        Assert.Equal(Program.Completed, exit);
        Assert.Equal([SkillsMcpNames.RecommendTool, ProjectHistoryNames.Tool, SkillsMcpNames.SearchTool], tools.Order(StringComparer.Ordinal));
        Assert.Contains(Worktrees.Id, text, StringComparison.Ordinal);
        Assert.Contains("Solo por palabras", text, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task With_a_history_that_does_not_read_the_executable_refuses_to_start()
    {
        var invalid = Path.Combine(_root, "history.json");
        await File.WriteAllTextAsync(invalid, "{\"schema\":\"agentstudio-history-snapshot/1\",\"fragments\":[{\"id\":\"mem:a#0\"}]}", Ct);

        var (exit, stdout, stderr) = await RunAsync([ProjectHistoryNames.SnapshotArgument, invalid]);

        Assert.Equal(Program.SnapshotError, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public async Task With_a_relative_history_path_the_executable_refuses_to_start_and_says_why()
    {
        var (exit, stdout, stderr) = await RunAsync([ProjectHistoryNames.SnapshotArgument, "history.json"]);

        Assert.Equal(Program.UsageError, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(ProjectHistoryNames.SnapshotArgument, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts the server, lists its tools, calls one, closes its stdin and waits for it to end: the names of its tools, the
    /// text of the answer, its exit code and what it wrote on stderr.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Tools, string Text, int Exit, string Stderr)> ServeAsync(
        string[] arguments, string workingDirectory, string tool, IReadOnlyDictionary<string, object?> callArguments)
    {
        using var process = SkillsMcpProcessTests.Start(arguments, workingDirectory);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            IList<McpClientTool> tools;
            CallToolResult result;
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream), cancellationToken: Ct))
            {
                tools = await client.ListToolsAsync(cancellationToken: Ct);
                result = await client.CallToolAsync(tool, callArguments, cancellationToken: Ct);
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
                // The client closed it already.
            }

            await process.WaitForExitAsync(Ct).WaitAsync(ProcessTimeout, Ct);
            Assert.NotEqual(true, result.IsError);
            return ([.. tools.Select(t => t.Name)], string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)), process.ExitCode, await stderr);
        }
        finally
        {
            SkillsMcpProcessTests.Stop(process);
        }
    }

    /// <summary>Runs the executable with stdin closed at once; it is killed if it does not end within the timeout.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] arguments)
    {
        using var process = SkillsMcpProcessTests.Start(arguments, _root);
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
            SkillsMcpProcessTests.Stop(process);
        }
    }
}
