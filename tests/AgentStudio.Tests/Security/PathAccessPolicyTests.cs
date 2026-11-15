using System.IO;
using AgentStudio.Domain.Agents;
using AgentStudio.Security.Paths;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Security;

public sealed class PathAccessPolicyTests : IDisposable
{
    // xUnit creates one instance per case, so no grant depends on a folder owned by another test or the user.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agentstudio-policy-tests-" + Guid.NewGuid().ToString("N"));

    public PathAccessPolicyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempFolders.Delete(_folder);

    // Keep dot segments intact: the policy, not the fixture, must normalise them.
    private string At(params string[] parts) => Path.Combine(_folder, Path.Combine(parts));

    private static PathAccessPolicy Policy(params (string Root, FileAccessLevel Access)[] grants) =>
        new(grants.Select(g => new PathGrant(g.Root, g.Access)));

    [Fact]
    public void Paths_inside_a_granted_root_get_that_access()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "src", "a.cs")));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo")));
        Assert.True(policy.CanRead(At("repo", "src", "a.cs")));
        Assert.True(policy.CanWrite(At("repo", "src", "a.cs")));
    }

    [Fact]
    public void Read_only_grants_allow_reading_but_not_writing()
    {
        var policy = Policy((At("repo"), FileAccessLevel.Read));

        Assert.True(policy.CanRead(At("repo", "a.txt")));
        Assert.False(policy.CanWrite(At("repo", "a.txt")));
    }

    [Fact]
    public void Without_a_grant_the_access_is_None()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("other", "a.txt")));
        Assert.False(policy.CanRead(At("other", "a.txt")));
        Assert.False(policy.CanWrite(At("other", "a.txt")));
        Assert.Equal(FileAccessLevel.None, Policy().Evaluate(At("repo", "a.txt")));

        if (OperatingSystem.IsWindows())
        {
            // Preserve the cross-drive case without inspecting or creating anything on the other drive.
            var otherDrive = string.Equals(Path.GetPathRoot(_folder), @"Z:\", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
            var other = Path.Combine(otherDrive, Path.GetFileName(_folder), "other", "a.txt");
            Assert.Equal(FileAccessLevel.None, policy.Evaluate(other));
            Assert.False(policy.CanRead(other));
            Assert.False(policy.CanWrite(other));
        }
    }

    [Fact]
    public void A_folder_boundary_is_respected_so_repo_does_not_grant_repo2()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo2", "a.txt")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo2")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repository", "a.txt")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo-evil", "x")));
    }

    [Fact]
    public void Dot_dot_segments_cannot_climb_out_of_a_grant()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo", "..", "secrets", "key.pem")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo", "src", "..", "..", "Windows", "win.ini")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo", "..", "repo2", "a.txt")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo", "..")));
    }

    [Fact]
    public void Dot_dot_segments_that_stay_inside_are_resolved()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "src", "..", "docs", "a.md")));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("other", "..", "repo", "a.md")));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", ".", "a.md")));
    }

    [Fact]
    public void A_dot_dot_in_the_grant_root_is_normalised_too()
    {
        var policy = Policy((At("repo", "src", ".."), FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", "a.txt")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("other", "a.txt")));
    }

    [Fact]
    public void Comparison_ignores_case_on_Windows_and_forward_slashes_and_trailing_separators_are_fine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Path comparison is case-insensitive on Windows only.");

        var policy = Policy((At("Repo") + Path.DirectorySeparatorChar, FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("REPO", "Src", "A.cs").ToUpperInvariant()));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "src", "a.cs").Replace(Path.DirectorySeparatorChar, '/')));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo") + Path.DirectorySeparatorChar));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("REPO2", "a")));
    }

    [Fact]
    public void The_most_specific_grant_wins_even_when_it_is_more_restrictive()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite), (At("repo", ".git"), FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "src", "a.cs")));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", ".git", "config")));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", ".git")));
        Assert.False(policy.CanWrite(At("repo", ".git", "hooks", "pre-commit")));
        Assert.True(policy.CanRead(At("repo", ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public void The_most_specific_grant_wins_regardless_of_declaration_order()
    {
        var policy = Policy((At("repo", ".git"), FileAccessLevel.Read), (At("repo"), FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", ".git", "config")));
        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "a")));
    }

    [Fact]
    public void A_more_specific_grant_can_also_widen_or_deny_access()
    {
        var policy = Policy((At("repo"), FileAccessLevel.Read), (At("repo", "out"), FileAccessLevel.ReadWrite), (At("repo", "secrets"), FileAccessLevel.None));

        Assert.Equal(FileAccessLevel.ReadWrite, policy.Evaluate(At("repo", "out", "build.log")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(At("repo", "secrets", "key")));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", "src", "a.cs")));
    }

    [Fact]
    public void A_deep_nested_path_that_climbs_into_a_restricted_subfolder_gets_the_restricted_access()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite), (At("repo", ".git"), FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(At("repo", "src", "..", ".git", "config")));
    }

    [Fact]
    public void Null_or_blank_paths_are_rejected()
    {
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

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
        var policy = Policy((At("repo"), FileAccessLevel.ReadWrite));

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
        var root = At("repo");
        var policy = Policy((root, FileAccessLevel.ReadWrite));
        string[] segments = ["src", "..", ".", "docs", "a", "repo2", "x"];

        for (var i = 0; i < 300; i++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 7)).Select(_ => segments[random.Next(segments.Length)]);
            var path = root + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);
            var resolved = Path.GetFullPath(path);
            var inside = resolved.Equals(root, StringComparison.OrdinalIgnoreCase)
                || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(inside ? FileAccessLevel.ReadWrite : FileAccessLevel.None, policy.Evaluate(path));
        }
    }

    [Fact]
    public void A_real_drive_root_can_be_granted_and_preserves_a_more_specific_grant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A Windows drive root is tested on Windows only.");
        var driveRoot = Path.GetPathRoot(_folder)!;
        Assert.SkipUnless(driveRoot.Length == 3 && driveRoot[1] == ':', "TEMP is not on a Windows drive.");
        Assert.True(Directory.Exists(driveRoot));
        var policy = Policy((driveRoot, FileAccessLevel.Read), (At("repo"), FileAccessLevel.ReadWrite));

        // The actual drive root is inspected; all child paths and every write remain under the private TEMP folder.
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(driveRoot));
        Assert.True(policy.CanRead(driveRoot));
        Assert.False(policy.CanWrite(driveRoot));
        Assert.True(policy.CanWrite(At("repo", "new.txt")));
    }
}
