using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/20 D-16 to D-19 (P0 of Phase 6, ADR-0062): the protocol shared by the host of Auto Graph and the analysis process, and the text of the model.
/// The output of the process is data that is not trusted: the strict reader is the last word, and everything it refuses is an <c>InvalidDataException</c>.
/// </summary>
public sealed class Phase6GraphContractTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "agentstudio-graph-copy");

    private static readonly GraphNode Solution = new("AgentStudio.slnx", GraphNodeKind.Solution, "AgentStudio", "AgentStudio.slnx", false);
    private static readonly GraphNode App = new("src/App/App.csproj", GraphNodeKind.Project, "App", "src/App/App.csproj", true);
    private static readonly GraphNode Ns = new("src/App/App.csproj::A.App", GraphNodeKind.Namespace, "A.App", null, false);

    private static readonly CodeGraph Graph = new(
        CommitA,
        1,
        [Solution, App, Ns],
        [
            new GraphEdge(Solution.Id, App.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge(App.Id, Ns.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
        ],
        [new GraphGap(App.Id, "graph.unevaluated", "src/App/App.csproj", 12)]);

    // ---- the request ----

    [Fact]
    public void A_request_is_written_as_arguments_and_read_back_by_the_same_rules()
    {
        var request = new GraphWorkerRequest(Root, CommitA, ["src/Big.cs", "src/Other.cs"]);

        var arguments = request.ToArguments();

        Assert.Equal(["--root", Root, "--commit", CommitA, "--skip", "src/Big.cs", "--skip", "src/Other.cs"], arguments);
        Assert.True(GraphWorkerRequest.TryParse(arguments, out var read));
        Assert.Equal(request, read);
        Assert.True(GraphWorkerRequest.TryParse(["--commit", CommitA, "--root", Root], out var reordered));
        Assert.Equal(new GraphWorkerRequest(Root, CommitA), reordered);
    }

    [Fact]
    public void Every_argument_is_an_element_and_nothing_is_joined_into_a_command_line()
    {
        var request = new GraphWorkerRequest(Path.Combine(Path.GetTempPath(), "a copy with spaces"), CommitA, ["dir with spaces/x y.cs"]);

        Assert.Contains(request.Root, request.ToArguments());
        Assert.Contains("dir with spaces/x y.cs", request.ToArguments());
        Assert.DoesNotContain(request.ToArguments(), argument => argument.StartsWith('"'));
    }

    public static TheoryData<string[]> NotRequests()
    {
        var data = new TheoryData<string[]>();
        foreach (var arguments in new string[][]
        {
            ["--root"],
            ["--root", "C:\\x"],
            ["--commit", "abc", "--root", "C:\\x"],
            ["--unknown", "1", "--root", "C:\\x", "--commit", CommitA],
            ["--root", "C:\\x", "--root", "C:\\y", "--commit", CommitA],
            ["--root", "C:\\x", "--commit", CommitA, "--commit", CommitA],
            ["--root", "relative\\path", "--commit", CommitA],
            ["--root", "C:\\x", "--commit", new string('A', 40)],
            ["--root", "C:\\x", "--commit", CommitA, "--skip", "../outside.cs"],
            ["--root", "C:\\x", "--commit", CommitA, "--skip", "C:\\Windows\\win.ini"],
            ["--root", "C:\\x", "--commit", CommitA, "extra"],
            ["root", "C:\\x", "commit", CommitA],
        })
        {
            data.Add(arguments);
        }

        return data;
    }
    [Theory]
    [MemberData(nameof(NotRequests))]
    public void Anything_that_is_not_exactly_the_protocol_is_not_a_request(string[] arguments)
    {
        Assert.False(GraphWorkerRequest.TryParse(arguments, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void An_empty_command_line_is_not_a_request()
    {
        Assert.False(GraphWorkerRequest.TryParse([], out _));
        Assert.Throws<ArgumentNullException>(() => GraphWorkerRequest.TryParse(null!, out _));
    }

    [Fact]
    public void A_request_has_an_absolute_root_a_full_commit_and_up_to_sixteen_files_to_skip_that_stay_inside()
    {
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest("relative", CommitA));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(string.Empty, CommitA));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(Root + "\n", CommitA));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(Root, "abc1234"));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(Root, CommitA, ["..\\x.cs"]));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(Root, CommitA, ["a/../../x.cs"]));
        Assert.Throws<ArgumentException>(() => new GraphWorkerRequest(Root, CommitA, [.. Enumerable.Range(0, GraphWorkerProtocol.MaxSkipped + 1).Select(i => $"f{i}.cs")]));
        Assert.Equal(GraphWorkerProtocol.MaxSkipped, new GraphWorkerRequest(Root, CommitA, [.. Enumerable.Range(0, GraphWorkerProtocol.MaxSkipped).Select(i => $"f{i}.cs")]).Skipped.Count);
        Assert.Equal(64, new GraphWorkerRequest(Root, new string('1', 64)).Commit.Length);
    }

    [Fact]
    public void The_skipped_files_are_copied_so_the_list_of_the_caller_cannot_change_the_request()
    {
        var skipped = new List<string> { "a.cs" };
        var request = new GraphWorkerRequest(Root, CommitA, skipped);

        skipped.Add("../../b.cs");

        Assert.Equal(["a.cs"], request.Skipped);
    }

    // ---- progress and constants ----

    [Fact]
    public void A_progress_line_names_the_file_the_process_starts_on_and_only_a_safe_path_is_believed()
    {
        Assert.Equal("progress:src/App/Big.cs", GraphWorkerProtocol.ProgressLine("src/App/Big.cs"));
        Assert.True(GraphWorkerProtocol.TryParseProgress("progress:src/App/Big.cs", out var path));
        Assert.Equal("src/App/Big.cs", path);
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:../../x.cs", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:C:\\x.cs", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress("The analysis failed.", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress(null, out _));
    }

    [Fact]
    public void The_constants_of_the_protocol_are_the_ones_of_the_design()
    {
        Assert.Equal((0, 2, 3, 4, 5), (GraphWorkerExitCodes.Completed, GraphWorkerExitCodes.UsageError, GraphWorkerExitCodes.RootError, GraphWorkerExitCodes.AnalysisUnavailable, GraphWorkerExitCodes.Failed));
        Assert.Equal(3, GraphWorkerProtocol.MaxRelaunches);
        Assert.Equal(TimeSpan.FromSeconds(120), GraphWorkerProtocol.MaxRuntime);
        Assert.Equal(1, GraphWorkerProtocol.SchemaVersion);
        Assert.True(GraphWorkerProtocol.MaxOutputBytes > 0);
    }

    // ---- the text of the model ----

    [Fact]
    public void The_model_is_written_in_one_fixed_compact_text_in_the_order_the_model_keeps()
    {
        var json = CodeGraphJson.Serialize(Graph);

        Assert.Equal(
            $$"""{"schema":1,"commit":"{{CommitA}}","generator":1,"nodes":[{"id":"AgentStudio.slnx","kind":"Solution","name":"AgentStudio","path":"AgentStudio.slnx","partial":false},{"id":"src/App/App.csproj","kind":"Project","name":"App","path":"src/App/App.csproj","partial":true},{"id":"src/App/App.csproj::A.App","kind":"Namespace","name":"A.App","partial":false}],"edges":[{"from":"AgentStudio.slnx","to":"src/App/App.csproj","kind":"Contains"},{"from":"src/App/App.csproj","to":"src/App/App.csproj::A.App","kind":"Contains"}],"gaps":[{"node":"src/App/App.csproj","code":"graph.unevaluated","path":"src/App/App.csproj","line":12}]}""",
            json);
        Assert.Equal(json, CodeGraphJson.Serialize(Graph));
    }

    [Fact]
    public void The_model_is_read_back_equal_with_the_same_fingerprint_and_written_again_byte_for_byte()
    {
        var json = CodeGraphJson.Serialize(Graph);

        var read = CodeGraphJson.Parse(json);

        Assert.Equal(Graph, read);
        Assert.Equal(Graph.ModelHash, read.ModelHash);
        Assert.Equal(json, CodeGraphJson.Serialize(read));
    }

    [Fact]
    public void A_relation_an_ai_proposed_is_never_written_because_it_lives_apart()
    {
        var inferred = new GraphEdge(Ns.Id, Ns.Id + "x", GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Inferred, new GraphInference("a", "m", T0, "r"));
        var other = new GraphNode(Ns.Id + "x", GraphNodeKind.Namespace, "A.Other", null, false);
        var withInferred = new CodeGraph(CommitA, 1, [.. Graph.Nodes, other], [.. Graph.Edges, inferred], Graph.Gaps);

        var json = CodeGraphJson.Serialize(withInferred);

        Assert.DoesNotContain("NamespaceUse", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inferred", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(withInferred.StaticOnly(), CodeGraphJson.Parse(json));
    }

    public static TheoryData<string> Corruptions()
    {
        var good = CodeGraphJson.Serialize(Graph);
        return
        [
            string.Empty,
            "null",
            "[]",
            "{}",
            "not json",
            good + " trailing",
            good[..^1],
            good.Replace("{\"schema\":1,", "{\"schema\":2,", StringComparison.Ordinal),
            good.Replace("{\"schema\":1,", "{\"schema\":1,\"extra\":true,", StringComparison.Ordinal),
            good.Replace("{\"schema\":1,", "{\"schema\":1,\"schema\":1,", StringComparison.Ordinal),
            good.Replace("\"kind\":\"Solution\"", "\"kind\":0", StringComparison.Ordinal),
            good.Replace("\"kind\":\"Solution\"", "\"kind\":\"Galaxy\"", StringComparison.Ordinal),
            good.Replace("\"name\":\"AgentStudio\"", "\"name\":null", StringComparison.Ordinal),
            good.Replace("\"partial\":false", "\"partial\":\"false\"", StringComparison.Ordinal),
            good.Replace($"\"commit\":\"{CommitA}\"", "\"commit\":\"HEAD\"", StringComparison.Ordinal),
            good.Replace("\"path\":\"src/App/App.csproj\",\"partial\":true", "\"path\":\"../../Windows/win.ini\",\"partial\":true", StringComparison.Ordinal),
            good.Replace("\"path\":\"src/App/App.csproj\",\"partial\":true", "\"path\":\"C:\\\\Windows\\\\win.ini\",\"partial\":true", StringComparison.Ordinal),
            good.Replace("\"to\":\"src/App/App.csproj\",\"kind\":\"Contains\"", "\"to\":\"src/Missing.csproj\",\"kind\":\"Contains\"", StringComparison.Ordinal),
            good.Replace("\"generator\":1", "\"generator\":0", StringComparison.Ordinal),
            good.Replace("\"code\":\"graph.unevaluated\"", "\"code\":\"doctor.unevaluated\"", StringComparison.Ordinal),
            good.Replace(",\"gaps\":[{\"node\":\"src/App/App.csproj\",\"code\":\"graph.unevaluated\",\"path\":\"src/App/App.csproj\",\"line\":12}]", ",\"gaps\":[]", StringComparison.Ordinal),
            new string('[', 100) + new string(']', 100),
        ];
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void A_model_that_is_not_valid_is_refused_with_an_invalid_data_error_that_never_quotes_the_text(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => CodeGraphJson.Parse(json));

        Assert.DoesNotContain("Windows", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("win.ini", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_model_has_no_inner_exception_so_nothing_the_process_wrote_reaches_a_log()
    {
        var good = CodeGraphJson.Serialize(Graph);
        var hostileCode = good.Replace("\"code\":\"graph.unevaluated\"", "\"code\":\"secret-marker\\u202E\\n\"", StringComparison.Ordinal);
        var hostileName = good.Replace("\"name\":\"A.App\"", "\"name\":\"secret-marker\\u202E\"", StringComparison.Ordinal);
        var unknown = good.Replace("{\"schema\":1,", "{\"secret-marker\":1,\"schema\":1,", StringComparison.Ordinal);

        foreach (var json in new[] { hostileCode, hostileName, unknown })
        {
            var error = Assert.Throws<InvalidDataException>(() => CodeGraphJson.Parse(json));

            Assert.Null(error.InnerException);
            Assert.DoesNotContain("secret-marker", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-marker", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_progress_line_that_still_has_the_carriage_return_of_a_windows_line_end_names_the_same_file()
    {
        Assert.True(GraphWorkerProtocol.TryParseProgress("progress:src/App/Big.cs\r", out var path));
        Assert.Equal("src/App/Big.cs", path);
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:src/App/Big.cs\r\r\n", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:NUL.cs", out _));
        Assert.False(GraphWorkerProtocol.TryParseProgress("progress:src/\u202Eevil.cs", out _));
    }

    [Fact]
    public void A_text_over_the_cap_of_the_output_is_refused_before_it_is_parsed()
    {
        var text = new string(' ', GraphWorkerProtocol.MaxOutputBytes + 1);

        Assert.Throws<InvalidDataException>(() => CodeGraphJson.Parse(text));
        Assert.Throws<ArgumentNullException>(() => CodeGraphJson.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => CodeGraphJson.Serialize(null!));
    }

    // ---- the analysis of a commit ----

    [Fact]
    public void An_analysis_joins_a_graph_and_the_discrepancies_of_the_same_commit_and_only_of_it()
    {
        var consistency = new ConsistencyReport(CommitA, [], [], false);

        var analysis = new CommitAnalysis(Graph, consistency);

        Assert.Equal(CommitA, analysis.Commit);
        Assert.Throws<ArgumentException>(() => new CommitAnalysis(Graph, new ConsistencyReport(CommitB, [], [], false)));
        Assert.Throws<ArgumentNullException>(() => new CommitAnalysis(null!, consistency));
        Assert.Throws<ArgumentNullException>(() => new CommitAnalysis(Graph, null!));
    }

    [Fact]
    public void A_task_of_the_graph_has_a_one_line_title_and_the_relative_paths_it_changed()
    {
        var run = new AgentStudio.Domain.Orchestration.RunId("20261112-a1b2c3");
        var task = new GraphTaskRef(run, Tid("T1"), "Calculadora: multiplicar", ["src/Calc.cs"]);

        Assert.Equal(["src/Calc.cs"], task.ChangedPaths);
        Assert.Equal(task, new GraphTaskRef(run, Tid("T1"), "Calculadora: multiplicar", ["src/Calc.cs"]));
        Assert.Throws<ArgumentException>(() => new GraphTaskRef(run, Tid("T1"), "two\nlines", []));
        Assert.Throws<ArgumentException>(() => new GraphTaskRef(run, Tid("T1"), " ", []));
        Assert.Throws<ArgumentException>(() => new GraphTaskRef(run, Tid("T1"), "t", ["../../secret.txt"]));
        Assert.Throws<ArgumentException>(() => new GraphTaskRef(run, Tid("T1"), new string('x', 201), []));
    }

    // ---- the discrepancies ----

    [Fact]
    public void The_codes_of_the_discrepancies_are_those_of_docs_19_and_the_one_of_the_report_of_a_milestone()
    {
        Assert.Equal(["doc.link-broken", "doc.adr-cites-missing", "doc.diagram-stale", "requirement.uncovered"], ConsistencyCodes.All);
        Assert.All(ConsistencyCodes.All, code => Assert.True(DoctorCodes.IsCode(code), code));
    }

    [Fact]
    public void A_finding_says_where_it_is_with_a_relative_path_and_a_line_and_carries_facts_as_data()
    {
        var finding = new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/README.md", 12, [DoctorFact.Of("declared", "docs/missing.md")]);

        Assert.Equal(("docs/README.md", 12), (finding.Path, finding.Line));
        Assert.Equal(finding, new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/README.md", 12, [new DoctorFact("declared", "docs/missing.md")]));
        Assert.Null(new ConsistencyFinding(ConsistencyCodes.RequirementUncovered, null, null, []).Path);
        Assert.Throws<ArgumentException>(() => new ConsistencyFinding(ConsistencyCodes.LinkBroken, "../README.md", 1, []));
        Assert.Throws<ArgumentException>(() => new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs\\README.md", 1, []));
        Assert.Throws<ArgumentException>(() => new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/README.md", 0, []));
        Assert.Throws<ArgumentException>(() => new ConsistencyFinding(ConsistencyCodes.LinkBroken, null, 3, []));
        Assert.Throws<ArgumentException>(() => new ConsistencyFinding("Link Broken", "a.md", 1, []));
    }

    [Fact]
    public void A_report_names_a_full_commit_holds_up_to_two_thousand_findings_and_counts_by_code()
    {
        var link = new ConsistencyFinding(ConsistencyCodes.LinkBroken, "a.md", 1, []);
        var stale = new ConsistencyFinding(ConsistencyCodes.DiagramStale, null, null, []);

        var report = new ConsistencyReport(CommitA, [link, link, stale], [ConsistencyCodes.RequirementUncovered], false);

        Assert.Equal(2, report.Count(ConsistencyCodes.LinkBroken));
        Assert.Equal(0, report.Count(ConsistencyCodes.AdrCitesMissing));
        Assert.Equal([ConsistencyCodes.RequirementUncovered], report.NotApplicable);
        Assert.Equal(report, new ConsistencyReport(CommitA, [link, link, stale], [ConsistencyCodes.RequirementUncovered], false));
        Assert.NotEqual(report, new ConsistencyReport(CommitA, [link, link, stale], [], false));
        Assert.Throws<ArgumentException>(() => new ConsistencyReport("HEAD", [], [], false));
        Assert.Throws<ArgumentException>(() => new ConsistencyReport(CommitA, [.. Enumerable.Repeat(link, ConsistencyReport.MaxFindings + 1)], [], true));
        Assert.Equal(ConsistencyReport.MaxFindings, new ConsistencyReport(CommitA, [.. Enumerable.Repeat(link, ConsistencyReport.MaxFindings)], [], true).Findings.Count);
        Assert.Throws<ArgumentException>(() => new ConsistencyReport(CommitA, [], ["Not a code"], false));
        Assert.Throws<ArgumentException>(() => new ConsistencyReport(CommitA, [], [ConsistencyCodes.LinkBroken, ConsistencyCodes.LinkBroken], false));
    }
}
