using System.Globalization;
using System.Text;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.SkillsMcp.History;

/// <summary>One fragment a search of the history found, with its place in the fusion (its rank by words and by meaning).</summary>
internal sealed record HistoryMatch(HistoryFragment Fragment, FusedCandidate Rank);

/// <summary>
/// What one search of the history found: the matches, best first and at most the <c>top</c> asked; how many fragments
/// matched by words; and whether the vectors of the best word hits widened it.
/// </summary>
internal sealed record HistorySearch(IReadOnlyList<HistoryMatch> Matches, int WordMatches, bool Widened);

/// <summary>
/// The ranking of <c>search_project_history</c> (docs/17 D-06; ADR-0047, decision 9), over the fragments of one
/// <see cref="ProjectHistorySnapshot"/>. The server never loads the model (ADR-0024 keeps its surface), so it cannot embed
/// the query:
/// <list type="number">
/// <item><b>By words,</b> as the lexical list of the index: the terms of <see cref="LexicalTerms.From"/> (no function
/// words), each one a phrase of its tokens and joined by <c>OR</c>, scored with the <c>bm25</c> of FTS5 (k1 = 1.2,
/// b = 0.75) over tokens folded as its <c>unicode61</c> tokenizer folds them: lower case, without diacritics, cut at
/// anything that is not a letter or a digit.</item>
/// <item><b>Widened by meaning,</b> only when the snapshot carries vectors: the normalised sum of the vectors of the first
/// <see cref="SeedCount"/> word hits that have one ranks every fragment with a vector by its dot product (the vectors are
/// L2-normalised, so it is the cosine), its first <see cref="NeighbourCount"/> are the semantic list, and the two lists
/// are fused by <see cref="HybridRanking.Fuse"/> (Reciprocal Rank Fusion, k = 60). A fragment that shares no word with
/// the query but means what the best hits mean can come in this way.</item>
/// </list>
/// Deterministic: ties go to the newest fragment and then to the ordinal id, as in the index. Nothing matches by words, no
/// result: the vectors only widen what the words found.
/// </summary>
internal sealed class HistoryRanking
{
    /// <summary>The word hits whose vectors make the direction of the widening.</summary>
    public const int SeedCount = 3;

    /// <summary>The fragments the widening ranks, the closest first: the length of the semantic list.</summary>
    public const int NeighbourCount = 20;

    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>The weight FTS5 gives a term found in half the fragments or more (its idf would not be positive).</summary>
    private const double MinimumIdf = 1e-6;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly List<Indexed> _fragments;
    private readonly Dictionary<string, HistoryFragment> _byId;
    private readonly double _averageLength;

    /// <exception cref="ArgumentNullException">No snapshot.</exception>
    public HistoryRanking(ProjectHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _fragments = [.. snapshot.Fragments.Select(f => new Indexed(f, Tokens(f.Text), Normalised(f.Vector)))];
        _byId = _fragments.ToDictionary(f => f.Fragment.Id, f => f.Fragment, StringComparer.Ordinal);
        _averageLength = _fragments.Count == 0 ? 1 : Math.Max(1, _fragments.Average(f => (double)f.Tokens.Length));
        HasVectors = snapshot.ModelFingerprint is not null && _fragments.Any(f => f.Vector is not null);
    }

    /// <summary>Whether any fragment carries a vector that can widen a search.</summary>
    public bool HasVectors { get; }

    /// <summary>The fragments that match <paramref name="query"/>, best first and at most <paramref name="top"/>.</summary>
    public HistorySearch Search(string query, int top)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        var lexical = ByWords(query);
        var semantic = HasVectors ? ByMeaning(lexical) : [];
        var fused = HybridRanking.Fuse(
            [.. lexical.Select(f => new RankedCandidate(f.Fragment.Id, f.Fragment.At))],
            [.. semantic.Select(f => new RankedCandidate(f.Fragment.Id, f.Fragment.At))]);
        var matches = fused.Take(top).Select(rank => new HistoryMatch(_byId[rank.Id], rank)).ToList();
        return new HistorySearch(matches, lexical.Count, semantic.Count > 0);
    }

    /// <summary>
    /// The tokens of <paramref name="text"/> as <c>unicode61</c> with <c>remove_diacritics</c> cuts and folds them:
    /// maximal runs of letters and digits, in lower case and without their diacritics.
    /// </summary>
    internal static string[] Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = WellFormed(text).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue; // a diacritic of the letter before: it is folded away and does not cut the token.
            }

            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    /// <summary>The lexical list: every fragment that has at least one phrase of the query, by bm25, then the newest, then the id.</summary>
    private List<Indexed> ByWords(string query)
    {
        var phrases = LexicalTerms.From(WellFormed(query))
            .Select(Tokens)
            .Where(tokens => tokens.Length > 0)
            .DistinctBy(tokens => string.Join(' ', tokens), StringComparer.Ordinal)
            .ToList();
        if (phrases.Count == 0 || _fragments.Count == 0)
        {
            return [];
        }

        var counts = _fragments.Select(f => phrases.Select(p => Occurrences(f.Tokens, p)).ToArray()).ToList();
        var idf = new double[phrases.Count];
        for (var p = 0; p < phrases.Count; p++)
        {
            var having = counts.Count(c => c[p] > 0);
            var weight = Math.Log((_fragments.Count - having + 0.5) / (having + 0.5));
            idf[p] = weight > 0 ? weight : MinimumIdf;
        }

        var scored = new List<(Indexed Fragment, double Score)>();
        for (var i = 0; i < _fragments.Count; i++)
        {
            var fragment = _fragments[i];
            var score = 0d;
            var matched = false;
            for (var p = 0; p < phrases.Count; p++)
            {
                var frequency = counts[i][p];
                if (frequency == 0)
                {
                    continue;
                }

                matched = true;
                var norm = K1 * (1 - B + (B * fragment.Tokens.Length / _averageLength));
                score += idf[p] * frequency * (K1 + 1) / (frequency + norm);
            }

            if (matched)
            {
                scored.Add((fragment, score));
            }
        }

        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : Newest(a.Fragment, b.Fragment);
        });
        return [.. scored.Select(s => s.Fragment)];
    }

    /// <summary>
    /// The semantic list: the fragments closest to the direction of the first <see cref="SeedCount"/> word hits with a
    /// vector, the closest first; empty when no word hit has one.
    /// </summary>
    private List<Indexed> ByMeaning(List<Indexed> lexical)
    {
        var seeds = lexical.Where(f => f.Vector is not null).Take(SeedCount).Select(f => f.Vector!).ToList();
        if (seeds.Count == 0)
        {
            return [];
        }

        var direction = new float[seeds[0].Length];
        foreach (var seed in seeds)
        {
            for (var d = 0; d < direction.Length; d++)
            {
                direction[d] += seed[d];
            }
        }

        if (Normalised(direction) is not { } unit)
        {
            return [];
        }

        var scored = _fragments
            .Where(f => f.Vector is not null)
            .Select(f => (Fragment: f, Score: Dot(unit, f.Vector!)))
            .ToList();
        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : Newest(a.Fragment, b.Fragment);
        });
        return [.. scored.Take(NeighbourCount).Select(s => s.Fragment)];
    }

    /// <summary>
    /// <paramref name="text"/> with every unpaired surrogate replaced by U+FFFD, so folding it (which normalises the text)
    /// never throws on a string that is not well-formed UTF-16, whoever wrote it.
    /// </summary>
    internal static string WellFormed(string text) => Utf8.GetString(Utf8.GetBytes(text));

    /// <summary>How many times <paramref name="phrase"/> appears in <paramref name="tokens"/> as consecutive tokens.</summary>
    private static int Occurrences(string[] tokens, string[] phrase)
    {
        var count = 0;
        for (var start = 0; start + phrase.Length <= tokens.Length; start++)
        {
            var all = true;
            for (var k = 0; k < phrase.Length; k++)
            {
                if (!string.Equals(tokens[start + k], phrase[k], StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The newest first, then by ordinal id: the tie-break of the index (docs/17 D-05).</summary>
    private static int Newest(Indexed a, Indexed b)
    {
        var byTime = b.Fragment.At.CompareTo(a.Fragment.At);
        return byTime != 0 ? byTime : string.CompareOrdinal(a.Fragment.Id, b.Fragment.Id);
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// <paramref name="vector"/> scaled to length 1; null when there is none, or it has a value that is not finite or no
    /// length (a vector the widening cannot use).
    /// </summary>
    private static float[]? Normalised(float[]? vector)
    {
        if (vector is null || vector.Length == 0 || vector.Any(v => !float.IsFinite(v)))
        {
            return null;
        }

        var length = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (length == 0 || !double.IsFinite(length))
        {
            return null;
        }

        return [.. vector.Select(v => (float)(v / length))];
    }

    /// <summary>A fragment with its tokens and its unit vector (null without a usable one).</summary>
    private sealed record Indexed(HistoryFragment Fragment, string[] Tokens, float[]? Vector);
}
