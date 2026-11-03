using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>Hits, entries and states of the Phase 12 search of «Memoria», as the fakes give them.</summary>
internal static class MemorySearchSamples
{
    public const string ModelFolder = @"C:\Users\ana\AppData\Local\AgentStudio\models\multilingual-e5-small\614241f622f53c4eeff9890bdc4f31cfecc418b3";

    public static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    public static SemanticIndexStatus Ready(int fragments = 120, int withVectors = 120) =>
        new(fragments, withVectors, new EmbeddingModelStatus(EmbeddingModelState.Ready, ModelFolder));

    public static SemanticIndexStatus Missing(int fragments = 120) =>
        new(fragments, 0, new EmbeddingModelStatus(EmbeddingModelState.Missing, ModelFolder, "Falta model_qint8_avx512_vnni.onnx."));

    public static MemoryEntry Entry(
        string id,
        MemoryTrust trust,
        MemoryKind kind = MemoryKind.Decision,
        MemoryOrigin origin = MemoryOrigin.System,
        string? content = null,
        string? source = null,
        DateTimeOffset? at = null) =>
        new(id, kind, "pruebas", content ?? $"Contenido de {id}", origin, source ?? "registro:20261018-c4a1f2#42", at ?? At, new string('b', 64), trust, null, null);

    /// <summary>A journal entry of <paramref name="day"/> (its source names the day of its file).</summary>
    public static MemoryEntry JournalEntry(string id, DateOnly day, MemoryTrust trust, MemoryOrigin origin = MemoryOrigin.Agent, string? content = null) =>
        Entry(id, trust, MemoryKind.JournalEntry, origin, content ?? $"Prompt de la sesión de desarrollo {id}", MemorySources.Journal(day, new string('e', 64)), new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(2)));

    /// <summary>A hit of a fragment of <paramref name="entry"/>, ranked by words, by meaning or by both.</summary>
    public static SemanticHit Hit(MemoryEntry entry, int? lexical, int? semantic, int part = 0)
    {
        var fragment = new SemanticFragment(FragmentSource.Memory(entry.Id, entry.ContentSha256), part, entry.Content, entry.CreatedAt);
        return new SemanticHit(fragment, entry.Kind.ToString(), entry, Rank(fragment, lexical, semantic));
    }

    /// <summary>A hit of a text the system wrote from the entry <paramref name="sequence"/> of the log.</summary>
    public static SemanticHit LogHit(long sequence, string label, string text, int? lexical, int? semantic, DateTimeOffset? at = null)
    {
        var fragment = new SemanticFragment(FragmentSource.LogEntry(sequence, new string('d', 64)), 0, text, at ?? At);
        return new SemanticHit(fragment, label, null, Rank(fragment, lexical, semantic));
    }

    private static FusedCandidate Rank(SemanticFragment fragment, int? lexical, int? semantic) =>
        new(fragment.Id, fragment.At, (lexical is { } l ? 1.0 / (60 + l) : 0) + (semantic is { } s ? 1.0 / (60 + s) : 0), lexical, semantic);
}

/// <summary>
/// The semantic index of one project as the test sets it: its state, and the answer of each search. It records the
/// searches and how many times its state was read; it never refreshes, snapshots nor deletes (the document does not).
/// </summary>
internal sealed class FakeSemanticIndex : ISemanticIndex
{
    public SemanticIndexStatus Status { get; set; } = MemorySearchSamples.Ready();

    public Exception? StatusFailure { get; set; }

    public Func<SemanticQuery, SemanticSearchResult> Answer { get; set; } = _ => new SemanticSearchResult([], SemanticSearchMode.Hybrid);

    public Exception? SearchFailure { get; set; }

    public List<(ProjectContext Project, SemanticQuery Query)> Searches { get; } = [];

    /// <summary>When set, a search answers only once this completes (a slow search, to see what happens meanwhile).</summary>
    public TaskCompletionSource? HoldSearch { get; set; }

    /// <summary>How many searches are answering now; the most at once is <see cref="MostAtOnce"/>.</summary>
    public int Running { get; private set; }

    public int MostAtOnce { get; private set; }

    public int StatusReads { get; private set; }

    public Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("«Memoria» never refreshes the index.");

    public async Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct)
    {
        lock (Searches)
        {
            Searches.Add((project, query));
            Running++;
            MostAtOnce = Math.Max(MostAtOnce, Running);
        }

        try
        {
            if (HoldSearch is { } hold)
            {
                await hold.Task;
            }

            return SearchFailure is { } failure ? throw failure : Answer(query);
        }
        finally
        {
            lock (Searches)
            {
                Running--;
            }
        }
    }

    public Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct)
    {
        StatusReads++;
        return StatusFailure is { } failure ? Task.FromException<SemanticIndexStatus>(failure) : Task.FromResult(Status);
    }

    public Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct) => throw new NotSupportedException("«Memoria» takes no snapshot.");

    public Task DeleteAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("«Memoria» never deletes the index.");
}

/// <summary>
/// The trust of the journal as the test sets it. Each decision is recorded and changes the state the next read gives, as
/// the log would; a decision can be held (<see cref="Hold"/>) as the real one waits while a run goes on, or fail.
/// </summary>
internal sealed class FakeJournalTrust : IJournalTrust
{
    public JournalTrustState State { get; set; } = new(false, [], 0);

    public Exception? ReadFailure { get; set; }

    public Exception? Failure { get; set; }

    public TaskCompletionSource? Hold { get; set; }

    public List<(ProjectContext Project, JournalTrustScope Scope, DateOnly? Day, bool Trusted, string DecidedBy, string? Comment)> Decisions { get; } = [];

    /// <summary>How many times the trust was read.</summary>
    public int Reads { get; private set; }

    public Task<JournalTrustState> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        Reads++;
        return ReadFailure is { } failure ? Task.FromException<JournalTrustState>(failure) : Task.FromResult(State);
    }

    public async Task SetAsync(ProjectContext project, JournalTrustScope scope, DateOnly? day, bool trusted, string decidedBy, string? comment, CancellationToken ct)
    {
        if (Hold is { } hold)
        {
            await hold.Task;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        Decisions.Add((project, scope, day, trusted, decidedBy, comment));
        State = scope == JournalTrustScope.Project
            ? State with { ProjectTrusted = trusted }
            : State with { TrustedDays = trusted ? [.. State.TrustedDays.Append(day!.Value).Distinct().Order()] : [.. State.TrustedDays.Where(d => d != day)] };
    }
}

/// <summary>The installer of the model as the test sets it: it records each consent and answers what <see cref="Install"/> says.</summary>
internal sealed class FakeModelInstaller : IEmbeddingModelInstaller
{
    public List<EmbeddingModelDownloadConsent> Consents { get; } = [];

    public Func<IProgress<EmbeddingModelDownloadProgress>?, CancellationToken, Task<EmbeddingModelStatus>> Install { get; set; } =
        (_, _) => Task.FromResult(new EmbeddingModelStatus(EmbeddingModelState.Ready, MemorySearchSamples.ModelFolder));

    public Task<EmbeddingModelStatus> InstallAsync(EmbeddingModelDownloadConsent consent, IProgress<EmbeddingModelDownloadProgress>? progress, CancellationToken ct)
    {
        Consents.Add(consent);
        return Install(progress, ct);
    }
}
