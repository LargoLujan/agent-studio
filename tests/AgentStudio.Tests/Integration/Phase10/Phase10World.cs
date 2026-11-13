using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using AgentStudio.Orchestration.Publication;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// The app as it composes itself (<c>AddAgentStudioApp</c>, through <see cref="AppWorld"/>) with a remote of its own: a bare folder with real git, the platform that acts on it (<see cref="LocalRemoteHostingProvider"/>) and
/// the real remote executor of git, whose transport is the one of the tests (it pushes to the folder), over a recorder of every process it launches. A real run of the engine (scripted agents) gives the commit that is published.
/// Nothing touches the network, <c>gh</c> or a CLI of a provider; the person's own git configuration is not read (a temporary <c>HOME</c>).
/// </summary>
internal sealed class Phase10World : IAsyncDisposable
{
    public static readonly RemoteRepository Repository = RemoteRepository.TryCreateGitHub("octo", "hello")!;

    private readonly string _container;

    /// <summary>The clock of the mailbox (only of the mailbox: the engine keeps its own): the parked work retries and the courtesy after letting the project go are measured on it, so a test moves it instead of waiting.</summary>
    public FakeTimeProvider Clock { get; }

    private Phase10World(AppWorld app, string container, string bare, LocalRemoteHostingProvider platform, RecordingProcessRunner git, FakeTimeProvider clock)
    {
        Clock = clock;
        App = app;
        _container = container;
        Bare = bare;
        Platform = platform;
        Git = git;
    }

    public AppWorld App { get; }

    public string Bare { get; }

    public LocalRemoteHostingProvider Platform { get; }

    /// <summary>Every process the remote executor of git launched.</summary>
    public RecordingProcessRunner Git { get; }

    public AgentStudio.Application.Projects.ProjectContext Project => App.App.Project;

    public IRemoteSync Sync => App.Get<IRemoteSync>();

    public IPublicationService Publication => App.Get<IPublicationService>();

    public IProjectActivity Activity => App.Get<IProjectActivity>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Phase10World> OpenAsync(Action<IServiceCollection>? configure = null)
    {
        var container = Path.Combine(Path.GetTempPath(), "as fase10 " + Guid.NewGuid().ToString("N")[..10]);
        var home = Path.Combine(container, "home");
        Directory.CreateDirectory(home);
        var bare = Path.Combine(container, "remote.git");
        TempGitRepo.Git(container, "init", "-q", "--bare", bare);
        var scratch = Path.Combine(container, "platform");
        var platform = new LocalRemoteHostingProvider(bare, scratch);
        var git = new RecordingProcessRunner(new ProcessRunner());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var repo = Phase5Runs.Repo();
        try
        {
            // The user's repository names GitHub as its origin (it is never fetched from), and the base branch of the platform starts where the repository is.
            TempGitRepo.Git(repo.Root, "remote", "add", "origin", "https://github.com/octo/hello.git");
            TempGitRepo.Git(repo.Root, "push", "-q", bare, "HEAD:refs/heads/main");
            var address = bare.Replace('\\', '/');
            var app = await AppWorld.OpenAsync(
                repo,
                configure: services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IRemoteGitRunner>(provider => new RemoteGitRunner(
                        git,
                        new ExecutableLocator(roots: provider.GetService<IUntrustedRoots>()),
                        RemoteGitFolders.Under(Path.Combine(container, "local")),
                        provider.GetRequiredService<OrchestrationPaths>().WorktreeRoot,
                        RemoteTransport.ForTests("file", _ => address),
                        () => Ambient(home))));
                    services.Replace(ServiceDescriptor.Singleton<IHostingProviders>(new LocalProviders(platform, Repository)));
                    services.Replace(ServiceDescriptor.Singleton(provider => new RemoteSyncService(
                        provider.GetRequiredService<IDecisionRecorder>(),
                        provider.GetRequiredService<IProjectActivity>(),
                        provider.GetRequiredService<IHostingProviders>(),
                        provider.GetRequiredService<IRemoteSyncStore>(),
                        provider.GetService<IRemoteSyncPolicy>(),
                        clock)));
                    configure?.Invoke(services);
                });
            return new Phase10World(app, container, bare, platform, git, clock);
        }
        catch
        {
            repo.Dispose();
            TempFolders.Delete(container);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> Ambient(string home)
    {
        var variables = new Dictionary<string, string>(DoctorEnvironment.CurrentProcess(), StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
        };
        variables.Remove("HOMEDRIVE");
        variables.Remove("HOMEPATH");
        return variables;
    }

    /// <summary>A reviewed run of the engine that succeeded, with the verdict of its judge: the commit to publish is the integration one.</summary>
    public Task<RunResult> SucceededRunAsync() => Phase5Runs.SucceededAsync(App.App);

    /// <summary>
    /// Returns when the mailbox has nothing left to do: it waits for what is running, and moves the clock of the mailbox past the courtesy and the retry of whatever was parked, as many times as it takes (a test never waits for the wall clock).
    /// </summary>
    public async Task SettleAsync()
    {
        var box = await App.Get<RemoteSyncService>().OfAsync(Project, Ct);
        for (var i = 0; i < 200; i++)
        {
            await box.WhenIdleAsync();

            // What is parked (an observation that found the project in its courtesy) is not in the statuses and waits for its timer: at least three turns of the clock past the courtesy and the retry, always.
            if (i >= 3 && box.Pending().Count == 0)
            {
                return;
            }

            Clock.Advance(TimeSpan.FromSeconds(1.5));
            await Task.Yield();
        }

        Assert.Fail("The mailbox did not settle: " + string.Join(", ", box.Pending().Select(p => $"{p.Ticket.Operation}:{p.State}:{p.Reason}")));
    }

    /// <summary>The objects the bare folder holds under a branch.</summary>
    public string? BareRef(string branch)
    {
        var text = TempGitRepo.Git(Bare, "for-each-ref", "--format=%(objectname)", "refs/heads/" + branch).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The log of the project, as the app holds it (the entries and whether it verifies).</summary>
    public Task<DecisionLogState> LogAsync() => App.App.LoadLogAsync();

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        TempFolders.Delete(_container);
    }
}
