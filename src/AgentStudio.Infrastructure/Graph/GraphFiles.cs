using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;

namespace AgentStudio.Infrastructure.Graph;

/// <summary>Where Auto Graph keeps its derived data: <c>&lt;root&gt;\&lt;project&gt;\</c>, the root being <c>%LOCALAPPDATA%\AgentStudio\graph</c> (docs/20 section 5). Nothing here is a source of truth: all of it is deleted and calculated again.</summary>
internal static partial class GraphFolders
{
    /// <summary>The folder of a project. A project id that is not plain (letters, digits and dashes) is named by its hash: it never makes a path.</summary>
    public static string Of(string root, ProjectContext project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(project);
        var name = PlainName().IsMatch(project.ProjectId) ? project.ProjectId : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.ProjectId)));
        return Path.Combine(Path.GetFullPath(root), name);
    }

    /// <summary>Writes the file whole or not at all: a temporary file in the same folder, then a move that replaces.</summary>
    public static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
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

    /// <summary>The bytes of a file of at most <paramref name="maxBytes"/> that is not a link, or null when it is missing, too long or cannot be read.</summary>
    public static async Task<byte[]?> ReadCappedAsync(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}

/// <summary>
/// The analyses of the commits on disk (docs/20 D-19): <c>&lt;project&gt;\&lt;commit&gt;.json</c>. It is read back as data: a strict schema, the caps of the
/// model, and a file that does not read back is not an analysis (it is calculated again; the file is left as it is).
/// </summary>
public sealed class CommitAnalysisFileCache(string root) : ICommitAnalysisCache
{
    /// <summary>The version of the file. A change of what the file holds raises it, and the older files are calculated again.</summary>
    public const int FormatVersion = 1;

    private const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public async Task<CachedAnalysis?> LoadAsync(ProjectContext project, string commit, CancellationToken ct)
    {
        var bytes = await GraphFolders.ReadCappedAsync(FilePath(project, commit), MaxFileBytes, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object || top.EnumerateObject().Count() != 4)
            {
                return null;
            }

            if (top.GetProperty("v").GetInt32() != FormatVersion)
            {
                return null;
            }

            var build = CodeGraphJson.ParseBuild(top.GetProperty("graph").GetRawText());
            var consistency = top.GetProperty("consistency").Deserialize<ReportDto>(Strict) ?? throw new InvalidDataException("No report.");
            var report = new ConsistencyReport(
                build.Graph.Commit,
                [.. consistency.Findings.Select(f => new ConsistencyFinding(f.Code, f.Path, f.Line, [.. f.Facts.Select(x => new DoctorFact(x.Name, x.Value))]))],
                consistency.NotApplicable,
                consistency.Truncated);
            var analysis = new CommitAnalysis(build.Graph, report, build.Files);
            return string.Equals(analysis.Commit, commit, StringComparison.Ordinal)
                ? new CachedAnalysis(analysis, top.GetProperty("consistencyChecked").GetBoolean())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ProjectContext project, CachedAnalysis cached, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cached);
        var analysis = cached.Analysis;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteBoolean("consistencyChecked", cached.ConsistencyChecked);
            json.WritePropertyName("graph");
            json.WriteRawValue(CodeGraphJson.Serialize(analysis.Graph, analysis.Files));
            json.WritePropertyName("consistency");
            JsonSerializer.Serialize(
                json,
                new ReportDto(
                    [.. analysis.Consistency.Findings.Select(f => new FindingDto(f.Code, f.Path, f.Line, [.. f.Facts.Select(x => new FactDto(x.Name, x.Value))]))],
                    [.. analysis.Consistency.NotApplicable],
                    analysis.Consistency.Truncated),
                Strict);
            json.WriteEndObject();
        }

        await GraphFolders.WriteAtomicAsync(FilePath(project, analysis.Commit), stream.ToArray(), ct).ConfigureAwait(false);
    }

    private string FilePath(ProjectContext project, string commit)
    {
        if (!Domain.Tracing.TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("The commit is a full id.", nameof(commit));
        }

        return Path.Combine(GraphFolders.Of(root, project), commit + ".json");
    }

    private sealed record FactDto(string Name, string Value);

    private sealed record FindingDto(string Code, string? Path, int? Line, List<FactDto> Facts);

    private sealed record ReportDto(List<FindingDto> Findings, List<string> NotApplicable, bool Truncated);
}

/// <summary>
/// The relations an AI proposed, kept apart from the static model (docs/20 D-21): <c>&lt;project&gt;\inferred.json</c>. Derived data: the log has the ends and
/// the kind of each accepted relation, and the evidence the text of the agent; losing the file loses only the reasons, and the proposal can be asked again.
/// </summary>
public sealed class GraphInferenceFileStore(string root) : IGraphInferenceStore
{
    /// <summary>The most relations kept for a project: the oldest go first.</summary>
    public const int MaxRelations = 1_000;

    private const long MaxFileBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 8,
    };

    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<IReadOnlyList<GraphEdge>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync(project, ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task AddAsync(ProjectContext project, IReadOnlyList<GraphEdge> edges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Any(e => e is null || e.Origin != GraphEdgeOrigin.Inferred || e.Inference is null))
        {
            throw new ArgumentException("Only relations proposed by an AI are kept here.", nameof(edges));
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await ReadAsync(project, ct).ConfigureAwait(false)).ToList();
            foreach (var edge in edges)
            {
                if (!all.Any(e => e.From == edge.From && e.To == edge.To && e.Kind == edge.Kind))
                {
                    all.Add(edge);
                }
            }

            var kept = all.Count > MaxRelations ? all.Skip(all.Count - MaxRelations).ToList() : all;
            var dto = kept.Select(e => new RelationDto(e.From, e.To, e.Kind, e.Inference!.Agent, e.Inference.Model, e.Inference.At, e.Inference.Reason)).ToList();
            await GraphFolders.WriteAtomicAsync(FilePath(project), JsonSerializer.SerializeToUtf8Bytes(dto, Strict), ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<IReadOnlyList<GraphEdge>> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        var bytes = await GraphFolders.ReadCappedAsync(FilePath(project), MaxFileBytes, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return [];
        }

        try
        {
            var dto = JsonSerializer.Deserialize<List<RelationDto>>(bytes, Strict) ?? [];
            if (dto.Count > MaxRelations)
            {
                return [];
            }

            return [.. dto.Select(r => new GraphEdge(r.From, r.To, r.Kind, GraphEdgeOrigin.Inferred, new GraphInference(r.Agent, r.Model, r.At, r.Reason)))];
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // A file that does not read back holds no relation: the person asks again.
            return [];
        }
    }

    private string FilePath(ProjectContext project) => Path.Combine(GraphFolders.Of(root, project), "inferred.json");

    private sealed record RelationDto(string From, string To, GraphEdgeKind Kind, string Agent, string Model, DateTimeOffset At, string Reason);
}
