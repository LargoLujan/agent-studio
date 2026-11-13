using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Memory.Diagrams;

/// <summary>What the index remembers of one version of a diagram: the fingerprint of its model, the version of the generators, the commit it describes and the fingerprint of its text.</summary>
internal sealed record IndexVersion(string ModelHash, int Generator, string? Describes, DateTimeOffset GeneratedAt, string TextSha256);

/// <summary>What the person asked to export and what was written: the path (relative to the project), the SHA-256 of the file as written and the one of the text of the cache it came from. Never read back from the file.</summary>
internal sealed record IndexExport(string Path, string FileSha256, string TextSha256);

/// <summary>A diagram of the cache: its current version, the previous one (<c>.prev</c>) and what was exported.</summary>
internal sealed record IndexEntry(DiagramRef Diagram, IndexVersion Current, IndexVersion? Previous, IndexExport? Export);

/// <summary>The index of the diagrams of a project (docs/20 D-25): the one place that says which diagrams exist, what they were made from and what was exported. A value.</summary>
internal sealed class DiagramIndex
{
    public static DiagramIndex Empty { get; } = new([]);

    public DiagramIndex(IReadOnlyList<IndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries.OrderBy(e => e.Diagram.Kind).ThenBy(e => e.Diagram.Subject, StringComparer.Ordinal)];
    }

    public IReadOnlyList<IndexEntry> Entries { get; }

    public IndexEntry? Find(DiagramRef diagram) => Entries.FirstOrDefault(e => e.Diagram == diagram);

    public DiagramIndex With(IndexEntry entry) => new([.. Entries.Where(e => e.Diagram != entry.Diagram), entry]);
}

/// <summary>
/// The cache of the diagrams on disk (docs/20 D-25): <c>&lt;folder&gt;/&lt;project&gt;/index.json</c>, and per diagram its text (<c>.mmd</c>) and its previous text
/// (<c>.prev</c>). The folder is given by the composition (<c>%LOCALAPPDATA%\AgentStudio\diagrams</c>): a test gives a temporary one. The names of the files are
/// derived (the kind and a fingerprint of the subject), never the subject: a name of a project is data. Everything is written to a temporary file and
/// moved over the real one, and the index is read as data that is not trusted: one that does not read back is an empty index, and what it names but
/// is not there is not a diagram. One writer at a time.
/// </summary>
internal sealed class DiagramCacheStore(string folder)
{
    private const int MaxIndexBytes = 8 * 1024 * 1024;

    private const int MaxEntries = 10_000;

    private readonly string _folder = Path.GetFullPath(folder);

    /// <summary>Serializes the reads and writes of the cache: a queue of one for everything that changes it.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The name of the file of a diagram in the cache, without extension.</summary>
    public static string Stem(DiagramRef diagram) =>
        DiagramGrammar.Word(diagram.Kind) + "-" + ContentHash.Sha256Hex(diagram.Kind + "\n" + diagram.Subject)[..24];

    public string ProjectFolder(string projectId) => Path.Combine(_folder, SafeName.Of(projectId));

    public async Task<DiagramIndex> LoadAsync(string projectId, CancellationToken ct)
    {
        var path = Path.Combine(ProjectFolder(projectId), "index.json");
        var bytes = await ReadBytesAsync(path, MaxIndexBytes, ct).ConfigureAwait(false);
        return bytes is null ? DiagramIndex.Empty : Parse(bytes);
    }

    public async Task SaveAsync(string projectId, DiagramIndex index, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", 1);
            json.WriteStartArray("diagrams");
            foreach (var entry in index.Entries)
            {
                json.WriteStartObject();
                json.WriteString("kind", entry.Diagram.Kind.ToString());
                json.WriteString("subject", entry.Diagram.Subject);
                WriteVersion(json, "current", entry.Current);
                if (entry.Previous is not null)
                {
                    WriteVersion(json, "previous", entry.Previous);
                }

                if (entry.Export is { } export)
                {
                    json.WriteStartObject("export");
                    json.WriteString("path", export.Path);
                    json.WriteString("file", export.FileSha256);
                    json.WriteString("text", export.TextSha256);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        await WriteBytesAsync(Path.Combine(ProjectFolder(projectId), "index.json"), stream.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>The text kept for a diagram (<paramref name="previous"/>: its previous one), or null when there is none or it is not the one the index fingerprints.</summary>
    public async Task<string?> ReadTextAsync(string projectId, DiagramRef diagram, bool previous, string expectedSha256, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(TextPath(projectId, diagram, previous), DiagramSource.MaxMermaidLength * 4, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return string.Equals(ContentHash.Sha256Hex(bytes), expectedSha256, StringComparison.Ordinal) ? new UTF8Encoding(false, true).GetString(bytes) : null;
    }

    public Task WriteTextAsync(string projectId, DiagramRef diagram, bool previous, string text, CancellationToken ct) =>
        WriteBytesAsync(TextPath(projectId, diagram, previous), new UTF8Encoding(false).GetBytes(text), ct);

    public void DeleteText(string projectId, DiagramRef diagram, bool previous)
    {
        try
        {
            File.Delete(TextPath(projectId, diagram, previous));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A previous text that cannot be deleted stays as an orphan: the index no longer names it, so nothing reads it.
        }
    }

    private string TextPath(string projectId, DiagramRef diagram, bool previous) =>
        Path.Combine(ProjectFolder(projectId), Stem(diagram) + (previous ? ".prev" : ".mmd"));

    private static async Task<byte[]?> ReadBytesAsync(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (stream.Length > maxBytes)
            {
                return null;
            }

            var buffer = new byte[stream.Length];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void WriteVersion(Utf8JsonWriter json, string name, IndexVersion version)
    {
        json.WriteStartObject(name);
        json.WriteString("model", version.ModelHash);
        json.WriteNumber("generator", version.Generator);
        if (version.Describes is not null)
        {
            json.WriteString("describes", version.Describes);
        }

        json.WriteString("at", version.GeneratedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("text", version.TextSha256);
        json.WriteEndObject();
    }

    /// <summary>Reads an index as data that is not trusted: anything that is not exactly what was written is left out, and an index that does not parse is an empty one.</summary>
    internal static DiagramIndex Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("diagrams", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return DiagramIndex.Empty;
            }

            var entries = new List<IndexEntry>();
            foreach (var item in list.EnumerateArray())
            {
                if (entries.Count == MaxEntries)
                {
                    break;
                }

                if (TryEntry(item) is { } entry && entries.All(e => e.Diagram != entry.Diagram))
                {
                    entries.Add(entry);
                }
            }

            return new DiagramIndex(entries);
        }
        catch (JsonException)
        {
            return DiagramIndex.Empty;
        }
    }

    private static IndexEntry? TryEntry(JsonElement item)
    {
        try
        {
            if (item.ValueKind != JsonValueKind.Object
                || !Enum.TryParse<DiagramKind>(Text(item, "kind"), ignoreCase: false, out var kind)
                || !Enum.IsDefined(kind)
                || Text(item, "subject") is not { } subject)
            {
                return null;
            }

            var diagram = new DiagramRef(kind, subject);
            if (!item.TryGetProperty("current", out var current) || TryVersion(current) is not { } now)
            {
                return null;
            }

            IndexVersion? previous = null;
            if (item.TryGetProperty("previous", out var prev) && (previous = TryVersion(prev)) is null)
            {
                return null;
            }

            IndexExport? export = null;
            if (item.TryGetProperty("export", out var exported))
            {
                if (exported.ValueKind != JsonValueKind.Object || Text(exported, "path") is not { } path || !RepositoryPaths.IsSafeRelative(path)
                    || Text(exported, "file") is not { } file || !ContentHash.IsSha256Hex(file)
                    || Text(exported, "text") is not { } text || !ContentHash.IsSha256Hex(text))
                {
                    return null;
                }

                export = new IndexExport(path, file, text);
            }

            return new IndexEntry(diagram, now, previous, export);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IndexVersion? TryVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Text(element, "model") is not { } model || !ContentHash.IsSha256Hex(model)
            || !element.TryGetProperty("generator", out var generator) || !generator.TryGetInt32(out var version) || version < 1
            || Text(element, "at") is not { } at || !DateTimeOffset.TryParseExact(at, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var generatedAt)
            || Text(element, "text") is not { } text || !ContentHash.IsSha256Hex(text))
        {
            return null;
        }

        string? describes = null;
        if (element.TryGetProperty("describes", out _) && ((describes = Text(element, "describes")) is null || !TraceRef.IsFullCommitId(describes)))
        {
            return null;
        }

        return new IndexVersion(model, version, describes, generatedAt, text);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A name that is safe as a file or folder name: letters, digits, <c>_</c> and <c>-</c>; anything else is replaced and a fingerprint of the original is added, so two names never share one.</summary>
internal static class SafeName
{
    private const int MaxLength = 48;

    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            builder.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '-');
        }

        var clean = builder.ToString().Trim('-');
        return clean.Length > 0 && string.Equals(clean, name, StringComparison.Ordinal)
            ? clean
            : (clean.Length == 0 ? "x" : clean) + "-" + ContentHash.Sha256Hex(name)[..8];
    }
}
