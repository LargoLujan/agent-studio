using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Semantic;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Derivation;
using AgentStudio.Memory.Journal;
using AgentStudio.Memory.Semantic;
using AgentStudio.Memory.Store;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Infrastructure.Embeddings;
using AgentStudio.Tests.Memory.Store;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// F12-5 end to end (docs/17 §9.2; ADR-0047): the search the app makes, <see cref="ISemanticIndex.SearchAsync"/> as
/// «Memoria» asks it and <see cref="ISemanticIndex.SearchManyAsync"/> as the context packer asks it (its three tiers by
/// relevance), against a project of 10 000 trusted entries of memory recorded by the path of the product: the memory with its
/// decision log and its anchor (<see cref="ProjectMemory.RecordAsync"/>), and the journal written by
/// <see cref="MarkdownDailyJournal"/>, derived (<see cref="MemoryDerivation"/>) and trusted by a person
/// (<see cref="JournalTrust"/>). The index is the one of the app: <see cref="SemanticIndex"/> over WP2's store and the real
/// model, so each search pays the recall of what is anchored, the frequencies, the identifiers first, the cut, the fusion
/// and the check of every hit against the anchored entries. Unlike <c>SemanticSearchMeasurementTests</c> (the store, the
/// embedding and the fusion with random vectors), nothing here is simulated.
/// <list type="bullet">
/// <item>Skipped unless <c>AGENTSTUDIO_SEMANTIC_MEASURE</c> is 1, the model is on disk (<c>AGENTSTUDIO_E5_MODEL_DIR</c>, the
/// folder with its two files) and the build is optimised (<c>-c Release</c>).</item>
/// <item>The seeded project (its folder, the anchor of its log and its index) is kept under <c>%TEMP%</c>, by the version of
/// the seed and the number of entries (<see cref="EntriesVariable"/>, 10 000 by default): recording 10 000 entries takes
/// minutes, so the next runs reuse it, and its index with its vectors, and only measure.</item>
/// <item>300 queries after 30 to warm up (every query of the mix is asked while warming up): those of WP2 and some by
/// identifier, taken from the seeded journal and memory. The p50, p95 and p99 of each call, those of WP2 and those with an
/// identifier apart, and where the time goes: recall, the state of the index, the model, the embedding, the two lists, the
/// check of the hits and the rest (fusion, frequencies, the identifiers first).</item>
/// <item>The report goes to the test output and, when <c>AGENTSTUDIO_SEMANTIC_MEASURE_REPORT</c> names a file, is appended
/// to it. The p95 of <see cref="ISemanticIndex.SearchAsync"/> must be under 200 ms.</item>
/// </list>
/// </summary>
[Trait("Category", "Measurement")]
public sealed partial class SemanticIndexEndToEndMeasurementTests
{
    /// <summary>How many entries the seeded project has (10 000 by default; fewer for a smoke run).</summary>
    public const string EntriesVariable = "AGENTSTUDIO_SEMANTIC_MEASURE_ENTRIES";

    /// <summary>The version of the seed: a change of what it records seeds a new folder.</summary>
    private const int SeedVersion = 1;

    private const int Seed = 20261105;
    private const int DefaultEntries = 10_000;
    private const int WarmUp = 30;
    private const int Measured = 300;

    /// <summary>A recall slower than this is counted apart: the decision log is read whole every 256 loads or 5 minutes.</summary>
    private const double SlowRecall = 50;

    private static readonly TimeZoneInfo JournalZone = TimeZoneInfo.CreateCustomTimeZone("medida+02", TimeSpan.FromHours(2), "medida+02", "medida+02");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_search_of_the_app_over_ten_thousand_anchored_entries_with_the_real_model()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(SemanticSearchMeasurementTests.MeasureVariable) == "1", $"Set {SemanticSearchMeasurementTests.MeasureVariable}=1 to measure the search of the index end to end (F12-5).");
        Assert.SkipWhen(
            typeof(SemanticIndex).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true,
            "Measure an optimised build: dotnet test -c Release.");
        var modelFolder = LocalE5Model.Require();
        var entries = EntriesToSeed();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Semantic index end to end, F12-5 ({DateTimeOffset.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} logical processors, .NET {Environment.Version})");

        // ---- the project: seeded once, reused after ----
        var folder = SeededFolder(entries);
        var seeding = Stopwatch.StartNew();
        var seeded = await EnsureSeededAsync(folder, entries, report);
        report.AppendLine(seeded
            ? string.Create(CultureInfo.InvariantCulture, $"project: {entries} entries seeded in {seeding.Elapsed.TotalMinutes:F1} min in {folder}")
            : string.Create(CultureInfo.InvariantCulture, $"project: {entries} entries reused from {folder}"));

        using var project = await SeededProject.OpenAsync(folder);
        var timings = new Timings();
        var memory = new TimedMemory(project.Memory, timings);
        var store = new TimedStore(new SemanticIndexStore(project.Databases, new SemanticIndexOptions(project.IndexFolder)), timings);
        using var source = new TimedSource(new OnnxEmbedderSource(new EmbeddingModelLocation(modelFolder, LocalE5Model.Descriptor)), timings);
        var index = new SemanticIndex(memory, store, source, TimeProvider.System);

        var trusted = await project.Memory.RecallAsync(project.Project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: true), Ct);
        Assert.Equal(entries, trusted.Count);

        // ---- the index: the first refresh embeds everything; a reused one only checks it ----
        var refreshing = Stopwatch.StartNew();
        var refresh = await index.RefreshAsync(project.Project, Ct);
        Assert.True(refresh.Completed, refresh.Reason);
        var status = await index.StatusAsync(project.Project, Ct);
        Assert.Equal(status.Fragments, status.WithVectors);
        report.AppendLine(CultureInfo.InvariantCulture, $"index: {status.Fragments} fragments, all with vectors; refresh {refreshing.Elapsed.TotalSeconds:F1} s ({refresh.Embedded} embedded now, {refresh.Unchanged} unchanged)");

        // ---- the queries: WP2's, and some by identifier taken from what was seeded ----
        var byIdentifier = IdentifierQueries(trusted);
        (string Text, bool Identifier)[] queries = [.. SemanticSearchMeasurementTests.Queries.Select(q => (q, false)), .. byIdentifier.Select(q => (q, true))];
        Assert.True(queries.Length <= WarmUp, "Every query must be asked while warming up.");
        report.AppendLine(CultureInfo.InvariantCulture, $"queries: {SemanticSearchMeasurementTests.Queries.Length} of WP2 and {byIdentifier.Count} by identifier: {string.Join(" | ", byIdentifier)}");
        report.Append(Shares(trusted, SemanticSearchMeasurementTests.Queries));

        var first = Stopwatch.StartNew();
        _ = await index.SearchAsync(project.Project, MemoriaQuery(queries[0].Text), Ct);
        report.AppendLine(CultureInfo.InvariantCulture, $"first search (cold caches of the cuts and the words): {first.Elapsed.TotalMilliseconds:F0} ms");

        var single = new Measures();
        var pack = new Measures();
        for (var i = 0; i < WarmUp + Measured; i++)
        {
            var (text, identifier) = queries[i % queries.Length];
            timings.Reset();
            var watch = Stopwatch.StartNew();
            var found = await index.SearchAsync(project.Project, MemoriaQuery(text), Ct);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(SemanticSearchMode.Hybrid, found.Mode);
            if (i >= WarmUp)
            {
                single.Add(elapsed, identifier, timings);
            }

            timings.Reset();
            watch.Restart();
            var tiers = await index.SearchManyAsync(project.Project, PackQueries(text), Ct);
            elapsed = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(3, tiers.Count);
            if (i >= WarmUp)
            {
                pack.Add(elapsed, identifier, timings);
            }
        }

        report.Append(single.Report("SearchAsync, as «Memoria» asks (top 50, every trust)"));
        report.Append(pack.Report("SearchManyAsync, a pack (three tiers by relevance, top 50 each, trusted only)"));
        var text2 = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text2);
        if (Environment.GetEnvironmentVariable(SemanticSearchMeasurementTests.ReportVariable) is { Length: > 0 } reportFile)
        {
            await File.AppendAllTextAsync(reportFile, text2 + Environment.NewLine, Ct);
        }

        Assert.True(Percentile(single.Total, 0.95) < 200, text2);
    }

    /// <summary>The query of «Memoria»: the fifty best, of every trust, the texts of the log too (<c>MemoryViewModel</c>).</summary>
    private static SemanticQuery MemoriaQuery(string text) => new(text, 50) { IncludeLog = true, TrustedOnly = false };

    /// <summary>The three tiers by relevance of a pack, as the context packer asks them (<c>ContextPacker</c>).</summary>
    private static SemanticQuery[] PackQueries(string focus) =>
    [
        new(focus, 50) { Kinds = [MemoryKind.KnownError, MemoryKind.Solution], IncludeLog = false, TrustedOnly = true },
        new(focus, 50) { Kinds = [MemoryKind.Change, MemoryKind.Verdict, MemoryKind.DebateSummary], IncludeLog = false, TrustedOnly = true },
        new(focus, 50) { Kinds = [MemoryKind.JournalEntry], IncludeLog = false, TrustedOnly = true },
    ];

    private static int EntriesToSeed() =>
        int.TryParse(Environment.GetEnvironmentVariable(EntriesVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var entries) && entries >= 100
            ? entries
            : DefaultEntries;

    private static string SeededFolder(int entries) =>
        Path.Combine(Path.GetTempPath(), "agentstudio-f12-5", string.Create(CultureInfo.InvariantCulture, $"v{SeedVersion}-{entries}"));

    private static string Mark(int entries) => string.Create(CultureInfo.InvariantCulture, $"agentstudio-f12-5 seed {SeedVersion} · {entries} entries · {Seed}");

    /// <summary>
    /// The seeded project in <paramref name="folder"/>: reused when its mark (written last, once everything was recorded and
    /// trusted) is the one of this seed; otherwise the folder is wiped and seeded again. True when it seeded now.
    /// </summary>
    private static async Task<bool> EnsureSeededAsync(string folder, int entries, StringBuilder report)
    {
        var mark = Path.Combine(folder, "semilla.txt");
        if (File.Exists(mark) && string.Equals(await File.ReadAllTextAsync(mark, Ct), Mark(entries), StringComparison.Ordinal))
        {
            return false;
        }

        if (Directory.Exists(folder))
        {
            Assert.True(TempFolders.Delete(folder), $"The stale seed in {folder} could not be deleted.");
        }

        Directory.CreateDirectory(folder);
        using (var project = await SeededProject.OpenAsync(folder))
        {
            await SeedAsync(project, entries, report);
        }

        var written = mark + ".tmp";
        await File.WriteAllTextAsync(written, Mark(entries), Ct);
        File.Move(written, mark, overwrite: true);
        return true;
    }

    /// <summary>
    /// Records three quarters of <paramref name="entries"/> as memory of the system (decisions, known errors, solutions,
    /// changes, verdicts, conventions) and writes the last quarter to the journal as the product writes it, over a hundred
    /// days; derives the journal and trusts it for the whole project, as a person does in «Memoria».
    /// </summary>
    private static async Task SeedAsync(SeededProject project, int entries, StringBuilder report)
    {
        var random = new Random(Seed);
        var journalEntries = entries / 4;
        var memoryEntries = entries - journalEntries;
        var output = TestContext.Current.TestOutputHelper;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < memoryEntries; i++)
        {
            var (kind, topic, content) = Corpus.Memory(random, i);
            await project.Memory.RecordAsync(project.Project, new MemoryDraft(kind, topic, content, MemoryOrigin.System, string.Create(CultureInfo.InvariantCulture, $"medida:{i:D5}")), Ct);
            if ((i + 1) % 1000 == 0)
            {
                output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed: {i + 1} of {memoryEntries} entries of memory, {watch.Elapsed.TotalSeconds:F0} s"));
            }
        }

        var zone = new FakeTimeProvider();
        zone.SetLocalTimeZone(JournalZone);
        var writer = new MarkdownDailyJournal(project.Root, new ProjectStorageFiles(), zone);
        var start = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.FromHours(2));
        var perDay = Math.Max(1, journalEntries / 100);
        for (var j = 0; j < journalEntries; j++)
        {
            var at = start.AddDays(j / perDay).AddMinutes(j % perDay * 25);
            var prompt = Corpus.Prompt(random, at);
            var id = await writer.RecordPromptAsync(prompt.Entry, Ct);
            await writer.RecordOutcomeAsync(id, prompt.Outcome, Ct);
        }

        var derived = await project.Derivation.DeriveAsync(project.Project, Ct);
        Assert.Null(derived.Reason);
        Assert.Empty(derived.Problems);
        Assert.Equal(journalEntries, derived.Recorded.Count(e => e.Kind == MemoryKind.JournalEntry));
        await project.Trust.SetAsync(project.Project, JournalTrustScope.Project, null, trusted: true, "persona", "medida de F12-5", Ct);

        var trusted = await project.Memory.RecallAsync(project.Project, new MemoryQuery(MaxEntries: int.MaxValue, TrustedOnly: true), Ct);
        Assert.Equal(entries, trusted.Count);
        report.AppendLine(CultureInfo.InvariantCulture, $"seed: {memoryEntries} entries of memory and {journalEntries} of the journal over {Math.Ceiling(journalEntries / (double)perDay):F0} days, derived and trusted, in {watch.Elapsed.TotalMinutes:F1} min");
    }

    /// <summary>
    /// Queries by identifier, as a person writes them, with identifiers that are in the seeded project: a number of the
    /// journal with its day, a run, an entry with its day, a day, and memory ids.
    /// </summary>
    private static List<string> IdentifierQueries(IReadOnlyList<MemoryEntry> trusted)
    {
        var journal = trusted.Where(e => e.Kind == MemoryKind.JournalEntry).OrderBy(e => e.Topic, StringComparer.Ordinal).ToList();
        var memory = trusted.Where(e => e.Kind != MemoryKind.JournalEntry).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var queries = new List<string>();
        foreach (var entry in new[] { journal[journal.Count / 7], journal[journal.Count / 2], journal[journal.Count * 5 / 6] })
        {
            if (JournalTopic().Match(entry.Topic) is not { Success: true } topic || RunOf().Match(entry.Content) is not { Success: true } run)
            {
                continue;
            }

            var day = DateOnly.ParseExact(topic.Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var number = topic.Groups["key"].Value;
            queries.Add(string.Create(CultureInfo.InvariantCulture, $"Dame lo de la {number} del {day.Day}/{day.Month}"));
            queries.Add($"¿Qué salió de la ejecución {run.Groups["run"].Value}?");
            queries.Add(string.Create(CultureInfo.InvariantCulture, $"¿Qué falló en la entrada {day:yyyy-MM-dd}/{number} con las pruebas?"));
        }

        var someDay = DateOnly.ParseExact(JournalTopic().Match(journal[journal.Count / 3].Topic).Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        queries.Add(string.Create(CultureInfo.InvariantCulture, $"¿Qué se revisó el {someDay.Day} de {Corpus.MonthName(someDay.Month)} en el lector de pruebas?"));
        queries.Add($"¿Qué dice {memory[memory.Count / 4].Id}?");
        queries.Add($"¿Sigue vigente {memory[memory.Count * 3 / 4].Id}?");
        return queries;
    }

    /// <summary>For each query of WP2, its terms in more than 10 % of the entries, with that share: how common its words are here.</summary>
    private static string Shares(IReadOnlyList<MemoryEntry> trusted, IReadOnlyList<string> queries)
    {
        var words = trusted.Select(e => FoldedWords.Of($"{e.Topic}: {e.Content}")).ToList();
        var report = new StringBuilder("term shares of the queries of WP2 (terms in more than 10 % of the entries):");
        var any = false;
        foreach (var query in queries)
        {
            foreach (var term in LexicalTerms.From(query))
            {
                var phrase = FoldedWords.Of(term);
                var share = phrase.Length == 0 ? 0 : (double)words.Count(w => FoldedWords.Contains(w, phrase)) / words.Count;
                if (share > 0.10)
                {
                    report.Append(CultureInfo.InvariantCulture, $" «{term}» {share:P0};");
                    any = true;
                }
            }
        }

        return report.Append(any ? string.Empty : " none").AppendLine().ToString();
    }

    private static double Percentile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
    }

    private static string Summary(IReadOnlyList<double> ms) => ms.Count == 0
        ? "none"
        : string.Create(CultureInfo.InvariantCulture, $"p50 {Percentile(ms, 0.50):F1} · p95 {Percentile(ms, 0.95):F1} · p99 {Percentile(ms, 0.99):F1} · max {ms.Max():F1} ms (n={ms.Count})");

    [GeneratedRegex(@"^Bitácora del (?<day>\d{4}-\d{2}-\d{2}) · (?<key>P-\d{4,9})$")]
    private static partial Regex JournalTopic();

    [GeneratedRegex(@"ejecución `(?<run>[^`]+)`")]
    private static partial Regex RunOf();

    /// <summary>What a call costs, by part: where the time of a search goes.</summary>
    private enum Part
    {
        Recall,
        State,
        Model,
        Embedding,
        Words,
        Vectors,
        Check,
        Writes,
    }

    /// <summary>The time each part took since the last <see cref="Reset"/>: one search at a time, so one set of counters.</summary>
    private sealed class Timings
    {
        private readonly double[] _ms = new double[Enum.GetValues<Part>().Length];

        public double this[Part part] => _ms[(int)part];

        public double Sum => _ms.Sum();

        public void Reset() => Array.Clear(_ms);

        public async Task<T> TimeAsync<T>(Part part, Func<Task<T>> call)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                return await call();
            }
            finally
            {
                _ms[(int)part] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }

        public async Task TimeAsync(Part part, Func<Task> call)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await call();
            }
            finally
            {
                _ms[(int)part] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }
    }

    /// <summary>The measures of one call: its total, and each part, for every query, for those of WP2 and for those by identifier.</summary>
    private sealed class Measures
    {
        private readonly Dictionary<Part, List<double>> _parts = Enum.GetValues<Part>().ToDictionary(p => p, _ => new List<double>());
        private readonly List<double> _rest = [];
        private readonly List<double> _wp2 = [];
        private readonly List<double> _identifiers = [];

        public List<double> Total { get; } = [];

        public void Add(double total, bool identifier, Timings timings)
        {
            Total.Add(total);
            (identifier ? _identifiers : _wp2).Add(total);
            foreach (var (part, values) in _parts)
            {
                values.Add(timings[part]);
            }

            _rest.Add(Math.Max(0, total - timings.Sum));
        }

        public string Report(string title)
        {
            var slow = _parts[Part.Recall].Count(ms => ms > SlowRecall);
            return new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"{title}: {Summary(Total)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  queries of WP2: {Summary(_wp2)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  queries by identifier: {Summary(_identifiers)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  recall of what is anchored: {Summary(_parts[Part.Recall])}; {slow} above {SlowRecall:F0} ms (the log is read whole every 256 loads or 5 minutes)")
                .AppendLine(CultureInfo.InvariantCulture, $"  state of the index: {Summary(_parts[Part.State])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  model (opening it): {Summary(_parts[Part.Model])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  embedding of the query: {Summary(_parts[Part.Embedding])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  list by words (FTS5): {Summary(_parts[Part.Words])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  list by meaning (cosine, two fingerprints): {Summary(_parts[Part.Vectors])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  check of the hits against the index: {Summary(_parts[Part.Check])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  writes (fragments the index lacked or held altered): {Summary(_parts[Part.Writes])}")
                .AppendLine(CultureInfo.InvariantCulture, $"  rest (cut, fusion, frequencies, identifiers first, checks against what is anchored): {Summary(_rest)}")
                .ToString();
        }
    }

    /// <summary>The memory of the project, timing its recall.</summary>
    private sealed class TimedMemory(IProjectMemory inner, Timings timings) : IProjectMemory
    {
        public Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct) => inner.RecordAsync(project, draft, ct);

        public Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct) =>
            inner.ReviewAsync(project, entry, decision, decidedBy, comment, ct);

        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct) =>
            timings.TimeAsync(Part.Recall, () => inner.RecallAsync(project, query, ct));
    }

    /// <summary>The store of the index (WP2), timing each call by what it is for.</summary>
    private sealed class TimedStore(ISemanticIndexStore inner, Timings timings) : ISemanticIndexStore
    {
        public Task<IReadOnlyList<IndexedFragmentState>> ReadStateAsync(ProjectContext project, CancellationToken ct) =>
            timings.TimeAsync(Part.State, () => inner.ReadStateAsync(project, ct));

        public Task UpsertAsync(ProjectContext project, IReadOnlyList<IndexedFragment> fragments, CancellationToken ct) =>
            timings.TimeAsync(Part.Writes, () => inner.UpsertAsync(project, fragments, ct));

        public Task RemoveAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct) =>
            timings.TimeAsync(Part.Writes, () => inner.RemoveAsync(project, fragmentIds, ct));

        public Task<IReadOnlyList<SemanticFragment>> ReadAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct) =>
            timings.TimeAsync(Part.Check, () => inner.ReadAsync(project, fragmentIds, ct));

        public Task<IReadOnlyList<IndexedFragment>> ReadIndexedAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct) =>
            timings.TimeAsync(Part.Check, () => inner.ReadIndexedAsync(project, fragmentIds, ct));

        public Task<IReadOnlyList<RankedCandidate>> SearchWordsAsync(ProjectContext project, IReadOnlyList<string> terms, int top, CancellationToken ct) =>
            timings.TimeAsync(Part.Words, () => inner.SearchWordsAsync(project, terms, top, ct));

        public Task<IReadOnlyList<SimilarCandidate>> SearchVectorsAsync(ProjectContext project, ReadOnlyMemory<float> query, string modelFingerprint, int top, CancellationToken ct) =>
            timings.TimeAsync(Part.Vectors, () => inner.SearchVectorsAsync(project, query, modelFingerprint, top, ct));

        public Task DeleteAsync(ProjectContext project, CancellationToken ct) => inner.DeleteAsync(project, ct);
    }

    /// <summary>The local model (WP2), timing its opening and each embedding.</summary>
    private sealed class TimedSource(IEmbedderSource inner, Timings timings) : IEmbedderSource
    {
        public EmbeddingModelStatus Check() => inner.Check();

        public async Task<EmbedderOpening> OpenAsync(CancellationToken ct)
        {
            var opening = await timings.TimeAsync(Part.Model, () => inner.OpenAsync(ct));
            return opening.Embedder is { } embedder ? opening with { Embedder = new TimedEmbedder(embedder, timings) } : opening;
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class TimedEmbedder(IEmbedder inner, Timings timings) : IEmbedder
    {
        public EmbeddingModelDescriptor Model => inner.Model;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct) =>
            timings.TimeAsync(Part.Embedding, () => inner.EmbedAsync(texts, role, ct));
    }

    /// <summary>
    /// The seeded project as the app opens it, with the system clock: its databases, its decision log with the anchor
    /// outside the project, its memory, the derivation of its journal and the trust of a person in it. Disposing it closes it.
    /// </summary>
    private sealed class SeededProject : IDisposable
    {
        private SeededProject(string folder)
        {
            Root = Path.Combine(folder, "proyecto");
            IndexFolder = Path.Combine(folder, "indice");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(IndexFolder);
            var redactor = new SecretRedactor(@"C:\Users\persona");
            var activity = new FakeRunActivity();
            var recorder = new DecisionRecorder(new SqliteDecisionLogStore(Databases), TimeProvider.System, new FileDecisionLogAnchorStore(new DecisionLogAnchorOptions(Path.Combine(folder, "anclas")), Databases));
            var memoryStore = new SqliteMemoryStore(Databases);
            Memory = new ProjectMemory(recorder, memoryStore, activity, redactor, TimeProvider.System);
            Derivation = new MemoryDerivation(recorder, new SqliteEvidenceStore(Databases, TimeProvider.System), memoryStore, Memory, activity, new ProjectStorageFiles());
            Trust = new JournalTrust(recorder, Memory, activity, redactor, TimeProvider.System);
        }

        public ProjectDatabases Databases { get; } = new();

        public string Root { get; }

        public string IndexFolder { get; }

        public ProjectMemory Memory { get; }

        public MemoryDerivation Derivation { get; }

        public JournalTrust Trust { get; }

        public ProjectContext Project { get; private set; } = null!;

        public static async Task<SeededProject> OpenAsync(string folder)
        {
            var project = new SeededProject(folder);
            try
            {
                project.Project = await new ProjectWorkspace(project.Databases, TimeProvider.System).OpenAsync(project.Root, Ct);
                return project;
            }
            catch
            {
                project.Dispose();
                throw;
            }
        }

        public void Dispose() => Databases.Dispose();
    }

    /// <summary>
    /// The texts of the seed, in Spanish, from pools of components, problems, causes, fixes and decisions, so the words of
    /// the queries are as common as in a project and not in every entry.
    /// </summary>
    private static class Corpus
    {
        private static readonly string[] Components =
        [
            "el lector de pruebas", "el almacén de evidencias", "el lienzo del constructor", "el motor de ejecución", "el juez",
            "el auditor", "el panel de consumo", "la bitácora", "la memoria del proyecto", "el índice semántico",
            "el instalador del modelo", "la bandeja del sistema", "el diálogo de aprobación", "el tema oscuro", "la sala de debate",
            "el empaquetador de contexto", "el registro de decisiones", "el planificador", "las copias de trabajo", "la integración con git",
            "el servidor MCP", "el catálogo de habilidades", "la vista de uso", "el monitor de ejecuciones", "la cuota del proveedor",
            "la ventana de contexto", "el traspaso de sesión", "el editor de flujos", "la exportación a Markdown", "el bloqueo por inactividad",
            "Windows Hello", "el resumen diario", "la verificación aislada", "las pruebas de integración", "el perfil de verificación",
            "los requisitos obligatorios", "la cola de mensajes", "el reintento de tareas", "la reanudación tras reiniciar", "la búsqueda híbrida",
        ];

        private static readonly string[] Problems =
        [
            "falla al arrancar", "pierde la posición de los nodos", "se bloquea con muchos elementos", "tarda más de diez segundos",
            "no guarda los cambios", "devuelve un error de permisos", "duplica las entradas", "no respeta la cuota", "se queda sin memoria",
            "muestra datos de otro proyecto", "corta el texto a la mitad", "ignora las pruebas omitidas", "no reconoce la fecha",
            "abre dos veces la misma sesión", "pierde la conexión con el CLI", "deja un fichero bloqueado", "no avisa del error",
            "se cuelga al cancelar", "calcula mal los tokens", "no encuentra la carpeta",
        ];

        private static readonly string[] Causes =
        [
            "por una condición de carrera en la cola", "porque el fichero estaba abierto por otro proceso", "por un desbordamiento al sumar",
            "porque la ruta tenía espacios y una eñe", "por un permiso de lectura que faltaba", "porque el reloj iba en UTC",
            "por una transacción que no se cerraba", "porque el proveedor cambió el formato", "por un índice que no existía",
            "porque el texto llegaba por argumento y no por stdin", "por un tiempo de espera demasiado corto", "por una caché sin invalidar",
        ];

        private static readonly string[] Fixes =
        [
            "se resolvió con una cola de un solo consumidor", "se añadió un reintento con espera creciente", "se movió la escritura a una transacción",
            "se pasa el texto por stdin", "se compara la huella antes de cargar", "se cierra el fichero antes de renombrarlo",
            "se usa la hora local de la persona", "se lee el registro entero al abrir", "se añadió una prueba que lo reproduce",
            "se limita a cincuenta resultados", "se guarda el estado en el registro", "se avisa en la barra de estado",
        ];

        private static readonly string[] Decisions =
        [
            "usa SQLite en modo WAL", "se ejecuta siempre en solo lectura", "guarda su estado en el registro de decisiones",
            "no lee nunca las credenciales de los CLI", "pide el OK de la persona antes de entregar", "mide los tokens por modelo",
            "se reconstruye cuando se quiera", "nunca decide la confianza de nada", "trabaja en una copia de trabajo propia",
            "se ancla con su SHA-256", "falla cerrado si el registro no verifica", "se describe en un ADR",
        ];

        private static readonly string[] Verdicts =
        [
            "falta la prueba de reconstrucción del índice", "las pruebas pasan y la evidencia cuadra", "tocó ficheros fuera de su permiso",
            "el diff no corresponde a la tarea", "no hay evidencia de la ejecución", "cumple los requisitos obligatorios",
        ];

        private static readonly string[] Actors = ["Orquestador", "Backend", "Frontend", "Tester", "Documentador", "Revisor", "Juez"];

        private static readonly (ProviderId Provider, string Model)[] Models =
            [(ProviderId.ClaudeCode, "claude-opus-5"), (ProviderId.ClaudeCode, "claude-sonnet-5"), (ProviderId.CodexCli, "gpt-5"), (ProviderId.GeminiCli, "gemini-2.5-pro")];

        private static readonly string[] Verbs = ["Implementar", "Arreglar", "Revisar", "Medir", "Documentar", "Planificar", "Probar", "Refactorizar"];

        private static readonly string[] Tags = ["#tests", "#docs", "#agente-backend", "#lienzo", "#debate"];

        private static readonly string[] Months =
            ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

        public static string MonthName(int month) => Months[month - 1];

        /// <summary>An entry of memory of the system, of a kind by its place, with a topic and one to four sentences.</summary>
        public static (MemoryKind Kind, string Topic, string Content) Memory(Random random, int i)
        {
            var component = Pick(random, Components);
            var text = new StringBuilder();
            MemoryKind kind;
            switch (i % 10)
            {
                case 0 or 1 or 2:
                    kind = MemoryKind.Decision;
                    text.Append(CultureInfo.InvariantCulture, $"Se decide que {component} {Pick(random, Decisions)}.");
                    break;
                case 3 or 4:
                    kind = MemoryKind.KnownError;
                    text.Append(CultureInfo.InvariantCulture, $"{Capital(component)} {Pick(random, Problems)} {Pick(random, Causes)}.");
                    break;
                case 5:
                    kind = MemoryKind.Solution;
                    text.Append(CultureInfo.InvariantCulture, $"Cuando {component} {Pick(random, Problems)}, {Pick(random, Fixes)}.");
                    break;
                case 6 or 7:
                    kind = MemoryKind.Verdict;
                    text.Append(CultureInfo.InvariantCulture, $"El veredicto {(random.Next(2) == 0 ? "acepta" : "rechaza")} la entrega de {component}: {Pick(random, Verdicts)}.");
                    break;
                case 8:
                    kind = MemoryKind.Change;
                    text.Append(CultureInfo.InvariantCulture, $"Cambio en {component}: {Pick(random, Fixes)}.");
                    break;
                default:
                    kind = MemoryKind.Convention;
                    text.Append(CultureInfo.InvariantCulture, $"Convención: {component} {Pick(random, Decisions)}.");
                    break;
            }

            for (var s = random.Next(4); s > 0; s--)
            {
                text.Append(' ').Append(Sentence(random));
            }

            return (kind, string.Create(CultureInfo.InvariantCulture, $"{Capital(component)} ({i % 997})"), text.ToString());
        }

        /// <summary>A prompt of the journal at <paramref name="at"/> and its result, with the anchor of its launch.</summary>
        public static (PromptJournalEntry Entry, PromptOutcome Outcome) Prompt(Random random, DateTimeOffset at)
        {
            var (provider, model) = Pick(random, Models);
            var task = string.Create(CultureInfo.InvariantCulture, $"T{1 + random.Next(6)}");
            var run = string.Create(CultureInfo.InvariantCulture, $"{at:yyyyMMdd}-{random.Next(0x1000000):x6}");
            var status = random.Next(10) switch
            {
                0 => AgentRunStatus.Failed,
                1 => AgentRunStatus.RateLimited,
                _ => AgentRunStatus.Succeeded,
            };
            var entry = new PromptJournalEntry(at, Pick(random, Actors), provider, model, $"{Pick(random, Verbs)} {Pick(random, Components)}", [Pick(random, Tags)], task);
            var note = random.Next(3) == 0 ? null : Sentence(random);
            var outcome = new PromptOutcome(at.AddMinutes(4 + random.Next(30)), status, TimeSpan.FromSeconds(60 + random.Next(2400)), MetricValue<long>.Exact(1000 + random.Next(40_000), "medida"), MetricValue<long>.Exact(200 + random.Next(6000), "medida"), note)
            {
                Anchor = new AgentRunAnchor(run, task, 1 + random.Next(2)),
            };
            return (entry, outcome);
        }

        private static string Sentence(Random random) => random.Next(4) switch
        {
            0 => $"{Capital(Pick(random, Components))} {Pick(random, Problems)} {Pick(random, Causes)}.",
            1 => $"{Capital(Pick(random, Fixes))} en {Pick(random, Components)}.",
            2 => string.Create(CultureInfo.InvariantCulture, $"Se revisa el {1 + random.Next(28)} de {Pick(random, Months)} con {Pick(random, Components)}."),
            _ => $"{Capital(Pick(random, Components))} {Pick(random, Decisions)}.",
        };

        private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

        private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    }
}
