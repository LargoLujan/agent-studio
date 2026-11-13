using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Team;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Hosting.Diagnostics;
using AgentStudio.Infrastructure.Hosting.GitHub;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// docs/21 D-09 and §10.4: the registry of providers (by the kind an identifier carries; a remote is recognised from the configuration of the repository, with no connection), the registration in the container, and the
/// one path of the version of <c>gh</c>: always the door of Auto Doctor, with the five variables that keep it from the network and its neutral folder, and never a launch of the adapter.
/// </summary>
public sealed class HostingProvidersTests : IDisposable
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\project");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class Remotes(RemoteListing listing) : IRepositoryRemoteReader
    {
        public List<string> Roots { get; } = [];

        public Task<RemoteListing> ListRemotesAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Roots.Add(projectRoot);
            return Task.FromResult(listing);
        }
    }

    /// <summary>The remotes as the git layer of the app keeps them: clean, so the user of an ssh address (<c>git@</c>) is already gone.</summary>
    private static RemoteListing Listing(params (string Name, string Url)[] remotes) => new([.. remotes.Select(r => RepositoryRemote.Cleaned(r.Name, r.Url))], null);

    // ---- The registry ----

    [Fact]
    public void A_github_repository_has_the_provider_of_github()
    {
        using var rig = new GhRig();
        var providers = new HostingProviders(rig.Provider, new Remotes(Listing()));

        Assert.Same(rig.Provider, providers.For(GhRig.Repo));
    }

    [Theory]
    [InlineData("https://github.com/octo/hello.git", "github.com/octo/hello")]
    [InlineData("https://github.com/octo/hello", "github.com/octo/hello")]
    [InlineData("git@github.com:octo/hello.git", "github.com/octo/hello")]
    [InlineData("ssh://git@github.com/octo/hello.git", "github.com/octo/hello")]
    [InlineData("ssh://git@github.com/octo/hello", "github.com/octo/hello")]
    public async Task A_remote_is_recognised_from_the_configuration_and_never_by_a_connection(string url, string id)
    {
        using var rig = new GhRig();
        var remotes = new Remotes(Listing(("origin", url)));

        var repository = await new HostingProviders(rig.Provider, remotes).IdentifyAsync(Project, "origin", CancellationToken.None);

        Assert.Equal(id, repository!.Id);
        Assert.Equal([Project.RootPath], remotes.Roots);
        Assert.Empty(rig.Script.Specs);
    }

    [Theory]
    [InlineData("https://gitlab.com/octo/hello.git")]
    [InlineData("https://github.com.evil.example/octo/hello.git")]
    [InlineData("http://github.com/octo/hello.git")]
    [InlineData("https://github.com:8443/octo/hello.git")]
    [InlineData("git@github.com:octo/hello/extra.git")]
    [InlineData("git@github.com:-octo/hello.git")]
    [InlineData("ssh://git@gitlab.com/octo/hello.git")]
    [InlineData("ssh://git@github.com:2222/octo/hello.git")]
    [InlineData("file:///c:/repos/hello")]
    public async Task A_remote_that_is_not_github_or_breaks_the_rules_of_an_identifier_is_not_a_repository(string url)
    {
        using var rig = new GhRig();

        var repository = await new HostingProviders(rig.Provider, new Remotes(Listing(("origin", url)))).IdentifyAsync(Project, "origin", CancellationToken.None);

        Assert.Null(repository);
    }

    [Fact]
    public async Task A_remote_that_is_not_there_or_could_not_be_read_is_not_a_repository_and_the_name_is_required()
    {
        using var rig = new GhRig();
        var missing = new HostingProviders(rig.Provider, new Remotes(Listing(("upstream", "https://github.com/octo/hello.git"))));
        var refused = new HostingProviders(rig.Provider, new Remotes(RemoteListing.Refused("git-failed")));

        Assert.Null(await missing.IdentifyAsync(Project, "origin", CancellationToken.None));
        Assert.Null(await refused.IdentifyAsync(Project, "origin", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => missing.IdentifyAsync(Project, " ", CancellationToken.None));
    }

    // ---- The container ----

    private ServiceProvider Container(ScriptedGh script, ScriptedDoor door)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(script);
        services.AddSingleton<IDoctorToolRunner>(door);
        services.AddSingleton<IRepositoryRemoteReader>(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))));
        services.AddAgentStudioGitHubHosting(_temp.Combine("local"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_registration_gives_one_provider_the_registry_and_the_two_sources_of_auto_doctor_without_the_services_of_the_mailbox()
    {
        using var container = Container(new ScriptedGh(), new ScriptedDoor());

        var provider = container.GetRequiredService<IHostingProvider>();
        Assert.IsType<GitHubHostingProvider>(provider);
        Assert.Same(provider, container.GetRequiredService<IHostingProvider>());
        Assert.Same(provider, container.GetRequiredService<GitHubHostingProvider>());
        var providers = container.GetRequiredService<IHostingProviders>();
        Assert.Same(provider, providers.For(GhRig.Repo));
        var sources = container.GetServices<IDoctorCheckSource>().ToList();
        Assert.Equal([DoctorSection.Tools, DoctorSection.Project], sources.Select(s => s.Section));
        Assert.IsType<GhToolCheckSource>(sources[0]);
        Assert.IsType<HostingCheckSource>(sources[1]);
        Assert.Equal(HostingProviderKind.GitHub, provider.Kind);
    }

    [Fact]
    public async Task The_provider_of_the_container_launches_gh_only_through_its_queue_with_the_folders_under_the_local_base()
    {
        var script = new ScriptedGh().Route("GET", "/user", "handmade-user-200.http");
        using var container = Container(script, new ScriptedDoor().Says(DoctorTool.GhVersion, "gh version 2.96.0 (2026-07-02)\n"));
        var provider = container.GetRequiredService<IHostingProvider>();
        var expected = System.IO.Path.Combine(_temp.Combine("local"), "hosting", "cwd");

        // The runner of the container is the scripted one, so nothing real starts; whether the answer is the account or «gh is not installed» depends on whether this computer has a gh.exe on its PATH.
        var result = await provider.ReadAccountAsync(GhRig.Repo, CancellationToken.None);

        Assert.Equal(expected, GhFoldersOf(container).WorkingFolder);
        if (result.Outcome == HostingOutcome.Ok)
        {
            var spec = Assert.Single(script.Specs);
            Assert.Equal(expected, spec.WorkingDirectory);
            Assert.EndsWith("gh.exe", spec.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("api", spec.Arguments[0]);
        }
        else
        {
            Assert.Equal(HostingOutcome.Unsupported, result.Outcome);
            Assert.Equal(GitHubRefusal.GhNotInstalled, result.RefusalCode);
            Assert.Empty(script.Specs);
        }
    }
    private static AgentStudio.Infrastructure.Hosting.Cli.GhFolders GhFoldersOf(ServiceProvider container) =>
        container.GetRequiredService<AgentStudio.Infrastructure.Hosting.Cli.GhFolders>();

    // ---- The version goes through the door and nowhere else ----

    [Fact]
    public async Task The_version_of_gh_is_one_launch_with_the_five_variables_and_the_neutral_folder_of_the_door_for_the_probe_and_for_auto_doctor_alike()
    {
        var recorder = new RecordingProcessRunner(_ => RecordingProcessRunner.Ok("gh version 2.96.0 (2026-07-02)\r\nhttps://github.com/cli/cli/releases/tag/v2.96.0\r\n"));
        var folders = DoctorFolders.Under(_temp.Combine("doctor"));
        var ambient = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GH_TOKEN"] = "sentinel", ["GH_HOST"] = "evil.example", ["GH_TELEMETRY"] = "true" };
        await using var door = new DoctorToolRunner(new FakeLocator(c => @"C:\Tools\" + c + ".exe"), recorder, folders, () => ambient, () => false);
        var gh = new GhRig();
        using var rig = gh;
        var provider = new GitHubHostingProvider(rig.Queue, door);

        var availability = await provider.ProbeAsync(CancellationToken.None);
        var checks = await new GhToolCheckSource(door).CheckAsync(Project, CancellationToken.None);

        Assert.True(availability.Installed);
        Assert.Equal(DoctorStatus.Ok, checks.Single(c => c.Code == "doctor.gh.installed").Status);
        Assert.Equal(2, recorder.Specs.Count);
        Assert.All(recorder.Specs, spec =>
        {
            Assert.Equal(["--version"], spec.Arguments);
            Assert.Equal(folders.WorkingFolder, spec.WorkingDirectory);
            Assert.Null(spec.StandardInput);
            foreach (var (name, value) in DoctorEnvironment.GhOffline)
            {
                Assert.Equal(value, spec.Environment![name]);
            }

            Assert.True(spec.Environment!["GH_TOKEN"] is null && spec.Environment["GH_HOST"] is null);
            Assert.Equal("false", spec.Environment["GH_TELEMETRY"]);
        });
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task The_source_of_the_project_launches_nothing_at_all()
    {
        using var rig = new GhRig();
        var remotes = new Remotes(Listing(("origin", "https://github.com/octo/hello.git")));

        await new HostingCheckSource(remotes).CheckAsync(Project, CancellationToken.None);

        Assert.Empty(rig.Script.Specs);
        Assert.Empty(rig.Door.Asked);
    }
}
