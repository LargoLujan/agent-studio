using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Application.Semantic;

// Phase 12 (docs/17 D-06; ADR-0047): what the planner's MCP tool search_project_history reads. Like the skills snapshot
// (docs/14 D-30, ADR-0024), the server never opens a database nor the index: Agent Studio writes the trusted fragments of
// the project to a file of the run, and the server answers from it. The server does not load the model either (ADR-0024
// keeps its surface): it ranks by words and, when the fragments carry vectors, it may widen from its best word hits.

/// <summary>The names and limits of the tool <c>search_project_history</c> of the local MCP server (docs/17 D-06).</summary>
public static class ProjectHistoryNames
{
    /// <summary>The name of the tool, in the server of Agent Studio (<c>mcp__agentstudio__search_project_history</c>).</summary>
    public const string Tool = "search_project_history";

    /// <summary>The argument that gives the server the absolute path of the history snapshot.</summary>
    public const string SnapshotArgument = "--history";

    /// <summary>The most results one call returns (docs/17 D-06: <c>top ≤ 10</c>).</summary>
    public const int MaxTop = 10;

    /// <summary>The results of a call that does not say how many.</summary>
    public const int DefaultTop = 5;

    /// <summary>The longest query the tool takes.</summary>
    public const int MaxQueryLength = 500;

    /// <summary>The most fragments a snapshot carries (the newest).</summary>
    public const int MaxFragments = 2000;
}

/// <summary>
/// One fragment as the snapshot carries it: its id (<see cref="SemanticFragment.Id"/>), a label of what it is in Spanish,
/// the time of its source, its text and, when the index has one of the snapshot's model, its vector (L2-normalised).
/// </summary>
public sealed record HistoryFragment(string Id, string Label, DateTimeOffset At, string Text, float[]? Vector = null);

/// <summary>
/// The trusted history of a project as the MCP server sees it (docs/17 D-06): when it was taken, the fingerprint and
/// dimensions of the model of its vectors (null and 0 when none has one) and its fragments, newest first.
/// </summary>
public sealed record ProjectHistorySnapshot(DateTimeOffset At, string? ModelFingerprint, int Dimensions, IReadOnlyList<HistoryFragment> Fragments);

/// <summary>
/// The file format of a <see cref="ProjectHistorySnapshot"/>, shared by its writer (Agent Studio, with <c>CreateNew</c> in
/// the folder of the run) and its reader (the MCP server): one JSON object with <c>schema</c> set to <see cref="Schema"/>,
/// names in camel case and each vector as the Base64 of its float32 values in little-endian order. Reading is strict:
/// anything that is not a complete snapshot of this format is an <see cref="InvalidDataException"/>.
/// </summary>
public static class ProjectHistorySnapshotJson
{
    /// <summary>The format and its version.</summary>
    public const string Schema = "agentstudio-history-snapshot/1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <exception cref="ArgumentException">The snapshot is not one this format can carry (see <see cref="Read"/>).</exception>
    public static string Write(ProjectHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Problem(snapshot) is { } problem)
        {
            throw new ArgumentException(problem, nameof(snapshot));
        }

        var fragments = snapshot.Fragments
            .Select(f => new FragmentDto(f.Id, f.Label, f.At, f.Text, f.Vector is null ? null : Encode(f.Vector)))
            .ToList();
        return JsonSerializer.Serialize(new FileDto(Schema, snapshot.At, snapshot.ModelFingerprint, snapshot.Dimensions, fragments), Options);
    }

    /// <exception cref="InvalidDataException">The text is not a complete snapshot of this format.</exception>
    public static ProjectHistorySnapshot Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The history snapshot is not valid JSON.", ex);
        }

        if (file is null || !string.Equals(file.Schema, Schema, StringComparison.Ordinal) || file.Fragments is null)
        {
            throw new InvalidDataException($"The history snapshot is not of the format {Schema}.");
        }

        var fragments = new List<HistoryFragment>(file.Fragments.Count);
        foreach (var fragment in file.Fragments)
        {
            if (fragment is null)
            {
                throw new InvalidDataException("The history snapshot has an empty fragment.");
            }

            float[]? vector = null;
            if (fragment.Vector is not null)
            {
                try
                {
                    vector = Decode(fragment.Vector);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException($"The vector of fragment '{fragment.Id}' is not Base64.", ex);
                }
            }

            fragments.Add(new HistoryFragment(fragment.Id!, fragment.Label!, fragment.At, fragment.Text!, vector));
        }

        var snapshot = new ProjectHistorySnapshot(file.At, file.ModelFingerprint, file.Dimensions, fragments);
        return Problem(snapshot) is { } problem ? throw new InvalidDataException(problem) : snapshot;
    }

    /// <summary>What makes <paramref name="snapshot"/> not one of this format, in English; null when it is one.</summary>
    private static string? Problem(ProjectHistorySnapshot snapshot)
    {
        if (snapshot.Fragments is null)
        {
            return "The history snapshot has no list of fragments.";
        }

        if (snapshot.Fragments.Count > ProjectHistoryNames.MaxFragments)
        {
            return $"The history snapshot has {snapshot.Fragments.Count} fragments; it carries at most {ProjectHistoryNames.MaxFragments}.";
        }

        var withVectors = snapshot.Fragments.Any(f => f?.Vector is not null);
        if (withVectors && (!ContentHash.IsSha256Hex(snapshot.ModelFingerprint) || snapshot.Dimensions <= 0))
        {
            return "A history snapshot with vectors names the fingerprint and the dimensions of their model.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in snapshot.Fragments)
        {
            if (fragment is null || string.IsNullOrWhiteSpace(fragment.Id) || string.IsNullOrWhiteSpace(fragment.Label) || string.IsNullOrWhiteSpace(fragment.Text))
            {
                return "A fragment of the history snapshot has no id, label or text.";
            }

            if (!ids.Add(fragment.Id))
            {
                return $"Fragment '{fragment.Id}' appears twice in the history snapshot.";
            }

            if (fragment.Text.Length > SemanticFragment.MaxCharacters)
            {
                return $"Fragment '{fragment.Id}' has more than {SemanticFragment.MaxCharacters} characters.";
            }

            if (fragment.Vector is { } vector && vector.Length != snapshot.Dimensions)
            {
                return $"The vector of fragment '{fragment.Id}' has {vector.Length} dimensions, not {snapshot.Dimensions}.";
            }
        }

        return null;
    }

    private static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    private static float[] Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new FormatException("A vector is a whole number of float32 values.");
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    private sealed record FileDto(string? Schema, DateTimeOffset At, string? ModelFingerprint, int Dimensions, IReadOnlyList<FragmentDto?>? Fragments);

    private sealed record FragmentDto(string? Id, string? Label, DateTimeOffset At, string? Text, string? Vector);
}
