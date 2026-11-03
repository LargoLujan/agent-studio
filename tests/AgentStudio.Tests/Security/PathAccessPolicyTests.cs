using AgentStudio.Domain.Agents;
using AgentStudio.Security.Paths;

namespace AgentStudio.Tests.Security;

public sealed class PathAccessPolicyTests
{
    private static PathAccessPolicy Policy(params (string Root, FileAccessLevel Access)[] grants) =>
        new(grants.Select(g => new PathGrant(g.Root, g.Access)));

    [Fact]
    public void Paths_inside_a_granted_root_get_that_access()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\src\a.cs"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo"));
        Assert.True(policy.CanRead(@"C:\repo\src\a.cs"));
        Assert.True(policy.CanWrite(@"C:\repo\src\a.cs"));
    }

    [Fact]
    public void Read_only_grants_allow_reading_but_not_writing()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.Read));

        Assert.True(policy.CanRead(@"C:\repo\a.txt"));
        Assert.False(policy.CanWrite(@"C:\repo\a.txt"));
    }

    [Fact]
    public void Without_a_grant_the_access_is_None()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"D:\other\a.txt"));
        Assert.False(policy.CanRead(@"D:\other\a.txt"));
        Assert.False(policy.CanWrite(@"D:\other\a.txt"));
        Assert.Equal(FileAccessLevel.None, Policy().Evaluate(@"C:\repo\a.txt"));
    }

    [Fact]
    public void A_folder_boundary_is_respected_so_repo_does_not_grant_repo2()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo2\a.txt"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo2"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repository\a.txt"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo-evil\x"));
    }

    [Fact]
    public void Dot_dot_segments_cannot_climb_out_of_a_grant()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo\..\secrets\key.pem"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo\src\..\..\Windows\win.ini"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo\..\repo2\a.txt"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo\.."));
    }

    [Fact]
    public void Dot_dot_segments_that_stay_inside_are_resolved()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\src\..\docs\a.md"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\other\..\repo\a.md"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\.\a.md"));
    }

    [Fact]
    public void A_dot_dot_in_the_grant_root_is_normalised_too()
    {
        var policy = Policy((@"C:\repo\src\..", FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\a.txt"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\other\a.txt"));
    }

    [Fact]
    public void Comparison_ignores_case_on_Windows_and_forward_slashes_and_trailing_separators_are_fine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Path comparison is case-insensitive on Windows only.");

        var policy = Policy((@"C:\Repo\", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"c:\REPO\Src\A.cs"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate("C:/repo/src/a.cs"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\REPO2\a"));
    }

    [Fact]
    public void The_most_specific_grant_wins_even_when_it_is_more_restrictive()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite), (@"C:\repo\.git", FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\src\a.cs"));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\.git\config"));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\.git"));
        Assert.False(policy.CanWrite(@"C:\repo\.git\hooks\pre-commit"));
        Assert.True(policy.CanRead(@"C:\repo\.git\hooks\pre-commit"));
    }

    [Fact]
    public void The_most_specific_grant_wins_regardless_of_declaration_order()
    {
        var policy = Policy((@"C:\repo\.git", FileAccessLevel.Read), (@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\.git\config"));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\a"));
    }

    [Fact]
    public void A_more_specific_grant_can_also_widen_or_deny_access()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.Read), (@"C:\repo\out", FileAccessLevel.ReadWrite), (@"C:\repo\secrets", FileAccessLevel.None));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(@"C:\repo\out\build.log"));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(@"C:\repo\secrets\key"));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\src\a.cs"));
    }

    [Fact]
    public void A_deep_nested_path_that_climbs_into_a_restricted_subfolder_gets_the_restricted_access()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite), (@"C:\repo\.git", FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(@"C:\repo\src\..\.git\config"));
    }

    [Fact]
    public void Null_or_blank_paths_are_rejected()
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.ThrowsAny<ArgumentException>(() => policy.Evaluate(""));
        Assert.ThrowsAny<ArgumentException>(() => policy.Evaluate("  "));
        Assert.ThrowsAny<ArgumentException>(() => policy.Evaluate(null!));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData(@"src\a.cs")]
    [InlineData(@"..\secrets")]
    [InlineData(@"C:a.txt")]
    [InlineData(@"\repo\a.txt")]
    public void Paths_that_are_not_fully_qualified_are_rejected_instead_of_resolved_against_the_cwd(string path)
    {
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));

        Assert.Throws<ArgumentException>(() => policy.Evaluate(path));
    }

    [Theory]
    [InlineData("repo")]
    [InlineData(@".\repo")]
    [InlineData(@"C:repo")]
    public void Grant_roots_that_are_not_fully_qualified_are_rejected(string root)
    {
        Assert.Throws<ArgumentException>(() => Policy((root, FileAccessLevel.Read)));
    }

    [Fact]
    public void Null_grants_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PathAccessPolicy(null!));
    }

    [Fact]
    public void Random_paths_under_the_root_never_escape_and_random_paths_outside_never_get_access()
    {
        var random = new Random(99);
        var policy = Policy((@"C:\repo", FileAccessLevel.ReadWrite));
        string[] segments = ["src", "..", ".", "docs", "a", "repo2", "x"];

        for (var i = 0; i < 300; i++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 7)).Select(_ => segments[random.Next(segments.Length)]);
            var path = @"C:\repo\" + string.Join('\\', parts);
            var resolved = System.IO.Path.GetFullPath(path);
            var inside = resolved.Equals(@"C:\repo", StringComparison.OrdinalIgnoreCase)
                || resolved.StartsWith(@"C:\repo\", StringComparison.OrdinalIgnoreCase);

            Assert.Equal(inside ? FileAccessLevel.ReadWrite : FileAccessLevel.None, policy.Evaluate(path));
        }
    }
}
