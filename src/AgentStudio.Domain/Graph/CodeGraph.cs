using System.Text.Json;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Domain.Graph;

/// <summary>What a node of Auto Graph is (docs/20 D-20). The names are written to the cache of models, so new kinds are appended.</summary>
public enum GraphNodeKind
{
    Solution,
    Project,
    Namespace,
    Package,
}

/// <summary>What joins two nodes (docs/20 D-20). The names are written to the cache of models, so new kinds are appended.</summary>
public enum GraphEdgeKind
{
    /// <summary>A solution holds a project, or a project holds a namespace.</summary>
    Contains,

    /// <summary>A project references another project.</summary>
    ProjectReference,

    /// <summary>A project references a package (a literal <c>PackageReference</c>; the versions of the central file are not applied).</summary>
    PackageReference,

    /// <summary>A namespace uses another one: a file that declares the first has a <c>using</c> of the second. Syntactic, not a call graph.</summary>
    NamespaceUse,
}

/// <summary>Where a relation comes from (docs/20 D-20 and D-21): read from the files, or proposed by an AI.</summary>
public enum GraphEdgeOrigin
{
    Static,
    Inferred,
}

/// <summary>
/// A node of the model (docs/20 D-20). The <paramref name="Id"/> is stable: the relative path of the project, or the project and the name
/// of the namespace. Everything in it came from a repository and is not trusted (ADR-0018): it is one line, with caps, and a path is
/// relative to the copy of the commit and never leads out of it (<see cref="RepositoryPaths.IsSafeRelative"/>).
/// </summary>
/// <param name="Partial">What MSBuild would have resolved was not evaluated, or a file was skipped: the node says so and its gaps say why. Nothing partial is shown as complete.</param>
public sealed record GraphNode
{
    public const int MaxIdLength = 400;

    public const int MaxNameLength = 200;

    public GraphNode(string id, GraphNodeKind kind, string name, string? path, bool partial)
    {
        Id = GraphText.RequireLine(id, MaxIdLength, nameof(id));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of node.");
        }

        Name = GraphText.RequireLine(name, MaxNameLength, nameof(name));
        if (path is not null && !RepositoryPaths.IsSafeRelative(path))
        {
            throw new ArgumentException("The path of a node is relative to the copy of the commit, in forward slashes, and never leads out of it.", nameof(path));
        }

        Kind = kind;
        Path = path;
        Partial = partial;
    }

    public string Id { get; }

    public GraphNodeKind Kind { get; }

    public string Name { get; }

    public string? Path { get; }

    public bool Partial { get; }
}

/// <summary>Who proposed a relation that was not read from the files, when and why (docs/20 D-21). It never enters the hash of the model.</summary>
public sealed record GraphInference
{
    public const int MaxReasonLength = 200;

    public GraphInference(string agent, string model, DateTimeOffset at, string reason)
    {
        Agent = GraphText.RequireLine(agent, GraphNode.MaxNameLength, nameof(agent));
        Model = GraphText.RequireLine(model, GraphNode.MaxNameLength, nameof(model));
        Reason = GraphText.RequireLine(reason, MaxReasonLength, nameof(reason));
        At = at;
    }

    public string Agent { get; }

    public string Model { get; }

    public DateTimeOffset At { get; }

    /// <summary>The reason the agent gave: text of an agent, shown framed as such and never part of a diagram.</summary>
    public string Reason { get; }
}

/// <summary>
/// A relation between two nodes (docs/20 D-20 and D-21). A static one has no inference; an inferred one always has it. The ends are
/// ordinary node ids: whether they exist is checked by the model that holds the relation.
/// </summary>
public sealed record GraphEdge
{
    public GraphEdge(string from, string to, GraphEdgeKind kind, GraphEdgeOrigin origin, GraphInference? inference = null)
    {
        From = GraphText.RequireLine(from, GraphNode.MaxIdLength, nameof(from));
        To = GraphText.RequireLine(to, GraphNode.MaxIdLength, nameof(to));
        if (string.Equals(From, To, StringComparison.Ordinal))
        {
            throw new ArgumentException("A node is not related to itself.", nameof(to));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of relation.");
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown origin of a relation.");
        }

        if ((origin == GraphEdgeOrigin.Inferred) != (inference is not null))
        {
            throw new ArgumentException("An inferred relation says who proposed it, and a static one has no inference.", nameof(inference));
        }

        Kind = kind;
        Origin = origin;
        Inference = inference;
    }

    public string From { get; }

    public string To { get; }

    public GraphEdgeKind Kind { get; }

    public GraphEdgeOrigin Origin { get; }

    public GraphInference? Inference { get; }
}

/// <summary>
/// Something the reading of a node did not cover (docs/20 D-16 to D-18): a code such as <c>graph.unevaluated</c>,
/// <c>graph.outside-root</c> or <c>graph.file-skipped</c>, the file that tells it (relative to the copy, never the offending target) and
/// the line, when it has one.
/// </summary>
public sealed record GraphGap
{
    public GraphGap(string nodeId, string code, string path, int? line)
    {
        NodeId = GraphText.RequireLine(nodeId, GraphNode.MaxIdLength, nameof(nodeId));
        Code = DoctorCodes.Require(code, nameof(code), "graph.");
        if (!RepositoryPaths.IsSafeRelative(path))
        {
            throw new ArgumentException("The path of a gap is the file that tells it, relative to the copy of the commit and never leading out of it.", nameof(path));
        }

        if (line is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line, "A line starts at 1.");
        }

        Path = path;
        Line = line;
    }

    public string NodeId { get; }

    public string Code { get; }

    public string Path { get; }

    public int? Line { get; }
}

/// <summary>How much of the solution was read completely (docs/20 D-20): nothing partial is presented as complete.</summary>
public sealed record GraphCoverage(int Projects, int CompleteProjects, int PartialProjects, int Gaps);

/// <summary>
/// The model of Auto Graph of a commit (docs/20 D-20): nodes, relations with their origin, and gaps. It is a value: sorted when it is built,
/// compared by what it holds and always valid, whoever wrote what it was built from (the analysis process, a cache, a test).
/// </summary>
/// <remarks>
/// <see cref="ModelHash"/> is the SHA-256 of the canonical JSON of the static part (nodes, static relations and gaps) and the version of the
/// generator. It leaves out the commit and every inferred relation: the same model of two commits has the same hash, which is what decides
/// «desactualizado» (D-25), and a proposal of an AI never changes it.
/// </remarks>
public sealed record CodeGraph
{
    /// <summary>The caps of a model: what a hostile or broken analysis cannot exceed when its output is read back (docs/20 D-18).</summary>
    public const int MaxNodes = 100_000;

    public const int MaxEdges = 500_000;

    public const int MaxGaps = 20_000;

    private string? _modelHash;

    /// <exception cref="ArgumentException">A commit that is not a full id, a version of the generator below 1, a repeated id, a relation whose ends are not nodes or do not fit its kind, a gap of a node that does not exist, a partial node with no gap, or a cap exceeded.</exception>
    public CodeGraph(string commit, int generatorVersion, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, IReadOnlyList<GraphGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(gaps);
        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("A model describes a commit by its full id (40 or 64 lowercase hexadecimal characters).", nameof(commit));
        }

        if (generatorVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(generatorVersion), generatorVersion, "The version of the generator starts at 1.");
        }

        if (nodes.Count > MaxNodes || edges.Count > MaxEdges || gaps.Count > MaxGaps)
        {
            throw new ArgumentException($"A model holds up to {MaxNodes} nodes, {MaxEdges} relations and {MaxGaps} gaps.");
        }

        if (nodes.Any(n => n is null) || edges.Any(e => e is null) || gaps.Any(g => g is null))
        {
            throw new ArgumentException("A model holds no null element.");
        }

        var byId = new Dictionary<string, GraphNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                throw new ArgumentException($"Two nodes have the id '{node.Id}'.", nameof(nodes));
            }
        }

        var seen = new HashSet<(string, string, GraphEdgeKind, GraphEdgeOrigin)>();
        foreach (var edge in edges)
        {
            if (!byId.TryGetValue(edge.From, out var from) || !byId.TryGetValue(edge.To, out var to))
            {
                throw new ArgumentException($"The relation {edge.Kind} '{edge.From}' to '{edge.To}' joins a node that does not exist.", nameof(edges));
            }

            if (!Fits(edge.Kind, from.Kind, to.Kind))
            {
                throw new ArgumentException($"A relation {edge.Kind} does not join a {from.Kind} to a {to.Kind}.", nameof(edges));
            }

            if (!seen.Add((edge.From, edge.To, edge.Kind, edge.Origin)))
            {
                throw new ArgumentException($"The relation {edge.Kind} '{edge.From}' to '{edge.To}' is there twice.", nameof(edges));
            }
        }

        foreach (var gap in gaps)
        {
            if (!byId.ContainsKey(gap.NodeId))
            {
                throw new ArgumentException($"A gap tells about the node '{gap.NodeId}', which does not exist.", nameof(gaps));
            }
        }

        var gapped = gaps.Select(g => g.NodeId).ToHashSet(StringComparer.Ordinal);
        var unexplained = nodes.FirstOrDefault(n => n.Partial && !gapped.Contains(n.Id));
        if (unexplained is not null)
        {
            throw new ArgumentException($"The node '{unexplained.Id}' is partial and no gap says why.", nameof(gaps));
        }

        Commit = commit;
        GeneratorVersion = generatorVersion;
        Nodes = [.. nodes.OrderBy(n => n.Id, StringComparer.Ordinal)];
        Edges = [.. edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.Origin)];
        Gaps = [.. gaps
            .OrderBy(g => g.NodeId, StringComparer.Ordinal)
            .ThenBy(g => g.Code, StringComparer.Ordinal)
            .ThenBy(g => g.Path, StringComparer.Ordinal)
            .ThenBy(g => g.Line ?? 0)];
    }

    /// <summary>The commit the model describes, by its full id.</summary>
    public string Commit { get; }

    public int GeneratorVersion { get; }

    /// <summary>The nodes, by id in ordinal order.</summary>
    public IReadOnlyList<GraphNode> Nodes { get; }

    /// <summary>The relations, static and inferred, by from, to, kind and origin in ordinal order.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; }

    public IReadOnlyList<GraphGap> Gaps { get; }

    /// <summary>The fingerprint of the static model (64 lowercase hexadecimal characters); see the remarks of the type.</summary>
    public string ModelHash => _modelHash ??= ComputeModelHash();

    /// <summary>How much was read completely.</summary>
    public GraphCoverage Coverage
    {
        get
        {
            var projects = Nodes.Where(n => n.Kind == GraphNodeKind.Project).ToList();
            var partial = projects.Count(n => n.Partial);
            return new GraphCoverage(projects.Count, projects.Count - partial, partial, Gaps.Count);
        }
    }

    /// <summary>The model without any relation proposed by an AI: what the diagrams and the hash are made from.</summary>
    public CodeGraph StaticOnly() =>
        Edges.All(e => e.Origin == GraphEdgeOrigin.Static)
            ? this
            : new CodeGraph(Commit, GeneratorVersion, Nodes, [.. Edges.Where(e => e.Origin == GraphEdgeOrigin.Static)], Gaps);

    /// <summary>
    /// The model with the relations an AI proposed laid over it (docs/20 D-21). A relation whose ends are not nodes of the model, or that
    /// repeats one already there, is left out and counted in <paramref name="discarded"/>. The hash does not change.
    /// </summary>
    /// <exception cref="ArgumentException">A relation that is not an inferred one.</exception>
    public CodeGraph WithInferred(IEnumerable<GraphEdge> inferred, out int discarded)
    {
        ArgumentNullException.ThrowIfNull(inferred);
        var ids = Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var kinds = Nodes.ToDictionary(n => n.Id, n => n.Kind, StringComparer.Ordinal);
        var accepted = new List<GraphEdge>();
        var seen = Edges.Select(e => (e.From, e.To, e.Kind, e.Origin)).ToHashSet();
        discarded = 0;
        foreach (var edge in inferred)
        {
            ArgumentNullException.ThrowIfNull(edge);
            if (edge.Origin != GraphEdgeOrigin.Inferred)
            {
                throw new ArgumentException("Only relations proposed by an AI are laid over the model.", nameof(inferred));
            }

            if (!ids.Contains(edge.From) || !ids.Contains(edge.To) || !Fits(edge.Kind, kinds[edge.From], kinds[edge.To])
                || !seen.Add((edge.From, edge.To, edge.Kind, edge.Origin)))
            {
                discarded++;
                continue;
            }

            accepted.Add(edge);
        }

        return accepted.Count == 0 ? this : new CodeGraph(Commit, GeneratorVersion, Nodes, [.. Edges, .. accepted], Gaps);
    }

    public bool Equals(CodeGraph? other) =>
        other is not null && Commit == other.Commit && GeneratorVersion == other.GeneratorVersion
        && Nodes.SequenceEqual(other.Nodes) && Edges.SequenceEqual(other.Edges) && Gaps.SequenceEqual(other.Gaps);

    public override int GetHashCode() => HashCode.Combine(Commit, GeneratorVersion, Nodes.Count, Edges.Count, Gaps.Count);

    /// <summary>Which kind of node each kind of relation joins: the structure is checked when the model is built, never trusted.</summary>
    private static bool Fits(GraphEdgeKind kind, GraphNodeKind from, GraphNodeKind to) => kind switch
    {
        GraphEdgeKind.Contains => (from == GraphNodeKind.Solution && to == GraphNodeKind.Project)
            || (from == GraphNodeKind.Project && to == GraphNodeKind.Namespace),
        GraphEdgeKind.ProjectReference => from == GraphNodeKind.Project && to == GraphNodeKind.Project,
        GraphEdgeKind.PackageReference => from == GraphNodeKind.Project && to == GraphNodeKind.Package,
        GraphEdgeKind.NamespaceUse => from == GraphNodeKind.Namespace && to == GraphNodeKind.Namespace,
        _ => false,
    };

    private string ComputeModelHash()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("generator", GeneratorVersion);
            json.WriteStartArray("nodes");
            foreach (var node in Nodes)
            {
                json.WriteStartObject();
                json.WriteString("id", node.Id);
                json.WriteString("kind", node.Kind.ToString());
                json.WriteString("name", node.Name);
                if (node.Path is not null)
                {
                    json.WriteString("path", node.Path);
                }

                json.WriteBoolean("partial", node.Partial);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("edges");
            foreach (var edge in Edges.Where(e => e.Origin == GraphEdgeOrigin.Static))
            {
                json.WriteStartObject();
                json.WriteString("from", edge.From);
                json.WriteString("to", edge.To);
                json.WriteString("kind", edge.Kind.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("gaps");
            foreach (var gap in Gaps)
            {
                json.WriteStartObject();
                json.WriteString("node", gap.NodeId);
                json.WriteString("code", gap.Code);
                json.WriteString("path", gap.Path);
                if (gap.Line is { } line)
                {
                    json.WriteNumber("line", line);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return ContentHash.Sha256Hex(stream.ToArray());
    }
}

/// <summary>The text rules of the model: every text came from a repository, so each is one line with a cap.</summary>
internal static class GraphText
{
    internal static string RequireLine(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Expected one line of 1 to {maxLength} characters with no control characters and no spaces at its ends.", parameterName);
        }

        return value;
    }
}
