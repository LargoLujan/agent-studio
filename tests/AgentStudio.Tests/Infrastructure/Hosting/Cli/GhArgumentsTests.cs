using AgentStudio.Infrastructure.Hosting.Cli;

namespace AgentStudio.Tests.Infrastructure.Hosting.Cli;

/// <summary>
/// docs/21 D-10 and F10-12: the one form of the command line of <c>gh</c>. Free text is never an argument; the only data of the remote that become one are the validated path and the exact form of
/// an <c>ETag</c>; no argument holds a brace (<c>gh</c> would fill <c>{owner}</c>, <c>{repo}</c> and <c>{branch}</c> from the folder it runs in).
/// </summary>
public sealed class GhArgumentsTests
{
    private const string Path = "/repos/octo/hello/pulls/7";

    [Fact]
    public void A_read_is_the_one_form_with_the_host_the_method_the_include_flag_and_the_two_fixed_headers()
    {
        var arguments = GhArguments.Build(new GhCall(GhMethod.Get, Path));

        Assert.Equal(
            ["api", "--hostname", "github.com", "-X", "GET", "-i", "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28", Path],
            arguments);
    }

    [Fact]
    public void A_condition_is_a_third_header_and_a_body_is_only_the_flag_for_standard_input()
    {
        var read = GhArguments.Build(new GhCall(GhMethod.Get, Path, "W/\"abc\""));
        var write = GhArguments.Build(new GhCall(GhMethod.Patch, Path, Body: "{\"title\":\"-x --admin @file\"}"));

        Assert.Equal("If-None-Match: W/\"abc\"", read[^2]);
        Assert.Equal(["--input", "-", Path], write.Skip(write.Count - 3));
        Assert.DoesNotContain(write, a => a.Contains("title", StringComparison.Ordinal));
    }

    [Fact]
    public void No_argument_is_one_of_the_forbidden_flags_or_holds_a_brace_whatever_the_call()
    {
        foreach (var method in Enum.GetValues<GhMethod>())
        {
            var call = method is GhMethod.Get or GhMethod.Delete ? new GhCall(method, Path, "\"e\"") : new GhCall(method, Path, "\"e\"", "{}");
            var arguments = GhArguments.Build(call);
            Assert.DoesNotContain(arguments, a => a is "-f" or "-F" or "--field" or "--raw-field" or "--jq" or "-q" or "--template" or "-t" or "--paginate" or "--slurp" or "--cache" or "--verbose" or "--preview" or "-p");
            Assert.DoesNotContain(arguments, a => a.Contains('{', StringComparison.Ordinal) || a.Contains('}', StringComparison.Ordinal));
            Assert.Equal("github.com", arguments[arguments.ToList().IndexOf("--hostname") + 1]);
        }
    }

    [Theory]
    [InlineData("repos/octo/hello")]
    [InlineData("/repos/octo/hello/../../orgs/x")]
    [InlineData("/repos/octo/hello/%2e%2e/%2E%2e/user")]
    [InlineData("/repos/octo/hello/./pulls")]
    [InlineData("/repos/{owner}/{repo}/pulls")]
    [InlineData("/repos/octo/hello/branches/{branch}")]
    [InlineData("/repos/octo/hello?head={branch}")]
    [InlineData("/repos/octo/hello?head=a b")]
    [InlineData("/repos/octo/hello\n/pulls")]
    [InlineData("/repos/octo/hello pulls")]
    [InlineData("/repos/octo/hello --admin")]
    [InlineData("/repos/octo/hello?x=1#frag")]
    [InlineData("/repos/octo/hello/pulls?x=a+b")]
    [InlineData("/repos/octo/hello/pulls?x=a:b")]
    [InlineData("-X")]
    [InlineData("--hostname")]
    [InlineData("@file")]
    [InlineData("https://evil.example/repos/octo/hello")]
    [InlineData("//evil.example/repos/octo/hello")]
    [InlineData("/graphql")]
    [InlineData("/user/../repos/octo/hello")]
    [InlineData("/repos/octo")]
    [InlineData("/repos/-octo/hello")]
    [InlineData("/repos/oc_to/hello")]
    [InlineData("/repos/octo/hello/\u202Epulls")]
    [InlineData("/orgs/octo")]
    [InlineData("/user\n")]
    [InlineData("/repos/octo/hello\n")]
    [InlineData("/repos/octo/hello/pulls\r\n")]
    [InlineData("")]
    public void A_path_that_is_not_one_of_the_two_forms_is_never_built_into_a_command(string path)
    {
        Assert.False(GhArguments.IsAllowedPath(path));
        Assert.Throws<ArgumentException>(() => GhArguments.Build(new GhCall(GhMethod.Get, path)));
    }

    [Theory]
    [InlineData("/user")]
    [InlineData("/repos/octo/hello")]
    [InlineData("/repos/o-c-t-o/he.llo_x-1/git/refs/heads/agentstudio/run-r1")]
    [InlineData("/repos/octo/hello/pulls?head=octo%3Aagentstudio%2Frun-r1&state=all&per_page=5")]
    [InlineData("/repos/octo/hello/commits?sha=main&since=2026-11-13T10%3A00%3A00Z&per_page=50")]
    public void The_two_forms_that_the_builder_writes_pass(string path) => Assert.True(GhArguments.IsAllowedPath(path));

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("W/\"a1b2c3d4e5f6\"", true)]
    [InlineData("abc", false)]
    [InlineData("\"\"", false)]
    [InlineData("\"a b\"", false)]
    [InlineData("\"a\"b\"", false)]
    [InlineData("\"a\"\n", false)]
    [InlineData("W/\"a\"\r\n", false)]
    [InlineData("\"a\"\nX-Evil: 1", false)]
    [InlineData("\"a\"\r\nX-Evil: 1", false)]
    [InlineData("\"a\", \"b\"", false)]
    [InlineData("w/\"abc\"", false)]
    [InlineData("W/ \"abc\"", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    public void An_etag_has_the_exact_form_of_one(string etag, bool valid)
    {
        Assert.Equal(valid, GhArguments.IsETag(etag));
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => GhArguments.Build(new GhCall(GhMethod.Get, Path, etag)));
        }
    }

    [Fact]
    public void An_etag_of_129_characters_is_not_one_and_one_of_128_is()
    {
        Assert.True(GhArguments.IsETag("\"" + new string('a', 128) + "\""));
        Assert.False(GhArguments.IsETag("\"" + new string('a', 129) + "\""));
    }

    [Fact]
    public void A_method_that_sends_nothing_refuses_a_body()
    {
        Assert.Throws<ArgumentException>(() => GhArguments.Build(new GhCall(GhMethod.Get, Path, Body: "{}")));
        Assert.Throws<ArgumentException>(() => GhArguments.Build(new GhCall(GhMethod.Delete, Path, Body: "{}")));
    }
}
