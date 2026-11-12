using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Tests.Application.Diagnostics;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of the panel of Auto Doctor (Phase 6, docs/20 section 6.1; WP-A): the panel as the dock shows it, with the five sections over scripted sources, a guide
/// open, and the table of the declared environment, which WP-E fills with its own checks. The data carries no words of either language, so the check of the
/// language reads only the interface.
/// </summary>
internal static class UiScreensDoctor
{
    private sealed class OpenProjects : IOpenProjects
    {
        public ProjectContext? Find(string projectRoot) => new("p-1", projectRoot);
    }

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Auto Doctor", async () => UiScreens.Of(new AutoDoctorView(), await PanelAsync()), 340, 640),
        UiScreens.ScreenAsync("Auto Doctor wide with a guide open", async () =>
        {
            var doctor = await PanelAsync();
            foreach (var check in doctor.Sections.SelectMany(s => s.Checks).Where(c => c.FixKind == DoctorFixKind.Guide))
            {
                check.RunFixCommand.Execute(null);
            }

            return UiScreens.Of(new AutoDoctorView(), doctor);
        }, 900, 900),
        UiScreens.ScreenAsync("Auto Doctor declared environment", async () =>
        {
            var source = new ScriptedSource(DoctorSection.DeclaredEnvironment, _ =>
            [
                new DoctorCheck(
                    "doctor.env.sample-sdk",
                    DoctorSection.DeclaredEnvironment,
                    DoctorStatus.Ok,
                    [new DoctorFact(DoctorFactNames.Declared, "10.0.303"), new DoctorFact(DoctorFactNames.Source, "global.json:3"), new DoctorFact(DoctorFactNames.Installed, "10.0.303")]),
                new DoctorCheck(
                    "doctor.env.sample-node",
                    DoctorSection.DeclaredEnvironment,
                    DoctorStatus.Error,
                    [new DoctorFact(DoctorFactNames.Declared, ">=99"), new DoctorFact(DoctorFactNames.Source, ".nvmrc:1"), new DoctorFact(DoctorFactNames.Installed, "24.14.0")]),
            ]);
            var doctor = Panel(new DoctorService([source]), null);
            await doctor.RefreshCommand.ExecuteAsync(null);
            return UiScreens.Of(new AutoDoctorView(), doctor);
        }, 600, 400),
        UiScreens.ScreenAsync("Auto Doctor providers open with their guides", async () =>
        {
            // A program of an agent that is not installed and another without a session: the rows of today, and under them the two guides, open.
            IProviderProbe[] probes =
            [
                new StubProbe("claude-code", InstallationState.NotInstalled, AuthenticationState.Unknown, method: null),
                new StubProbe("codex-cli", InstallationState.Installed, AuthenticationState.NotAuthenticated, method: null),
                new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Authenticated),
            ];
            var diagnostics = new ProviderDiagnostics(probes);
            var providers = new ProviderDiagnosticsViewModel(diagnostics, TimeProvider.System);
            var source = new ProviderCheckSource(diagnostics);
            var doctor = Panel(new DoctorService([source]), source, providers);
            await doctor.RefreshCommand.ExecuteAsync(null);
            var section = doctor.Sections.Single(s => s.IsProviders);
            section.IsExpanded = true;
            foreach (var check in section.ActionChecks)
            {
                check.RunFixCommand.Execute(null);
            }

            return UiScreens.Of(new AutoDoctorView(), doctor);
        }, 700, 900),
        UiScreens.ScreenAsync("Auto Doctor providers only", async () =>
        {
            // A shell built by hand: the panel has no service of checks and shows the providers as before Phase 6.
            var providers = new ProviderDiagnosticsViewModel(new ProviderDiagnostics(UiScreensShell.Providers()), TimeProvider.System);
            var doctor = new DoctorViewModel(null, providers, null, null, TimeProvider.System);
            await doctor.RefreshCommand.ExecuteAsync(null);
            return UiScreens.Of(new AutoDoctorView(), doctor);
        }, 340, 400),
    ];

    private static async Task<DoctorViewModel> PanelAsync()
    {
        var providers = new ProviderDiagnosticsViewModel(new ProviderDiagnostics(UiScreensShell.Providers()), TimeProvider.System);
        var providerSource = new ProviderCheckSource(
            new ProviderDiagnostics(UiScreensShell.Providers()),
            new ProviderCheckOptions(IsParked: r => r.Provider.Value == "gemini-cli"));

        // A computer where Docker is installed with the engine stopped and WSL has only the distribution of Docker; a project that declares containers.
        var snapshots = new ToolSnapshotProvider(ScriptedDoor.LikeTheDevelopmentMachine(), new FixedWebView2("130.0.2849.80"));
        var probe = new FakeEnvironmentProbe { Containers = true, Layout = new ProjectLayout(true, true, ["hook-a"]) };
        var project = new ProjectCheckSource(
            new AuditRepository("a".PadRight(40, 'a')),
            new FakeRemotes(),
            new LogRecorder(),
            new FakeProfiles { Stored = SetupFakes.Profile("dotnet") },
            snapshots,
            probe);
        var service = new DoctorService([providerSource, new ToolsCheckSource(snapshots), project, new ContainersCheckSource(snapshots, probe)], [snapshots]);
        var doctor = Panel(service, providerSource, providers);
        await doctor.RefreshCommand.ExecuteAsync(null);
        return doctor;
    }

    private static DoctorViewModel Panel(IDoctorService service, ProviderCheckSource? providerSource, ProviderDiagnosticsViewModel? providers = null)
    {
        providers ??= new ProviderDiagnosticsViewModel(new ProviderDiagnostics(UiScreensShell.Providers()), TimeProvider.System);
        var doctor = new DoctorViewModel(service, providers, providerSource, new DoctorFixRunner(), TimeProvider.System, new OpenProjects(), new NoActions())
        {
            ProjectRoot = () => @"C:\x\proyecto",
            Navigation = new DoctorNavigation(new HashSet<string> { "setup", "settings", "skills" }, _ => { }),
        };
        return doctor;
    }

    private sealed class NoActions : IDoctorActions
    {
        public void Copy(string text)
        {
        }

        public void OpenPage(Uri page)
        {
        }
    }
}
