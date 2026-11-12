using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;
using AgentStudio.Infrastructure.Graph;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Graph;

/// <summary>
/// docs/20 D-18 (WP-G): the host of the analysis process. The loop that relaunches it is driven with a launcher that answers what the test scripts; the bounded
/// readers over in-memory streams. The process itself, at low integrity, is in <c>GraphWorkerLocalToolsTests</c>.
/// </summary>
public sealed class GraphWorkerHostTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "as-graph-host", "v-0123456789ab", "src");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CodeGraph Model = new(
        CommitA,
        1,
        [new GraphNode("S.slnx", GraphNodeKind.Solution, "S", "S.slnx", false)],
        [],
        []);

    private sealed class FakeSnapshot : IRepositorySnapshot
    {
        public string Commit => CommitA;

        public string Root => GraphWorkerHostTests.Root;

        public IReadOnlyList<string> Paths => [];

        public Stream OpenRead(string relativePath, long maxBytes) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedLauncher(params GraphWorkerLaunch[] launches) : IGraphWorkerLauncher
    {
        private int _next;

        public List<GraphWorkerRequest> Requests { get; } = [];

        public Task<GraphWorkerLaunch> LaunchAsync(GraphWorkerRequest request, string workingDirectory, string tempDirectory, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(launches[Math.Min(_next++, launches.Length - 1)]);
        }
    }

    private static GraphWorkerLaunch Done(CodeGraph? model = null) => new(GraphWorkerExitCodes.Completed, CodeGraphJson.Serialize(model ?? Model), null, false, false);

    private static GraphWorkerLaunch Crash(string? lastFile) => new(unchecked((int)0xC00000FD), null, lastFile, false, false);

    private static Task<GraphBuild> BuildAsync(ScriptedLauncher launcher) => new GraphWorkerHost(launcher).BuildAsync(new FakeSnapshot(), Ct);

    [Fact]
    public async Task A_launch_that_completes_gives_the_model_and_the_process_is_launched_once_with_the_root_the_commit_and_nothing_to_skip()
    {
        var launcher = new ScriptedLauncher(Done());

        var build = await BuildAsync(launcher);

        Assert.Equal(Model, build.Graph);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(new GraphWorkerRequest(Root, CommitA), request);
    }

    [Fact]
    public async Task A_process_that_dies_is_launched_again_skipping_the_file_it_was_on()
    {
        var launcher = new ScriptedLauncher(Crash("src/Core/Deep.cs"), Done());

        var build = await BuildAsync(launcher);

        Assert.Equal(Model, build.Graph);
        Assert.Equal([[], ["src/Core/Deep.cs"]], launcher.Requests.Select(r => r.Skipped.ToArray()));
    }

    [Fact]
    public async Task Each_relaunch_adds_the_file_that_killed_the_last_one_up_to_three_times_and_then_the_analysis_fails()
    {
        var launcher = new ScriptedLauncher(Crash("a.cs"), Crash("b.cs"), Crash("c.cs"), Crash("d.cs"));

        var failure = await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(launcher));

        Assert.Equal(GraphWorkerProtocol.MaxRelaunches + 1, launcher.Requests.Count);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], launcher.Requests[^1].Skipped);
        Assert.DoesNotContain("d.cs", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Three_relaunches_are_enough_when_the_fourth_launch_completes()
    {
        var launcher = new ScriptedLauncher(Crash("a.cs"), Crash("b.cs"), Crash("c.cs"), Done());

        var build = await BuildAsync(launcher);

        Assert.Equal(Model, build.Graph);
        Assert.Equal(4, launcher.Requests.Count);
    }

    [Fact]
    public async Task A_crash_with_no_file_to_name_or_on_a_file_already_skipped_is_not_launched_again()
    {
        var noFile = new ScriptedLauncher(Crash(null), Done());
        var sameFile = new ScriptedLauncher(Crash("a.cs"), Crash("a.cs"), Done());

        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(noFile));
        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(sameFile));

        Assert.Single(noFile.Requests);
        Assert.Equal(2, sameFile.Requests.Count);
    }

    [Theory]
    [InlineData(GraphWorkerExitCodes.UsageError)]
    [InlineData(GraphWorkerExitCodes.RootError)]
    [InlineData(GraphWorkerExitCodes.AnalysisUnavailable)]
    [InlineData(GraphWorkerExitCodes.Failed)]
    public async Task A_code_of_the_process_own_is_a_failed_analysis_that_is_not_launched_again(int code)
    {
        var launcher = new ScriptedLauncher(new GraphWorkerLaunch(code, null, "src/A.cs", false, false), Done());

        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(launcher));

        Assert.Single(launcher.Requests);
    }

    [Fact]
    public async Task A_timeout_or_an_output_over_the_cap_fails_the_analysis_without_relaunching()
    {
        var slow = new ScriptedLauncher(new GraphWorkerLaunch(-1, null, "src/A.cs", false, true), Done());
        var long_ = new ScriptedLauncher(new GraphWorkerLaunch(-1, null, "src/A.cs", true, false), Done());

        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(slow));
        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(long_));

        Assert.Single(slow.Requests);
        Assert.Single(long_.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":1,\"commit\":\"1111111111111111111111111111111111111111\",\"generator\":1,\"nodes\":[],\"edges\":[],\"gaps\":[],\"extra\":1}")]
    public async Task An_output_that_is_not_a_model_of_the_schema_fails_the_analysis_without_quoting_it(string output)
    {
        var launcher = new ScriptedLauncher(new GraphWorkerLaunch(GraphWorkerExitCodes.Completed, output, null, false, false));

        var failure = await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(launcher));

        Assert.DoesNotContain("not json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_of_another_commit_than_the_one_asked_fails_the_analysis()
    {
        var other = new CodeGraph(CommitB, 1, Model.Nodes, Model.Edges, Model.Gaps);

        await Assert.ThrowsAsync<GraphAnalysisException>(() => BuildAsync(new ScriptedLauncher(Done(other))));
    }

    [Fact]
    public async Task The_files_of_the_namespaces_come_with_the_model()
    {
        var graph = new CodeGraph(
            CommitA,
            1,
            [
                new GraphNode("a/A.csproj", GraphNodeKind.Project, "A", "a/A.csproj", false),
                new GraphNode("a/A.csproj|N", GraphNodeKind.Namespace, "N", null, false),
            ],
            [new GraphEdge("a/A.csproj", "a/A.csproj|N", GraphEdgeKind.Contains, GraphEdgeOrigin.Static)],
            []);
        var files = new NamespaceFiles(new Dictionary<string, IReadOnlyList<string>> { ["a/A.csproj|N"] = ["a/One.cs", "a/Two.cs"] });
        var launcher = new ScriptedLauncher(new GraphWorkerLaunch(GraphWorkerExitCodes.Completed, CodeGraphJson.Serialize(graph, files), null, false, false));

        var build = await BuildAsync(launcher);

        Assert.Equal(["a/One.cs", "a/Two.cs"], build.Files.For("a/A.csproj|N"));
    }

    // ---- the bounded readers ----

    private static StreamReader ReaderOf(string text) => new(new MemoryStream(new UTF8Encoding(false).GetBytes(text)), new UTF8Encoding(false));

    [Fact]
    public async Task The_standard_output_is_read_to_its_end_when_it_is_within_the_cap()
    {
        var read = await BoundedProcessReaders.ReadOutputAsync(ReaderOf("{\"a\":\"Núcleo\"}"), 1024, () => Assert.Fail("not exceeded"), Ct);

        Assert.Equal(new BoundedText("{\"a\":\"Núcleo\"}", false), read);
    }

    [Fact]
    public async Task The_cap_is_applied_while_the_output_is_read_by_bytes_the_reading_stops_and_the_process_is_told_to_die()
    {
        var killed = 0;
        // 40 000 characters of 2 bytes each: 80 000 bytes against a cap of 50 000, with an endless stream behind them.
        using var endless = new EndlessReader(new string('ñ', 40_000));

        var read = await BoundedProcessReaders.ReadOutputAsync(endless, 50_000, () => killed++, Ct);

        Assert.True(read.Exceeded);
        Assert.Equal(string.Empty, read.Text);
        Assert.Equal(1, killed);
        Assert.True(endless.CharactersGiven < 120_000, "it kept reading long after the cap");
    }

    [Fact]
    public async Task The_standard_error_gives_the_last_safe_progress_line_with_its_cr_trimmed_and_a_short_tail_of_the_rest()
    {
        var text = "progress:src/A.cs\r\nsomething said\r\nprogress:src/B/Deep.cs\r\n";

        var summary = await BoundedProcessReaders.ReadErrorAsync(ReaderOf(text), 1024, () => Assert.Fail("not exceeded"), Ct);

        Assert.Equal("src/B/Deep.cs", summary.LastProgress);
        Assert.Equal(["something said"], summary.Tail);
        Assert.False(summary.Exceeded);
    }

    [Theory]
    [InlineData("progress:..\\..\\secret.cs\n")]
    [InlineData("progress:/etc/passwd\n")]
    [InlineData("progress:C:\\Windows\\x.cs\n")]
    [InlineData("progress:src/a.cs")]
    [InlineData("progress:\n")]
    public async Task A_progress_line_with_a_path_that_is_not_safe_or_that_has_no_line_end_is_not_believed(string text)
    {
        var summary = await BoundedProcessReaders.ReadErrorAsync(ReaderOf("progress:ok/One.cs\n" + text), 1024, () => { }, Ct);

        Assert.Equal("ok/One.cs", summary.LastProgress);
    }

    [Fact]
    public async Task A_line_of_the_standard_error_is_bounded_and_a_long_progress_line_is_not_believed()
    {
        var text = "progress:" + new string('a', 5_000) + ".cs\nprogress:ok.cs\n" + new string('x', 100_000) + "\n";

        var summary = await BoundedProcessReaders.ReadErrorAsync(ReaderOf(text), 1_000_000, () => Assert.Fail("not exceeded"), Ct);

        Assert.Equal("ok.cs", summary.LastProgress);
        Assert.All(summary.Tail, line => Assert.True(line.Length <= 200));
    }

    [Fact]
    public async Task The_tail_of_the_standard_error_keeps_the_last_ten_lines_with_hidden_characters_cleaned()
    {
        var text = string.Concat(Enumerable.Range(1, 25).Select(i => $"line {i}\u202E\n"));

        var summary = await BoundedProcessReaders.ReadErrorAsync(ReaderOf(text), 1_000_000, () => { }, Ct);

        Assert.Equal(BoundedProcessReaders.MaxTailLines, summary.Tail.Count);
        Assert.Equal("line 25", summary.Tail[^1]);
        Assert.DoesNotContain(summary.Tail, line => line.Contains('\u202E', StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_standard_error_is_cut_too_when_it_passes_its_cap_and_keeps_the_last_file_it_named()
    {
        var killed = 0;
        using var endless = new EndlessReader("progress:last.cs\n" + new string('y', 30_000) + "\n");

        var summary = await BoundedProcessReaders.ReadErrorAsync(endless, 100_000, () => killed++, Ct);

        Assert.True(summary.Exceeded);
        Assert.Equal("last.cs", summary.LastProgress);
        Assert.Equal(1, killed);
    }

    /// <summary>A reader that never ends: it gives its text again and again, so a read that ignores the cap would never return.</summary>
    private sealed class EndlessReader(string text) : TextReader
    {
        private int _position;

        public int CharactersGiven { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, text.Length - _position);
            text.AsMemory(_position, count).CopyTo(buffer);
            _position = (_position + count) % text.Length;
            CharactersGiven += count;
            return ValueTask.FromResult(count);
        }
    }
}
