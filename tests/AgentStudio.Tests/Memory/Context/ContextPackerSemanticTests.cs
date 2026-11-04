using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Context;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Memory.Semantic;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Context;

/// <summary>
/// The packer with the semantic index (docs/17 D-02 and D-06, F12-7 and F12-8; ADR-0047, which amends ADR-0023): the tiers
/// by relevance follow the hybrid search, the journal has a tier of its own after every other one and is the first left
/// out when there is no room, the text of an item is always the anchored entry's, the index failing falls back to the
/// full-text search, a log that does not verify still stops the pack, and a judge or an auditor never asks the index.
/// </summary>
public sealed class ContextPackerSemanticTests
{
    private const string Focus = "Implementar Multiply";

    private static readonly ProjectContext Project = new("proyecto", @"C:\proyecto");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Agent(AgentRole role, MemoryPolicy memory = MemoryPolicy.ReadSelected) => new(
        Aid(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic),
        "instructions",
        [],
        [],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
        1,
        memory,
        new ReviewPolicy(false, false));

    private static ContextItem Requirement(string id, string text) => new(id, ContextTier.MandatoryRequirement, "requirement", "User", text, ContentHash.Sha256Hex(text));

    private static MemoryEntry Entry(string id, MemoryKind kind, string topic, string content, MemoryOrigin origin = MemoryOrigin.System, MemoryTrust trust = MemoryTrust.Trusted) =>
        new(id, kind, topic, content, origin, "registro:r#1", T0, MemoryContent.Sha256(kind, topic, content), trust, null, null);

    private static ContextPackRequest Request(AgentRole role, int budget, string focus = Focus, MemoryPolicy memory = MemoryPolicy.ReadSelected) =>
        new(Agent(role, memory), focus, [Requirement("RQ-1", "Añadir Multiply")], budget);

    private static string[] Ids(IEnumerable<ContextItem> items) => [.. items.Select(i => i.Id)];

    /// <summary>One entry of each tier by relevance and one of the journal; each item takes topic + ": " + content.</summary>
    private static readonly MemoryEntry[] Relevant =
    [
        Entry("mem-d1", MemoryKind.Decision, "Despliegue", "Blue green."),
        Entry("mem-k1", MemoryKind.KnownError, "Overflow", "Multiply desborda."),
        Entry("mem-x1", MemoryKind.Change, "Integración", "T1 integrada."),
        Entry("mem-j1", MemoryKind.JournalEntry, "Bitácora del 2026-10-20, 10:42", "Hecho: Multiply con checked."),
    ];

    // ---- the journal, last ----

    [Fact]
    public async Task The_journal_has_a_tier_of_its_own_after_every_other_one()
    {
        var pack = await new ContextPacker(new ScriptedMemory(Relevant)).PackAsync(Project, Request(AgentRole.Executor, 10_000), Ct);

        Assert.Equal(["RQ-1", "mem-d1", "mem-k1", "mem-x1", "mem-j1"], Ids(pack.Items));
        Assert.Equal(
            [ContextTier.MandatoryRequirement, ContextTier.Decision, ContextTier.KnownIssue, ContextTier.Other, ContextTier.History],
            pack.Items.Select(i => i.Tier));
        Assert.Equal(new ContextItem("mem-j1", ContextTier.History, "JournalEntry", "System", "Bitácora del 2026-10-20, 10:42: Hecho: Multiply con checked.", Relevant[3].ContentSha256), pack.Items[^1]);
        Assert.Equal(Ids(pack.Items), pack.ToReference().Entries);
    }

    [Fact]
    public async Task When_there_is_no_room_the_journal_is_left_out_first_then_the_rest_then_the_known_errors()
    {
        var packer = new ContextPacker(new ScriptedMemory(Relevant));

        // RQ-1 (15) + decision (23) + known error (28) + change (26) = 92; the journal entry needs 60 more.
        var noJournal = await packer.PackAsync(Project, Request(AgentRole.Executor, 92), Ct);
        var noRest = await packer.PackAsync(Project, Request(AgentRole.Executor, 91), Ct);
        var noKnown = await packer.PackAsync(Project, Request(AgentRole.Executor, 65), Ct);

        Assert.Equal(["RQ-1", "mem-d1", "mem-k1", "mem-x1"], Ids(noJournal.Items));
        Assert.Equal(["mem-j1"], Ids(noJournal.Omitted));
        Assert.Equal(["RQ-1", "mem-d1", "mem-k1"], Ids(noRest.Items));
        Assert.Equal(["mem-x1", "mem-j1"], Ids(noRest.Omitted));
        Assert.Equal(["RQ-1", "mem-d1"], Ids(noKnown.Items));
        Assert.Equal(["mem-k1", "mem-x1", "mem-j1"], Ids(noKnown.Omitted));
        Assert.Equal(ContextTier.History, noJournal.Omitted.Single().Tier);
    }

    [Fact]
    public async Task Without_a_focus_neither_the_index_nor_the_journal_is_asked()
    {
        var index = new SpyIndex();

        var pack = await new ContextPacker(new ScriptedMemory(Relevant), index).PackAsync(Project, Request(AgentRole.Executor, 10_000, focus: "  "), Ct);

        Assert.Equal(["RQ-1", "mem-d1"], Ids(pack.Items));
        Assert.Empty(index.Queries);
    }

    // ---- relevance by the index (F12-7) ----

    [Fact]
    public async Task The_tiers_by_relevance_ask_the_index_for_trusted_entries_of_each_tier_only()
    {
        var index = new SpyIndex();

        await new ContextPacker(new ScriptedMemory(Relevant), index).PackAsync(Project, Request(AgentRole.Orchestrator, 10_000), Ct);

        Assert.Equal(
            new[] { new[] { MemoryKind.KnownError, MemoryKind.Solution }, new[] { MemoryKind.Change, MemoryKind.Verdict, MemoryKind.DebateSummary }, new[] { MemoryKind.JournalEntry } },
            index.Queries.Select(q => q.Kinds!.ToArray()));
        Assert.All(index.Queries, q => Assert.Equal((Focus, ContextPacker.MaxCandidatesPerTier, true, false), (q.Text, q.Top, q.TrustedOnly, q.IncludeLog)));
        Assert.All(index.Queries, q => Assert.DoesNotContain(MemoryKind.DailySummary, q.Kinds!));
    }

    [Fact]
    public async Task With_the_index_a_tier_follows_the_order_of_the_hybrid_search_and_quotes_the_anchored_entries()
    {
        var second = Entry("mem-k2", MemoryKind.KnownError, "Overflow", "Multiply desborda.");
        var first = Entry("mem-k1", MemoryKind.Solution, "Checked", "Usar checked en Multiply.");
        var proposal = Entry("mem-k3", MemoryKind.KnownError, "Propuesta", "Quitar checked.", MemoryOrigin.Agent, MemoryTrust.Proposed);
        var index = new SpyIndex(q => q.Kinds!.Contains(MemoryKind.KnownError) ? Hits(first, second, proposal, first) : new SemanticSearchResult([], SemanticSearchMode.Hybrid));

        var pack = await new ContextPacker(new ScriptedMemory([]), index).PackAsync(Project, Request(AgentRole.Executor, 10_000), Ct);

        Assert.Equal(["RQ-1", "mem-k1", "mem-k2"], Ids(pack.Items));
        Assert.Equal("Checked: Usar checked en Multiply.", pack.Items[1].Text);
        Assert.Equal(first.ContentSha256, pack.Items[1].Sha256);
    }

    [Fact]
    public async Task With_the_real_index_the_pack_brings_by_meaning_what_the_words_alone_miss_and_the_journal_last()
    {
        using var world = await IndexWorld.CreateAsync();
        var red = await world.RecordAsync(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas tras el cambio de IVA.");
        await world.RecordAsync(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.");
        var journal = await world.RecordAsync(MemoryKind.JournalEntry, "Bitácora del 2026-10-21, 09:00", "«Arreglar facturas» · Hecho: el test de facturas ya no está en rojo; fallaba por el redondeo.");
        await world.RecordAsync(MemoryKind.DailySummary, "Resumen del 2026-10-21", "Hecho: el test de facturas vuelve a estar en verde.");
        await world.RefreshAsync();
        var request = new ContextPackRequest(Agent(AgentRole.Executor), "pruebas que fallan", [Requirement("RQ-1", "Añadir Multiply")], 10_000);

        var byWords = await new ContextPacker(world.World.Memory).PackAsync(world.Project, request, Ct);
        var hybrid = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);

        Assert.DoesNotContain(byWords.Items, i => i.Id == red.Id);
        Assert.Equal(["RQ-1", red.Id, journal.Id], Ids(hybrid.Items.Where(i => i.Tier is not (ContextTier.Decision))));
        Assert.Equal((ContextTier.KnownIssue, ContextTier.History), (hybrid.Items.Single(i => i.Id == red.Id).Tier, hybrid.Items.Single(i => i.Id == journal.Id).Tier));
        Assert.Equal($"{journal.Topic}: {journal.Content}", hybrid.Items.Single(i => i.Id == journal.Id).Text);
        Assert.DoesNotContain(hybrid.Items.Concat(hybrid.Omitted), i => i.Kind == nameof(MemoryKind.DailySummary));

        var again = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);
        Assert.Equal(hybrid.Sha256, again.Sha256);
    }

    [Fact]
    public async Task With_the_index_unrelated_entries_in_every_tier_never_fill_the_pack()
    {
        // The probe of the review: one relevant entry, and the rest of every tier by relevance unrelated to the focus.
        using var world = await IndexWorld.CreateAsync();
        var overflow = await world.RecordAsync(MemoryKind.KnownError, "Overflow", "Multiply desborda con enteros grandes.");
        await world.RecordAsync(MemoryKind.KnownError, "Caché", "Se corrompe al reiniciar el servidor.");
        await world.RecordAsync(MemoryKind.Solution, "Impresora", "La impresora vuelve a imprimir a doble cara con el controlador nuevo.");
        await world.RecordAsync(MemoryKind.Change, "Logotipo", "El logotipo pasa a color azul.");
        await world.RecordAsync(MemoryKind.Verdict, "Concurso de nombres", "El jurado eligió «Atlas» como nombre del boletín.");
        for (var i = 0; i < 30; i++)
        {
            await world.RecordAsync(MemoryKind.JournalEntry, $"Bitácora del 2026-10-{10 + (i % 20):D2}", $"Hecho: revisada la pantalla de ajustes número {i} sin cambios; {new string('x', 200)}");
        }

        await world.RefreshAsync();
        var request = Request(AgentRole.Executor, 24_000);

        var pack = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);

        Assert.Equal(["RQ-1", overflow.Id], Ids(pack.Items));
        Assert.Empty(pack.Omitted);
        Assert.Equal(ContextRelevance.Hybrid with { Cut = new MeaningOnlyCut(0.2f) }, pack.Relevance);

        // Without the floor of the model, the nearest of every tier come in whatever they say.
        var unfloored = await new ContextPacker(world.World.Memory, world.With(TestModels.Uncut)).PackAsync(world.Project, request, Ct);
        Assert.True(unfloored.Items.Count > 30, $"{unfloored.Items.Count} items without the floor");
        Assert.Null(unfloored.Relevance!.Cut);
    }

    [Fact]
    public async Task The_pack_says_how_its_tiers_by_relevance_were_ranked()
    {
        using var world = await IndexWorld.CreateAsync();
        await world.RecordAsync(MemoryKind.KnownError, "Overflow", "Multiply desborda con enteros grandes.");
        var request = Request(AgentRole.Executor, 10_000);

        var pending = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);
        await world.RefreshAsync();
        var hybrid = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);
        world.Model.State = EmbeddingModelState.Missing;
        var missing = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, request, Ct);
        var failing = await new ContextPacker(world.World.Memory, new SpyIndex(failure: new IOException("The index is unreadable."))).PackAsync(world.Project, request, Ct);
        var withoutIndex = await new ContextPacker(world.World.Memory).PackAsync(world.Project, request, Ct);
        var withoutFocus = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, Request(AgentRole.Executor, 10_000, focus: " "), Ct);
        var judge = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, Request(AgentRole.Judge, 10_000), Ct);

        Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.VectorsPending), pending.Relevance);
        Assert.Equal(ContextRelevance.Hybrid with { Cut = new MeaningOnlyCut(0.2f) }, hybrid.Relevance);
        Assert.Null(hybrid.Relevance!.Notice);
        Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.ModelMissing), missing.Relevance);
        Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.IndexUnavailable), failing.Relevance);
        Assert.Equal("Solo por palabras: el índice semántico no se ha podido usar y se ha buscado en la memoria.", failing.Relevance!.Notice);
        Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.IndexUnavailable), withoutIndex.Relevance);
        Assert.Null(withoutFocus.Relevance);
        Assert.Null(judge.Relevance);
        Assert.All(new[] { pending, hybrid, missing, failing, withoutIndex }, p => Assert.Contains(p.Items, i => i.Kind == nameof(MemoryKind.KnownError)));
    }

    [Fact]
    public async Task The_packer_asks_the_index_once_per_pack_for_its_three_tiers()
    {
        var index = new SpyIndex();

        await new ContextPacker(new ScriptedMemory(Relevant), index).PackAsync(Project, Request(AgentRole.Orchestrator, 10_000), Ct);

        var batch = Assert.Single(index.Batches);
        Assert.Equal(3, batch.Count);
        Assert.All(batch, q => Assert.Equal(Focus, q.Text));
    }

    [Fact]
    public async Task With_the_real_index_and_no_model_the_pack_still_ranks_by_words()
    {
        using var world = await IndexWorld.CreateAsync(EmbeddingModelState.Missing);
        var overflow = await world.RecordAsync(MemoryKind.KnownError, "Overflow", "Multiply desborda con enteros grandes.");
        await world.RecordAsync(MemoryKind.KnownError, "Caché", "Se corrompe al reiniciar.");
        var journal = await world.RecordAsync(MemoryKind.JournalEntry, "Bitácora del 2026-10-22, 12:00", "Hecho: Multiply ya usa checked.");

        var pack = await new ContextPacker(world.World.Memory, world.Index).PackAsync(world.Project, Request(AgentRole.Executor, 10_000), Ct);

        Assert.Equal(["RQ-1", overflow.Id, journal.Id], Ids(pack.Items));
    }

    [Fact]
    public async Task If_the_index_fails_each_tier_falls_back_to_the_full_text_search_of_the_memory()
    {
        var memory = new ScriptedMemory(Relevant);
        var withoutIndex = await new ContextPacker(memory).PackAsync(Project, Request(AgentRole.Executor, 10_000), Ct);

        var pack = await new ContextPacker(memory, new SpyIndex(failure: new IOException("The index of the project is unreadable."))).PackAsync(Project, Request(AgentRole.Executor, 10_000), Ct);

        Assert.Equal(withoutIndex.Items, pack.Items);
        Assert.Equal(withoutIndex.Sha256, pack.Sha256);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_still_stops_the_pack()
    {
        var failure = new DecisionLogIntegrityException(Project.ProjectId, new DecisionLogVerification(false, 3, "hash roto"));
        var packer = new ContextPacker(new ScriptedMemory(Relevant), new SpyIndex(failure: failure));

        var thrown = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => packer.PackAsync(Project, Request(AgentRole.Executor, 10_000), Ct));

        Assert.Same(failure, thrown);
    }

    // ---- F12-8: never for a judge nor an auditor ----

    [Theory]
    [InlineData(AgentRole.Judge, MemoryPolicy.ReadSelected)]
    [InlineData(AgentRole.Judge, MemoryPolicy.None)]
    [InlineData(AgentRole.Auditor, MemoryPolicy.ReadSelected)]
    [InlineData(AgentRole.Auditor, MemoryPolicy.None)]
    public async Task A_judge_or_an_auditor_gets_nothing_of_the_index_nor_of_the_memory(AgentRole role, MemoryPolicy policy)
    {
        var memory = new ScriptedMemory(Relevant);
        var index = new SpyIndex(_ => Hits(Relevant[1], Relevant[3]));

        var pack = await new ContextPacker(memory, index).PackAsync(Project, Request(role, 10_000, memory: policy), Ct);

        Assert.Equal(["RQ-1"], Ids(pack.Items));
        Assert.Empty(pack.Omitted);
        Assert.Empty(index.Queries);
        Assert.Equal(0, index.OtherCalls);
        Assert.Empty(memory.Queries);
    }

    private static SemanticSearchResult Hits(params MemoryEntry[] entries) =>
        new([.. entries.Select((e, i) => new SemanticHit(
            FragmentCutter.Of(e)[0],
            e.Kind.ToString(),
            e,
            new FusedCandidate(SemanticFragment.IdOf(FragmentSource.Memory(e.Id, e.ContentSha256), 0), T0, 1.0 / (HybridRanking.K + i + 1), i + 1, null)))],
            SemanticSearchMode.Hybrid);

    /// <summary>The memory as recall gives it: the entries of the categories asked for, in their order; it never writes.</summary>
    private sealed class ScriptedMemory(IReadOnlyList<MemoryEntry> entries) : IProjectMemory
    {
        public List<MemoryQuery> Queries { get; } = [];

        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            IReadOnlyList<MemoryEntry> found = [.. entries.Where(e => query.Kinds is null || query.Kinds.Contains(e.Kind)).Take(query.MaxEntries)];
            return Task.FromResult(found);
        }

        public Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct) =>
            throw new InvalidOperationException("The packer never writes memory.");

        public Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct) =>
            throw new InvalidOperationException("The packer never reviews memory.");
    }

    /// <summary>An index that answers what the test says, or fails as it says, and records every search; the packer never asks it anything else.</summary>
    private sealed class SpyIndex(Func<SemanticQuery, SemanticSearchResult>? answer = null, Exception? failure = null) : ISemanticIndex
    {
        public List<SemanticQuery> Queries { get; } = [];

        public List<IReadOnlyList<SemanticQuery>> Batches { get; } = [];

        public int OtherCalls { get; private set; }

        public Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            return failure is not null
                ? Task.FromException<SemanticSearchResult>(failure)
                : Task.FromResult(answer?.Invoke(query) ?? new SemanticSearchResult([], SemanticSearchMode.Hybrid));
        }

        public async Task<IReadOnlyList<SemanticSearchResult>> SearchManyAsync(ProjectContext project, IReadOnlyList<SemanticQuery> queries, CancellationToken ct)
        {
            Batches.Add(queries);
            var results = new List<SemanticSearchResult>();
            foreach (var query in queries)
            {
                results.Add(await SearchAsync(project, query, ct));
            }

            return results;
        }

        public Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct) => Refused<SemanticIndexRefresh>();

        public Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct) => Refused<SemanticIndexStatus>();

        public Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct) => Refused<ProjectHistorySnapshot>();

        public Task DeleteAsync(ProjectContext project, CancellationToken ct) => Refused<bool>();

        private Task<T> Refused<T>()
        {
            OtherCalls++;
            return Task.FromException<T>(new InvalidOperationException("The packer only searches the index."));
        }
    }
}
