using System.Text.Json;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>What is kept of an answer of GitHub: its <c>ETag</c> and the two values the doctor reads from it, never the body.</summary>
/// <param name="Archived">For the answer about a repository.</param>
/// <param name="CommitSha">For the answer about the commits: the last one that touched the folder, or null when there is none.</param>
public sealed record GitHubCachedAnswer(string ETag, bool? Archived, string? CommitSha);

/// <summary>
/// The answers of GitHub with their <c>ETag</c> (docs/20 D-14), in <c>cache.json</c> of a folder the composition decides (by default
/// <c>%LOCALAPPDATA%\AgentStudio\skills-doctor\</c>). A 304 does not count against the limit, so the next batch asks with <c>If-None-Match</c>.
/// It keeps no body and no token. The file is the person's disk, so it is read as untrusted: at most <see cref="MaxFileBytes"/>, at most
/// <see cref="MaxEntries"/> entries, and an entry that does not pass the checks is left out; a file that cannot be read is an empty cache, never an
/// error. It can be deleted at any time.
/// </summary>
public sealed class GitHubResponseCache
{
    public const int MaxETagLength = 200;

    public const int MaxFileBytes = 1024 * 1024;

    public const int MaxEntries = 2000;

    public const int MaxKeyLength = 700;

    private const string FileName = "cache.json";

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, GitHubCachedAnswer>? _entries;

    public GitHubResponseCache(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _path = Path.Combine(Path.GetFullPath(folder), FileName);
    }

    /// <summary><c>%LOCALAPPDATA%\AgentStudio\skills-doctor</c>.</summary>
    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "skills-doctor");

    /// <summary>The key of a request: its path and query, and whether it went with a token (the answers of a private repository differ).</summary>
    public static string KeyOf(Uri uri, bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return (authenticated ? "t|" : "n|") + uri.PathAndQuery;
    }

    /// <summary>Whether <paramref name="etag"/> can travel in a header: visible ASCII (and no more), as GitHub sends it.</summary>
    public static bool IsValidETag(string? etag) =>
        etag is { Length: > 0 and <= MaxETagLength } && etag.All(c => c is >= ' ' and <= '~');

    public GitHubCachedAnswer? Get(string key)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(key);
        }
    }

    public void Set(string key, GitHubCachedAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (!IsValidKey(key) || !IsValid(answer))
        {
            return;
        }

        lock (_gate)
        {
            var entries = Load();
            if (entries.Count >= MaxEntries && !entries.ContainsKey(key))
            {
                return;
            }

            entries[key] = answer;
            Save(entries);
        }
    }

    private static bool IsValidKey(string key) => key is { Length: > 2 and <= MaxKeyLength } && key.All(c => c is >= ' ' and <= '~');

    private static bool IsValid(GitHubCachedAnswer answer) =>
        IsValidETag(answer.ETag) && (answer.CommitSha is null || GitHubSkillOrigin.IsCommit(answer.CommitSha));

    private Dictionary<string, GitHubCachedAnswer> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new Dictionary<string, GitHubCachedAnswer>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length <= MaxFileBytes && info.LinkTarget is null)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(_path), new JsonDocumentOptions { MaxDepth = 6 });
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("entries", out var list)
                    && list.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in list.EnumerateObject())
                    {
                        if (entries.Count >= MaxEntries)
                        {
                            break;
                        }

                        if (IsValidKey(property.Name) && ReadAnswer(property.Value) is { } answer)
                        {
                            entries[property.Name] = answer;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            entries.Clear();
        }

        _entries = entries;
        return entries;
    }

    private static GitHubCachedAnswer? ReadAnswer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("etag", out var etag) || etag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        bool? archived = null;
        if (element.TryGetProperty("archived", out var a))
        {
            if (a.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            archived = a.GetBoolean();
        }

        string? sha = null;
        if (element.TryGetProperty("sha", out var s))
        {
            if (s.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            sha = s.GetString();
        }

        var answer = new GitHubCachedAnswer(etag.GetString() ?? string.Empty, archived, sha);
        return IsValid(answer) ? answer : null;
    }

    private void Save(Dictionary<string, GitHubCachedAnswer> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteStartObject("entries");
                foreach (var (key, answer) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(key);
                    writer.WriteString("etag", answer.ETag);
                    if (answer.Archived is { } archived)
                    {
                        writer.WriteBoolean("archived", archived);
                    }

                    if (answer.CommitSha is not null)
                    {
                        writer.WriteString("sha", answer.CommitSha);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs the requests it would have saved.
        }
    }
}
