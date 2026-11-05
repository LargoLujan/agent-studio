using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Context;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Memory.Semantic;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase12;

/// <summary>
/// The two scenarios docs/17 §9.2 gives the integration of Phase 12 (F12-7, F12-8 and F12-3), with the real engine, the real
/// memory over SQLite, the real derivation of the journal and the real index (WP2 and WP3), and scripted CLIs:
/// <list type="bullet">
/// <item>a run whose executor gets in its pack what a previous day left (a known error recorded yesterday and an entry of
/// yesterday's journal, trusted for its day), each in its tier, ranked by relevance against the objective of its task, with
/// the pack saying how it ranked (<see cref="ContextPack.Relevance"/>); and the judge of that run gets no pack (F12-8);</item>
/// <item>an index deleted and rebuilt: the same fragments, with the same fingerprints and, with the same model, the same
/// order of results (F12-3).</item>
/// </list>
/// Without the model both go by words, and always run; with the model on disk (<c>AGENTSTUDIO_E5_MODEL_DIR</c>) they also
/// go by meaning, and the run finds a paraphrase that only the meaning finds. Nothing is ever downloaded, no app is created
/// and no CLI is launched.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class Phase12ScenarioTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- a run brings what a previous day decided --------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scripted_run_brings_into_its_pack_what_a_previous_day_decided(bool byMeaning)
    {
        var model = ModelOrSkip(byMeaning);
        using var repo = TempGitRepo.Create();
        var paths = Phase4Paths.Beside(repo.Root);
        var clock = new ShiftedClock { Shift = TimeSpan.FromDays(1) };
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var codex = ScriptedCli.Codex(TimeProvider.System);
        claude.Script.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.txt", "a\n")));
        codex.Script.Script("juez", "R1", 1, new AgentScript { StructuredOutput = "{}", FinalText = "Sin veredicto." });
        var person = new ScriptedApprovalPrompt();
        IReadOnlyList<AgentDefinition> roster = [.. Roster().Select(a => a.Id.Value == "ejecutor" ? a with { Memory = MemoryPolicy.ReadSelected } : a)];
        var packs = new List<(ContextPackRequest Request, ContextPack Pack)>();
        var services = Phase4Composition.Services(paths, person, Phase4ScriptedProviders.Over(claude, codex)).WithoutCliProbes();
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        AddIndex(services, Path.Combine(paths.Root, "index-local"), model);
        services.Replace(ServiceDescriptor.Singleton<IContextPacker>(sp => new RecordingPacker(new ContextPacker(sp.GetRequiredService<IProjectMemory>(), sp.GetService<ISemanticIndex>()), packs)));
        await using var app = await Phase4App.OpenAsync(repo.Root, services, roster);

        // Yesterday: a known error recorded in the memory, another one about something else (the tier of known errors goes by
        // relevance, so it stays out), and a day of the journal the person trusts.
        var yesterday = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var memory = app.Get<IProjectMemory>();
        var knownError = await memory.RecordAsync(
            app.Project,
            new MemoryDraft(MemoryKind.KnownError, "Facturas", byMeaning
                ? "Test en rojo en el módulo de facturas por el redondeo (marca ERR-12)."
                : "La exportación de facturas falla con los importes negativos por el redondeo (marca ERR-12).", MemoryOrigin.System, "nota:1"),
            Ct);
        var unrelated = await memory.RecordAsync(
            app.Project,
            new MemoryDraft(MemoryKind.KnownError, "Interfaz", "Los iconos de la barra superior se ven borrosos con el escalado de pantalla al 150 %.", MemoryOrigin.System, "nota:2"),
            Ct);
        AppendHookDay(app.Project.RootPath, yesterday, "10:15:00", "La exportación de facturas a CSV usa punto y coma como separador (marca BIT-12).");
        var derived = await app.Get<IMemoryDerivation>().DeriveAsync(app.Project, Ct);
        Assert.True(derived.Completed, derived.Reason);
        var journal = Assert.Single(derived.Recorded, e => e.Kind == MemoryKind.JournalEntry && e.Content.Contains("BIT-12", StringComparison.Ordinal));
        await app.Get<IJournalTrust>().SetAsync(app.Project, JournalTrustScope.Day, yesterday, true, ScriptedApprovalPrompt.Person, "Mi bitácora de ayer.", Ct);
        var recalled = await memory.RecallAsync(app.Project, new MemoryQuery(Kinds: [MemoryKind.JournalEntry, MemoryKind.KnownError]), Ct);
        Assert.Equal(MemoryTrust.Trusted, Assert.Single(recalled, e => e.Id == journal.Id).Trust);
        Assert.Equal(yesterday, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Assert.Single(recalled, e => e.Id == knownError.Id).CreatedAt, clock.LocalTimeZone).DateTime));
        await app.Get<ISemanticIndex>().RefreshAsync(app.Project, Ct);

        // Today: a run whose task is about it, said by its words, or with other words only the meaning relates.
        clock.Shift = TimeSpan.Zero;
        var objective = byMeaning ? "Arreglar las pruebas que fallan" : "Revisar la exportación de facturas a CSV y los importes negativos";
        var plan = Plan(Work("T1", scope: ["src/T1/"]) with { Objective = objective }, Node("R1", WorkTaskKind.Review).After(On("T1")));
        var request = await app.RunRequestAsync("Que el ejecutor tenga en cuenta lo de ayer.", Requirements(), plan, new VerificationProfile("sin verificación", []));
        await using var run = await app.Orchestrator.StartAsync(request, Ct);
        var result = await run.Completion.WaitAsync(Guard, Ct);
        var log = await app.LoadLogAsync();
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(log));
        var starts = await app.PayloadsAsync<AgentRunStartedPayload>(run.Id.Value);

        // The executor: what yesterday left, each in its tier, ranked by relevance, and the pack says how it ranked.
        (ContextPackRequest Request, ContextPack Pack)[] recorded;
        lock (packs)
        {
            recorded = [.. packs];
        }

        var (asked, pack) = Assert.Single(recorded, p => p.Request.Agent.Id.Value == "ejecutor");
        Assert.Equal(objective, asked.Focus);
        Assert.Equal(ContextTier.KnownIssue, Assert.Single(pack.Items, i => i.Id == knownError.Id).Tier);
        if (byMeaning)
        {
            Assert.Equal(SemanticSearchMode.Hybrid, pack.Relevance?.Mode);
            Assert.NotNull(pack.Relevance!.Cut);

            // Only the meaning relates them: the memory searched by its words, as before the index, does not find it. (With
            // three fragments, the cut of e5 also lets the other known error in: what stays out by meaning is F12-4's to
            // measure, with its 50 questions and its distractors, not this scenario's.)
            var byWords = await new ContextPacker(memory).PackAsync(app.Project, asked, Ct);
            Assert.DoesNotContain(byWords.Items, i => i.Id == knownError.Id);
        }
        else
        {
            // By words: what yesterday left about the task, each in its tier, and the known error about something else out.
            Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.ModelMissing), pack.Relevance);
            Assert.Equal(ContextTier.History, Assert.Single(pack.Items, i => i.Id == journal.Id).Tier);
            Assert.DoesNotContain(pack.Items, i => i.Id == unrelated.Id);
        }

        var executorStart = Assert.Single(starts, s => s.Payload.Task == new TaskId("T1")).Payload;
        var reference = Assert.IsType<ContextPackReference>(executorStart.ContextPack);
        var expected = pack.ToReference();
        Assert.Equal((expected.Sha256, expected.Characters), (reference.Sha256, reference.Characters));
        Assert.Equal(expected.Entries, reference.Entries);
        Assert.Equal(expected.Omitted, reference.Omitted);
        Assert.Contains(knownError.Id, reference.Entries);
        var executor = Assert.Single(claude.Script.Starts).Request;
        Assert.Contains("ERR-12", executor.Prompt, StringComparison.Ordinal);

        // F12-8: the judge of the run gets no pack and none of it, and the packer was not even asked for it.
        Assert.DoesNotContain(recorded, p => p.Request.Agent.Role is AgentRole.Judge or AgentRole.Auditor);
        var judgeStart = Assert.Single(starts, s => s.Payload.Purpose == AgentRunPurpose.Judge).Payload;
        Assert.Null(judgeStart.ContextPack);
        var judge = Assert.Single(codex.Script.Starts, s => s.Request.Agent.Id.Value == "juez").Request;
        Assert.DoesNotContain("<project_memory", judge.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ERR-12", judge.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("BIT-12", judge.Prompt, StringComparison.Ordinal);
        Assert.True(judge.McpServers is null or { Count: 0 });
    }

    // ---- the index deleted is rebuilt the same -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_index_deleted_is_rebuilt_the_same(bool byMeaning)
    {
        var model = ModelOrSkip(byMeaning);
        using var repo = TempGitRepo.Create();
        var paths = Phase4Paths.Beside(repo.Root);
        var services = Phase4Composition.Services(paths, new ScriptedApprovalPrompt()).WithoutCliProbes();
        AddIndex(services, Path.Combine(paths.Root, "index-local"), model);
        await using var app = await Phase4App.OpenAsync(repo.Root, services);
        var memory = app.Get<IProjectMemory>();
        string[] texts =
        [
            "Test en rojo en el módulo de facturas por el redondeo de los importes negativos.",
            "Los botones de la barra superior usan iconos de Segoe Fluent.",
            "La exportación de facturas a CSV usa punto y coma como separador.",
            "Las pruebas de integración necesitan git 2.45 o posterior en el PATH.",
            "El juez de una revisión es siempre de otro proveedor que los ejecutores.",
            "Los importes se guardan en decimal, nunca en double, para no perder céntimos.",
        ];
        foreach (var (text, i) in texts.Select((t, i) => (t, i)))
        {
            await memory.RecordAsync(app.Project, new MemoryDraft(i % 2 == 0 ? MemoryKind.KnownError : MemoryKind.Decision, "Tema " + i, text, MemoryOrigin.System, "nota:" + i), Ct);
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        AppendHookDay(app.Project.RootPath, today.AddDays(-1), "09:00:00", "Las pruebas de facturas fallan con importes negativos: falta redondear a dos decimales.");
        Assert.True((await app.Get<IMemoryDerivation>().DeriveAsync(app.Project, Ct)).Completed);
        await app.Get<IJournalTrust>().SetAsync(app.Project, JournalTrustScope.Project, null, true, ScriptedApprovalPrompt.Person, null, Ct);

        var index = app.Get<ISemanticIndex>();
        var store = app.Get<SemanticIndexStore>();
        string[] queries = ["facturas", "importes negativos", "pruebas que fallan", "iconos de la interfaz", "qué tipo numérico usamos para el dinero"];

        var built = await index.RefreshAsync(app.Project, Ct);
        Assert.True(built.Completed, built.Reason);
        var before = await StateAsync(store, app.Project);
        var resultsBefore = await ResultsAsync(index, app.Project, queries);
        var file = store.IndexFileOf(app.Project);
        Assert.True(File.Exists(file));

        await index.DeleteAsync(app.Project, Ct);
        Assert.False(File.Exists(file), "The index was not deleted.");
        var rebuilt = await index.RefreshAsync(app.Project, Ct);
        Assert.True(rebuilt.Completed, rebuilt.Reason);
        var after = await StateAsync(store, app.Project);
        var resultsAfter = await ResultsAsync(index, app.Project, queries);

        // The same fragments with the same fingerprints (and, with the model, vectors of the same model), and the same order.
        Assert.Equal(built.Added, rebuilt.Added);
        Assert.Equal(before, after);
        Assert.True(before.Count >= texts.Length + 1, $"Only {before.Count} fragments were indexed.");
        Assert.Equal(resultsBefore, resultsAfter);
        var mode = byMeaning ? SemanticSearchMode.Hybrid : SemanticSearchMode.WordsOnly;
        Assert.All(resultsAfter, r => Assert.Equal(mode, r.Mode));
        Assert.All(after, f => Assert.Equal(byMeaning, f.ModelFingerprint is not null));
        Assert.Contains(resultsAfter, r => r.Ids.Count > 0);
    }

    // ---- what both scenarios share --------------------------------------------------------------------------------------

    /// <summary>The folder of the model on disk for a run by meaning (skipped without it); null for a run by words.</summary>
    private static string? ModelOrSkip(bool byMeaning)
    {
        if (!byMeaning)
        {
            return null;
        }

        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(LocalModel.DirectoryVariable) is { Length: > 0 } && LocalModel.IsOnDisk(Path.GetTempPath(), out _),
            $"Set {LocalModel.DirectoryVariable} to the folder of the model to run this scenario by meaning.");
        LocalModel.IsOnDisk(Path.GetTempPath(), out var directory);
        return directory;
    }

    /// <summary>
    /// The index of WP2 and WP3 as the app composes it (<c>SemanticIndexServices</c>), over the folders of the test: one store,
    /// and the local model in <paramref name="modelDirectory"/>, or in a folder that does not hold it (by words only).
    /// </summary>
    private static void AddIndex(IServiceCollection services, string localAppData, string? modelDirectory)
    {
        var location = modelDirectory is null
            ? EmbeddingModelLocation.Under(localAppData, EmbeddingModels.MultilingualE5Small)
            : new EmbeddingModelLocation(modelDirectory, EmbeddingModels.MultilingualE5Small);
        services.AddSingleton(new SemanticIndexOptions(localAppData));
        services.AddSingleton<SemanticIndexStore>();
        services.AddSingleton<ISemanticIndexStore>(sp => sp.GetRequiredService<SemanticIndexStore>());
        services.AddSingleton<IEmbedderSource>(_ => new OnnxEmbedderSource(location));
        services.AddAgentStudioSemanticIndex();
    }

    /// <summary>Appends a day of the journal as the hook of development writes it (an author that is not Agent Studio).</summary>
    private static void AppendHookDay(string projectRoot, DateOnly day, string time, string excerpt)
    {
        var folder = Directory.CreateDirectory(Path.Combine(projectRoot, ".agentstudio", "bitacora")).FullName;
        var file = Path.Combine(folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
        var text = new StringBuilder()
            .Append("# Bitácora · ").Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("\r\n\r\n")
            .Append("Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.\r\n")
            .Append("Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.\r\n\r\n")
            .Append("- **").Append(time).Append("** · sesión `eb1c6494` · #prompt #facturas · «").Append(excerpt).Append("»\r\n");
        File.AppendAllText(file, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>What the index holds, fragment by fragment: its id, its fingerprint and the model of its vector.</summary>
    private static async Task<List<IndexedFragmentState>> StateAsync(ISemanticIndexStore store, ProjectContext project) =>
        [.. (await store.ReadStateAsync(project, Ct)).OrderBy(s => s.Id, StringComparer.Ordinal)];

    /// <summary>The results of each query, in their order, by the ids of their fragments, and how the search went.</summary>
    private static async Task<List<SearchOrder>> ResultsAsync(ISemanticIndex index, ProjectContext project, IReadOnlyList<string> queries)
    {
        var results = new List<SearchOrder>();
        foreach (var query in queries)
        {
            var found = await index.SearchAsync(project, new SemanticQuery(query), Ct);
            results.Add(new SearchOrder(query, found.Mode, [.. found.Hits.Select(h => h.Fragment.Id)]));
        }

        return results;
    }

    /// <summary>The order of the results of one query: compared whole, so the same ids in another order are another result.</summary>
    private sealed record SearchOrder(string Query, SemanticSearchMode Mode, IReadOnlyList<string> Ids)
    {
        public bool Equals(SearchOrder? other) =>
            other is not null && Query == other.Query && Mode == other.Mode && Ids.SequenceEqual(other.Ids, StringComparer.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Query, Mode, Ids.Count);
    }

    /// <summary>The system clock, moved back by <see cref="Shift"/>: the memory of «yesterday» is recorded with the clock a day back.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() - Shift;
    }

    /// <summary>The real packer, with each pack it gives recorded with what it was asked.</summary>
    private sealed class RecordingPacker(IContextPacker inner, List<(ContextPackRequest Request, ContextPack Pack)> packs) : IContextPacker
    {
        public async Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            var pack = await inner.PackAsync(project, request, ct);
            lock (packs)
            {
                packs.Add((request, pack));
            }

            return pack;
        }
    }
}
