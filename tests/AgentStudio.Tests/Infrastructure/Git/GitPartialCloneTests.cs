using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// H-04: in a partial clone (a promisor remote) git fetches a missing object on demand, a "lazy fetch", from inside an
/// ordinary command (<c>diff --binary</c>, <c>archive</c>, <c>reset --hard</c>), through the transport or remote helper
/// the repository's configuration chooses. Measured with git 2.45.2: with <c>remote.origin.url = sentinel::x</c> (or
/// <c>remote.origin.vcs = sentinel</c>) and <c>protocol.sentinel.allow = always</c> in the repository, that lazy fetch ran
/// <c>git-remote-sentinel</c> despite every <c>-c protocol.*.allow=never</c> Agent Studio passes. These tests put sentinel
/// helpers on the PATH (the PATH is outside the threat model, ADR-0018; it only makes a helper observable) and so run
/// alone. Each one first shows, with a plain git, that the helper does run.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
[Trait("Category", "LocalTools")]
public sealed class GitPartialCloneTests : GitManagerTestBase
{
    /// <summary>
    /// The user's project as a partial clone (<c>--filter=blob:none</c>) of <see cref="GitManagerTestBase.Repo"/>, with a
    /// second commit whose blob the clone lacks, and the hostile configuration: origin's transport is the sentinel helper
    /// and the repository allows it.
    /// </summary>
    private (string Client, string First, string Second) PartialClone()
    {
        var first = Repo.BaseCommit;
        Repo.Write("datos.txt", "contenido que el clon parcial no tiene\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "segundo");
        var second = Repo.Head;
        Repo.Run("config", "uploadpack.allowfilter", "true");
        Repo.Run("config", "uploadpack.allowanysha1inwant", "true");

        var container = Path.GetDirectoryName(Repo.Root)!;
        var client = Path.Combine(container, "clon parcial");
        TempGitRepo.Git(container, "clone", "-q", "--no-local", "--filter=blob:none", "--no-checkout", Repo.Root, client);
        Assert.Contains(
            TempGitRepo.Git(client, "rev-list", "--objects", "--missing=print", second).Split('\n'),
            line => line.StartsWith('?'));

        TempGitRepo.Git(client, "config", "remote.origin.url", "sentinel::x");
        TempGitRepo.Git(client, "config", "protocol.sentinel.allow", "always");
        return (client, first, second);
    }

    [Fact]
    public async Task A_diff_that_needs_a_missing_object_never_fetches_it_so_no_remote_helper_runs()
    {
        var (client, first, second) = PartialClone();
        using var helpers = new SentinelHelpers();
        using (helpers.OnPath())
        {
            // Control: a plain git fetches the missing blob lazily for the same diff, through the helper.
            TempGitRepo.TryGit(client, "diff", "--binary", "--no-ext-diff", "--no-textconv", first, second, "--");
            Assert.True(helpers.Ran, "control: a plain git diff did not run the sentinel helper, so this test could not see one.");
            helpers.Reset();

            // diff is not a content command, so the configuration inspection does not refuse it first.
            var manager = NewManager();
            using var patch = new MemoryStream();
            var error = await Record.ExceptionAsync(() => manager.WriteDiffAsync(client, first, second, patch, Ct));

            Assert.False(helpers.Ran, "an internal diff fetched a missing object lazily through the repository's remote helper.");
            var failure = Assert.IsType<GitCommandException>(error);

            // GIT_NO_LAZY_FETCH stopped it before any transport was chosen, not the protocol check of the fetch: that check
            // names the transport ("transport 'sentinel' not allowed", in any language git speaks).
            Assert.DoesNotContain("sentinel", failure.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_partial_clone_is_refused_with_a_clear_message_before_any_object_is_fetched()
    {
        var (client, _, second) = PartialClone();
        using var helpers = new SentinelHelpers();
        using (helpers.OnPath())
        {
            var recorder = new RecordingProcessRunner(new ProcessRunner());
            var manager = new GitWorktreeManager(recorder, new ExecutableLocator(), new GitWorktreeOptions(Repo.WorktreeRoot));
            var project = new ProjectContext(ProjectId, client);

            var inspect = await Record.ExceptionAsync(() => manager.InspectAsync(client, Ct));
            var ensure = await Record.ExceptionAsync(() => manager.EnsureAsync(new WorktreeRequest(project, TestRun, "T1", second), Ct));

            Assert.False(helpers.Ran, "creating a worktree in a partial clone fetched a missing object lazily through the repository's remote helper.");
            Assert.All(new[] { inspect, ensure }, error =>
            {
                var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
                Assert.Contains(refusal.Refusals, r => r.Key == "remote.origin.promisor");
                Assert.Contains("partial clone", refusal.Message, StringComparison.Ordinal);
            });
            Assert.DoesNotContain(recorder.Specs, s => RecordingProcessRunner.SubCommand(s) == "worktree" && s.Arguments.Contains("add"));
        }
    }

    /// <summary>
    /// Measured with git 2.45.2: <c>-c protocol.allow=never</c> and the per-transport <c>-c</c> cover only the named
    /// transports, so a repository's <c>protocol.sentinel.allow=always</c> opens <c>sentinel::x</c>. GIT_ALLOW_PROTOCOL
    /// overrides every <c>protocol.*.allow</c>; an EMPTY one is no answer, because git splits it into one empty name and
    /// then runs the empty-named helper of <c>::x</c>. Both URLs are tried with the configuration and environment of a
    /// real internal command.
    /// </summary>
    [Fact]
    public async Task No_transport_opens_with_the_configuration_and_environment_of_an_internal_command_whatever_the_repository_allows()
    {
        Repo.Run("config", "protocol.sentinel.allow", "always");
        using var helpers = new SentinelHelpers();
        using (helpers.OnPath())
        {
            var recorder = new RecordingProcessRunner(new ProcessRunner());
            await NewManager(recorder).InspectAsync(Repo.Root, Ct);
            var internalCommand = recorder.Specs.Last(s => RecordingProcessRunner.SubCommand(s) == "status");

            foreach (var url in new[] { "sentinel::x", "::x" })
            {
                // Control: a plain git opens the transport and runs the helper.
                TempGitRepo.TryGit(Repo.Root, "ls-remote", url);
                Assert.True(helpers.Ran, $"control: a plain git ls-remote {url} did not run the sentinel helper.");
                helpers.Reset();

                var (exitCode, _, _) = RunLike(internalCommand, Repo.Root, "ls-remote", url);

                Assert.False(helpers.Ran, $"the configuration and environment of an internal command let git run the remote helper of '{url}'.");
                Assert.NotEqual(0, exitCode);
            }
        }
    }

    [Fact]
    public async Task Every_internal_command_disables_lazy_fetching_and_allows_no_transport_whatever_its_parent_says()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        using (new EnvironmentScope(new Dictionary<string, string>
        {
            ["GIT_NO_LAZY_FETCH"] = "0",
            ["GIT_ALLOW_PROTOCOL"] = "file:ext:sentinel",
        }))
        {
            var manager = NewManager(recorder);
            var lease = await EnsureAsync(manager, "T1");
            await CommitInAsync(manager, lease, "nuevo.txt", "x\n");
            await manager.InspectAsync(Repo.Root, Ct);
        }

        Assert.NotEmpty(recorder.Specs);
        Assert.All(recorder.Specs, spec =>
        {
            Assert.True(spec.Environment!.TryGetValue("GIT_NO_LAZY_FETCH", out var noLazyFetch) && noLazyFetch == "1", "GIT_NO_LAZY_FETCH must be 1.");
            Assert.True(spec.Environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var allowed) && allowed == "-", "GIT_ALLOW_PROTOCOL must allow no transport.");
        });
    }

    /// <summary>
    /// Runs git with the <c>-c</c> configuration and the environment of <paramref name="spec"/>, a recorded internal
    /// command, but with <paramref name="command"/> as the sub-command.
    /// </summary>
    private static (int ExitCode, string Output, string Error) RunLike(ProcessSpec spec, string workingDirectory, params string[] command)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var index = 0; index + 1 < spec.Arguments.Count && spec.Arguments[index] == "-c"; index += 2)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(spec.Arguments[index + 1]);
        }

        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Remote helpers that leave a mark when git runs them: <c>git-remote-sentinel</c> (for <c>sentinel::x</c> or
    /// <c>remote.*.vcs = sentinel</c>) and <c>git-remote-</c>, the empty-named helper of a <c>::x</c> URL. Each exits 1,
    /// so no fetch ever succeeds. The folder is outside every repository and worktree, with an ASCII path.
    /// </summary>
    private sealed class SentinelHelpers : IDisposable
    {
        public SentinelHelpers()
        {
            Folder = Path.Combine(Path.GetTempPath(), "as-helper-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Folder);
            foreach (var name in new[] { "sentinel", string.Empty })
            {
                File.WriteAllText(
                    Path.Combine(Folder, "git-remote-" + name),
                    "#!/bin/sh\necho \"helper[" + name + "] $*\" >> '" + Mark.Replace('\\', '/') + "'\nexit 1\n");
            }
        }

        public string Folder { get; }

        public string Mark => Path.Combine(Folder, "ran.txt");

        public bool Ran => File.Exists(Mark);

        public void Reset() => File.Delete(Mark);

        /// <summary>Puts the helpers first on the PATH of the test process, which every git started meanwhile inherits.</summary>
        public EnvironmentScope OnPath() =>
            new(new Dictionary<string, string> { ["PATH"] = Folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") });

        public void Dispose() => TempFolders.Delete(Folder);
    }
}
