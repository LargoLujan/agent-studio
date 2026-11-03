using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Where the anchors of the decision logs live: a folder outside every project (ADR-0010, addendum). The composition
/// root decides it; the app keeps it with its other machine-local folders.
/// </summary>
public sealed record DecisionLogAnchorOptions(string Directory)
{
    /// <summary><c>%LOCALAPPDATA%\AgentStudio\anchors</c>, next to the other machine-local folders of the app.</summary>
    public static DecisionLogAnchorOptions ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "anchors"));
}

/// <summary>
/// The head of each project's decision log, outside the project, in <c>&lt;folder&gt;\&lt;key&gt;.json</c>: the key is
/// the SHA-256 of the final path of the project root in upper case (<see cref="ProjectStorage.FinalRootPath"/>). The
/// anchor belongs to where the project is, never to the id its own database gives, so whoever edits that database can
/// change the id but cannot leave the log without its anchor (ADR-0010, addenda of 2026-10-16 and 2026-10-18).
/// <list type="bullet">
/// <item>The file records the project id and the location it anchors. An anchor of the location with another project
/// id is a <see cref="DecisionLogAnchorMismatchException"/>: the id was changed, or the folder holds the database of
/// another project. Any other file that is not the anchor of the location (damaged, of another format or location) is an
/// <see cref="InvalidDataException"/>. Neither is ever "no anchor": the recorder then refuses to trust the log.</item>
/// <item>A write goes to a new temporary file in the same folder, flushed to disk, and then replaces the anchor with a
/// rename: a reader finds the old anchor or the new one, never half of one.</item>
/// <item>An anchor written before 2026-10-18 is named by the project id (<c>&lt;projectId&gt;.json</c>, format 1). It
/// is read only when the location has no anchor, and only as the anchor of that id, and reading it changes nothing. It
/// moves to the location's name (written there, then deleted) only once the log verified against it
/// (<see cref="ConfirmAsync"/>, ADR-0010, addendum P-04): a copy of the project whose log does not verify never takes
/// it, and a later copy or move never finds it stale. A move cut short between its two steps ends at a later
/// confirmation, when the location holds the same anchor.</item>
/// <item>The project id must be a plain file name (letters, digits, '-' and '_'), so that old name never points elsewhere.</item>
/// </list>
/// The limit is the same user: a process running as that user can rewrite the anchor too. Only process isolation
/// (Phase 7) removes it.
/// </summary>
public sealed partial class FileDecisionLogAnchorStore(DecisionLogAnchorOptions options, ProjectDatabases databases) : IDecisionLogAnchorStore
{
    /// <summary>The format of an anchor named by the location of its project.</summary>
    public const string Format = "agent-studio/decision-log-anchor/2";

    /// <summary>The format of an anchor named by the project id, written before 2026-10-18.</summary>
    public const string LegacyFormat = "agent-studio/decision-log-anchor/1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<DecisionLogAnchor?> ReadAsync(string projectId, CancellationToken cancellationToken)
    {
        EnsurePlainName(projectId);
        var location = LocationOf(projectId);
        var path = PathOf(location);
        if (await ReadTextAsync(path, cancellationToken).ConfigureAwait(false) is { } text)
        {
            return Parse(text, projectId, location, path);
        }

        // Left where it is: it moves only once the log verified against it (ConfirmAsync).
        var legacy = LegacyPathOf(projectId);
        return await ReadTextAsync(legacy, cancellationToken).ConfigureAwait(false) is { } legacyText
            ? ParseLegacy(legacyText, projectId, legacy)
            : null;
    }

    /// <summary>
    /// Moves the anchor named by the project id to the location's name, now that the log verified against it: written
    /// there, then deleted. Only when the location has no anchor and the old file still holds <paramref name="anchor"/>;
    /// anything else named by the id is left where it is. If the location already holds that same anchor (a move cut
    /// short after its write: the process died, or the delete failed), the old file is deleted then, as best effort.
    /// </summary>
    /// <exception cref="IOException">The anchor could not be moved; the old file is where it was.</exception>
    public async Task ConfirmAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        EnsurePlainName(projectId);
        var legacy = LegacyPathOf(projectId);
        if (!File.Exists(legacy))
        {
            return;
        }

        var location = LocationOf(projectId);
        var path = PathOf(location);
        if (File.Exists(path))
        {
            try
            {
                if (await HoldsAsync(path, text => Parse(text, projectId, location, path), anchor, cancellationToken).ConfigureAwait(false)
                    && await HoldsAsync(legacy, text => ParseLegacy(text, projectId, legacy), anchor, cancellationToken).ConfigureAwait(false))
                {
                    TryDelete(legacy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the old file is never read for a location that has an anchor, and the next confirmation tries again.
            }

            return;
        }

        if (await HoldsAsync(legacy, text => ParseLegacy(text, projectId, legacy), anchor, cancellationToken).ConfigureAwait(false))
        {
            await WriteFileAsync(path, projectId, location, anchor, cancellationToken).ConfigureAwait(false);
            TryDelete(legacy);
        }
    }

    public async Task WriteAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        EnsurePlainName(projectId);
        var location = LocationOf(projectId);
        await WriteFileAsync(PathOf(location), projectId, location, anchor, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsurePlainName(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!PlainName().IsMatch(projectId))
        {
            throw new ArgumentException($"The project id '{projectId}' is not a plain file name, so it cannot name an anchor.", nameof(projectId));
        }
    }

    /// <summary>Where the open project <paramref name="projectId"/> really is: the final path of its root.</summary>
    private string LocationOf(string projectId) => databases.Get(projectId).FinalRootPath;

    private string Folder => Path.GetFullPath(options.Directory);

    private string PathOf(string location) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(location.ToUpperInvariant()))) + ".json");

    private string LegacyPathOf(string projectId) => Path.Combine(Folder, projectId + ".json");

    private static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> holds <paramref name="anchor"/>: false when it is missing or
    /// <paramref name="parse"/> refuses it. An error reading it is the caller's.
    /// </summary>
    private static async Task<bool> HoldsAsync(string path, Func<string, DecisionLogAnchor> parse, DecisionLogAnchor anchor, CancellationToken cancellationToken)
    {
        if (await ReadTextAsync(path, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return false;
        }

        try
        {
            return parse(text) == anchor;
        }
        catch (Exception ex) when (ex is InvalidDataException or DecisionLogAnchorMismatchException)
        {
            return false;
        }
    }

    private static async Task WriteFileAsync(string path, string projectId, string location, DecisionLogAnchor anchor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                await file.WriteAsync(Utf8NoBom.GetBytes(ToJson(projectId, location, anchor)), cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            // Same folder, same volume: replacing is a rename, which happens entirely or not at all.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string ToJson(string projectId, string location, DecisionLogAnchor anchor)
    {
        var root = new JsonObject
        {
            ["format"] = Format,
            ["projectId"] = projectId,
            ["location"] = location,
            ["entryCount"] = anchor.EntryCount,
            ["headHash"] = anchor.HeadHash,
        };
        if (anchor.NextHash is not null)
        {
            root["nextHash"] = anchor.NextHash;
        }

        return root.ToJsonString(Indented);
    }

    /// <summary>The anchor of <paramref name="location"/>, which must be that of <paramref name="projectId"/>.</summary>
    private static DecisionLogAnchor Parse(string text, string projectId, string location, string path)
    {
        InvalidDataException Invalid(string why, Exception? inner = null) => new($"The anchor '{path}' is not the anchor of the location '{location}': {why}.", inner);

        using var json = ParseObject(text, Invalid);
        var root = json.RootElement;
        if (!string.Equals(Text(root, "format"), Format, StringComparison.Ordinal))
        {
            throw Invalid($"its format is not {Format}");
        }

        var anchoredLocation = Text(root, "location") ?? throw Invalid("it names no location");
        if (!string.Equals(anchoredLocation, location, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid($"it anchors '{anchoredLocation}'");
        }

        var anchoredProject = Text(root, "projectId") ?? throw Invalid("it names no project");
        var anchor = Head(root, Invalid);
        if (!string.Equals(anchoredProject, projectId, StringComparison.Ordinal))
        {
            throw new DecisionLogAnchorMismatchException(
                $"The anchor '{path}' of '{location}' belongs to project '{anchoredProject}' ({anchor.EntryCount} entries), but the database there is that of project '{projectId}': the project id was changed, or the folder holds the database of another project.",
                anchoredProject,
                anchor.EntryCount);
        }

        return anchor;
    }

    /// <summary>An anchor named by <paramref name="projectId"/>, as Agent Studio wrote them before 2026-10-18.</summary>
    private static DecisionLogAnchor ParseLegacy(string text, string projectId, string path)
    {
        InvalidDataException Invalid(string why, Exception? inner = null) => new($"The anchor '{path}' is not the anchor of project '{projectId}': {why}.", inner);

        using var json = ParseObject(text, Invalid);
        var root = json.RootElement;
        if (!string.Equals(Text(root, "format"), LegacyFormat, StringComparison.Ordinal))
        {
            throw Invalid($"its format is not {LegacyFormat}");
        }

        if (!string.Equals(Text(root, "projectId"), projectId, StringComparison.Ordinal))
        {
            throw Invalid($"it anchors project '{Text(root, "projectId")}'");
        }

        return Head(root, Invalid);
    }

    private static JsonDocument ParseObject(string text, Func<string, Exception?, InvalidDataException> invalid)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw invalid("it is not JSON", ex);
        }

        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            json.Dispose();
            throw invalid("it is not a JSON object", null);
        }

        return json;
    }

    private static DecisionLogAnchor Head(JsonElement root, Func<string, Exception?, InvalidDataException> invalid)
    {
        if (!root.TryGetProperty("entryCount", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var entryCount))
        {
            throw invalid("it has no entry count", null);
        }

        var head = Text(root, "headHash") ?? throw invalid("it has no head hash", null);
        return new DecisionLogAnchor(entryCount, head, Text(root, "nextHash"));
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is never read; a legacy anchor left behind is never read once the location has one.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex PlainName();
}
