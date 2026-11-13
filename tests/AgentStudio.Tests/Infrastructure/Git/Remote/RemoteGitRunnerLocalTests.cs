using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// The remote executor of git with the real git.exe against a bare folder (docs/21 F10-3, ADR-0064): the transport is the one of the tests (<c>file</c>), the repositories are temporary and nothing touches the network. What a
/// <c>file</c> remote does not exercise (the credential manager, HTTPS and its redirects) waits for the live test (decision 14). The «global» configuration is a temporary <c>HOME</c> that the executor is told about through its
/// snapshot of the environment, so nothing of the person's own is read.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class RemoteGitRunnerLocalTests : IDisposable
{
    private static readonly RunId Run = new(RemoteGitFixture.RunIdValue);

    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as remoto real " + Guid.NewGuid().ToString("N")[..10]);
    private readonly RecordingProcessRunner _process = new(new ProcessRunner());

    public RemoteGitRunnerLocalTests()
    {
        Directory.CreateDirectory(Home);
        TempGitRepo.Git(_container, "init", "-q", "--bare", Bare);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Bare => Path.Combine(_container, "remote.git");

    private string Home => Path.Combine(_container, "home");

    /// <summary>The address of the bare folder as the transport writes it: with forward slashes, so a rewrite of it is easy to write in a configuration.</summary>
    private string BareAddress => Bare.Replace('\\', '/');

    private string ContainerAddress => _container.Replace('\\', '/');

    private ProjectContext Project => new("0f8fad5b-d9cb-469f-a165-70867728950e", _repo.Root);

    private string RunBranch => "agentstudio/run-" + RemoteGitFixture.RunIdValue;

    public void Dispose()
    {
        _repo.Dispose();
        TempFolders.Delete(_container);
    }

    private IReadOnlyDictionary<string, string> Ambient()
    {
        var variables = new Dictionary<string, string>(DoctorEnvironment.CurrentProcess(), StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
        };
        variables.Remove("HOMEDRIVE");
        variables.Remove("HOMEPATH");
        return variables;
    }

    private RemoteGitRunner NewRunner(string? address = null) =>
        new(
            _process,
            new ExecutableLocator(),
            RemoteGitFolders.Under(Path.Combine(_container, "local")),
            _repo.WorktreeRoot,
            RemoteTransport.ForTests("file", _ => address ?? BareAddress),
            Ambient);

    private RemotePush Push(string? commit = null) => new(Project, RemoteGitFixture.Repository, Run, commit ?? _repo.Head);

    private string[] RemoteRefs() =>
        TempGitRepo.Git(Bare, "for-each-ref", "--format=%(refname) %(objectname)").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private IEnumerable<string> Launched => _process.Specs.Select(RecordingProcessRunner.SubCommand);

    private void WriteGlobal(string text) => File.WriteAllText(Path.Combine(Home, ".gitconfig"), text);

    private string Sentinel(string name) => Path.Combine(_container, name + ".txt");

    private static string ShellWriting(string file) => "#!/bin/sh\necho ran > \"" + file.Replace('\\', '/') + "\"\n";

    private void AssertNothingForbiddenWasLaunched()
    {
        Assert.NotEmpty(_process.Specs);
        foreach (var spec in _process.Specs)
        {
            RemoteGitCommandLineTests.AssertClosed(spec);
        }
    }

    // ---- publishing ----

    [Fact]
    public async Task It_publishes_the_commit_to_the_branch_of_the_run_and_ls_remote_confirms_it_and_a_second_time_it_is_already_up_to_date()
    {
        var commit = _repo.Head;
        var runner = NewRunner();

        var first = await runner.PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, first.Outcome);
        Assert.Equal(commit, first.RemoteCommit);
        Assert.True(first.Inspection.Accepted);
        Assert.Equal([$"refs/heads/{RunBranch} {commit}"], RemoteRefs());
        // «grep» is there when the filter of git-lfs is configured (the system configuration of Git for Windows does it): the commit that goes up is asked about its attributes.
        Assert.Equal(["config", "ls-remote", "ls-remote", "push", "ls-remote"], Launched.Where(c => c != "grep"));

        var pushesBefore = Launched.Count(c => c == "push");
        var second = await runner.PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.AlreadyUpToDate, second.Outcome);
        Assert.Equal(commit, second.RemoteCommit);
        Assert.Equal(pushesBefore, Launched.Count(c => c == "push"));
        var read = await runner.ReadBranchAsync(Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(Run), Ct);
        Assert.True(read.Reached);
        Assert.Equal(commit, read.Commit);
        AssertNothingForbiddenWasLaunched();
    }

    [Fact]
    public async Task A_remote_that_moved_elsewhere_is_rejected_and_never_overwritten()
    {
        var mine = _repo.Head;
        TempGitRepo.Git(_repo.Root, "checkout", "-q", "-b", "elsewhere");
        _repo.Write("otro.txt", "otro\n");
        TempGitRepo.Git(_repo.Root, "add", "-A");
        TempGitRepo.Git(_repo.Root, "commit", "-q", "-m", "otro");
        var theirs = _repo.Head;
        TempGitRepo.Git(_repo.Root, "push", "-q", Bare, $"{theirs}:refs/heads/{RunBranch}");
        TempGitRepo.Git(_repo.Root, "checkout", "-q", "main");

        var result = await NewRunner().PushRunBranchAsync(Push(mine), Ct);

        Assert.Equal(RemotePushOutcome.RejectedNotFastForward, result.Outcome);
        Assert.Null(result.RemoteCommit);
        Assert.Equal([$"refs/heads/{RunBranch} {theirs}"], RemoteRefs());
        Assert.Equal(1, Launched.Count(c => c == "push"));
        AssertNothingForbiddenWasLaunched();
    }

    [Fact]
    public async Task A_fast_forward_of_the_branch_of_the_run_is_pushed()
    {
        var first = _repo.Head;
        await NewRunner().PushRunBranchAsync(Push(), Ct);
        _repo.Write("mas.txt", "mas\n");
        TempGitRepo.Git(_repo.Root, "add", "-A");
        TempGitRepo.Git(_repo.Root, "commit", "-q", "-m", "mas");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.NotEqual(first, result.RemoteCommit);
        Assert.Equal([$"refs/heads/{RunBranch} {_repo.Head}"], RemoteRefs());
    }

    [Fact]
    public async Task A_commit_the_repository_does_not_have_is_a_failure_and_nothing_reaches_the_remote()
    {
        var result = await NewRunner().PushRunBranchAsync(Push("0123456789012345678901234567890123456789"), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_is_unreachable_and_reading_it_says_so()
    {
        var missing = Path.Combine(_container, "no-such-remote.git").Replace('\\', '/');
        var runner = NewRunner(missing);

        var push = await runner.PushRunBranchAsync(Push(), Ct);
        var read = await runner.ReadBranchAsync(Project, RemoteGitFixture.Repository, RemoteBranch.ForRun(Run), Ct);

        Assert.Equal(RemotePushOutcome.Unreachable, push.Outcome);
        Assert.False(read.Reached);
        Assert.Equal(RemoteGitCodes.RemoteUnreachable, read.FailureCode);
    }

    [Fact]
    public async Task Reading_a_branch_that_does_not_exist_is_reached_with_no_commit()
    {
        var read = await NewRunner().ReadBranchAsync(Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("feature/nada")!, Ct);

        Assert.True(read.Reached);
        Assert.Null(read.Commit);
        Assert.Null(read.FailureCode);
    }

    [Fact]
    public async Task Reading_the_main_branch_gives_the_commit_it_points_at()
    {
        TempGitRepo.Git(_repo.Root, "push", "-q", Bare, "main:refs/heads/main");

        var read = await NewRunner().ReadBranchAsync(Project, RemoteGitFixture.Repository, RemoteBranch.TryParse("main")!, Ct);

        Assert.Equal(_repo.Head, read.Commit);
    }

    // ---- the user's repository stays as it was ----

    [Fact]
    public async Task Publishing_to_an_address_leaves_the_repository_of_the_person_exactly_as_it_was_even_with_a_remote_configured()
    {
        _repo.Run("remote", "add", "origin", "https://github.com/someone/else.git");
        _repo.Run("config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        _repo.Run("config", "branch.main.remote", "origin");
        _repo.Run("config", "branch.main.merge", "refs/heads/main");
        _repo.Write("sin-seguimiento.txt", "x\n");
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
        var before = _repo.Snapshot();
        var stateBefore = await manager.CaptureUserRepositoryStateAsync(_repo.Root, Ct);

        // Besides the snapshot (which leaves out the refs of Agent Studio, because its internal layer creates them): the bytes of the configuration, the packed refs and the whole list of refs, unfiltered. The executor
        // creates no ref in the repository of the person, so nothing is left out here.
        var configBefore = File.ReadAllBytes(Path.Combine(_repo.Root, ".git", "config"));
        var packedPath = Path.Combine(_repo.Root, ".git", "packed-refs");
        var packedBefore = File.Exists(packedPath) ? File.ReadAllBytes(packedPath) : null;
        var refsBefore = TempGitRepo.Git(_repo.Root, "for-each-ref", "--format=%(refname) %(objectname)");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.Equal(before, _repo.Snapshot());
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(_repo.Root, ".git", "config")));
        Assert.Equal(packedBefore, File.Exists(packedPath) ? File.ReadAllBytes(packedPath) : null);
        Assert.Equal(refsBefore, TempGitRepo.Git(_repo.Root, "for-each-ref", "--format=%(refname) %(objectname)"));
        Assert.DoesNotContain("agentstudio", TempGitRepo.Git(_repo.Root, "for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
        var stateAfter = await manager.CaptureUserRepositoryStateAsync(_repo.Root, Ct);
        Assert.Empty(UserRepositoryState.Compare(stateBefore, stateAfter));
        Assert.False(File.Exists(Path.Combine(_repo.Root, ".git", "FETCH_HEAD")));
        Assert.DoesNotContain("refs/remotes/", TempGitRepo.Git(_repo.Root, "for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, TempGitRepo.Git(_repo.Root, "for-each-ref", "refs/remotes").Trim());
        Assert.Equal("https://github.com/someone/else.git", _repo.Run("config", "remote.origin.url").Trim());
    }

    // ---- D-05: planted configurations that are refused, one for each family ----

    public static TheoryData<string, string, string, string> RefusedPlantedKeys => new()
    {
        { "credential.helper", "credential.helper", "!sentinel", "credential.helper" },
        { "credential.url helper", "credential.https://github.com.helper", "!sentinel", "credential.*.helper" },
        { "url.insteadOf", "url.https://evil.example/.insteadOf", "https://github.com/", "url.*.insteadof" },
        { "url.pushInsteadOf", "url.https://evil.example/.pushInsteadOf", "https://github.com/", "url.*.pushinsteadof" },
        { "remote.origin.pushurl", "remote.origin.pushurl", "C:/elsewhere/other.git", "remote.*.pushurl" },
        { "http.proxy", "http.proxy", "http://secret-user:secret-pw@proxy.example:8080", "http.proxy" },
        { "http.extraHeader", "http.extraHeader", "Authorization: Basic eDp5", "http.extraheader" },
        { "http.url proxy", "http.https://github.com/.proxy", "http://proxy.example:8080", "http.*.proxy" },
        { "push.pushOption", "push.pushOption", "ci.skip", "push.pushoption" },
        { "remote.origin.receivepack", "remote.origin.receivepack", "sh -c 'exit 1'", "remote.*.receivepack" },
        { "remote.origin.uploadpack", "remote.origin.uploadpack", "sh -c 'exit 1'", "remote.*.uploadpack" },
        { "remote.origin.vcs", "remote.origin.vcs", "sentinel", "remote.*.vcs" },
        { "remote.origin.proxy", "remote.origin.proxy", "http://proxy.example:8080", "remote.*.proxy" },
        { "remote.origin.push", "remote.origin.push", "refs/heads/main:refs/heads/other", "remote.*.push" },
        { "remote.pushDefault", "remote.pushDefault", "origin", "remote.pushdefault" },
        { "branch.main.pushRemote", "branch.main.pushRemote", "origin", "branch.*.pushremote" },
        { "core.alternateRefsCommand", "core.alternateRefsCommand", "sh -c 'exit 1'", "core.alternaterefscommand" },
    };

    [Theory]
    [MemberData(nameof(RefusedPlantedKeys))]
    public async Task A_configuration_planted_in_the_repository_that_a_pair_cannot_override_is_refused_and_nothing_runs(string name, string key, string value, string shown)
    {
        _ = name;
        var sentinel = Sentinel("credential-ran");
        _repo.Run("config", key, value == "!sentinel" ? "!sh -c 'echo ran > \"" + sentinel.Replace('\\', '/') + "\"'" : value);
        var before = _repo.Snapshot();

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.False(result.Inspection.Accepted);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal(shown, refusal.Key);
        Assert.Equal("local", refusal.Scope);
        Assert.EndsWith("config", refusal.OriginFile, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["config"], Launched);
        Assert.False(File.Exists(sentinel), "The sentinel of the helper ran.");
        Assert.Empty(RemoteRefs());
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_global_include_that_points_to_a_file_inside_the_repository_is_refused_for_where_the_file_lives()
    {
        File.WriteAllText(Path.Combine(_repo.Root, "extra.cfg"), "[http]\n\tsslVerify = false\n");
        WriteGlobal("[include]\n\tpath = " + Path.Combine(_repo.Root, "extra.cfg").Replace('\\', '/') + "\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal("http.sslverify", refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.EndsWith("extra.cfg", refusal.OriginFile, StringComparison.Ordinal);
        Assert.Equal(["config"], Launched);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task A_global_includeif_that_points_to_a_file_inside_the_repository_is_refused_too()
    {
        File.WriteAllText(Path.Combine(_repo.Root, "extra.cfg"), "[credential]\n\thelper = !sh -c 'exit 1'\n");
        WriteGlobal("[includeIf \"gitdir/i:**/\"]\n\tpath = " + Path.Combine(_repo.Root, "extra.cfg").Replace('\\', '/') + "\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal("credential.helper", refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task A_global_include_to_a_file_outside_the_repository_is_the_persons_own_and_counts()
    {
        File.WriteAllText(Path.Combine(_container, "mio.cfg"), "[http]\n\tsslVerify = true\n[credential]\n\thelper = manager\n");
        WriteGlobal("[include]\n\tpath = " + Path.Combine(_container, "mio.cfg").Replace('\\', '/') + "\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
    }

    [Fact]
    public async Task A_global_insteadof_that_rewrites_the_address_is_refused_with_the_motive_and_nothing_is_pushed()
    {
        WriteGlobal($"[url \"{ContainerAddress}/elsewhere/\"]\n\tinsteadOf = {ContainerAddress}/\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal("url.*.insteadof", refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.EndsWith(".gitconfig", refusal.OriginFile, StringComparison.Ordinal);
        Assert.Equal(["config", "ls-remote"], Launched);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task A_global_pushinsteadof_that_rewrites_the_address_is_refused_because_get_url_does_not_see_it()
    {
        WriteGlobal($"[url \"{ContainerAddress}/elsewhere/\"]\n\tpushInsteadOf = {ContainerAddress}/\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        Assert.Equal("url.*.pushinsteadof", Assert.Single(result.Inspection.Refusals).Key);
        Assert.Equal(["config"], Launched);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task A_global_insteadof_that_does_not_touch_the_address_does_not_stop_anything()
    {
        WriteGlobal("[url \"https://mirror.example/\"]\n\tinsteadOf = https://gitlab.com/\n[url \"https://mirror.example/\"]\n\tpushInsteadOf = https://gitlab.com/\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
    }

    // ---- D-05: planted configurations that are overridden ----

    [Fact]
    public async Task A_pre_push_hook_of_the_repository_and_a_hooks_path_like_the_one_of_husky_do_not_run_and_the_push_goes_up()
    {
        var hookRan = Sentinel("hook-ran");
        var huskyRan = Sentinel("husky-ran");
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".git", "hooks"));
        File.WriteAllText(Path.Combine(_repo.Root, ".git", "hooks", "pre-push"), ShellWriting(hookRan));
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".husky"));
        File.WriteAllText(Path.Combine(_repo.Root, ".husky", "pre-push"), ShellWriting(huskyRan));

        // The hooks are live: an ordinary push runs the one of .git/hooks, and with core.hooksPath the one of husky.
        TempGitRepo.Git(_repo.Root, "push", "-q", Bare, "HEAD:refs/heads/sanity-1");
        Assert.True(File.Exists(hookRan), "The planted hook is not live: the test proves nothing.");
        File.Delete(hookRan);
        _repo.Run("config", "core.hooksPath", ".husky");
        TempGitRepo.Git(_repo.Root, "push", "-q", Bare, "HEAD:refs/heads/sanity-2");
        Assert.True(File.Exists(huskyRan), "The planted hook of husky is not live: the test proves nothing.");
        File.Delete(huskyRan);
        TempGitRepo.Git(Bare, "update-ref", "-d", "refs/heads/sanity-1");
        TempGitRepo.Git(Bare, "update-ref", "-d", "refs/heads/sanity-2");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.False(File.Exists(hookRan), "The pre-push of .git/hooks ran.");
        Assert.False(File.Exists(huskyRan), "The pre-push of core.hooksPath ran.");
        Assert.Equal([$"refs/heads/{RunBranch} {_repo.Head}"], RemoteRefs());
    }

    [Fact]
    public async Task A_fsmonitor_an_ssh_command_a_proxy_command_and_an_askpass_of_the_repository_do_not_run_and_the_pairs_that_override_them_are_on_every_command()
    {
        var programs = new[] { "fsmonitor", "ssh", "proxy", "askpass" }.ToDictionary(n => n, n => Sentinel(n));
        static string Writing(string file) => "sh -c 'echo ran > \"" + file.Replace('\\', '/') + "\"'";
        _repo.Run("config", "core.fsmonitor", Writing(programs["fsmonitor"]));
        _repo.Run("config", "core.sshCommand", Writing(programs["ssh"]));
        _repo.Run("config", "core.gitProxy", Writing(programs["proxy"]));
        _repo.Run("config", "core.askPass", Writing(programs["askpass"]));

        // The settings are live: without the protection, the programs of the repository do run. (askPass needs a server that asks for credentials, and gitProxy a git:// connection that measured no better here: for those two only the pair is checked.)
        TempGitRepo.TryGit(_repo.Root, "status");
        TempGitRepo.TryGit(_repo.Root, "ls-remote", "ssh://localhost/x.git");

        Assert.True(File.Exists(programs["fsmonitor"]), "core.fsmonitor is not live: the test proves nothing.");
        Assert.True(File.Exists(programs["ssh"]), "core.sshCommand is not live: the test proves nothing.");

        foreach (var file in programs.Values.Where(File.Exists).ToList())
        {
            File.Delete(file);
        }

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.All(programs, p => Assert.False(File.Exists(p.Value), p.Key + " ran."));
        Assert.Equal([$"refs/heads/{RunBranch} {_repo.Head}"], RemoteRefs());
        foreach (var spec in _process.Specs)
        {
            var settings = RemoteGitCommands.Settings(spec);
            Assert.Contains("core.fsmonitor=false", settings);
            Assert.Contains("core.sshCommand=", settings);
            Assert.Contains("core.gitProxy=", settings);
            Assert.Contains("core.askPass=", settings);
        }
    }
    [Fact]
    public async Task Follow_tags_and_a_default_push_of_the_repository_do_not_send_a_tag_nor_another_branch()
    {
        TempGitRepo.Git(_repo.Root, "tag", "-a", "v1", "-m", "v1");
        TempGitRepo.Git(_repo.Root, "branch", "otra");
        _repo.Run("config", "push.followTags", "true");
        _repo.Run("config", "push.default", "matching");
        _repo.Run("config", "push.recurseSubmodules", "on-demand");
        _repo.Run("config", "push.autoSetupRemote", "true");
        _repo.Run("config", "submodule.recurse", "true");

        // The settings are live: an ordinary push of the same repository sends the tag along.
        var sanity = Path.Combine(_container, "sanity.git");
        TempGitRepo.Git(_container, "init", "-q", "--bare", sanity);
        TempGitRepo.Git(_repo.Root, "push", "-q", sanity, "HEAD:refs/heads/main");
        Assert.Contains("refs/tags/v1", TempGitRepo.Git(sanity, "for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
        _repo.Run("config", "push.gpgSign", "true");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.Equal([$"refs/heads/{RunBranch} {_repo.Head}"], RemoteRefs());
    }

    private void ConfigureLfsFilter()
    {
        _repo.Run("config", "filter.lfs.clean", "git-lfs clean -- %f");
        _repo.Run("config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        _repo.Run("config", "filter.lfs.process", "git-lfs filter-process");
    }

    private void CommitAttributes(string relative, string content)
    {
        _repo.Write(relative, content);
        TempGitRepo.Git(_repo.Root, "add", "-f", "--", relative);
        TempGitRepo.Git(_repo.Root, "commit", "-q", "-m", "atributos");
    }

    [Theory]
    [InlineData(".gitattributes")]
    [InlineData("deep/er/.gitattributes")]
    public async Task A_commit_with_git_lfs_in_its_attributes_is_not_published_wherever_the_file_is(string relative)
    {
        ConfigureLfsFilter();
        CommitAttributes(relative, "*.psd filter=lfs diff=lfs merge=lfs -text\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.LfsRefused, result.Outcome);
        Assert.Equal(RemoteGitCodes.LfsDetected, result.FailureCode);
        Assert.True(result.Inspection.UsesLfs);
        Assert.Equal(["config", "ls-remote", "grep"], Launched);
        Assert.Empty(RemoteRefs());
    }

    [Fact]
    public async Task The_commit_that_goes_up_decides_and_not_the_working_tree_in_either_direction()
    {
        ConfigureLfsFilter();
        var clean = _repo.Head;
        CommitAttributes(".gitattributes", "*.psd filter=lfs diff=lfs merge=lfs -text\n");
        var withLfs = _repo.Head;

        // The working tree does not say it (the file was removed from the checkout) and the commit does: refused.
        File.Delete(Path.Combine(_repo.Root, ".gitattributes"));
        var refused = await NewRunner().PushRunBranchAsync(Push(withLfs), Ct);

        // The working tree says it (an uncommitted file) and the commit that goes up does not: published.
        File.WriteAllText(Path.Combine(_repo.Root, ".gitattributes"), "*.psd filter=lfs\n");
        var published = await NewRunner().PushRunBranchAsync(Push(clean), Ct);

        Assert.Equal(RemotePushOutcome.LfsRefused, refused.Outcome);
        Assert.Equal(RemotePushOutcome.Pushed, published.Outcome);
        Assert.Equal([$"refs/heads/{RunBranch} {clean}"], RemoteRefs());
    }

    [Fact]
    public async Task A_commit_with_only_a_commented_line_about_lfs_is_published()
    {
        CommitAttributes(".gitattributes", "# *.psd filter=lfs\n*.cs text\n");
        ConfigureLfsFilter();
        var commented = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.Pushed, commented.Outcome);
    }

    [Fact]
    public async Task A_commit_that_cannot_be_asked_about_lfs_is_not_published()
    {
        ConfigureLfsFilter();

        var result = await NewRunner().PushRunBranchAsync(Push("0123456789012345678901234567890123456789"), Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
        Assert.Equal(RemoteGitCodes.LfsCheckFailed, result.FailureCode);
        Assert.Empty(RemoteRefs());
    }
    [Theory]
    [InlineData("pushurl")]
    [InlineData("url")]
    [InlineData("mirror")]
    public async Task A_remote_of_the_repository_called_like_the_address_is_refused_and_nothing_reaches_either_remote(string variable)
    {
        var other = Path.Combine(_container, "other.git");
        TempGitRepo.Git(_container, "init", "-q", "--bare", other);
        _repo.Run("config", $"remote.{BareAddress}.{variable}", variable == "mirror" ? "true" : other.Replace('\\', '/'));
        var before = _repo.Snapshot();

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal("remote.*." + variable, refusal.Key);
        Assert.Equal("local", refusal.Scope);
        Assert.Equal(["config"], Launched);
        Assert.Empty(RemoteRefs());
        Assert.Equal(string.Empty, TempGitRepo.Git(other, "for-each-ref").Trim());
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task A_nested_include_from_a_file_outside_the_repository_to_one_inside_it_is_refused()
    {
        File.WriteAllText(Path.Combine(_repo.Root, "inner.cfg"), "[http]\n\tsslVerify = false\n");
        File.WriteAllText(Path.Combine(_container, "outer.cfg"), "[include]\n\tpath = " + Path.Combine(_repo.Root, "inner.cfg").Replace('\\', '/') + "\n");
        WriteGlobal("[include]\n\tpath = " + Path.Combine(_container, "outer.cfg").Replace('\\', '/') + "\n");

        var result = await NewRunner().PushRunBranchAsync(Push(), Ct);

        Assert.Equal(RemotePushOutcome.ConfigurationRefused, result.Outcome);
        var refusal = Assert.Single(result.Inspection.Refusals);
        Assert.Equal("http.sslverify", refusal.Key);
        Assert.Equal("global", refusal.Scope);
        Assert.EndsWith("inner.cfg", refusal.OriginFile, StringComparison.Ordinal);
        Assert.Equal(["config"], Launched);
        Assert.Empty(RemoteRefs());
    }
    [Fact]
    public async Task A_configuration_that_is_not_readable_is_an_inspection_that_failed_and_nothing_is_pushed()
    {
        var push = Push();
        File.AppendAllText(Path.Combine(_repo.Root, ".git", "config"), "\n[broken\n\tkey = 1\n");

        var result = await NewRunner().PushRunBranchAsync(push, Ct);

        Assert.Equal(RemotePushOutcome.Failed, result.Outcome);
        Assert.Equal(RemoteGitCodes.InspectionFailed, result.Inspection.FailureCode);
        Assert.Equal(["config"], Launched);
        Assert.Empty(RemoteRefs());
    }
}
