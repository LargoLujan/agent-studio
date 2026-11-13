using System.IO;
using System.Text;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;
using AgentStudio.GraphWorker;

namespace AgentStudio.Tests.GraphWorker;

/// <summary>
/// docs/20 D-18 (WP-G, hardening): every cap of the analysis truncates with a gap and goes on, and never throws; the parser runs where a deep file does not
/// bring the process down, and a parser that does not return in time is stopped by ending the process, which the host reads as a file to skip.
/// </summary>
public sealed class SolutionGraphAnalysisLimitsTests
{
    private static string Csproj(params string[] items) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" + string.Concat(items) + "</ItemGroup></Project>";

    private static void AssertLimitGap(GraphBuild build, string nodeId) =>
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit && g.NodeId == nodeId);

    [Fact]
    public async Task More_packages_than_the_cap_of_a_project_are_cut_with_a_gap_and_the_analysis_goes_on()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", Csproj(string.Concat(Enumerable.Range(0, 10).Select(i => $"<PackageReference Include=\"P{i}\" />"))));
        repository.Add("A/One.cs", "namespace A.One { }");

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxPackagesPerProject = 3 }));

        Assert.Equal(3, build.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Package));
        AssertLimitGap(build, "A/A.csproj");
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "A/A.csproj").Partial);
        Assert.Contains(build.Graph.Nodes, n => n.Name == "A.One");
    }

    [Fact]
    public async Task More_nodes_than_the_cap_are_cut_with_a_gap_and_what_was_kept_is_a_valid_graph()
    {
        using var repository = GraphTestRepository.FromSample();

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxNodes = 8 }));

        Assert.True(build.Graph.Nodes.Count <= 8);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
        Assert.NotEmpty(build.Graph.Edges);
    }

    [Fact]
    public async Task A_cap_of_nodes_reached_by_packages_alone_does_not_fail_the_analysis()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", Csproj(string.Concat(Enumerable.Range(0, 50).Select(i => $"<PackageReference Include=\"P{i}\" />"))));
        repository.Add("B/B.csproj", Csproj(string.Concat(Enumerable.Range(0, 50).Select(i => $"<PackageReference Include=\"Q{i}\" />"))));

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxNodes = 20 }));

        Assert.Equal(20, build.Graph.Nodes.Count);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
    }

    [Fact]
    public async Task More_relations_than_the_cap_are_cut_with_a_gap()
    {
        using var repository = GraphTestRepository.FromSample();

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxEdges = 4 }));

        Assert.Equal(4, build.Graph.Edges.Count);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
    }

    [Fact]
    public async Task The_list_of_files_of_the_namespaces_is_cut_at_its_cap_with_a_gap_and_the_model_is_whole()
    {
        using var repository = GraphTestRepository.FromSample();
        var whole = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis());

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxNamespaceFilesTotal = 2 }));

        Assert.True(build.Files.Entries.Values.Sum(v => v.Count) <= 2);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
        Assert.Equal(whole.Graph.Nodes.Count, build.Graph.Nodes.Count);
    }

    [Fact]
    public async Task A_namespace_in_more_files_than_its_list_holds_is_cut_with_a_gap()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", Csproj());
        for (var i = 0; i < NamespaceFiles.MaxFilesPerNamespace + 5; i++)
        {
            repository.Add($"A/F{i:D3}.cs", "namespace Shared { }");
        }

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis());

        Assert.Equal(NamespaceFiles.MaxFilesPerNamespace, build.Files.For("A/A.csproj|Shared").Count);
        AssertLimitGap(build, "A/A.csproj");
    }

    [Fact]
    public async Task More_namespaces_and_usings_in_a_file_than_its_caps_are_cut_with_a_gap_and_the_file_is_read()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", Csproj());
        repository.Add("A/Many.cs", string.Concat(Enumerable.Range(0, 10).Select(i => $"using U{i};\n")) + string.Concat(Enumerable.Range(0, 10).Select(i => $"namespace N{i} {{ }}\n")));

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(scan: ScanOptions.Default with { MaxNamespaces = 3, MaxUsings = 4 }));

        Assert.Equal(3, build.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Namespace));
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.Limit, Path: "A/Many.cs" });
    }

    [Fact]
    public async Task More_gaps_than_the_cap_are_cut_without_failing()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">" + string.Concat(Enumerable.Range(0, 100).Select(i => $"<Import Project=\"p{i}.props\" />\n")) + "</Project>");

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxGaps = 10 }));

        Assert.True(build.Graph.Gaps.Count <= 10);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
    }

    [Fact]
    public async Task More_projects_and_solution_entries_and_files_than_their_caps_are_cut_with_a_gap()
    {
        using var repository = new GraphTestRepository();
        repository.Add("S.slnx", "<Solution>" + string.Concat(Enumerable.Range(0, 6).Select(i => $"<Project Path=\"P{i}/P{i}.csproj\" />")) + "</Solution>");
        for (var i = 0; i < 6; i++)
        {
            repository.Add($"P{i}/P{i}.csproj", Csproj());
            repository.Add($"P{i}/A.cs", $"namespace P{i}.A {{ }}");
            repository.Add($"P{i}/B.cs", $"namespace P{i}.B {{ }}");
        }

        var projects = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxProjects = 2 }));
        var entries = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxSolutionEntries = 3 }));
        var files = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(new AnalysisLimits { MaxCsFiles = 3 }));

        Assert.Equal(2, projects.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Project));
        Assert.Contains(projects.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
        Assert.Equal(3, entries.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Project));
        Assert.Contains(entries.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
        Assert.Equal(3, files.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Namespace));
        Assert.Contains(files.Graph.Gaps, g => g.Code == GraphGapCodes.Limit);
    }

    [Fact]
    public void The_usings_of_a_level_above_are_shared_by_reference_and_never_copied_per_namespace()
    {
        var text = string.Concat(Enumerable.Range(0, 5_000).Select(i => $"using U{i};\n")) + string.Concat(Enumerable.Range(0, 5_000).Select(i => $"namespace N{i} {{ }}\n"));

        var failure = CSharpSyntaxScanner.TryScan(text, ScanOptions.Default, out var declarations);

        Assert.Equal(ScanFailure.None, failure);
        Assert.True(declarations.Truncated, "the caps of usings and of namespaces cut the file");
        Assert.Equal(ScanOptions.Default.MaxNamespaces, declarations.Namespaces.Count);
        Assert.Equal(ScanOptions.Default.MaxUsings, declarations.Namespaces[0].Scope.Outer!.Own.Count);
        Assert.All(declarations.Namespaces, n => Assert.Same(declarations.Namespaces[0].Scope.Outer, n.Scope.Outer));
        Assert.Equal(ScanOptions.Default.MaxUsings, declarations.Namespaces[10].Scope.All().Count());
    }

    [Fact]
    public async Task A_launch_with_no_sources_reads_the_projects_and_marks_each_one_partial_with_a_gap()
    {
        using var repository = GraphTestRepository.FromSample();

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(), withoutSources: true);

        Assert.DoesNotContain(build.Graph.Nodes, n => n.Kind == GraphNodeKind.Namespace);
        Assert.Equal(3, build.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Project && n.Partial));
        Assert.Contains(build.Graph.Edges, e => e.Kind == GraphEdgeKind.ProjectReference);
    }

    // ---- the parser: a stack of its own, a nest turned away, a parser that does not return ----

    [Fact]
    public void A_file_nested_a_few_thousand_deep_by_casts_is_read_on_the_stack_of_the_parser_thread_and_does_not_bring_the_process_down()
    {
        var text = "namespace Deep { class C { int Y(int a) => " + string.Concat(Enumerable.Repeat("(int)", 5_000)) + "a; } }";

        var failure = CSharpSyntaxScanner.TryScan(text, ScanOptions.Default, out var declarations);

        Assert.Equal(ScanFailure.None, failure);
        Assert.Equal("Deep", Assert.Single(declarations.Namespaces).Name);
    }

    [Theory]
    [InlineData("!", 300)]
    [InlineData("-", 300)]
    [InlineData("~ ", 300)]
    [InlineData("<", 300)]
    public void A_long_run_of_one_operator_is_turned_away_before_the_parser_sees_it(string unit, int count)
    {
        var text = "class C { int X = " + string.Concat(Enumerable.Repeat(unit, count)) + "a; }";

        Assert.True(CSharpSyntaxScanner.LooksLikeANest(text));
        Assert.Equal(ScanFailure.TooDeep, CSharpSyntaxScanner.TryScan(text, ScanOptions.Default, out _));
    }

    [Fact]
    public void A_statement_with_no_end_for_hundreds_of_thousands_of_characters_is_turned_away()
    {
        var text = "class C { int X = " + string.Concat(Enumerable.Repeat("a+", CSharpSyntaxScanner.MaxStatementCharacters)) + "a; }";

        Assert.True(CSharpSyntaxScanner.LooksLikeANest(text));
    }

    [Fact]
    public void Code_that_is_normal_is_never_turned_away_not_even_the_code_of_the_analysis_itself()
    {
        var own = Path.Combine(FindRepositoryRoot(), "src", "AgentStudio.GraphWorker", "SolutionGraphAnalysis.cs");
        var banner = "// " + new string('=', 80) + "\nclass C { string S = \"" + new string('-', 100) + "\"; }";

        Assert.False(CSharpSyntaxScanner.LooksLikeANest(File.ReadAllText(own)));
        Assert.False(CSharpSyntaxScanner.LooksLikeANest(banner));
    }

    private static string FindRepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "AgentStudio.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("The repository was not found.");
    }

    [Fact]
    public void A_parser_that_honors_its_cancellation_ends_as_timed_out_and_one_that_ignores_it_is_stuck_in_its_time_plus_the_grace()
    {
        var options = ScanOptions.Default with { Timeout = TimeSpan.FromMilliseconds(150), StuckGrace = TimeSpan.FromMilliseconds(300) };
        var cooperative = options with
        {
            BeforeParse = (_, ct) =>
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                ct.ThrowIfCancellationRequested();
            },
        };
        var deaf = options with
        {
            BeforeParse = (_, _) => Thread.Sleep(TimeSpan.FromSeconds(3)),
        };

        var started = DateTime.UtcNow;
        var timedOut = CSharpSyntaxScanner.TryScan("class C { }", cooperative, out _);
        var stuck = CSharpSyntaxScanner.TryScan("class C { }", deaf, out _);

        Assert.Equal(ScanFailure.TimedOut, timedOut);
        Assert.Equal(ScanFailure.Stuck, stuck);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5), "it waited for the deaf parser to finish");
    }

    [Fact]
    public async Task A_stuck_parser_ends_the_process_by_the_hook_that_names_the_file_and_the_file_is_a_gap_if_the_hook_returns()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", Csproj());
        repository.Add("A/Slow.cs", "namespace Slow { }");
        repository.Add("A/Fine.cs", "namespace Fine { }");
        var stuckOn = new List<string>();
        var scan = ScanOptions.Default with
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            StuckGrace = TimeSpan.FromMilliseconds(100),
            BeforeParse = (text, _) =>
            {
                if (text.Contains("Slow", StringComparison.Ordinal))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            },
        };

        var build = await repository.AnalyzeWithAsync(new SolutionGraphAnalysis(scan: scan, onStuck: stuckOn.Add));

        Assert.Equal(["A/Slow.cs"], stuckOn);
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "A/Slow.cs" });
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Fine");
    }

    [Fact]
    public void The_exit_code_of_a_stuck_process_is_not_one_the_host_treats_as_a_decision_of_the_process()
    {
        int[] ownCodes = [GraphWorkerExitCodes.Completed, GraphWorkerExitCodes.UsageError, GraphWorkerExitCodes.RootError, GraphWorkerExitCodes.AnalysisUnavailable, GraphWorkerExitCodes.Failed];

        Assert.DoesNotContain(GraphWorkerExitCodes.FileStuck, ownCodes);
    }

    [Fact]
    public void The_request_without_sources_is_written_and_read_back_as_the_same_request_and_a_repeated_or_wrong_value_is_not_the_protocol()
    {
        var request = new GraphWorkerRequest(Path.GetTempPath(), new string('1', 40), ["a.cs"], withoutSources: true);

        Assert.True(GraphWorkerRequest.TryParse([.. request.ToArguments()], out var back));
        Assert.Equal(request, back);
        Assert.False(GraphWorkerRequest.TryParse([.. request.ToArguments(), "--sources", "none"], out _));
        Assert.False(GraphWorkerRequest.TryParse([.. new GraphWorkerRequest(Path.GetTempPath(), new string('1', 40)).ToArguments(), "--sources", "all"], out _));
        Assert.NotEqual(request, new GraphWorkerRequest(Path.GetTempPath(), new string('1', 40), ["a.cs"]));
    }

    [Fact]
    public void The_worker_has_no_code_of_network_it_references_no_assembly_of_http_or_sockets_and_names_none_in_its_sources()
    {
        var referenced = typeof(SolutionGraphAnalysis).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var folder = Path.Combine(FindRepositoryRoot(), "src", "AgentStudio.GraphWorker");
        var sources = Directory.GetFiles(folder, "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText).ToList();

        Assert.DoesNotContain(referenced, n => n.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Contains("System.Net", StringComparison.Ordinal) || s.Contains("HttpClient", StringComparison.Ordinal) || s.Contains("Socket", StringComparison.Ordinal));
    }
}
