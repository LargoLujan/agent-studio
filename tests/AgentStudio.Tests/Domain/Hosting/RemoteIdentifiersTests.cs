using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>
/// docs/21 D-04 (P0 of Phase 10): a repository, a branch, a pull request and an account exist only by validated identifiers. A parse rejects, it does not fix; the
/// address of a push is written by the code from the identifiers; and what does not pass never becomes a value.
/// </summary>
public sealed class RemoteIdentifiersTests
{
    // ---- RemoteRepository ----

    [Theory]
    [InlineData("https://github.com/LargoLujan/agent-studio.git", "LargoLujan", "agent-studio")]
    [InlineData("https://github.com/LargoLujan/agent-studio", "LargoLujan", "agent-studio")]
    [InlineData("git@github.com:LargoLujan/agent-studio.git", "LargoLujan", "agent-studio")]
    [InlineData("git@github.com:o/r", "o", "r")]
    [InlineData("https://github.com/o/r.git.git", "o", "r.git")]
    [InlineData("https://github.com/a-b-c/.github", "a-b-c", ".github")]
    [InlineData("https://github.com/o/a.b_c-d", "o", "a.b_c-d")]
    public void The_two_forms_that_identify_a_repository_are_read_and_the_address_is_written_by_the_code(string url, string owner, string name)
    {
        var repository = RemoteRepository.TryParseGitHub(url, out var refusal);

        Assert.NotNull(repository);
        Assert.Null(refusal);
        Assert.Equal((HostingProviderKind.GitHub, "github.com", owner, name), (repository.Provider, repository.Host, repository.Owner, repository.Name));
        Assert.Equal($"https://github.com/{owner}/{name}.git", repository.PushUrl);
        Assert.Equal($"github.com/{owner}/{name}", repository.Id);
        Assert.Equal(repository, RemoteRepository.TryParseId(repository.Id));
    }

    [Theory]
    [InlineData("", RemoteRepository.RefusalUnrecognized)]
    [InlineData(" https://github.com/o/r", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o/r ", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o/r\n", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o/r\0", RemoteRepository.RefusalUnrecognized)]
    [InlineData("http://github.com/o/r", RemoteRepository.RefusalUnrecognized)]
    [InlineData("ssh://git@github.com/o/r.git", RemoteRepository.RefusalUnrecognized)]
    [InlineData("file:///c:/repo", RemoteRepository.RefusalUnrecognized)]
    [InlineData("C:\\repo\\bare.git", RemoteRepository.RefusalUnrecognized)]
    [InlineData("/srv/git/r.git", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o/r/", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com/o/r/extra", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://github.com//o/r", RemoteRepository.RefusalUnrecognized)]
    [InlineData("git@github.com", RemoteRepository.RefusalUnrecognized)]
    [InlineData("git@github.com:o", RemoteRepository.RefusalUnrecognized)]
    [InlineData("git@github.com:o/r/x", RemoteRepository.RefusalUnrecognized)]
    [InlineData("user@github.com:o/r", RemoteRepository.RefusalUnrecognized)]
    [InlineData("https://gitlab.com/o/r", RemoteRepository.RefusalHost)]
    [InlineData("https://GitHub.com/o/r", RemoteRepository.RefusalHost)]
    [InlineData("https://github.com.evil.example/o/r", RemoteRepository.RefusalHost)]
    [InlineData("https://evilgithub.com/o/r", RemoteRepository.RefusalHost)]
    [InlineData("https://github.co/o/r", RemoteRepository.RefusalHost)]
    [InlineData("git@gitlab.com:o/r", RemoteRepository.RefusalHost)]
    [InlineData("git@github.com.evil.example:o/r", RemoteRepository.RefusalHost)]
    [InlineData("https://user@github.com/o/r", RemoteRepository.RefusalCredentialsInUrl)]
    [InlineData("https://user:ghp_secret@github.com/o/r", RemoteRepository.RefusalCredentialsInUrl)]
    [InlineData("https://github.com:443/o/r", RemoteRepository.RefusalPort)]
    [InlineData("https://github.com:8443/o/r", RemoteRepository.RefusalPort)]
    [InlineData("https://github.com/-o/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/o_o/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/o.o/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/ó/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/o%2fx/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/o?x=1/r", RemoteRepository.RefusalOwner)]
    [InlineData("https://github.com/o/..", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/.", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/a..b", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/.git", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/r;rm", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/r$x", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/r#frag", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/r\\x", RemoteRepository.RefusalName)]
    [InlineData("https://github.com/o/ré", RemoteRepository.RefusalName)]
    public void What_is_not_exactly_a_repository_of_github_is_refused_with_a_code_and_never_fixed(string url, string code)
    {
        var repository = RemoteRepository.TryParseGitHub(url, out var refusal);

        Assert.Null(repository);
        Assert.Equal(code, refusal);
    }

    [Fact]
    public void A_null_address_and_one_that_is_too_long_are_refused_and_the_owner_and_the_name_have_their_limits()
    {
        Assert.Null(RemoteRepository.TryParseGitHub(null, out var nothing));
        Assert.Equal(RemoteRepository.RefusalUnrecognized, nothing);
        Assert.Null(RemoteRepository.TryParseGitHub("https://github.com/o/" + new string('r', 600), out var tooLong));
        Assert.Equal(RemoteRepository.RefusalUnrecognized, tooLong);

        Assert.NotNull(RemoteRepository.TryCreateGitHub("a" + new string('b', 38), new string('r', 100)));
        Assert.Null(RemoteRepository.TryCreateGitHub("a" + new string('b', 39), "r"));
        Assert.Null(RemoteRepository.TryCreateGitHub("o", new string('r', 101)));
        Assert.Null(RemoteRepository.TryCreateGitHub(null, "r"));
        Assert.Null(RemoteRepository.TryCreateGitHub("o", null));
        Assert.Null(RemoteRepository.TryCreateGitHub("o", ""));
    }

    [Theory]
    [InlineData("github.com/o/r", true)]
    [InlineData("github.com/o/r/x", false)]
    [InlineData("gitlab.com/o/r", false)]
    [InlineData("o/r", false)]
    [InlineData("github.com/o", false)]
    [InlineData("", false)]
    public void An_identifier_is_read_back_only_when_it_has_the_form_the_code_writes(string id, bool valid) =>
        Assert.Equal(valid, RemoteRepository.TryParseId(id) is not null);

    [Fact]
    public void A_repository_cannot_be_changed_into_another_that_was_not_validated()
    {
        var repository = HostingP0Data.Repository;

        // No setter, not even init: a «with» has nothing to change, and the equality is exact.
        Assert.All(typeof(RemoteRepository).GetProperties(), p => Assert.True(p.SetMethod is null, p.Name));
        Assert.NotEqual(repository, RemoteRepository.TryCreateGitHub("largolujan", "agent-studio"));
        Assert.Equal(repository, RemoteRepository.TryCreateGitHub("LargoLujan", "agent-studio"));
    }

    // ---- RemoteBranch ----

    [Theory]
    [InlineData("main")]
    [InlineData("feature/x")]
    [InlineData("release-1.2")]
    [InlineData("agentstudio/run-20261113-a1b2c3")]
    [InlineData("a@b")]
    [InlineData("ñandú")]
    [InlineData("user/topic_1")]
    public void A_valid_name_of_a_branch_is_kept(string name) => Assert.Equal(name, RemoteBranch.TryParse(name)?.Name);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-main")]
    [InlineData("--force")]
    [InlineData("-")]
    [InlineData("@")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\u007fb")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a.")]
    [InlineData("a@{u}")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    [InlineData("a.lock")]
    [InlineData("a/b.lock/c")]
    [InlineData("a\u202eb")]
    [InlineData("a\u200bb")]
    public void A_name_that_git_check_ref_format_refuses_or_that_could_be_an_option_is_not_a_branch(string name) => Assert.Null(RemoteBranch.TryParse(name));

    [Fact]
    public void A_name_too_long_is_not_a_branch_and_null_is_not_one()
    {
        Assert.Null(RemoteBranch.TryParse(null));
        Assert.Null(RemoteBranch.TryParse(new string('a', RemoteBranch.MaxLength + 1)));
        Assert.NotNull(RemoteBranch.TryParse(new string('a', RemoteBranch.MaxLength)));
    }

    [Fact]
    public void The_branch_of_a_run_is_the_one_the_code_writes_and_only_that_shape_is_a_run_branch()
    {
        var branch = RemoteBranch.ForRun(new RunId("20261113-a1b2c3"));

        Assert.Equal("agentstudio/run-20261113-a1b2c3", branch.Name);
        Assert.True(branch.IsRunBranch);
        Assert.Equal("20261113-a1b2c3", branch.RunIdentifier);
        Assert.Equal(branch, RemoteBranch.TryParse("agentstudio/run-20261113-a1b2c3"));
        Assert.False(RemoteBranch.TryParse("main")!.IsRunBranch);
        Assert.Null(RemoteBranch.TryParse("main")!.RunIdentifier);
        Assert.False(RemoteBranch.TryParse("agentstudio/run-")!.IsRunBranch);
        Assert.False(RemoteBranch.TryParse("agentstudio/run-a/b")!.IsRunBranch);
        Assert.False(RemoteBranch.TryParse("agentstudio/run-a.b")!.IsRunBranch);
        Assert.False(RemoteBranch.TryParse("agentstudio/other-1")!.IsRunBranch);
        Assert.False(RemoteBranch.TryParse("x/agentstudio/run-1")!.IsRunBranch);
        Assert.False(RemoteBranch.TryParse("agentstudio/run-" + new string('a', RunId.MaxLength + 1))!.IsRunBranch);
    }

    // ---- PullRequestRef and the node of the trace ----

    [Fact]
    public void A_pull_request_is_a_repository_and_a_number_above_zero_and_its_id_is_the_one_of_the_trace()
    {
        var pull = new PullRequestRef(HostingP0Data.Repository, 12);

        Assert.Equal("github.com/LargoLujan/agent-studio#12", pull.TraceId);
        Assert.Equal(pull, PullRequestRef.TryParseTraceId(pull.TraceId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PullRequestRef(HostingP0Data.Repository, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PullRequestRef(HostingP0Data.Repository, -3));
        Assert.Throws<ArgumentNullException>(() => new PullRequestRef(null!, 1));
    }

    [Theory]
    [InlineData("github.com/o/r#0")]
    [InlineData("github.com/o/r#01")]
    [InlineData("github.com/o/r#-1")]
    [InlineData("github.com/o/r#1x")]
    [InlineData("github.com/o/r#")]
    [InlineData("github.com/o/r#1234567890")]
    [InlineData("github.com/o/r")]
    [InlineData("gitlab.com/o/r#1")]
    [InlineData("#1")]
    [InlineData("github.com/o/r/x#1")]
    public void An_id_of_a_pull_request_that_is_not_exactly_the_one_the_code_writes_is_not_read(string id)
    {
        Assert.Null(PullRequestRef.TryParseTraceId(id));
        Assert.Throws<ArgumentException>(() => new TraceRef(TraceNodeKind.PullRequest, id));
    }

    [Fact]
    public void The_node_of_a_pull_request_exists_in_the_trace_with_its_id_and_is_appended_to_the_kinds()
    {
        var node = TraceRef.PullRequest(HostingP0Data.PullRequest);

        Assert.Equal(TraceNodeKind.PullRequest, node.Kind);
        Assert.Equal("github.com/LargoLujan/agent-studio#12", node.Id);
        Assert.Equal("PullRequest:github.com/LargoLujan/agent-studio#12", node.ToString());
        Assert.Equal(TraceNodeKind.PullRequest, Enum.GetValues<TraceNodeKind>()[^1]);
        Assert.Equal(12, (int)TraceNodeKind.PullRequest);
    }

    // ---- RemoteLogin ----

    [Theory]
    [InlineData("manuel")]
    [InlineData("Manuel-Lujan")]
    [InlineData("a")]
    [InlineData("A1")]
    public void An_account_follows_the_rule_of_the_owner(string login) => Assert.Equal(login, RemoteLogin.TryParse(login)?.Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" manuel")]
    [InlineData("-manuel")]
    [InlineData("man_uel")]
    [InlineData("man.uel")]
    [InlineData("dependabot[bot]")]
    [InlineData("manuel\n")]
    [InlineData("@manuel")]
    [InlineData("mánuel")]
    public void Anything_else_is_no_consta_not_an_account(string? login) => Assert.Null(RemoteLogin.TryParse(login));

    [Fact]
    public void An_account_of_the_longest_length_passes_and_one_character_more_does_not()
    {
        Assert.NotNull(RemoteLogin.TryParse("a" + new string('b', 38)));
        Assert.Null(RemoteLogin.TryParse("a" + new string('b', 39)));
    }
}
