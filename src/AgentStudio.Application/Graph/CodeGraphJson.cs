using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Graph;

namespace AgentStudio.Application.Graph;

/// <summary>
/// The text of a <see cref="CodeGraph"/> (docs/20 D-18 and D-19): what the analysis process writes to its standard output and what the cache of
/// models keeps.
/// </summary>
/// <remarks>
/// Two rules shape it:
/// <list type="bullet">
/// <item><b>Written</b> in one fixed form: camelCase, enums by name, compact, nodes and relations in the order the model keeps them. It holds the
/// static model only: an inferred relation is never written, because the relations of an AI live apart (D-21).</item>
/// <item><b>Read</b> as data that is not trusted: a strict schema (nothing unknown, nothing repeated, nothing null, no number for a name), a depth
/// of 16, the caps of the model and of <see cref="GraphWorkerProtocol.MaxOutputBytes"/>, and the invariants of <see cref="CodeGraph"/> as the last word.
/// Anything that fails is an <see cref="InvalidDataException"/> whose message names what failed and never quotes the text.</item>
/// </list>
/// </remarks>
public static class CodeGraphJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The static model as text. A model that holds inferred relations is written without them.</summary>
    public static string Serialize(CodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var model = graph.StaticOnly();
        var document = new Document
        {
            Schema = GraphWorkerProtocol.SchemaVersion,
            Commit = model.Commit,
            Generator = model.GeneratorVersion,
            Nodes = [.. model.Nodes.Select(n => new NodeDto { Id = n.Id, Kind = n.Kind, Name = n.Name, Path = n.Path, Partial = n.Partial })],
            Edges = [.. model.Edges.Select(e => new EdgeDto { From = e.From, To = e.To, Kind = e.Kind })],
            Gaps = [.. model.Gaps.Select(g => new GapDto { Node = g.NodeId, Code = g.Code, Path = g.Path, Line = g.Line })],
        };
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Reads a model from text that is not trusted.</summary>
    /// <exception cref="InvalidDataException">The text is not a valid model.</exception>
    public static CodeGraph Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > GraphWorkerProtocol.MaxOutputBytes)
        {
            throw new InvalidDataException("The model is longer than the cap.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, Options)
                ?? throw new InvalidDataException("The model is null.");
            if (document.Schema != GraphWorkerProtocol.SchemaVersion)
            {
                throw new InvalidDataException("The model has a version of the format that this build does not read.");
            }

            if (document.Nodes.Count > CodeGraph.MaxNodes || document.Edges.Count > CodeGraph.MaxEdges || document.Gaps.Count > CodeGraph.MaxGaps)
            {
                throw new InvalidDataException("The model holds more than the caps allow.");
            }

            return new CodeGraph(
                document.Commit,
                document.Generator,
                [.. document.Nodes.Select(n => new GraphNode(n.Id, n.Kind, n.Name, n.Path, n.Partial))],
                [.. document.Edges.Select(e => new GraphEdge(e.From, e.To, e.Kind, GraphEdgeOrigin.Static))],
                [.. document.Gaps.Select(g => new GraphGap(g.Node, g.Code, g.Path, g.Line))]);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The model is not valid JSON of the schema.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The model breaks an invariant of the graph.", ex);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class Document
    {
        public required int Schema { get; init; }

        public required string Commit { get; init; }

        public required int Generator { get; init; }

        public required List<NodeDto> Nodes { get; init; }

        public required List<EdgeDto> Edges { get; init; }

        public required List<GapDto> Gaps { get; init; }
    }

    private sealed class NodeDto
    {
        public required string Id { get; init; }

        public required GraphNodeKind Kind { get; init; }

        public required string Name { get; init; }

        public string? Path { get; init; }

        public required bool Partial { get; init; }
    }

    private sealed class EdgeDto
    {
        public required string From { get; init; }

        public required string To { get; init; }

        public required GraphEdgeKind Kind { get; init; }
    }

    private sealed class GapDto
    {
        public required string Node { get; init; }

        public required string Code { get; init; }

        public required string Path { get; init; }

        public int? Line { get; init; }
    }
}
