using System.Text.RegularExpressions;
using AgentStudio.Application.Hosting;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// The two lists of docs/21 D-05 over entries of the configuration, with no process: one test for each key that is refused, one for each key that a <c>-c</c> overrides (and so is never refused), and the rules of scope
/// and of origin. The keys are written as git prints them (section and variable in lowercase, the subsection as it is written).
/// </summary>
public sealed class RemoteConfigInspectionTests
{
    private const string Address = "https://github.com/o/r.git";
    private const string Secret = "ghp_S3cretT0ken";

    private static GitCallbacks.ConfigEntry Entry(string scope, string key, string value = "x", string origin = "file:.git/config") => new(scope, origin, key, value);

    private static RemoteConfigAnalysis Analyze(bool inside, params GitCallbacks.ConfigEntry[] entries) => RemoteConfigInspection.Analyze(entries, Address, _ => inside);

    public static TheoryData<string, string> RefusedKeys => new()
    {
        { "credential.helper", "credential.helper" },
        { "credential.https://github.com.helper", "credential.*.helper" },
        { "credential.username", "credential.username" },
        { "credential.useHttpPath", "credential.usehttppath" },
        { "url.https://evil.example/.insteadof", "url.*.insteadof" },
        { "url.https://evil.example/.pushinsteadof", "url.*.pushinsteadof" },
        { "remote.origin.pushurl", "remote.*.pushurl" },
        { "remote.origin.push", "remote.*.push" },
        { "remote.origin.receivepack", "remote.*.receivepack" },
        { "remote.origin.uploadpack", "remote.*.uploadpack" },
        { "remote.origin.proxy", "remote.*.proxy" },
        { "remote.origin.proxyauthmethod", "remote.*.proxyauthmethod" },
        { "remote.origin.vcs", "remote.*.vcs" },
        { "remote.pushdefault", "remote.pushdefault" },
        { "branch.main.pushremote", "branch.*.pushremote" },
        { "push.pushoption", "push.pushoption" },
        { "http.proxy", "http.proxy" },
        { "http.extraheader", "http.extraheader" },
        { "http.sslverify", "http.sslverify" },
        { "http.sslcainfo", "http.sslcainfo" },
        { "http.cookiefile", "http.cookiefile" },
        { "http.https://example.com/.proxy", "http.*.proxy" },
        { "http.https://example.com/.extraheader", "http.*.extraheader" },
        { "http.https://example.com/.followredirects", "http.*.followredirects" },
        { "core.alternaterefscommand", "core.alternaterefscommand" },
    };

    public static TheoryData<string> OverriddenKeys => new()
    {
        "core.hookspath", "core.fsmonitor", "core.sshcommand", "core.gitproxy", "core.askpass",
        "protocol.https.allow", "protocol.file.allow", "protocol.allow", "http.followredirects",
        "push.default", "push.followtags", "push.recursesubmodules", "push.gpgsign", "push.autosetupremote", "submodule.recurse",
    };

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void A_key_that_a_pair_cannot_override_is_refused_when_the_repository_defines_it(string key, string shown)
    {
        var analysis = Analyze(false, Entry("local", key));

        var refusal = Assert.Single(analysis.Refusals);
        Assert.Equal(shown, refusal.Key);
        Assert.Equal("local", refusal.Scope);
        Assert.Equal(".git/config", refusal.OriginFile);
    }

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void The_same_key_of_a_worktree_is_refused_too(string key, string shown)
    {
        var refusal = Assert.Single(Analyze(false, Entry("worktree", key, origin: "file:.git/config.worktree")).Refusals);

        Assert.Equal(shown, refusal.Key);
        Assert.Equal("worktree", refusal.Scope);
    }

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void The_global_and_system_configuration_of_the_person_counts_and_is_not_refused(string key, string shown)
    {
        _ = shown;
        var analysis = Analyze(false, Entry("global", key, origin: "file:C:/Users/ana/.gitconfig"), Entry("system", key, origin: "file:C:/Program Files/Git/etc/gitconfig"));

        // A global url.*.pushinsteadof only refuses when it rewrites the address (see the test of the prefix).
        Assert.Empty(analysis.Refusals);
    }

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void A_global_key_that_comes_from_a_file_inside_the_repository_is_refused_for_its_origin(string key, string shown)
    {
        var refusal = Assert.Single(Analyze(true, Entry("global", key, origin: @"file:C:\repo\sub\extra.cfg")).Refusals);

        Assert.Equal(shown, refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.Equal(@"C:\repo\sub\extra.cfg", refusal.OriginFile);
    }

    [Theory]
    [MemberData(nameof(OverriddenKeys))]
    public void A_key_that_a_pair_overrides_is_never_refused_even_when_the_repository_defines_it(string key)
    {
        var analysis = Analyze(true, Entry("local", key), Entry("worktree", key), Entry("global", key));

        Assert.Empty(analysis.Refusals);
    }

    [Theory]
    [InlineData("remote.origin.url")]
    [InlineData("remote.origin.fetch")]
    [InlineData("remote.origin.tagopt")]
    [InlineData("branch.main.remote")]
    [InlineData("branch.main.merge")]
    [InlineData("core.autocrlf")]
    [InlineData("core.repositoryformatversion")]
    [InlineData("include.path")]
    [InlineData("includeif.gitdir:c:/work/.path")]
    [InlineData("filter.lfs.clean")]
    public void The_ordinary_keys_of_a_repository_do_not_stop_a_publication(string key) =>
        Assert.Empty(Analyze(true, Entry("local", key)).Refusals);

    [Theory]
    [InlineData("remote.https://github.com/o/r.git.url", "remote.*.url")]
    [InlineData("remote.https://github.com/o/r.git.mirror", "remote.*.mirror")]
    [InlineData("remote.https://github.com/o/r.git.fetch", "remote.*.fetch")]
    [InlineData("remote.https://github.com/o/r.git.pushurl", "remote.*.pushurl")]
    [InlineData("remote.https://github.com/o/r.git.tagopt", "remote.*.tagopt")]
    [InlineData("remote.C:/path/to/remote.git.anything", "remote.*.anything")]
    [InlineData(@"remote.C:\path\to\remote.git.mirror", "remote.*.mirror")]
    [InlineData("remote.git@github.com:o/r.git.url", "remote.*.url")]
    [InlineData("remote.../elsewhere.mirror", "remote.*.mirror")]
    public void A_remote_of_the_repository_called_like_an_address_or_a_path_is_refused_whatever_its_key(string key, string shown)
    {
        var local = Assert.Single(Analyze(false, Entry("local", key)).Refusals);
        var inside = Assert.Single(Analyze(true, Entry("global", key, origin: @"file:C:\repo\x.cfg")).Refusals);

        Assert.Equal(shown, local.Key);
        Assert.Equal("global", inside.Scope);
        Assert.Empty(Analyze(false, Entry("global", key, origin: "file:C:/Users/ana/.gitconfig")).Refusals);
    }

    [Theory]
    [InlineData("remote.origin.mirror")]
    [InlineData("remote.origin.url")]
    [InlineData("remote.origin.prune")]
    [InlineData("remote.upstream.fetch")]
    [InlineData("remote.my-fork_2.tagopt")]
    public void A_remote_with_a_plain_name_is_not_refused_for_its_key(string key) =>
        Assert.Empty(Analyze(true, Entry("local", key)).Refusals);

    [Fact]
    public void A_scope_that_is_not_one_of_the_files_of_the_person_counts_as_the_repository_s()
    {
        Assert.Single(Analyze(false, Entry("unknown", "http.proxy")).Refusals);
        Assert.Single(Analyze(false, Entry(string.Empty, "credential.helper")).Refusals);
    }

    [Fact]
    public void The_pairs_of_the_command_line_are_not_the_configuration_of_anybody()
    {
        Assert.Empty(Analyze(true, Entry("command", "core.sshcommand", string.Empty, "command line:"), Entry("command", "credential.helper", "x", "command line:")).Refusals);
    }

    [Fact]
    public void The_refusal_names_the_key_the_scope_and_the_file_and_never_the_value_nor_an_address_inside_the_key()
    {
        var analysis = Analyze(
            false,
            Entry("local", "credential.helper", "!echo " + Secret),
            Entry("local", "url.https://user:" + Secret + "@evil.example/.insteadof", "https://github.com/"),
            Entry("local", "http.https://u:" + Secret + "@example.com/.proxy", "http://" + Secret + "@proxy"),
            Entry("local", "http.proxy", "http://secret-user:" + Secret + "@proxy.example:8080"));

        Assert.Equal(4, analysis.Refusals.Count);
        foreach (var refusal in analysis.Refusals)
        {
            Assert.DoesNotContain(Secret, refusal.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-user", refusal.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(["credential.helper", "url.*.insteadof", "http.*.proxy", "http.proxy"], analysis.Refusals.Select(r => r.Key));
    }

    [Fact]
    public void The_file_of_a_refusal_loses_its_control_characters_and_is_capped()
    {
        var refusal = Assert.Single(Analyze(false, Entry("local", "http.proxy", origin: "file:.git/con\u001b[31mfig\r\n" + new string('a', 400))).Refusals);

        Assert.DoesNotContain(refusal.OriginFile, char.IsControl);
        Assert.Equal(260, refusal.OriginFile.Length);
    }

    [Fact]
    public void The_same_key_twice_in_one_file_is_one_refusal()
    {
        var analysis = Analyze(false, Entry("local", "credential.helper", "a"), Entry("local", "credential.helper", "b"));

        Assert.Single(analysis.Refusals);
    }

    [Fact]
    public void A_pushinsteadof_of_the_person_that_rewrites_the_address_is_refused_by_prefix_because_ls_remote_get_url_does_not_apply_it()
    {
        var rewrites = Analyze(false, Entry("global", "url.https://mirror.example/.pushinsteadof", "https://github.com/", "file:C:/Users/ana/.gitconfig"));
        var rewritesOne = Analyze(false, Entry("system", "url.https://mirror.example/.pushinsteadof", "https://github.com/o/", "file:C:/etc/gitconfig"));
        var everything = Analyze(false, Entry("global", "url.https://mirror.example/.pushinsteadof", string.Empty));
        var other = Analyze(false, Entry("global", "url.https://mirror.example/.pushinsteadof", "https://gitlab.com/"));
        var otherOwner = Analyze(false, Entry("global", "url.https://mirror.example/.pushinsteadof", "https://github.com/someone-else/"));

        var refusal = Assert.Single(rewrites.Refusals);
        Assert.Equal(("url.*.pushinsteadof", "global", "C:/Users/ana/.gitconfig"), (refusal.Key, refusal.Scope, refusal.OriginFile));
        Assert.Single(rewritesOne.Refusals);
        Assert.Single(everything.Refusals);
        Assert.Empty(other.Refusals);
        Assert.Empty(otherOwner.Refusals);
    }

    [Fact]
    public void A_rewrite_that_git_reported_is_named_by_the_entry_of_insteadof_whose_value_is_a_prefix_and_otherwise_by_a_generic_key()
    {
        var entries = new[]
        {
            Entry("global", "url.https://other.example/.insteadof", "https://gitlab.com/", "file:C:/Users/ana/.gitconfig"),
            Entry("global", "url.https://mirror.example/.insteadof", "https://github.com/", "file:C:/Users/ana/.gitconfig"),
        };

        var named = RemoteConfigInspection.NameRewrite(entries, Address);
        var generic = RemoteConfigInspection.NameRewrite([], Address);

        Assert.Equal(new RemoteConfigRefusal("url.*.insteadof", "global", "C:/Users/ana/.gitconfig"), named);
        Assert.Equal("url.*.insteadof", generic.Key);
        Assert.Equal("unknown", generic.Scope);
    }

    [Theory]
    [InlineData("system", "filter.lfs.clean", "git-lfs clean -- %f", true)]
    [InlineData("global", "filter.lfs.smudge", "git-lfs smudge -- %f", true)]
    [InlineData("local", "filter.lfs.process", "git-lfs filter-process", true)]
    [InlineData("system", "filter.lfs.required", "true", false)]
    [InlineData("system", "filter.lfs.clean", "", false)]
    [InlineData("system", "filter.other.clean", "x", false)]
    public void A_filter_of_git_lfs_in_any_scope_is_the_first_sign_of_lfs(string scope, string key, string value, bool configured) =>
        Assert.Equal(configured, Analyze(false, Entry(scope, key, value, "file:C:/etc/gitconfig")).LfsFilterConfigured);

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void The_pattern_of_the_query_reaches_every_key_that_is_judged(string key, string shown)
    {
        _ = shown;
        Assert.Matches(new Regex(RemoteConfigInspection.Pattern, RegexOptions.CultureInvariant), key);
        Assert.DoesNotMatch(new Regex(RemoteConfigInspection.Pattern, RegexOptions.CultureInvariant), "user.name");
    }

    [Fact]
    public void The_output_of_git_is_read_by_the_reader_of_the_internal_layer()
    {
        var output = RemoteGitCommands.ConfigEntry("local", "file:.git/config", "credential.helper", "!x")
            + RemoteGitCommands.ConfigEntry("global", "file:C:/Users/ana/.gitconfig", "url.https://mirror.example/.insteadof", "https://github.com/")
            + RemoteGitCommands.ConfigEntry("local", "file:.git/config", "http.sslverify", "false");

        var analysis = RemoteConfigInspection.Analyze(GitCallbacks.Parse(output), Address, origin => origin.StartsWith("file:C:/inside", StringComparison.Ordinal));

        Assert.Equal(["credential.helper", "http.sslverify"], analysis.Refusals.Select(r => r.Key));
    }
}
