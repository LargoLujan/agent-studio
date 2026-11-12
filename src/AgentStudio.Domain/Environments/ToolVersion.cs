using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// The version of a tool as a tool tells it (<c>dotnet --list-sdks</c>, <c>node --version</c>, a version resource) or as a file of a
/// repository declares it (<c>sdk.version</c> of <c>global.json</c>): one to four numbers, an optional pre-release label and an optional
/// build label (docs/20 D-07). What a repository says is not trusted, so it has a strict shape, a cap of 64 characters and a cap of nine
/// digits per number (no overflow, nothing to parse as a surprise). Two versions compare by their numbers (missing ones are zero) and then
/// by the pre-release, as SemVer does: a version with a pre-release is lower than the same without it. The build label never counts.
/// </summary>
/// <remarks>The rules of ranges (<c>^</c>, <c>~</c>, PEP 440) and of <c>rollForward</c> are the logic of WP-E; this is the value they compare.</remarks>
public sealed class ToolVersion : IComparable<ToolVersion>, IEquatable<ToolVersion>
{
    /// <summary>The longest text of a version.</summary>
    public const int MaxLength = 64;

    private const int MaxNumbers = 4;

    private const int MaxDigits = 9;

    private const int MaxLabelLength = 50;

    private readonly int[] _numbers;

    private ToolVersion(string text, int[] numbers, string? preRelease)
    {
        Text = text;
        _numbers = numbers;
        PreRelease = preRelease;
    }

    /// <summary>The text, as it was read and whole: <c>10.0.303-preview.1+abc</c> keeps its pre-release and its build label.</summary>
    public string Text { get; }

    /// <summary>The numbers, as many as the text had (one to four).</summary>
    public IReadOnlyList<int> Numbers => _numbers;

    public int Major => _numbers[0];

    public int Minor => _numbers.Length > 1 ? _numbers[1] : 0;

    public int Patch => _numbers.Length > 2 ? _numbers[2] : 0;

    /// <summary>The label after <c>-</c>, or null.</summary>
    public string? PreRelease { get; }

    public bool IsPreRelease => PreRelease is not null;

    /// <exception cref="FormatException">The text is not a version of this shape.</exception>
    public static ToolVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{Truncated(text)}' is not a version: 1 to 4 numbers of up to 9 digits, an optional -pre-release and an optional +build, at most {MaxLength} characters.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ToolVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        var rest = text.AsSpan();
        string? build = null;
        var plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            build = rest[(plus + 1)..].ToString();
            rest = rest[..plus];
            if (!IsLabel(build))
            {
                return false;
            }
        }

        string? preRelease = null;
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = rest[(dash + 1)..].ToString();
            rest = rest[..dash];
            if (!IsLabel(preRelease))
            {
                return false;
            }
        }

        var parts = rest.ToString().Split('.');
        if (parts.Length is < 1 or > MaxNumbers)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is < 1 or > MaxDigits || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new ToolVersion(text, numbers, preRelease);
        return true;
    }

    public int CompareTo(ToolVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < MaxNumbers; i++)
        {
            var left = i < _numbers.Length ? _numbers[i] : 0;
            var right = i < other._numbers.Length ? other._numbers[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>Equal in text: <c>10.0</c> and <c>10.0.0</c> compare as the same version but are not the same text.</summary>
    public bool Equals(ToolVersion? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ToolVersion other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    public static bool operator <(ToolVersion? left, ToolVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(ToolVersion? left, ToolVersion? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(ToolVersion? left, ToolVersion? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >=(ToolVersion? left, ToolVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    private static bool IsLabel(string label) =>
        label.Length is >= 1 and <= MaxLabelLength
        && label.Split('.').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var l = leftParts[i];
            var r = rightParts[i];
            var leftNumeric = l.All(char.IsAsciiDigit);
            var rightNumeric = r.All(char.IsAsciiDigit);
            int order;
            if (leftNumeric && rightNumeric)
            {
                // Numbers of any length compare by value without overflowing: by length, then by digits.
                var a = l.TrimStart('0');
                var b = r.TrimStart('0');
                order = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else if (leftNumeric != rightNumeric)
            {
                // A numeric identifier is lower than an alphanumeric one.
                order = leftNumeric ? -1 : 1;
            }
            else
            {
                order = string.CompareOrdinal(l, r);
            }

            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>A bounded copy of what was read, with no control character, to put in a message.</summary>
    private static string Truncated(string? text)
    {
        if (text is null)
        {
            return "(null)";
        }

        var shown = text.Length <= 40 ? text : text[..40] + "…";
        return string.Concat(shown.Select(c => char.IsControl(c) ? '?' : c));
    }
}
