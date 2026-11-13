using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Environments;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Doctor;

/// <summary>Auto Doctor in the composition the app builds at startup (docs/20 section 9.3): the pieces resolve, the folders of the door live under the local folder of the app, and the shell holds the panel.</summary>
public sealed class DoctorCompositionTests
{
    private static ServiceProvider Build(TempDirectory temp)
    {
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_service_the_door_the_sources_and_the_runner_of_fixes_resolve_with_the_sections_of_this_package_and_the_declared_environment()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        Assert.IsType<DoctorService>(provider.GetRequiredService<IDoctorService>());
        Assert.IsType<DoctorToolRunner>(provider.GetRequiredService<IDoctorToolRunner>());
        Assert.IsType<OpenProjectLookup>(provider.GetRequiredService<IOpenProjects>());
        Assert.IsType<DoctorFixRunner>(provider.GetRequiredService<IDoctorFixRunner>());
        Assert.Same(provider.GetRequiredService<ToolSnapshotProvider>(), provider.GetRequiredService<IInstalledToolInventory>());
        Assert.Contains(provider.GetServices<IDoctorRunScope>(), s => ReferenceEquals(s, provider.GetRequiredService<ToolSnapshotProvider>()));
        var sections = provider.GetServices<IDoctorCheckSource>().Select(s => s.Section).Order().ToArray();
        // «Proyecto» has two sources: its own and the line of the discrepancies (WP-D, docs/20 D-30), which reads what Auto Graph saved.
        Assert.Equal([DoctorSection.Providers, DoctorSection.Tools, DoctorSection.Project, DoctorSection.Project, DoctorSection.DeclaredEnvironment, DoctorSection.Containers], sections);
    }

    [Fact]
    public void The_declared_environment_is_composed_after_the_inventory_it_compares_with_and_its_reader_of_versions_reaches_the_runner_of_verification()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        // The source of the declared environment is the one of WP-E, built over the inventory of WP-A (one object for the sources of tools and the comparison).
        Assert.Single(provider.GetServices<IDoctorCheckSource>(), s => s is DeclaredEnvironmentCheckSource);
        Assert.Same(provider.GetRequiredService<ToolSnapshotProvider>(), provider.GetRequiredService<IInstalledToolInventory>());
        Assert.IsType<DeclaredEnvironmentReader>(provider.GetRequiredService<IDeclaredEnvironmentReader>());

        // The block «tools» of the proof of a step needs this reader in the runner the app really uses, not only in the container.
        var reader = Assert.IsType<StepToolVersionReader>(provider.GetRequiredService<IStepToolVersionReader>());
        var runner = Assert.IsType<VerificationRunner>(provider.GetRequiredService<IVerificationRunner>());
        var held = typeof(VerificationRunner).GetField("_toolVersions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(held);
        Assert.Same(reader, held.GetValue(runner));
    }

    [Fact]
    public void The_folders_of_the_door_are_under_the_local_folder_of_the_app_and_never_in_a_project()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        var folders = provider.GetRequiredService<DoctorFolders>();

        Assert.Equal(temp.Combine("local", "doctor", "cwd"), folders.WorkingFolder);
        Assert.Equal(temp.Combine("local", "doctor", "tmp"), folders.TempFolder);
        Assert.Equal(temp.Combine("local", "doctor", "docker-config"), folders.DockerConfigFolder);
    }

    [Fact]
    public void The_shell_holds_the_panel_the_container_gives_and_it_follows_the_open_project()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        var main = provider.GetRequiredService<MainViewModel>();

        Assert.Same(provider.GetRequiredService<DoctorViewModel>(), main.Doctor);
        Assert.Same(main.Diagnostics, main.Doctor.Providers);
        Assert.Null(main.Doctor.ProjectRoot!());
        Assert.NotNull(main.Doctor.Navigation);
        Assert.Contains("skills", main.Doctor.Navigation.Targets);
    }

    [Fact]
    public void A_shell_built_by_hand_still_has_a_panel_with_the_providers_alone()
    {
        var main = ShellFactory.CreateMain();

        Assert.NotNull(main.Doctor);
        Assert.Same(main.Diagnostics, main.Doctor.Providers);
    }
}
