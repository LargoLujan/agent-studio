using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;
using AgentStudio.GraphWorker;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.GraphWorker;

/// <summary>
/// docs/20 D-18 and §9.2 (P0 of Phase 6, ADR-0062): the smoke test of the protocol of the analysis process. The skeleton analyzes nothing, so what is
/// proved here is the contract the host and WP-G build on: the arguments, the exit codes, that the standard output is the model or nothing, that progress
/// goes to the standard error, and that a failure never carries a path or text of the repository. It is driven over in-memory writers, as the tests of
/// the MCP server are, and once as the real executable that ships next to the app.
/// </summary>
public sealed class GraphWorkerProtocolSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentstudio-graphworker-" + Guid.NewGuid().ToString("N"));

    public GraphWorkerProtocolSmokeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] Arguments(params string[] skipped) => [.. new GraphWorkerRequest(_root, CommitA, skipped).ToArguments()];

    private static readonly CodeGraph Model = new(
        CommitA,
        1,
        [new GraphNode("S.slnx", GraphNodeKind.Solution, "S", "S.slnx", false), new GraphNode("src/A/A.csproj", GraphNodeKind.Project, "A", "src/A/A.csproj", false)],
        [new GraphEdge("S.slnx", "src/A/A.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static)],
        []);

    private sealed class FakeAnalysis(Func<GraphWorkerRequest, Action<string>, CodeGraph> analyze) : IGraphAnalysis
    {
        public GraphWorkerRequest? Received { get; private set; }

        public Task<GraphBuild> AnalyzeAsync(GraphWorkerRequest request, Action<string> fileStarted, CancellationToken ct)
        {
            Received = request;
            return Task.FromResult(new GraphBuild(analyze(request, fileStarted), NamespaceFiles.Empty));
        }
    }

    private static async Task<(int Code, string Out, string Error)> RunAsync(string[] args, IGraphAnalysis analysis)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = await GraphWorkerRunner.RunAsync(args, output, error, analysis, Ct);
        return (code, output.ToString(), error.ToString());
    }

    // ---- in memory ----

    [Fact]
    public async Task A_completed_analysis_writes_the_model_and_only_the_model_to_the_standard_output()
    {
        var analysis = new FakeAnalysis((_, _) => Model);

        var (code, output, error) = await RunAsync(Arguments(), analysis);

        Assert.Equal(GraphWorkerExitCodes.Completed, code);
        Assert.Equal(CodeGraphJson.Serialize(Model), output);
        Assert.Equal(Model, CodeGraphJson.Parse(output));
        Assert.Equal(string.Empty, error);
        Assert.Equal(new GraphWorkerRequest(_root, CommitA), analysis.Received);
    }

    [Fact]
    public async Task The_files_to_skip_reach_the_analysis_as_the_host_wrote_them()
    {
        var analysis = new FakeAnalysis((_, _) => Model);

        await RunAsync(Arguments("src/Big.cs", "src/Deep.cs"), analysis);

        Assert.Equal(["src/Big.cs", "src/Deep.cs"], analysis.Received!.Skipped);
    }

    [Fact]
    public async Task Each_file_the_analysis_starts_on_is_a_progress_line_on_the_standard_error_before_the_model_is_written()
    {
        var analysis = new FakeAnalysis((_, started) =>
        {
            started("src/A/One.cs");
            started("src/A/Two.cs");
            return Model;
        });

        var (code, output, error) = await RunAsync(Arguments(), analysis);

        Assert.Equal(GraphWorkerExitCodes.Completed, code);
        Assert.Equal(["progress:src/A/One.cs", "progress:src/A/Two.cs"], error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.All(error.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(GraphWorkerProtocol.TryParseProgress(line, out _), line));
        Assert.DoesNotContain("progress", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_skeleton_analyzes_nothing_and_says_so_with_its_own_exit_code_and_an_empty_standard_output()
    {
        var (code, output, error) = await RunAsync(Arguments(), new UnavailableGraphAnalysis());

        Assert.Equal(GraphWorkerExitCodes.AnalysisUnavailable, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("not implemented", error, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> NotTheProtocol()
    {
        var data = new TheoryData<string[]>();
        foreach (var arguments in new string[][] { [], ["--root"], ["--unknown", "x"], ["--root", "relative", "--commit", "abc"] })
        {
            data.Add(arguments);
        }

        return data;
    }
    [Theory]
    [MemberData(nameof(NotTheProtocol))]
    public async Task A_command_line_that_is_not_the_protocol_ends_with_the_usage_and_nothing_on_the_standard_output(string[] args)
    {
        var analysis = new FakeAnalysis((_, _) => Model);

        var (code, output, error) = await RunAsync(args, analysis);

        Assert.Equal(GraphWorkerExitCodes.UsageError, code);
        Assert.Equal(string.Empty, output);
        Assert.StartsWith("Usage:", error, StringComparison.Ordinal);
        Assert.Null(analysis.Received);
    }

    [Fact]
    public async Task A_root_that_is_not_a_folder_ends_with_its_own_exit_code_before_the_analysis_starts()
    {
        var analysis = new FakeAnalysis((_, _) => Model);
        var missing = new GraphWorkerRequest(Path.Combine(_root, "missing"), CommitA).ToArguments();

        var (code, output, error) = await RunAsync([.. missing], analysis);

        Assert.Equal(GraphWorkerExitCodes.RootError, code);
        Assert.Equal(string.Empty, output);
        Assert.DoesNotContain(_root, error, StringComparison.Ordinal);
        Assert.Null(analysis.Received);
    }

    [Fact]
    public async Task A_failure_of_the_analysis_ends_with_its_exit_code_and_names_the_type_never_the_message_with_a_path_or_text_of_the_repository()
    {
        var analysis = new FakeAnalysis((_, _) => throw new InvalidOperationException(@"Could not read C:\Users\someone\secret\file.cs: password=hunter2"));

        var (code, output, error) = await RunAsync(Arguments(), analysis);

        Assert.Equal(GraphWorkerExitCodes.Failed, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("InvalidOperationException", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_of_another_commit_than_the_one_asked_is_a_failure_and_is_not_written()
    {
        var analysis = new FakeAnalysis((_, _) => new CodeGraph(CommitB, 1, Model.Nodes, Model.Edges, Model.Gaps));

        var (code, output, _) = await RunAsync(Arguments(), analysis);

        Assert.Equal(GraphWorkerExitCodes.Failed, code);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task A_cancellation_is_not_swallowed_as_a_failure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var analysis = new FakeAnalysis((_, _) => throw new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GraphWorkerRunner.RunAsync(Arguments(), new StringWriter(), new StringWriter(), analysis, cancelled.Token));
    }

    // ---- the real executable ----

    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "AgentStudio.GraphWorker.exe");

    [Fact]
    public void The_executable_and_the_parser_it_needs_ship_next_to_the_app()
    {
        Assert.True(File.Exists(ExecutablePath), ExecutablePath);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "AgentStudio.GraphWorker.dll")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "AgentStudio.GraphWorker.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.CodeAnalysis.CSharp.dll")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.CodeAnalysis.dll")));
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task The_real_executable_keeps_the_protocol_it_ends_with_the_usage_without_arguments_and_writes_the_model_of_an_empty_copy()
    {
        var (usageCode, usageOut, usageError) = await LaunchAsync([]);
        var (code, output, error) = await LaunchAsync(Arguments());

        Assert.Equal(GraphWorkerExitCodes.UsageError, usageCode);
        Assert.Equal(string.Empty, usageOut);
        Assert.StartsWith("Usage:", usageError, StringComparison.Ordinal);
        Assert.Equal(GraphWorkerExitCodes.Completed, code);
        Assert.Empty(CodeGraphJson.Parse(output).Nodes);
        Assert.Equal(string.Empty, error);
    }

    private static async Task<(int Code, string Out, string Error)> LaunchAsync(string[] args)
    {
        var start = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output, await error);
    }
}
