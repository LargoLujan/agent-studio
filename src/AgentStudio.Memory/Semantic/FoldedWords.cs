using System.Globalization;
using System.Text;

namespace AgentStudio.Memory.Semantic;

/// <summary>
/// The words of a text as the lexical search sees them (FTS5 <c>unicode61</c>, as the store tokenizes): lower case, without
/// accents, cut at anything that is not a letter or a digit. With them the index counts how many fragments a term is in
/// without asking the store, and finds an identifier of a search exactly, at the level of whole words, as an FTS5 phrase
/// would (ADR-0047, addendum of 2026-11-04). A surrogate is never a letter here, so a lone one cannot break the
/// normalization.
/// </summary>
public static class FoldedWords
{
    /// <summary>The words of <paramref name="text"/>, in order.</summary>
    public static string[] Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            clean.Append(char.IsSurrogate(c) ? ' ' : c);
        }

        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in clean.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return [.. words];
    }

    /// <summary>Whether <paramref name="phrase"/> comes, word after word, somewhere in <paramref name="words"/>; an empty phrase never does.</summary>
    public static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(phrase);
        return IndexOf(words, phrase, 0) >= 0;
    }

    /// <summary>Where <paramref name="phrase"/> comes in <paramref name="words"/> from <paramref name="start"/> on; -1 when it does not.</summary>
    public static int IndexOf(IReadOnlyList<string> words, IReadOnlyList<string> phrase, int start)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(phrase);
        if (phrase.Count == 0)
        {
            return -1;
        }

        for (var i = Math.Max(start, 0); i + phrase.Count <= words.Count; i++)
        {
            var j = 0;
            while (j < phrase.Count && string.Equals(words[i + j], phrase[j], StringComparison.Ordinal))
            {
                j++;
            }

            if (j == phrase.Count)
            {
                return i;
            }
        }

        return -1;
    }
}
