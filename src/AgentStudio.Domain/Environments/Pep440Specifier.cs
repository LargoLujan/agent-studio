using System.Globalization;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// A subset of the version specifiers of PEP 440 as <c>requires-python</c> of a <c>pyproject.toml</c> writes them (docs/20 D-07): clauses joined by commas
/// (and), each with <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>==</c>, <c>!=</c> or <c>~=</c> and a release of up to four numbers; <c>==</c> and <c>!=</c> also with a
/// trailing <c>.*</c>. Anything else is <see cref="RangeVerdict.NotComparable"/>: an epoch (<c>1!</c>), a pre-release, post-release, development or local label,
/// the arbitrary equality <c>===</c>, <c>~=</c> with a single number, a wildcard where it is not allowed, an empty specifier, and an installed version that is
/// a pre-release. A clause that is certainly not satisfied settles the answer even if another cannot be read. It is total (it never throws) and bounded:
/// <see cref="MaxLength"/> characters and <see cref="MaxClauses"/> clauses.
/// </summary>
public static class Pep440Specifier
{
    /// <summary>The longest specifier that is looked at.</summary>
    public const int MaxLength = 200;

    /// <summary>The most clauses.</summary>
    public const int MaxClauses = 20;

    private const int MaxNumbers = 4;

    public static RangeVerdict Evaluate(string? specifier, ToolVersion? version)
    {
        if (specifier is null || version is null || specifier.Length > MaxLength || version.IsPreRelease || string.IsNullOrWhiteSpace(specifier))
        {
            return RangeVerdict.NotComparable;
        }

        var clauses = specifier.Split(',');
        if (clauses.Length > MaxClauses)
        {
            return RangeVerdict.NotComparable;
        }

        var installed = version.Numbers.ToArray();
        var undecided = false;
        foreach (var clause in clauses)
        {
            switch (EvaluateClause(clause.Trim(), installed))
            {
                case RangeVerdict.NotSatisfied:
                    return RangeVerdict.NotSatisfied;
                case RangeVerdict.NotComparable:
                    undecided = true;
                    break;
            }
        }

        return undecided ? RangeVerdict.NotComparable : RangeVerdict.Satisfied;
    }

    private static RangeVerdict EvaluateClause(string clause, int[] installed)
    {
        string? op = null;
        foreach (var candidate in new[] { "===", "~=", "==", "!=", ">=", "<=", ">", "<" })
        {
            if (clause.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                break;
            }
        }

        if (op is null or "===")
        {
            return RangeVerdict.NotComparable;
        }

        var text = clause[op.Length..].Trim();
        var wildcard = text.EndsWith(".*", StringComparison.Ordinal);
        if (wildcard)
        {
            text = text[..^2];
        }

        if (!Release(text, out var spec) || (wildcard && op is not ("==" or "!=")) || (op == "~=" && spec.Length < 2))
        {
            return RangeVerdict.NotComparable;
        }

        bool holds;
        switch (op)
        {
            case "==":
                holds = wildcard ? StartsWith(installed, spec) : Compare(installed, spec) == 0;
                break;
            case "!=":
                holds = wildcard ? !StartsWith(installed, spec) : Compare(installed, spec) != 0;
                break;
            case ">=":
                holds = Compare(installed, spec) >= 0;
                break;
            case "<=":
                holds = Compare(installed, spec) <= 0;
                break;
            case ">":
                holds = Compare(installed, spec) > 0;
                break;
            case "<":
                holds = Compare(installed, spec) < 0;
                break;
            default:
                // ~=: at least this release and the same prefix without the last number (~=3.8.2 is >=3.8.2, ==3.8.*).
                holds = Compare(installed, spec) >= 0 && StartsWith(installed, spec[..^1]);
                break;
        }

        return holds ? RangeVerdict.Satisfied : RangeVerdict.NotSatisfied;
    }

    /// <summary>A release segment: one to four numbers of up to nine digits joined by dots, and nothing else.</summary>
    private static bool Release(string text, out int[] numbers)
    {
        numbers = [];
        var parts = text.Split('.');
        if (parts.Length is < 1 or > MaxNumbers)
        {
            return false;
        }

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is < 1 or > 9 || !parts[i].All(char.IsAsciiDigit))
            {
                return false;
            }

            result[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }

        numbers = result;
        return true;
    }

    private static int Compare(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    private static bool StartsWith(int[] version, int[] prefix)
    {
        for (var i = 0; i < prefix.Length; i++)
        {
            if ((i < version.Length ? version[i] : 0) != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
