using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Help;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Panels;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.Tools;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Desktop.Views.Panels;
using AgentStudio.Application.Help;
using AgentStudio.Tests.Desktop.Skills;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens the inventory of the interface walks (docs/17 D-62): every document, panel, dialog and window of the app, each
/// built with its view models over fakes and synthetic data, the way the render tests do. The synthetic data carries no words of
/// either language (file names, identifiers, figures), so the check of the language reads only the interface. A screen that holds
/// others (a document with its sections, the main window with its panels) counts for all the views it shows: the guard
/// (<c>UiInventoryTests</c>) compares the views reached with the XAML of <c>Views/**</c> and <c>Controls/**</c>.
/// </summary>
internal static class UiScreens
{
    public static IReadOnlyList<UiScreen> All { get; } = Build();

    /// <summary>A document, shown the way the dock shows it: the template of its view model, from the resources of the app.</summary>
    internal static UiBuilt Document(object viewModel) => new(new ContentControl { Content = viewModel });

    internal static UiBuilt Of(FrameworkElement view, object? dataContext = null)
    {
        view.DataContext = dataContext;
        return new UiBuilt(view);
    }

    internal static UiBuilt OfWindow(Window window, object? dataContext = null)
    {
        // A window that composes itself (the main window) keeps the data context it gave itself.
        if (dataContext is not null)
        {
            window.DataContext = dataContext;
        }

        return new UiBuilt((FrameworkElement)window.Content, window);
    }

    internal static UiScreen Screen(string name, Func<UiBuilt> create, double width = 1100, double height = 760) =>
        new(name, () => Task.FromResult(create()), width, height);

    internal static UiScreen ScreenAsync(string name, Func<Task<UiBuilt>> create, double width = 1100, double height = 760) =>
        new(name, create, width, height);

    private static IReadOnlyList<UiScreen> Build() =>
        [.. Core(), .. UiScreensShell.All, .. UiScreensOrchestrator.All, .. UiScreensDebate.All, .. UiScreensServices.All, .. UiScreensRepository.All, .. UiScreensWork.All,
         .. UiScreensDoctor.All, .. UiScreensGraph.All, .. UiScreensDocs.All, .. UiScreensHosting.All];

    private static IReadOnlyList<UiScreen> Core() =>
    [
        Screen("Welcome", () =>
        {
            var recent = new ObservableCollection<RecentProjectItem> { RecentProjectItem.From(@"C:\work\calc"), RecentProjectItem.From(@"C:\work\parser") };
            return Document(new WelcomeDocumentViewModel(recent, new RelayCommand(() => { }), new RelayCommand<string>(_ => { })));
        }),
        Screen("Explorer", () => Of(new ExplorerView(), LoadedExplorer()), 320, 520),
        Screen("Journal", () => Of(new JournalView(), new JournalViewModel(TimeProvider.System)), 420, 520),
        Screen("Output panel", () => Of(new OutputPanelView(), new OutputPanelViewModel()), 900, 320),
        Screen("Problems panel", () => Of(new ProblemsPanelView(), new ProblemsPanelViewModel()), 900, 320),
        Screen("Tests panel", () => Of(new TestsPanelView(), new TestsPanelViewModel()), 900, 320),
        Screen("Tools", () => Document(new ToolsDocumentViewModel(LoadedTools()))),
        Screen("Settings", () => Document(SettingsDocument())),
        ScreenAsync("Skills", async () =>
        {
            // With the doctor wired and a skill selected, so the panels of the access key and of the scripts are in the inventory too.
            var world = new SkillsWorld();
            var document = world.Document(world.DoctorBackend());
            await document.OpenAsync(CancellationToken.None);
            document.Sectors.SelectMany(sector => sector.Skills).First().IsSelected = true;
            return Document(document);
        }),
        Screen("Help", () => Document(new HelpDocumentViewModel(new HelpBackend(HelpCorpus.FromAssembly(), new HelpSearch(HelpCorpus.FromAssembly()), new HelpActionCatalog())))),
    ];

    private static ExplorerViewModel LoadedExplorer()
    {
        var explorer = new ExplorerViewModel();
        explorer.Load(Project.Path);
        return explorer;
    }

    /// <summary>A folder of files that lives as long as the run of the tests, for the screens that read a project from disk.</summary>
    private static TempDirectory Project { get; } = CreateProject();

    private static TempDirectory CreateProject()
    {
        var folder = new TempDirectory();
        folder.WriteFile("src/App/Program.cs");
        folder.WriteFile("src/App/App.csproj");
        folder.WriteFile("README.md");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => folder.Dispose();
        return folder;
    }

    private static ToolsViewModel LoadedTools() => new();

    private static SettingsDocumentViewModel SettingsDocument()
    {
        var store = new InMemorySettingsStore(AppSettings.Default with { IdleLockEnabled = true });
        return new SettingsDocumentViewModel(
            store,
            new ObservabilitySettingsViewModel(store),
            new UnattendedSettingsViewModel(store, new LockSettingsViewModel(store)),
            time: new FakeTimeProvider(T0),
            memory: new MemorySettingsViewModel(store),
            language: new LanguageSettingsViewModel(store));
    }
}
