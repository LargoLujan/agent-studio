using AgentStudio.Domain.Graph;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Graph;

/// <summary>
/// docs/20 D-20 and D-21 (P0 of Phase 6): the model of Auto Graph is a value that is always valid, sorted and compared by what it holds, and its
/// fingerprint is the one thing that decides «desactualizado»: it must not move with the commit, with the order the parts came in or with a relation an
/// AI proposed.
/// </summary>
public sealed class CodeGraphTests
{
    private static readonly GraphNode Solution = new("AgentStudio.slnx", GraphNodeKind.Solution, "AgentStudio", "AgentStudio.slnx", false);
    private static readonly GraphNode Domain = new("src/Domain/Domain.csproj", GraphNodeKind.Project, "Domain", "src/Domain/Domain.csproj", false);
    private static readonly GraphNode App = new("src/App/App.csproj", GraphNodeKind.Project, "App", "src/App/App.csproj", false);
    private static readonly GraphNode DomainNs = new("src/Domain/Domain.csproj::A.Domain", GraphNodeKind.Namespace, "A.Domain", null, false);
    private static readonly GraphNode AppNs = new("src/App/App.csproj::A.App", GraphNodeKind.Namespace, "A.App", null, false);
    private static readonly GraphNode Package = new("pkg:Newtonsoft.Json", GraphNodeKind.Package, "Newtonsoft.Json", null, false);

    private static readonly GraphEdge[] StaticEdges =
    [
        new(Solution.Id, Domain.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
        new(Solution.Id, App.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
        new(Domain.Id, DomainNs.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
        new(App.Id, AppNs.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
        new(App.Id, Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static),
        new(App.Id, Package.Id, GraphEdgeKind.PackageReference, GraphEdgeOrigin.Static),
        new(AppNs.Id, DomainNs.Id, GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Static),
    ];

    private static readonly GraphInference Inference = new("analista", "modelo-x", T0, "Comparten el contrato de registro.");

    private static CodeGraph Build(
        string? commit = null,
        int generator = 1,
        IEnumerable<GraphNode>? nodes = null,
        IEnumerable<GraphEdge>? edges = null,
        IEnumerable<GraphGap>? gaps = null) =>
        new(commit ?? CommitA, generator, [.. nodes ?? AllNodes], [.. edges ?? StaticEdges], [.. gaps ?? []]);

    private static readonly GraphNode[] AllNodes = [Solution, Domain, App, DomainNs, AppNs, Package];

    // ---- sorted, equal, and one fingerprint ----

    [Fact]
    public void A_model_is_sorted_when_it_is_built_whatever_the_order_the_parts_come_in()
    {
        var shuffled = Build(
            nodes: [Package, AppNs, Domain, Solution, DomainNs, App],
            edges: Enumerable.Reverse(StaticEdges));

        Assert.Equal(["AgentStudio.slnx", "pkg:Newtonsoft.Json", "src/App/App.csproj", "src/App/App.csproj::A.App", "src/Domain/Domain.csproj", "src/Domain/Domain.csproj::A.Domain"], shuffled.Nodes.Select(n => n.Id));
        Assert.Equal(Build(), shuffled);
        Assert.Equal(Build().GetHashCode(), shuffled.GetHashCode());
        Assert.Equal(Build().ModelHash, shuffled.ModelHash);
        Assert.Equal(Build().Edges, shuffled.Edges);
    }

    [Fact]
    public void The_fingerprint_is_a_sha256_that_does_not_move_with_the_commit()
    {
        var one = Build(commit: CommitA);
        var other = Build(commit: CommitB);

        Assert.Matches("^[0-9a-f]{64}$", one.ModelHash);
        Assert.Equal(one.ModelHash, other.ModelHash);
        Assert.NotEqual(one, other);
    }

    [Fact]
    public void The_fingerprint_does_not_move_with_a_relation_an_ai_proposed_nor_with_its_text()
    {
        var model = Build();
        var inferred = new GraphEdge(Domain.Id, App.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, Inference);

        var laid = model.WithInferred([inferred], out var discarded);

        Assert.Equal(0, discarded);
        Assert.Equal(model.Edges.Count + 1, laid.Edges.Count);
        Assert.Equal(model.ModelHash, laid.ModelHash);
        Assert.Equal(model.ModelHash, laid.StaticOnly().ModelHash);
        Assert.Equal(model, laid.StaticOnly());
        Assert.NotEqual(model, laid);
    }

    [Fact]
    public void Everything_static_moves_the_fingerprint_and_the_generator_too()
    {
        var baseline = Build().ModelHash;
        var partialDomain = new GraphNode(Domain.Id, Domain.Kind, Domain.Name, Domain.Path, true);
        var gap = new GraphGap(Domain.Id, "graph.unevaluated", "src/Domain/Domain.csproj", 12);

        Assert.NotEqual(baseline, Build(generator: 2).ModelHash);
        Assert.NotEqual(baseline, Build(edges: StaticEdges.Skip(1)).ModelHash);
        Assert.NotEqual(baseline, Build(nodes: [Solution, new GraphNode(Domain.Id, Domain.Kind, "Other", Domain.Path, false), App, DomainNs, AppNs, Package]).ModelHash);
        Assert.NotEqual(baseline, Build(nodes: [Solution, partialDomain, App, DomainNs, AppNs, Package], gaps: [gap]).ModelHash);
        Assert.NotEqual(baseline, Build(gaps: [gap]).ModelHash);
        Assert.NotEqual(Build(gaps: [gap]).ModelHash, Build(gaps: [new GraphGap(Domain.Id, "graph.unevaluated", "src/Domain/Domain.csproj", 13)]).ModelHash);
    }

    [Fact]
    public void The_fingerprint_of_a_fixed_model_is_pinned_so_it_never_changes_by_accident()
    {
        // The canonical JSON is part of the contract: a change here makes every cached diagram «desactualizado». This is the SHA-256 of the text
        // {"generator":1,"nodes":[{"id":"AgentStudio.slnx","kind":"Solution","name":"AgentStudio","path":"AgentStudio.slnx","partial":false},
        // {"id":"pkg:Newtonsoft.Json","kind":"Package","name":"Newtonsoft.Json","partial":false}, … sorted by id …],"edges":[{"from":…,"to":…,"kind":…}, … sorted
        // by from, to and kind, static only …],"gaps":[]} written by hand and hashed outside the code under test.
        Assert.Equal("8165cb9c96e38e236c39716c47c8807baa729bb7952212228a76bba1c3fe4cba", Build().ModelHash);
    }

    // ---- invariants ----

    [Fact]
    public void A_model_describes_a_commit_by_its_full_id_and_a_generator_from_one()
    {
        Assert.Throws<ArgumentException>(() => Build(commit: "abc1234"));
        Assert.Throws<ArgumentException>(() => Build(commit: new string('A', 40)));
        Assert.Throws<ArgumentException>(() => Build(commit: CommitA + "\n"));
        Assert.Equal(64, Build(commit: new string('3', 64)).Commit.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(generator: 0));
    }

    [Fact]
    public void Two_nodes_with_one_id_and_a_relation_to_nothing_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(nodes: [Solution, Domain, Domain]));
        Assert.Throws<ArgumentException>(() => Build(edges: [.. StaticEdges, new GraphEdge(App.Id, "src/Missing/Missing.csproj", GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static)]));
        Assert.Throws<ArgumentException>(() => Build(edges: [.. StaticEdges, new GraphEdge("src/Missing/Missing.csproj", Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static)]));
    }

    [Fact]
    public void A_relation_joins_the_kinds_of_node_its_kind_says()
    {
        Assert.Throws<ArgumentException>(() => Build(edges: [new GraphEdge(Domain.Id, App.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static)]));
        Assert.Throws<ArgumentException>(() => Build(edges: [new GraphEdge(Solution.Id, Package.Id, GraphEdgeKind.PackageReference, GraphEdgeOrigin.Static)]));
        Assert.Throws<ArgumentException>(() => Build(edges: [new GraphEdge(Domain.Id, AppNs.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static)]));
        Assert.Throws<ArgumentException>(() => Build(edges: [new GraphEdge(Domain.Id, App.Id, GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Static)]));
        Assert.Throws<ArgumentException>(() => Build(edges: [new GraphEdge(Package.Id, Domain.Id, GraphEdgeKind.PackageReference, GraphEdgeOrigin.Static)]));
    }

    [Fact]
    public void A_relation_is_not_there_twice_but_a_static_and_an_inferred_one_may_share_their_ends()
    {
        var twice = new GraphEdge(App.Id, Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static);
        var inferred = new GraphEdge(App.Id, Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, Inference);

        Assert.Throws<ArgumentException>(() => Build(edges: [.. StaticEdges, twice]));
        Assert.Equal(StaticEdges.Length + 1, Build(edges: [.. StaticEdges, inferred]).Edges.Count);
    }

    [Fact]
    public void A_gap_tells_about_a_node_that_exists_and_a_partial_node_has_a_gap_that_says_why()
    {
        var partialApp = new GraphNode(App.Id, App.Kind, App.Name, App.Path, true);

        Assert.Throws<ArgumentException>(() => Build(gaps: [new GraphGap("src/Missing/Missing.csproj", "graph.unevaluated", "src/App/App.csproj", null)]));
        Assert.Throws<ArgumentException>(() => Build(nodes: [Solution, Domain, partialApp, DomainNs, AppNs, Package]));
        var model = Build(nodes: [Solution, Domain, partialApp, DomainNs, AppNs, Package], gaps: [new GraphGap(App.Id, "graph.file-skipped", "src/App/Big.cs", null)]);
        Assert.Equal(new GraphCoverage(2, 1, 1, 1), model.Coverage);
        Assert.Equal(new GraphCoverage(2, 2, 0, 0), Build().Coverage);
    }

    [Fact]
    public void A_model_holds_no_null_and_no_more_than_its_caps()
    {
        Assert.Throws<ArgumentNullException>(() => new CodeGraph(CommitA, 1, null!, [], []));
        Assert.Throws<ArgumentException>(() => new CodeGraph(CommitA, 1, [null!], [], []));
        var tooMany = Enumerable.Range(0, CodeGraph.MaxNodes + 1).Select(i => new GraphNode($"n{i}", GraphNodeKind.Package, $"n{i}", null, false)).ToList();
        Assert.Throws<ArgumentException>(() => new CodeGraph(CommitA, 1, tooMany, [], []));
    }

    // ---- what the texts may be ----

    [Theory]
    [InlineData("../outside/Evil.csproj")]
    [InlineData("..\\..\\outside.csproj")]
    [InlineData("C:\\Users\\x\\Evil.csproj")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b.csproj")]
    [InlineData("a/b.csproj\n")]
    public void The_path_of_a_node_never_leads_out_of_the_copy(string path)
    {
        Assert.Throws<ArgumentException>(() => new GraphNode("src/X/X.csproj", GraphNodeKind.Project, "X", path, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("two\nlines")]
    [InlineData("tab\tin")]
    public void The_id_and_the_name_of_a_node_are_one_line_without_spaces_at_the_ends(string text)
    {
        Assert.Throws<ArgumentException>(() => new GraphNode(text, GraphNodeKind.Project, "X", null, false));
        Assert.Throws<ArgumentException>(() => new GraphNode("src/X/X.csproj", GraphNodeKind.Project, text, null, false));
    }

    [Fact]
    public void The_texts_of_a_node_have_their_caps()
    {
        Assert.Equal(GraphNode.MaxIdLength, new GraphNode(new string('a', GraphNode.MaxIdLength), GraphNodeKind.Package, "n", null, false).Id.Length);
        Assert.Throws<ArgumentException>(() => new GraphNode(new string('a', GraphNode.MaxIdLength + 1), GraphNodeKind.Package, "n", null, false));
        Assert.Throws<ArgumentException>(() => new GraphNode("id", GraphNodeKind.Package, new string('a', GraphNode.MaxNameLength + 1), null, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphNode("id", (GraphNodeKind)99, "n", null, false));
    }

    [Fact]
    public void A_gap_has_a_graph_code_the_file_that_tells_it_and_a_line_from_one()
    {
        Assert.Throws<ArgumentException>(() => new GraphGap(App.Id, "doctor.unevaluated", "src/App/App.csproj", null));
        Assert.Throws<ArgumentException>(() => new GraphGap(App.Id, "graph.outside-root", "..\\..\\target.csproj", null));
        Assert.Throws<ArgumentException>(() => new GraphGap(App.Id, "graph.outside-root", "../target.csproj", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphGap(App.Id, "graph.unevaluated", "src/App/App.csproj", 0));
        Assert.Equal(7, new GraphGap(App.Id, "graph.unevaluated", "src/App/App.csproj", 7).Line);
    }

    // ---- what an AI proposed ----

    [Fact]
    public void An_inferred_relation_says_who_proposed_it_and_a_static_one_does_not()
    {
        Assert.Throws<ArgumentException>(() => new GraphEdge(App.Id, Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred));
        Assert.Throws<ArgumentException>(() => new GraphEdge(App.Id, Domain.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static, Inference));
        Assert.Throws<ArgumentException>(() => new GraphEdge(App.Id, App.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphEdge(App.Id, Domain.Id, (GraphEdgeKind)99, GraphEdgeOrigin.Static));
        Assert.Throws<ArgumentException>(() => new GraphInference("analista", "modelo", T0, new string('x', GraphInference.MaxReasonLength + 1)));
        Assert.Throws<ArgumentException>(() => new GraphInference("analista", "modelo", T0, "two\nlines"));
    }

    [Fact]
    public void Laying_proposals_over_the_model_drops_those_whose_ends_do_not_exist_or_that_repeat_something()
    {
        var model = Build();
        var good = new GraphEdge(Domain.Id, App.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, Inference);
        var missingEnd = new GraphEdge(Domain.Id, "src/Missing/Missing.csproj", GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, Inference);
        var wrongKind = new GraphEdge(Domain.Id, AppNs.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, Inference);
        var repeatedInferred = new GraphEdge(Domain.Id, App.Id, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, new GraphInference("otro", "modelo-y", T0, "Otra razón."));

        var laid = model.WithInferred([good, missingEnd, wrongKind, repeatedInferred], out var discarded);

        Assert.Equal(3, discarded);
        Assert.Contains(good, laid.Edges);
        Assert.Equal(Inference, laid.Edges.Single(e => e.Origin == GraphEdgeOrigin.Inferred).Inference);
        Assert.Same(model, model.WithInferred([missingEnd], out var none));
        Assert.Equal(1, none);
        Assert.Throws<ArgumentException>(() => model.WithInferred([StaticEdges[0]], out _));
        Assert.Same(model, model.StaticOnly());
    }
}
