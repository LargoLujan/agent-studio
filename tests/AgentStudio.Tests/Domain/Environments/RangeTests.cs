using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-07 (WP-E): the ranges of semver (<c>engines</c>, <c>.nvmrc</c>) and the specifiers of PEP 440 (<c>requires-python</c>) evaluated by Agent Studio. The rule
/// that matters: what the evaluator does not understand is «not comparable», never «satisfied», and no text makes it throw.
/// </summary>
public sealed class RangeTests
{
    private static ToolVersion V(string text) => ToolVersion.Parse(text);

    // ---- semver ----

    [Theory]
    [InlineData(">=22", "22.0.0", RangeVerdict.Satisfied)]
    [InlineData(">=22", "21.9.9", RangeVerdict.NotSatisfied)]
    [InlineData(">=22.11.0 <23", "22.11.0", RangeVerdict.Satisfied)]
    [InlineData(">=22.11.0 <23", "23.0.0", RangeVerdict.NotSatisfied)]
    [InlineData(">= 18 || >= 20.1", "16.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("^18 || ^20 || ^22", "20.11.1", RangeVerdict.Satisfied)]
    [InlineData("^18 || ^20", "19.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("^1.2.3", "1.9.0", RangeVerdict.Satisfied)]
    [InlineData("^1.2.3", "2.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("^1.2.3", "1.2.2", RangeVerdict.NotSatisfied)]
    [InlineData("^0.2.3", "0.2.9", RangeVerdict.Satisfied)]
    [InlineData("^0.2.3", "0.3.0", RangeVerdict.NotSatisfied)]
    [InlineData("^0.0.3", "0.0.3", RangeVerdict.Satisfied)]
    [InlineData("^0.0.3", "0.0.4", RangeVerdict.NotSatisfied)]
    [InlineData("^0.x", "0.9.0", RangeVerdict.Satisfied)]
    [InlineData("^0.x", "1.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("^0.0", "0.0.9", RangeVerdict.Satisfied)]
    [InlineData("^0.0", "0.1.0", RangeVerdict.NotSatisfied)]
    [InlineData("~1.2.3", "1.2.9", RangeVerdict.Satisfied)]
    [InlineData("~1.2.3", "1.3.0", RangeVerdict.NotSatisfied)]
    [InlineData("~1", "1.9.9", RangeVerdict.Satisfied)]
    [InlineData("~1", "2.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("~0.2", "0.2.7", RangeVerdict.Satisfied)]
    [InlineData("1.x", "1.5.0", RangeVerdict.Satisfied)]
    [InlineData("1.x", "2.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("22", "22.3.1", RangeVerdict.Satisfied)]
    [InlineData("v22.11", "22.11.4", RangeVerdict.Satisfied)]
    [InlineData("v22.11", "22.12.0", RangeVerdict.NotSatisfied)]
    [InlineData("=22.11.0", "22.11.0", RangeVerdict.Satisfied)]
    [InlineData("22.11.0", "22.11.1", RangeVerdict.NotSatisfied)]
    [InlineData("*", "0.0.1", RangeVerdict.Satisfied)]
    [InlineData("x", "9.9.9", RangeVerdict.Satisfied)]
    [InlineData("", "9.9.9", RangeVerdict.Satisfied)]
    [InlineData(">1.2", "1.2.9", RangeVerdict.NotSatisfied)]
    [InlineData(">1.2", "1.3.0", RangeVerdict.Satisfied)]
    [InlineData(">1", "1.9.9", RangeVerdict.NotSatisfied)]
    [InlineData("<=1.2", "1.2.9", RangeVerdict.Satisfied)]
    [InlineData("<=1.2", "1.3.0", RangeVerdict.NotSatisfied)]
    [InlineData("<1.2", "1.1.9", RangeVerdict.Satisfied)]
    [InlineData("<1.2", "1.2.0", RangeVerdict.NotSatisfied)]
    [InlineData(">*", "1.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("  >=1.0.0   <2.0.0  ", "1.5.0", RangeVerdict.Satisfied)]
    public void A_semver_range_is_evaluated_against_a_version(string range, string version, RangeVerdict expected)
    {
        Assert.Equal(expected, SemverRange.Evaluate(range, V(version)));
    }

    [Theory]
    [InlineData("1.2.3 - 2.3.4")]
    [InlineData("lts/*")]
    [InlineData("node")]
    [InlineData("latest")]
    [InlineData(">=1.2.3-beta.1")]
    [InlineData("^1.2.3+build")]
    [InlineData("https://example.com/x.tgz")]
    [InlineData(">=")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3.4")]
    [InlineData(">=99999999999")]
    [InlineData("~>1.2")]
    [InlineData("^")]
    [InlineData("&&")]
    public void A_range_outside_the_subset_is_not_comparable_and_never_satisfied(string range)
    {
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(range, V("1.2.3")));
    }

    [Fact]
    public void An_alternative_that_cannot_be_read_does_not_hide_one_that_is_satisfied_but_is_not_a_no()
    {
        Assert.Equal(RangeVerdict.Satisfied, SemverRange.Evaluate("^1 || lts/*", V("1.2.0")));
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate("^2 || lts/*", V("1.2.0")));
    }

    [Fact]
    public void A_semver_range_of_too_many_sets_or_comparators_or_characters_is_not_comparable()
    {
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(string.Join("||", Enumerable.Repeat(">=1", SemverRange.MaxSets + 1)), V("5.0.0")));
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(string.Join(" ", Enumerable.Repeat(">=1", SemverRange.MaxComparators + 1)), V("5.0.0")));
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(new string(' ', SemverRange.MaxLength) + ">=1", V("5.0.0")));
        Assert.Equal(RangeVerdict.Satisfied, SemverRange.Evaluate(string.Join("||", Enumerable.Repeat(">=1", SemverRange.MaxSets)), V("5.0.0")));
    }

    [Fact]
    public void A_prerelease_or_a_null_is_not_comparable()
    {
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(">=1", V("2.0.0-rc.1")));
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(">=1", null));
        Assert.Equal(RangeVerdict.NotComparable, SemverRange.Evaluate(null, V("2.0.0")));
    }

    [Fact]
    public void The_semver_evaluator_never_throws_for_any_text()
    {
        string[] texts = ["\0", "‮>=1", "^^^1", ">=>=1", "|| || ||", "||", "1..2", ".", "..", "x.x.x", new string('^', 190), "9999999999.0.0", "-1", "1 - ", "\n\r\t"];
        foreach (var text in texts)
        {
            _ = SemverRange.Evaluate(text, V("1.0.0"));
        }

        for (var seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            var text = new string([.. Enumerable.Range(0, random.Next(1, 40)).Select(_ => ">=<^~|x*.0123 v-"[random.Next(16)])]);
            _ = SemverRange.Evaluate(text, V("3.4.5"));
        }
    }

    // ---- PEP 440 ----

    [Theory]
    [InlineData(">=3.9", "3.12.5", RangeVerdict.Satisfied)]
    [InlineData(">=3.9", "3.8.18", RangeVerdict.NotSatisfied)]
    [InlineData(">=3.9,<3.13", "3.12.5", RangeVerdict.Satisfied)]
    [InlineData(">=3.9, <3.12", "3.12.5", RangeVerdict.NotSatisfied)]
    [InlineData(">= 3.9 , != 3.12.5", "3.12.5", RangeVerdict.NotSatisfied)]
    [InlineData(">3.12", "3.12.5", RangeVerdict.Satisfied)]
    [InlineData(">3.12", "3.12.0", RangeVerdict.NotSatisfied)]
    [InlineData("<=3.12", "3.12.0", RangeVerdict.Satisfied)]
    [InlineData("<=3.12", "3.12.1", RangeVerdict.NotSatisfied)]
    [InlineData("==3.12", "3.12.0", RangeVerdict.Satisfied)]
    [InlineData("==3.12", "3.12.1", RangeVerdict.NotSatisfied)]
    [InlineData("==3.12.*", "3.12.9", RangeVerdict.Satisfied)]
    [InlineData("==3.12.*", "3.13.0", RangeVerdict.NotSatisfied)]
    [InlineData("==3.*", "3.99.0", RangeVerdict.Satisfied)]
    [InlineData("!=3.12.*", "3.12.4", RangeVerdict.NotSatisfied)]
    [InlineData("!=3.12.*", "3.11.4", RangeVerdict.Satisfied)]
    [InlineData("~=3.10", "3.12.5", RangeVerdict.Satisfied)]
    [InlineData("~=3.10", "4.0.0", RangeVerdict.NotSatisfied)]
    [InlineData("~=3.10", "3.9.9", RangeVerdict.NotSatisfied)]
    [InlineData("~=3.10.2", "3.10.9", RangeVerdict.Satisfied)]
    [InlineData("~=3.10.2", "3.11.0", RangeVerdict.NotSatisfied)]
    [InlineData("~=3.10.2", "3.10.1", RangeVerdict.NotSatisfied)]
    public void A_pep_440_specifier_is_evaluated_against_a_version(string specifier, string version, RangeVerdict expected)
    {
        Assert.Equal(expected, Pep440Specifier.Evaluate(specifier, V(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("~=3")]
    [InlineData("===3.12.5")]
    [InlineData(">=3.9rc1")]
    [InlineData(">=3.9.post1")]
    [InlineData(">=3.9.dev1")]
    [InlineData(">=3.9+local")]
    [InlineData(">=1!3.9")]
    [InlineData(">=3.*")]
    [InlineData("<3.*")]
    [InlineData("~=3.10.*")]
    [InlineData("3.9")]
    [InlineData(">=")]
    [InlineData(">=3.9,,<4")]
    [InlineData(">=3.9 and <4")]
    [InlineData(">=1.2.3.4.5")]
    [InlineData("python_version >= '3.9'")]
    public void A_specifier_outside_the_subset_is_not_comparable_and_never_satisfied(string specifier)
    {
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(specifier, V("3.12.5")));
    }

    [Fact]
    public void A_clause_that_is_certainly_not_satisfied_settles_the_answer_even_if_another_cannot_be_read()
    {
        Assert.Equal(RangeVerdict.NotSatisfied, Pep440Specifier.Evaluate(">=3.13,<4rc1", V("3.12.5")));
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(">=3.9,<4rc1", V("3.12.5")));
    }

    [Fact]
    public void A_prerelease_python_a_null_and_an_oversized_specifier_are_not_comparable_and_nothing_throws()
    {
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(">=3.9", V("3.13.0-rc1")));
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(">=3.9", null));
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(null, V("3.9.0")));
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(string.Join(",", Enumerable.Repeat(">=1", Pep440Specifier.MaxClauses + 1)), V("3.9.0")));
        Assert.Equal(RangeVerdict.NotComparable, Pep440Specifier.Evaluate(">=3.9" + new string(' ', Pep440Specifier.MaxLength), V("3.9.0")));

        for (var seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            var text = new string([.. Enumerable.Range(0, random.Next(1, 40)).Select(_ => "<>=!~,.* 0123456789ab"[random.Next(21)])]);
            _ = Pep440Specifier.Evaluate(text, V("3.12.5"));
        }
    }
}
