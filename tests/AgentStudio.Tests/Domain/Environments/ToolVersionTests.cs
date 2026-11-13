using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-07 (P0 of Phase 6): the version of a tool, read from the output of a tool or from a file of a repository. It has a strict shape, because
/// what a repository says is not trusted, and it compares as SemVer does. The rules of ranges and of rollForward are WP-E; this is the value they compare.
/// </summary>
public sealed class ToolVersionTests
{
    [Theory]
    [InlineData("10")]
    [InlineData("10.0")]
    [InlineData("10.0.303")]
    [InlineData("1.2.3.4")]
    [InlineData("22.11.0")]
    [InlineData("10.0.303-preview.2.26159.112")]
    [InlineData("10.0.303+abc.1")]
    [InlineData("1.0.0-rc.1+build.5")]
    [InlineData("3.12.4")]
    [InlineData("999999999.999999999.999999999")]
    public void A_version_has_one_to_four_numbers_and_optional_labels(string text)
    {
        var version = ToolVersion.Parse(text);

        Assert.Equal(text, version.Text);
        Assert.Equal(text, version.ToString());
        Assert.True(ToolVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("v22.11.0")]
    [InlineData("22.11.0\n")]
    [InlineData("\n22.11.0")]
    [InlineData(" 22.11.0")]
    [InlineData("22.11.0 ")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("1.2.x")]
    [InlineData("*")]
    [InlineData("^1.2.3")]
    [InlineData(">=1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-pre..1")]
    [InlineData("1.2.3-pre_1")]
    [InlineData("1.2.3-é")]
    [InlineData("1,2,3")]
    [InlineData("1234567890")]
    [InlineData("-1.2.3")]
    [InlineData("１０.0")]
    [InlineData("lts/*")]
    public void Anything_else_is_not_a_version(string? text)
    {
        Assert.False(ToolVersion.TryParse(text, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void Parsing_what_is_not_a_version_throws_a_format_error_that_never_carries_a_control_character()
    {
        var error = Assert.Throws<FormatException>(() => ToolVersion.Parse("1.2\n\u001b[31mred"));

        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain('\u001b', error.Message);
        Assert.Throws<FormatException>(() => ToolVersion.Parse(new string('1', 100)));
    }

    [Fact]
    public void A_text_of_the_cap_is_a_version_and_one_character_more_is_not()
    {
        var label = new string('a', 50);
        var atCap = "1.2.3-" + label + "+" + new string('b', ToolVersion.MaxLength - 6 - 50 - 1);

        Assert.Equal(ToolVersion.MaxLength, atCap.Length);
        Assert.True(ToolVersion.TryParse(atCap, out _));
        Assert.False(ToolVersion.TryParse(atCap + "b", out _));
        Assert.False(ToolVersion.TryParse("1.2.3-" + new string('a', 51), out _));
    }

    [Fact]
    public void The_numbers_are_the_parts_and_missing_ones_count_as_zero()
    {
        var version = ToolVersion.Parse("10.0.303");

        Assert.Equal([10, 0, 303], version.Numbers);
        Assert.Equal((10, 0, 303), (version.Major, version.Minor, version.Patch));
        Assert.Equal((10, 0, 0), (ToolVersion.Parse("10").Major, ToolVersion.Parse("10").Minor, ToolVersion.Parse("10").Patch));
        Assert.Equal(0, ToolVersion.Parse("0").Major);
    }

    [Fact]
    public void Versions_compare_by_their_numbers_so_ten_is_after_nine()
    {
        string[] ordered = ["8.0.100", "9.0.9", "9.0.10", "10.0.0", "10.0.303", "10.1", "10.1.0.1", "11.0.100"];

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var low = ToolVersion.Parse(ordered[i]);
            var high = ToolVersion.Parse(ordered[i + 1]);
            Assert.True(low < high, $"{low} < {high}");
            Assert.True(high > low);
            Assert.True(low <= high);
            Assert.True(high >= low);
            Assert.Equal(-1, Math.Sign(low.CompareTo(high)));
            Assert.Equal(1, Math.Sign(high.CompareTo(low)));
        }

        Assert.Equal(ordered, ordered.Select(ToolVersion.Parse).OrderDescending().Reverse().Select(v => v.Text));
    }

    [Fact]
    public void A_missing_number_is_a_zero_and_equality_is_the_same_as_comparing_while_the_text_keeps_what_was_read()
    {
        var short1 = ToolVersion.Parse("10.0");
        var long1 = ToolVersion.Parse("10.0.0");

        Assert.Equal(0, short1.CompareTo(long1));
        Assert.Equal(short1, long1);
        Assert.Equal(short1.GetHashCode(), long1.GetHashCode());
        Assert.NotEqual(short1.Text, long1.Text);
        Assert.Equal(ToolVersion.Parse("1.2.3+a"), ToolVersion.Parse("1.2.3+b"));
        Assert.Equal(ToolVersion.Parse("1.2.3+a").GetHashCode(), ToolVersion.Parse("1.2.3+b").GetHashCode());
        Assert.Equal(ToolVersion.Parse("1.0.0-0001").GetHashCode(), ToolVersion.Parse("1.0.0-1").GetHashCode());
        Assert.NotEqual(ToolVersion.Parse("1.0.0-rc.1"), ToolVersion.Parse("1.0.0"));
        Assert.NotEqual(ToolVersion.Parse("10.0.1"), long1);
        Assert.True(short1 <= long1 && short1 >= long1);
        Assert.Equal(ToolVersion.Parse("10.0"), ToolVersion.Parse("10.0"));
        Assert.Equal(ToolVersion.Parse("10.0").GetHashCode(), ToolVersion.Parse("10.0").GetHashCode());
        Assert.False(ToolVersion.Parse("10.0").Equals((object?)null));
    }

    [Fact]
    public void A_pre_release_is_lower_than_the_same_version_without_it_and_pre_releases_compare_as_semver_does()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(ToolVersion.Parse(ordered[i]) < ToolVersion.Parse(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
        }

        Assert.True(ToolVersion.Parse("10.0.303-preview.1").IsPreRelease);
        Assert.Equal("preview.1", ToolVersion.Parse("10.0.303-preview.1").PreRelease);
        Assert.False(ToolVersion.Parse("10.0.303").IsPreRelease);
        Assert.True(ToolVersion.Parse("10.0.304-preview.1") > ToolVersion.Parse("10.0.303"));
    }

    [Fact]
    public void The_build_label_never_counts_when_comparing()
    {
        Assert.Equal(0, ToolVersion.Parse("1.2.3+aaa").CompareTo(ToolVersion.Parse("1.2.3+zzz")));
        Assert.Equal(0, ToolVersion.Parse("1.2.3-rc.1+aaa").CompareTo(ToolVersion.Parse("1.2.3-rc.1")));
    }

    [Fact]
    public void A_huge_numeric_pre_release_identifier_compares_by_value_without_overflowing()
    {
        var a = ToolVersion.Parse("1.0.0-" + new string('9', 30));
        var b = ToolVersion.Parse("1.0.0-1" + new string('0', 30));

        Assert.True(a < b);
        Assert.Equal(ToolVersion.Parse("1.0.0-0001"), ToolVersion.Parse("1.0.0-0001"));
        Assert.Equal(0, ToolVersion.Parse("1.0.0-0001").CompareTo(ToolVersion.Parse("1.0.0-1")));
    }

    [Fact]
    public void A_null_is_lower_than_any_version_for_the_operators()
    {
        var version = ToolVersion.Parse("1.0");

        Assert.True(version > null);
        Assert.True(null < version);
        Assert.True(null <= version);
        Assert.False(version <= null);
        Assert.True(version >= null);
        Assert.Equal(1, version.CompareTo(null));
    }
}
