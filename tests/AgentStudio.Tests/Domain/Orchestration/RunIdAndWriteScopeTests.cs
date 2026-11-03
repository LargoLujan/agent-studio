using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Domain.Orchestration;

public sealed class RunIdAndWriteScopeTests
{
    // ---- RunId ----

    [Theory]
    [InlineData("20261015-a1b2c3")]
    [InlineData("run_1")]
    [InlineData("A")]
    public void A_run_id_may_be_used_in_refs_and_paths(string value)
    {
        Assert.Equal(value, new RunId(value).Value);
        Assert.Equal(value, new RunId(value).ToString());
    }

    [Fact]
    public void A_run_id_may_be_64_characters_long_but_not_65()
    {
        Assert.Equal(64, new RunId(new string('a', 64)).Value.Length);
        Assert.ThrowsAny<ArgumentException>(() => new RunId(new string('a', 65)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    [InlineData("a:b")]
    [InlineData("a.b")]
    [InlineData("año")]
    public void A_run_id_rejects_anything_that_is_not_safe_for_refs_and_paths(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RunId(value));
    }

    [Fact]
    public void A_null_run_id_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RunId(null!));
    }

    [Fact]
    public void Run_ids_compare_by_value()
    {
        Assert.Equal(new RunId("r1"), new RunId("r1"));
        Assert.NotEqual(new RunId("r1"), new RunId("r2"));
    }

    // ---- WriteScope.TryNormalize ----

    [Theory]
    [InlineData("src/Calc/", "src/Calc/")]
    [InlineData("src/Calc", "src/Calc")]
    [InlineData("src\\Calc\\", "src/Calc/")]
    [InlineData("./src/Calc/", "src/Calc/")]
    [InlineData("src//Calc///util.cs", "src/Calc/util.cs")]
    [InlineData("  tests/Calc.Tests/  ", "tests/Calc.Tests/")]
    [InlineData("README.md", "README.md")]
    [InlineData("src/./Calc/", "src/Calc/")]
    [InlineData("src/.github/", "src/.github/")]
    [InlineData("a/.gitignore", "a/.gitignore")]
    public void Safe_entries_are_normalised_to_forward_slashes(string entry, string expected)
    {
        Assert.True(WriteScope.TryNormalize(entry, out var normalized, out var problem), problem);
        Assert.Equal(expected, normalized);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("/")]
    public void Empty_entries_and_the_repository_root_are_rejected(string? entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x/")]
    [InlineData("src/../../x")]
    [InlineData("src/..")]
    [InlineData("src\\..\\x")]
    public void Parent_directory_segments_are_rejected(string entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.Contains("..", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:/x/")]
    [InlineData("c:\\x")]
    [InlineData("C:x")]
    public void Absolute_paths_are_rejected(string entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.Contains("absolute", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".git/")]
    [InlineData(".git")]
    [InlineData(".GIT/hooks/")]
    [InlineData("src/.git/")]
    [InlineData(".agentstudio/")]
    [InlineData(".agentstudio/bitacora/2026-10-15.md")]
    [InlineData(".AgentStudio/x")]
    [InlineData(".git./")]
    [InlineData(".git /config")]
    [InlineData("sub\\.git\\")]
    public void The_git_and_agentstudio_folders_are_protected_wherever_they_appear(string entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.Contains("protected", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("src/*.cs")]
    [InlineData("src/a?.cs")]
    [InlineData("src/<x>/")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("file.txt:stream")]
    [InlineData("a\u0001b")]
    public void Wildcards_and_characters_that_windows_does_not_allow_are_rejected(string entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("src/Calc./")]
    [InlineData("src/Calc /x")]
    [InlineData("bad.")]
    public void Segments_that_windows_silently_rewrites_are_rejected(string entry)
    {
        Assert.False(WriteScope.TryNormalize(entry, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    // ---- Contains ----

    [Theory]
    [InlineData("src/Calc/Multiply.cs", true)]
    [InlineData("src/Calc/sub/deep/x.cs", true)]
    [InlineData("SRC/calc/x.cs", true)]
    [InlineData("src/CalcExtra/x.cs", false)]
    [InlineData("src/Calc", false)]
    [InlineData("tests/Calc.Tests/MultiplyTests.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("README.md.bak", false)]
    [InlineData("docs/README.md", false)]
    [InlineData("../src/Calc/x.cs", false)]
    [InlineData("/src/Calc/x.cs", false)]
    [InlineData("", false)]
    public void Contains_checks_a_changed_path_against_directories_and_exact_files(string path, bool expected)
    {
        string[] scope = ["src/Calc/", "tests/Calc.Tests/", "README.md"];

        Assert.Equal(expected, WriteScope.Contains(scope, path));
    }

    [Fact]
    public void An_empty_scope_contains_nothing()
    {
        Assert.False(WriteScope.Contains([], "a.cs"));
    }

    [Fact]
    public void Contains_accepts_backslashes_in_the_changed_path()
    {
        Assert.True(WriteScope.Contains(["src/Calc/"], "src\\Calc\\x.cs"));
    }

    // ---- Overlap ----

    [Theory]
    [InlineData("src/", "src/Calc/", true)]
    [InlineData("src/Calc/", "src/", true)]
    [InlineData("src/Calc/", "src/Calc/", true)]
    [InlineData("src/Calc/", "src/calc/", true)]
    [InlineData("src/Calc/", "src/Calc/x.cs", true)]
    [InlineData("src/Calc/x.cs", "src/Calc/", true)]
    [InlineData("src/Calc/x.cs", "src/Calc/x.cs", true)]
    [InlineData("src/Calc/", "src/CalcExtra/", false)]
    [InlineData("src/Calc/", "src/Calc.cs", false)]
    [InlineData("src/Calc", "src/Calc/", true)]
    [InlineData("src/Calc", "src/Calc/Sub/", true)]
    [InlineData("src/Calc", "src/CalcExtra/", false)]
    [InlineData("src/a.cs", "src/b.cs", false)]
    [InlineData("src/", "tests/", false)]
    public void Two_entries_overlap_when_one_contains_the_other_or_they_are_the_same_file(string a, string b, bool expected)
    {
        Assert.Equal(expected, WriteScope.Overlaps([a], [b]));
        Assert.Equal(expected, WriteScope.Overlaps([b], [a]));
    }

    [Fact]
    public void Scopes_overlap_when_any_pair_of_entries_overlaps()
    {
        Assert.True(WriteScope.Overlaps(["a/", "b/"], ["c/", "b/x/"]));
        Assert.False(WriteScope.Overlaps(["a/", "b/"], ["c/", "d/"]));
        Assert.False(WriteScope.Overlaps([], ["a/"]));
        Assert.False(WriteScope.Overlaps(["a/"], []));
    }

    [Fact]
    public void Overlap_is_symmetric_and_never_misses_a_path_that_both_scopes_admit()
    {
        // Overlaps is deliberately conservative (an entry without a trailing slash may be a folder), so the property is
        // soundness: if some path is admitted by both scopes, they must be reported as overlapping.
        string[] segments = ["src", "Src", "a"]; // few distinct names so that prefixes and equal entries are common
        var random = new Random(26101501);
        var overlapping = 0;
        var disjoint = 0;
        var witnessed = 0;
        for (var i = 0; i < 2000; i++)
        {
            var x = RandomEntry(random, segments);
            var y = RandomEntry(random, segments);
            var overlap = WriteScope.Overlaps([x], [y]);
            Assert.Equal(overlap, WriteScope.Overlaps([y], [x]));
            Assert.True(WriteScope.Overlaps([x], [x]));

            var witness = Witness(x, y);
            if (witness is not null)
            {
                witnessed++;
                Assert.True(overlap, $"{x} and {y} both admit {witness} but were reported as disjoint.");
            }

            if (overlap)
            {
                overlapping++;
            }
            else
            {
                disjoint++;
            }
        }

        Assert.True(overlapping > 200 && disjoint > 200 && witnessed > 100, "The generator must produce overlapping, disjoint and witnessed pairs.");
    }

    private static string RandomEntry(Random random, string[] segments)
    {
        var depth = random.Next(1, 4);
        var path = string.Join('/', Enumerable.Range(0, depth).Select(_ => segments[random.Next(segments.Length)]));
        return random.Next(2) == 0 ? path + "/" : path + ".cs";
    }

    /// <summary>A file path that both entries admit, or null: each entry, made into a file when it is a folder.</summary>
    private static string? Witness(string x, string y)
    {
        foreach (var candidate in new[] { x, y })
        {
            var path = candidate.EndsWith('/') ? candidate + "file.cs" : candidate;
            if (WriteScope.Contains([x], path) && WriteScope.Contains([y], path))
            {
                return path;
            }
        }

        return null;
    }
}
