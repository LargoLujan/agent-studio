using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Memory.Derivation;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// The coded form of the derived memory (docs/17 D-47 and D-48, ADR-0054 addendum of Phase 14): what the screens say in the language of
/// the person says, in Spanish, exactly what the anchored entry says; every code has its text in both languages with the same markers;
/// and a part that is not exactly what the facts of the log give is never translated, the anchored words are shown in its place.
/// </summary>
public sealed partial class DerivedUserTextsTests
{
    private const string TextPrefix = "Text_memory_";

    /// <summary>
    /// The one Spanish word the screens say differently from the anchored text: the guide of texts asks for «límite de uso», not «cuota»
    /// (D-52), and the entries are anchored, and go to the agents, with the word they were written with.
    /// </summary>
    private static readonly (string Anchored, string Screen) Glossary = ("esperando cuota", "esperando el límite de uso");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(DerivationWorld World, IReadOnlyList<MemoryEntry> Entries, IReadOnlyDictionary<string, MemoryEntryText> Coded)> DeriveAllAsync()
    {
        var world = await DerivationWorld.CreateAsync();
        await DerivedTextsScenario.WriteAsync(world);
        var result = await world.DeriveAsync();
        Assert.True(result.Completed, result.Reason);
        var coded = await world.Derivation.ReadAsync(world.Project, result.Recorded, Ct);
        return (world, result.Recorded, coded);
    }

    /// <summary>Every piece and sentence a coded text is made of, itself included.</summary>
    private static IEnumerable<UserText> Flatten(UserText text) =>
        new[] { text }.Concat(text.Arguments.OfType<UserText>().SelectMany(Flatten));

    [Fact]
    public async Task Every_entry_the_system_wrote_has_a_coded_topic_and_content_that_say_in_Spanish_what_it_holds()
    {
        var (world, entries, coded) = await DeriveAllAsync();
        using (world)
        {
            var system = entries.Where(e => e.Origin == MemoryOrigin.System).ToList();
            Assert.True(system.Count >= 90, $"The scenario wrote only {system.Count} system entries.");
            foreach (var entry in system)
            {
                var text = Assert.Contains(entry.Id, coded);
                var topic = Assert.IsType<UserText>(text.Topic);
                var content = Assert.IsType<UserText>(text.Content);
                Assert.Equal(entry.Topic, Spanish.RenderNested(topic));
                Assert.Equal(entry.Content.Replace(Glossary.Anchored, Glossary.Screen, StringComparison.Ordinal), Spanish.RenderNested(content));
            }

            // What an agent wrote is never translated; only the topic, which the system wrote, is.
            foreach (var entry in entries.Where(e => e.Origin == MemoryOrigin.Agent))
            {
                var text = Assert.Contains(entry.Id, coded);
                Assert.Equal(entry.Topic, Spanish.RenderNested(Assert.IsType<UserText>(text.Topic)));
                Assert.Null(text.Content);
            }
        }
    }

    [Fact]
    public async Task The_coded_texts_say_it_in_English_with_no_missing_text()
    {
        var (world, entries, coded) = await DeriveAllAsync();
        using (world)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in coded.Values.SelectMany(c => new[] { c.Topic, c.Content }.OfType<UserText>()))
            {
                Assert.DoesNotContain("[Text_", English.RenderNested(text), StringComparison.Ordinal);
                foreach (var piece in Flatten(text))
                {
                    seen.Add(piece.Code);
                }
            }

            // A sentence is not the Spanish one: only a topic made of an identifier and a mode may read the same.
            Assert.All(coded.Values.Select(c => c.Content).OfType<UserText>(), c => Assert.NotEqual(Spanish.RenderNested(c), English.RenderNested(c)));

            // The scenario goes over every sentence of the derivation (the quoted question has its own test below).
            Assert.True(seen.Count >= 120, $"The scenario reached only {seen.Count} codes.");
            var sample = entries.First(e => e.Topic.StartsWith("Plan de la ejecución 20261101-000001", StringComparison.Ordinal));
            Assert.Equal("Plan of run 20261101-000001", English.RenderNested(coded[sample.Id].Topic!));
            Assert.Equal(
                "The person approved plan " + sample.Content.Split(' ')[5] + " of run 20261101-000001 (1 task) on 2026-01-01 at 10:00 UTC.",
                English.RenderNested(coded[sample.Id].Content!));
        }
    }

    /// <summary>The time of an entry is always UTC, said so, whatever the culture of the screen.</summary>
    [Fact]
    public void A_time_is_said_in_UTC_in_every_language_and_never_in_the_zone_of_the_machine()
    {
        var at = new DateTimeOffset(2026, 10, 29, 23, 40, 0, TimeSpan.FromHours(1));
        var code = new UserText("memory.when", [at.ToUniversalTime()]);

        Assert.Equal("29/10/2026 a las 22:40 UTC", Spanish.Render(code));
        Assert.Equal("2026-10-29 at 22:40 UTC", English.Render(code));
    }

    /// <summary>
    /// A part is coded only when its Spanish text is exactly what the entry holds: an entry cut, redacted or written by another
    /// version shows as it was anchored, and the other part, if it still matches, is coded.
    /// </summary>
    [Fact]
    public async Task A_part_that_is_not_what_the_facts_give_is_left_as_anchored()
    {
        var (world, entries, _) = await DeriveAllAsync();
        using (world)
        {
            var entry = entries.First(e => e.Origin == MemoryOrigin.System && e.Kind == MemoryKind.Verdict);
            var changed = entry with { Content = entry.Content + " (editado)", Topic = entry.Topic + " (editado)" };
            var cut = entry with { Content = entry.Content[..^1] };

            Assert.Empty(await world.Derivation.ReadAsync(world.Project, [changed], Ct));
            var onlyTopic = Assert.Contains(cut.Id, await world.Derivation.ReadAsync(world.Project, [cut], Ct));
            Assert.NotNull(onlyTopic.Topic);
            Assert.Null(onlyTopic.Content);
        }
    }

    /// <summary>The sentence about the question of a debate is coded in each of its five forms; the question is quoted as it was anchored.</summary>
    [Fact]
    public async Task The_question_of_a_debate_is_coded_in_its_five_forms_and_quoted_as_anchored()
    {
        var (world, entries, coded) = await DeriveAllAsync();
        using (world)
        {
            var summaries = entries.Where(e => e.Kind == MemoryKind.DebateSummary).ToList();
            Assert.Contains(summaries, s => s.Content.EndsWith("Pregunta: «¿Qué hacemos?»", StringComparison.Ordinal));
            Assert.Contains(summaries, s => s.Content.EndsWith("La pregunta está vacía.", StringComparison.Ordinal));
            Assert.Contains(summaries, s => s.Content.EndsWith("no la escribió una persona: no se cita.", StringComparison.Ordinal));
            var quoted = summaries.First(s => s.Content.EndsWith("Pregunta: «¿Qué hacemos?»", StringComparison.Ordinal));

            Assert.EndsWith("Question: “¿Qué hacemos?”", English.RenderNested(coded[quoted.Id].Content!), StringComparison.Ordinal);
            var empty = summaries.First(s => s.Content.EndsWith("La pregunta está vacía.", StringComparison.Ordinal));
            Assert.EndsWith("The question is empty.", English.RenderNested(coded[empty.Id].Content!), StringComparison.Ordinal);
            var byAgent = summaries.First(s => s.Content.EndsWith("no la escribió una persona: no se cita.", StringComparison.Ordinal));
            Assert.EndsWith("was not written by a person: it is not quoted.", English.RenderNested(coded[byAgent.Id].Content!), StringComparison.Ordinal);
            Assert.All(summaries, s => Assert.Equal(s.Content, Spanish.RenderNested(coded[s.Id].Content!)));
        }
    }

    [Fact]
    public void The_topic_of_a_journal_entry_is_coded_from_the_topic_itself_and_only_when_it_is_the_one_derived()
    {
        var topic = DerivationTexts.JournalTopic(new DateOnly(2026, 11, 5), "P-0007 · notas");

        var code = DerivationUserTexts.TryJournalTopic(topic);

        Assert.NotNull(code);
        Assert.Equal(topic, Spanish.RenderNested(code));
        Assert.Equal("Journal of 2026-11-05 · P-0007 · notas", English.RenderNested(code));
        Assert.Null(DerivationUserTexts.TryJournalTopic("Bitácora del 2026-11-05"));
        Assert.Null(DerivationUserTexts.TryJournalTopic("Bitácora del 5 de noviembre · P-0007"));
        Assert.Null(DerivationUserTexts.TryJournalTopic("Otro tema"));
    }

    [Fact]
    public void The_reasons_say_in_Spanish_what_the_anchored_text_of_the_log_says_and_in_English_the_same()
    {
        foreach (var reason in new[] { DerivationTexts.BusyReason, DerivationTexts.StoppedReason, DerivationTexts.UnanchoredReason, DerivationTexts.BrokenMidwayReason })
        {
            var code = DerivationUserTexts.ReasonOf(reason);
            Assert.NotNull(code);
            Assert.Equal(reason, Spanish.Render(code));
            Assert.DoesNotContain("[Text_", English.Render(code), StringComparison.Ordinal);
            Assert.NotEqual(reason, English.Render(code));
        }

        var broken = new DecisionLogVerification(false, 12, "x");
        var withEntry = DerivationUserTexts.NotVerifiedReason(broken);
        Assert.Equal(DerivationTexts.NotVerifiedReason(broken), Spanish.Render(withEntry));
        Assert.Equal("The decision log does not verify (entry 12). No memory is derived.", English.Render(withEntry));
        var without = DerivationUserTexts.NotVerifiedReason(new DecisionLogVerification(false, null, "x"));
        Assert.Equal(DerivationTexts.NotVerifiedReason(new DecisionLogVerification(false, null, "x")), Spanish.Render(without));
        Assert.Null(DerivationUserTexts.ReasonOf("Otra razón"));
        Assert.Equal(
            "Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine.",
            Spanish.Render(DerivationUserTexts.ExportBusyReason()));
    }

    /// <summary>Each code has its text in both languages with the same markers: the number of an argument, and how a time or a number is written.</summary>
    [Fact]
    public void Every_code_of_the_memory_has_its_text_in_both_languages_with_the_same_markers()
    {
        var files = ResourceFiles()["MemoryStrings"];
        var spanish = Read(files.Neutral).Where(p => p.Key.StartsWith(TextPrefix, StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
        var english = Read(files.Satellites["en"]).Where(p => p.Key.StartsWith(TextPrefix, StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);

        Assert.True(spanish.Count >= 200, $"Only {spanish.Count} texts of the memory.");
        Assert.Equal(spanish.Keys.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        var different = spanish.Keys.Where(k => !Markers(spanish[k]).SequenceEqual(Markers(english[k]))).ToList();
        Assert.True(different.Count == 0, "Texts whose markers differ between the languages: " + string.Join(", ", different));
        Assert.All(spanish.Concat(english), p => Assert.False(string.IsNullOrWhiteSpace(p.Value), p.Key));
    }

    /// <summary>Every code the source names, and every value of the enumerations the sentences say by name, has its text.</summary>
    [Fact]
    public void Every_code_the_source_names_and_every_enumeration_value_it_says_has_a_text()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "Derivation/DerivationUserTexts.cs", "Export/MemoryMarkdownExporter.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "AgentStudio.Memory", file));
            foreach (Match match in CodeLiteral().Matches(source))
            {
                codes.Add(match.Groups[1].Value);
            }
        }

        foreach (var (prefix, values) in new (string, IEnumerable<string>)[]
        {
            ("memory.phase.", Enum.GetNames<RunPhase>()),
            ("memory.status.", Enum.GetNames<WorkTaskStatus>()),
            ("memory.task-kind.", Enum.GetNames<WorkTaskKind>()),
            ("memory.decision.", Enum.GetNames<VerdictDecision>()),
            ("memory.mode.", Enum.GetNames<DebateMode>()),
            ("memory.outcome.", Enum.GetNames<DebateOutcome>()),
            ("memory.export.kind.", Enum.GetNames<MemoryKind>()),
            ("memory.export.title.", Enum.GetNames<MemoryKind>().Where(n => n != nameof(MemoryKind.JournalEntry))),
        })
        {
            foreach (var value in values)
            {
                codes.Add(prefix + DerivationUserTexts.Kebab(value));
            }
        }

        foreach (var unit in new[] { "run", "debate", "audit" })
        {
            foreach (var form in new[] { "unit", "of-unit", "in-unit" })
            {
                codes.Add($"memory.{form}.{unit}");
            }
        }

        foreach (var noun in new[] { "tasks", "files", "attempts", "commits", "findings" })
        {
            codes.Add($"memory.count.{noun}-one");
            codes.Add($"memory.count.{noun}-other");
        }

        Assert.True(codes.Count >= 200, $"Only {codes.Count} codes were found.");
        var missing = new List<string>();
        foreach (var code in codes)
        {
            var key = UserText.KeyOf(code);
            foreach (var (language, provider) in new[] { ("es", Spanish), ("en", English) })
            {
                if (!provider.Has(key))
                {
                    missing.Add($"{language}: {key}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Codes without a text:\n" + string.Join('\n', missing));
    }

    private static List<string> Markers(string text) =>
        [.. Marker().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    /// <summary>A placeholder: <c>{0}</c>, or <c>{0:dd'/'MM}</c> with the way a time is written, which differs by language, so only its argument counts.</summary>
    [GeneratedRegex(@"\{\d+")]
    private static partial Regex Marker();

    [GeneratedRegex("\"(memory\\.[a-z0-9]+(?:[.-][a-z0-9]+)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex CodeLiteral();
}
