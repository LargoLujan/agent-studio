using AgentStudio.Application.Consistency;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Memory.Consistency;

/// <summary>
/// The Markdown files of a copy that the document checks read (docs/20 D-28): at most <see cref="MaxFiles"/> files and <see cref="MaxFileBytes"/> each, and a total budget, read through
/// the copy (<see cref="IRepositorySnapshot.OpenRead"/>), which refuses a link and a file over the limit. The folder where diagrams are exported
/// is skipped: what Agent Studio generated is not treated as written by the person.
/// </summary>
internal static class MarkdownFiles
{
    /// <summary>The most Markdown files that are read (docs/20 D-28).</summary>
    public const int MaxFiles = 5_000;

    /// <summary>The largest Markdown file that is read, in bytes.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>The most bytes read across every file: a repository of thousands of files of the largest size is not read in full.</summary>
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>Where diagrams are exported by default (<c>DiagramExportRequest.DefaultFolder</c>) and where Phase 16 exports its documents.</summary>
    public const string GeneratedFolder = "docs/generado/";

    /// <summary>The Markdown files of the copy in the order of their paths, as (path, text); one that cannot be read is left out.</summary>
    public static IEnumerable<(string Path, string Text)> Read(IRepositorySnapshot snapshot, IReadOnlySet<string> exportedFolders, Func<string, bool> wanted)
    {
        var files = 0;
        long total = 0;
        foreach (var path in snapshot.Paths)
        {
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.StartsWith(GeneratedFolder, StringComparison.OrdinalIgnoreCase)
                || exportedFolders.Contains(FolderOf(path)) || !wanted(path))
            {
                continue;
            }

            if (files == MaxFiles || total >= MaxTotalBytes)
            {
                yield break;
            }

            files++;
            byte[] bytes;
            try
            {
                using var stream = snapshot.OpenRead(path, MaxFileBytes);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            total += bytes.Length;
            yield return (path, MarkdownScanner.Decode(bytes));
        }
    }

    public static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}

/// <summary>The folders the diagrams of the project were exported to from this computer (the index of the cache), which the document checks skip. Never read from the repository.</summary>
internal static class ExportedFolders
{
    public static async Task<IReadOnlySet<string>> LoadAsync(DiagramCacheStore? store, string projectId, CancellationToken ct)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (store is null)
        {
            return folders;
        }

        var index = await store.LoadAsync(projectId, ct).ConfigureAwait(false);
        foreach (var entry in index.Entries)
        {
            if (entry.Export is { } export)
            {
                folders.Add(MarkdownFiles.FolderOf(export.Path));
            }
        }

        return folders;
    }
}

/// <summary>
/// <c>doc.link-broken</c> (docs/20 D-27 and D-28): a link or an image of a Markdown file to a path that does not exist in the commit. It skips the links with a scheme
/// (<c>https:</c>, <c>mailto:</c>: there is no network), the ones that are only an anchor and the anchors of the rest (the anchors are Phase 16). The destination is
/// resolved from the folder of the file, inside the copy: one that leaves the repository is a finding. A link that only fails by case is said so, because on GitHub it is broken.
/// </summary>
public sealed class LinkBrokenCheck : IConsistencyCheck
{
    private readonly DiagramCacheStore? _store;

    internal LinkBrokenCheck(DiagramCacheStore? store)
    {
        _store = store;
    }

    /// <summary>The check without any folder to skip but the default one.</summary>
    public LinkBrokenCheck()
        : this(null)
    {
    }

    public string Code => ConsistencyCodes.LinkBroken;

    public async Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var exported = await ExportedFolders.LoadAsync(_store, scope.Project.ProjectId, ct).ConfigureAwait(false);
        var files = SnapshotFiles.Of(scope.Snapshot);
        var findings = new List<ConsistencyFinding>();
        foreach (var (path, text) in MarkdownFiles.Read(scope.Snapshot, exported, _ => true))
        {
            ct.ThrowIfCancellationRequested();
            var (links, _) = MarkdownScanner.Scan(text);
            foreach (var link in links)
            {
                if (Evaluate(link.Destination, path, files) is not { } problem)
                {
                    continue;
                }

                findings.Add(new ConsistencyFinding(ConsistencyCodes.LinkBroken, path, link.Line, problem));
                if (findings.Count > ConsistencyReport.MaxFindings)
                {
                    return findings;
                }
            }
        }

        return findings;
    }

    /// <summary>The facts of the problem of a link, or null when the link is fine or is not one to check.</summary>
    internal static IReadOnlyList<DoctorFact>? Evaluate(string written, string markdownPath, SnapshotFiles files)
    {
        var destination = written.Trim();
        if (destination.Length == 0 || UntrustedText.HasHidden(destination))
        {
            return null;
        }

        var cut = destination.IndexOfAny(['#', '?']);
        if (cut == 0)
        {
            return null;
        }

        var target = cut < 0 ? destination : destination[..cut];
        var drive = false;
        if (target.StartsWith("//", StringComparison.Ordinal) || HasScheme(target, out drive))
        {
            return drive ? Problem(written, "outside") : null;
        }

        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            // Not an escape: the text is read as it is written.
        }

        var segments = new List<string>();
        if (!target.StartsWith('/'))
        {
            segments.AddRange(MarkdownFiles.FolderOf(markdownPath).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in target.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return Problem(written, "outside");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        var resolved = string.Join('/', segments);
        var folderOnly = target.EndsWith('/');
        if (folderOnly ? files.FolderExists(resolved) : files.Exists(resolved))
        {
            return null;
        }

        return files.ExistsIgnoringCase(resolved) is { } real ? Problem(written, "case", real) : Problem(written, "missing");
    }

    private static List<DoctorFact> Problem(string written, string reason, string? found = null)
    {
        var facts = new List<DoctorFact> { DoctorFact.Of("target", written), DoctorFact.Of("reason", reason) };
        if (found is not null)
        {
            facts.Add(DoctorFact.Of("found", found));
        }

        return facts;
    }

    /// <summary>
    /// Whether the destination starts with a scheme (<c>https:</c>, <c>mailto:</c>): letters, digits, <c>+</c>, <c>-</c> and <c>.</c> up to a colon that comes before any slash. A single
    /// letter is a drive of Windows (<c>C:/…</c>), which no repository can mean: <paramref name="drive"/> is true and the link is a finding.
    /// </summary>
    private static bool HasScheme(string target, out bool drive)
    {
        drive = false;
        var colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !char.IsAsciiLetter(target[0]))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(target[i]) || target[i] is '+' or '-' or '.'))
            {
                return false;
            }
        }

        drive = colon == 1;
        return true;
    }
}

/// <summary>
/// <c>doc.adr-cites-missing</c> (docs/20 D-27 and D-28): an ADR (a file <c>NNNN-*.md</c> under a folder called <c>adr</c>) that names, between backticks, a path of the
/// repository that does not exist in the commit. What looks like a path has a <c>/</c> or a <c>\</c>, no spaces and ends in an extension or in <c>/</c>; a pattern with <c>*</c> is
/// checked by its fixed prefix; what carries <c>&lt;</c>, <c>{</c> or <c>%</c> (a template, a variable) is skipped. «Borrado» is «no existe en el commit»: the history is not read.
/// </summary>
public sealed class AdrCitesMissingCheck : IConsistencyCheck
{
    private const int MaxCitationLength = 200;

    private readonly DiagramCacheStore? _store;

    internal AdrCitesMissingCheck(DiagramCacheStore? store)
    {
        _store = store;
    }

    /// <summary>The check without any folder to skip but the default one.</summary>
    public AdrCitesMissingCheck()
        : this(null)
    {
    }

    public string Code => ConsistencyCodes.AdrCitesMissing;

    public async Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var exported = await ExportedFolders.LoadAsync(_store, scope.Project.ProjectId, ct).ConfigureAwait(false);
        var files = SnapshotFiles.Of(scope.Snapshot);
        var findings = new List<ConsistencyFinding>();
        foreach (var (path, text) in MarkdownFiles.Read(scope.Snapshot, exported, IsAdr))
        {
            ct.ThrowIfCancellationRequested();
            var (_, code) = MarkdownScanner.Scan(text);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var span in code)
            {
                if (Citation(span.Text) is not { } cited || !seen.Add(cited) || Exists(cited, files))
                {
                    continue;
                }

                findings.Add(new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, path, span.Line, [DoctorFact.Of("cited", span.Text.Trim())]));
                if (findings.Count > ConsistencyReport.MaxFindings)
                {
                    return findings;
                }
            }
        }

        return findings;
    }

    /// <summary>Whether a path of the copy is an ADR: <c>NNNN-*.md</c> (four digits and a dash) in a folder called <c>adr</c>, at any depth.</summary>
    internal static bool IsAdr(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 2 || !segments[..^1].Any(s => s.Equals("adr", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var name = segments[^1];
        return name.Length > 5 && name[..4].All(char.IsAsciiDigit) && name[4] == '-' && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path a span of code cites, as a path of the repository (<c>/</c>, no leading <c>./</c> or <c>/</c>), or null when it does not look like one.</summary>
    internal static string? Citation(string span)
    {
        var text = span.Trim();
        if (text.Length is 0 or > MaxCitationLength || text.Contains(' ') || text.IndexOfAny(['<', '{', '%', ':', '~', '"', '\'']) >= 0
            || UntrustedText.HasHidden(text) || (!text.Contains('/') && !text.Contains('\\')))
        {
            return null;
        }

        text = text.Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        text = text.TrimStart('/');
        if (text.Length == 0 || text.StartsWith("../", StringComparison.Ordinal) || text.Contains("/../", StringComparison.Ordinal) || text.EndsWith("/..", StringComparison.Ordinal))
        {
            return null;
        }

        if (text.EndsWith('/'))
        {
            return text;
        }

        var name = text[(text.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot - 1 is >= 1 and <= 8 && name[(dot + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c == '*') ? text : null;
    }

    private static bool Exists(string cited, SnapshotFiles files)
    {
        var star = cited.IndexOf('*', StringComparison.Ordinal);
        if (star >= 0)
        {
            // A pattern is checked by its fixed prefix; with no prefix there is nothing to check.
            var prefix = cited[..star];
            return prefix.Length == 0 || files.AnyStartsWith(prefix);
        }

        var path = cited.TrimEnd('/');
        return (cited.EndsWith('/') ? files.FolderExists(path) : files.Exists(path)) || files.ExistsIgnoringCase(path) is not null;
    }
}
