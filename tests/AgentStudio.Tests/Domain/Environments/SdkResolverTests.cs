using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-07 (WP-E): the roll-forward rule of <c>global.json</c> evaluated over a list of installed SDKs, as the table of «global.json overview» of Microsoft Learn
/// describes it. The band of <c>10.0.303</c> is 3; <c>feature</c> goes to the next band and <c>latestFeature</c> to the highest one.
/// </summary>
public sealed class SdkResolverTests
{
    private static readonly string[] Installed = ["8.0.404", "9.0.100", "9.0.205", "10.0.100", "10.0.103", "10.0.303", "10.0.400", "10.1.100", "11.0.100-preview.2.1234"];

    private static IReadOnlyList<ToolVersion> Sdks(params string[] versions) => [.. versions.Select(ToolVersion.Parse)];

    private static string? Pick(string? version, string? policy, string[]? installed = null, bool? allowPrerelease = null, params string[] paths)
    {
        var resolution = SdkResolver.Resolve(version, policy, allowPrerelease, Sdks(installed ?? Installed), paths.Length == 0 ? null : paths);
        return resolution.Kind switch
        {
            SdkResolutionKind.Selected => resolution.Selected!.Text,
            SdkResolutionKind.NoMatch => "no-match",
            _ => "not-comparable:" + resolution.Reason,
        };
    }

    [Theory]
    [InlineData("10.0.303", "patch", "10.0.303")]
    [InlineData("10.0.304", "patch", "no-match")]
    [InlineData("10.0.100", "patch", "10.0.100")]
    [InlineData("10.0.101", "patch", "10.0.103")]
    [InlineData("10.0.303", null, "10.0.303")]
    [InlineData("10.0.200", "patch", "no-match")]
    [InlineData("10.0.100", "latestPatch", "10.0.103")]
    [InlineData("10.0.103", "latestPatch", "10.0.103")]
    [InlineData("10.0.104", "latestPatch", "no-match")]
    [InlineData("10.0.100", "feature", "10.0.103")]
    [InlineData("10.0.200", "feature", "10.0.303")]
    [InlineData("10.0.304", "feature", "10.0.400")]
    [InlineData("10.0.401", "feature", "no-match")]
    [InlineData("10.0.100", "latestFeature", "10.0.400")]
    [InlineData("10.0.401", "latestFeature", "no-match")]
    [InlineData("10.0.401", "minor", "10.1.100")]
    [InlineData("10.0.100", "minor", "10.0.103")]
    [InlineData("10.1.101", "minor", "no-match")]
    [InlineData("10.0.100", "latestMinor", "10.1.100")]
    [InlineData("10.2.0", "latestMinor", "no-match")]
    [InlineData("8.0.100", "major", "8.0.404")]
    [InlineData("8.0.405", "major", "9.0.100")]
    [InlineData("9.0.206", "major", "10.0.103")]
    [InlineData("11.0.200", "major", "no-match")]
    [InlineData("8.0.100", "latestMajor", "11.0.100-preview.2.1234")]
    [InlineData("12.0.100", "latestMajor", "no-match")]
    [InlineData("10.0.303", "disable", "10.0.303")]
    [InlineData("10.0.304", "disable", "no-match")]
    [InlineData("10.0.303", "LATESTFEATURE", "10.0.400")]
    public void The_policy_picks_the_sdk_the_host_would_use(string version, string? policy, string expected)
    {
        Assert.Equal(expected, Pick(version, policy));
    }

    [Fact]
    public void Without_a_version_the_highest_installed_sdk_is_used_and_a_prerelease_counts_by_default()
    {
        Assert.Equal("11.0.100-preview.2.1234", Pick(null, null));
        Assert.Equal("11.0.100-preview.2.1234", Pick(null, "latestMajor"));
    }

    [Fact]
    public void Allow_prerelease_false_leaves_the_preview_sdks_out()
    {
        Assert.Equal("10.1.100", Pick(null, null, allowPrerelease: false));
        Assert.Equal("10.1.100", Pick("8.0.100", "latestMajor", allowPrerelease: false));
        Assert.Equal("11.0.100-preview.2.1234", Pick("11.0.100-preview.2.1234", "disable", allowPrerelease: true));
        Assert.Equal("no-match", Pick("11.0.100-preview.2.1234", "disable", allowPrerelease: false));
    }

    [Fact]
    public void A_prerelease_is_lower_than_its_release_when_the_version_is_compared()
    {
        string[] installed = ["10.0.100-rc.1.2", "10.0.100"];

        Assert.Equal("10.0.100", Pick("10.0.100-rc.1.2", "latestPatch", installed));
        Assert.Equal("10.0.100-rc.1.2", Pick("10.0.100-rc.1.2", "disable", installed));
    }

    [Fact]
    public void With_nothing_installed_there_is_no_match_never_an_error()
    {
        Assert.Equal("no-match", Pick("10.0.100", "latestFeature", []));
        Assert.Equal("no-match", Pick(null, null, []));
    }

    [Theory]
    [InlineData("10.0", null, "not-comparable:invalid-version")]
    [InlineData("10", "latestFeature", "not-comparable:invalid-version")]
    [InlineData("10.0.x", null, "not-comparable:invalid-version")]
    [InlineData("10.0.1.2", null, "not-comparable:invalid-version")]
    [InlineData("^10.0.100", null, "not-comparable:invalid-version")]
    [InlineData("", null, "not-comparable:invalid-version")]
    [InlineData("10.0.100", "sideways", "not-comparable:invalid-roll-forward")]
    [InlineData("10.0.100", "", "not-comparable:invalid-roll-forward")]
    [InlineData(null, "latestFeature", "not-comparable:version-required")]
    [InlineData(null, "disable", "not-comparable:version-required")]
    public void A_rule_that_the_host_would_refuse_is_not_comparable(string? version, string? policy, string expected)
    {
        Assert.Equal(expected, Pick(version, policy));
    }

    [Fact]
    public void Paths_that_are_not_only_the_host_are_never_a_yes_because_a_folder_of_the_project_is_not_read()
    {
        Assert.Equal("10.0.303", Pick("10.0.303", "patch", paths: [SdkResolver.HostPath]));
        Assert.Equal("not-comparable:paths", Pick("10.0.303", "patch", paths: [".dotnet"]));
        Assert.Equal("not-comparable:paths", Pick("10.0.303", "patch", paths: [".dotnet", SdkResolver.HostPath]));
        Assert.Equal("not-comparable:paths", Pick("10.0.303", "patch", paths: [SdkResolver.HostPath, ".dotnet"]));
        Assert.Equal("not-comparable:paths", Pick("99.0.0", "patch", paths: [".dotnet"]));
    }

    [Theory]
    [InlineData("patch", GlobalJsonRollForward.Patch)]
    [InlineData("latestMajor", GlobalJsonRollForward.LatestMajor)]
    [InlineData("LATESTFEATURE", GlobalJsonRollForward.LatestFeature)]
    [InlineData("disable", GlobalJsonRollForward.Disable)]
    public void The_nine_policies_are_read_ignoring_case(string text, GlobalJsonRollForward expected)
    {
        Assert.True(SdkResolver.TryParseRollForward(text, out var policy));
        Assert.Equal(expected, policy);
        Assert.Equal(9, Enum.GetValues<GlobalJsonRollForward>().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("latest")]
    [InlineData("patch ")]
    public void Anything_else_is_not_a_policy(string? text)
    {
        Assert.False(SdkResolver.TryParseRollForward(text, out _));
    }
}
