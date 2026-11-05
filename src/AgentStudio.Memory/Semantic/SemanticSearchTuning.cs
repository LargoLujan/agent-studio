using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Memory.Semantic;

/// <summary>
/// What the hybrid search of the index calibrates (ADR-0047, addenda of 2026-11-03 and 2026-11-04), on the question set of
/// F12-4, its questions by metadata and identifiers and its distractors, with the real model. The two lists are always
/// fused as D-05 says, with the same weight and k = 60 (<c>HybridRanking.Fuse</c>).
/// <list type="bullet">
/// <item>The cut of the semantic list, by the fingerprint of the model (<see cref="CutFor"/>): only what reaches it has a
/// place by meaning, whether the words find it too or not; what only the words find still comes out, by its place in the
/// lexical list. A model the table does not know has no cut, and its searches say so
/// (<see cref="SemanticSearchResult.CutNotice"/>).</item>
/// <item>The words of the search the lexical list leaves out: the fixed words of the template of the journal
/// (<see cref="TemplateWords"/>), unless the search has no other; and, as a net, those in more than
/// <see cref="CommonTermFraction"/> of the fragments.</item>
/// <item>How many fragments that contain exactly an identifier of the search (a run, a number of the journal, a day, a memory
/// id: <see cref="SearchIdentifiers"/>) go first, before the fusion (<see cref="MaxIdentifierHits"/>).</item>
/// </list>
/// </summary>
public sealed record SemanticSearchTuning
{
    /// <summary>
    /// The share of <see cref="CommonTermFraction"/>: chosen by risk, not by the question set, where no ceiling changes
    /// F12-4 (ADR-0047, addendum of 2026-11-04). It drops only what is in more than half of the fragments; a lower one would
    /// also drop the roles, states and names a person searches the journal by.
    /// </summary>
    public const double DefaultCommonTermFraction = 0.5;

    /// <summary>The fragments with an identifier of the search that go first, at most (ADR-0047, addendum of 2026-11-04, second).</summary>
    public const int DefaultMaxIdentifierHits = 3;

    // Before Default: a static initializer runs in the order of the text.
    private static readonly IReadOnlyDictionary<string, MeaningOnlyCut> CalibratedCuts = new Dictionary<string, MeaningOnlyCut>(StringComparer.Ordinal)
    {
        // multilingual-e5-small: a floor and a margin below the best similarity of the search (ADR-0047, addenda of
        // 2026-11-03 and 2026-11-04).
        [EmbeddingModels.MultilingualE5Small.Fingerprint()] = new MeaningOnlyCut(0.79f, 0.04f),
    };

    /// <summary>The search as calibrated.</summary>
    public static SemanticSearchTuning Default { get; } = new();

    /// <summary>The cut of each model, by its fingerprint (<see cref="EmbeddingModelDescriptor.Fingerprint"/>).</summary>
    public IReadOnlyDictionary<string, MeaningOnlyCut> Cuts { get; init; } = CalibratedCuts;

    /// <summary>
    /// The largest share of the fragments of the project a term of the search may be in and still go to the lexical list
    /// (counted over what is anchored, as FTS5 would count it); null for no ceiling.
    /// </summary>
    public double? CommonTermFraction { get; init; } = DefaultCommonTermFraction;

    /// <summary>
    /// The words the lexical list of a search leaves out, folded (<see cref="JournalTemplate.Fold"/>): the fixed words of
    /// the template of the journal (<see cref="JournalTemplate.Words"/>). A search made only of them still searches by them.
    /// </summary>
    public IReadOnlySet<string> TemplateWords { get; init; } = JournalTemplate.Words;

    /// <summary>How many fragments with an identifier of the search go first, at most; 0 for none.</summary>
    public int MaxIdentifierHits { get; init; } = DefaultMaxIdentifierHits;

    /// <summary>The cut of the model with <paramref name="modelFingerprint"/>; null when the table has none for it.</summary>
    public MeaningOnlyCut? CutFor(string modelFingerprint)
    {
        ArgumentNullException.ThrowIfNull(modelFingerprint);
        return Cuts.GetValueOrDefault(modelFingerprint);
    }
}
