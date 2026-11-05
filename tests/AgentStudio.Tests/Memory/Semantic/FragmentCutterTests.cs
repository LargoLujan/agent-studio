using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Semantic;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// How an anchored entry becomes fragments (docs/17 D-01, F12-3): at most 1 500 characters each, cut the same way every
/// time, citing the entry by its id and the SHA-256 its announcement carries.
/// </summary>
public sealed class FragmentCutterTests
{
    private static MemoryEntry Entry(string topic, string content, MemoryKind kind = MemoryKind.Decision) =>
        new("mem-0123456789abcdef", kind, topic, content, MemoryOrigin.System, "bitacora:2026-10-20#sha256:" + HashA, T0, MemoryContent.Sha256(kind, topic, content), MemoryTrust.Trusted, null, null);

    [Fact]
    public void A_short_entry_is_one_fragment_with_the_text_the_pack_quotes()
    {
        var entry = Entry("Bitácora del 2026-10-20, 10:42", "Hecho: almacén de evidencias con SHA-256.");

        var fragment = Assert.Single(FragmentCutter.Of(entry));

        Assert.Equal("Bitácora del 2026-10-20, 10:42: Hecho: almacén de evidencias con SHA-256.", fragment.Text);
        Assert.Equal(("mem:mem-0123456789abcdef#0", 0, T0), (fragment.Id, fragment.Part, fragment.At));
        Assert.Equal(FragmentSource.Memory(entry.Id, entry.ContentSha256), fragment.Source);
    }

    [Fact]
    public void A_long_entry_is_cut_at_line_breaks_and_every_piece_keeps_its_topic()
    {
        var lines = Enumerable.Range(1, 60).Select(i => $"  - Hecho {i:D2}: la tarea número {i} quedó integrada con sus pruebas en verde y su informe.").ToList();
        var entry = Entry("Bitácora del 2026-10-21, 09:00", string.Join('\n', lines));

        var fragments = FragmentCutter.Of(entry);

        Assert.True(fragments.Count >= 3, $"{fragments.Count} fragments");
        Assert.All(fragments, f => Assert.True(f.Text.Length <= SemanticFragment.MaxCharacters, $"{f.Text.Length} characters"));
        Assert.All(fragments, f => Assert.StartsWith("Bitácora del 2026-10-21, 09:00: - Hecho ", f.Text, StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, fragments.Count), fragments.Select(f => f.Part));
        Assert.Equal(fragments.Count, fragments.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());

        // Nothing is lost: every line is in exactly one piece, whole.
        foreach (var line in lines)
        {
            Assert.Single(fragments, f => f.Text.Contains(line.Trim(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Cutting_is_deterministic_so_a_rebuilt_index_has_the_same_fingerprints()
    {
        var random = new Random(12);
        for (var round = 0; round < 50; round++)
        {
            var words = Enumerable.Range(0, random.Next(1, 900)).Select(_ => new string('x', random.Next(1, 12)) + (random.Next(9) == 0 ? "\n" : string.Empty));
            var entry = Entry("Tema " + round, string.Join(' ', words));

            var first = FragmentCutter.Of(entry);
            var again = FragmentCutter.Of(entry with { });

            Assert.Equal(first.Select(f => (f.Id, f.Sha256)), again.Select(f => (f.Id, f.Sha256)));
            Assert.All(first, f => Assert.InRange(f.Text.Length, 1, SemanticFragment.MaxCharacters));
        }
    }

    [Fact]
    public void A_word_longer_than_a_fragment_is_cut_without_splitting_a_surrogate_pair()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 2000));

        var pieces = FragmentCutter.Cut(text);

        Assert.All(pieces, p => Assert.True(p.Length <= SemanticFragment.MaxCharacters));
        Assert.All(pieces, p => Assert.False(char.IsLowSurrogate(p[0]) || char.IsHighSurrogate(p[^1])));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public void A_topic_too_long_to_repeat_is_cut_with_the_content()
    {
        var topic = new string('t', 900);
        var entry = Entry(topic, string.Join(' ', Enumerable.Repeat("contenido", 400)));

        var fragments = FragmentCutter.Of(entry);

        Assert.True(fragments.Count >= 2);
        Assert.StartsWith(topic + ": contenido", fragments[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(topic, fragments[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mem 1")]
    [InlineData("")]
    public void An_entry_that_cannot_be_cited_gives_no_fragment(string id)
    {
        Assert.Empty(FragmentCutter.Of(Entry("Tema", "Contenido") with { Id = id }));
        Assert.Empty(FragmentCutter.Of(Entry("Tema", "Contenido") with { ContentSha256 = "no-es-un-hash" }));
    }
}
