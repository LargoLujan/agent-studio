using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Tests.Domain.Semantic;

/// <summary>
/// The fragments of the semantic index (docs/17 D-01 and D-04, F12-3): they cite an anchored source, keep at most 1 500
/// characters, and their id and fingerprint depend only on what they say, so rebuilding the index gives the same ones.
/// </summary>
public sealed class SemanticFragmentTests
{
    private static readonly string HashA = ContentHash.Sha256Hex("a");
    private static readonly string HashB = ContentHash.Sha256Hex("b");
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void The_id_names_the_source_and_the_part()
    {
        Assert.Equal("mem:mem-20261030-0001#0", new SemanticFragment(FragmentSource.Memory("mem-20261030-0001", HashA), 0, "Decisión: el juez es de otro vendor.", At).Id);
        Assert.Equal("log:42#3", new SemanticFragment(FragmentSource.LogEntry(42, HashA), 3, "Plan aprobado.", At).Id);
        Assert.Equal("log:42#3", SemanticFragment.IdOf(FragmentSource.LogEntry(42, HashB), 3));
    }

    [Fact]
    public void The_fingerprint_depends_on_what_the_fragment_says_and_not_on_its_time()
    {
        var fragment = new SemanticFragment(FragmentSource.Memory("m1", HashA), 0, "texto", At);

        Assert.True(ContentHash.IsSha256Hex(fragment.Sha256));
        Assert.Equal(fragment.Sha256, new SemanticFragment(FragmentSource.Memory("m1", HashA), 0, "texto", At.AddDays(3)).Sha256);
        Assert.NotEqual(fragment.Sha256, new SemanticFragment(FragmentSource.Memory("m1", HashA), 0, "texto.", At).Sha256);
        Assert.NotEqual(fragment.Sha256, new SemanticFragment(FragmentSource.Memory("m1", HashA), 1, "texto", At).Sha256);
        Assert.NotEqual(fragment.Sha256, new SemanticFragment(FragmentSource.Memory("m1", HashB), 0, "texto", At).Sha256);
        Assert.NotEqual(fragment.Sha256, new SemanticFragment(FragmentSource.Memory("m2", HashA), 0, "texto", At).Sha256);
        Assert.NotEqual(fragment.Sha256, new SemanticFragment(new FragmentSource(FragmentSourceKind.LogEntry, "m1", HashA), 0, "texto", At).Sha256);
    }

    [Fact]
    public void A_fragment_keeps_at_most_1500_characters_of_text()
    {
        var source = FragmentSource.Memory("m1", HashA);

        Assert.Equal(1500, SemanticFragment.MaxCharacters);
        Assert.Equal(1500, new SemanticFragment(source, 0, new string('x', 1500), At).Text.Length);
        Assert.Throws<ArgumentException>(() => new SemanticFragment(source, 0, new string('x', 1501), At));
        Assert.Throws<ArgumentException>(() => new SemanticFragment(source, 0, "  ", At));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticFragment(source, -1, "texto", At));
    }

    [Fact]
    public void A_source_is_only_what_the_log_anchors()
    {
        Assert.Throws<ArgumentException>(() => FragmentSource.Memory("m1", "not-a-hash"));
        Assert.Throws<ArgumentException>(() => FragmentSource.Memory("m1", HashA.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => FragmentSource.Memory("m 1", HashA));
        Assert.Throws<ArgumentException>(() => FragmentSource.Memory(" ", HashA));
        Assert.Throws<ArgumentOutOfRangeException>(() => FragmentSource.LogEntry(0, HashA));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FragmentSource((FragmentSourceKind)7, "m1", HashA));

        var source = FragmentSource.LogEntry(7, HashB);
        Assert.Equal((FragmentSourceKind.LogEntry, "7", HashB), (source.Kind, source.Reference, source.Sha256));
    }
}
