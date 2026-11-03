namespace AgentStudio.Domain.Orchestration;

/// <summary>
/// Rules for the write scope of a task: the folders and files it may change, relative to the repository root. An
/// entry ending in <c>/</c> is a folder (everything below it); any other entry is exactly one file. Comparison is
/// case-insensitive, as on the Windows file systems Agent Studio runs on.
/// </summary>
/// <remarks>
/// Overlap is deliberately conservative: an entry without a trailing slash might be a folder the planner forgot to
/// close, so it is compared as a path prefix too. A false overlap makes the validator ask for a clearer plan; a
/// missed one would let two parallel tasks edit the same files.
/// </remarks>
public static class WriteScope
{
    private const string InvalidCharacters = "<>:\"|?*";
    private static readonly string[] ProtectedFolders = [".git", ".agentstudio"];

    /// <summary>
    /// Checks one entry and returns it in canonical form: forward slashes, no <c>.</c> segments, no empty segments and
    /// a trailing slash only for folders. Rejects the repository root, absolute paths, <c>..</c>, wildcards,
    /// characters Windows does not allow, segments Windows silently rewrites (trailing dot or space) and the
    /// <c>.git</c> and <c>.agentstudio</c> folders wherever they appear.
    /// </summary>
    public static bool TryNormalize(string? entry, out string normalized, out string? problem)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            problem = "The entry is empty.";
            return false;
        }

        var text = entry.Trim().Replace('\\', '/');
        if (text.StartsWith('/') || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':'))
        {
            problem = "Absolute paths are not allowed.";
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsControl(c) || InvalidCharacters.Contains(c, StringComparison.Ordinal))
            {
                var shown = char.IsControl(c) ? "a control character" : $"'{c}'";
                problem = $"{shown} is not valid in a path (wildcards and Windows-reserved characters are rejected).";
                return false;
            }
        }

        var segments = new List<string>();
        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                problem = "Parent directory segments ('..') are not allowed.";
                return false;
            }

            var bare = segment.TrimEnd('.', ' ');
            if (ProtectedFolders.Any(folder => string.Equals(bare, folder, StringComparison.OrdinalIgnoreCase)))
            {
                problem = $"'{segment}' is a protected folder: tasks never write to .git or .agentstudio.";
                return false;
            }

            if (bare.Length != segment.Length)
            {
                problem = $"The segment '{segment}' ends with a dot or a space, which Windows silently removes.";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            problem = "The entry names the repository root; list the folders or files the task may change.";
            return false;
        }

        normalized = string.Join('/', segments) + (text.EndsWith('/') ? "/" : string.Empty);
        problem = null;
        return true;
    }

    /// <summary>True if <paramref name="path"/> (a changed file, as git reports it) is inside the scope. Entries that are not valid admit nothing.</summary>
    public static bool Contains(IReadOnlyList<string> scope, string? path)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!TryNormalize(path, out var file, out _))
        {
            return false;
        }

        foreach (var entry in scope)
        {
            if (!TryNormalize(entry, out var normalized, out _))
            {
                continue;
            }

            var admitted = normalized.EndsWith('/')
                ? file.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                : string.Equals(file, normalized, StringComparison.OrdinalIgnoreCase);
            if (admitted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if any entry of <paramref name="a"/> overlaps any entry of <paramref name="b"/>. Entries that are not valid are ignored (the validator reports them).</summary>
    public static bool Overlaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        foreach (var x in a)
        {
            if (!TryNormalize(x, out var left, out _))
            {
                continue;
            }

            foreach (var y in b)
            {
                if (TryNormalize(y, out var right, out _) && EntriesOverlap(left, right))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EntriesOverlap(string left, string right)
    {
        var x = left.TrimEnd('/');
        var y = right.TrimEnd('/');
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase) || IsSegmentPrefix(x, y) || IsSegmentPrefix(y, x);
    }

    private static bool IsSegmentPrefix(string prefix, string path) =>
        path.Length > prefix.Length && path[prefix.Length] == '/' && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
