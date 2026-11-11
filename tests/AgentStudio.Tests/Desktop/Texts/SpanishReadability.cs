using System.Text.RegularExpressions;

namespace AgentStudio.Tests.Desktop.Texts;

/// <summary>
/// The measures of the guide of texts (docs/17 D-52 and D-53, ADR-0054 point 9): sentences, words, Spanish syllables and the
/// INFLESZ index of Barrio-Cantalejo, which is the Szigriszt-Pazos formula
/// <c>206.835 - 62.3 * syllables / words - words / sentences</c> read on the INFLESZ scale (55 or more is «normal»).
/// </summary>
internal static partial class SpanishReadability
{
    /// <summary>The scale of INFLESZ, as the guide reads it: 55 or more is «normal», the level D-53 asks of an explanation.</summary>
    public const double NormalLevel = 55;

    private const string Vowels = "aeiouáéíóúü";

    /// <summary>Strong vowels (a, e, o) and the accented weak ones (í, ú): two of them in a row are a hiatus, two syllables.</summary>
    private const string Strong = "aeoáéóíú";

    /// <summary>What stands for a placeholder like <c>{0}</c> or <c>{1:N0}</c>: a short word, as the person reads a number or a name.</summary>
    private const string PlaceholderWord = "dato";

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<=[.!?…])\s+|\r?\n")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[\p{L}\p{N}]")]
    private static partial Regex LetterOrDigit();

    [GeneratedRegex(@"([qg])u(?=[eiéí])")]
    private static partial Regex SilentU();

    /// <summary>The sentences of a text: they end at «.», «?», «!» or «…» followed by a space, and at every new line (a list line is a sentence).</summary>
    public static IReadOnlyList<string> Sentences(string text) =>
        [.. SentenceBreak().Split(Placeholder().Replace(text, PlaceholderWord)).Where(s => LetterOrDigit().IsMatch(s))];

    /// <summary>The words of a sentence: what is between spaces and has a letter or a digit (so «·» and «→» are not words).</summary>
    public static IReadOnlyList<string> Words(string sentence) =>
        [.. sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => LetterOrDigit().IsMatch(w))];

    /// <summary>The syllables of a Spanish word, by its vowels: a diphthong or a triphthong is one, a hiatus is two; the «u» of «que» and «gui» is silent.</summary>
    public static int CountSyllables(string word)
    {
        var total = 0;
        foreach (Match part in Parts().Matches(word))
        {
            total += CountLetterRun(part.Value.ToLowerInvariant());
        }

        // A number or a symbol read aloud has syllables too: a token with no letter but with a digit counts two.
        return total == 0 && word.Any(char.IsDigit) ? 2 : total;
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Parts();

    private static int CountLetterRun(string run)
    {
        run = SilentU().Replace(run, "$1");
        var isVowel = new bool[run.Length];
        for (var i = 0; i < run.Length; i++)
        {
            // A final «y» is a vowel («hay», «muy», «y»); any other «y» is a consonant.
            isVowel[i] = Vowels.Contains(run[i]) || (run[i] == 'y' && i == run.Length - 1);
        }

        var count = 0;
        var position = 0;
        while (position < run.Length)
        {
            if (!isVowel[position])
            {
                position++;
                continue;
            }

            var end = position;
            while (end < run.Length && isVowel[end])
            {
                end++;
            }

            count++;
            for (var k = position + 1; k < end; k++)
            {
                if (Strong.Contains(run[k - 1]) && Strong.Contains(run[k]))
                {
                    count++;
                }
            }

            position = end;
        }

        return count == 0 && run.Length > 0 ? 1 : count;
    }

    /// <summary>The INFLESZ index of a text, or <c>null</c> when it has no words.</summary>
    public static double? Inflesz(string text)
    {
        var sentences = Sentences(text);
        var words = sentences.SelectMany(Words).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        var syllables = words.Sum(CountSyllables);
        return 206.835 - (62.3 * syllables / words.Count) - ((double)words.Count / sentences.Count);
    }

    /// <summary>The words of the longest sentence of a text, and that sentence.</summary>
    public static (int Words, string Sentence) LongestSentence(string text)
    {
        var best = (Words: 0, Sentence: string.Empty);
        foreach (var sentence in Sentences(text))
        {
            var count = Words(sentence).Count;
            if (count > best.Words)
            {
                best = (count, sentence);
            }
        }

        return best;
    }
}
