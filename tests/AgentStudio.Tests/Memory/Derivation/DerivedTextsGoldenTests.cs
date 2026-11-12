using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// The anchored words of the derived memory never change (docs/17 D-48, ADR-0054 addendum of Phase 14): a log with every shape
/// the derivation writes a text for gives, entry by entry, the topic, the content and the SHA-256 recorded in the golden file, which
/// was written from the code before its texts also had a coded form. A change here rewrites history that is already anchored.
/// </summary>
public sealed partial class DerivedTextsGoldenTests
{
    private const string GoldenName = "derived-entries.golden.txt";

    [Fact]
    public async Task The_topics_the_contents_and_the_hashes_of_the_derived_entries_are_the_anchored_ones()
    {
        using var world = await DerivationWorld.CreateAsync();
        await DerivedTextsScenario.WriteAsync(world);

        var result = await world.DeriveAsync();

        Assert.True(result.Completed, result.Reason);
        Assert.Empty(result.Problems);
        var actual = Dump(result.Recorded);
        if (Environment.GetEnvironmentVariable("AS_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(SourceGolden(), actual, new UTF8Encoding(false));
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Fixtures", GoldenName), Encoding.UTF8).ReplaceLineEndings("\n");
        Assert.Equal(golden, actual);
    }

    /// <summary>One block per entry, in a stable order; the ids of evidence are random and become a mark, so they do not matter.</summary>
    internal static string Dump(IReadOnlyList<MemoryEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries
            .OrderBy(e => Normalize(e.Topic), StringComparer.Ordinal)
            .ThenBy(e => Normalize(e.Content), StringComparer.Ordinal))
        {
            var topic = Normalize(entry.Topic);
            var content = Normalize(entry.Content);
            text.Append("# ").Append(entry.Kind).Append(" / ").Append(entry.Origin).Append(" / ").Append(MemoryContent.Sha256(entry.Kind, topic, content)).Append('\n')
                .Append(topic).Append('\n').Append(content).Append("\n\n");
        }

        return text.ToString();
    }

    private static string Normalize(string text) => EvidenceId().Replace(text, "<id>");

    private static string SourceGolden([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "Fixtures", GoldenName);

    [GeneratedRegex(@"\b[0-9a-f]{32}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceId();
}
