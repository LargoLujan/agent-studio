using System.Globalization;
using System.Text;

namespace AgentStudio.Domain.Semantic;

/// <summary>
/// The terms of a search text that go to the lexical list of the hybrid search (docs/17 D-05; ADR-0047). The text is cut at
/// white space; a term with no letter or digit is dropped, and so is a function word (Spanish, and a few English ones), each
/// term counts once and at most <see cref="MaxTerms"/> go. Whoever runs the lexical search quotes each one as a phrase, with
/// its quotes doubled, and joins them with <c>OR</c>, as the memory does (docs/14 D-21), so no FTS5 operator ever gets
/// through from what a person or an agent wrote.
/// <para>
/// Why the function words go: with them every fragment matches an <c>OR</c> query («de», «la», «el» are everywhere), the
/// lexical order of a paraphrased question is noise, and the fusion came out worse than the vectors alone. Without them,
/// the hybrid search put the answer among the first five in 29 of 30 questions, against 27 for the vectors and 21 for
/// FTS5 as it is today (docs/investigacion/2026-10-30-spike-embeddings.md).
/// </para>
/// Function words are compared folded as <c>unicode61</c> folds them: lower case, without accents and without what is
/// not a letter or a digit («¿Qué» is «que»).
/// </summary>
public static class LexicalTerms
{
    /// <summary>The most terms a text sends to the lexical search; the rest are dropped.</summary>
    public const int MaxTerms = 32;

    // The Spanish list is the one the spike measured; the English words only keep a mixed query clean.
    private static readonly HashSet<string> FunctionWords = new(
        """
        a al algo algun alguna algunas alguno algunos ante antes aqui asi aun cada como con contra cual cuales cuando cuanto
        de del desde donde dos el ella ellas ello ellos en entre era es esa esas ese eso esos esta estan estas este esto estos
        fue ha hace hay hasta la las le les lo los mas me mi mis mucho muy nada ni no nos nosotros o otra otras otro otros
        para pero poco por porque puede que quien se sea ser si sin sobre son su sus tambien tan tanto te tiene tienen todo
        todos tu tus un una uno unos y ya yo
        an and are as at be by for from in is it of on or that the this to was with
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    /// <summary>The terms of <paramref name="text"/> for the lexical list, in their order; empty when none is left.</summary>
    public static IReadOnlyList<string> From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var folded = Fold(term);
            if (folded.Length == 0 || FunctionWords.Contains(folded) || !seen.Add(folded))
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == MaxTerms)
            {
                break;
            }
        }

        return terms;
    }

    /// <summary>Whether <paramref name="term"/>, folded, is one of the function words the lexical search drops.</summary>
    public static bool IsFunctionWord(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return FunctionWords.Contains(Fold(term));
    }

    private static string Fold(string term)
    {
        var decomposed = WithoutLoneSurrogates(term).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                folded.Append(c);
            }
        }

        return folded.ToString();
    }

    /// <summary>
    /// <paramref name="term"/> with each lone surrogate (half of a pair, without the other half) replaced by U+FFFD:
    /// normalization refuses them, and a pasted text or a JSON <c>\uD800</c> can bring one.
    /// </summary>
    private static string WithoutLoneSurrogates(string term)
    {
        StringBuilder? clean = null;
        for (var i = 0; i < term.Length; i++)
        {
            var c = term[i];
            if (char.IsHighSurrogate(c) && i + 1 < term.Length && char.IsLowSurrogate(term[i + 1]))
            {
                clean?.Append(c).Append(term[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                clean ??= new StringBuilder(term.Length).Append(term, 0, i);
                clean.Append('\uFFFD');
            }
            else
            {
                clean?.Append(c);
            }
        }

        return clean?.ToString() ?? term;
    }
}
