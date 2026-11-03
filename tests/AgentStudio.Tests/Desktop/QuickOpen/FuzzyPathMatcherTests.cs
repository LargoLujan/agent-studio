using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop.QuickOpen;

public sealed class FuzzyPathMatcherTests
{
    [Theory]
    [InlineData("xyz", "src/Program.cs")]
    [InlineData("ba", "ab")] // the characters must keep their order
    [InlineData("progrram", "Program.cs")] // the path has only one 'r' after the 'g'
    [InlineData("sc", "Program.cs")] // both letters exist, but 's' comes after the 'c'
    public void Match_IsNull_WhenTheQueryIsNotASubsequenceOfThePath(string query, string path) =>
        Assert.Null(FuzzyPathMatcher.Match(query, path));

    [Fact]
    public void Match_IgnoresCase()
    {
        var match = FuzzyPathMatcher.Match("PROGRAM", "src/program.cs");

        Assert.Equal(0, Assert.NotNull(match).Gaps);
    }

    [Fact]
    public void Match_AContiguousQuery_HasNoGaps()
    {
        var match = FuzzyPathMatcher.Match("gram", "src/Program.cs");

        Assert.Equal(0, Assert.NotNull(match).Gaps);
    }

    [Theory]
    [InlineData("pgm", "Program.cs", 2)] // P . . g . . m: two breaks
    [InlineData("proam", "Program.cs", 1)] // pro | am
    [InlineData("prgm", "Program.cs", 2)] // pr | g | m
    [InlineData("srcprog", "src/Program.cs", 1)] // the separator breaks the run: src | prog
    public void Match_CountsEveryBreakBetweenMatchedCharacters(string query, string path, int gaps) =>
        Assert.Equal(gaps, Assert.NotNull(FuzzyPathMatcher.Match(query, path)).Gaps);

    [Theory]
    [InlineData("ab", "a_b_ab")] // the greedy left-most alignment (a0, b2) has a gap; the last "ab" has none
    [InlineData("aa", "a_aa")]
    [InlineData("abc", "a_bc_abc")]
    public void Match_FindsTheMostContiguousAlignment_NotJustTheFirstOne(string query, string path) =>
        Assert.Equal(0, Assert.NotNull(FuzzyPathMatcher.Match(query, path)).Gaps);

    [Theory]
    [InlineData("view", "src/viewer.cs", true)]
    [InlineData("view", "views/other.cs", false)] // the run is in the directory name
    [InlineData("view", "views/view.cs", true)]
    [InlineData("vwr", "src/viewer.cs", true)] // scattered, but entirely inside the file name
    [InlineData("srcprog", "src/Program.cs", false)] // spans directory and file name
    [InlineData("views/ex", "src/views/Explorer.cs", false)] // a separator can only match across the path
    [InlineData("program", "Program.cs", true)] // a file in the project root is all file name
    public void Match_SaysWhetherTheBestAlignmentLivesInTheFileName(string query, string path, bool inFileName) =>
        Assert.Equal(inFileName, Assert.NotNull(FuzzyPathMatcher.Match(query, path)).InFileName);

    [Fact]
    public void Match_ReportsTheLengthOfThePath() =>
        Assert.Equal(14, Assert.NotNull(FuzzyPathMatcher.Match("p", "src/Program.cs")).PathLength);

    [Theory]
    [InlineData("src\\prog", "src/Program.cs")]
    [InlineData("src/prog", "src\\Program.cs")]
    public void Match_TreatsBackslashAndSlashAsTheSameSeparator(string query, string path) =>
        Assert.Equal(0, Assert.NotNull(FuzzyPathMatcher.Match(query, path)).Gaps);

    [Fact]
    public void Match_IgnoresWhitespaceInTheQuery() =>
        Assert.Equal(0, Assert.NotNull(FuzzyPathMatcher.Match("  main view ", "ViewModels/MainViewModel.cs")).Gaps);

    [Fact]
    public void Match_AnEmptyQuery_MatchesAnything() =>
        Assert.NotNull(FuzzyPathMatcher.Match(" ", "src/Program.cs"));

    // ---- Ranking: each test is built so that only the rule it names can explain the expected order. ----

    [Fact]
    public void Rank_PutsContiguousMatchesBeforeScatteredOnes_EvenWhenTheyAreLonger()
    {
        string[] paths = ["a_b_cd.txt", "abcd_with_a_long_name.txt"];

        var ranked = FuzzyPathMatcher.Rank(paths, "abcd", 10);

        Assert.Equal(["abcd_with_a_long_name.txt", "a_b_cd.txt"], ranked.Paths);
    }

    [Fact]
    public void Rank_PutsFewerBreaksFirst_EvenWhenThePathIsLonger()
    {
        // abcd: a_b_c_d.txt has 3 breaks; ab_cd_and_a_longer_name.txt only 1.
        string[] paths = ["a_b_c_d.txt", "ab_cd_and_a_longer_name.txt"];

        var ranked = FuzzyPathMatcher.Rank(paths, "abcd", 10);

        Assert.Equal(["ab_cd_and_a_longer_name.txt", "a_b_c_d.txt"], ranked.Paths);
    }

    [Fact]
    public void Rank_PutsAMatchInTheFileNameBeforeOneInTheDirectory_EvenWhenThePathIsLonger()
    {
        string[] paths = ["util/zzz.cs", "src/deep/Utility.cs"];

        var ranked = FuzzyPathMatcher.Rank(paths, "util", 10);

        Assert.Equal(["src/deep/Utility.cs", "util/zzz.cs"], ranked.Paths);
    }

    [Fact]
    public void Rank_PutsShorterPathsFirst_WhenNothingElseDiffers()
    {
        string[] paths = ["a/b/c/foo.cs", "a/foo.cs", "a/b/foo.cs"];

        var ranked = FuzzyPathMatcher.Rank(paths, "foo", 10);

        Assert.Equal(["a/foo.cs", "a/b/foo.cs", "a/b/c/foo.cs"], ranked.Paths);
    }

    [Fact]
    public void Rank_BreaksTiesAlphabetically_IgnoringCase()
    {
        string[] paths = ["c/foo.cs", "B/foo.cs", "a/foo.cs"];

        var ranked = FuzzyPathMatcher.Rank(paths, "foo", 10);

        Assert.Equal(["a/foo.cs", "B/foo.cs", "c/foo.cs"], ranked.Paths);
    }

    [Fact]
    public void Rank_AppliesTheRulesInOrder_Contiguity_ThenFileName_ThenLength()
    {
        string[] paths =
        [
            "x/y/z/a_b_c.txt", // 2 breaks
            "short/abc.txt/readme.md", // contiguous, in a directory name, long
            "abc/x.md", // contiguous, in a directory name, short
            "very/long/path/to/abc.md", // contiguous, in the file name, long
            "abc.md", // contiguous, in the file name, shortest
        ];

        var ranked = FuzzyPathMatcher.Rank(paths, "abc", 10);

        Assert.Equal(
            ["abc.md", "very/long/path/to/abc.md", "abc/x.md", "short/abc.txt/readme.md", "x/y/z/a_b_c.txt"],
            ranked.Paths);
    }

    [Fact]
    public void Rank_LeavesOutPathsThatDoNotMatch()
    {
        string[] paths = ["src/Program.cs", "README.md", "docs/prog.md"];

        var ranked = FuzzyPathMatcher.Rank(paths, "prog", 10);

        Assert.Equal(["docs/prog.md", "src/Program.cs"], ranked.Paths);
        Assert.Equal(2, ranked.TotalMatches);
    }

    [Fact]
    public void Rank_ReturnsOnlyTheBestMatchesUpToTheLimit_AndCountsAllOfThem()
    {
        string[] paths = ["a/foo.cs", "foo.cs", "a/b/foo.cs", "no.cs"];

        var ranked = FuzzyPathMatcher.Rank(paths, "foo", 2);

        Assert.Equal(["foo.cs", "a/foo.cs"], ranked.Paths);
        Assert.Equal(3, ranked.TotalMatches);
    }

    [Fact]
    public void Rank_WithAnEmptyQuery_KeepsEveryPathShortestFirst()
    {
        string[] paths = ["src/Program.cs", "README.md", "a.cs"];

        var ranked = FuzzyPathMatcher.Rank(paths, string.Empty, 10);

        Assert.Equal(["a.cs", "README.md", "src/Program.cs"], ranked.Paths);
        Assert.Equal(3, ranked.TotalMatches);
    }

    [Fact]
    public void Rank_WithNoPaths_IsEmpty()
    {
        var ranked = FuzzyPathMatcher.Rank([], "x", 10);

        Assert.Empty(ranked.Paths);
        Assert.Equal(0, ranked.TotalMatches);
    }

    [Fact]
    public void Rank_RejectsANegativeLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FuzzyPathMatcher.Rank(["a"], "a", -1));
}
