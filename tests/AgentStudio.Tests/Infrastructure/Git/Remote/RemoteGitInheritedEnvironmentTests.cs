using System.IO;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// Variables of the environment of the app that redirect git, choose how it authenticates or dump what it does (docs/21 A-3, F10-3): with the real git, none of them reaches the process of the executor. They run alone
/// (<see cref="ProcessEnvironmentCollection"/>): a git started meanwhile by another test would inherit them.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
[Trait("Category", "LocalTools")]
public sealed class RemoteGitInheritedEnvironmentTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as remoto env " + Guid.NewGuid().ToString("N")[..10]);

    public RemoteGitInheritedEnvironmentTests()
    {
        Directory.CreateDirectory(Home);
        TempGitRepo.Git(_container, "init", "-q", "--bare", Bare);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Bare => Path.Combine(_container, "remote.git");

    private string Home => Path.Combine(_container, "home");

    public void Dispose()
    {
        _repo.Dispose();
        TempFolders.Delete(_container);
    }

    private IReadOnlyDictionary<string, string> Ambient()
    {
        var variables = new Dictionary<string, string>(DoctorEnvironment.CurrentProcess(), StringComparer.OrdinalIgnoreCase) { ["HOME"] = Home, ["USERPROFILE"] = Home };
        variables.Remove("HOMEDRIVE");
        variables.Remove("HOMEPATH");
        return variables;
    }

    [Fact]
    public async Task Inherited_variables_that_redirect_git_or_dump_its_work_do_not_reach_the_process()
    {
        var bare = Bare.Replace('\\', '/');
        var trace = Path.Combine(_container, "trace.txt");
        var rewriting = Path.Combine(_container, "rewriting.gitconfig");
        File.WriteAllText(rewriting, $"[url \"{_container.Replace('\\', '/')}/elsewhere/\"]\n\tinsteadOf = {_container.Replace('\\', '/')}/\n");

        // The variable is live: with only GIT_TRACE set, an ordinary git writes the file.
        using (new EnvironmentScope(new Dictionary<string, string> { ["GIT_TRACE"] = trace }))
        {
            TempGitRepo.TryGit(_repo.Root, "status");
        }

        Assert.True(File.Exists(trace), "GIT_TRACE does nothing here: the test proves nothing.");
        File.Delete(trace);

        RemotePushResult result;
        var head = _repo.Head;
        var variables = new Dictionary<string, string>
        {
            ["GIT_TRACE"] = trace,
            ["GIT_TRACE_CURL"] = trace,
            ["GIT_CURL_VERBOSE"] = "1",
            ["GIT_SSH_COMMAND"] = "evil-ssh",
            ["GIT_ASKPASS"] = "evil-askpass",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "url." + _container.Replace('\\', '/') + "/elsewhere/.insteadOf",
            ["GIT_CONFIG_VALUE_0"] = _container.Replace('\\', '/') + "/",
            ["GIT_CONFIG_GLOBAL"] = rewriting,
            ["GIT_CONFIG_SYSTEM"] = rewriting,
            ["GH_TOKEN"] = "ghp_S3cretT0ken",
            ["GITHUB_TOKEN"] = "ghp_S3cretT0ken",
            ["HTTPS_PROXY"] = "http://evil.example:3128",
        };
        using (new EnvironmentScope(variables))
        {
            var runner = new RemoteGitRunner(
                new ProcessRunner(),
                new ExecutableLocator(),
                RemoteGitFolders.Under(Path.Combine(_container, "local")),
                _repo.WorktreeRoot,
                RemoteTransport.ForTests("file", _ => bare),
                Ambient);
            result = await runner.PushRunBranchAsync(new RemotePush(new ProjectContext("p", _repo.Root), RemoteGitFixture.Repository, new RunId(RemoteGitFixture.RunIdValue), head), Ct);
        }

        Assert.Equal(RemotePushOutcome.Pushed, result.Outcome);
        Assert.False(File.Exists(trace), "GIT_TRACE reached the process of the executor.");
        Assert.Equal($"refs/heads/agentstudio/run-{RemoteGitFixture.RunIdValue} {_repo.Head}", TempGitRepo.Git(Bare, "for-each-ref", "--format=%(refname) %(objectname)").Trim());
        Assert.False(Directory.Exists(Path.Combine(_container, "elsewhere")));
    }
}

/// <summary>The composition of the executor (docs/21 D-06): it has no parameters and always resolves the one transport of production.</summary>
public sealed class RemoteGitCompositionTests
{
    [Fact]
    public void The_registration_has_no_parameter_and_resolves_one_executor_with_the_https_transport_of_production()
    {
        var method = typeof(RemoteGitDependencyInjection).GetMethod(nameof(RemoteGitDependencyInjection.AddAgentStudioRemoteGit))!;
        Assert.Single(method.GetParameters());

        using var provider = new ServiceCollection().AddAgentStudioRemoteGit().BuildServiceProvider();

        var runner = Assert.IsType<RemoteGitRunner>(provider.GetRequiredService<AgentStudio.Application.Hosting.IRemoteGitRunner>());
        Assert.Same(RemoteTransport.GitHubHttps, runner.Transport);
        Assert.Equal("https", runner.Transport.Scheme);
        Assert.Same(runner, provider.GetRequiredService<AgentStudio.Application.Hosting.IRemoteGitRunner>());
    }

    [Fact]
    public void The_registration_keeps_the_process_runner_and_the_locator_registered_before()
    {
        var process = new ProcessRunner();
        var services = new ServiceCollection();
        services.AddSingleton<AgentStudio.Application.Execution.IProcessRunner>(process);

        using var provider = services.AddAgentStudioRemoteGit().BuildServiceProvider();

        Assert.Same(process, provider.GetRequiredService<AgentStudio.Application.Execution.IProcessRunner>());
        Assert.NotNull(provider.GetRequiredService<AgentStudio.Application.Execution.IExecutableLocator>());
    }
}
