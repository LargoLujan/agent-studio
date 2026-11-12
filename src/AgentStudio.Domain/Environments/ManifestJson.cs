using System.Text;
using System.Text.Json;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// A JSON manifest of a repository read as untrusted data (docs/20 D-06): validated in one pass of <see cref="Utf8JsonReader"/> that counts the depth
/// (at most <see cref="MaxDepth"/>) and rejects a repeated key, before anything is built from it. A failure is one of the three kinds of issue that say
/// why: <see cref="DeclaredIssueKind.Malformed"/>, <see cref="DeclaredIssueKind.TooDeep"/> or <see cref="DeclaredIssueKind.DuplicateKey"/>. The pass also
/// records the line of each property by its path (<c>sdk/version</c>), so what is read can say where it was. It never throws.
/// </summary>
internal sealed class ManifestJson : IDisposable
{
    /// <summary>The deepest nesting accepted: the top-level object is level 1.</summary>
    internal const int MaxDepth = 32;

    private const int MaxLines = 5000;

    private readonly Dictionary<string, int> _lines;

    private ManifestJson(JsonDocument? document, DeclaredIssueKind? failure, Dictionary<string, int> lines)
    {
        Document = document;
        Failure = failure;
        _lines = lines;
    }

    /// <summary>The document, when the text is valid; its root is an object.</summary>
    internal JsonDocument? Document { get; }

    /// <summary>Why the text was not accepted; null when it was.</summary>
    internal DeclaredIssueKind? Failure { get; }

    /// <param name="comments">Whether comments and a trailing comma are admitted (<c>global.json</c> and <c>devcontainer.json</c>).</param>
    internal static ManifestJson Read(string text, bool comments)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var failure = Validate(bytes, comments, lines);
        if (failure is not null)
        {
            return new ManifestJson(null, failure, lines);
        }

        try
        {
            var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow, AllowTrailingCommas = comments, MaxDepth = 64 });
            return new ManifestJson(document, null, lines);
        }
        catch (JsonException)
        {
            return new ManifestJson(null, DeclaredIssueKind.Malformed, lines);
        }
    }

    /// <summary>The line of the property at <paramref name="path"/> (<c>engines/node</c>), or null when the pass did not record it.</summary>
    internal int? LineOf(string path) => _lines.TryGetValue(path, out var line) ? line : null;

    public void Dispose() => Document?.Dispose();

    private static DeclaredIssueKind? Validate(byte[] bytes, bool comments, Dictionary<string, int> lines)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions { CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow, AllowTrailingCommas = comments, MaxDepth = 64 });
        var keys = new Stack<HashSet<string>?>();
        var path = new List<string>();
        string? pending = null;
        int[]? newlines = null;
        var depth = 0;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        if (depth == 0 && reader.TokenType != JsonTokenType.StartObject)
                        {
                            return DeclaredIssueKind.Malformed;
                        }

                        if (depth >= MaxDepth)
                        {
                            return DeclaredIssueKind.TooDeep;
                        }

                        if (depth > 0)
                        {
                            path.Add(pending ?? "[]");
                        }

                        pending = null;
                        depth++;
                        keys.Push(reader.TokenType == JsonTokenType.StartObject ? new HashSet<string>(StringComparer.Ordinal) : null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        keys.Pop();
                        if (depth > 0)
                        {
                            path.RemoveAt(path.Count - 1);
                        }

                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (keys.Peek() is not { } seen || !seen.Add(name))
                        {
                            return DeclaredIssueKind.DuplicateKey;
                        }

                        pending = name;
                        if (lines.Count < MaxLines)
                        {
                            newlines ??= NewlineOffsets(bytes);
                            lines.TryAdd(string.Join('/', [.. path, name]), LineAt(newlines, reader.TokenStartIndex));
                        }

                        break;
                    default:
                        pending = null;
                        if (depth == 0)
                        {
                            return DeclaredIssueKind.Malformed;
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            return DeclaredIssueKind.Malformed;
        }
        catch (InvalidOperationException)
        {
            return DeclaredIssueKind.Malformed;
        }

        return depth == 0 && keys.Count == 0 ? null : DeclaredIssueKind.Malformed;
    }

    private static int[] NewlineOffsets(byte[] bytes)
    {
        var offsets = new List<int>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                offsets.Add(i);
            }
        }

        return [.. offsets];
    }

    private static int LineAt(int[] newlines, long offset)
    {
        var index = Array.BinarySearch(newlines, (int)Math.Min(offset, int.MaxValue));
        return (index >= 0 ? index : ~index) + 1;
    }
}
