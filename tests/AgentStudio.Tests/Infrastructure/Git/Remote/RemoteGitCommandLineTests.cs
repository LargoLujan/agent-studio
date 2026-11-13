using AgentStudio.Application.Execution;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// «Nunca forzar» (docs/21 D-07): the lists of arguments are constants, and what is not a constant is checked. <see cref="AssertClosed"/> is the walk that every test of the executor runs on every
/// <see cref="ProcessSpec"/> it launches: nothing in the closed list may carry a force, a deletion, a mirror, a tag, a push option, another refspec form or another sub-command.
/// </summary>
public sealed class RemoteGitCommandLineTests
{
    private static readonly string[] Forbidden =
    [
        "--force", "-f", "--force-with-lease", "--force-if-includes", "--mirror", "--prune", "--delete", "-d", "--all", "--branches", "--tags", "-o", "--push-option",
        "--receive-pack", "--exec", "--upload-pack", "--set-upstream", "-u", "--atomic", "--repo",
    ];

    private static readonly string[] Commands = ["config", "grep", "ls-remote", "push"];

    /// <summary>The walk of D-07 over one launched command; fails with the argument that breaks it.</summary>
    internal static void AssertClosed(ProcessSpec spec)
    {
        var command = RemoteGitCommands.CommandOf(spec);
        Assert.Contains(command[0], Commands);
        Assert.DoesNotContain(command[0], new[] { "fetch", "pull", "clone", "remote", "checkout", "merge" });
        Assert.Equal(command[0] == "config" ? 0 : 1, command.Count(argument => argument == "--"));

        // The pairs of configuration come first and in the order of D-06, whatever the command; the environment is the allow list.
        var settings = RemoteGitCommands.Settings(spec);
        Assert.StartsWith("core.hooksPath=", settings[0], StringComparison.Ordinal);
        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(spec.Environment);
        var scheme = environment["GIT_ALLOW_PROTOCOL"]!;
        var transport = scheme == "https" ? RemoteTransport.GitHubHttps : RemoteTransport.ForTests(scheme, r => r.PushUrl);
        var expected = RemoteGitCommandLine.Configuration(transport, settings[0]["core.hooksPath=".Length..]);
        Assert.Equal(expected, spec.Arguments.Take(expected.Count));
        foreach (var name in RemoteGitEnvironment.AlwaysRemoved)
        {
            Assert.True(environment.ContainsKey(name), name);
            Assert.Null(environment[name]);
        }

        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("cat", environment["GIT_PAGER"]);
        Assert.Equal("1", environment["GIT_NO_LAZY_FETCH"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
        Assert.Equal("C", environment["LC_ALL"]);
        Assert.Equal(environment["TEMP"], environment["TMP"]);
        Assert.Null(spec.StandardInput);
        Assert.Equal(RemoteGitCommandLine.MaxOutputCharacters, spec.MaxOutputCharacters);
        if (command[0] == "push")
        {
            Assert.Contains("--porcelain", command);
            Assert.Contains("--no-verify", command);
            Assert.Contains("--no-follow-tags", command);
            Assert.Equal(RemoteGitCommandLine.PushTimeout, spec.Timeout);
        }
        else
        {
            Assert.Equal(RemoteGitCommandLine.QueryTimeout, spec.Timeout);
        }

        if (command[0] == "grep")
        {
            Assert.Equal(["grep", "-l", "-i", "-E", "--no-color", "-e", "^[^#]*filter=lfs"], command.Take(7));
        }
        foreach (var argument in command)
        {
            Assert.DoesNotContain(argument, Forbidden);
            Assert.False(argument.StartsWith("--force", StringComparison.Ordinal) || argument.StartsWith("--delete", StringComparison.Ordinal) || argument.StartsWith("--push-option", StringComparison.Ordinal), argument);
        }

        if (command[0] == "push")
        {
            var refspecs = command.SkipWhile(argument => argument != "--").Skip(2).ToList();
            var refspec = Assert.Single(refspecs);
            Assert.DoesNotContain(refspec[0], "+:");
            Assert.Matches("^[0-9a-f]{40}:refs/heads/agentstudio/run-[A-Za-z0-9_-]{1,64}$", refspec);
        }

        // Nothing that opens or closes a protocol by hand is among the pairs but the closed ones.
        foreach (var setting in RemoteGitCommands.Settings(spec))
        {
            Assert.DoesNotContain("insteadOf", setting, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pushOption", setting, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential.", setting, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_pairs_of_configuration_of_production_are_the_ones_of_D06_with_only_https_open()
    {
        var settings = RemoteGitCommandLine.Configuration(RemoteTransport.GitHubHttps, @"C:\hosting\no-hooks");

        Assert.Equal(
            [
                "-c", @"core.hooksPath=C:\hosting\no-hooks", "-c", "core.fsmonitor=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
                "-c", "core.sshCommand=", "-c", "core.gitProxy=", "-c", "core.askPass=",
                "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.file.allow=never", "-c", "protocol.git.allow=never", "-c", "protocol.ssh.allow=never",
                "-c", "protocol.http.allow=never", "-c", "protocol.ftp.allow=never", "-c", "protocol.ftps.allow=never", "-c", "protocol.ext.allow=never",
                "-c", "http.followRedirects=false", "-c", "submodule.recurse=false",
                "-c", "push.default=nothing", "-c", "push.followTags=false", "-c", "push.recurseSubmodules=no", "-c", "push.gpgSign=false", "-c", "push.autoSetupRemote=false",
            ],
            settings);
    }

    [Fact]
    public void The_helper_of_credentials_and_every_other_http_key_are_not_set_because_a_pair_beats_the_global_configuration_of_the_person()
    {
        var settings = RemoteGitCommandLine.Configuration(RemoteTransport.GitHubHttps, @"C:\h\no-hooks");

        Assert.DoesNotContain(settings, s => s.StartsWith("credential.", StringComparison.Ordinal));
        Assert.Equal(["http.followRedirects=false"], settings.Where(s => s.StartsWith("http.", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_transport_of_the_tests_opens_its_scheme_and_closes_https_and_the_scheme_is_written_once()
    {
        var local = RemoteTransport.ForTests("file", r => r.PushUrl);

        var settings = RemoteGitCommandLine.Configuration(local, @"C:\h\no-hooks");

        Assert.Equal("protocol.file.allow=always", Assert.Single(settings, s => s.StartsWith("protocol.file.", StringComparison.Ordinal)));
        Assert.Contains("protocol.https.allow=never", settings);
        Assert.Single(settings, s => s == "protocol.allow=never");
    }

    [Fact]
    public void A_scheme_that_is_not_in_the_list_is_added_as_open_and_everything_else_stays_closed()
    {
        var settings = RemoteGitCommandLine.Configuration(RemoteTransport.ForTests("scp", r => r.PushUrl), @"C:\h\no-hooks");

        Assert.Equal("protocol.scp.allow=always", Assert.Single(settings, s => s.StartsWith("protocol.scp.", StringComparison.Ordinal)));
        Assert.Equal(9, settings.Count(s => s.StartsWith("protocol.", StringComparison.Ordinal) && s.EndsWith("=never", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_command_lines_are_the_closed_list_with_the_address_and_the_refspec_behind_two_dashes()
    {
        var branch = RemoteBranch.ForRun(new RunId("run-1"));

        Assert.Equal(
            ["push", "--porcelain", "--no-verify", "--no-follow-tags", "--recurse-submodules=no", "--no-signed", "--", "https://github.com/o/r.git", RemoteGitCommandLineTests.Refspec],
            RemoteGitCommandLine.Push("https://github.com/o/r.git", Refspec));
        Assert.Equal(["ls-remote", "--refs", "--", "https://github.com/o/r.git", "refs/heads/agentstudio/run-run-1"], RemoteGitCommandLine.LsRemote("https://github.com/o/r.git", branch));
        Assert.Equal(["ls-remote", "--get-url", "--", "https://github.com/o/r.git"], RemoteGitCommandLine.GetUrl("https://github.com/o/r.git"));
        Assert.Equal(["config", "-z", "--show-scope", "--show-origin", "--get-regexp", "--end-of-options", RemoteConfigInspection.Pattern], RemoteGitCommandLine.ConfigQuery());
    }

    private static string Refspec => $"{RemoteGitFixture.Commit}:refs/heads/agentstudio/run-{RemoteGitFixture.RunIdValue}";

    [Fact]
    public void The_refspec_is_a_commit_and_the_branch_of_the_run_and_never_a_plus_a_colon_or_a_deletion()
    {
        Assert.Equal(Refspec, RemoteGitCommandLine.TryRefspec(RemoteGitFixture.Commit, new RunId(RemoteGitFixture.RunIdValue)));
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01:refs/heads/agentstudio/run-r_1-x", RemoteGitCommandLine.TryRefspec("abcdef0123456789abcdef0123456789abcdef01", new RunId("r_1-x")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("+1111111111111111111111111111111111111111")]
    [InlineData(":1111111111111111111111111111111111111111")]
    [InlineData("1111111111111111111111111111111111111111:refs/heads/main")]
    [InlineData("111111111111111111111111111111111111111")]
    [InlineData("11111111111111111111111111111111111111111")]
    [InlineData("1111111111111111111111111111111111111111 ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("1111111111111111111111111111111111111111\n")]
    public void A_commit_that_is_not_forty_lowercase_hexadecimal_characters_writes_no_refspec(string? commit) =>
        Assert.Null(RemoteGitCommandLine.TryRefspec(commit, new RunId(RemoteGitFixture.RunIdValue)));

    [Fact]
    public void A_run_without_an_identifier_writes_no_refspec()
    {
        Assert.Null(RemoteGitCommandLine.TryRefspec(RemoteGitFixture.Commit, default));
    }

    [Fact]
    public void The_timeouts_and_the_cap_are_the_ones_of_the_design()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), RemoteGitCommandLine.PushTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), RemoteGitCommandLine.QueryTimeout);
        Assert.Equal(64_000, RemoteGitCommandLine.MaxOutputCharacters);
    }

    [Fact]
    public void The_walk_of_D07_rejects_every_form_of_forcing_deleting_and_the_other_sub_commands()
    {
        // The walk has to fail on what it is written to find: it is run here over command lines that break one rule each, with the configuration and the environment right.
        static ProcessSpec Spec(params string[] command) => new(
            "git",
            [.. RemoteGitCommandLine.Configuration(RemoteTransport.GitHubHttps, "h"), .. command],
            null,
            RemoteGitEnvironment.Build(RemoteTransport.GitHubHttps, "t", new Dictionary<string, string>()),
            command[0] == "push" ? RemoteGitCommandLine.PushTimeout : RemoteGitCommandLine.QueryTimeout,
            MaxOutputCharacters: RemoteGitCommandLine.MaxOutputCharacters);

        var bad = new[]
        {
            new[] { "push", "--force", "--", "u", Refspec },
            new[] { "push", "-f", "--", "u", Refspec },
            new[] { "push", "--force-with-lease", "--", "u", Refspec },
            new[] { "push", "--mirror", "--", "u", Refspec },
            new[] { "push", "--delete", "--", "u", Refspec },
            new[] { "push", "--tags", "--", "u", Refspec },
            new[] { "push", "--all", "--", "u", Refspec },
            new[] { "push", "-o", "--", "u", Refspec },
            new[] { "push", "--porcelain", "--no-verify", "--", "u", "+" + Refspec },
            new[] { "push", "--porcelain", "--no-verify", "--", "u", ":refs/heads/agentstudio/run-x" },
            new[] { "push", "--porcelain", "--no-verify", "--", "u", "main" },
            new[] { "push", "--no-verify", "--", "u", Refspec },
            new[] { "push", "--porcelain", "--", "u", Refspec },
            new[] { "fetch", "--", "u" },
            new[] { "pull", "--", "u" },
            new[] { "push", "--porcelain", "--no-verify", "u", Refspec },
        };

        foreach (var arguments in bad)
        {
            Assert.ThrowsAny<Exception>(() => AssertClosed(Spec(arguments)));
        }

        // Without the pairs first, in their order, or with another environment, the walk fails too.
        var good = Spec("push", "--porcelain", "--no-verify", "--no-follow-tags", "--", "https://github.com/o/r.git", Refspec);
        AssertClosed(good);
        Assert.ThrowsAny<Exception>(() => AssertClosed(good with { Arguments = [.. good.Arguments.Skip(2)] }));
        Assert.ThrowsAny<Exception>(() => AssertClosed(good with { Arguments = ["-c", good.Arguments[3], "-c", good.Arguments[1], .. good.Arguments.Skip(4)] }));
        Assert.ThrowsAny<Exception>(() => AssertClosed(good with { Environment = new Dictionary<string, string?>(good.Environment!) { ["GIT_SSH_COMMAND"] = "evil" } }));
        Assert.ThrowsAny<Exception>(() => AssertClosed(good with { Environment = new Dictionary<string, string?>(good.Environment!) { ["GIT_TERMINAL_PROMPT"] = "1" } }));
        AssertClosed(Spec("grep", "-l", "-i", "-E", "--no-color", "-e", "^[^#]*filter=lfs", RemoteGitFixture.Commit, "--", ":(glob)**/.gitattributes"));
    }

    [Fact]
    public void The_command_that_reads_the_attributes_of_a_commit_is_a_local_grep_of_the_tree_and_takes_only_a_commit_or_head()
    {
        Assert.Equal(
            ["grep", "-l", "-i", "-E", "--no-color", "-e", "^[^#]*filter=lfs", RemoteGitFixture.Commit, "--", ":(glob)**/.gitattributes"],
            RemoteGitCommandLine.LfsAttributes(RemoteGitFixture.Commit));
        Assert.Equal("HEAD", RemoteGitCommandLine.LfsAttributes("HEAD")[7]);
        foreach (var other in new[] { "--force", "main", "-e", "HEAD~1", "refs/heads/x", string.Empty, "1111" })
        {
            Assert.Throws<ArgumentException>(() => RemoteGitCommandLine.LfsAttributes(other));
        }
    }
}