using System.Globalization;
using System.Text;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Graph;

namespace AgentStudio.Memory.Diagrams;

/// <summary>
/// The two diagrams made from the model of Auto Graph (docs/20 D-23): the solution (<c>flowchart LR</c>, a <c>subgraph</c> per folder of the solution) and the
/// namespaces of one project. Pure: the same model and the same instant give the same bytes (docs/20 D-24), in any culture. Only the static part of the model is read, so
/// a relation an AI proposed never comes out. The identifiers are of its own (<c>n1</c>, <c>n2</c>… in the order of the ids of the model) and every name goes
/// through <see cref="MermaidText"/>.
/// </summary>
internal static class GraphDiagrams
{
    /// <summary>The diagram of the solution: its projects, grouped by folder, and the references between them.</summary>
    public static DiagramSource Solution(CodeGraph graph, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var model = graph.StaticOnly();
        var shown = model.Nodes
            .Where(n => n.Kind is GraphNodeKind.Project or GraphNodeKind.SolutionFolder)
            .Take(DiagramGrammar.MaxNodes)
            .ToList();
        var omittedNodes = model.Nodes.Count(n => n.Kind is GraphNodeKind.Project or GraphNodeKind.SolutionFolder) - shown.Count;
        var ids = Identifiers(shown);
        var contains = model.Edges.Where(e => e.Kind == GraphEdgeKind.Contains && ids.ContainsKey(e.From) && ids.ContainsKey(e.To)).ToList();
        var references = model.Edges.Where(e => e.Kind == GraphEdgeKind.ProjectReference && ids.ContainsKey(e.From) && ids.ContainsKey(e.To)).ToList();
        var omittedEdges = Math.Max(0, references.Count - DiagramGrammar.MaxEdges);
        references = [.. references.Take(DiagramGrammar.MaxEdges)];

        var builder = new StringBuilder();
        builder.Append("flowchart LR\n");
        var children = ChildrenByParent(shown, contains);
        var nodes = shown.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // The roots: what no folder of the solution holds. An iterative walk, so a model with folders nested very deep cannot exhaust the stack.
        var live = LiveFolders(shown, children, nodes);
        var stack = new Stack<(string Id, int Depth, bool Close)>();
        foreach (var root in Roots(shown, contains).Reverse())
        {
            stack.Push((root, 1, false));
        }

        while (stack.Count > 0)
        {
            var (id, depth, close) = stack.Pop();
            var indent = new string(' ', depth * 2);
            if (close)
            {
                builder.Append(indent).Append("end\n");
                continue;
            }

            var node = nodes[id];
            if (node.Kind == GraphNodeKind.SolutionFolder)
            {
                if (!live.Contains(id))
                {
                    continue;
                }

                builder.Append(indent).Append("subgraph ").Append(ids[id]).Append("[\"").Append(MermaidText.Label(node.Name)).Append("\"]\n");
                stack.Push((id, depth, true));
                foreach (var child in children.GetValueOrDefault(id, []).AsEnumerable().Reverse())
                {
                    stack.Push((child, depth + 1, false));
                }
            }
            else
            {
                builder.Append(indent).Append(ids[id]).Append("[\"").Append(ProjectLabel(node)).Append("\"]\n");
            }
        }

        foreach (var edge in references)
        {
            builder.Append("  ").Append(ids[edge.From]).Append(" --> ").Append(ids[edge.To]).Append('\n');
        }

        AppendOmitted(builder, omittedNodes, omittedEdges);
        var body = builder.ToString();
        return Source(DiagramKind.Solution, string.Empty, DiagramGrammar.Word(DiagramKind.Solution), DiagramGrammar.CommitOrigin(model.Commit), model.ModelHash, body, at);
    }

    /// <summary>The diagram of the namespaces of one project. The fingerprint of its model is the one of its text, so a project is «desactualizado» only when its own diagram changes.</summary>
    /// <exception cref="ArgumentException">The subject is not a project of the model.</exception>
    public static DiagramSource ProjectNamespaces(CodeGraph graph, string projectId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(projectId);
        var model = graph.StaticOnly();
        var project = model.Nodes.FirstOrDefault(n => n.Kind == GraphNodeKind.Project && string.Equals(n.Id, projectId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The subject is not a project of the model.", nameof(projectId));
        var owned = model.Edges
            .Where(e => e.Kind == GraphEdgeKind.Contains && string.Equals(e.From, project.Id, StringComparison.Ordinal))
            .Select(e => e.To)
            .ToHashSet(StringComparer.Ordinal);
        var namespaces = model.Nodes.Where(n => n.Kind == GraphNodeKind.Namespace && owned.Contains(n.Id)).Take(DiagramGrammar.MaxNodes).ToList();
        var omittedNodes = model.Nodes.Count(n => n.Kind == GraphNodeKind.Namespace && owned.Contains(n.Id)) - namespaces.Count;
        var ids = Identifiers(namespaces);
        var uses = model.Edges.Where(e => e.Kind == GraphEdgeKind.NamespaceUse && ids.ContainsKey(e.From) && ids.ContainsKey(e.To)).ToList();
        var omittedEdges = Math.Max(0, uses.Count - DiagramGrammar.MaxEdges);
        uses = [.. uses.Take(DiagramGrammar.MaxEdges)];

        var builder = new StringBuilder();
        builder.Append("flowchart LR\n");
        if (namespaces.Count == 0)
        {
            builder.Append("  n1[\"").Append(ProjectLabel(project)).Append("\"]\n");
        }
        else
        {
            builder.Append("  subgraph p1[\"").Append(ProjectLabel(project)).Append("\"]\n");
            foreach (var ns in namespaces)
            {
                builder.Append("    ").Append(ids[ns.Id]).Append("[\"").Append(MermaidText.Label(ns.Name)).Append("\"]\n");
            }

            builder.Append("  end\n");
        }

        foreach (var edge in uses)
        {
            builder.Append("  ").Append(ids[edge.From]).Append(" --> ").Append(ids[edge.To]).Append('\n');
        }

        AppendOmitted(builder, omittedNodes, omittedEdges);
        var body = builder.ToString();
        return Source(DiagramKind.ProjectNamespaces, project.Id, DiagramGrammar.Word(DiagramKind.ProjectNamespaces), DiagramGrammar.CommitOrigin(model.Commit), DiagramGrammar.Sha256(body), body, at);
    }

    private static DiagramSource Source(DiagramKind kind, string subject, string word, string origin, string modelHash, string body, DateTimeOffset at) =>
        new(kind, subject, modelHash, DiagramGrammar.Header(word, origin, modelHash, at) + body);

    private static string ProjectLabel(GraphNode node) => MermaidText.Label(node.Name) + (node.Partial ? DiagramGrammar.Words.ProjectSuffix : string.Empty);

    /// <summary>The identifiers <c>n1</c>, <c>n2</c>… in the order the nodes are given (the model sorts them by id, ordinal).</summary>
    private static Dictionary<string, string> Identifiers(IReadOnlyList<GraphNode> nodes)
    {
        var ids = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            ids[nodes[i].Id] = "n" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return ids;
    }

    /// <summary>The nodes each folder holds, by id in ordinal order. A node has one parent (the model checks it).</summary>
    private static Dictionary<string, List<string>> ChildrenByParent(IReadOnlyList<GraphNode> shown, IReadOnlyList<GraphEdge> contains)
    {
        var present = shown.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in contains.Where(e => present.Contains(e.From)))
        {
            if (!children.TryGetValue(edge.From, out var list))
            {
                children[edge.From] = list = [];
            }

            list.Add(edge.To);
        }

        foreach (var list in children.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        return children;
    }

    /// <summary>The nodes no shown node contains: they come out at the top. The solution itself is not drawn.</summary>
    private static IEnumerable<string> Roots(IReadOnlyList<GraphNode> shown, IReadOnlyList<GraphEdge> contains)
    {
        var present = shown.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var held = contains.Where(e => present.Contains(e.From)).Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        return shown.Where(n => !held.Contains(n.Id)).Select(n => n.Id);
    }

    /// <summary>The folders that hold at least one project, directly or through another folder: an empty <c>subgraph</c> is not drawn.</summary>
    private static HashSet<string> LiveFolders(IReadOnlyList<GraphNode> shown, Dictionary<string, List<string>> children, Dictionary<string, GraphNode> nodes)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (parent, list) in children)
        {
            foreach (var child in list)
            {
                parentOf[child] = parent;
            }
        }

        foreach (var project in shown.Where(n => n.Kind == GraphNodeKind.Project))
        {
            // The model has no cycles among folders, and a step bound keeps even a broken one finite.
            var current = project.Id;
            for (var steps = 0; steps <= nodes.Count && parentOf.TryGetValue(current, out var parent); steps++)
            {
                if (nodes.TryGetValue(parent, out var holder) && holder.Kind == GraphNodeKind.SolutionFolder && !live.Add(parent))
                {
                    break;
                }

                current = parent;
            }
        }

        return live;
    }

    private static void AppendOmitted(StringBuilder builder, int nodes, int edges)
    {
        if (nodes > 0 || edges > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  %% omitido: {nodes} nodos y {edges} relaciones\n");
        }
    }
}
