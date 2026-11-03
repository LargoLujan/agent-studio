using System.Text;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// How well a query matches a relative path. Smaller sorts first: fewer <see cref="Gaps"/>, then a match inside the
/// file name, then a shorter path (see <see cref="CompareTo"/>).
/// </summary>
/// <param name="Gaps">
/// Breaks between the matched characters in the most contiguous alignment of the query inside the path;
/// 0 means the query appears as one uninterrupted run.
/// </param>
/// <param name="InFileName">
/// True when an alignment just as contiguous lies entirely inside the file name (the part after the last separator).
/// </param>
/// <param name="PathLength">Length of the path; between equally good matches the shorter path wins.</param>
public readonly record struct PathMatch(int Gaps, bool InFileName, int PathLength) : IComparable<PathMatch>
{
    /// <summary>Orders better matches first.</summary>
    public int CompareTo(PathMatch other)
    {
        var byGaps = Gaps.CompareTo(other.Gaps);
        if (byGaps != 0)
        {
            return byGaps;
        }

        if (InFileName != other.InFileName)
        {
            return InFileName ? -1 : 1;
        }

        return PathLength.CompareTo(other.PathLength);
    }

    public static bool operator <(PathMatch left, PathMatch right) => left.CompareTo(right) < 0;

    public static bool operator >(PathMatch left, PathMatch right) => left.CompareTo(right) > 0;

    public static bool operator <=(PathMatch left, PathMatch right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PathMatch left, PathMatch right) => left.CompareTo(right) >= 0;
}

/// <summary>The best matches of a query, in rank order, and how many paths matched in total.</summary>
public sealed record RankedPaths(IReadOnlyList<string> Paths, int TotalMatches);

/// <summary>
/// Fuzzy "go to file" matching on relative paths: the query must be a case-insensitive subsequence of the path.
/// Ranking (each rule only breaks the ties of the previous one):
/// <list type="number">
/// <item><description>More contiguous matches first: a query that appears as one run beats one scattered over the path.</description></item>
/// <item><description>A match inside the file name beats a match in the directory part.</description></item>
/// <item><description>Shorter paths first.</description></item>
/// <item><description>Alphabetical (ignoring case), so the order is deterministic.</description></item>
/// </list>
/// </summary>
public static class FuzzyPathMatcher
{
    // Larger than any real gap count, small enough that adding to it never overflows.
    private const int Unreachable = 1 << 20;

    // Queries up to this length keep their working memory on the stack.
    private const int StackLimit = 128;

    /// <summary>
    /// Matches <paramref name="query"/> against <paramref name="path"/>. White space in the query is ignored and
    /// "\" counts as "/" on both sides. An empty query matches everything with no gaps.
    /// </summary>
    /// <returns>The match, or null when the query is not a subsequence of the path.</returns>
    public static PathMatch? Match(string query, string path)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(path);
        return MatchNormalized(NormalizeQuery(query), Normalize(path));
    }

    /// <summary>
    /// Keeps the paths that match <paramref name="query"/>, best first, and returns the first <paramref name="limit"/>
    /// of them. An empty query keeps every path, shortest first.
    /// </summary>
    public static RankedPaths Rank(IReadOnlyList<string> paths, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var normalizedQuery = NormalizeQuery(query);
        var matches = new List<(PathMatch Match, string Path)>();
        foreach (var path in paths)
        {
            if (MatchNormalized(normalizedQuery, Normalize(path)) is { } match)
            {
                matches.Add((match, path));
            }
        }

        matches.Sort(static (a, b) =>
        {
            var byMatch = a.Match.CompareTo(b.Match);
            if (byMatch != 0)
            {
                return byMatch;
            }

            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.Path, b.Path);
        });

        var best = matches.Count <= limit ? matches : matches.GetRange(0, limit);
        return new RankedPaths(best.ConvertAll(static m => m.Path), matches.Count);
    }

    private static string Normalize(string path) => path.Contains('\\', StringComparison.Ordinal) ? path.Replace('\\', '/') : path;

    /// <summary>Upper-cases the query, drops white space and unifies the separators.</summary>
    private static string NormalizeQuery(string query)
    {
        var builder = new StringBuilder(query.Length);
        foreach (var c in query)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c == '\\' ? '/' : char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static PathMatch? MatchNormalized(string query, string path)
    {
        var gaps = MinGaps(query, path);
        if (gaps < 0)
        {
            return null;
        }

        // Without a separator the whole path is the file name. Otherwise the file name must offer an alignment
        // that is as contiguous as the best one found in the whole path.
        var nameStart = path.LastIndexOf('/') + 1;
        var inFileName = nameStart == 0 || MinGaps(query, path.AsSpan(nameStart)) == gaps;
        return new PathMatch(gaps, inFileName, path.Length);
    }

    /// <summary>
    /// The fewest breaks between consecutive matched characters over every alignment of <paramref name="query"/>
    /// (already upper-cased) inside <paramref name="path"/>, or -1 when the query is not a subsequence of it.
    /// </summary>
    private static int MinGaps(ReadOnlySpan<char> query, ReadOnlySpan<char> path)
    {
        var length = query.Length;
        if (length == 0)
        {
            return 0;
        }

        if (length > path.Length)
        {
            return -1;
        }

        // Dynamic programming over the columns of the path, kept in two rows:
        //   endingHere[j]  = fewest breaks aligning query[..j] with query[j] on the current column,
        //   endingBefore[j] = fewest breaks aligning query[..j] with query[j] on any column seen so far.
        // Walking j downwards leaves the entries below j as they were on the previous column.
        Span<int> rows = length <= StackLimit ? stackalloc int[length * 2] : new int[length * 2];
        var endingHere = rows[..length];
        var endingBefore = rows[length..];
        rows.Fill(Unreachable);

        foreach (var raw in path)
        {
            var c = char.ToUpperInvariant(raw);
            for (var j = length - 1; j >= 0; j--)
            {
                var cost = Unreachable;
                if (c == query[j])
                {
                    // Right after the previous match: no break. Otherwise one more break than the best earlier match.
                    cost = j == 0 ? 0 : Math.Min(endingHere[j - 1], endingBefore[j - 1] + 1);
                }

                endingHere[j] = cost;
                if (cost < endingBefore[j])
                {
                    endingBefore[j] = cost;
                }
            }
        }

        var best = endingBefore[length - 1];
        return best >= Unreachable ? -1 : best;
    }
}
