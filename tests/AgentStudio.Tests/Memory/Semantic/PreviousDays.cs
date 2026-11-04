using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>One entry of the synthetic memory or journal of the question set: its key, what it is, when, and its text.</summary>
internal sealed record CorpusEntry(string Key, MemoryKind Kind, MemoryOrigin Origin, DateTimeOffset At, string Topic, string Content);

/// <summary>A question of the set and the key of the entry that answers it.</summary>
internal sealed record Question(string Id, string Text, string Answer);

/// <summary>How a search did on the set: the questions whose answer came among the first five, of how many, and those that did not.</summary>
internal sealed record Score(int Found, int Asked, IReadOnlyList<string> Missed)
{
    public double Rate => (double)Found / Asked;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Found}/{Asked} ({Rate:P0}); fuera de las cinco primeras: {(Missed.Count == 0 ? "ninguna" : string.Join(", ", Missed))}");
}

/// <summary>
/// The question set of F12-4 (docs/17 §9.2): a synthetic memory and journal of earlier days
/// (<c>Fixtures/preguntas-dias-anteriores.json</c>) and 50 questions in Spanish, each answered by one entry. It records the
/// entries in a project with the times of their days, the journal as anchored <see cref="MemoryKind.JournalEntry"/>
/// memory with its source in the journal, and scores any search by whether the answer comes among its first
/// <see cref="Top"/>. The integration measures the real journal of the repository the same way.
/// </summary>
internal sealed class PreviousDays
{
    /// <summary>How many results count: the answer must come among the first five.</summary>
    public const int Top = 5;

    /// <summary>When the questions are asked: the day after the last entry.</summary>
    public static readonly DateTimeOffset AskedAt = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private PreviousDays(IReadOnlyList<CorpusEntry> entries, IReadOnlyList<CorpusEntry> distractors, IReadOnlyList<Question> questions)
    {
        Entries = [.. entries, .. distractors];
        Distractors = distractors.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        Questions = questions;
    }

    /// <summary>Every entry of the set: the memory and the journal the questions are about, and the distractors.</summary>
    public IReadOnlyList<CorpusEntry> Entries { get; }

    /// <summary>
    /// The keys of the entries that have nothing to do with any question (office, design, events…), in every tier by
    /// relevance: any of them that a search brings is noise.
    /// </summary>
    public IReadOnlySet<string> Distractors { get; }

    public IReadOnlyList<Question> Questions { get; }

    public static PreviousDays Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Memory", "Semantic", "Fixtures", "preguntas-dias-anteriores.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var questions = root.GetProperty("preguntas").EnumerateArray().Select(q => new Question(
            q.GetProperty("id").GetString()!,
            q.GetProperty("texto").GetString()!,
            q.GetProperty("respuesta").GetString()!)).ToList();
        return new PreviousDays(EntriesOf(root.GetProperty("entradas")), EntriesOf(root.GetProperty("distractores")), questions);
    }

    private static List<CorpusEntry> EntriesOf(JsonElement entries) =>
        [.. entries.EnumerateArray().Select(e => new CorpusEntry(
            e.GetProperty("clave").GetString()!,
            Enum.Parse<MemoryKind>(e.GetProperty("tipo").GetString()!),
            Enum.Parse<MemoryOrigin>(e.GetProperty("origen").GetString()!),
            DateTimeOffset.Parse(e.GetProperty("momento").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            e.GetProperty("tema").GetString()!,
            e.GetProperty("contenido").GetString()!))];

    /// <summary>
    /// Records every entry in the project of <paramref name="world"/> at its time, oldest first, and leaves the clock at
    /// <see cref="AskedAt"/>; the id of the memory entry of each key.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> RecordAsync(MemoryWorld world, CancellationToken ct)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries.OrderBy(e => e.At).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            world.Clock.SetUtcNow(entry.At);
            var source = entry.Kind == MemoryKind.JournalEntry
                ? MemorySources.Journal(DateOnly.FromDateTime(entry.At.UtcDateTime), ContentHash.Sha256Hex(entry.Content))
                : "nota:" + entry.Key;
            var recorded = await world.Memory.RecordAsync(world.Project, new MemoryDraft(entry.Kind, entry.Topic, entry.Content, entry.Origin, source), ct);
            ids.Add(entry.Key, recorded.Id);
        }

        world.Clock.SetUtcNow(AskedAt);
        return ids;
    }

    /// <summary>Asks every question to <paramref name="firstFive"/> (the ids of the memory entries it gives, best first) and scores it.</summary>
    public async Task<Score> ScoreAsync(IReadOnlyDictionary<string, string> ids, Func<string, Task<IReadOnlyList<string>>> firstFive)
    {
        var missed = new List<string>();
        foreach (var question in Questions)
        {
            var found = await firstFive(question.Text);
            if (!found.Take(Top).Contains(ids[question.Answer], StringComparer.Ordinal))
            {
                missed.Add(question.Id);
            }
        }

        return new Score(Questions.Count - missed.Count, Questions.Count, missed);
    }
}
