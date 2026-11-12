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
    [InlineData("docs/50%.md")]
    [InlineData("a/%2e%2e/b.cs")]
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

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("Nul")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("COM¹")]
    [InlineData("COM²")]
    [InlineData("COM³")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("LPT¹")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("conout$")]
    [InlineData("NUL.cs")]
    [InlineData("aux.csproj")]
    [InlineData("COM1.txt.bak")]
    [InlineData("NUL .cs")]
    [InlineData("aux  .csproj")]
    [InlineData("CON.")]
    [InlineData("NUL ")]
    public void A_name_that_windows_reserves_for_a_device_is_refused_in_any_segment_in_any_case_and_with_an_extension(string name)
    {
        Assert.False(RepositoryPaths.IsSafeRelative(name), name);
        Assert.False(RepositoryPaths.IsSafeRelative("src/" + name), name);
        Assert.False(RepositoryPaths.IsSafeRelative(name + "/x.cs"), name);
        Assert.False(RepositoryPaths.IsSafeRelative("src/" + name + "/x.cs"), name);
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("auxiliary.cs")]
    [InlineData("nullable.cs")]
    [InlineData("CONIN")]
    [InlineData("x.NUL")]
    [InlineData("src/com1x/a.cs")]
    public void A_name_that_only_looks_like_a_device_is_a_name(string path)
    {
        Assert.True(RepositoryPaths.IsSafeRelative(path), path);
    }

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u202Ab")]
    [InlineData("a\u2066b")]
    [InlineData("a\u2069b")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u200Db")]
    [InlineData("a\uFEFFb")]
    [InlineData("a\U000E0041b")]
    public void A_path_with_a_hidden_character_is_refused(string path)
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
