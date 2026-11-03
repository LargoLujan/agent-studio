namespace AgentStudio.Observability.Logging;

/// <summary>Makes text safe to hand to the JSON writer, which refuses unpaired surrogates.</summary>
internal static class CleanText
{
    private const char ReplacementCharacter = '�';

    /// <summary>Returns the text with every unpaired UTF-16 surrogate replaced by U+FFFD; the same instance when there is none.</summary>
    public static string Clean(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (!char.IsSurrogate(current))
            {
                continue;
            }

            if (char.IsHighSurrogate(current) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++; // A valid pair.
                continue;
            }

            return Replace(text, i);
        }

        return text;
    }

    private static string Replace(string text, int firstBad)
    {
        var chars = text.ToCharArray();
        for (var i = firstBad; i < chars.Length; i++)
        {
            if (!char.IsSurrogate(chars[i]))
            {
                continue;
            }

            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
                continue;
            }

            chars[i] = ReplacementCharacter;
        }

        return new string(chars);
    }
}
