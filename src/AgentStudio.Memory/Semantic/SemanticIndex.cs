using System.Collections.Concurrent;
using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Memory.Semantic;

/// <summary>
/// The semantic index of the projects (docs/17 D-01 to D-06; ADR-0047, decisions 5 to 9, and its addenda of 2026-11-03 and
/// 2026-11-04): a local projection of the anchored memory, ranked by words and by meaning, that never decides the trust of
/// anything.
/// <list type="bullet">
/// <item><b>What it holds.</b> Every memory entry the log anchors and nobody rejected or retracted, and that no trusted entry
/// supersedes: the journal entries (<see cref="MemoryKind.JournalEntry"/>) and the summaries of the log the derivation
/// writes as system memory (plans, verdicts, deliveries, alerts) included. Each entry is cut by <see cref="FragmentCutter"/>
/// (the cut of an entry is kept by its id, its SHA-256 and its time, so it is computed once) and cites its id and the
/// SHA-256 its <c>MemoryRecorded</c> announced. The memory is already redacted and limited when it is recorded, so nothing
/// else is read: no source code, no transcript, no raw evidence, nothing outside the project. No fragment cites an entry of
/// the log directly (<see cref="FragmentSourceKind.LogEntry"/>): what the system writes from the log is already memory, so
/// <see cref="SemanticQuery.IncludeLog"/> changes nothing.</item>
/// <item><b>The refresh</b> brings the index to what is anchored now: gone fragments out, new, changed or unreadable ones in
/// (first without a vector, so the words find them at once), and then, with the local model, the vectors that are missing
/// or are of another model, newest first, in batches of <see cref="EmbeddingBatch"/>. One refresh of a project at a time;
/// a cancelled one leaves what it wrote, and the next one goes on from there.</item>
/// <item><b>The search</b> (D-05): the lexical list is FTS5 and <c>bm25</c> over the terms of <see cref="LexicalTerms"/>
/// (without function words, and without those in more than <see cref="SemanticSearchTuning.CommonTermFraction"/> of the
/// fragments); with the model and at least one vector of it, the semantic list is the cosine with the <c>query:</c>
/// embedding, and only what passes the cut of its model has a place in it (<see cref="SemanticSearchTuning.CutFor"/>, by the
/// fingerprint of the model: a floor, and a margin below the best similarity of the search), whether the words find it too
/// or not. So a fragment that only the meaning finds enters only above the cut, what the words find always enters by its
/// lexical place, and a word in common never buys a place by meaning. A model the table does not know has no cut, and the
/// result says so (<see cref="SemanticSearchResult.Cut"/>). Each list keeps its first <see cref="ListDepth"/> fragments among
/// what the query admits, and both are fused by <see cref="HybridRanking.Fuse"/> (k = 60, the same weight, ties by the time
/// of the entry and the id). An entry of the journal is searched by words over its whole text and by meaning over its clean
/// view (<see cref="FragmentCutter.MeaningOf"/>). Without the model, or before any vector of it, it goes by words only and
/// says why (<see cref="SemanticSearchResult.Notice"/>). Several searches
/// (<see cref="SearchManyAsync"/>, the tiers of a pack) share one reading of what is anchored and of the index, and each
/// text is embedded once. A search first adds to the index, without vectors, the anchored fragments it does not hold (or
/// holds unreadable), so what was anchored after the last refresh is still found by its words.</item>
/// <item><b>Every hit is checked now</b> (F12-2): its fragment must be one the anchored entry gives today (the same id and
/// the same fingerprint, cut from the entry recall returns now), and the index must hold exactly that fragment. A fragment
/// of an entry not announced, rejected, retracted, superseded or changed, or one the index holds with another text, never
/// comes out. One hit per entry, at the place of its best fragment; its text and its entry are those of the anchored source,
/// never the index's. What the index holds with another text, or cannot read, is written again as its entry gives it; what
/// it no longer holds (a refresh removed it meanwhile) is left as the refresh left it. No search ever fails for one row. A
/// search with <see cref="SemanticQuery.TrustedOnly"/>, and the snapshot of the planner's tool, give only what may reach a
/// prompt (<see cref="SemanticQuery.MayReachAPrompt"/>): never a daily summary, whatever its origin.</item>
/// <item><b>Fail closed.</b> On a log that does not verify a search throws <see cref="DecisionLogIntegrityException"/>, as
/// recall does, and a refresh writes nothing and says why.</item>
/// <item><b>Never for a judge nor an auditor</b> (F12-8): the context packer does not ask for them, and nothing else gives
/// them the index.</item>
/// </list>
/// </summary>
/// <param name="model">The model the vectors of the index are counted against in <see cref="StatusAsync"/> and given with
/// in <see cref="SnapshotAsync"/>: <see cref="EmbeddingModels.MultilingualE5Small"/> unless another is given. A refresh and a
/// search use the model of the embedder they open, and its cut.</param>
public sealed class SemanticIndex(IProjectMemory memory, ISemanticIndexStore store, IEmbedderSource embedders, TimeProvider timeProvider, EmbeddingModelDescriptor? model = null, SemanticSearchTuning? tuning = null) : ISemanticIndex
{
    /// <summary>How many places of each list a search reads from the store at first: enough for the filters (categories, trust).</summary>
    public const int CandidatePool = 1000;

    /// <summary>
    /// How many places of each list go to the fusion once filtered: the 50 best fragments by words and the 50 nearest by
    /// meaning among what may come out, the depth the spike measured (docs/investigacion/2026-10-30-spike-embeddings.md).
    /// </summary>
    public const int ListDepth = 50;

    /// <summary>How many fragments a refresh embeds and writes at a time.</summary>
    public const int EmbeddingBatch = 8;

    /// <summary>How many candidates a search checks against the index at a time.</summary>
    public const int VerificationBatch = 32;

    /// <summary>How many anchored entries the cache of their cuts keeps before it starts again.</summary>
    public const int MaxCachedEntries = 100_000;

    /// <summary>Why a refresh wrote nothing: the decision log does not verify.</summary>
    public const string UnverifiedLogReason = "El registro de decisiones no verifica: el índice no se actualiza hasta que verifique.";

    private readonly EmbeddingModelDescriptor _model = model ?? EmbeddingModels.MultilingualE5Small;
    private readonly SemanticSearchTuning _tuning = tuning ?? SemanticSearchTuning.Default;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Id, string Sha256, long At), IReadOnlyList<SemanticFragment>> _cuts = new();

    /// <summary>Why a refresh stopped before computing every vector: the model failed on <paramref name="left"/> fragments.</summary>
    public static string EmbeddingFailedReason(int left) => left == 1
        ? "El modelo no pudo calcular el significado de 1 fragmento: la búsqueda lo encuentra solo por sus palabras hasta la próxima actualización."
        : string.Create(CultureInfo.InvariantCulture, $"El modelo no pudo calcular el significado de {left} fragmentos: la búsqueda los encuentra solo por sus palabras hasta la próxima actualización.");

    public async Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var gate = _gates.GetOrAdd(project.ProjectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Anchored anchored;
            try
            {
                anchored = await AnchoredAsync(project, ct).ConfigureAwait(false);
            }
            catch (DecisionLogIntegrityException)
            {
                return new SemanticIndexRefresh(0, 0, 0, 0, UnverifiedLogReason);
            }

            var held = await HeldAsync(project, ct).ConfigureAwait(false);
            var gone = held.Keys.Where(id => !anchored.ById.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
            if (gone.Count > 0)
            {
                await store.RemoveAsync(project, gone, ct).ConfigureAwait(false);
            }

            var added = Missing(anchored, held);
            if (added.Count > 0)
            {
                await store.UpsertAsync(project, [.. added.Select(f => new IndexedFragment(f.Fragment))], ct).ConfigureAwait(false);
            }

            var addedIds = added.Select(f => f.Fragment.Id).ToHashSet(StringComparer.Ordinal);
            var opening = await embedders.OpenAsync(ct).ConfigureAwait(false);
            if (opening.Embedder is not { } embedder)
            {
                return new SemanticIndexRefresh(added.Count, gone.Count, 0, anchored.Fragments.Count - added.Count);
            }

            var fingerprint = VectorFingerprint(embedder.Model);
            var pending = anchored.Fragments
                .Where(f => addedIds.Contains(f.Fragment.Id) || !string.Equals(held[f.Fragment.Id].ModelFingerprint, fingerprint, StringComparison.Ordinal))
                .ToList();
            var embedded = new HashSet<string>(StringComparer.Ordinal);
            string? reason = null;
            foreach (var batch in pending.Chunk(EmbeddingBatch))
            {
                var vectors = await EmbedAsync(embedder, [.. batch.Select(f => FragmentCutter.MeaningOf(f.Entry, f.Fragment))], EmbeddingRole.Passage, ct).ConfigureAwait(false);
                if (vectors is null)
                {
                    reason = EmbeddingFailedReason(pending.Count - embedded.Count);
                    break;
                }

                await store.UpsertAsync(project, [.. batch.Select((f, i) => new IndexedFragment(f.Fragment, vectors[i], fingerprint))], ct).ConfigureAwait(false);
                embedded.UnionWith(batch.Select(f => f.Fragment.Id));
            }

            var unchanged = anchored.Fragments.Count(f => !addedIds.Contains(f.Fragment.Id) && !embedded.Contains(f.Fragment.Id));
            return new SemanticIndexRefresh(added.Count, gone.Count, embedded.Count, unchanged, reason);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct) =>
        (await SearchManyAsync(project, [query], ct).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchManyAsync(ProjectContext project, IReadOnlyList<SemanticQuery> queries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(queries);
        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(query.Text);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Top);
        }

        if (queries.Count == 0)
        {
            return [];
        }

        var reading = await ReadingAsync(project, ct).ConfigureAwait(false);
        var texts = new Dictionary<string, TextLists>(StringComparer.Ordinal);
        var altered = new Dictionary<string, SemanticFragment>(StringComparer.Ordinal);
        var results = new List<SemanticSearchResult>(queries.Count);
        foreach (var query in queries)
        {
            if (!texts.TryGetValue(query.Text, out var lists))
            {
                lists = await ListsAsync(project, reading, query.Text, ct).ConfigureAwait(false);
                texts.Add(query.Text, lists);
            }

            var lexical = await LexicalAsync(project, reading, lists, query, ct).ConfigureAwait(false);
            var semantic = await SemanticAsync(project, reading, lists, query, ct).ConfigureAwait(false);
            var fused = HybridRanking.Fuse(lexical, semantic);
            var hits = await CheckedHitsAsync(project, reading.Anchored, fused, query, altered, ct).ConfigureAwait(false);
            results.Add(new SemanticSearchResult(hits, lists.Reason is null ? SemanticSearchMode.Hybrid : SemanticSearchMode.WordsOnly, lists.Reason) { Cut = lists.Meaning?.Cut });
        }

        if (altered.Count > 0)
        {
            // The index holds another text for these fragments, or cannot read them: they go back to what their anchored
            // entry gives, without a vector (the next refresh computes it again), so the words find them from now on.
            await store.RemoveAsync(project, [.. altered.Keys], ct).ConfigureAwait(false);
            await store.UpsertAsync(project, [.. altered.Values.Select(f => new IndexedFragment(f))], ct).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var held = await HeldAsync(project, ct).ConfigureAwait(false);
        var fingerprint = VectorFingerprint(_model);
        return new SemanticIndexStatus(held.Count, held.Values.Count(h => string.Equals(h.ModelFingerprint, fingerprint, StringComparison.Ordinal)), embedders.Check());
    }

    public async Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFragments);

        // Only what may reach a prompt: trusted, not superseded, and never a daily summary (ADR-0023).
        var anchored = await AnchoredAsync(project, ct).ConfigureAwait(false);
        var chosen = anchored.Fragments
            .Where(f => SemanticQuery.MayReachAPrompt(f.Entry))
            .OrderByDescending(f => f.Fragment.At)
            .ThenBy(f => f.Fragment.Id, StringComparer.Ordinal)
            .Take(Math.Min(maxFragments, ProjectHistoryNames.MaxFragments))
            .ToList();
        var held = new Dictionary<string, IndexedFragment>(StringComparer.Ordinal);
        if (chosen.Count > 0)
        {
            foreach (var indexed in await store.ReadIndexedAsync(project, [.. chosen.Select(f => f.Fragment.Id)], ct).ConfigureAwait(false))
            {
                held.TryAdd(indexed.Fragment.Id, indexed);
            }
        }

        var fingerprint = VectorFingerprint(_model);
        var fragments = chosen
            .Select(f => new HistoryFragment(f.Fragment.Id, SemanticLabels.Of(f.Entry.Kind), f.Fragment.At, f.Fragment.Text, VectorOf(held, f.Fragment, fingerprint)))
            .ToList();
        var withVectors = fragments.Any(f => f.Vector is not null);
        return new ProjectHistorySnapshot(timeProvider.GetUtcNow(), withVectors ? fingerprint : null, withVectors ? _model.Dimensions : 0, fragments);
    }

    public async Task DeleteAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var gate = _gates.GetOrAdd(project.ProjectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await store.DeleteAsync(project, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The fingerprint the vectors of the index carry: the model and the version of the view the meaning of an entry of the
    /// journal is computed from (<see cref="JournalSearchText.Version"/>), so a new view, like a new model, computes them
    /// again and they are never compared with the old ones.
    /// </summary>
    public static string VectorFingerprint(EmbeddingModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ContentHash.Sha256Hex(ContentHash.Canonical(
            "agent-studio/semantic-vectors/1",
            model.Fingerprint(),
            JournalSearchText.Version.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The terms of <paramref name="terms"/> that are in at most <see cref="SemanticSearchTuning.CommonTermFraction"/> of the
    /// fragments the index holds, counted by the lexical search itself (as FTS5 counts them), and only as far as the ceiling:
    /// a term in more is the template of the journal or a word of every entry, and it only brings noise. All of them without
    /// a ceiling, or when it would drop them all (a search made only of such words still searches by them).
    /// </summary>
    private async Task<IReadOnlyList<string>> UncommonAsync(ProjectContext project, Reading reading, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (_tuning.CommonTermFraction is not { } fraction || terms.Count == 0 || reading.Size == 0)
        {
            return terms;
        }

        var ceiling = (int)Math.Floor(fraction * reading.Size);
        var uncommon = new List<string>(terms.Count);
        foreach (var term in terms)
        {
            if ((await store.SearchWordsAsync(project, [term], ceiling + 1, ct).ConfigureAwait(false)).Count <= ceiling)
            {
                uncommon.Add(term);
            }
        }

        return uncommon.Count > 0 ? uncommon : terms;
    }

    private static bool Admits(SemanticQuery query, MemoryEntry entry) =>
        (query.Kinds is null || query.Kinds.Contains(entry.Kind)) && (!query.TrustedOnly || SemanticQuery.MayReachAPrompt(entry));

    /// <summary>
    /// The anchored fragments the index does not hold, or holds with another fingerprint (an unreadable row is held without
    /// one), in the order of <paramref name="anchored"/>.
    /// </summary>
    private static List<AnchoredFragment> Missing(Anchored anchored, Dictionary<string, IndexedFragmentState> held) =>
        [.. anchored.Fragments.Where(f => !held.TryGetValue(f.Fragment.Id, out var state) || !string.Equals(state.Sha256, f.Fragment.Sha256, StringComparison.Ordinal))];

    /// <summary>The vector the index holds for <paramref name="fragment"/> with the model <paramref name="fingerprint"/>, if it holds exactly that fragment.</summary>
    private float[]? VectorOf(Dictionary<string, IndexedFragment> held, SemanticFragment fragment, string fingerprint) =>
        held.TryGetValue(fragment.Id, out var indexed)
        && string.Equals(indexed.Fragment.Sha256, fragment.Sha256, StringComparison.Ordinal)
        && string.Equals(indexed.ModelFingerprint, fingerprint, StringComparison.Ordinal)
        && indexed.Vector is { } vector
        && vector.Length == _model.Dimensions
            ? vector
            : null;

    private static WordsOnlyReason ReasonOf(EmbeddingModelState state) => state switch
    {
        EmbeddingModelState.Missing => WordsOnlyReason.ModelMissing,
        EmbeddingModelState.Mismatch => WordsOnlyReason.ModelMismatch,
        _ => WordsOnlyReason.ModelUnloadable,
    };

    /// <summary>
    /// The vectors of <paramref name="texts"/>, one per text and of the dimensions of the model; null when the model failed
    /// (anything but a cancellation) or gave something else.
    /// </summary>
    private static async Task<IReadOnlyList<float[]>?> EmbedAsync(IEmbedder embedder, IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await embedder.EmbedAsync(texts, role, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return vectors is not null && vectors.Count == texts.Count && vectors.All(v => v is not null && v.Length == embedder.Model.Dimensions) ? vectors : null;
    }

    /// <summary>
    /// One reading for a batch of searches: what is anchored now, what the index holds (with what it lacked written in,
    /// without vectors), and the local model when its vectors can be used, or why not.
    /// </summary>
    private async Task<Reading> ReadingAsync(ProjectContext project, CancellationToken ct)
    {
        var anchored = await AnchoredAsync(project, ct).ConfigureAwait(false);
        var held = await HeldAsync(project, ct).ConfigureAwait(false);
        var missing = Missing(anchored, held);
        if (missing.Count > 0)
        {
            await store.UpsertAsync(project, [.. missing.Select(f => new IndexedFragment(f.Fragment))], ct).ConfigureAwait(false);
        }

        var size = held.Count + missing.Count;
        var opening = await embedders.OpenAsync(ct).ConfigureAwait(false);
        if (opening.Embedder is not { } embedder)
        {
            return new Reading(anchored, size, null, ReasonOf(opening.Status.State));
        }

        var fingerprint = VectorFingerprint(embedder.Model);
        return held.Values.Any(h => string.Equals(h.ModelFingerprint, fingerprint, StringComparison.Ordinal))
            ? new Reading(anchored, size, embedder, null)
            : new Reading(anchored, size, null, WordsOnlyReason.VectorsPending);
    }

    /// <summary>
    /// The two lists of <paramref name="text"/> as the store gives them (their first <see cref="CandidatePool"/> places), the
    /// text embedded once as a query, or why there is no semantic list. A blank text has neither list.
    /// </summary>
    private async Task<TextLists> ListsAsync(ProjectContext project, Reading reading, string text, CancellationToken ct)
    {
        var terms = await UncommonAsync(project, reading, LexicalTerms.From(text), ct).ConfigureAwait(false);
        var lexical = terms.Count == 0 ? [] : await store.SearchWordsAsync(project, terms, CandidatePool, ct).ConfigureAwait(false);
        if (reading.Embedder is not { } embedder || string.IsNullOrWhiteSpace(text))
        {
            return new TextLists(terms, lexical, null, [], reading.Reason);
        }

        var vectors = await EmbedAsync(embedder, [text.Trim()], EmbeddingRole.Query, ct).ConfigureAwait(false);
        if (vectors is null)
        {
            return new TextLists(terms, lexical, null, [], WordsOnlyReason.ModelUnloadable);
        }

        var meaning = new QueryMeaning(vectors[0], VectorFingerprint(embedder.Model), _tuning.CutFor(embedder.Model.Fingerprint()));
        var semantic = await store.SearchVectorsAsync(project, meaning.Vector, meaning.Fingerprint, CandidatePool, ct).ConfigureAwait(false);
        return new TextLists(terms, lexical, meaning, semantic, null);
    }

    /// <summary>
    /// The lexical list of <paramref name="query"/>: its first <see cref="ListDepth"/> admitted places. The store filters
    /// neither by category nor by trust, so when its first <see cref="CandidatePool"/> places leave fewer and there may be
    /// more, the whole list is read once (and kept for the other queries of the same text): a category the rest of a large
    /// index outnumbers still has its candidates.
    /// </summary>
    private async Task<List<RankedCandidate>> LexicalAsync(ProjectContext project, Reading reading, TextLists lists, SemanticQuery query, CancellationToken ct)
    {
        const int depth = ListDepth;
        var admitted = Admitted(lists.Lexical.Select(c => (c.Id, Similarity: 1f)), reading.Anchored, query, float.NegativeInfinity, depth);
        if (admitted.Count < depth && lists.Lexical.Count >= CandidatePool && reading.Size > CandidatePool)
        {
            lists.WholeLexical ??= await store.SearchWordsAsync(project, lists.Terms, reading.Size, ct).ConfigureAwait(false);
            admitted = Admitted(lists.WholeLexical.Select(c => (c.Id, Similarity: 1f)), reading.Anchored, query, float.NegativeInfinity, depth);
        }

        return admitted;
    }

    /// <summary>
    /// The semantic list of <paramref name="query"/>: its first <see cref="ListDepth"/> admitted places that reach the cut of
    /// the model, whether the lexical list has them too or not: its floor and, when it has a margin, within it of the best
    /// similarity of the search among what its trust admits, of any category. Without a cut for the model everything the
    /// meaning found may enter, and the result says the search had none. Read whole once when the first places leave too few
    /// and the last of them still reaches the cut (the list goes from the best), as the lexical list.
    /// </summary>
    private async Task<List<RankedCandidate>> SemanticAsync(ProjectContext project, Reading reading, TextLists lists, SemanticQuery query, CancellationToken ct)
    {
        if (lists.Meaning is not { } meaning)
        {
            return [];
        }

        var minimum = meaning.Cut?.Minimum ?? float.NegativeInfinity;
        if (meaning.Cut?.Margin is { } margin)
        {
            var best = lists.Semantic
                .Where(c => reading.Anchored.ById.TryGetValue(c.Id, out var expected) && (!query.TrustedOnly || SemanticQuery.MayReachAPrompt(expected.Entry)))
                .Select(c => c.Similarity)
                .DefaultIfEmpty(float.NegativeInfinity)
                .First();
            minimum = Math.Max(minimum, best - margin);
        }

        var admitted = Admitted(lists.Semantic.Select(c => (c.Id, c.Similarity)), reading.Anchored, query, minimum, ListDepth);
        if (admitted.Count < ListDepth && lists.Semantic.Count >= CandidatePool && reading.Size > CandidatePool && lists.Semantic[^1].Similarity >= minimum)
        {
            lists.WholeSemantic ??= await store.SearchVectorsAsync(project, meaning.Vector, meaning.Fingerprint, reading.Size, ct).ConfigureAwait(false);
            admitted = Admitted(lists.WholeSemantic.Select(c => (c.Id, c.Similarity)), reading.Anchored, query, minimum, ListDepth);
        }

        return admitted;
    }

    /// <summary>
    /// The first <paramref name="depth"/> places of a list whose fragment an anchored entry gives now, the query admits and
    /// whose similarity reaches <paramref name="minimum"/>, in their order and with the time of the anchored entry (each list
    /// is ranked among what may come out before it is fused, and the index never decides a tie).
    /// </summary>
    private static List<RankedCandidate> Admitted(IEnumerable<(string Id, float Similarity)> list, Anchored anchored, SemanticQuery query, float minimum, int depth)
    {
        var admitted = new List<RankedCandidate>();
        foreach (var (id, similarity) in list)
        {
            if (!anchored.ById.TryGetValue(id, out var expected) || !Admits(query, expected.Entry) || similarity < minimum)
            {
                continue;
            }

            admitted.Add(new RankedCandidate(id, expected.Fragment.At));
            if (admitted.Count == depth)
            {
                break;
            }
        }

        return admitted;
    }

    /// <summary>
    /// The hits of <paramref name="fused"/> (already admitted), in its order and at most <see cref="SemanticQuery.Top"/>: only
    /// fragments the index holds exactly as their anchored entry gives them now, one per entry. The fragments it holds with
    /// another text, or cannot read, go to <paramref name="altered"/>, as their entry gives them.
    /// </summary>
    private async Task<IReadOnlyList<SemanticHit>> CheckedHitsAsync(
        ProjectContext project,
        Anchored anchored,
        IReadOnlyList<FusedCandidate> fused,
        SemanticQuery query,
        Dictionary<string, SemanticFragment> altered,
        CancellationToken ct)
    {
        var hits = new List<SemanticHit>();
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<(FusedCandidate Rank, AnchoredFragment Expected)>(VerificationBatch);
        foreach (var candidate in fused)
        {
            var expected = anchored.ById[candidate.Id];
            if (entries.Contains(expected.Entry.Id))
            {
                continue;
            }

            batch.Add((candidate, expected));
            if (batch.Count == VerificationBatch)
            {
                await CheckAsync(project, batch, hits, altered, entries, query.Top, ct).ConfigureAwait(false);
                batch.Clear();
                if (hits.Count == query.Top)
                {
                    return hits;
                }
            }
        }

        if (batch.Count > 0)
        {
            await CheckAsync(project, batch, hits, altered, entries, query.Top, ct).ConfigureAwait(false);
        }

        return hits;
    }

    /// <summary>
    /// Adds to <paramref name="hits"/> the candidates of <paramref name="batch"/> the index holds exactly as their anchored
    /// entry gives them, and to <paramref name="altered"/> those it holds with another text or cannot read. A candidate the
    /// index no longer holds was removed by a refresh meanwhile: it is neither a hit nor written again.
    /// </summary>
    private async Task CheckAsync(
        ProjectContext project,
        IReadOnlyList<(FusedCandidate Rank, AnchoredFragment Expected)> batch,
        List<SemanticHit> hits,
        Dictionary<string, SemanticFragment> altered,
        HashSet<string> entries,
        int top,
        CancellationToken ct)
    {
        var (stored, unreadable) = await StoredAsync(project, [.. batch.Select(b => b.Rank.Id)], ct).ConfigureAwait(false);
        foreach (var (rank, expected) in batch)
        {
            if (!stored.TryGetValue(rank.Id, out var held))
            {
                if (unreadable.Contains(rank.Id))
                {
                    altered.TryAdd(rank.Id, expected.Fragment);
                }

                continue;
            }

            if (!string.Equals(held.Sha256, expected.Fragment.Sha256, StringComparison.Ordinal))
            {
                altered.TryAdd(rank.Id, expected.Fragment);
                continue;
            }

            if (hits.Count < top && entries.Add(expected.Entry.Id))
            {
                hits.Add(new SemanticHit(expected.Fragment, SemanticLabels.Of(expected.Entry.Kind), expected.Entry, rank));
            }
        }
    }

    /// <summary>
    /// The fragments the index holds with these ids, and the ids whose row it could not read. The store skips a row that is
    /// not a fragment; one that fails on it instead is read again an id at a time, so one row never fails the search.
    /// </summary>
    private async Task<(Dictionary<string, SemanticFragment> Stored, HashSet<string> Unreadable)> StoredAsync(ProjectContext project, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var stored = new Dictionary<string, SemanticFragment>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var fragment in await store.ReadAsync(project, ids, ct).ConfigureAwait(false))
            {
                stored.TryAdd(fragment.Id, fragment);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
        {
            foreach (var id in ids)
            {
                try
                {
                    foreach (var fragment in await store.ReadAsync(project, [id], ct).ConfigureAwait(false))
                    {
                        stored.TryAdd(fragment.Id, fragment);
                    }
                }
                catch (Exception rowEx) when (rowEx is InvalidDataException or ArgumentException or FormatException)
                {
                    unreadable.Add(id);
                }
            }
        }

        return (stored, unreadable);
    }

    /// <summary>The id, fingerprint and model of every fragment the index holds, by id (the first of a repeated id counts).</summary>
    private async Task<Dictionary<string, IndexedFragmentState>> HeldAsync(ProjectContext project, CancellationToken ct)
    {
        var held = new Dictionary<string, IndexedFragmentState>(StringComparer.Ordinal);
        foreach (var state in await store.ReadStateAsync(project, ct).ConfigureAwait(false))
        {
            held.TryAdd(state.Id, state);
        }

        return held;
    }

    /// <summary>
    /// The anchored entries the index may hold now, as recall gives them (newest first, with their trust): every one but the
    /// rejected, the retracted and the superseded, each cut into its fragments (from the cache when it was cut before).
    /// Throws on a log that does not verify.
    /// </summary>
    private async Task<Anchored> AnchoredAsync(ProjectContext project, CancellationToken ct)
    {
        var entries = await memory.RecallAsync(project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: false), ct).ConfigureAwait(false);
        var fragments = new List<AnchoredFragment>(entries.Count);
        var byId = new Dictionary<string, AnchoredFragment>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Trust is MemoryTrust.Rejected or MemoryTrust.Retracted || entry.SupersededBy is not null)
            {
                continue;
            }

            foreach (var fragment in CutOf(entry))
            {
                var anchoredFragment = new AnchoredFragment(fragment, entry);
                if (byId.TryAdd(fragment.Id, anchoredFragment))
                {
                    fragments.Add(anchoredFragment);
                }
            }
        }

        return new Anchored(fragments, byId);
    }

    /// <summary>
    /// The fragments of <paramref name="entry"/>, cut once: an anchored entry never changes under its id, its SHA-256 and its
    /// time, and cutting is deterministic.
    /// </summary>
    private IReadOnlyList<SemanticFragment> CutOf(MemoryEntry entry)
    {
        var key = (entry.Id, entry.ContentSha256, entry.CreatedAt.UtcTicks);
        if (_cuts.TryGetValue(key, out var cut))
        {
            return cut;
        }

        cut = FragmentCutter.Of(entry);
        if (_cuts.Count >= MaxCachedEntries)
        {
            _cuts.Clear();
        }

        _cuts.TryAdd(key, cut);
        return cut;
    }

    /// <summary>A fragment as its anchored entry gives it now, with that entry.</summary>
    private sealed record AnchoredFragment(SemanticFragment Fragment, MemoryEntry Entry);

    /// <summary>The fragments of the anchored entries, newest entry first and each in its parts' order, and the same by id.</summary>
    private sealed record Anchored(IReadOnlyList<AnchoredFragment> Fragments, IReadOnlyDictionary<string, AnchoredFragment> ById);

    /// <summary>
    /// One reading for a batch of searches: what is anchored, how many fragments the index holds, and the local model when its
    /// vectors can be used, or why not.
    /// </summary>
    private sealed record Reading(Anchored Anchored, int Size, IEmbedder? Embedder, WordsOnlyReason? Reason);

    /// <summary>The embedding of a search, the fingerprint of the vectors it is compared with, and the cut of its model (null without one).</summary>
    private sealed record QueryMeaning(float[] Vector, string Fingerprint, MeaningOnlyCut? Cut);

    /// <summary>The two lists of one text of a batch, as the store gave them, and the whole ones once they were needed.</summary>
    private sealed class TextLists(IReadOnlyList<string> terms, IReadOnlyList<RankedCandidate> lexical, QueryMeaning? meaning, IReadOnlyList<SimilarCandidate> semantic, WordsOnlyReason? reason)
    {
        public IReadOnlyList<string> Terms { get; } = terms;

        public IReadOnlyList<RankedCandidate> Lexical { get; } = lexical;

        public IReadOnlyList<RankedCandidate>? WholeLexical { get; set; }

        public QueryMeaning? Meaning { get; } = meaning;

        public IReadOnlyList<SimilarCandidate> Semantic { get; } = semantic;

        public IReadOnlyList<SimilarCandidate>? WholeSemantic { get; set; }

        public WordsOnlyReason? Reason { get; } = reason;
    }
}
