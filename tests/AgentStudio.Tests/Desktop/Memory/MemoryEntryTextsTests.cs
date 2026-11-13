using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Memory;
using AgentStudio.Tests.Memory;
using AgentStudio.Tests.Memory.Derivation;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// What the screens of the memory say of the entries the derivation wrote (docs/17 D-47 and D-48; F14-1; ADR-0054 addendum of Phase 14):
/// in the language of the interface, from the coded form of the entry, while the entry itself, its anchored words and its hash, stay
/// as they were. Without a coded form, the screen shows the anchored words; and the export goes out in the language in use.
/// </summary>
public sealed class MemoryEntryTextsTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_the_interface_in_English_an_entry_the_system_wrote_reads_in_English_and_keeps_its_anchored_words()
    {
        using var world = await DerivationWorld.CreateAsync();
        var run = world.Run("20261015-a1b2c3");
        var (hash, _) = await run.PlanAsync("Plan.", 1);
        await run.StartAsync(hash, "manuel", ("T1", AgentStudio.Domain.Work.WorkTaskKind.AgentWork));
        var result = await world.DeriveAsync();
        var entry = Assert.Single(result.Recorded, e => e is { Kind: MemoryKind.Decision, Origin: MemoryOrigin.System });
        var coded = Assert.Contains(entry.Id, await world.Derivation.ReadAsync(world.Project, [entry], Ct));

        var english = new MemoryEntryViewModel(entry, TimeZoneInfo.Utc, coded: coded, texts: English);
        var spanish = new MemoryEntryViewModel(entry, TimeZoneInfo.Utc, coded: coded, texts: Spanish);
        var anchored = new MemoryEntryViewModel(entry, TimeZoneInfo.Utc, texts: English);

        Assert.Equal("Plan of run 20261015-a1b2c3", english.Topic);
        Assert.Equal($"The person approved plan {hash[..12]} of run 20261015-a1b2c3 (1 task) on 2026-01-01 at 10:00 UTC.", english.Content);
        Assert.Equal("Decision · Plan of run 20261015-a1b2c3 · Trusted", english.ToString());
        Assert.Equal(entry.Topic, spanish.Topic);
        Assert.Equal(entry.Content, spanish.Content);
        Assert.Equal(entry.Topic, anchored.Topic);
        Assert.Equal(entry.Content, anchored.Content);

        // The anchored entry never changes: what the screen shows is one thing and what is hashed another.
        Assert.Equal(entry, english.Entry);
        Assert.Equal(entry.ContentSha256, MemoryContent.Sha256(entry.Kind, entry.Topic, entry.Content));
    }

    /// <summary>A code the resources of the language do not have shows the anchored words, never an empty line nor a key between brackets.</summary>
    [Fact]
    public void A_coded_text_without_its_resource_shows_the_anchored_words()
    {
        var entry = new MemoryEntry("m1", MemoryKind.Decision, "Tema anclado", "Contenido anclado", MemoryOrigin.System, "registro:r1#1", At, new string('a', 64), MemoryTrust.Trusted, null, null);
        var coded = new MemoryEntryText(new UserText("memory.does-not-exist"), new UserText("memory.neither"));

        var shown = new MemoryEntryViewModel(entry, TimeZoneInfo.Utc, coded: coded, texts: English);

        Assert.Equal("Tema anclado", shown.Topic);
        Assert.Equal("Contenido anclado", shown.Content);
    }

    [Fact]
    public async Task The_list_asks_for_the_coded_form_and_paints_it_in_the_language_in_use()
    {
        var entry = new MemoryEntry("m1", MemoryKind.Decision, "Plan de la ejecución r1", "La persona aprobó el plan.", MemoryOrigin.System, "registro:r1#1", At, new string('a', 64), MemoryTrust.Trusted, null, null);
        var store = new FakeProjectMemory();
        store.Entries.Add(entry);
        var texts = new FakeEntryTexts(new Dictionary<string, MemoryEntryText> { ["m1"] = new(new UserText("memory.topic.plan", ["r1"]), null) });
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            store,
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(EntryTexts: texts));

        await memory.SetProjectAsync(Root);

        var shown = Assert.Single(memory.Entries);
        Assert.Equal(MemoryReasonTexts.Say(new UserText("memory.topic.plan", ["r1"]), "x"), shown.Topic);
        Assert.Equal("La persona aprobó el plan.", shown.Content);
        Assert.Equal([entry.Id], texts.Asked.Select(e => e.Id));
    }

    /// <summary>The coded form is a nicety: if it fails, the entries show as anchored and the screen does not fail.</summary>
    [Fact]
    public async Task A_failure_of_the_coded_form_leaves_the_anchored_words()
    {
        var entry = new MemoryEntry("m1", MemoryKind.Decision, "Plan de la ejecución r1", "Contenido.", MemoryOrigin.System, "registro:r1#1", At, new string('a', 64), MemoryTrust.Trusted, null, null);
        var store = new FakeProjectMemory();
        store.Entries.Add(entry);
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            store,
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(EntryTexts: new FakeEntryTexts(null)));

        await memory.SetProjectAsync(Root);

        Assert.Equal("Plan de la ejecución r1", Assert.Single(memory.Entries).Topic);
        Assert.Null(memory.ErrorMessage);
    }

    [Fact]
    public async Task The_export_goes_out_in_the_language_in_use_whoever_asks_for_it()
    {
        var exporter = new FakeMemoryExporter();
        var upkeep = new ProjectMemoryUpkeep(
            new FakeProjectWorkspace(),
            new FakeMemoryDerivation(new MemoryDerivationResult([], [], [])),
            exporter);

        await upkeep.AfterRunAsync(Root, Ct);

        Assert.Equal([Texts.Current.Culture.Name], exporter.Languages);
    }

    /// <summary>The reasons the layers below give as codes are said in the language in use; one without a code keeps its Spanish text.</summary>
    [Fact]
    public void A_reason_is_said_in_the_language_asked_and_without_a_code_it_keeps_its_text()
    {
        var code = new UserText("memory.reason.export-busy");

        Assert.StartsWith("The project is busy", MemoryReasonTexts.Say(English, code, "Hay una ejecución"), StringComparison.Ordinal);
        Assert.StartsWith("El proyecto está ocupado", MemoryReasonTexts.Say(Spanish, code, "x"), StringComparison.Ordinal);
        Assert.Equal("Hay una ejecución", MemoryReasonTexts.Say(English, null, "Hay una ejecución"));
        Assert.Equal("Hay una ejecución", MemoryReasonTexts.Say(English, new UserText("memory.unknown"), "Hay una ejecución"));
        Assert.Equal(string.Empty, MemoryReasonTexts.Say(English, null, null));
    }

    private sealed class FakeEntryTexts(IReadOnlyDictionary<string, MemoryEntryText>? coded) : IMemoryEntryTexts
    {
        public List<MemoryEntry> Asked { get; } = [];

        public Task<IReadOnlyDictionary<string, MemoryEntryText>> ReadAsync(ProjectContext project, IReadOnlyList<MemoryEntry> entries, CancellationToken ct)
        {
            Asked.AddRange(entries);
            return coded is null ? throw new InvalidOperationException("The log could not be read.") : Task.FromResult(coded);
        }
    }
}
