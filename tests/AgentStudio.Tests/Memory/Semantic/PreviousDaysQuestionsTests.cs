using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// F12-4 (docs/17 §9.2; ADR-0047, decision 6): over a synthetic memory and journal of earlier days, 50 questions in
/// Spanish. The hybrid search (bm25 and cosine fused with k = 60) must put the answer among the first five in 85 % of them
/// or more, and at least 15 points more than FTS5 alone. The measures by words run always and fix their figures; the one
/// with the real model runs only when the model is on disk and the local model is plugged in (<see cref="LocalModel"/>),
/// and never downloads it.
/// </summary>
public sealed class PreviousDaysQuestionsTests
{
    /// <summary>The answers among the first five by words alone, as the memory searches today (FTS5, every word quoted, joined by OR), with the distractors: 52 %.</summary>
    private const int FoundByWordsToday = 26;

    /// <summary>
    /// The answers among the first five by the index without a model, as it is tuned (FTS5 over its fragments, the journal
    /// with its whole text, without function words nor those in more than half of the fragments), with the distractors: 52 %.
    /// </summary>
    private const int FoundByWordsInTheIndex = 26;

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
    public async Task With_the_local_model_the_hybrid_search_finds_the_answer_among_the_first_five_in_85_percent_and_15_points_more_than_words_alone()
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

        var mode = SemanticSearchMode.Hybrid;
        var hybrid = await set.ScoreAsync(ids, async question =>
        {
            var result = await index.SearchAsync(world.Project, new SemanticQuery(question, PreviousDays.Top), Ct);
            mode = result.Mode;
            return [.. result.Hits.Select(h => h.Entry!.Id)];
        });
        var today = await set.ScoreAsync(ids, async question =>
            [.. (await world.Memory.RecallAsync(world.Project, new MemoryQuery(question, MaxEntries: PreviousDays.Top), Ct)).Select(e => e.Id)]);

        LocalModel.Report($"Híbrida: {hybrid}");
        LocalModel.Report($"FTS5 como hoy: {today}");
        Assert.Equal(SemanticSearchMode.Hybrid, mode);
        Assert.True(hybrid.Rate >= 0.85, $"Híbrida: {hybrid}");
        Assert.True(hybrid.Found - today.Found >= 0.15 * set.Questions.Count, $"Híbrida: {hybrid}; FTS5 como hoy: {today}");
    }
}
