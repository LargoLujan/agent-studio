using System.IO;
using System.Windows;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Controls.Terminal;
using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Desktop.Views.Documents;
using AgentStudio.Desktop.Views.Setup;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Memory.Documents;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Memory.Documents;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>The screens of the shell: the main window, the file tab, the configurator, the documents for clients and the small windows.</summary>
internal static class UiScreensShell
{
    private static IProviderProbe[] Providers() =>
    [
        new StubProbe("claude-code", InstallationState.Installed, AuthenticationState.Authenticated),
        new StubProbe("codex-cli", InstallationState.Installed, AuthenticationState.Authenticated),
        new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null),
    ];

    /// <summary>The terminal only starts a shell once its control is loaded, which the inventory never does.</summary>
    private sealed class NoLauncher : ITerminalLauncher
    {
        public ITerminalSession Start(TerminalStartInfo info) => throw new InvalidOperationException("No shell should start.");
    }

    private sealed class NoShells : IShellLocator
    {
        public IReadOnlyList<ShellInfo> GetAvailableShells() => [];
    }

    /// <summary>The folder the shell screens read, kept for the whole run of the tests.</summary>
    private static readonly TempDirectory Folder = CreateFolder();

    private static TempDirectory CreateFolder()
    {
        var folder = new TempDirectory();
        folder.CreateDirectory("calc");
        folder.WriteFile("calc/src/Calc.cs", "public static class Calc { }");
        folder.WriteFile("calc/README.md", "# Calc");
        folder.WriteFile("calc/main.cs", "class A {}\r\n");
        folder.WriteFile("calc/notes.txt", "one\r\ntwo\r\n");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => folder.Dispose();
        return folder;
    }

    private static string Calc => Path.Combine(Folder.Path, "calc");

    private static async Task<SetupViewModel> SetupAsync()
    {
        var roster = new InMemoryRoster(SetupTeams.Default());
        var service = new ProjectSetupService(
            new FakeRepositoryReader(SetupFakes.Facts(["App.slnx", "src/App.csproj"])),
            roster,
            new FakeProfiles(),
            new FakeOptionsStore(),
            new RecordingRecorder(),
            new FakeBrief(),
            new FakeSkillCatalog(FakeSkillCatalog.Skill("dotnet-testing", ["dotnet", "testing"])),
            new FakeActivations(),
            requirements: null,
            models: new BuiltInModelCatalog(),
            learning: null,
            gate: new FakeGate(true));
        var vm = new SetupViewModel(service);
        await vm.LoadAsync(SetupFakes.Project);
        return vm;
    }

    private static async Task<ClientDocumentsViewModel> ClientDocumentsAsync()
    {
        var vm = new ClientDocumentsViewModel(
            new Workspace(),
            new Builder(),
            new Exporter(),
            [new MarkdownDocumentWriter(new ResourceUserTextRenderer(), new SecretRedactor(@"C:\Users\persona"))],
            null);
        await vm.SetProjectAsync(@"C:\x\proyecto");
        vm.Destination = @"C:\salida";
        await vm.GenerateCommand.ExecuteAsync(null);
        return vm;
    }

    private static async Task<FileDocumentViewModel> FileAsync(string name)
    {
        var document = new FileDocumentViewModel(Path.Combine(Calc, name));
        await document.LoadAsync(CancellationToken.None);
        return document;
    }

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Main window with a project", async () =>
        {
            var vm = ShellFactory.CreateMain(null, null, null, Providers());
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(Path.Combine(Folder.Path, "data"))), new TerminalViewModel(new NoLauncher(), new NoShells()));
            await vm.InitializeAsync();
            await vm.OpenProjectAsync(Calc);
            await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);
            vm.ShowWelcomeCommand.Execute(null);
            return UiScreens.OfWindow(window);
        }, 1280, 800),
        UiScreens.ScreenAsync("Source file", async () => UiScreens.Of(new FileEditorView(), await FileAsync("main.cs")), 1100, 760),
        UiScreens.ScreenAsync("Configurator", async () => UiScreens.Of(new SetupView(), await SetupAsync()), 1000, 800),
        UiScreens.ScreenAsync("Client documents", async () => UiScreens.Of(new ClientDocumentsView(), await ClientDocumentsAsync()), 1100, 900),
        UiScreens.ScreenAsync("Quick open window", async () =>
        {
            var vm = new QuickOpenViewModel();
            var window = new QuickOpenWindow { DataContext = vm };
            await vm.OpenAsync(Calc, "Ir al fichero", _ => Task.CompletedTask);
            return new UiBuilt((FrameworkElement)window.Content, window);
        }, 640, 420),
        UiScreens.Screen("Unsaved changes window", () => UiScreens.OfWindow(new UnsavedChangesWindow(), new UnsavedChangesPromptViewModel(["Program.cs"], canSave: false)), 520, 360),
        UiScreens.Screen("Terminal", () => UiScreens.Of(new TerminalControl(), new TerminalViewModel(new NoLauncher(), new NoShells())), 900, 320),
        UiScreens.ScreenAsync("Auto Doctor", async () =>
        {
            var diagnostics = new ProviderDiagnosticsViewModel(new ProviderDiagnostics(Providers()), TimeProvider.System);
            await diagnostics.RefreshCommand.ExecuteAsync(null);
            return UiScreens.Of(new AutoDoctorView(), diagnostics);
        }, 340, 400),
    ];

    private sealed class Workspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new ProjectContext("p-1", @"C:\x\proyecto"));
    }

    private sealed class Builder : AgentStudio.Application.Documents.IClientDocumentBuilder
    {
        public Task<AgentStudio.Application.Documents.ClientDocumentBuild> BuildAsync(AgentStudio.Application.Documents.ClientDocumentBuildRequest request, CancellationToken ct) =>
            Task.FromResult(GoldenModel.Prepared());
    }

    private sealed class Exporter : AgentStudio.Application.Documents.IClientDocumentExporter
    {
        public Task<AgentStudio.Application.Documents.ClientDocumentExportResult> ExportAsync(AgentStudio.Application.Documents.ClientDocumentExportRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("The inventory never exports.");
    }
}
