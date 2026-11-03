using System.Text;
using static System.FormattableString;

namespace AgentStudio.Debate.Arena;

/// <summary>Why a citation did not resolve in the copy of the commit, or <see cref="None"/> when it did.</summary>
public enum CitationProblem
{
    None,

    /// <summary>
    /// Not a path relative to the copy: empty or too long, a root, a drive, a stream or a URI (<c>:</c>), a <c>..</c> or
    /// <c>.</c> segment, an empty segment, a control character or one no file name may have, a segment ending in a dot or
    /// a space, or a name Windows reserves for a device. It is never opened.
    /// </summary>
    InvalidPath,

    /// <summary>Not 1 to <see cref="ContributionLimits.MaxCitedLines"/> lines from line 1 or later.</summary>
    InvalidLines,

    /// <summary>No such file in the commit, or a file where the path needs a folder.</summary>
    NotFound,

    /// <summary>
    /// A junction or a symbolic link on the way to the file, or the file itself. The copy holds only files and folders, so
    /// a link is a sign someone wrote into it; it is never read through.
    /// </summary>
    Link,

    /// <summary>A folder, or something else than a regular file.</summary>
    NotAFile,

    /// <summary>More than <see cref="CitationResolver.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>Not UTF-8 text: bytes that are not UTF-8, or a NUL character.</summary>
    NotText,

    /// <summary>The file has fewer lines than the citation needs.</summary>
    LinesOutOfRange,

    /// <summary>The file, or a folder on the way, could not be examined, opened or read.</summary>
    Unreadable,
}

/// <summary>
/// One citation, resolved in the copy of the commit or not. <see cref="Text"/> is the exact text of the cited lines (each
/// with its line end, as in the copy) when <see cref="Resolved"/>, and null otherwise. <see cref="Detail"/> says what was
/// found, in English and starting with the citation (<c>src/x.cs:200-210: ...</c>): for an unresolved citation it is the
/// fact the judge gets (D-10), and it never quotes the content of a file.
/// </summary>
public sealed record CitationResolution(ContributionCitation Citation, CitationProblem Problem, string? Text, string Detail)
{
    public bool Resolved => Problem == CitationProblem.None;
}

/// <summary>
/// Resolves the citations of a contribution in the copy of the commit the debate is about (docs/15 D-10 and section 7):
/// only the exact lines of a regular UTF-8 file of at most <see cref="MaxFileBytes"/>, inside the copy and reached without
/// any link, are read. Whatever a participant writes, a citation never fails the turn: one that does not resolve comes back
/// with a stable <see cref="CitationProblem"/>, and it is a matter for the judge (a <c>FabricatedResult</c>, say), not a
/// problem of the material. Only a copy that is missing or is itself a link throws: that is the system's failure.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The path is checked first (<see cref="PathProblem"/>), then its full path must fall inside the copy, then every
/// folder on the way and the file are examined without following links. The file is opened with others allowed to read
/// only, its size is checked on the open handle and at most one byte more than the cap is read, and the way is examined
/// again while the file is open, so a folder swapped for a link in between is caught. What a process of the same user
/// could still swap and restore in between is the residual of ADR-0018, as for the dossier package.</item>
/// <item>A hard link is a regular file: through one, the file read is another file the link names. The copy is written
/// by the git layer with regular files only and the participants are read-only, so it stays a residual (the same user
/// could as well copy that file into the copy).</item>
/// <item>The text is the decoded file, cut at the lines: line ends are <c>\n</c>, a <c>\r</c> before one stays part of its
/// line, and a last line without line end counts. Capturing it as evidence (<c>Document</c>, <c>CapturedBySystem</c>, with
/// the commit as <c>SubjectCommit</c>) is the caller's, which knows the debate and the turn.</item>
/// </list>
/// </remarks>
public static class CitationResolver
{
    /// <summary>The largest file a citation reads (D-10).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly char[] Separators = ['/', '\\'];

    private static readonly char[] ForbiddenInNames = ['<', '>', '"', '|', '?', '*'];

    private static readonly HashSet<string> DeviceNames = BuildDeviceNames();

    /// <summary>
    /// Why <paramref name="path"/> is not a path relative to the copy of the commit, or null when it is one (the reason never
    /// repeats the path, so the parser can send it back while the round is sealed): relative, with
    /// <c>/</c> or <c>\</c> between its segments, at most <see cref="ContributionLimits.MaxPathChars"/> characters, without
    /// <c>:</c>, control characters or characters no file name may have, without empty, <c>.</c> or <c>..</c> segments,
    /// without a segment ending in a dot or a space (Windows drops them, so the path would name another file) and without a
    /// name Windows reserves for a device (<c>NUL</c>, <c>CON</c>, <c>COM1</c>...).
    /// </summary>
    public static string? PathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "it is empty";
        }

        if (path.Length > ContributionLimits.MaxPathChars)
        {
            return Invariant($"it has {path.Length} characters; a path takes at most {ContributionLimits.MaxPathChars}");
        }

        if (path.Any(char.IsControl))
        {
            return "it has a control character";
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            return "it has ':', which names a drive, a stream or a URI";
        }

        if (path[0] is '/' or '\\')
        {
            return "it starts at a root";
        }

        foreach (var segment in path.Split(Separators))
        {
            if (segment.Length == 0)
            {
                return "it has an empty segment (two separators in a row, or one at the end)";
            }

            if (segment is "." or "..")
            {
                return $"it has a '{segment}' segment";
            }

            if (segment.IndexOfAny(ForbiddenInNames) >= 0)
            {
                return "it has a character no file name may have (< > \" | ? *)";
            }

            if (segment[^1] is '.' or ' ')
            {
                return "a segment ends in a dot or a space, which Windows drops";
            }

            if (DeviceNames.Contains(segment.Split('.')[0].TrimEnd(' ')))
            {
                return "a segment is a name Windows reserves for a device (NUL, CON, COM1...)";
            }
        }

        return null;
    }

    /// <summary>Why lines <paramref name="startLine"/> to <paramref name="endLine"/> are not a range a citation may take, or null when they are one.</summary>
    public static string? LinesProblem(int startLine, int endLine)
    {
        if (startLine < 1)
        {
            return Invariant($"startLine is {startLine}, but lines are numbered from 1");
        }

        if (endLine < startLine)
        {
            return Invariant($"endLine {endLine} comes before startLine {startLine}");
        }

        var lines = (long)endLine - startLine + 1;
        return lines > ContributionLimits.MaxCitedLines
            ? Invariant($"lines {startLine}-{endLine} are {lines} lines; a citation takes at most {ContributionLimits.MaxCitedLines}")
            : null;
    }

    /// <summary>Resolves one citation in the copy at <paramref name="snapshotRoot"/>.</summary>
    /// <exception cref="InvalidOperationException">The copy does not exist or is itself a link.</exception>
    public static async Task<CitationResolution> ResolveAsync(string snapshotRoot, ContributionCitation citation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(citation);
        var root = Root(snapshotRoot);
        return await ResolveInAsync(root, citation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves each citation on its own, in their order.</summary>
    /// <exception cref="InvalidOperationException">The copy does not exist or is itself a link.</exception>
    public static async Task<IReadOnlyList<CitationResolution>> ResolveAsync(string snapshotRoot, IReadOnlyList<ContributionCitation> citations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(citations);
        var root = Root(snapshotRoot);
        var resolutions = new List<CitationResolution>(citations.Count);
        foreach (var citation in citations)
        {
            ArgumentNullException.ThrowIfNull(citation);
            resolutions.Add(await ResolveInAsync(root, citation, cancellationToken).ConfigureAwait(false));
        }

        return resolutions;
    }

    private static async Task<CitationResolution> ResolveInAsync(string root, ContributionCitation citation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PathProblem(citation.Path) is { } invalid)
        {
            return Unresolved(citation, CitationProblem.InvalidPath, $"not a path relative to the copy of the commit ({invalid})");
        }

        if (LinesProblem(citation.StartLine, citation.EndLine) is { } range)
        {
            return Unresolved(citation, CitationProblem.InvalidLines, range);
        }

        var segments = citation.Path.Split(Separators);
        var full = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Unresolved(citation, CitationProblem.InvalidPath, "not a path relative to the copy of the commit (it falls outside it)");
        }

        if (Examine(root, segments) is { } blocked)
        {
            return Unresolved(citation, blocked.Problem, blocked.Reason);
        }

        byte[] content;
        try
        {
            var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (file.ConfigureAwait(false))
            {
                if (file.Length > MaxFileBytes)
                {
                    return TooLarge(citation, file.Length);
                }

                content = await ReadAtMostAsync(file, MaxFileBytes + 1, cancellationToken).ConfigureAwait(false);

                // Again, now that the file is open: a folder swapped for a link since the first look is caught.
                if (Examine(root, segments) is { } swapped)
                {
                    return Unresolved(citation, swapped.Problem, swapped.Reason);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unresolved(citation, CitationProblem.Unreadable, $"the file could not be opened or read ({ex.GetType().Name})");
        }

        if (content.Length > MaxFileBytes)
        {
            return TooLarge(citation, content.Length);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Unresolved(citation, CitationProblem.NotText, "the file is not UTF-8 text");
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return Unresolved(citation, CitationProblem.NotText, "the file has a NUL character, so it is not text");
        }

        var count = LineCount(text);
        if (citation.EndLine > count)
        {
            return Unresolved(
                citation,
                CitationProblem.LinesOutOfRange,
                Invariant($"the file has {count} {(count == 1 ? "line" : "lines")}, so lines {citation.StartLine}-{citation.EndLine} do not exist in the commit"));
        }

        return new CitationResolution(citation, CitationProblem.None, Slice(text, citation.StartLine, citation.EndLine), citation.Locator + ": the exact lines of the copy of the commit.");
    }

    /// <summary>The full path of the copy, which must exist and must not be a link.</summary>
    private static string Root(string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotRoot));
        var folder = new DirectoryInfo(root);
        if (folder.LinkTarget is not null || (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException($"The copy of the commit '{root}' is a link (a junction or a symbolic link): no citation is read through one.");
        }

        if (!folder.Exists)
        {
            throw new InvalidOperationException($"The copy of the commit '{root}' does not exist: extract it again from the commit before resolving citations.");
        }

        return root;
    }

    /// <summary>
    /// Looks at every folder on the way and at the file, from the root of the copy down, without following any link (the
    /// attributes of a link are its own). Null when the way holds folders only and ends at a regular file.
    /// </summary>
    private static (CitationProblem Problem, string Reason)? Examine(string root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var shown = string.Join('/', segments[..(i + 1)]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (CitationProblem.NotFound, "the file does not exist in the commit");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (CitationProblem.Unreadable, $"'{shown}' could not be examined ({ex.GetType().Name})");
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return (CitationProblem.Link, $"'{shown}' is a link (a junction or a symbolic link), and the copy of the commit holds only files and folders: it is never read through one");
            }

            var last = i == segments.Length - 1;
            if (!last && !attributes.HasFlag(FileAttributes.Directory))
            {
                return (CitationProblem.NotFound, $"the file does not exist in the commit ('{shown}' is a file, not a folder)");
            }

            if (last && attributes.HasFlag(FileAttributes.Directory))
            {
                return (CitationProblem.NotAFile, $"'{shown}' is a folder, not a file");
            }

            if (last && attributes.HasFlag(FileAttributes.Device))
            {
                return (CitationProblem.NotAFile, $"'{shown}' is not a regular file");
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Lines end at <c>\n</c>; a last line without one counts, and an empty text has none.</summary>
    private static int LineCount(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var ends = text.AsSpan().Count('\n');
        return text[^1] == '\n' ? ends : ends + 1;
    }

    /// <summary>Lines <paramref name="startLine"/> to <paramref name="endLine"/> (both there), each with its line end.</summary>
    private static string Slice(string text, int startLine, int endLine)
    {
        var from = 0;
        for (var line = 1; line < startLine; line++)
        {
            from = text.IndexOf('\n', from) + 1;
        }

        var to = from;
        for (var line = startLine; line <= endLine; line++)
        {
            var end = text.IndexOf('\n', to);
            to = end < 0 ? text.Length : end + 1;
        }

        return text[from..to];
    }

    private static CitationResolution TooLarge(ContributionCitation citation, long size) =>
        Unresolved(citation, CitationProblem.TooLarge, Invariant($"the file has {size} bytes; a citation reads files of at most {MaxFileBytes}"));

    private static CitationResolution Unresolved(ContributionCitation citation, CitationProblem problem, string reason) =>
        new(citation, problem, null, $"{citation.Locator}: {reason}.");

    private static HashSet<string> BuildDeviceNames()
    {
        var names = new HashSet<string>(["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"], StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in "0123456789¹²³")
        {
            names.Add("COM" + suffix);
            names.Add("LPT" + suffix);
        }

        return names;
    }
}
