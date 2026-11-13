using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// The remote executor of git against a script (docs/21 F10-3 and F10-10, D-05 to D-08): the exact command lines, the environment, the order, the planted configurations (one test for each key of D-05) and the
/// failures, with no process started and no wait on a clock. The real git is in <see cref="RemoteGitRunnerLocalTests"/>.
/// </summary>
public sealed class RemoteGitRunnerScriptTests : IDisposable
{
    private const string Address = "https://github.com/o/r.git";
    private const string Secret = "ghp_S3cretT0ken";

    private readonly RemoteGitFixture _fixture = new();
    private readonly RemoteGitScript _script = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refspec => RemoteGitFixture.Refspec;

    public void Dispose() => _fixture.Dispose();

    private RemoteGitRunner Runner(Func<IReadOnlyDictionary<string, string>>? ambient = null) => _fixture.NewRunner(_script, ambient: ambient);

    private void ThePushWorks()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.Commit)));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]"));
    }

    private void Plant(string scope, string origin, string key, string value = "x") =>
        _script.ConfigOutput += RemoteGitCommands.ConfigEntry(scope, origin, key, value);

    private void AssertAllClosed()
    {
        Assert.NotEmpty(_script.Specs);
        foreach (var spec in _script.Specs)
        {
            RemoteGitCommandLineTests.AssertClosed(spec);
        }
    }

    // ---- the commands, in order, with their arguments and environment ----

    [Fact]
    public async Task A_push_launches_the_closed_list_in_order_with_the_exact_arguments_and_the_limits_of_the_design()
    {
        ThePushWorks();

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.Equal(RemoteGitFixture.Commit, result.RemoteCommit);
        Assert.True(result.Inspection.Accepted);
        Assert.Empty(result.Inspection.Refusals);
        Assert.False(result.Inspection.UsesLfs);
        Assert.Null(result.Inspection.FailureCode);
        Assert.Equal(["config", "get-url", "ls-remote", "push", "ls-remote"], _script.Kinds);

        var configuration = RemoteGitCommandLine.Configuration(RemoteTransport.GitHubHttps, _fixture.Folders.HooksFile);
        var push = _script.Specs[3];
        Assert.Equal(_fixture.GitPath, push.FileName);
        Assert.Equal(_fixture.Root, push.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(120), push.Timeout);
        Assert.Null(push.StandardInput);
        Assert.Equal(64_000, push.MaxOutputCharacters);
        Assert.Equal(ProcessIsolation.None, push.Isolation);
        Assert.Equal([.. configuration, "push", "--porcelain", "--no-verify", "--no-follow-tags", "--recurse-submodules=no", "--no-signed", "--", Address, Refspec], push.Arguments);

        foreach (var spec in _script.Specs.Where(s => RemoteGitCommands.Kind(s) != "push"))
        {
            Assert.Equal(TimeSpan.FromSeconds(20), spec.Timeout);
            Assert.Equal(_fixture.Root, spec.WorkingDirectory);
            Assert.Null(spec.StandardInput);
            Assert.Equal(configuration, RemoteGitCommands.Settings(spec).SelectMany(s => new[] { "-c", s }));
        }

        Assert.Equal(["config", "-z", "--show-scope", "--show-origin", "--get-regexp", "--end-of-options", RemoteConfigInspection.Pattern], RemoteGitCommands.CommandOf(_script.Specs[0]));
        Assert.Equal(["ls-remote", "--get-url", "--", Address], RemoteGitCommands.CommandOf(_script.Specs[1]));
        Assert.Equal(["ls-remote", "--refs", "--", Address, "refs/heads/agentstudio/run-" + RemoteGitFixture.RunIdValue], RemoteGitCommands.CommandOf(_script.Specs[2]));
        Assert.Equal(RemoteGitCommands.CommandOf(_script.Specs[2]), RemoteGitCommands.CommandOf(_script.Specs[4]));
        AssertAllClosed();
    }

    [Fact]
    public async Task The_hooks_file_is_an_empty_file_outside_the_repository_and_the_temporary_folder_exists()
    {
        ThePushWorks();

        await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(0, new FileInfo(_fixture.Folders.HooksFile).Length);
        Assert.True(Directory.Exists(_fixture.Folders.TempFolder));
        Assert.False(_fixture.Folders.HooksFile.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("core.hooksPath=" + _fixture.Folders.HooksFile, RemoteGitCommands.Settings(_script.Specs[3]));
    }

    [Fact]
    public async Task The_environment_removes_what_is_inherited_and_sets_what_the_executor_needs()
    {
        ThePushWorks();
        var ambient = new Dictionary<string, string>
        {
            ["GIT_SSH_COMMAND"] = "evil", ["GIT_ASKPASS"] = "evil", ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "credential.helper", ["GIT_CONFIG_VALUE_0"] = "!evil",
            ["GIT_TRACE"] = "1", ["GIT_CURL_VERBOSE"] = "1", ["GH_TOKEN"] = Secret, ["GITHUB_TOKEN"] = Secret, ["GCM_TRACE"] = "1", ["HTTPS_PROXY"] = "http://evil",
            ["GH_HOST"] = "evil", ["GH_REPO"] = "evil", ["GH_CONFIG_DIR"] = "evil", ["GH_DEBUG"] = "api", ["USERPROFILE"] = @"C:\Users\ana", ["PATH"] = ".\\planted",
        };

        await Runner(() => ambient).PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(5, _script.Specs.Count);
        foreach (var spec in _script.Specs)
        {
            var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(spec.Environment);
            foreach (var name in new[] { "GIT_SSH_COMMAND", "GIT_ASKPASS", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_TRACE", "GIT_CURL_VERBOSE", "GH_TOKEN", "GITHUB_TOKEN", "GCM_TRACE", "HTTPS_PROXY", "GH_HOST", "GH_REPO", "GH_CONFIG_DIR", "GH_DEBUG" })
            {
                Assert.True(environment.ContainsKey(name), name);
                Assert.Null(environment[name]);
            }

            Assert.Equal(@"C:\Users\ana", environment["USERPROFILE"]);
            Assert.False(environment.ContainsKey("PATH"));
            Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
            Assert.Equal("cat", environment["GIT_PAGER"]);
            Assert.Equal("1", environment["GIT_NO_LAZY_FETCH"]);
            Assert.Equal("https", environment["GIT_ALLOW_PROTOCOL"]);
            Assert.Equal("never", environment["GCM_INTERACTIVE"]);
            Assert.Equal(_fixture.Folders.TempFolder, environment["TEMP"]);
            Assert.Equal(_fixture.Folders.TempFolder, environment["TMP"]);
        }
    }

    [Fact]
    public async Task A_remote_that_already_has_the_branch_at_the_commit_is_done_and_nothing_is_pushed()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.Commit)));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.AlreadyUpToDate, result.Outcome);
        Assert.Equal(RemoteGitFixture.Commit, result.RemoteCommit);
        Assert.Equal(["config", "get-url", "ls-remote"], _script.Kinds);
    }

    [Fact]
    public async Task A_push_that_git_says_was_up_to_date_is_confirmed_and_counts_as_done()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.Commit)));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("=", Refspec, "[up to date]"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.AlreadyUpToDate, result.Outcome);
    }

    [Fact]
    public async Task A_fast_forward_is_pushed()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.OtherCommit)));
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.Commit)));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput(" ", Refspec, "2222222..1111111"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.Equal(RemoteGitFixture.Commit, result.RemoteCommit);
    }

    // ---- «no confirmado», after the push ----

    [Fact]
    public async Task A_push_whose_commit_is_not_on_the_remote_afterwards_is_not_confirmed_and_never_published()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.NotConfirmed, result.Outcome);
        Assert.Null(result.RemoteCommit);
    }

    [Fact]
    public async Task A_remote_that_answers_another_commit_afterwards_is_not_confirmed_and_says_which_one_it_has()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.OtherCommit)));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.NotConfirmed, result.Outcome);
        Assert.Equal(RemoteGitFixture.OtherCommit, result.RemoteCommit);
    }

    [Fact]
    public async Task A_confirmation_that_fails_is_not_confirmed()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Failure(128, "fatal: unable to access: Could not resolve host"));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.NotConfirmed, result.Outcome);
        Assert.Null(result.RemoteCommit);
    }

    [Fact]
    public async Task A_push_that_timed_out_is_not_confirmed_and_nothing_else_is_launched()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = RemoteGitScript.TimedOut();

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.NotConfirmed, result.Outcome);
        Assert.Equal(["config", "get-url", "ls-remote", "push"], _script.Kinds);
    }

    [Fact]
    public async Task A_push_that_wrote_more_than_the_cap_failed()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = RemoteGitScript.TooLarge();

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
    }

    // ---- «la rama del remoto cambió; no se sobrescribe» ----

    [Theory]
    [InlineData("[rejected] (non-fast-forward)")]
    [InlineData("[rejected] (fetch first)")]
    public async Task A_push_that_is_not_a_fast_forward_is_a_result_and_is_never_retried_with_force(string summary)
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.OtherCommit)));
        _script.PushResult = new ProcessResult(1, RemoteGitScript.PorcelainOutput("!", Refspec, summary), "error: failed to push some refs to '" + Address + "'", TimeSpan.Zero, false);

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.RejectedNotFastForward, result.Outcome);
        Assert.Null(result.RemoteCommit);
        Assert.Equal(1, _script.Kinds.Count(kind => kind == "push"));
        AssertAllClosed();
    }

    [Theory]
    [InlineData("!", "[remote rejected] (pre-receive hook declined)")]
    [InlineData("!", "[rejected] (stale info)")]
    [InlineData("+", "1111111...2222222 (forced update)")]
    [InlineData("-", "[deleted]")]
    public async Task Any_other_line_of_a_rejection_is_a_failure_and_not_a_non_fast_forward(string flag, string summary)
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = new ProcessResult(1, RemoteGitScript.PorcelainOutput(flag, Refspec, summary), string.Empty, TimeSpan.Zero, false);

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_line_for_another_refspec_is_not_the_answer_to_this_push()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = RemoteGitScript.Ok(RemoteGitScript.PorcelainOutput("*", RemoteGitFixture.OtherCommit + ":refs/heads/agentstudio/run-" + RemoteGitFixture.RunIdValue, "[new branch]"));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.NotConfirmed, result.Outcome);
    }

    // ---- failures are codes ----

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", RemotePushOutcome.CredentialsNeeded)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/o/r.git/'", RemotePushOutcome.CredentialsNeeded)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403", RemotePushOutcome.CredentialsNeeded)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/o/r.git/' not found", RemotePushOutcome.CredentialsNeeded)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", RemotePushOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Failed to connect to github.com port 443 after 21050 ms", RemotePushOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': SSL certificate problem", RemotePushOutcome.Unreachable)]
    [InlineData("fatal: unable to update url base from redirection:\n  asked for: https://github.com/o/r.git/info/refs\n  redirect: https://github.com/n/r.git", RemotePushOutcome.Redirected)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 301", RemotePushOutcome.Redirected)]
    [InlineData("fatal: transport 'file' not allowed", RemotePushOutcome.Failed)]
    [InlineData("something nobody has ever seen", RemotePushOutcome.Failed)]
    public async Task A_push_that_failed_without_a_line_is_classified_by_its_error_output(string error, RemotePushOutcome expected)
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = RemoteGitScript.Failure(128, error);

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.RemoteCommit);
    }

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", RemotePushOutcome.CredentialsNeeded)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", RemotePushOutcome.Unreachable)]
    [InlineData("fatal: unable to update url base from redirection", RemotePushOutcome.Redirected)]
    [InlineData("?", RemotePushOutcome.Failed)]
    public async Task A_remote_that_cannot_be_read_before_the_push_stops_there_and_nothing_is_pushed(string error, RemotePushOutcome expected)
    {
        _script.LsRemote.Add(RemoteGitScript.Failure(128, error));

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(["config", "get-url", "ls-remote"], _script.Kinds);
    }

    [Fact]
    public async Task A_remote_that_does_not_answer_in_time_is_unreachable()
    {
        _script.LsRemote.Add(RemoteGitScript.TimedOut());

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Unreachable, result.Outcome);
        Assert.DoesNotContain("push", _script.Kinds);
    }

    [Fact]
    public async Task What_the_process_wrote_never_reaches_a_result()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));
        _script.PushResult = new ProcessResult(128, "token " + Secret, "fatal: " + Secret + " https://user:" + Secret + "@github.com", TimeSpan.Zero, false);
        _script.ConfigOutput = RemoteGitCommands.ConfigEntry("global", "file:C:/Users/ana/.gitconfig", "http.proxy", "http://u:" + Secret + "@p");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);
        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(RemoteGitFixture.Run), Ct);

        Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Inspection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, read.ToString(), StringComparison.Ordinal);
    }

    // ---- a commit and a run that cannot be written as a refspec ----

    [Theory]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("+1111111111111111111111111111111111111111")]
    [InlineData(":1111111111111111111111111111111111111111")]
    [InlineData("HEAD")]
    [InlineData("1111111111111111111111111111111111111111:refs/heads/main")]
    [InlineData("1111111")]
    public async Task A_commit_that_is_not_forty_hexadecimal_characters_launches_nothing(string commit)
    {
        var result = await Runner().PushRunBranchAsync(_fixture.Push(commit), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
        Assert.Equal(RemoteGitCodes.InvalidCommit, result.Inspection.FailureCode);
        Assert.Empty(_script.Specs);
    }

    [Fact]
    public async Task A_run_without_an_identifier_launches_nothing()
    {
        var result = await Runner().PushRunBranchAsync(new RemotePush(_fixture.Project, RemoteGitFixture.Repository, default, RemoteGitFixture.Commit), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
        Assert.Empty(_script.Specs);
    }

    // ---- D-05: the planted configurations, one test for each key ----

    [Theory]
    [MemberData(nameof(RemoteConfigInspectionTests.RefusedKeys), MemberType = typeof(RemoteConfigInspectionTests))]
    public async Task A_key_that_a_pair_cannot_override_stops_the_publication_before_any_transport_is_opened(string key, string shown)
    {
        Plant("local", "file:.git/config", key, "!" + Secret);

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.False(result.Inspection.Accepted);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal(shown, refusal.Key);
        Assert.Equal("local", refusal.Scope);
        Assert.Equal(".git/config", refusal.OriginFile);
        Assert.Equal(["config"], _script.Kinds);
        Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RemoteConfigInspectionTests.RefusedKeys), MemberType = typeof(RemoteConfigInspectionTests))]
    public async Task A_global_include_that_points_inside_the_repository_is_judged_by_its_file(string key, string shown)
    {
        Plant("global", "file:" + Path.Combine(_fixture.Root, "docs", "extra.cfg"), key);

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal(shown, refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.Equal(Path.Combine(_fixture.Root, "docs", "extra.cfg"), refusal.OriginFile);
        Assert.Equal(["config"], _script.Kinds);
    }

    [Fact]
    public async Task A_refusal_lists_every_key_with_its_own_scope_and_file()
    {
        Plant("local", "file:.git/config", "credential.helper");
        Plant("worktree", "file:.git/config.worktree", "http.proxy");
        Plant("global", "file:" + Path.Combine(_fixture.WorktreeRoot, "x", "inc.cfg"), "push.pushoption");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(
            [("credential.helper", "local"), ("http.proxy", "worktree"), ("push.pushoption", "global")],
            result.Inspection.Refusals.Select(r => (r.Key, r.Scope)));
    }

    [Theory]
    [MemberData(nameof(RemoteConfigInspectionTests.OverriddenKeys), MemberType = typeof(RemoteConfigInspectionTests))]
    public async Task A_key_that_a_pair_overrides_does_not_stop_the_publication_and_the_pair_is_on_the_command_line(string key)
    {
        ThePushWorks();
        Plant("local", "file:.git/config", key, "evil");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        var settings = RemoteGitCommands.Settings(_script.Specs[3]);
        var overridden = key switch
        {
            "core.hookspath" => "core.hooksPath=" + _fixture.Folders.HooksFile,
            "core.fsmonitor" => "core.fsmonitor=false",
            "core.sshcommand" => "core.sshCommand=",
            "core.gitproxy" => "core.gitProxy=",
            "core.askpass" => "core.askPass=",
            "protocol.https.allow" => "protocol.https.allow=always",
            "protocol.file.allow" => "protocol.file.allow=never",
            "protocol.allow" => "protocol.allow=never",
            "http.followredirects" => "http.followRedirects=false",
            "push.default" => "push.default=nothing",
            "push.followtags" => "push.followTags=false",
            "push.recursesubmodules" => "push.recurseSubmodules=no",
            "push.gpgsign" => "push.gpgSign=false",
            "push.autosetupremote" => "push.autoSetupRemote=false",
            "submodule.recurse" => "submodule.recurse=false",
            _ => throw new InvalidOperationException(key),
        };
        Assert.Contains(overridden, settings);
        Assert.Contains("--no-verify", _script.Specs[3].Arguments);
        Assert.Contains("--no-follow-tags", _script.Specs[3].Arguments);
        AssertAllClosed();
    }

    [Fact]
    public async Task The_ordinary_remote_of_a_repository_does_not_stop_the_publication_and_is_not_used()
    {
        ThePushWorks();
        Plant("local", "file:.git/config", "remote.origin.url", "https://github.com/someone/else.git");
        Plant("local", "file:.git/config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        Plant("local", "file:.git/config", "branch.main.remote", "origin");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.All(_script.Specs, spec => Assert.DoesNotContain(spec.Arguments, a => a.Contains("someone", StringComparison.Ordinal) || a == "origin"));
        Assert.Contains(Address, _script.Specs[3].Arguments);
    }

    [Fact]
    public async Task A_global_insteadof_that_rewrites_the_address_is_refused_by_what_git_says_and_is_named()
    {
        Plant("global", "file:C:/Users/ana/.gitconfig", "url.https://mirror.example/.insteadof", "https://github.com/");
        _script.GetUrlOutput = "https://mirror.example/o/r.git";

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.Equal(new RemoteConfigRefusal("url.*.insteadof", "global", "C:/Users/ana/.gitconfig"), Assert.Single(result.Inspection.Refusals));
        Assert.Equal(["config", "get-url"], _script.Kinds);
    }

    [Fact]
    public async Task A_rewrite_that_git_reports_without_an_entry_to_name_is_refused_with_a_generic_key()
    {
        _script.GetUrlOutput = "https://elsewhere.example/o/r.git";

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.Equal("url.*.insteadof", Assert.Single(result.Inspection.Refusals).Key);
    }

    [Fact]
    public async Task A_global_pushinsteadof_that_rewrites_the_address_is_refused_by_prefix()
    {
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "url.https://mirror.example/.pushinsteadof", "https://github.com/");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.Equal("url.*.pushinsteadof", Assert.Single(result.Inspection.Refusals).Key);
        Assert.Equal(["config"], _script.Kinds);
    }

    [Fact]
    public async Task The_configuration_of_the_person_that_does_not_touch_the_address_is_accepted()
    {
        ThePushWorks();
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "credential.helper", "manager");
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "http.sslcainfo", "C:/Program Files/Git/mingw64/etc/ssl/certs/ca-bundle.crt");
        Plant("global", "file:C:/Users/ana/.gitconfig", "http.proxy", "http://proxy.corp:8080");
        Plant("global", "file:C:/Users/ana/.gitconfig", "url.https://mirror.example/.insteadof", "https://gitlab.com/");
        Plant("global", "file:C:/Users/ana/.gitconfig", "url.https://mirror.example/.pushinsteadof", "https://gitlab.com/");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(RemoteConfigInspectionTests.RefusedKeys), MemberType = typeof(RemoteConfigInspectionTests))]
    public async Task Reading_a_branch_is_refused_in_the_same_way_before_ls_remote(string key, string shown)
    {
        _ = shown;
        Plant("local", "file:.git/config", key);

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(RemoteGitFixture.Run), Ct);

        Assert.False(read.Reached);
        Assert.Null(read.Commit);
        Assert.Equal(RemoteGitCodes.ConfigurationRefused, read.FailureCode);
        Assert.Equal(["config"], _script.Kinds);
    }

    [Fact]
    public async Task Inspecting_gives_the_refusals_without_launching_a_transport()
    {
        Plant("local", "file:.git/config", "credential.helper");

        var inspection = await Runner().InspectAsync(_fixture.Project, RemoteGitFixture.Repository, Ct);

        Assert.False(inspection.Accepted);
        Assert.Equal("credential.helper", Assert.Single(inspection.Refusals).Key);
        Assert.Equal(["config"], _script.Kinds);
    }

    // ---- an inspection that fails does not publish ----

    public static TheoryData<string, string> InspectionFailures
    {
        get
        {
            var data = new TheoryData<string, string>
            {
                { "config-exit", RemoteGitCodes.InspectionFailed },
                { "config-timeout", RemoteGitCodes.TimedOut },
                { "config-large", RemoteGitCodes.InspectionOutputTooLarge },
                { "url-exit", RemoteGitCodes.InspectionFailed },
                { "url-timeout", RemoteGitCodes.TimedOut },
                { "url-large", RemoteGitCodes.InspectionOutputTooLarge },
            };
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InspectionFailures))]
    public async Task An_inspection_that_fails_gives_a_code_and_nothing_is_pushed_or_read(string failure, string code)
    {
        switch (failure)
        {
            case "config-exit":
                _script.ConfigResult = RemoteGitScript.Failure(128, "fatal: bad config line 3 in file .git/config");
                break;
            case "config-timeout":
                _script.ConfigResult = RemoteGitScript.TimedOut();
                break;
            case "config-large":
                _script.ConfigResult = RemoteGitScript.TooLarge();
                break;
            case "url-exit":
                _script.GetUrlResult = RemoteGitScript.Failure(128, "fatal: x");
                break;
            case "url-timeout":
                _script.GetUrlResult = RemoteGitScript.TimedOut();
                break;
            default:
                _script.GetUrlResult = RemoteGitScript.TooLarge();
                break;
        }

        var push = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);
        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(RemoteGitFixture.Run), Ct);
        var inspection = await Runner().InspectAsync(_fixture.Project, RemoteGitFixture.Repository, Ct);

        Assert.Equal(RemotePushOutcome.Failed, push.Outcome);
        Assert.Equal(code, push.Inspection.FailureCode);
        Assert.False(push.Inspection.Accepted);
        Assert.False(read.Reached);
        Assert.Equal(code, read.FailureCode);
        Assert.Equal(code, inspection.FailureCode);
        Assert.DoesNotContain("push", _script.Kinds);
        Assert.DoesNotContain("ls-remote", _script.Kinds);
    }

    [Fact]
    public async Task No_key_matching_is_an_answer_and_not_a_failure()
    {
        ThePushWorks();
        _script.ConfigOutput = string.Empty;

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
    }

    // ---- git.exe ----

    [Fact]
    public async Task Without_a_git_outside_the_projects_nothing_is_launched()
    {
        var cmd = Path.Combine(Path.GetDirectoryName(_fixture.GitPath)!, "git.cmd");
        File.WriteAllBytes(cmd, []);
        var inside = Path.Combine(_fixture.Root, "tools", "git.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, []);
        var locators = new IExecutableLocator[]
        {
            new FixedLocator(null),
            new FixedLocator(cmd),
            new FixedLocator("git.exe"),
            new FixedLocator(Path.Combine(_fixture.Root, "missing", "git.exe")),
            new FixedLocator(_fixture.GitPath, untrusted: true),
            new FixedLocator(inside),
        };

        foreach (var locator in locators)
        {
            var push = await _fixture.NewRunner(_script, locator: locator).PushRunBranchAsync(_fixture.Push(), Ct);
            var read = await _fixture.NewRunner(_script, locator: locator).ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(RemoteGitFixture.Run), Ct);

            Assert.Equal(RemotePushOutcome.Failed, push.Outcome);
            Assert.Equal(RemoteGitCodes.GitUnavailable, push.Inspection.FailureCode);
            Assert.Equal(RemoteGitCodes.GitUnavailable, read.FailureCode);
        }

        Assert.Empty(_script.Specs);
    }

    [Fact]
    public async Task The_git_is_asked_for_again_on_every_use()
    {
        ThePushWorks();
        var locator = new SwitchingLocator(_fixture.GitPath);
        var runner = _fixture.NewRunner(_script, locator: locator);
        await runner.PushRunBranchAsync(_fixture.Push(), Ct);
        var launched = _script.Specs.Count;
        locator.Untrusted = true;

        var after = await runner.PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemoteGitCodes.GitUnavailable, after.Inspection.FailureCode);
        Assert.Equal(launched, _script.Specs.Count);
    }

    private sealed class SwitchingLocator(string path) : IExecutableLocator
    {
        public bool Untrusted { get; set; }

        public string? Find(string command) => path;

        public bool IsUntrusted(string candidate) => Untrusted;
    }

    // ---- the hooks file ----

    [Fact]
    public async Task A_hooks_file_that_is_a_folder_or_has_content_is_refused_and_nothing_is_emptied()
    {
        Directory.CreateDirectory(_fixture.Folders.HooksFile);

        var asFolder = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemoteGitCodes.UnsafeHooksFile, asFolder.Inspection.FailureCode);
        Assert.True(Directory.Exists(_fixture.Folders.HooksFile));
        Directory.Delete(_fixture.Folders.HooksFile);

        File.WriteAllText(_fixture.Folders.HooksFile, "#!/bin/sh\n");
        var withContent = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemoteGitCodes.UnsafeHooksFile, withContent.Inspection.FailureCode);
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(_fixture.Folders.HooksFile));
        Assert.Empty(_script.Specs);
    }

    // ---- the project ----

    [Fact]
    public async Task A_project_that_is_not_a_folder_of_a_repository_launches_nothing()
    {
        var plain = Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "plain");
        Directory.CreateDirectory(plain);
        var projects = new[] { new ProjectContext("p", plain), new ProjectContext("p", Path.Combine(plain, "missing")), new ProjectContext("p", "relative"), new ProjectContext("p", string.Empty) };

        foreach (var project in projects)
        {
            var inspection = await Runner().InspectAsync(project, RemoteGitFixture.Repository, Ct);

            Assert.False(inspection.Accepted);
            Assert.Equal(RemoteGitCodes.ProjectUnreadable, inspection.FailureCode);
        }

        Assert.Empty(_script.Specs);
    }

    // ---- LFS ----

    [Fact]
    public async Task A_repository_that_says_filter_lfs_in_its_attributes_with_the_filter_configured_is_not_published()
    {
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "filter.lfs.clean", "git-lfs clean -- %f");
        File.WriteAllText(Path.Combine(_fixture.Root, ".gitattributes"), "*.psd filter=lfs diff=lfs merge=lfs -text\n");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.LfsRefused, result.Outcome);
        Assert.True(result.Inspection.UsesLfs);
        Assert.False(result.Inspection.Accepted);
        Assert.Empty(result.Inspection.Refusals);
        Assert.Equal(["config", "get-url"], _script.Kinds);
    }

    [Theory]
    [InlineData("sub/dir/.gitattributes")]
    [InlineData(".git/info/attributes")]
    public async Task The_attributes_of_a_folder_and_of_the_repository_count_too(string relative)
    {
        Plant("global", "file:C:/Users/ana/.gitconfig", "filter.lfs.smudge", "git-lfs smudge -- %f");
        var file = Path.Combine(_fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "# comment\n*.bin filter=lfs -text\n");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.LfsRefused, result.Outcome);
    }

    [Fact]
    public async Task The_filter_of_git_lfs_in_the_configuration_of_the_system_alone_is_not_lfs()
    {
        ThePushWorks();
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "filter.lfs.clean", "git-lfs clean -- %f");
        File.WriteAllText(Path.Combine(_fixture.Root, ".gitattributes"), "*.cs text eol=crlf\n# *.bin filter=lfs\n");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.False(result.Inspection.UsesLfs);
    }

    [Fact]
    public async Task Attributes_that_say_filter_lfs_without_the_filter_configured_are_not_lfs()
    {
        ThePushWorks();
        File.WriteAllText(Path.Combine(_fixture.Root, ".gitattributes"), "*.psd filter=lfs\n");

        var result = await Runner().PushRunBranchAsync(_fixture.Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
    }

    [Fact]
    public async Task Reading_a_branch_of_a_repository_that_uses_lfs_is_allowed_because_nothing_is_sent()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitScript.BranchLine(RemoteGitFixture.Commit)));
        Plant("system", "file:C:/Program Files/Git/etc/gitconfig", "filter.lfs.clean", "git-lfs clean -- %f");
        File.WriteAllText(Path.Combine(_fixture.Root, ".gitattributes"), "*.psd filter=lfs\n");

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(RemoteGitFixture.Run), Ct);

        Assert.True(read.Reached);
        Assert.Equal(RemoteGitFixture.Commit, read.Commit);
    }

    // ---- ReadBranchAsync ----

    [Fact]
    public async Task Reading_a_branch_takes_the_line_of_exactly_that_ref_and_ignores_the_rest()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(
            RemoteGitFixture.OtherCommit + "\trefs/heads/other/main\n"
            + RemoteGitFixture.OtherCommit + "\trefs/heads/main-2\n"
            + "not a line\n"
            + "ABCDEF0123456789ABCDEF0123456789ABCDEF01\trefs/heads/main\n"
            + RemoteGitFixture.Commit + "\trefs/heads/main\n"));

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);

        Assert.True(read.Reached);
        Assert.Equal(RemoteGitFixture.Commit, read.Commit);
        Assert.Null(read.FailureCode);
        Assert.Equal(["ls-remote", "--refs", "--", Address, "refs/heads/main"], RemoteGitCommands.CommandOf(_script.Specs[^1]));
        Assert.Equal(TimeSpan.FromSeconds(20), _script.Specs[^1].Timeout);
        AssertAllClosed();
    }

    [Fact]
    public async Task Two_different_commits_for_the_same_ref_are_not_an_answer()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(RemoteGitFixture.OtherCommit + "\trefs/heads/main\n" + RemoteGitFixture.Commit + "\trefs/heads/main\n"));

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);

        Assert.True(read.Reached);
        Assert.Null(read.Commit);
    }

    [Fact]
    public async Task A_branch_that_does_not_exist_is_reached_with_no_commit()
    {
        _script.LsRemote.Add(RemoteGitScript.Ok(string.Empty));

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("feature/x")!, Ct);

        Assert.True(read.Reached);
        Assert.Null(read.Commit);
        Assert.Null(read.FailureCode);
    }

    [Theory]
    [InlineData(128, "fatal: could not read Username for 'https://github.com': terminal prompts disabled", RemoteGitCodes.CredentialsNeeded)]
    [InlineData(128, "fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", RemoteGitCodes.RemoteUnreachable)]
    [InlineData(128, "fatal: unable to update url base from redirection", RemoteGitCodes.Redirected)]
    [InlineData(128, "fatal: transport 'file' not allowed", RemoteGitCodes.ProtocolRefused)]
    [InlineData(1, "who knows", RemoteGitCodes.GitFailed)]
    public async Task A_branch_that_cannot_be_read_gives_a_code_and_never_the_text(int exit, string error, string code)
    {
        _script.LsRemote.Add(RemoteGitScript.Failure(exit, error));

        var read = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);

        Assert.False(read.Reached);
        Assert.Null(read.Commit);
        Assert.Equal(code, read.FailureCode);
    }

    [Fact]
    public async Task A_read_that_timed_out_or_wrote_too_much_is_not_reached()
    {
        _script.LsRemote.Add(RemoteGitScript.TimedOut());
        var timedOut = await Runner().ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);
        var script = new RemoteGitScript();
        script.LsRemote.Add(RemoteGitScript.TooLarge());
        var large = await _fixture.NewRunner(script).ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);

        Assert.Equal(RemoteGitCodes.TimedOut, timedOut.FailureCode);
        Assert.Equal(RemoteGitCodes.OutputTooLarge, large.FailureCode);
    }

    // ---- D-07 over the whole battery ----

    [Fact]
    public async Task No_command_of_the_whole_battery_forces_deletes_mirrors_or_fetches()
    {
        // Every way a push can go, in one script: new, up to date, fast-forward, rejected, refused, not confirmed, and reads.
        foreach (var (lsRemote, push) in new[]
        {
            (new[] { string.Empty, RemoteGitScript.BranchLine(RemoteGitFixture.Commit) }, RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]")),
            (new[] { RemoteGitScript.BranchLine(RemoteGitFixture.Commit) }, string.Empty),
            (new[] { RemoteGitScript.BranchLine(RemoteGitFixture.OtherCommit), RemoteGitScript.BranchLine(RemoteGitFixture.Commit) }, RemoteGitScript.PorcelainOutput(" ", Refspec, "a..b")),
            (new[] { RemoteGitScript.BranchLine(RemoteGitFixture.OtherCommit) }, RemoteGitScript.PorcelainOutput("!", Refspec, "[rejected] (non-fast-forward)")),
            (new[] { string.Empty, string.Empty }, RemoteGitScript.PorcelainOutput("*", Refspec, "[new branch]")),
        })
        {
            var script = new RemoteGitScript { PushResult = RemoteGitScript.Ok(push) };
            script.LsRemote.AddRange(lsRemote.Select(RemoteGitScript.Ok));
            var runner = _fixture.NewRunner(script);
            await runner.PushRunBranchAsync(_fixture.Push(), Ct);
            await runner.ReadBranchAsync(_fixture.Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);
            await runner.InspectAsync(_fixture.Project, RemoteGitFixture.Repository, Ct);

            Assert.NotEmpty(script.Specs);
            foreach (var spec in script.Specs)
            {
                RemoteGitCommandLineTests.AssertClosed(spec);
            }
        }
    }
}
