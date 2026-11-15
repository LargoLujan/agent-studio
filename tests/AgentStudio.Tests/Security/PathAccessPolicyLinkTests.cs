using System.IO;
using AgentStudio.Domain.Agents;
using AgentStudio.Security.Paths;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Security;

/// <summary>Real links in a private temporary tree; no provider, external data or ACL is touched.</summary>
public sealed class PathAccessPolicyLinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_directory_link_cannot_escape_a_grant_even_for_a_file_that_does_not_exist(bool junction)
    {
        using var tree = new Tree();
        var granted = tree.Folder("granted");
        var outside = tree.Folder("outside");
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var policy = Policy((granted, FileAccessLevel.ReadWrite));
        var link = Path.Combine(granted, "redirect");
        tree.DirectoryLink(link, outside, junction);

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(link));
        Assert.False(policy.CanRead(Path.Combine(link, "sentinel.txt")));
        Assert.False(policy.CanWrite(Path.Combine(link, "new", "nested", "output.txt")));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(Path.Combine(outside, "new")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_internal_directory_link_keeps_access_to_existing_and_new_descendants(bool junction)
    {
        using var tree = new Tree();
        var granted = tree.Folder("granted");
        var target = tree.Folder("granted", "source");
        File.WriteAllText(Path.Combine(target, "file.txt"), "inside");
        var link = Path.Combine(granted, "alias");
        tree.DirectoryLink(link, target, junction);
        var policy = Policy((granted, FileAccessLevel.ReadWrite));

        Assert.True(policy.CanRead(Path.Combine(link, "file.txt")));
        Assert.True(policy.CanWrite(Path.Combine(link, "new", "file.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "new")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_grant_root_resolves_intermediate_links_and_allows_its_missing_suffix(bool junction)
    {
        using var tree = new Tree();
        var real = tree.Folder("real");
        tree.Folder("real", "project");
        var alias = tree.At("alias");
        tree.DirectoryLink(alias, real, junction);
        var granted = Path.Combine(alias, "project", "generated");
        var policy = Policy((granted, FileAccessLevel.ReadWrite));

        Assert.True(policy.CanWrite(Path.Combine(granted, "new", "file.txt")));
        Assert.False(policy.CanWrite(Path.Combine(alias, "project", "other.txt")));
        Assert.False(Directory.Exists(Path.Combine(real, "project", "generated")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retargeting_a_granted_link_does_not_grant_the_replacement_tree(bool junction)
    {
        using var tree = new Tree();
        var first = tree.Folder("first");
        var replacement = tree.Folder("replacement");
        var alias = tree.At("alias");
        tree.DirectoryLink(alias, first, junction);
        var policy = Policy((alias, FileAccessLevel.ReadWrite));
        Assert.True(policy.CanWrite(Path.Combine(alias, "new.txt")));

        Assert.True(TempFolders.Delete(alias));
        tree.DirectoryLink(alias, replacement, junction);

        Assert.False(policy.CanRead(Path.Combine(alias, "new.txt")));
        Assert.False(policy.CanWrite(Path.Combine(alias, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_alias_cannot_bypass_a_more_restrictive_target_grant(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var readOnly = tree.Folder("root", "read-only");
        var denied = tree.Folder("root", "denied");
        var readAlias = Path.Combine(root, "read-alias");
        var deniedAlias = Path.Combine(root, "denied-alias");
        tree.DirectoryLink(readAlias, readOnly, junction);
        tree.DirectoryLink(deniedAlias, denied, junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite), (readOnly, FileAccessLevel.Read), (denied, FileAccessLevel.None));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(Path.Combine(readAlias, "new.txt")));
        Assert.False(policy.CanWrite(Path.Combine(readAlias, "new.txt")));
        Assert.Equal(FileAccessLevel.None, policy.Evaluate(Path.Combine(deniedAlias, "new.txt")));
        Assert.True(policy.CanWrite(Path.Combine(root, "ordinary.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolving_a_link_does_not_remove_the_restriction_on_its_requested_name(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var readOnly = tree.Folder("root", "read-only");
        var writable = tree.Folder("root", "writable");
        var alias = Path.Combine(readOnly, "alias");
        tree.DirectoryLink(alias, writable, junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite), (readOnly, FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(Path.Combine(alias, "new.txt")));
        Assert.True(policy.CanWrite(Path.Combine(writable, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_explicit_external_grant_allows_a_link_only_at_the_lower_access_level(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var outside = tree.Folder("outside");
        var alias = Path.Combine(root, "alias");
        tree.DirectoryLink(alias, outside, junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite), (outside, FileAccessLevel.Read));

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(Path.Combine(alias, "new.txt")));
        Assert.False(policy.CanWrite(Path.Combine(alias, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_link_target_with_another_link_in_its_parent_is_checked_again(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var outside = tree.Folder("outside");
        tree.Folder("outside", "child");
        var hop = Path.Combine(root, "hop");
        var alias = Path.Combine(root, "alias");
        tree.DirectoryLink(hop, outside, junction);
        tree.DirectoryLink(alias, Path.Combine(hop, "child"), junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(Path.Combine(alias, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cycles_fail_closed_for_a_candidate_and_reject_an_unresolvable_grant(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        tree.DirectoryLink(first, second, junction);
        tree.DirectoryLink(second, first, junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(Path.Combine(first, "new.txt")));
        Assert.Throws<IOException>(() => Policy((root, FileAccessLevel.ReadWrite), (first, FileAccessLevel.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cycle_that_keeps_expanding_the_path_also_fails_closed(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var link = Path.Combine(root, "loop");
        tree.DirectoryLink(link, Path.Combine(link, "deeper"), junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(Path.Combine(link, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dangling_directory_link_outside_the_grant_is_not_a_missing_safe_directory(bool junction)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var link = Path.Combine(root, "alias");
        tree.DirectoryLink(link, tree.At("missing-outside"), junction);
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.False(policy.CanWrite(Path.Combine(link, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grants_for_the_same_physical_tree_use_the_restrictive_level_regardless_of_order(bool reverse)
    {
        using var tree = new Tree();
        var real = tree.Folder("real");
        var alias = tree.At("alias");
        tree.DirectoryLink(alias, real, junction: true);
        var grants = new[] { (real, FileAccessLevel.ReadWrite), (alias, FileAccessLevel.Read) };
        var policy = Policy(reverse ? grants.Reverse().ToArray() : grants);

        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(Path.Combine(real, "file.txt")));
        Assert.Equal(FileAccessLevel.Read, policy.Evaluate(Path.Combine(alias, "file.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_link_outside_a_grant_is_denied_whether_its_target_exists_or_not(bool targetExists)
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var target = tree.At("outside.txt");
        if (targetExists)
        {
            File.WriteAllText(target, "outside");
        }

        var link = Path.Combine(root, "alias.txt");
        tree.FileLink(link, target);
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.Equal(FileAccessLevel.None, policy.Evaluate(link));
    }

    [Fact]
    public void A_relative_file_link_to_a_missing_internal_target_can_be_created_but_not_used_as_a_directory()
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var link = Path.Combine(root, "alias.txt");
        tree.FileLink(link, "new.txt");
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.True(policy.CanWrite(link));
        Assert.False(policy.CanWrite(Path.Combine(link, "child.txt")));
        File.WriteAllText(Path.Combine(root, "new.txt"), "inside");
        Assert.False(policy.CanRead(Path.Combine(link, "child.txt")));
    }

    [Fact]
    public void A_regular_file_is_not_treated_as_a_missing_parent_directory()
    {
        using var tree = new Tree();
        var root = tree.Folder("root");
        var file = Path.Combine(root, "file.txt");
        File.WriteAllText(file, "inside");
        var policy = Policy((root, FileAccessLevel.ReadWrite));

        Assert.True(policy.CanRead(file));
        Assert.False(policy.CanWrite(Path.Combine(file, "child.txt")));
    }

    private static PathAccessPolicy Policy(params (string Root, FileAccessLevel Access)[] grants) =>
        new(grants.Select(g => new PathGrant(g.Root, g.Access)));

    private sealed class Tree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentstudio-path-policy-" + Guid.NewGuid().ToString("N"));

        public Tree() => Directory.CreateDirectory(_root);

        public string At(params string[] parts) => Path.Combine(_root, Path.Combine(parts));

        public string Folder(params string[] parts) => Directory.CreateDirectory(At(parts)).FullName;

        public void DirectoryLink(string link, string target, bool junction)
        {
            if (junction)
            {
                Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions require Windows.");
                GitManagerTestBase.CreateJunction(link, target);
                return;
            }

            CreateSymbolicLink(() => Directory.CreateSymbolicLink(link, target));
        }

        public void FileLink(string link, string target) => CreateSymbolicLink(() => File.CreateSymbolicLink(link, target));

        public void Dispose()
        {
            // The shared helper removes dangling links and cycles as links, before deleting the private tree.
            TempFolders.Delete(_root);
        }

        private static void CreateSymbolicLink(Action create)
        {
            try
            {
                create();
            }
            catch (Exception ex) when (OperatingSystem.IsWindows()
                && (ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xffff) == 1314)))
            {
                Assert.Skip("Creating symbolic links needs Developer Mode or the symbolic-link privilege on this machine.");
            }
        }
    }
}
