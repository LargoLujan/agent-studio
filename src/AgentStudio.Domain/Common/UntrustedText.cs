using System.Globalization;
using System.Text;

namespace AgentStudio.Domain.Common;

/// <summary>
/// Which characters of a text that came from a repository, a tool or an agent cannot be shown or written to a diagram as they are (docs/20 D-06 and D-24):
/// the control characters, the line and paragraph separators (U+2028 and U+2029) and every character of the category «format» (the marks of direction
/// U+202A to U+202E and U+2066 to U+2069, the zero-width ones, the byte-order mark, the tag characters). They would reorder or hide what the person reads.
/// </summary>
public static class UntrustedText
{
    /// <summary>Whether <paramref name="rune"/> is one of the characters described in the summary of the type.</summary>
    public static bool IsHidden(Rune rune) =>
        Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    public static bool HasHidden(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHidden(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text with every hidden character replaced by a space (a line end or a tab) or taken out (a format character, which has no width).</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsHidden(rune))
            {
                builder.Append(rune.ToString());
            }
            else if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
