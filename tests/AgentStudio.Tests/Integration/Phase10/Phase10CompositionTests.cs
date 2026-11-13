using System.IO;
using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Infrastructure.Hosting.Diagnostics;
using AgentStudio.Infrastructure.Hosting.GitHub;
using AgentStudio.Infrastructure.Hosting.State;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Infrastructure.Diagnostics;
using AgentStudio.Tests.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// Phase 10 at the level of the composition (docs/21 §10.9, F10-11 and F10-13): the app composed by <c>AddAgentStudioApp</c> resolves everything of the phase with the transport of production; and over the real adapter of GitHub, the real
/// remote executor and the real mailbox, with a <c>gh</c> of the tests (a process runner that records every launch and answers from the hand-written responses), there is no network in the background without the permission of the project, Auto
/// Doctor launches nothing of <c>gh</c> but its version, and a token in the environment of the process never reaches a process of the phase. Each test that says «nothing» has its positive control in the same run, so it cannot pass for having launched nothing at all.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class Phase10CompositionTests
{
    private static readonly string[] TokenVariables = ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_CONFIG_DIR", "GH_DEBUG", "GIT_ASKPASS", "GCM_TRACE"];
    private const string Sentinel = "sentinel-token-that-must-never-travel";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_composition_resolves_everything_of_the_phase_with_the_transport_of_production_and_the_state_under_the_folders_of_the_app()
    {
        await using var world = await AppWorld.OpenAsync();
        var services = world.Services;

        // The remote executor of git knows one transport: the one of GitHub over HTTPS (D-06). No other can be built outside the tests.
        var runner = Assert.IsType<RemoteGitRunner>(services.GetRequiredService<IRemoteGitRunner>());
        var transport = typeof(RemoteGitRunner).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(RemoteTransport)).GetValue(runner);
        Assert.Same(RemoteTransport.GitHubHttps, transport);
        Assert.Equal("https", RemoteTransport.GitHubHttps.Scheme);

        // The gate, the service, the mailbox (one object for both of its ports), the registry of providers, the projection and the local state.
        Assert.NotNull(services.GetRequiredService<IHostingPolicyGate>());
        Assert.NotNull(services.GetRequiredService<IHostingUseGate>());
        Assert.NotNull(services.GetRequiredService<IPublicationService>());
        var mailbox = services.GetRequiredService<RemoteSyncService>();
        Assert.Same(mailbox, services.GetRequiredService<IRemoteSync>());
        Assert.Same(mailbox, services.GetRequiredService<IRemoteMailboxes>());
        Assert.IsType<HostingProviders>(services.GetRequiredService<IHostingProviders>());
        Assert.NotNull(services.GetRequiredService<IPullRequestProjector>());
        Assert.IsType<RemoteSyncStore>(services.GetRequiredService<IRemoteSyncStore>());
        Assert.Equal(Path.Combine(world.Folders.Local.BaseDirectory, "hosting"), services.GetRequiredService<RemoteSyncStoreOptions>().Root);

        // The permission and the policy are one value of the settings.
        var permission = services.GetRequiredService<AppSettingsRemoteSyncPermission>();
        Assert.Same(permission, services.GetRequiredService<IRemoteSyncPermission>());
        Assert.Same(permission, services.GetRequiredService<IRemoteSyncPolicy>());

        // The screens: the section of pull requests is the last part of the document of the repository, and «Trabajo» gets the projection.
        var pullRequests = services.GetRequiredService<PullRequestsViewModel>();
        Assert.Same(pullRequests, services.GetRequiredService<RepositoryViewModel>().PullRequests);
        Assert.True(pullRequests.IsAvailable);
        Assert.Same(services.GetRequiredService<IPullRequestProjector>(), services.GetRequiredService<WorkBackend>().PullRequests);
        Assert.NotNull(services.GetRequiredService<RemoteSyncAttachment>());
        Assert.IsType<ShellAddressOpener>(services.GetRequiredService<IAddressOpener>());

        // Auto Doctor: the sources of the adapter are in the panel, and «Ir a…» the pull requests can be reached from it.
        var sources = services.GetServices<IDoctorCheckSource>().ToList();
        Assert.Contains(sources, s => s is GhToolCheckSource);
        Assert.Contains(sources, s => s is HostingCheckSource);
        var main = services.GetRequiredService<MainViewModel>();
        Assert.Contains("pull-requests", main.Doctor.Navigation!.Targets);
        Assert.Contains(DoctorFixCatalog.All.OfType<DoctorFix.Navigate>(), f => f.Code == DoctorFixCatalog.OpenPullRequests && main.Doctor.Navigation.Targets.Contains(f.Target));
    }

    [Fact]
    public async Task Without_the_permission_a_day_goes_by_and_no_gh_api_is_launched_and_with_it_the_poll_only_reads()
    {
        using var rig = new GhApp();
        await using var world = await AppWorld.OpenAsync(RepoWithOrigin(), configure: rig.Configure);
        var project = world.App.Project;
        var pull = new PullRequestRef(Phase10World.Repository, 7);
        await world.Get<RemoteSyncService>().AttachAsync(project, Ct);
        await world.Get<IRemoteSync>().FollowAsync(project, pull, null, Ct);
        await FlushAsync(world, project);
        var permission = world.Get<IRemoteSyncPermission>();
        Assert.False(permission.IsAllowed(project.ProjectId));
        var beforeTheDay = rig.Script.Calls.Count(IsApi);

        // Opening the mailbox and following a pull request launch nothing either: without the permission not one call has gone out so far.
        Assert.Equal(0, beforeTheDay);

        // A day, in steps that are longer than the longest interval: the clock of the mailbox is the one of the test.
        for (var i = 0; i < 24; i++)
        {
            rig.Time.Advance(TimeSpan.FromHours(1));
            await Task.Yield();
        }

        await FlushAsync(world, project);
        Assert.Equal(beforeTheDay, rig.Script.Calls.Count(IsApi));

        // The control: the person gives the permission, and the next tick of the poll reads, with GET and nothing else.
        await permission.SetAsync(project.ProjectId, true, Ct);
        var interval = world.Get<IRemoteSyncPolicy>().Interval;
        rig.Time.Advance(interval);
        await rig.Launched.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await FlushAsync(world, project);

        var calls = rig.Script.Calls.Where(IsApi).Skip(beforeTheDay).ToList();
        Assert.True(calls.Count > 0, Why(rig, world, project));
        Assert.All(calls, c => Assert.Equal("GET", c.Method));
    }

    [Fact]
    public async Task Auto_Doctor_with_all_its_sources_launches_nothing_of_gh_but_its_version()
    {
        using var rig = new GhApp();
        await using var world = await AppWorld.OpenAsync(RepoWithOrigin(), configure: rig.Configure);

        var report = await world.Get<IDoctorService>().RunAsync(world.App.Project, Ct);

        // The control: the sources ran, and gh was asked for its version through the door; nothing of gh reached the process runner.
        Assert.NotEmpty(report.Checks);
        Assert.Contains(report.Checks, c => c.Code.StartsWith("doctor.gh.", StringComparison.Ordinal));
        Assert.Contains(rig.Door.Asked, a => a.Tool == DoctorTool.GhVersion);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task A_token_in_the_environment_of_the_process_reaches_no_process_of_gh_nor_of_the_doctor()
    {
        var saved = TokenVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in TokenVariables)
            {
                Environment.SetEnvironmentVariable(name, Sentinel);
            }

            using var rig = new GhApp(ambientIsTheProcess: true);
            await using var world = await AppWorld.OpenAsync(RepoWithOrigin(), configure: rig.Configure);
            var project = world.App.Project;
            await world.Get<RemoteSyncService>().AttachAsync(project, Ct);
            await world.Get<IRemoteSync>().FollowAsync(project, new PullRequestRef(Phase10World.Repository, 7), null, Ct);
            await world.Get<IRemoteSync>().SyncNowAsync(project, Ct);
            _ = await world.Get<IDoctorService>().RunAsync(project, Ct);

            // The control: the variables are really in this process, and gh was launched at least once by the click of «Sincronizar ahora».
            Assert.Equal(Sentinel, Environment.GetEnvironmentVariable("GH_TOKEN"));
            Assert.True(rig.Script.Specs.Count > 0, Why(rig, world, project));
            foreach (var spec in rig.Script.Specs)
            {
                // A null value is the removal of a variable (the executor says «this one does not pass»); only a value is carried. The directory of the configuration of gh passes only when it is a safe absolute path (WP2).
                var carried = (spec.Environment ?? new Dictionary<string, string?>()).Where(e => e.Value is not null).ToList();
                Assert.DoesNotContain(carried, e => TokenVariables.Contains(e.Key, StringComparer.OrdinalIgnoreCase) && !string.Equals(e.Key, "GH_CONFIG_DIR", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(carried, e => e.Value!.Contains(Sentinel, StringComparison.Ordinal));
                Assert.DoesNotContain(spec.Arguments, a => a.Contains(Sentinel, StringComparison.Ordinal));
                Assert.DoesNotContain(spec.Arguments, a => a is "auth" or "token" or "--show-token");
                Assert.DoesNotContain(Sentinel, spec.StandardInput ?? string.Empty);
            }
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Waits until the mailbox has dealt with everything that was posted before: one message that goes through its single queue and comes back.</summary>
    private static Task FlushAsync(AppWorld world, AgentStudio.Application.Projects.ProjectContext project) =>
        world.Get<IRemoteSync>().CancelAsync(project.ProjectId, "flush", Ct);

    /// <summary>The repository of the runs of the tests with GitHub as its origin: the mailbox only follows the repository of the project, and it is never fetched from.</summary>
    private static AgentStudio.Tests.Infrastructure.Git.TempGitRepo RepoWithOrigin()
    {
        var repo = AgentStudio.Tests.Integration.Phase5.Phase5Runs.Repo();
        AgentStudio.Tests.Infrastructure.Git.TempGitRepo.Git(repo.Root, "remote", "add", "origin", "https://github.com/octo/hello.git");
        return repo;
    }

    private static string Why(GhApp rig, AppWorld world, AgentStudio.Application.Projects.ProjectContext project) =>
        $"asked of the door: {string.Join(",", rig.Door.Asked.Select(a => a.Tool))}; health: {world.Get<IRemoteSync>().Health(project.ProjectId)}; pending: {string.Join(",", world.Get<IRemoteSync>().Pending(project.ProjectId).Select(p => p.State + "/" + p.Reason))}; specs: {rig.Script.Specs.Count}";

    /// <summary>Whether a launch of <c>gh</c> is a call of its API (the only ones that use the network); <c>gh --version</c> is not.</summary>
    private static bool IsApi(GhCallSeen call) => call.Spec.Arguments.Count > 0 && call.Spec.Arguments[0] == "api";

    /// <summary>The real adapter of GitHub, its queue and the real mailbox, over a <c>gh</c> that is a stand-in file, a process runner that answers from the hand-written responses, and a clock of the test.</summary>
    private sealed class GhApp : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "as fase10 gh " + Guid.NewGuid().ToString("N")[..10]);
        private readonly bool _ambientIsTheProcess;

        public GhApp(bool ambientIsTheProcess = false)
        {
            _ambientIsTheProcess = ambientIsTheProcess;
            Directory.CreateDirectory(Path.Combine(_folder, "tools"));
            File.WriteAllText(Path.Combine(_folder, "tools", "gh.exe"), "stand-in");
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
            Script = new ScriptedGh();
            Script.Route("GET", "/repos/octo/hello/pulls/7", "handmade-pull-open-200.http");
            Door = ScriptedDoor.LikeTheDevelopmentMachine().Says(DoctorTool.GhVersion, "gh version 2.96.0 (2026-07-02)\r\nhttps://github.com/cli/cli/releases/tag/v2.96.0\r\n");
        }

        public FakeTimeProvider Time { get; }

        public ScriptedGh Script { get; }

        public ScriptedDoor Door { get; }

        /// <summary>Released once for each process the adapter launches, so a test waits for the launch without waiting for the clock.</summary>
        public SemaphoreSlim Launched { get; } = new(0);

        public void Configure(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            services.Replace(ServiceDescriptor.Singleton<IDoctorToolRunner>(Door));
            services.Replace(ServiceDescriptor.Singleton(provider => new GhRunner(
                new Signalling(Script, Launched),
                GhFolders.Under(Path.Combine(_folder, "local")),
                provider.GetService<IUntrustedRoots>(),
                () => Path.Combine(_folder, "tools"),
                () => _ambientIsTheProcess ? DoctorEnvironment.CurrentProcess() : new Dictionary<string, string>(),
                Time)));
        }

        private sealed class Signalling(IProcessRunner inner, SemaphoreSlim launched) : IProcessRunner
        {
            public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
            {
                var result = await inner.RunAsync(spec, cancellationToken);
                launched.Release();
                return result;
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>The tests that change variables of the environment of the process run one at a time and apart from the rest: the environment is of the whole process.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
