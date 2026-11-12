namespace AgentStudio.Domain.Common;

/// <summary>
/// The shape of a path that names something inside a repository or a copy of a commit (docs/20 D-16 and D-24): relative, in forward
/// slashes and with nothing that leads out of the folder it is read from. Paths that come from a repository are not trusted (ADR-0018):
/// the rule is the same wherever one is accepted, so it lives once.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>The longest path of the model of Auto Graph and of the findings of the checks.</summary>
    public const int MaxLength = 400;

    /// <summary>
    /// Whether <paramref name="path"/> is relative to the root of a repository: not empty and at most <see cref="MaxLength"/> characters;
    /// no control character, no <c>\</c>, no <c>:</c> (a drive, a stream or a device), no <c>?</c>, <c>*</c>, <c>"</c>, <c>&lt;</c>,
    /// <c>&gt;</c> or <c>|</c>; not starting with <c>/</c> (rooted or UNC); and no empty, <c>.</c> or <c>..</c> segment. A <c>%</c> is a legal
    /// character of a file name (<c>50%.md</c>) and the file system does not decode it, so it is not refused here.
    /// A segment that ends in a space or a dot is refused too: Windows trims them, so it would name another file.
    /// </summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength)
        {
            return false;
        }

        if (UntrustedText.HasHidden(path))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c is '\\' or ':' or '?' or '*' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or ".." || segment[^1] is ' ' or '.' || IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a segment names a device of Windows, which opening would not read a file from: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c> to
    /// <c>COM9</c>, <c>LPT1</c> to <c>LPT9</c> (also with the superscript digits <c>¹</c>, <c>²</c> and <c>³</c>), <c>CONIN$</c> and <c>CONOUT$</c>, in any case, and also
    /// with an extension (<c>NUL.cs</c>) or with spaces before the dot (<c>aux .csproj</c>): Windows ignores all of that.
    /// </summary>
    public static bool IsReservedDeviceName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (stem.Length == 0)
        {
            return false;
        }

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    /// <summary>The same rule for a path that is relative to the root of a project or a folder and may be empty (the root itself).</summary>
    public static bool IsSafeRelativeOrEmpty(string? path) => path is "" || IsSafeRelative(path);
}
