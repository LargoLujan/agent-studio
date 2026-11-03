using System.Globalization;
using System.Text;

namespace AgentStudio.Domain.Skills;

/// <summary>Text helpers shared by the tagger and the recommender: accent-free lower-case words.</summary>
internal static class SkillText
{
    /// <summary>Lower-cases, strips diacritics and turns every run of non [a-z0-9] into one space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var c = char.ToLowerInvariant(ch);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    public static string[] Words(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Length == 0 ? [] : normalized.Split(' ');
    }
}
