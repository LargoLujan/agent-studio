using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// F12-4 (docs/17 §9.2; ADR-0047, decision 6, and the criterion the person chose on 2026-11-04, third addendum of that day):
/// over a synthetic memory and journal of earlier days, 50 questions in Spanish by paraphrase and 15 by the metadata of the
/// journal and by identifiers. The hybrid search (bm25 and cosine fused with k = 60, the identifiers first) is never worse
/// than FTS5 as the memory searches today in any type of question (paraphrase, identifier with a meaning, metadata), and it
/// finds the answer among the first five in at least 10 points more of all the questions. The measures by words run always
/// and fix their figures; the one with the real model runs only when the model is on disk and the local model is plugged in
/// (<see cref="LocalModel"/>), and never downloads it.
/// </summary>
public sealed class PreviousDaysQuestionsTests
{
    /// <summary>The answers among the first five by words alone, as the memory searches today (FTS5, every word quoted, joined by OR), with the distractors: 52 %.</summary>
    private const int FoundByWordsToday = 26;

    /// <summary>
    /// The answers among the first five by the index without a model, as it is tuned (FTS5 over its fragments, the journal
    /// with its whole text, without function words, the keywords of the template of the journal nor those in more than half
    /// of the fragments), with the distractors: 50 %. Without «tarea» it misses q40, which FTS5 as today misses too.
    /// </summary>
    private const int FoundByWordsInTheIndex = 25;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_set_has_fifty_questions_about_earlier_days_each_answered_by_one_entry()
    {
        var set = PreviousDays.Load();

        Assert.Equal(50, set.Questions.Count);
        Assert.Equal(50, set.Questions.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(set.Entries.Count, set.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(set.Questions, q => Assert.Single(set.Entries, e => e.Key == q.Answer));
        Assert.Equal(50, set.Questions.Select(q => q.Answer).Distinct(StringComparer.Ordinal).Count());
        Assert.All(set.Entries, e => Assert.True(e.At < PreviousDays.AskedAt.AddHours(-12), $"{e.Key} is not of an earlier day"));
        Assert.True(set.Entries.Count >= 110, $"{set.Entries.Count} entries: too few distractors");

        // Twelve unrelated distractors in each tier by relevance, none of them an answer.
        var distractors = set.Entries.Where(e => set.Distractors.Contains(e.Key)).ToList();
        Assert.Equal(12, distractors.Count(e => e.Kind is MemoryKind.KnownError or MemoryKind.Solution));
        Assert.Equal(12, distractors.Count(e => e.Kind is MemoryKind.Change or MemoryKind.Verdict or MemoryKind.DebateSummary));
        Assert.Equal(12, distractors.Count(e => e.Kind == MemoryKind.JournalEntry));
        Assert.DoesNotContain(set.Questions, q => set.Distractors.Contains(q.Answer));

        // Half the answers are entries of the journal, the rest are memory of every category but the daily summary.
        var answers = set.Questions.Select(q => set.Entries.Single(e => e.Key == q.Answer)).ToList();
        Assert.Equal(25, answers.Count(e => e.Kind == MemoryKind.JournalEntry));
        Assert.DoesNotContain(set.Entries, e => e.Kind == MemoryKind.DailySummary);
        Assert.All(set.Entries, e => Assert.InRange(e.Content.Length, 1, MemoryContent.MaxCharacters));

        // Fifteen more, by the metadata of the journal and by identifiers, each answered by one entry: they are not F12-4.
        Assert.Equal(15, set.ByMetadata.Count);
        Assert.All(set.ByMetadata, q => Assert.Single(set.Entries, e => e.Key == q.Answer));
        Assert.Equal(set.All.Count, set.All.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["identificador con significado", "metadatos"], set.ByMetadata.Select(q => q.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.All(set.Questions, q => Assert.Equal(Question.Paraphrase, q.Type));
    }

    [Fact]
    public async Task By_words_alone_as_the_memory_searches_today()
    {
        var set = PreviousDays.Load();
        using var world = await MemoryWorld.CreateAsync();
        var ids = await set.RecordAsync(world, Ct);

        var score = await set.ScoreAsync(ids, async question =>
            [.. (await world.Memory.RecallAsync(world.Project, new MemoryQuery(question, MaxEntries: PreviousDays.Top), Ct)).Select(e => e.Id)]);

        TestContext.Current.TestOutputHelper?.WriteLine($"FTS5 como hoy: {score}");
        Assert.Equal(FoundByWordsToday, score.Found);
    }

    [Fact]
    public async Task By_words_alone_in_the_index_without_function_words_and_always_in_the_same_order()
    {
        var set = PreviousDays.Load();
        using var world = await IndexWorld.CreateAsync(new TestEmbedder(TestModels.Hashing), SemanticSearchTuning.Default);
        world.Model.State = EmbeddingModelState.Missing;
        var ids = await set.RecordAsync(world.World, Ct);
        await world.RefreshAsync();

        var score = await set.ScoreAsync(ids, async question => await world.FoundAsync(question, PreviousDays.Top));
        var again = await set.ScoreAsync(ids, async question => await world.FoundAsync(question, PreviousDays.Top));

        TestContext.Current.TestOutputHelper?.WriteLine($"Índice solo por palabras: {score}");
        Assert.Equal(FoundByWordsInTheIndex, score.Found);
        Assert.Equal(score.Missed, again.Missed);
    }

    [Fact]
    [Trait("Category", "LocalModel")]
    public async Task With_the_local_model_the_hybrid_search_is_never_worse_than_words_alone_in_any_type_and_wins_ten_points_in_all()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipUnless(
            LocalModel.IsOnDisk(local, out var directory),
            $"El modelo multilingual-e5-small no está en {directory}: F12-4 con el modelo real se mide cuando esté (una prueba nunca lo descarga).");
        using var model = LocalModel.Open(local);
        Assert.SkipWhen(model is null, $"Los ficheros de {directory} no dan su SHA-256 fijado: no se cargan.");

        var set = PreviousDays.Load();
        using var world = await MemoryWorld.CreateAsync();
        using var store = new TestSemanticStore();
        var index = new SemanticIndex(world.Memory, store, model!, world.Clock);
        var ids = await set.RecordAsync(world, Ct);
        var refresh = await index.RefreshAsync(world.Project, Ct);
        Assert.True(refresh.Completed, refresh.Reason);

        var modes = new HashSet<SemanticSearchMode>();
        var hybrid = await PreviousDays.ScoreAsync(ids, set.All, async question =>
        {
            var result = await index.SearchAsync(world.Project, new SemanticQuery(question, PreviousDays.Top), Ct);
            modes.Add(result.Mode);
            return [.. result.Hits.Select(h => h.Entry!.Id)];
        });
        var today = await PreviousDays.ScoreAsync(ids, set.All, async question =>
            [.. (await world.Memory.RecallAsync(world.Project, new MemoryQuery(question, MaxEntries: PreviousDays.Top), Ct)).Select(e => e.Id)]);

        var types = set.All.GroupBy(q => q.Type).Select(g => (Type: g.Key, Asked: g.Count(), Hybrid: g.Count(q => !hybrid.Missed.Contains(q.Id)), Today: g.Count(q => !today.Missed.Contains(q.Id)))).ToList();
        var points = 100d * (hybrid.Found - today.Found) / set.All.Count;
        var lost = set.All.Where(q => !today.Missed.Contains(q.Id) && hybrid.Missed.Contains(q.Id)).Select(q => q.Id).ToList();

        LocalModel.Report($"Híbrida: {hybrid}");
        LocalModel.Report($"FTS5 como hoy: {today}");
        LocalModel.Report(string.Join("; ", types.Select(t => $"{t.Type}: híbrida {t.Hybrid}/{t.Asked}, FTS5 como hoy {t.Today}/{t.Asked}")));
        LocalModel.Report(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"puntos de más en total: {points:F1}; de lo que FTS5 encuentra, la híbrida pierde: {(lost.Count == 0 ? "nada" : string.Join(" ", lost))}"));
        Assert.Equal([SemanticSearchMode.Hybrid], modes);
        Assert.All(types, t => Assert.True(t.Hybrid >= t.Today, $"{t.Type}: híbrida {t.Hybrid}/{t.Asked}, FTS5 como hoy {t.Today}/{t.Asked}"));
        Assert.True(points >= 10, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Híbrida: {hybrid}; FTS5 como hoy: {today}; {points:F1} puntos"));
    }
}
