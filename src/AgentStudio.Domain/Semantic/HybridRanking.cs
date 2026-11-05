namespace AgentStudio.Domain.Semantic;

/// <summary>
/// One place of a ranked list of the hybrid search: the id of what is ranked (a fragment, or the entry it belongs to) and
/// the time of its source, which breaks ties (docs/17 D-05).
/// </summary>
public readonly record struct RankedCandidate(string Id, DateTimeOffset At);

/// <summary>
/// One place of the semantic list of the hybrid search: the id and the time of a fragment, as in
/// <see cref="RankedCandidate"/>, and its similarity to the query, the dot product of the two normalised vectors (their
/// cosine).
/// </summary>
public readonly record struct SimilarCandidate(string Id, DateTimeOffset At, float Similarity);

/// <summary>
/// One result of <see cref="HybridRanking.Fuse"/>: the id and time of the candidate, its score (the sum of
/// <c>1 / (k + rank)</c> over the lists it is in) and its rank, from 1, in the lexical list and in the semantic one (null
/// when it is not in that list).
/// </summary>
public sealed record FusedCandidate(string Id, DateTimeOffset At, double Score, int? LexicalRank, int? SemanticRank);

/// <summary>
/// The fusion of the two lists of the hybrid search (docs/17 D-05; ADR-0047): the lexical list (FTS5 and <c>bm25</c>
/// over the terms of <see cref="LexicalTerms"/>) and the semantic one (cosine with the <c>query:</c> embedding), fused by
/// Reciprocal Rank Fusion with <see cref="K"/> = 60. Deterministic: ties go to the newest source first and then by
/// ordinal id, so the order never depends on chance nor on the order of a dictionary.
/// </summary>
public static class HybridRanking
{
    /// <summary>The constant of Reciprocal Rank Fusion (docs/17 D-05).</summary>
    public const int K = 60;

    /// <summary>
    /// Fuses <paramref name="lexical"/> and <paramref name="semantic"/>, each ordered from the best: a candidate scores
    /// <c>1 / (k + rank)</c> in every list it is in (rank from 1; a repeated id counts at its first place), and the fused
    /// list goes from the highest score, then the newest <see cref="RankedCandidate.At"/>, then the ordinal id. Either list
    /// may be empty: with no semantic list (no model) the result is the lexical order.
    /// </summary>
    /// <exception cref="ArgumentException">An id is blank, or the two lists give one id two different times.</exception>
    public static IReadOnlyList<FusedCandidate> Fuse(IReadOnlyList<RankedCandidate> lexical, IReadOnlyList<RankedCandidate> semantic, int k = K)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var lexicalRanks = Ranks(lexical, nameof(lexical));
        var semanticRanks = Ranks(semantic, nameof(semantic));
        var fused = new List<FusedCandidate>(lexicalRanks.Count + semanticRanks.Count);
        foreach (var (id, (lexicalRank, at)) in lexicalRanks)
        {
            int? semanticRank = null;
            if (semanticRanks.TryGetValue(id, out var other))
            {
                if (other.At != at)
                {
                    throw new ArgumentException($"Candidate '{id}' has two different times in the two lists.", nameof(semantic));
                }

                semanticRank = other.Rank;
            }

            // Always lexical first, then semantic: the same pair of ranks gives the same double whatever the list.
            var score = Contribution(lexicalRank, k) + (semanticRank is { } rank ? Contribution(rank, k) : 0);
            fused.Add(new FusedCandidate(id, at, score, lexicalRank, semanticRank));
        }

        foreach (var (id, (semanticRank, at)) in semanticRanks)
        {
            if (!lexicalRanks.ContainsKey(id))
            {
                fused.Add(new FusedCandidate(id, at, Contribution(semanticRank, k), null, semanticRank));
            }
        }

        fused.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byTime = b.At.CompareTo(a.At);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
        });
        return fused;
    }

    private static double Contribution(int rank, int k) => 1.0 / (k + rank);

    private static Dictionary<string, (int Rank, DateTimeOffset At)> Ranks(IReadOnlyList<RankedCandidate> list, string name)
    {
        var ranks = new Dictionary<string, (int Rank, DateTimeOffset At)>(list.Count, StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[i];
            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                throw new ArgumentException("A ranked candidate has a blank id.", name);
            }

            ranks.TryAdd(candidate.Id, (i + 1, candidate.At));
        }

        return ranks;
    }
}
