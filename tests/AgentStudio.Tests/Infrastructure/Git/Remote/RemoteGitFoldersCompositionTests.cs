using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>P71: composed executors keep their files and process paths in the host's local folders, without launching git.</summary>
public sealed class RemoteGitFoldersCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_private_local_base_contains_the_hooks_file_and_temporary_folder_of_every_inspection_command(bool composeApp)
    {
        using var temp = new TempDirectory();
        using var fixture = new RemoteGitFixture();
        var localBase = temp.Combine("local");
        var script = new RemoteGitScript();
        var locator = new FixedLocator(fixture.GitPath);
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(script);
        services.AddSingleton<IExecutableLocator>(locator);
        if (composeApp)
        {
            var folders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(localBase), null);
            services.AddWindowsDoubles().AddTestLocalData(localBase).AddAgentStudioApp(folders);

            // Replace only the app's process boundaries; the executor and its folder selection stay real.
            services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(script));
            services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(locator));
        }
        else
        {
            services.AddSingleton(new GitWorktreeOptions(fixture.WorktreeRoot));
            services.AddAgentStudioRemoteGit(localBase);
        }

        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IRemoteGitRunner>();

        var inspection = await runner.InspectAsync(fixture.Project, RemoteGitFixture.Repository, TestContext.Current.CancellationToken);

        Assert.True(inspection.Accepted);
        Assert.Same(script, provider.GetRequiredService<IProcessRunner>());
        Assert.Same(locator, provider.GetRequiredService<IExecutableLocator>());
        Assert.Same(RemoteTransport.GitHubHttps, Assert.IsType<RemoteGitRunner>(runner).Transport);
        var hooksFile = Path.Combine(localBase, "hosting", "no-hooks");
        var temporaryFolder = Path.Combine(localBase, "hosting", "tmp");
        Assert.True(File.Exists(hooksFile));
        Assert.Equal(0L, new FileInfo(hooksFile).Length);
        Assert.True(Directory.Exists(temporaryFolder));
        Assert.NotEmpty(script.Specs);
        Assert.All(script.Specs, spec =>
        {
            Assert.Equal("core.hooksPath=" + hooksFile,
                Assert.Single(RemoteGitCommands.Settings(spec), setting => setting.StartsWith("core.hooksPath=", StringComparison.Ordinal)));
            Assert.NotNull(spec.Environment);
            var environment = spec.Environment!;
            Assert.Equal(temporaryFolder, environment["TEMP"]);
            Assert.Equal(temporaryFolder, environment["TMP"]);
            RemoteGitCommandLineTests.AssertClosed(spec);
        });
    }
}
