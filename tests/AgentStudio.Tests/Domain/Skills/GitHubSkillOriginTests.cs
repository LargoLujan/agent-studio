using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Domain.Skills;

/// <summary>
/// docs/20 D-14 and F6-13 (P0 of Phase 6): the origin of a pinned skill on GitHub came from an index or from a URL, so it is not trusted, and an address is
/// only ever built from a value that passed here. A <c>..</c>, a <c>?</c>, a <c>#</c>, a <c>%2e%2e</c>, a new line or a backslash must never get through, and a
/// trailing new line must not slip past the end of a pattern.
/// </summary>
public sealed class GitHubSkillOriginTests
{
    private static readonly string Commit = new('a', 40);

    private static bool Valid(string? owner = "LargoLujan", string? repo = "agent-studio", string? branch = "main", string? commit = null, string? folder = "skills/demo") =>
        GitHubSkillOrigin.TryCreate(owner, repo, branch, commit ?? Commit, folder, out _, out _);

    [Fact]
    public void A_valid_origin_keeps_its_five_parts()
    {
        var origin = GitHubSkillOrigin.Create("anthropics", "skills.v2_x", "release/1.0", Commit, "skills/pdf-tools");

        Assert.Equal(("anthropics", "skills.v2_x", "release/1.0", Commit, "skills/pdf-tools"), (origin.Owner, origin.Repository, origin.Branch, origin.Commit, origin.Folder));
        Assert.Equal(origin, GitHubSkillOrigin.Create("anthropics", "skills.v2_x", "release/1.0", Commit, "skills/pdf-tools"));
    }

    [Fact]
    public void A_skill_at_the_root_of_its_repository_has_an_empty_folder()
    {
        Assert.Equal(string.Empty, GitHubSkillOrigin.Create("a", "b", "main", Commit, string.Empty).Folder);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A1")]
    [InlineData("a-b-c")]
    [InlineData("1abc")]
    public void An_owner_is_letters_digits_and_hyphens_starting_with_a_letter_or_a_digit(string owner)
    {
        Assert.True(GitHubSkillOrigin.IsOwner(owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("a_b")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("a%2e")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("..")]
    [InlineData("ñ")]
    [InlineData("a\\b")]
    public void Anything_else_is_not_an_owner(string? owner)
    {
        Assert.False(GitHubSkillOrigin.IsOwner(owner));
        Assert.False(Valid(owner: owner));
    }

    [Fact]
    public void An_owner_has_up_to_thirty_nine_characters()
    {
        Assert.True(GitHubSkillOrigin.IsOwner(new string('a', 39)));
        Assert.False(GitHubSkillOrigin.IsOwner(new string('a', 40)));
    }

    [Theory]
    [InlineData("agent-studio")]
    [InlineData("a")]
    [InlineData("my.repo_name-2")]
    [InlineData(".github")]
    [InlineData("...")]
    public void A_repository_is_letters_digits_dots_underscores_and_hyphens(string repository)
    {
        Assert.True(GitHubSkillOrigin.IsRepository(repository));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a%2e%2e")]
    [InlineData("%2e%2e")]
    [InlineData("a:b")]
    [InlineData("a@b")]
    public void Anything_else_is_not_a_repository_and_neither_are_the_two_names_that_lead_up(string? repository)
    {
        Assert.False(GitHubSkillOrigin.IsRepository(repository));
        Assert.False(Valid(repo: repository));
    }

    [Fact]
    public void A_repository_has_up_to_one_hundred_characters()
    {
        Assert.True(GitHubSkillOrigin.IsRepository(new string('a', 100)));
        Assert.False(GitHubSkillOrigin.IsRepository(new string('a', 101)));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.0")]
    [InlineData("feature/a_b-c.d")]
    [InlineData("v1.2.3")]
    public void A_branch_is_names_joined_by_single_slashes(string branch)
    {
        Assert.True(GitHubSkillOrigin.IsBranch(branch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("../main")]
    [InlineData("a..b")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("main\n")]
    [InlineData("a?b=c")]
    [InlineData("a#b")]
    [InlineData("a&b")]
    [InlineData("a%2e%2e/b")]
    [InlineData("a:b")]
    [InlineData("a~b")]
    [InlineData("a^b")]
    [InlineData(".")]
    [InlineData("a/./b")]
    [InlineData("a/.")]
    [InlineData("./a")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    [InlineData("a/.hidden/b")]
    [InlineData("main.lock")]
    [InlineData("a/b.lock/c")]
    [InlineData("a/B.LOCK")]
    [InlineData("main.")]
    [InlineData("a./b")]
    [InlineData("a@{1}")]
    [InlineData("@{u}")]
    public void A_branch_that_could_change_the_address_or_lead_up_is_refused(string? branch)
    {
        Assert.False(GitHubSkillOrigin.IsBranch(branch));
        Assert.False(Valid(branch: branch));
    }

    [Fact]
    public void A_branch_has_up_to_two_hundred_characters()
    {
        Assert.True(GitHubSkillOrigin.IsBranch(new string('a', 200)));
        Assert.False(GitHubSkillOrigin.IsBranch(new string('a', 201)));
    }

    [Fact]
    public void A_commit_is_forty_lowercase_hexadecimal_characters()
    {
        Assert.True(GitHubSkillOrigin.IsCommit("0123456789abcdef0123456789abcdef01234567"));
        Assert.False(GitHubSkillOrigin.IsCommit(null));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('a', 39)));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('a', 41)));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('A', 40)));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('g', 40)));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('a', 39) + "\n"));
        Assert.False(GitHubSkillOrigin.IsCommit(new string('a', 64)));
        Assert.False(GitHubSkillOrigin.IsCommit("main"));
        Assert.False(Valid(commit: new string('a', 39) + "\n"));
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("skills/demo")]
    [InlineData("a/b.c/d_e-f")]
    [InlineData(".claude/skills/x")]
    public void A_folder_is_segments_that_follow_the_rule_of_a_repository_name(string folder)
    {
        Assert.True(GitHubSkillOrigin.IsFolder(folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../b")]
    [InlineData("../a")]
    [InlineData("a/./b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a%2e%2e")]
    [InlineData("%2e%2e/a")]
    [InlineData("a:b")]
    [InlineData("C:/a")]
    public void A_folder_that_could_lead_up_or_change_the_address_is_refused(string? folder)
    {
        Assert.False(GitHubSkillOrigin.IsFolder(folder));
        Assert.False(Valid(folder: folder));
    }

    [Fact]
    public void A_folder_has_up_to_four_hundred_characters()
    {
        Assert.True(GitHubSkillOrigin.IsFolder(new string('a', 400)));
        Assert.False(GitHubSkillOrigin.IsFolder(new string('a', 401)));
    }

    [Theory]
    [InlineData(null, "r", "main", GitHubOriginProblem.Owner)]
    [InlineData("o", "..", "main", GitHubOriginProblem.Repository)]
    [InlineData("o", "r", "a/../b", GitHubOriginProblem.Branch)]
    public void The_first_part_that_is_not_valid_is_the_one_named_and_nothing_is_created(string? owner, string? repository, string? branch, GitHubOriginProblem expected)
    {
        var created = GitHubSkillOrigin.TryCreate(owner, repository, branch, Commit, "f", out var origin, out var problem);

        Assert.False(created);
        Assert.Null(origin);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void Creating_with_a_part_that_is_not_valid_names_the_part_and_never_the_value()
    {
        var error = Assert.Throws<ArgumentException>(() => GitHubSkillOrigin.Create("o", "r", "main", "secret-not-a-commit", "f"));

        Assert.Contains(nameof(GitHubOriginProblem.Commit), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-not-a-commit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_in_the_commit_and_in_the_folder_are_reported_as_such()
    {
        GitHubSkillOrigin.TryCreate("o", "r", "main", "abc", "f", out _, out var commitProblem);
        GitHubSkillOrigin.TryCreate("o", "r", "main", Commit, "..", out _, out var folderProblem);

        Assert.Equal(GitHubOriginProblem.Commit, commitProblem);
        Assert.Equal(GitHubOriginProblem.Folder, folderProblem);
    }
}
