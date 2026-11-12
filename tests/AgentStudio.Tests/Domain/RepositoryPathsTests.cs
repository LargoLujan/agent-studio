using AgentStudio.Domain.Common;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// docs/20 D-16 and D-24 (P0 of Phase 6): the shape of a path that names something inside a repository. Paths that come from a repository are
/// data that is not trusted, so everything that could lead out of the copy of a commit, or name another file than it seems, is refused.
/// </summary>
public sealed class RepositoryPathsTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("src/AgentStudio.Domain/AgentStudio.Domain.csproj")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("docs/20-fase-6-auto-doctor-y-auto-graph.md")]
    [InlineData("a b/c d.cs")]
    [InlineData("ñandú/ü.cs")]
    public void A_relative_path_with_forward_slashes_is_safe(string path)
    {
        Assert.True(RepositoryPaths.IsSafeRelative(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../outside.cs")]
    [InlineData("a/../../outside.cs")]
    [InlineData("a/./b")]
    [InlineData(".")]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/file.cs")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("..\\..\\outside.cs")]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("C:file.cs")]
    [InlineData("c:/file.cs")]
    [InlineData("a/b:stream")]
    [InlineData("file.cs\n")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("%2e%2e/x")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a\"b")]
    [InlineData("folder./file.cs")]
    [InlineData("folder /file.cs")]
    [InlineData("file.cs.")]
    [InlineData("file.cs ")]
    public void A_path_that_leads_out_names_another_file_or_hides_a_character_is_refused(string? path)
    {
        Assert.False(RepositoryPaths.IsSafeRelative(path));
    }

    [Fact]
    public void A_path_longer_than_the_cap_is_refused_and_one_of_the_cap_is_not()
    {
        Assert.True(RepositoryPaths.IsSafeRelative(new string('a', RepositoryPaths.MaxLength)));
        Assert.False(RepositoryPaths.IsSafeRelative(new string('a', RepositoryPaths.MaxLength + 1)));
    }

    [Fact]
    public void The_root_of_a_folder_may_be_empty_where_the_rule_allows_it()
    {
        Assert.True(RepositoryPaths.IsSafeRelativeOrEmpty(string.Empty));
        Assert.True(RepositoryPaths.IsSafeRelativeOrEmpty("docs"));
        Assert.False(RepositoryPaths.IsSafeRelativeOrEmpty(null));
        Assert.False(RepositoryPaths.IsSafeRelativeOrEmpty(".."));
    }
}
