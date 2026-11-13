using System.IO;
using System.Text;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;
using AgentStudio.GraphWorker;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.GraphWorker;

/// <summary>
/// docs/20 F6-3 and F6-12, WP-G: the analysis over the sample solution (against a golden, not against this repository) and over small repositories built
/// for each rule, in-process. What must never be opened is a sentinel in a folder beside the copy.
/// </summary>
public sealed class SolutionGraphAnalysisTests
{
    private const string SentinelNamespace = "Sentinel.Outside.MustNeverAppear";

    private static string Golden => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GraphWorker", "Fixtures", "sample-solution.graph.json"));

    private static string Json(GraphBuild build) => CodeGraphJson.Serialize(build.Graph, build.Files);

    [Fact]
    public async Task The_sample_solution_gives_the_golden_model_projects_references_namespaces_and_the_files_that_declare_them()
    {
        using var repository = GraphTestRepository.FromSample();
        repository.Add("src/Core/obj/Ignored.cs", "namespace Sample.Core.ShouldNotAppearObj { }");
        repository.Add("src/Core/bin/Ignored.cs", "namespace Sample.Core.ShouldNotAppearBin { }");

        var (build, _) = await repository.AnalyzeAsync();

        // The model of the sample solution at the commit of TestData: the golden has its own commit, so the one of the request is put in.
        Assert.Equal(Golden.Replace(new string('a', 40), CommitA, StringComparison.Ordinal), Json(build));
        Assert.DoesNotContain("ShouldNotAppear", Json(build), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_has_the_structure_of_the_solution_a_project_with_a_condition_partial_and_the_rest_complete()
    {
        using var repository = GraphTestRepository.FromSample();

        var (build, _) = await repository.AnalyzeAsync();
        var graph = build.Graph;

        Assert.Equal(GraphNodeKind.Solution, graph.Nodes.Single(n => n.Id == "Sample.slnx").Kind);
        Assert.Equal(["src/App/App.csproj", "src/Core/Core.csproj", "tests/Core.Tests/Core.Tests.csproj"], graph.Nodes.Where(n => n.Kind == GraphNodeKind.Project).Select(n => n.Id));
        Assert.Contains(graph.Edges, e => e is { Kind: GraphEdgeKind.ProjectReference, From: "src/App/App.csproj", To: "src/Core/Core.csproj" });
        Assert.Contains(graph.Edges, e => e is { Kind: GraphEdgeKind.Contains, From: "folder:/src/", To: "src/Core/Core.csproj" });
        Assert.Contains(graph.Edges, e => e is { Kind: GraphEdgeKind.PackageReference, To: "package:Serilog" });
        Assert.Equal(new GraphCoverage(3, 2, 1, 1), graph.Coverage);
        var gap = Assert.Single(graph.Gaps);
        Assert.Equal((GraphGapCodes.Unevaluated, "tests/Core.Tests/Core.Tests.csproj", 4), (gap.Code, gap.Path, gap.Line));
        Assert.True(graph.Nodes.Single(n => n.Id == "tests/Core.Tests/Core.Tests.csproj").Partial);

        // A namespace of a file with a file-scoped namespace and a non-ASCII name, and nested ones, are namespaces of the project.
        Assert.Contains(graph.Nodes, n => n.Name == "Sample.Núcleo");
        Assert.Contains(graph.Nodes, n => n.Name == "Sample.Core.Inner");
        Assert.Equal(["src/Core/Nested.cs", "src/Core/Service.cs"], build.Files.For("src/Core/Core.csproj|Sample.Core"));
    }

    [Fact]
    public async Task A_using_static_and_an_alias_name_the_namespace_that_declares_the_type()
    {
        using var repository = GraphTestRepository.FromSample();

        var (build, _) = await repository.AnalyzeAsync();

        var uses = build.Graph.Edges.Where(e => e.Kind == GraphEdgeKind.NamespaceUse && e.From == "src/App/App.csproj|Sample.App").Select(e => e.To).ToList();
        Assert.Contains("src/Core/Core.csproj|Sample.Core.Model", uses);
        Assert.Contains("src/Core/Core.csproj|Sample.Núcleo", uses);
        Assert.Contains("src/Core/Core.csproj|Sample.Core", uses);
    }

    [Fact]
    public async Task The_same_commit_gives_the_same_model_and_the_same_hash_and_the_hash_ignores_the_commit_and_the_files()
    {
        using var repository = GraphTestRepository.FromSample();

        var (first, _) = await repository.AnalyzeAsync();
        var (second, _) = await repository.AnalyzeAsync();

        Assert.Equal(Json(first), Json(second));
        Assert.Equal(first.Graph.ModelHash, second.Graph.ModelHash);
        Assert.Equal(first.Graph.ModelHash, new CodeGraph(CommitB, first.Graph.GeneratorVersion, first.Graph.Nodes, first.Graph.Edges, first.Graph.Gaps).ModelHash);
    }

    [Fact]
    public async Task Each_file_is_reported_before_it_is_opened_and_every_progress_path_is_safe()
    {
        using var repository = GraphTestRepository.FromSample();

        var (_, progress) = await repository.AnalyzeAsync();

        Assert.Equal("Sample.slnx", progress[0]);
        Assert.Contains("src/Core/Service.cs", progress);
        Assert.All(progress, p => Assert.True(GraphWorkerProtocol.TryParseProgress(GraphWorkerProtocol.ProgressLine(p), out _), p));
    }

    // ---- the readers ----

    [Fact]
    public void The_slnx_reader_gives_folders_projects_and_their_lines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"a/A.csproj\" />\n  </Folder>\n  <Project Path=\"b/B.csproj\" />\n</Solution>"));

        var solution = SlnxReader.Read(stream);

        Assert.Equal(["/src/"], solution.Folders);
        Assert.Equal([("a/A.csproj", 3, "/src/"), ("b/B.csproj", 5, null)], solution.Projects.Select(p => (p.RawPath, p.Line, p.Folder)));
    }

    [Fact]
    public void The_sln_reader_gives_projects_and_the_solution_folders_they_are_nested_in_and_skips_the_folders_themselves()
    {
        const string Sln = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "src\A\A.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "B\B.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Global
                GlobalSection(NestedProjects) = preSolution
                    {22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
                EndGlobalSection
            EndGlobal
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sln));

        var solution = SlnReader.Read(stream);

        Assert.Equal(["/src/"], solution.Folders);
        Assert.Equal([("src\\A\\A.csproj", "/src/"), ("B\\B.csproj", null)], solution.Projects.Select(p => (p.RawPath, p.Folder)));
    }

    [Fact]
    public async Task A_sln_at_the_root_is_read_like_a_slnx()
    {
        using var repository = new GraphTestRepository();
        repository.Add("S.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\", \"A\\A.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\r\nEndProject\r\n");
        repository.Add("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.Add("A/One.cs", "namespace A.One { }");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Equal(["A/A.csproj", "A/A.csproj|A.One", "S.sln"], build.Graph.Nodes.Select(n => n.Id));
        Assert.Contains(build.Graph.Edges, e => e is { From: "S.sln", To: "A/A.csproj", Kind: GraphEdgeKind.Contains });
    }

    [Fact]
    public async Task Without_a_solution_every_project_found_is_read()
    {
        using var repository = new GraphTestRepository();
        repository.Add("x/X.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.Add("y/Y.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Equal(["x/X.csproj", "y/Y.csproj"], build.Graph.Nodes.Select(n => n.Id));
        Assert.Empty(build.Graph.Edges);
    }

    // ---- what MSBuild would resolve is not guessed ----

    [Theory]
    [InlineData("<PropertyGroup Condition=\"'$(X)'=='1'\"><TargetFramework>net10.0</TargetFramework></PropertyGroup>", 2)]
    [InlineData("<ItemGroup><ProjectReference Include=\"$(Dir)\\B.csproj\" /></ItemGroup>", 2)]
    [InlineData("<ItemGroup><ProjectReference Include=\"..\\*\\B.csproj\" /></ItemGroup>", 2)]
    [InlineData("<Import Project=\"other.props\" />", 2)]
    [InlineData("<ItemGroup><Compile Include=\"Extra\\*.cs\" /></ItemGroup>", 2)]
    [InlineData("<PropertyGroup><RootNamespace>$(Foo)</RootNamespace></PropertyGroup>", 2)]
    public async Task What_msbuild_would_evaluate_makes_the_project_partial_with_the_line_and_is_never_followed(string fragment, int line)
    {
        using var repository = new GraphTestRepository();
        repository.Add("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  " + fragment + "\n</Project>");
        repository.Add("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        var (build, _) = await repository.AnalyzeAsync();

        var gap = Assert.Single(build.Graph.Gaps);
        Assert.Equal((GraphGapCodes.Unevaluated, "A/A.csproj", line), (gap.Code, gap.Path, gap.Line));
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "A/A.csproj").Partial);
        Assert.False(build.Graph.Nodes.Single(n => n.Id == "B/B.csproj").Partial);
        Assert.DoesNotContain(build.Graph.Edges, e => e.Kind == GraphEdgeKind.ProjectReference);
    }

    [Fact]
    public async Task A_directory_build_props_above_a_project_is_named_and_not_applied_and_the_project_is_partial()
    {
        using var repository = new GraphTestRepository();
        repository.Add("Directory.Build.props", "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>");
        repository.Add("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        var (build, _) = await repository.AnalyzeAsync();

        var gap = Assert.Single(build.Graph.Gaps);
        Assert.Equal((GraphGapCodes.BuildProps, "Directory.Build.props", "src/A/A.csproj"), (gap.Code, gap.Path, gap.NodeId));
        Assert.True(build.Graph.Nodes.Single().Partial);
    }

    [Fact]
    public async Task A_project_with_no_xml_that_parses_is_a_gap_and_the_rest_of_the_graph_stays_whole()
    {
        using var repository = GraphTestRepository.FromSample();
        repository.Add("src/App/App.csproj", "<Project><oops></Project>");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/App/App.csproj" });
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "src/App/App.csproj").Partial);
        Assert.Contains(build.Graph.Nodes, n => n.Id == "src/Core/Core.csproj|Sample.Core");
    }

    [Fact]
    public async Task A_dtd_in_a_project_is_refused_and_no_entity_is_expanded_or_resolved()
    {
        using var repository = new GraphTestRepository();
        var secret = repository.AddOutside("secret.txt", "TOP-SECRET-VALUE");
        repository.Add("A/A.csproj", "<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY x SYSTEM \"" + secret.Replace('\\', '/') + "\">]><Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>&x;</RootNamespace></PropertyGroup></Project>");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "A/A.csproj" });
        Assert.DoesNotContain("TOP-SECRET", Json(build), StringComparison.Ordinal);
    }

    // ---- F6-12: nothing outside the copy is opened ----

    private static readonly string[] HostilePaths =
    [
        @"..\..\outside\Evil.csproj",
        @"..\outside\Evil.csproj",
        @"C:\Windows\Evil.csproj",
        @"\\server\share\Evil.csproj",
        "/etc/Evil.csproj",
        @"C:Evil.csproj",
        @"src\..\..\outside\Evil.csproj",
        @"NUL.csproj",
    ];

    [Fact]
    public async Task A_slnx_with_paths_that_lead_out_or_are_absolute_opens_none_and_says_outside_root_without_the_path()
    {
        using var repository = new GraphTestRepository();
        var evil = repository.AddOutside("Evil.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.AddOutside("Evil.cs", "namespace " + SentinelNamespace + " { }");
        var projects = string.Join('\n', HostilePaths.Select(p => $"  <Project Path=\"{p}\" />"));
        repository.Add("S.slnx", "<Solution>\n  <Project Path=\"ok/Ok.csproj\" />\n" + projects + "\n</Solution>");
        repository.Add("ok/Ok.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.Add("ok/Ok.cs", "namespace Ok { }");

        var (build, progress) = await repository.AnalyzeAsync();
        var json = Json(build);

        Assert.Equal(["Ok", "S"], build.Graph.Nodes.Where(n => n.Kind is GraphNodeKind.Namespace or GraphNodeKind.Solution).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("Evil", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelNamespace, json, StringComparison.Ordinal);
        Assert.DoesNotContain(progress, p => p.Contains("Evil", StringComparison.Ordinal) || p.Contains("outside", StringComparison.Ordinal));
        Assert.DoesNotContain(evil, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.OutsideRoot && g.Path == "S.slnx");
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.InvalidPath && g.Path == "S.slnx");
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "S.slnx").Partial);
    }

    [Fact]
    public async Task A_sln_with_paths_that_lead_out_or_are_absolute_opens_none()
    {
        using var repository = new GraphTestRepository();
        repository.AddOutside("Evil.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        var lines = new StringBuilder();
        var n = 0;
        foreach (var path in HostilePaths)
        {
            lines.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"E{n}\", \"{path}\", \"{{{n:D8}-0000-0000-0000-000000000000}}\"\r\nEndProject\r\n");
            n++;
        }

        repository.Add("S.sln", lines.ToString());

        var (build, progress) = await repository.AnalyzeAsync();

        Assert.Equal(["S.sln"], build.Graph.Nodes.Select(x => x.Id));
        Assert.Equal(HostilePaths.Length, build.Graph.Gaps.Count(g => g.Code is GraphGapCodes.OutsideRoot or GraphGapCodes.InvalidPath));
        Assert.DoesNotContain(progress, p => p.Contains("Evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_reference_that_leads_out_or_is_absolute_is_not_opened_and_a_legitimate_one_through_dotdot_is()
    {
        using var repository = new GraphTestRepository();
        repository.AddOutside("Evil/Evil.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.AddOutside("Evil/Evil.cs", "namespace " + SentinelNamespace + " { }");
        repository.Add("src/A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\B\B.csproj" />
                <ProjectReference Include="..\..\..\outside\Evil\Evil.csproj" />
                <ProjectReference Include="C:\Users\someone\Evil.csproj" />
                <ProjectReference Include="\\server\share\Evil.csproj" />
              </ItemGroup>
            </Project>
            """);
        repository.Add("src/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.Add("src/B/B.cs", "namespace B { }");

        var (build, progress) = await repository.AnalyzeAsync();
        var json = Json(build);

        Assert.Contains(build.Graph.Edges, e => e is { Kind: GraphEdgeKind.ProjectReference, From: "src/A/A.csproj", To: "src/B/B.csproj" });
        Assert.Equal(3, build.Graph.Gaps.Count(g => g is { Code: GraphGapCodes.OutsideRoot, Path: "src/A/A.csproj" }));
        Assert.DoesNotContain("Evil", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelNamespace, json, StringComparison.Ordinal);
        Assert.DoesNotContain(progress, p => p.Contains("Evil", StringComparison.Ordinal));
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "src/A/A.csproj").Partial);
    }

    [Fact]
    public async Task A_junction_to_a_folder_outside_the_copy_is_not_followed_not_for_sources_not_for_a_project_and_not_for_a_reference()
    {
        using var repository = new GraphTestRepository();
        repository.AddOutside("Linked/Linked.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.AddOutside("Linked/Sentinel.cs", "namespace " + SentinelNamespace + " { }");
        repository.Add("S.slnx", "<Solution><Project Path=\"src/A/A.csproj\" /><Project Path=\"jump/Linked.csproj\" /></Solution>");
        repository.Add("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\..\\jump\\Linked.csproj\" /></ItemGroup></Project>");
        repository.Add("src/A/A.cs", "namespace A { }");
        repository.Junction("src/A/inside", Path.Combine(repository.Outside, "Linked"));
        repository.Junction("jump", Path.Combine(repository.Outside, "Linked"));

        var (build, progress) = await repository.AnalyzeAsync();
        var json = Json(build);

        Assert.DoesNotContain(SentinelNamespace, json, StringComparison.Ordinal);
        Assert.DoesNotContain(progress, p => p.Contains("Linked", StringComparison.Ordinal) || p.Contains("Sentinel", StringComparison.Ordinal));
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.Link, NodeId: "src/A/A.csproj" });
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.Link, NodeId: "jump/Linked.csproj" });
        Assert.Contains(build.Graph.Nodes, n => n.Id == "src/A/A.csproj|A");
    }

    // ---- a file that fails is a gap, and the graph stands ----

    [Fact]
    public async Task A_file_the_host_says_killed_an_earlier_launch_is_skipped_without_being_opened_and_its_project_is_partial()
    {
        using var repository = GraphTestRepository.FromSample();

        var (build, progress) = await repository.AnalyzeAsync("src/Core/Service.cs");

        Assert.DoesNotContain("src/Core/Service.cs", progress);
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Core/Service.cs", NodeId: "src/Core/Core.csproj" });
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "src/Core/Core.csproj").Partial);
        Assert.Contains(build.Graph.Nodes, n => n.Id == "src/App/App.csproj|Sample.App");
    }

    [Fact]
    public async Task A_cs_file_over_the_cap_is_skipped_and_counted_and_the_rest_of_the_graph_is_whole()
    {
        using var repository = GraphTestRepository.FromSample();
        repository.Add("src/Core/Huge.cs", "namespace Sample.Huge { }\n" + new string('/', (int)SolutionGraphAnalysis.MaxCsFileBytes + 10));

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Core/Huge.cs" });
        Assert.DoesNotContain(build.Graph.Nodes, n => n.Name == "Sample.Huge");
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Sample.Core.Model");
    }

    [Fact]
    public async Task Namespaces_nested_deeper_than_the_cap_skip_the_file_and_not_the_graph()
    {
        using var repository = GraphTestRepository.FromSample();
        var depth = CSharpSyntaxScanner.MaxNamespaceDepth + 1;
        repository.Add("src/Core/Deep.cs", string.Concat(Enumerable.Range(0, depth).Select(i => $"namespace N{i} {{ ")) + string.Concat(Enumerable.Repeat("}", depth)));

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Core/Deep.cs" });
        Assert.DoesNotContain(build.Graph.Nodes, n => n.Name.StartsWith("N0", StringComparison.Ordinal));
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Sample.Core.Model");
    }

    [Fact]
    public async Task An_expression_nested_extremely_deep_is_turned_away_before_the_parser_sees_it_so_the_stack_is_not_spent()
    {
        using var repository = GraphTestRepository.FromSample();
        var depth = 20_000;
        repository.Add("src/Core/Expr.cs", "namespace Sample.Expr { class C { int X = " + new string('(', depth) + "1" + new string(')', depth) + "; } }");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Core/Expr.cs", NodeId: "src/Core/Core.csproj" });
        Assert.DoesNotContain(build.Graph.Nodes, n => n.Name == "Sample.Expr");
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Sample.Core.Model");
    }

    [Fact]
    public void The_bracket_count_ignores_what_a_normal_file_has_and_counts_what_an_attack_has()
    {
        Assert.Equal(3, CSharpSyntaxScanner.BracketDepth("namespace A { class B { void C() { } } }") );
        Assert.Equal(0, CSharpSyntaxScanner.BracketDepth("a ) ] } b"));
        Assert.True(CSharpSyntaxScanner.BracketDepth(new string('[', 5_000)) > CSharpSyntaxScanner.MaxBracketDepth);
    }

    [Fact]
    public async Task Names_that_the_model_refuses_never_make_the_analysis_fail()
    {
        using var repository = GraphTestRepository.FromSample();
        repository.Add("src/Core/Long.cs", "namespace " + string.Join('.', Enumerable.Repeat("Segment", 60)) + " { }");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Core/Long.cs" });
    }

    [Fact]
    public async Task The_same_project_twice_in_the_solution_is_kept_once()
    {
        using var repository = new GraphTestRepository();
        repository.Add("S.slnx", "<Solution><Folder Name=\"/a/\"><Project Path=\"A/A.csproj\" /></Folder><Folder Name=\"/b/\"><Project Path=\"A/../A/A.csproj\" /></Folder></Solution>");
        repository.Add("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Single(build.Graph.Nodes, n => n.Kind == GraphNodeKind.Project);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Duplicate);
    }

    [Fact]
    public async Task A_second_solution_at_the_root_is_named_and_not_read()
    {
        using var repository = new GraphTestRepository();
        repository.Add("A.slnx", "<Solution><Project Path=\"A/A.csproj\" /></Solution>");
        repository.Add("B.slnx", "<Solution><Project Path=\"B/B.csproj\" /></Solution>");
        repository.Add("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        repository.Add("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        var (build, _) = await repository.AnalyzeAsync();

        Assert.Equal(["A.slnx", "A/A.csproj"], build.Graph.Nodes.Select(n => n.Id));
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.IgnoredSolution, Path: "B.slnx" });
    }
}
