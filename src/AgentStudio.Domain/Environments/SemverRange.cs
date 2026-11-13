namespace AgentStudio.Domain.Environments;

/// <summary>The answer of a comparison of a version with a range that a repository wrote (docs/20 D-07): what cannot be decided is never «satisfied».</summary>
public enum RangeVerdict
{
    /// <summary>The version is in the range.</summary>
    Satisfied,

    /// <summary>The version is not in the range.</summary>
    NotSatisfied,

    /// <summary>The range is outside the subset this code understands (or too long, too complex, or the version is a pre-release): no «yes» and no «no».</summary>
    NotComparable,
}

/// <summary>
/// A subset of the ranges of <c>semver</c> as <c>engines</c> of a <c>package.json</c> and <c>.nvmrc</c> write them (docs/20 D-07): comparators with
/// <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c> and <c>=</c> (or none), <c>^</c> and <c>~</c>, partial versions and <c>x</c>, <c>X</c> or <c>*</c>, joined by spaces
/// (and) and by <c>||</c> (or), with an optional <c>v</c>. Anything else (a pre-release or a build label in the range, a hyphen range
/// <c>1.2 - 2.0</c>, a tag such as <c>lts/*</c>, a URL) is <see cref="RangeVerdict.NotComparable"/>, and so is a version that is a pre-release.
/// It is total: it never throws, whatever the text, and it is bounded: <see cref="MaxLength"/> characters, <see cref="MaxSets"/> sets of
/// <see cref="MaxComparators"/> comparators. Only the first three numbers of a version count.
/// </summary>
public static class SemverRange
{
    /// <summary>The longest range that is looked at.</summary>
    public const int MaxLength = 200;

    /// <summary>The most sets joined by <c>||</c>.</summary>
    public const int MaxSets = 20;

    /// <summary>The most comparators of a set.</summary>
    public const int MaxComparators = 20;

    private enum Op
    {
        Ge,
        Gt,
        Le,
        Lt,
        Eq,
    }

    private readonly record struct Triple(long Major, long Minor, long Patch) : IComparable<Triple>
    {
        public int CompareTo(Triple other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0)
            {
                return major;
            }

            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }
    }

    /// <summary>One condition on a version: it is compared with <see cref="Version"/> by <see cref="Op"/>.</summary>
    private sealed record Bound(Op Op, Triple Version);

    public static RangeVerdict Evaluate(string? range, ToolVersion? version)
    {
        if (range is null || version is null || range.Length > MaxLength || version.IsPreRelease
            || (version.Numbers.Count > 3 && version.Numbers[3] != 0))
        {
            return RangeVerdict.NotComparable;
        }

        var sets = range.Split("||");
        if (sets.Length > MaxSets)
        {
            return RangeVerdict.NotComparable;
        }

        var installed = new Triple(version.Major, version.Minor, version.Patch);
        var undecided = false;
        foreach (var set in sets)
        {
            switch (EvaluateSet(set, installed))
            {
                case RangeVerdict.Satisfied:
                    return RangeVerdict.Satisfied;
                case RangeVerdict.NotComparable:
                    undecided = true;
                    break;
            }
        }

        return undecided ? RangeVerdict.NotComparable : RangeVerdict.NotSatisfied;
    }

    private static RangeVerdict EvaluateSet(string set, Triple version)
    {
        var tokens = Tokens(set);
        if (tokens is null || tokens.Count > MaxComparators)
        {
            return RangeVerdict.NotComparable;
        }

        var all = new List<Bound>();
        foreach (var token in tokens)
        {
            var bounds = Comparator(token);
            if (bounds is null)
            {
                return RangeVerdict.NotComparable;
            }

            all.AddRange(bounds);
        }

        return all.All(bound => Holds(bound, version)) ? RangeVerdict.Satisfied : RangeVerdict.NotSatisfied;
    }

    /// <summary>The comparators of a set: split at spaces, an operator written alone (<c>&gt;= 1.2.3</c>) joined to the version after it. Null when it cannot be done.</summary>
    private static List<string>? Tokens(string set)
    {
        var words = set.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word is ">=" or ">" or "<=" or "<" or "=" or "^" or "~")
            {
                if (i + 1 >= words.Length)
                {
                    return null;
                }

                word += words[++i];
            }

            tokens.Add(word);
        }

        return tokens;
    }

    private static bool Holds(Bound bound, Triple version)
    {
        var order = version.CompareTo(bound.Version);
        return bound.Op switch
        {
            Op.Ge => order >= 0,
            Op.Gt => order > 0,
            Op.Le => order <= 0,
            Op.Lt => order < 0,
            _ => order == 0,
        };
    }

    private static readonly Bound[] Any = [];

    // Never satisfied: a version cannot be below zero.
    private static readonly Bound[] Nothing = [new Bound(Op.Lt, new Triple(0, 0, 0))];

    /// <summary>The bounds one comparator means, all of which must hold; null when its text is not in the subset.</summary>
    private static Bound[]? Comparator(string token)
    {
        var rest = token;
        string op = string.Empty;
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
        {
            if (rest.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                rest = rest[candidate.Length..];
                break;
            }
        }

        if (rest.Length > 0 && rest[0] is 'v' or 'V')
        {
            rest = rest[1..];
        }

        if (!Partial(rest, out var major, out var minor, out var patch))
        {
            return null;
        }

        if (major is null)
        {
            return op is ">" or "<" ? Nothing : Any;
        }

        long m = major.Value, n = minor ?? 0, p = patch ?? 0;
        Bound Ge(long a, long b, long c) => new(Op.Ge, new Triple(a, b, c));
        Bound Lt(long a, long b, long c) => new(Op.Lt, new Triple(a, b, c));
        switch (op)
        {
            case "" or "=":
                return minor is null ? [Ge(m, 0, 0), Lt(m + 1, 0, 0)]
                    : patch is null ? [Ge(m, n, 0), Lt(m, n + 1, 0)]
                    : [new Bound(Op.Eq, new Triple(m, n, p))];
            case ">=":
                return [Ge(m, n, p)];
            case ">":
                return minor is null ? [Ge(m + 1, 0, 0)]
                    : patch is null ? [Ge(m, n + 1, 0)]
                    : [new Bound(Op.Gt, new Triple(m, n, p))];
            case "<":
                return [Lt(m, n, p)];
            case "<=":
                return minor is null ? [Lt(m + 1, 0, 0)]
                    : patch is null ? [Lt(m, n + 1, 0)]
                    : [new Bound(Op.Le, new Triple(m, n, p))];
            case "~":
                return minor is null ? [Ge(m, 0, 0), Lt(m + 1, 0, 0)] : [Ge(m, n, p), Lt(m, n + 1, 0)];
            default:
                // ^: the leftmost number that is not zero is the one that cannot change.
                if (m > 0 || minor is null)
                {
                    return [Ge(m, n, p), Lt(m + 1, 0, 0)];
                }

                if (n > 0 || patch is null)
                {
                    return [Ge(m, n, p), minor is not null && n == 0 ? Lt(0, 1, 0) : Lt(0, n + 1, 0)];
                }

                return [Ge(m, n, p), Lt(0, 0, p + 1)];
        }
    }

    /// <summary>A partial version (<c>1</c>, <c>1.2</c>, <c>1.2.3</c>, <c>1.x</c>, <c>*</c>): the numbers, null from the first wildcard on. False when it is anything else.</summary>
    private static bool Partial(string text, out long? major, out long? minor, out long? patch)
    {
        major = minor = patch = null;
        var parts = text.Split('.');
        if (text.Length == 0 || parts.Length > 3)
        {
            return false;
        }

        var wildcard = false;
        var numbers = new long?[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is "x" or "X" or "*")
            {
                wildcard = true;
            }
            else if (wildcard || part.Length is < 1 or > 9 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
            else
            {
                numbers[i] = long.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        (major, minor, patch) = (numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
