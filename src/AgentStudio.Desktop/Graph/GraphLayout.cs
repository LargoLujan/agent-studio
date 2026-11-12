using System.Windows;

namespace AgentStudio.Desktop.Graph;

/// <summary>
/// The layout of the canvas of Auto Graph (docs/20 D-22): deterministic and calculated here, with no library of layout. Each node goes in a layer
/// by the depth of its dependencies, and inside a layer the nodes are ordered by name. The same graph is always laid out the same way, so a person
/// who comes back finds everything where it was.
/// </summary>
public static class GraphLayout
{
    /// <summary>The distance between two layers, left to right.</summary>
    public const double LayerWidth = 300;

    /// <summary>The distance between two nodes of a layer, top to bottom.</summary>
    public const double RowHeight = 120;

    /// <summary>
    /// The layer of each node: 0 for a node that depends on nothing, and one more than its deepest dependency otherwise, so a dependency is always
    /// to the left of whoever uses it. A cycle (two namespaces that use each other) does not hold the layout: the relation that closes it, found in
    /// the order of the names, is left out of the depth.
    /// </summary>
    /// <param name="nodes">The ids of the nodes.</param>
    /// <param name="dependencies">Pairs (user, used) between those nodes; a pair with an end that is not in <paramref name="nodes"/> is ignored.</param>
    public static IReadOnlyDictionary<string, int> Layers(IReadOnlyCollection<string> nodes, IEnumerable<(string User, string Used)> dependencies)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(dependencies);
        var known = nodes.ToHashSet(StringComparer.Ordinal);
        var uses = known.ToDictionary(n => n, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (user, used) in dependencies)
        {
            if (user != used && known.Contains(user) && known.Contains(used))
            {
                uses[user].Add(used);
            }
        }

        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        // An explicit stack: a chain of thousands of dependencies must not use the stack of the UI thread.
        foreach (var start in known.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            var stack = new Stack<(string Node, IEnumerator<string> Next)>();
            state[start] = 1;
            stack.Push((start, uses[start].GetEnumerator()));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Peek();
                if (next.MoveNext())
                {
                    var used = next.Current;
                    if (!state.ContainsKey(used))
                    {
                        state[used] = 1;
                        stack.Push((used, uses[used].GetEnumerator()));
                    }
                    else if (state[used] == 1)
                    {
                        // Back to a node still being visited: this relation closes a cycle and is not counted.
                        // The enumerator of a set that changed is not valid: the node reads its remaining relations again from the start.
                        uses[node].Remove(used);
                        stack.Pop();
                        stack.Push((node, uses[node].GetEnumerator()));
                    }

                    continue;
                }

                var depth = 0;
                foreach (var used in uses[node])
                {
                    depth = Math.Max(depth, layer[used] + 1);
                }

                layer[node] = depth;
                state[node] = 2;
                stack.Pop();
            }
        }

        return layer;
    }

    /// <summary>The place of each node: its layer picks the column, its order by name inside the layer picks the row.</summary>
    public static IReadOnlyDictionary<string, Point> Locations(
        IReadOnlyCollection<string> nodes,
        IEnumerable<(string User, string Used)> dependencies,
        Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        var layers = Layers(nodes, dependencies);
        var result = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var group in nodes.GroupBy(n => layers[n]).OrderBy(g => g.Key))
        {
            var row = 0;
            foreach (var id in group.OrderBy(nameOf, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal))
            {
                result[id] = new Point(group.Key * LayerWidth, row++ * RowHeight);
            }
        }

        return result;
    }
}
