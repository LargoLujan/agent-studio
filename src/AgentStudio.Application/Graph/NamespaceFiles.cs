using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Graph;

/// <summary>
/// The files of the copy that declare each namespace of the model (docs/20 D-22, F6-5): where the person goes from a namespace to a file. It is
/// not part of <see cref="Domain.Graph.CodeGraph"/> and never of its hash: the model says what the solution is made of, this says where to find it,
/// and a file moving from one folder to another is not a change of the model. Every path is relative to the copy and never leads out of it.
/// </summary>
public sealed record NamespaceFiles
{
    /// <summary>The most files listed for one namespace: a namespace in thousands of files lists the first ones, in ordinal order.</summary>
    public const int MaxFilesPerNamespace = 200;

    /// <summary>The most paths in the whole list.</summary>
    public const int MaxTotal = 100_000;

    public static NamespaceFiles Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    private readonly Dictionary<string, IReadOnlyList<string>> _byNode;

    /// <exception cref="ArgumentException">A node id that is not one line, a path that is not safe, more files than the caps allow, or a file listed twice for a namespace.</exception>
    public NamespaceFiles(IReadOnlyDictionary<string, IReadOnlyList<string>> byNode)
    {
        ArgumentNullException.ThrowIfNull(byNode);
        var total = 0;
        _byNode = new Dictionary<string, IReadOnlyList<string>>(byNode.Count, StringComparer.Ordinal);
        foreach (var (node, files) in byNode)
        {
            ArgumentNullException.ThrowIfNull(files);
            if (string.IsNullOrWhiteSpace(node) || node.Length > Domain.Graph.GraphNode.MaxIdLength || UntrustedText.HasHidden(node))
            {
                throw new ArgumentException("The key of a list of files is the id of a node: one line.", nameof(byNode));
            }

            if (files.Count > MaxFilesPerNamespace || files.Any(f => !RepositoryPaths.IsSafeRelative(f)))
            {
                throw new ArgumentException($"A namespace lists up to {MaxFilesPerNamespace} files, each relative to the copy and never leading out of it.", nameof(byNode));
            }

            var sorted = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (sorted.Distinct(StringComparer.Ordinal).Count() != sorted.Count)
            {
                throw new ArgumentException("A file is listed once for each namespace.", nameof(byNode));
            }

            total += sorted.Count;
            if (total > MaxTotal)
            {
                throw new ArgumentException($"At most {MaxTotal} paths are listed.", nameof(byNode));
            }

            _byNode.Add(node, sorted);
        }
    }

    /// <summary>The namespaces that list files, by node id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Entries => _byNode;

    /// <summary>The files that declare the namespace, in ordinal order; none when the model does not know it.</summary>
    public IReadOnlyList<string> For(string nodeId) => _byNode.TryGetValue(nodeId, out var files) ? files : [];

    public bool Equals(NamespaceFiles? other) =>
        other is not null && _byNode.Count == other._byNode.Count
        && _byNode.All(e => other._byNode.TryGetValue(e.Key, out var files) && files.SequenceEqual(e.Value));

    public override int GetHashCode() => _byNode.Count;
}

/// <summary>The graph of a commit with the files that declare each namespace: what the analysis process returns and the cache keeps.</summary>
public sealed record GraphBuild(Domain.Graph.CodeGraph Graph, NamespaceFiles Files);
