using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The calibration of the hybrid search with the real model (ADR-0047, addenda of 2026-11-03 and 2026-11-04), on the question
/// set of F12-4 and its distractors (twelve unrelated entries in each tier by relevance) and on nothing else. For each
/// setting it measures how many answers come among the first five (F12-4), those of the journal and those of the rest of
/// the memory apart, and the noise of the tiers by relevance of a pack: the distractors they bring, and every entry that is
/// not the answer. The settings are those of <see cref="SemanticSearchTuning"/>: the cut of the semantic list and the share
/// of the fragments above which a term of the question is ignored (the two lists are fused as D-05 says, with the same weight
/// and k = 60). The tuning of the index must keep the answers F12-4 has without a cut, bring less noise, and lose none of
/// the answers FTS5 finds as the memory searches today. It also leaves where each answer that is not among the first five
/// is, in each list. Skipped without the model; it never downloads it.
/// </summary>
[Trait("Category", "LocalModel")]
public sealed class MeaningOnlyFloorCalibrationTests
{
    /// <summary>The cuts measured, null for none: the floor alone, the margin below the best similarity alone, and both.</summary>
    private static readonly MeaningOnlyCut?[] Cuts =
    [
        null,
        new(0.78f), new(0.79f), new(0.80f),
        new(0.78f, 0.04f), new(0.79f, 0.03f), new(0.79f, 0.04f), new(0.79f, 0.05f), new(0.80f, 0.04f),
    ];

    /// <summary>The shares of the fragments above which a term of the question is ignored, null for no ceiling.</summary>
    private static readonly double?[] CommonTermFractions = [null, 0.5, 0.25];

    /// <summary>How many fragments with an identifier of the question go first: 0 is the fusion alone.</summary>
    private static readonly int[] IdentifierHits = [0, 1, 2, 3, 5];

    /// <summary>The words of the template the lexical list leaves out: none, the keywords, with the states, with the CLIs (the index's), and all.</summary>
    private static readonly (string Label, IReadOnlySet<string> Words)[] TemplateLists =
    [
        ("ninguna", new HashSet<string>(StringComparer.Ordinal)),
        ("palabras clave", JournalTemplate.FoldAll(JournalTemplate.Keywords)),
        ("palabras clave y estados", JournalTemplate.FoldAll([.. JournalTemplate.Keywords, .. JournalTemplate.Statuses])),
        ("palabras clave y CLI (la del índice)", JournalTemplate.Words),
        ("palabras clave, estados y CLI", JournalTemplate.FoldAll([.. JournalTemplate.Keywords, .. JournalTemplate.Statuses, .. JournalTemplate.Providers])),
    ];

    private static readonly (ContextTierName Tier, MemoryKind[] Kinds)[] Tiers =
    [
        (ContextTierName.KnownIssue, [MemoryKind.KnownError, MemoryKind.Solution]),
        (ContextTierName.Other, [MemoryKind.Change, MemoryKind.Verdict, MemoryKind.DebateSummary]),
        (ContextTierName.History, [MemoryKind.JournalEntry]),
    ];

    private enum ContextTierName
    {
        KnownIssue,
        Other,
        History,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_tuning_of_the_index_keeps_the_answers_and_cuts_the_noise_of_every_tier()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipUnless(LocalModel.IsOnDisk(local, out var directory), $"El modelo multilingual-e5-small no está en {directory}: la calibración se hace cuando esté (una prueba nunca lo descarga).");
        using var model = LocalModel.Open(local);
        Assert.SkipWhen(model is null, $"Los ficheros de {directory} no dan su SHA-256 fijado: no se cargan.");

        var set = PreviousDays.Load();
        using var world = await MemoryWorld.CreateAsync();
        var ids = await set.RecordAsync(world, Ct);
        var memory = new FixedMemory(await world.Memory.RecallAsync(world.Project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: false), Ct));
        var keyOf = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var entries = set.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        using var store = new TestSemanticStore();
        var embedder = new QueryCache((await model!.OpenAsync(Ct)).Embedder!);
        var production = SemanticSearchTuning.Default;
        var refresh = await Index(production).RefreshAsync(world.Project, Ct);
        Assert.True(refresh.Completed, refresh.Reason);

        var today = await PreviousDays.ScoreAsync(ids, set.All, async question =>
            [.. (await world.Memory.RecallAsync(world.Project, new MemoryQuery(question, MaxEntries: PreviousDays.Top), Ct)).Select(e => e.Id)]);
        LocalModel.Report($"FTS5 como hoy: {ByType(set, today.Missed)}; fuera: {string.Join(" ", today.Missed)}");
        LocalModel.Report(await SimilaritiesAsync(set, keyOf, store, embedder, world.Project));
        LocalModel.Report(await FrequenciesAsync(set, store, world.Project));
        var pinned = await MeasureAsync(set, ids, keyOf, entries, Index(production), world.Project);
        var wordsOnly = await MeasureAsync(set, ids, keyOf, entries, Index(production, new MissingSource()), world.Project);
        LocalModel.Report($"el índice tal como queda: {pinned}");
        LocalModel.Report($"el índice sin modelo: {wordsOnly.Brief()}");

        var cuts = new Dictionary<string, Measure>(StringComparer.Ordinal);
        foreach (var cut in Cuts)
        {
            var measure = await MeasureAsync(set, ids, keyOf, entries, Index(production with { Cuts = CutsOf(cut) }), world.Project);
            cuts[Label(cut)] = measure;
            LocalModel.Report($"{Label(cut)}: {measure}");
        }

        foreach (var fraction in CommonTermFractions)
        {
            var tuning = production with { CommonTermFraction = fraction };
            var hybrid = await MeasureAsync(set, ids, keyOf, entries, Index(tuning), world.Project);
            var words = await MeasureAsync(set, ids, keyOf, entries, Index(tuning, new MissingSource()), world.Project);
            LocalModel.Report($"techo de términos comunes {Share(fraction)}: {hybrid.Brief()}{Flips(hybrid, pinned)}; sin modelo: {words.Brief()}{Flips(words, wordsOnly)}");
        }

        foreach (var (label, words) in TemplateLists)
        {
            var hybrid = await MeasureAsync(set, ids, keyOf, entries, Index(production with { TemplateWords = words }), world.Project);
            var alone = await MeasureAsync(set, ids, keyOf, entries, Index(production with { TemplateWords = words }, new MissingSource()), world.Project);
            LocalModel.Report($"plantilla fuera, {label}: {hybrid.Brief()}{Flips(hybrid, pinned)}; sin modelo: {alone.Brief()}{Flips(alone, wordsOnly)}");
        }

        foreach (var hits in IdentifierHits)
        {
            var hybrid = await MeasureAsync(set, ids, keyOf, entries, Index(production with { MaxIdentifierHits = hits }), world.Project);
            LocalModel.Report(string.Create(CultureInfo.InvariantCulture, $"identificadores primero, hasta {hits}: {hybrid}{Flips(hybrid, pinned)}"));
        }

        LocalModel.Report(await RanksAsync(set, ids, Index(production), world.Project));

        // The rule of the calibration: with the tuning of the index, its cut keeps every answer F12-4 has without a cut and
        // cuts the distractors, and the hybrid search loses none of the answers FTS5 finds as the memory searches today, of
        // any type.
        var none = cuts[Label(null)];
        var lost = set.All.Select(q => q.Id).Where(q => !today.Missed.Contains(q) && pinned.Missed.Contains(q)).ToList();
        LocalModel.Report($"de lo que FTS5 como hoy encuentra, la híbrida pierde: {(lost.Count == 0 ? "nada" : string.Join(" ", lost))}");
        Assert.Empty(lost);
        Assert.True(pinned.Found >= none.Found, $"El corte del modelo pierde aciertos: {pinned}; sin corte: {none}");
        Assert.True(pinned.Distractors < none.Distractors * 0.6, $"El corte del modelo no corta el ruido: {pinned}; sin corte: {none}");
        Assert.True(pinned.AnswersInTier >= none.AnswersInTier, $"El corte del modelo saca respuestas de su nivel: {pinned}; sin corte: {none}");

        SemanticIndex Index(SemanticSearchTuning tuning, IEmbedderSource? source = null) =>
            new(memory, store, source ?? new FixedSource(embedder), world.Clock, tuning: tuning);
    }

    /// <summary>How many of each type of question came among the first five, given the ones that did not.</summary>
    private static string ByType(PreviousDays set, IReadOnlyCollection<string> missed) =>
        string.Join(", ", set.All.GroupBy(q => q.Type).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count(q => !missed.Contains(q.Id))}/{g.Count()}")));

    /// <summary>The questions a setting finds and <paramref name="reference"/> does not, and the other way round.</summary>
    private static string Flips(Measure measure, Measure reference)
    {
        var gained = reference.Missed.Except(measure.Missed, StringComparer.Ordinal).ToList();
        var lost = measure.Missed.Except(reference.Missed, StringComparer.Ordinal).ToList();
        return gained.Count == 0 && lost.Count == 0 ? string.Empty : $" (gana {string.Join(" ", gained)}; pierde {string.Join(" ", lost)})";
    }

    /// <summary>The table of cuts with <paramref name="cut"/> for the model, or with none.</summary>
    private static Dictionary<string, MeaningOnlyCut> CutsOf(MeaningOnlyCut? cut) =>
        cut is null ? [] : new(StringComparer.Ordinal) { [EmbeddingModels.MultilingualE5Small.Fingerprint()] = cut };

    private static string Label(MeaningOnlyCut? cut) => cut is null
        ? "sin corte"
        : string.Create(CultureInfo.InvariantCulture, $"suelo {cut.Minimum,5:F2}, margen {(cut.Margin is { } m ? m.ToString("F2", CultureInfo.InvariantCulture) : "—"),4}");

    private static string Share(double? fraction) => fraction is { } f ? f.ToString("P0", CultureInfo.InvariantCulture) : "ninguno";

    /// <summary>For each question whose answer is not among the first five, where it is: its place in the fused list and in each list.</summary>
    private static async Task<string> RanksAsync(PreviousDays set, IReadOnlyDictionary<string, string> ids, SemanticIndex index, ProjectContext project)
    {
        var lines = new List<string> { "puestos de las respuestas que faltan (L: en la lista por palabras, S: en la del significado, I: primera por un identificador; delante, las cinco primeras):" };
        foreach (var question in set.All)
        {
            var all = await index.SearchAsync(project, new SemanticQuery(question.TextWith(ids), 500), Ct);
            var place = all.Hits.Select((h, i) => (h, i)).FirstOrDefault(p => p.h.Entry!.Id == ids[question.Answer]);
            if (place.h is null)
            {
                lines.Add($"{question.Id}: no sale");
                continue;
            }

            if (place.i >= PreviousDays.Top)
            {
                var above = string.Join(" ", all.Hits.Take(PreviousDays.Top).Select(h => $"(L{h.Rank.LexicalRank?.ToString(CultureInfo.InvariantCulture) ?? "-"},S{h.Rank.SemanticRank?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(h.Identifier is null ? string.Empty : ",I")})"));
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{question.Id}: puesto {place.i + 1} (L{place.h.Rank.LexicalRank?.ToString(CultureInfo.InvariantCulture) ?? "-"}, S{place.h.Rank.SemanticRank?.ToString(CultureInfo.InvariantCulture) ?? "-"}); delante: {above}"));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>For each question, its lexical terms that are in more than 5 % of the fragments, with that share: what a ceiling for common terms would drop.</summary>
    private static async Task<string> FrequenciesAsync(PreviousDays set, TestSemanticStore store, ProjectContext project)
    {
        var size = (await store.ReadStateAsync(project, Ct)).Count;
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"fragmentos: {size}") };
        var shares = new List<double>();
        foreach (var question in set.Questions)
        {
            var common = new List<string>();
            foreach (var term in LexicalTerms.From(question.Text))
            {
                var share = (double)(await store.SearchWordsAsync(project, [term], size, Ct)).Count / size;
                shares.Add(share);
                if (share > 0.05)
                {
                    common.Add(string.Create(CultureInfo.InvariantCulture, $"{term} {share:P0}"));
                }
            }

            if (common.Count > 0)
            {
                lines.Add($"{question.Id}: {string.Join(", ", common)}");
            }
        }

        var sorted = shares.Order().ToList();
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"términos de las preguntas: {sorted.Count}; en más del 10 %: {sorted.Count(s => s > 0.10)}, del 20 %: {sorted.Count(s => s > 0.20)}, del 30 %: {sorted.Count(s => s > 0.30)}; el más frecuente, en el {sorted[^1]:P0}"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What comes among the first five for every question of the set, by type, and the noise of the tiers by relevance of a pack for the 50 of F12-4, for one setting.</summary>
    private static async Task<Measure> MeasureAsync(
        PreviousDays set,
        IReadOnlyDictionary<string, string> ids,
        IReadOnlyDictionary<string, string> keyOf,
        IReadOnlyDictionary<string, CorpusEntry> entries,
        SemanticIndex index,
        ProjectContext project)
    {
        var found = 0;
        var journal = 0;
        var journalAsked = 0;
        var distractors = 0;
        var others = 0;
        var characters = 0;
        var answersInTier = 0;
        var answersWithTier = 0;
        var missed = new List<string>();
        foreach (var question in set.ByMetadata)
        {
            var five = await index.SearchAsync(project, new SemanticQuery(question.TextWith(ids), PreviousDays.Top), Ct);
            if (!five.Hits.Any(h => h.Entry!.Id == ids[question.Answer]))
            {
                missed.Add(question.Id);
            }
        }

        foreach (var question in set.Questions)
        {
            var answerKind = entries[question.Answer].Kind;
            journalAsked += answerKind == MemoryKind.JournalEntry ? 1 : 0;
            var five = await index.SearchAsync(project, new SemanticQuery(question.Text, PreviousDays.Top), Ct);
            if (five.Hits.Any(h => h.Entry!.Id == ids[question.Answer]))
            {
                found++;
                journal += answerKind == MemoryKind.JournalEntry ? 1 : 0;
            }
            else
            {
                missed.Add(question.Id);
            }

            var tiers = await index.SearchManyAsync(project, [.. Tiers.Select(t => new SemanticQuery(question.Text, 50) { Kinds = t.Kinds })], Ct);
            for (var i = 0; i < Tiers.Length; i++)
            {
                foreach (var hit in tiers[i].Hits)
                {
                    var key = keyOf[hit.Entry!.Id];
                    if (key == question.Answer)
                    {
                        continue;
                    }

                    others++;
                    characters += hit.Entry.Topic.Length + 2 + hit.Entry.Content.Length;
                    distractors += set.Distractors.Contains(key) ? 1 : 0;
                }

                if (Tiers[i].Kinds.Contains(answerKind))
                {
                    answersWithTier++;
                    answersInTier += tiers[i].Hits.Any(h => h.Entry!.Id == ids[question.Answer]) ? 1 : 0;
                }
            }
        }

        return new Measure(found, set.Questions.Count, journal, journalAsked, distractors, others, characters, answersInTier, answersWithTier, set.Questions.Count * Tiers.Length, missed, ByType(set, missed));
    }

    /// <summary>
    /// For each question, the similarity of its answer, of the best entry that is not the answer and of the best distractor,
    /// and their spread over the set: what tells whether one floor can tell them apart.
    /// </summary>
    private static async Task<string> SimilaritiesAsync(
        PreviousDays set,
        IReadOnlyDictionary<string, string> keyOf,
        TestSemanticStore store,
        QueryCache embedder,
        ProjectContext project)
    {
        var e5 = EmbeddingModels.MultilingualE5Small;
        var answers = new List<float>();
        var bestOthers = new List<float>();
        var bestDistractors = new List<float>();
        var lines = new List<string>();
        var byVectorsAlone = 0;
        foreach (var question in set.Questions)
        {
            var vector = (await embedder.EmbedAsync([question.Text], EmbeddingRole.Query, Ct))[0];

            // The vectors of the journal carry a fingerprint of their own (the version of its view): both lists, merged.
            var list = (await store.SearchVectorsAsync(project, vector, SemanticIndex.VectorFingerprint(e5, MemoryKind.Decision), 10_000, Ct))
                .Concat(await store.SearchVectorsAsync(project, vector, SemanticIndex.VectorFingerprint(e5, MemoryKind.JournalEntry), 10_000, Ct))
                .OrderByDescending(c => c.Similarity)
                .ThenByDescending(c => c.At)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            float Best(Func<string, bool> which) => list.Where(c => which(keyOf[EntryOf(c.Id)])).Select(c => c.Similarity).DefaultIfEmpty(-1f).Max();
            var answer = Best(k => k == question.Answer);
            var other = Best(k => k != question.Answer);
            var distractor = Best(set.Distractors.Contains);
            var rank = list.Select(c => keyOf[EntryOf(c.Id)]).Distinct(StringComparer.Ordinal).ToList().IndexOf(question.Answer) + 1;
            byVectorsAlone += rank is > 0 and <= PreviousDays.Top ? 1 : 0;
            answers.Add(answer);
            bestOthers.Add(other);
            bestDistractors.Add(distractor);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{question.Id}: respuesta {answer:F3} (puesto {rank} por significado) · mejor otra {other:F3} · mejor distractor {distractor:F3} · margen {answer - distractor:+0.000;-0.000}"));
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"solo por significado: {byVectorsAlone}/{set.Questions.Count} entre las cinco primeras"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"respuesta: mín {answers.Min():F3}, p10 {Percentile(answers, 0.10):F3}, mediana {Percentile(answers, 0.5):F3}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"mejor distractor: mediana {Percentile(bestDistractors, 0.5):F3}, p90 {Percentile(bestDistractors, 0.9):F3}, máx {bestDistractors.Max():F3}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"mejor otra: mediana {Percentile(bestOthers, 0.5):F3}, máx {bestOthers.Max():F3}"));
        return string.Join(Environment.NewLine, lines);

        string EntryOf(string fragmentId) => fragmentId[SemanticFragment.MemoryPrefix.Length..fragmentId.IndexOf('#', StringComparison.Ordinal)];
    }

    private static float Percentile(List<float> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Round(p * (sorted.Count - 1))];
    }

    /// <summary>What one setting gives: F12-4 (those of the journal apart), and the noise of the tiers by relevance over every question.</summary>
    private sealed record Measure(
        int Found,
        int Asked,
        int JournalFound,
        int JournalAsked,
        int Distractors,
        int Others,
        int Characters,
        int AnswersInTier,
        int AnswersWithTier,
        int Tiers,
        IReadOnlyList<string> Missed,
        string Types)
    {
        /// <summary>F12-4 and the questions by type alone.</summary>
        public string Brief() => string.Create(
            CultureInfo.InvariantCulture,
            $"F12-4 {Found}/{Asked} (bitácora {JournalFound}/{JournalAsked}, memoria {Found - JournalFound}/{Asked - JournalAsked}); por tipo: {Types}; fuera: {string.Join(" ", Missed)}");

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"F12-4 {Found}/{Asked} (bitácora {JournalFound}/{JournalAsked}, memoria {Found - JournalFound}/{Asked - JournalAsked}); por tipo: {Types}; ruido: {Distractors} distractores ({(double)Distractors / Tiers:F2} por nivel), {Others} entradas que no son la respuesta ({(double)Others / Tiers:F2} por nivel, {Characters / Asked} caracteres por paquete); respuesta en su nivel {AnswersInTier}/{AnswersWithTier}; fuera de las cinco primeras: {string.Join(" ", Missed)}");
    }

    /// <summary>The anchored entries, as recall gave them once: every search reads them without reading the log again.</summary>
    private sealed class FixedMemory(IReadOnlyList<MemoryEntry> entries) : IProjectMemory
    {
        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct) => Task.FromResult(entries);

        public Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct) => throw new InvalidOperationException("The index never writes memory.");

        public Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct) => throw new InvalidOperationException("The index never reviews memory.");
    }

    /// <summary>A source that always opens the same embedder.</summary>
    private sealed class FixedSource(IEmbedder embedder) : IEmbedderSource
    {
        public EmbeddingModelStatus Check() => new(EmbeddingModelState.Ready, "modelo de la calibración");

        public Task<EmbedderOpening> OpenAsync(CancellationToken ct) => Task.FromResult(new EmbedderOpening(embedder, Check()));

        public void Dispose()
        {
        }
    }

    /// <summary>A source without a model: the index searches by words only.</summary>
    private sealed class MissingSource : IEmbedderSource
    {
        public EmbeddingModelStatus Check() => new(EmbeddingModelState.Missing, "sin modelo");

        public Task<EmbedderOpening> OpenAsync(CancellationToken ct) => Task.FromResult(new EmbedderOpening(null, Check()));

        public void Dispose()
        {
        }
    }

    /// <summary>The real embedder, keeping the vector of every query it computed, so each setting asks the same questions without embedding them again.</summary>
    private sealed class QueryCache(IEmbedder inner) : IEmbedder
    {
        private readonly Dictionary<string, float[]> _queries = new(StringComparer.Ordinal);

        public EmbeddingModelDescriptor Model => inner.Model;

        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
        {
            if (role != EmbeddingRole.Query)
            {
                return await inner.EmbedAsync(texts, role, ct);
            }

            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                if (!_queries.TryGetValue(text, out var vector))
                {
                    vector = (await inner.EmbedAsync([text], role, ct))[0];
                    _queries.Add(text, vector);
                }

                vectors.Add(vector);
            }

            return vectors;
        }
    }
}
