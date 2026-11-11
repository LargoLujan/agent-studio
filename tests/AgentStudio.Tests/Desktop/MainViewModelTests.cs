using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Initialize_ShowsTheWelcomeTabWithRecentProjects()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var store = new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(project) with { LastProjectPath = null });
        var vm = ShellFactory.CreateMain(store);

        await vm.InitializeAsync();

        var welcome = Assert.IsType<WelcomeDocumentViewModel>(Assert.Single(vm.Documents));
        Assert.Same(welcome, vm.ActiveContent);
        Assert.Equal([project], welcome.RecentProjects.Select(r => r.Path));
        Assert.False(vm.HasProject);
        Assert.Equal("Sin proyecto", vm.ProjectName);
    }

    [Fact]
    public async Task Initialize_ReopensTheLastProjectIfItStillExists()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        temp.WriteFile("proj/readme.md");
        var vm = ShellFactory.CreateMain(new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(project)));

        await vm.InitializeAsync();

        Assert.Equal(project, vm.ProjectPath);
        Assert.Equal("proj", vm.ProjectName);
        Assert.Equal(["readme.md"], vm.Explorer.Roots.Single().Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Initialize_ForgetsALastProjectWhoseFolderVanished()
    {
        using var temp = new TempDirectory();
        var gone = temp.CreateDirectory("gone");
        var store = new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(gone));
        Directory.Delete(gone);
        var vm = ShellFactory.CreateMain(store);

        await vm.InitializeAsync();

        Assert.False(vm.HasProject);
        Assert.Empty(vm.RecentProjects);
        Assert.Null(store.Current.LastProjectPath);
    }

    [Fact]
    public async Task OpenFolder_LoadsTheProjectRemembersItAndAnnouncesTheChange()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var store = new InMemorySettingsStore();
        var picker = new FakeFolderPicker { Result = project };
        var vm = ShellFactory.CreateMain(store, picker);
        var events = new List<string>();
        vm.ProjectChanging += (_, old) => events.Add($"changing:{old ?? "none"}");
        vm.ProjectChanged += (_, now) => events.Add($"changed:{now}");
        await vm.InitializeAsync();

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(project, vm.ProjectPath);
        Assert.Equal(project, vm.Explorer.RootPath);
        Assert.Equal(project, store.Current.LastProjectPath);
        Assert.Equal([project], store.Current.RecentProjects);
        Assert.Equal([project], vm.RecentProjects.Select(r => r.Path));
        Assert.Equal(["changing:none", $"changed:{project}"], events);
    }

    [Fact]
    public async Task OpenFolder_Cancelled_ChangesNothing()
    {
        var store = new InMemorySettingsStore();
        var picker = new FakeFolderPicker { Result = null };
        var vm = ShellFactory.CreateMain(store, picker);
        await vm.InitializeAsync();

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.Calls);
        Assert.False(vm.HasProject);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task OpenRecent_OnAMissingFolder_ReportsItAndDropsItFromTheList()
    {
        using var temp = new TempDirectory();
        var gone = temp.CreateDirectory("gone");
        var store = new InMemorySettingsStore(AppSettings.Default.WithProjectOpened(gone) with { LastProjectPath = null });
        var vm = ShellFactory.CreateMain(store);
        await vm.InitializeAsync();
        Directory.Delete(gone);

        await vm.OpenRecentCommand.ExecuteAsync(gone);

        Assert.False(vm.HasProject);
        Assert.Contains("ya no existe", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.RecentProjects);
        Assert.Empty(store.Current.RecentProjects);
    }

    [Fact]
    public async Task OpeningElevenProjects_KeepsTenMostRecentFirst()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore();
        var vm = ShellFactory.CreateMain(store);
        await vm.InitializeAsync();
        var paths = Enumerable.Range(1, 11).Select(i => temp.CreateDirectory($"p{i:00}")).ToList();

        foreach (var path in paths)
        {
            await vm.OpenProjectAsync(path);
        }

        Assert.Equal(10, vm.RecentProjects.Count);
        Assert.Equal(paths[^1], vm.RecentProjects[0].Path);
        Assert.Equal(paths[1], vm.RecentProjects[^1].Path);
    }

    [Fact]
    public async Task SwitchingProject_ClosesFileTabsButKeepsTheWelcomeTab()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        var file = temp.WriteFile("a/x.txt", "x");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        await vm.OpenProjectAsync(a);
        await vm.OpenFileAsync(file);
        Assert.Equal(2, vm.Documents.Count);

        await vm.OpenProjectAsync(b);

        Assert.IsType<WelcomeDocumentViewModel>(Assert.Single(vm.Documents));
    }

    [Fact]
    public async Task OpenFile_AddsATabOnceAndReactivatesIt()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("notes.txt", "hola");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();

        await vm.OpenFileAsync(file);
        var document = Assert.IsType<FileDocumentViewModel>(vm.Documents.Last());
        vm.ActiveContent = vm.Documents[0];
        await vm.OpenFileAsync(file);

        Assert.Equal(2, vm.Documents.Count);
        Assert.Same(document, vm.ActiveContent);
        Assert.Equal("notes.txt", document.Title);
        Assert.Equal("hola", document.Text);
    }

    [Fact]
    public async Task ExplorerActivation_OpensTheFile()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var file = temp.WriteFile("proj/a.txt", "contenido");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        await vm.OpenProjectAsync(project);

        vm.Explorer.OpenCommand.Execute(vm.Explorer.Roots.Single().Children.Single());

        var document = Assert.IsType<FileDocumentViewModel>(vm.Documents.Last());
        Assert.Equal(file, document.FilePath);
        // The file is read in the background after the tab is added.
        for (var i = 0; i < 100 && document.Text.Length == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal("contenido", document.Text);
    }

    [Fact]
    public async Task ClosingADocument_RemovesItAndActivatesTheLastOne()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("a.txt");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        await vm.OpenFileAsync(file);
        var document = vm.Documents.Last();

        document.CloseCommand.Execute(null);

        Assert.DoesNotContain(document, vm.Documents);
        Assert.IsType<WelcomeDocumentViewModel>(vm.ActiveContent);
    }

    [Fact]
    public async Task ShowWelcome_ReopensAClosedWelcomeTabWithoutDuplicates()
    {
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        vm.Documents[0].CloseCommand.Execute(null);
        Assert.Empty(vm.Documents);

        vm.ShowWelcomeCommand.Execute(null);
        vm.ShowWelcomeCommand.Execute(null);

        Assert.Single(vm.Documents);
    }

    /// <summary>The bar offers only what works: no entry waits for a phase (Agentes and Pruebas were dead icons), and the terminal has one.</summary>
    [Fact]
    public void Activities_OfferOnlyWhatWorks_AndTheTerminalWithItsShortcut()
    {
        var vm = ShellFactory.CreateMain();

        var byId = vm.Activities.ToDictionary(a => a.Id);

        // Phase 13: «Agentes» (the layer Team of the orchestrator), «Herramientas» (a screen) and «Pruebas» (the panel of tests).
        Assert.Equal(
            [
                "Explorador", "Orquestador", Texts.Get("Shell_Activity_Agents"), "Skills", Texts.Get("Shell_Activity_Tools"), Texts.Get("Shell_Activity_Tests"),
                "Terminal", "Auto Doctor", "Historial (bitácora)",
            ],
            vm.Activities.Select(a => a.Title));
        Assert.All(vm.Activities, a => Assert.True(a.IsEnabled));
        Assert.All(vm.Activities, a => Assert.Null(a.AvailableInPhase));
        Assert.Equal(PanelIds.Tests, byId["tests"].PanelId);
        Assert.Null(byId["agents"].PanelId);
        Assert.Null(byId["tools"].PanelId);

        // Phase 4 delivered the skills screen: it opens a tab, not a panel.
        Assert.Null(byId["skills"].PanelId);
        Assert.Equal("Skills", byId["skills"].ToolTip);

        // The terminal panel can always come back, from the bar or with Ctrl+Ñ (Ctrl+` on a US keyboard, the same key).
        Assert.Equal(PanelIds.Terminal, byId["terminal"].PanelId);
        Assert.Equal("Terminal (Ctrl+Ñ, o Ctrl+` en un teclado de EE. UU.)", byId["terminal"].ToolTip);
    }

    [Fact]
    public void Activities_ThePanelOnesToggleTheirPanel()
    {
        var vm = ShellFactory.CreateMain();
        var requested = new List<string>();
        vm.PanelToggleRequested += (_, id) => requested.Add(id);

        foreach (var activity in vm.Activities)
        {
            activity.Command.Execute(activity);
        }

        Assert.Equal([PanelIds.Explorer, PanelIds.Tests, PanelIds.Terminal, PanelIds.AutoDoctor, PanelIds.Journal], requested);
        Assert.All(vm.Activities.Where(a => a.Shortcut is null), a => Assert.Equal(a.Title, a.ToolTip));
    }

    [Fact]
    public void TheTerminalShortcut_AndTheProviderSummary_ToggleTheirPanels()
    {
        var vm = ShellFactory.CreateMain();
        var requested = new List<string>();
        vm.PanelToggleRequested += (_, id) => requested.Add(id);

        vm.ToggleTerminalCommand.Execute(null);
        vm.ToggleAutoDoctorCommand.Execute(null);

        Assert.Equal([PanelIds.Terminal, PanelIds.AutoDoctor], requested);
    }

    /// <summary>A screen reader names the entries of the bar by their title, not by the type behind them.</summary>
    [Fact]
    public void Activities_AreNamedByTheirTitle()
    {
        var vm = ShellFactory.CreateMain();

        Assert.All(vm.Activities, a => Assert.Equal(a.Title, a.ToString()));
    }

    [Theory]
    [InlineData("1.0.0+87f7a07ea0a72383bdf19e3308374f1f7b8cc62e", "v1.0.0 · 87f7a07")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3+abc", "v1.2.3")]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    public void TheVersionOfTheStatusBar_SaysTheVersionAndTheCommit(string? informational, string expected) =>
        Assert.Equal(expected, MainViewModel.FormatVersion(informational));

    [Fact]
    public void TheVersionOfTheStatusBar_IsTheOneOfThisBuild()
    {
        var vm = ShellFactory.CreateMain();

        Assert.StartsWith("v", vm.AppVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("Fase", vm.AppVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWelcomeTab_WithoutAProject_OffersToOpenOne_AndWithOne_TheWaysIntoIt()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(Assert.Single(vm.Documents));
        Assert.False(welcome.HasProject);

        await vm.OpenProjectAsync(project);

        Assert.True(welcome.HasProject);
        Assert.Equal("calc", welcome.ProjectName);
        Assert.Equal(vm.ProjectPathDisplay, welcome.ProjectPathDisplay);

        // Without the screens of the debates, the usage and the memory (a shell built by hand) only what was composed is offered.
        Assert.Equal(["Orquestador", "Ir al fichero", "Terminal"], welcome.ProjectActions.Select(a => a.Title));
        Assert.Equal([null, "Ctrl+P", "Ctrl+Ñ"], welcome.ProjectActions.Select(a => a.Shortcut));
        Assert.Same(vm.OpenOrchestratorCommand, welcome.ProjectActions[0].Command);
        Assert.Same(vm.GoToFileCommand, welcome.ProjectActions[1].Command);
        Assert.Same(vm.ToggleTerminalCommand, welcome.ProjectActions[2].Command);
        Assert.All(welcome.ProjectActions, a => Assert.Equal(a.Title, a.ToString()));
    }

    [Fact]
    public async Task TheWelcomeTab_ReopenedWithAProject_ShowsIt()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        var vm = ShellFactory.CreateMain();
        await vm.OpenProjectAsync(project);

        vm.ShowWelcomeCommand.Execute(null);

        var welcome = Assert.IsType<WelcomeDocumentViewModel>(vm.ActiveContent);
        Assert.Equal("calc", welcome.ProjectName);
    }

    private static Task OpenOrchestratorAsync(MainViewModel vm)
    {
        var activity = vm.Activities.Single(a => a.Id == "orchestrator");
        return ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);
    }

    [Fact]
    public async Task TheOrchestratorActivity_OpensTheOrchestratorTabOfTheOpenProjectOnce()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var vm = ShellFactory.CreateMain();
        await vm.OpenProjectAsync(project);

        await OpenOrchestratorAsync(vm);

        var tab = Assert.IsType<OrchestratorDocumentViewModel>(vm.ActiveContent);
        Assert.Contains(tab, vm.Documents);
        Assert.Equal(project, tab.ProjectRoot);
        Assert.Equal("Orquestador · proj", tab.Title);

        // A shell built by hand, without the composition root, has no engine: the tab says so instead of pretending.
        Assert.Equal("Motor no disponible", tab.EngineStatus);
        Assert.False(tab.ExecuteCommand.CanExecute(null));

        vm.ActiveContent = vm.Documents[0];
        await OpenOrchestratorAsync(vm);

        Assert.Same(tab, vm.ActiveContent);
        Assert.Single(vm.Documents.OfType<OrchestratorDocumentViewModel>());
    }

    [Fact]
    public async Task TheOrchestratorActivity_WithoutAProject_SaysWhatToDo()
    {
        var vm = ShellFactory.CreateMain();

        await OpenOrchestratorAsync(vm);

        Assert.Equal("Abre una carpeta de proyecto para usar el orquestador.", vm.StatusMessage);
        Assert.Empty(vm.Documents.OfType<OrchestratorDocumentViewModel>());
    }

    [Fact]
    public async Task SwitchingProject_ClosesAnIdleOrchestratorTab_AndKeepsItsCanvasForLater()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        var vm = ShellFactory.CreateMain();
        await vm.OpenProjectAsync(a);
        await OpenOrchestratorAsync(vm);
        var tabOfA = Assert.IsType<OrchestratorDocumentViewModel>(vm.ActiveContent);
        tabOfA.Canvas.AddNodeCommand.Execute(AgentStudio.Domain.Work.WorkTaskKind.AgentWork);

        await vm.OpenProjectAsync(b);

        Assert.DoesNotContain(tabOfA, vm.Documents);

        await vm.OpenProjectAsync(a);
        await OpenOrchestratorAsync(vm);

        Assert.Same(tabOfA, vm.ActiveContent);
        Assert.Single(tabOfA.Canvas.Nodes);
    }

    [Fact]
    public async Task ProviderSummary_FollowsTheDiagnostics()
    {
        var vm = ShellFactory.CreateMain(
            null,
            null,
            null,
            new StubProbe("claude", InstallationState.Installed, AuthenticationState.Authenticated),
            new StubProbe("codex", InstallationState.NotInstalled, AuthenticationState.Unknown));
        Assert.Equal("Programas: sin comprobar", vm.ProviderSummary.Text);

        await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Programas: 1 de 2 listos", vm.ProviderSummary.Text);
        Assert.Equal(StatusKind.Warning, vm.ProviderSummary.Status);
    }

    [Fact]
    public async Task ProviderSummary_AllReadyIsOk_AnyErrorIsError()
    {
        var ok = ShellFactory.CreateMain(null, null, null, new StubProbe("claude", InstallationState.Installed, AuthenticationState.Authenticated));
        await ok.Diagnostics.RefreshCommand.ExecuteAsync(null);
        var failing = ShellFactory.CreateMain(null, null, null, new StubProbe("codex", InstallationState.Installed, AuthenticationState.NotAuthenticated));
        await failing.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(StatusKind.Ok, ok.ProviderSummary.Status);
        Assert.Equal(StatusKind.Error, failing.ProviderSummary.Status);
    }

    /// <summary>Gemini is parked (ADR-0009): it neither counts nor turns the summary to a warning, and its check is still shown.</summary>
    [Fact]
    public async Task ProviderSummary_DoesNotCountAParkedProvider_AndIsGreenWhenTheOthersAreReady()
    {
        var vm = ShellFactory.CreateMain(
            null,
            null,
            null,
            new StubProbe("claude-code", InstallationState.Installed, AuthenticationState.Authenticated),
            new StubProbe("codex-cli", InstallationState.Installed, AuthenticationState.Authenticated),
            new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null));

        await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Programas: 2 de 2 listos", vm.ProviderSummary.Text);
        Assert.Equal(StatusKind.Ok, vm.ProviderSummary.Status);
        var gemini = Assert.Single(vm.Diagnostics.Providers, p => p.IsParked);
        Assert.Equal(("gemini-cli", StatusKind.Parked, "Instalado"), (gemini.Name, gemini.Status, gemini.Installation));
    }

    [Fact]
    public async Task ProviderSummary_AParkedProviderThatFails_IsStillNotAWarning_ButOneInUseIs()
    {
        var vm = ShellFactory.CreateMain(
            null,
            null,
            null,
            new StubProbe("claude-code", InstallationState.Installed, AuthenticationState.Authenticated),
            new StubProbe("codex-cli", InstallationState.NotInstalled, AuthenticationState.Unknown),
            new StubProbe("gemini-cli", InstallationState.Error, AuthenticationState.Unknown, method: null));

        await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Programas: 1 de 2 listos", vm.ProviderSummary.Text);
        Assert.Equal(StatusKind.Warning, vm.ProviderSummary.Status);
    }

    [Fact]
    public async Task ProviderSummary_WithOnlyParkedProviders_SaysSo()
    {
        var vm = ShellFactory.CreateMain(null, null, null, new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null));

        await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Programas: todos aparcados", vm.ProviderSummary.Text);
        Assert.Equal(StatusKind.Warning, vm.ProviderSummary.Status);
    }

    [Fact]
    public async Task ProviderSummary_WithoutProviders_SaysSoAfterTheCheck()
    {
        var vm = ShellFactory.CreateMain();

        await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Sin programas registrados", vm.ProviderSummary.Text);
    }
}

public sealed class FileDocumentViewModelTests
{
    [Fact]
    public async Task LoadAsync_ReadsTheFileAsText()
    {
        using var temp = new TempDirectory();
        var document = new FileDocumentViewModel(temp.WriteFile("a.cs", "class A {}\r\n"));

        await document.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("class A {}\r\n", document.Text);
        Assert.Null(document.ErrorMessage);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task LoadAsync_BinaryFile_IsNotShown()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 0, 3], TestContext.Current.CancellationToken);
        var document = new FileDocumentViewModel(path);

        await document.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, document.Text);
        Assert.Contains("binario", document.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_FileOverTheLimit_IsNotLoaded()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("big.txt");
        await File.WriteAllBytesAsync(path, new byte[FileDocumentViewModel.MaxBytes + 1], TestContext.Current.CancellationToken);
        var document = new FileDocumentViewModel(path);

        await document.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, document.Text);
        Assert.Contains("el visor no admite más de", document.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReportsTheError()
    {
        using var temp = new TempDirectory();
        var document = new FileDocumentViewModel(temp.Combine("nope.txt"));

        await document.LoadAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("No se pudo leer el fichero", document.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentId_IsTheSameForTheSameFileRegardlessOfCase()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("A.txt");

        Assert.Equal(FileDocumentViewModel.CreateId(path), FileDocumentViewModel.CreateId(path.ToLowerInvariant()));
    }
}
