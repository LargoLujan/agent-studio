using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Controls.Terminal;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Desktop.Orchestrator;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;

namespace AgentStudio.Tests.Desktop;

/// <summary>Builds the real MainWindow (AvalonDock included) on an STA thread with fake services.</summary>
public sealed class MainWindowTests
{
    private static MainWindow CreateWindow(MainViewModel vm, ILayoutStore store) => new(vm, store);

    private static DockingManager Dock(MainWindow window) => (DockingManager)window.FindName("DockManager");

    private static IEnumerable<LayoutAnchorable> Anchorables(MainWindow window) => Dock(window).Layout.Descendents().OfType<LayoutAnchorable>();

    private static LayoutAnchorable Panel(MainWindow window, string id) => Anchorables(window).Single(a => a.ContentId == id);

    /// <summary>
    /// Builds the visual tree of the dock without showing the window and lays it out at 1280 × 800 (<see cref="OffscreenRender"/>):
    /// AvalonDock builds its controls when it is loaded, which a window never shown is not, so Loaded is raised on the dock.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource LayOut(MainWindow window)
    {
        var dock = Dock(window);
        dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, dock));
        return LayOutAgain(window);
    }

    /// <summary>Lays out again a window whose dock <see cref="LayOut"/> already loaded.</summary>
    private static System.Windows.Media.Imaging.BitmapSource LayOutAgain(MainWindow window) =>
        OffscreenRender.Render((FrameworkElement)window.Content, 1280, 800);

    private static void Toggle(MainViewModel vm, string activityId)
    {
        var activity = vm.Activities.Single(a => a.Id == activityId);
        activity.Command.Execute(activity);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>What UI Automation says for the tabs of each pane of the dock, in the order of the panes.</summary>
    private static List<string> TabNames(DockingManager dock) =>
        [.. Descendants<TabControl>(dock).Select(pane =>
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(pane);
            peer.ResetChildrenCache();
            return string.Join(" | ", (peer.GetChildren() ?? []).Select(tab => tab.GetName()));
        }).Where(names => names.Length > 0)];

    [Fact]
    public void Constructing_TheWindow_WithFakeServices_DoesNotThrow()
    {
        using var temp = new TempDirectory();

        var title = WpfHost.Run(() =>
        {
            var window = CreateWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Path)));
            return window.Title;
        });

        Assert.Equal("Agent Studio", title);
    }

    [Fact]
    public void DefaultLayout_HasTheSevenPanelsAndAPlaceholderWithoutTerminal()
    {
        using var temp = new TempDirectory();

        var (ids, terminalText) = WpfHost.Run(() =>
        {
            var window = CreateWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Path)));
            var host = Assert.IsType<ContentControl>(window.FindName("TerminalHost"));
            return (Anchorables(window).Select(a => a.ContentId).Order().ToList(), Assert.IsType<TextBlock>(host.Content).Text);
        });

        // The three panels of the run (Phase 13) sit in the pane of the terminal.
        Assert.Equal([PanelIds.AutoDoctor, PanelIds.Explorer, PanelIds.Journal, PanelIds.Output, PanelIds.Problems, PanelIds.Terminal, PanelIds.Tests], ids);
        Assert.Equal("El terminal no está disponible.", terminalText);
    }

    /// <summary>
    /// The default layout gives the documents most of the window (V12): a narrower explorer and terminal, and Auto Doctor and
    /// the journal folded on the right edge instead of a fixed column. Pinned, they dock in a column of their own, 340 wide.
    /// </summary>
    [Fact]
    public void DefaultLayout_LeavesMostOfTheWidthToTheDocuments_AndFoldsAutoDoctorAndTheJournalOnTheRight()
    {
        using var temp = new TempDirectory();

        var (explorer, terminal, folded, documentsWidth, pinnedWidth) = WpfHost.Run(() =>
        {
            var window = CreateWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Path)));
            var dock = Dock(window);
            var explorerPane = (LayoutAnchorablePane)Panel(window, PanelIds.Explorer).Parent;
            var terminalPane = (LayoutAnchorablePane)Panel(window, PanelIds.Terminal).Parent;
            var onTheRight = dock.Layout.RightSide.Children.SelectMany(g => g.Children).Select(a => a.ContentId).ToList();
            LayOut(window);
            var documents = Descendants<LayoutDocumentPaneControl>(dock).Single().ActualWidth;

            Panel(window, PanelIds.AutoDoctor).ToggleAutoHide();
            LayOutAgain(window);
            var column = Descendants<LayoutAnchorablePaneControl>(dock)
                .Single(p => ((LayoutAnchorablePane)((ILayoutControl)p).Model).Children.Any(a => a.ContentId == PanelIds.AutoDoctor));
            return (explorerPane.DockWidth, terminalPane.DockHeight, onTheRight, documents, column.ActualWidth);
        });

        Assert.Equal(new GridLength(240), explorer);
        Assert.Equal(new GridLength(180), terminal);
        Assert.Equal([PanelIds.AutoDoctor, PanelIds.Journal], folded);
        Assert.True(documentsWidth >= 900, $"The documents get only {documentsWidth:F0} of 1280.");
        Assert.Equal(340, pinnedWidth, 0);
    }

    /// <summary>A project without a saved layout gets the default one back from its serialized copy, the column for pinning included.</summary>
    [Fact]
    public async Task AProjectWithoutASavedLayout_GetsTheDefaultLayout_WithItsColumnForThePinnedPanels()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");

        var (folded, onHover, flyoutWidths, pinnedWidth) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            await vm.OpenProjectAsync(project);
            var dock = Dock(window);
            var onTheRight = dock.Layout.RightSide.Children.SelectMany(g => g.Children).ToList();

            // Restored from its serialized copy, a folded panel still opens only on a click, as wide as in the XAML.
            var hover = onTheRight.Select(a => a.CanShowOnHover).ToList();
            var widths = onTheRight.Select(a => a.AutoHideWidth).ToList();
            LayOut(window);
            Panel(window, PanelIds.AutoDoctor).ToggleAutoHide();
            LayOutAgain(window);
            var column = Descendants<LayoutAnchorablePaneControl>(dock)
                .Single(p => ((LayoutAnchorablePane)((ILayoutControl)p).Model).Children.Any(a => a.ContentId == PanelIds.AutoDoctor));
            return (onTheRight.Select(a => a.ContentId).ToList(), hover, widths, column.ActualWidth);
        });

        Assert.Equal([PanelIds.AutoDoctor, PanelIds.Journal], folded);
        Assert.Equal([false, false], onHover);
        Assert.Equal([340d, 420d], flyoutWidths);
        Assert.Equal(340, pinnedWidth, 0);
    }

    /// <summary>
    /// B1: a panel that is a tab behind another of its group counts as visible for AvalonDock, but it is not in view, and its
    /// entry hid it instead of showing it. Now it shows it; only a panel in view is hidden.
    /// </summary>
    [Fact]
    public void TogglingAPanelBehindAnotherOfItsGroup_ShowsIt_AndOnlyTheOneInViewHides()
    {
        using var temp = new TempDirectory();

        var steps = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            var doctor = Panel(window, PanelIds.AutoDoctor);
            var journal = Panel(window, PanelIds.Journal);
            string State(LayoutAnchorable panel) => panel.IsHidden ? "cerrado" : window.IsInView(panel) ? "a la vista" : "detrás";

            // Pinned, Auto Doctor and the journal share a column, Auto Doctor in front.
            doctor.ToggleAutoHide();
            var states = new List<string> { State(journal) };
            Toggle(vm, "history");
            states.Add(State(journal));
            Toggle(vm, "history");
            states.Add(State(journal));
            Toggle(vm, "history");
            states.Add(State(journal));
            states.Add(State(doctor));
            Toggle(vm, "autodoctor");
            states.Add(State(doctor));
            return states;
        });

        Assert.Equal(["detrás", "a la vista", "cerrado", "a la vista", "detrás", "a la vista"], steps);
    }

    [Fact]
    public void TogglingAClosedPanel_BringsItBack_AndCtrlÑ_DoesTheSameForTheTerminal()
    {
        using var temp = new TempDirectory();

        var (afterClose, afterToggle, afterShortcut, key, modifiers) = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            var terminal = Panel(window, PanelIds.Terminal);

            // The person closes the terminal with the X of its panel; the bar and Ctrl+Ñ bring it back.
            terminal.Hide();
            var closed = terminal.IsHidden;
            Toggle(vm, "terminal");
            var back = window.IsInView(terminal);
            vm.ToggleTerminalCommand.Execute(null);
            var hidden = terminal.IsHidden;
            WpfHost.FlushBindings();
            var binding = window.InputBindings.OfType<KeyBinding>().SingleOrDefault(b => ReferenceEquals(b.Command, vm.ToggleTerminalCommand));
            return (closed, back, hidden, binding?.Key, binding?.Modifiers);
        });

        Assert.True(afterClose);
        Assert.True(afterToggle, "The entry «Terminal» brings back a closed terminal.");
        Assert.True(afterShortcut, "Ctrl+Ñ hides the terminal that is in view.");

        // Oem3 is «Ñ» on a Spanish keyboard and «`» on a US one.
        Assert.Equal(Key.Oem3, key);
        Assert.Equal(ModifierKeys.Control, modifiers);
    }

    /// <summary>A folded panel opens in a flyout of AvalonDock, which exists only with the window on screen: off screen nothing breaks.</summary>
    [Fact]
    public void TogglingAFoldedPanel_WithTheWindowOffScreen_LeavesItFolded()
    {
        using var temp = new TempDirectory();

        var (folded, closed) = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            LayOut(window);
            vm.ToggleAutoDoctorCommand.Execute(null);
            var doctor = Panel(window, PanelIds.AutoDoctor);
            return (doctor.IsAutoHidden, doctor.IsHidden);
        });

        Assert.True(folded);
        Assert.False(closed);
    }

    /// <summary>V10: the bar takes no focus, so the keyboard ring goes on the button that has it and never around the whole bar.</summary>
    [Fact]
    public void TheActivityBar_TakesNoFocus_ButItsButtonsDo()
    {
        using var temp = new TempDirectory();

        var (bar, buttons, names) = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            LayOut(window);
            var activityBar = (ItemsControl)window.FindName("ActivityBar");
            var items = UIElementAutomationPeer.CreatePeerForElement(activityBar).GetChildren() ?? [];
            return (
                activityBar.Focusable,
                Descendants<Button>(activityBar).Select(b => b.Focusable).ToList(),
                items.Select(i => i.GetName()).ToList());
        });

        Assert.False(bar);
        Assert.Equal(9, buttons.Count);
        Assert.All(buttons, Assert.True);

        // A screen reader names each entry by its title (A1), not by the type behind it.
        Assert.Equal(
            [
                "Explorador", "Orquestador", Texts.Get("Shell_Activity_Agents"), "Skills", Texts.Get("Shell_Activity_Tools"), Texts.Get("Shell_Activity_Tests"),
                "Terminal", "Auto Doctor", "Historial (bitácora)",
            ],
            names);
    }

    /// <summary>
    /// A1: UI Automation named the tabs of the dock after their model («AvalonDock.Layout.LayoutDocument»). Now each says its
    /// title, a document with unsaved changes says so, and the tabs of the folded panels say theirs too.
    /// </summary>
    [Fact]
    public async Task TheTabsOfTheDock_AreNamedByTheirTitles()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("Calc.cs", "class Calc {}");

        var (tabs, sideTabs, title, tabTitle) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            await vm.InitializeAsync();
            await vm.OpenFileAsync(file);
            var document = vm.Documents.OfType<FileDocumentViewModel>().Single();
            document.IsDirty = true;
            var dock = Dock(window);
            LayOut(window);
            return (
                TabNames(dock),
                Descendants<LayoutAnchorControl>(dock).Select(a => AutomationProperties.GetName(a)).ToList(),
                document.Title,
                dock.Layout.Descendents().OfType<LayoutDocument>().Single(d => ReferenceEquals(d.Content, document)).Title);
        });

        Assert.Equal(
            ["Explorador", "Bienvenida | Calc.cs, con cambios sin guardar", $"Terminal | {Texts.Get("Panels_Problems_Title")} | {Texts.Get("Panels_Output_Title")} | {Texts.Get("Panels_Tests_Title")}"],
            tabs);
        Assert.Equal(["Auto Doctor", "Bitácora de hoy"], sideTabs);

        // V8: the tab marks the unsaved changes; the title of the document, which the questions and the messages use, stays.
        Assert.Equal("Calc.cs", title);
        Assert.Equal("Calc.cs ●", tabTitle);
    }

    [Fact]
    public async Task TheTabOfADocument_LosesItsMarkerOnceItIsSaved()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("Calc.cs", "class Calc {}");

        var titles = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            await vm.OpenFileAsync(file);
            var document = vm.Documents.OfType<FileDocumentViewModel>().Single();

            // The dock builds the tab of the document when it is loaded and laid out, as in the test above; without it the
            // tab is not always there yet on a slower machine.
            LayOut(window);
            var tab = Dock(window).Layout.Descendents().OfType<LayoutDocument>().Single(d => ReferenceEquals(d.Content, document));
            var seen = new List<string>();
            foreach (var dirty in new[] { true, false })
            {
                document.IsDirty = dirty;
                WpfHost.FlushBindings();
                seen.Add(tab.Title);
            }

            return seen;
        });

        Assert.Equal(["Calc.cs ●", "Calc.cs"], titles);
    }

    /// <summary>V2 and V3: the status bar says the version, not a phase; the provider summary is a button that opens Auto Doctor.</summary>
    [Fact]
    public async Task TheBars_SayTheVersion_AndTheProviderSummaryOpensAutoDoctor()
    {
        using var temp = new TempDirectory();

        var (version, summary, command, name, toolTip) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain(
                null,
                null,
                null,
                new StubProbe("claude-code", InstallationState.Installed, AuthenticationState.Authenticated),
                new StubProbe("codex-cli", InstallationState.Installed, AuthenticationState.Authenticated),
                new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null));
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);
            WpfHost.FlushBindings();
            var button = (Button)window.FindName("ProviderSummaryButton");
            return (
                ((TextBlock)window.FindName("VersionText")).Text,
                vm.ProviderSummary,
                ReferenceEquals(button.Command, vm.ToggleAutoDoctorCommand),
                AutomationProperties.GetName(button),
                button.ToolTip as string);
        });

        Assert.StartsWith("v", version, StringComparison.Ordinal);
        Assert.DoesNotContain("Fase", version, StringComparison.Ordinal);
        Assert.Equal(("Programas: 2 de 2 listos", StatusKind.Ok), (summary.Text, summary.Status));
        Assert.True(command);
        Assert.Equal("Programas: 2 de 2 listos", name);
        Assert.Contains("Auto Doctor", toolTip, StringComparison.Ordinal);
    }

    /// <summary>A screen reader says which project is open in the top bar (it said «Nombre del proyecto», never the name).</summary>
    [Fact]
    public async Task TheNameOfTheProjectInTheTopBar_SaysWhichIsOpen()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");

        var (before, after) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            var name = (TextBlock)window.FindName("ProjectNameText");
            WpfHost.FlushBindings();
            var withoutProject = AutomationProperties.GetName(name);
            await vm.OpenProjectAsync(project);
            WpfHost.FlushBindings();
            return (withoutProject, AutomationProperties.GetName(name));
        });

        Assert.Equal("Sin proyecto abierto", before);
        Assert.Equal("Proyecto abierto: calc", after);
    }

    /// <summary>V11: after a tab closes, the keyboard goes to the view of the document that stays active.</summary>
    [Fact]
    public async Task AfterATabCloses_TheKeyboardGoesToTheDocumentThatStaysActive()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("Calc.cs", "class Calc {}");

        var (target, welcomeView) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            await vm.InitializeAsync();
            await vm.OpenFileAsync(file);
            LayOut(window);

            await vm.CloseDocumentAsync(vm.Documents.OfType<FileDocumentViewModel>().Single());
            LayOutAgain(window);
            var welcome = Dock(window).Layout.Descendents().OfType<LayoutDocument>().Single();
            return (window.FocusTargetAfterClose(), Dock(window).GetLayoutItemFromModel(welcome)?.View);
        });

        Assert.NotNull(target);
        Assert.Same(welcomeView, target);
    }

    [Fact]
    public void WithATerminal_TheHostShowsItAndItStartsInTheOpenProject()
    {
        using var temp = new TempDirectory();
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "proyecto")).FullName;

        var (hosted, workingDirectory) = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            vm.ProjectPath = project;
            var terminal = new TerminalViewModel(new NoLauncher(), new NoShells());
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)), terminal);
            var host = Assert.IsType<ContentControl>(window.FindName("TerminalHost"));
            var control = Assert.IsType<TerminalControl>(host.Content);
            return (ReferenceEquals(control.DataContext, terminal), terminal.WorkingDirectory);
        });

        Assert.True(hosted);
        Assert.Equal(project, workingDirectory);
    }

    /// <summary>The terminal only starts a shell once its control is loaded, which these tests never do.</summary>
    private sealed class NoLauncher : ITerminalLauncher
    {
        public ITerminalSession Start(TerminalStartInfo info) => throw new InvalidOperationException("No shell should start.");
    }

    private sealed class NoShells : IShellLocator
    {
        public IReadOnlyList<ShellInfo> GetAvailableShells() => [];
    }

    [Fact]
    public void Panels_ShowTheirViewModels()
    {
        using var temp = new TempDirectory();

        var (explorer, doctor, journal) = WpfHost.Run(() =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Path)));
            return (
                ReferenceEquals(((FrameworkElement)window.FindName("ExplorerPanel")).DataContext, vm.Explorer),
                ReferenceEquals(((FrameworkElement)window.FindName("AutoDoctorPanel")).DataContext, vm.Doctor),
                ReferenceEquals(((FrameworkElement)window.FindName("JournalPanel")).DataContext, vm.Journal));
        });

        Assert.True(explorer);
        Assert.True(doctor);
        Assert.True(journal);
    }

    /// <summary>
    /// The layout a project saved before Phase 13 has no panels «Problemas», «Salida» and «Pruebas» (the layout of the user of the
    /// demo project is one): restoring it brings them back in the pane of the terminal, so «Pruebas» of the activity bar always finds its panel.
    /// </summary>
    [Fact]
    public async Task ALayoutSavedBeforePhase13_GetsThePanelsOfTheRunInThePaneOfTheTerminal()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
        var runPanels = new[] { PanelIds.Problems, PanelIds.Output, PanelIds.Tests };

        var (ids, sameGroup, activated) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, store);
            await vm.OpenProjectAsync(a);
            await vm.OpenProjectAsync(b);

            // What the layout of A looked like before Phase 13: the same tree without the three panels.
            var xml = System.Xml.Linq.XDocument.Parse(store.Load(a)!);
            xml.Descendants("LayoutAnchorable").Where(e => runPanels.Contains((string?)e.Attribute("ContentId"))).ToList().ForEach(e => e.Remove());
            Assert.DoesNotContain(xml.Descendants("LayoutAnchorable"), e => runPanels.Contains((string?)e.Attribute("ContentId")));
            store.Save(a, xml.ToString());

            await vm.OpenProjectAsync(a);
            var visible = Anchorables(window).Where(x => x.IsVisible).Select(x => x.ContentId).Order().ToList();
            var terminalPane = Panel(window, PanelIds.Terminal).Parent;
            var together = runPanels.All(id => ReferenceEquals(Panel(window, id).Parent, terminalPane));

            // «Pruebas» of the activity bar shows it: the panel exists, so the request does something.
            Panel(window, PanelIds.Tests).Hide();
            Toggle(vm, "tests");
            return (visible, together, Panel(window, PanelIds.Tests).IsVisible);
        });

        Assert.Equal([PanelIds.AutoDoctor, PanelIds.Explorer, PanelIds.Journal, PanelIds.Output, PanelIds.Problems, PanelIds.Terminal, PanelIds.Tests], ids);
        Assert.True(sameGroup);
        Assert.True(activated);
    }

    [Fact]
    public async Task OpeningAProject_SavesTheLayoutOfTheProjectBeingLeft_AndRestoresItLater()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));

        var (journalInB, journalInA) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, store);
            await vm.OpenProjectAsync(a);

            // The user hides the journal in project A, then moves to B (A's layout is saved) and back.
            Anchorables(window).Single(x => x.ContentId == PanelIds.Journal).Hide();
            await vm.OpenProjectAsync(b);
            var inB = Anchorables(window).Single(x => x.ContentId == PanelIds.Journal).IsVisible;
            await vm.OpenProjectAsync(a);
            var inA = Anchorables(window).Single(x => x.ContentId == PanelIds.Journal).IsVisible;
            return (inB, inA);
        });

        Assert.True(journalInB);
        Assert.False(journalInA);
        Assert.NotNull(store.Load(a));
    }

    [Fact]
    public async Task CorruptLayoutFile_IsIgnoredAndTheDefaultLayoutIsUsed()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
        Directory.CreateDirectory(temp.Combine("data", "layouts"));
        File.WriteAllText(store.GetLayoutPath(project), "<LayoutRoot><broken");

        var ids = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, store);
            await vm.OpenProjectAsync(project);
            return Anchorables(window).Where(a => a.IsVisible).Select(a => a.ContentId).Order().ToList();
        });

        Assert.Equal([PanelIds.AutoDoctor, PanelIds.Explorer, PanelIds.Journal, PanelIds.Output, PanelIds.Problems, PanelIds.Terminal, PanelIds.Tests], ids);
    }

    [Fact]
    public async Task WellFormedButForeignLayoutXml_FallsBackToTheDefaultLayout()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
        store.Save(project, "<Unrelated><Thing/></Unrelated>");

        var count = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            var window = CreateWindow(vm, store);
            await vm.OpenProjectAsync(project);
            return Anchorables(window).Count(a => a.IsVisible);
        });

        Assert.Equal(7, count);
    }

    [Fact]
    public async Task DocumentTemplates_ResolveAViewForEachDocumentType()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("a.txt", "x");

        var types = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            await vm.InitializeAsync();
            await vm.OpenFileAsync(file);
            return vm.Documents.Select(d =>
            {
                var template = (DataTemplate)System.Windows.Application.Current.FindResource(new DataTemplateKey(d.GetType()));
                return template.LoadContent().GetType().Name;
            }).ToList();
        });

        Assert.Equal(["WelcomeView", "FileEditorView"], types);
    }
}

public sealed class DockThemeTokensTests
{
    [Fact]
    public void Apply_RepaintsEveryThemeBrushWithATokenColour()
    {
        var (unmatched, count) = WpfHost.Run(() =>
        {
            var tokens = System.Windows.Application.Current.Resources;
            var manager = new DockingManager { Theme = new Vs2013DarkTheme() };
            var painted = DockThemeTokens.Apply(manager.Resources, tokens);

            var allowed = DockThemeTokens.TokenKeys.Select(k => (System.Windows.Media.Color)tokens[k]).ToHashSet();
            var leftovers = new List<string>();
            void Walk(ResourceDictionary d)
            {
                foreach (var m in d.MergedDictionaries)
                {
                    Walk(m);
                }

                foreach (var key in d.Keys.OfType<ComponentResourceKey>())
                {
                    if (d[key] is System.Windows.Media.SolidColorBrush b && !allowed.Contains(b.Color))
                    {
                        leftovers.Add($"{key.ResourceId}={b.Color}");
                    }
                }
            }

            Walk(manager.Resources);
            return (leftovers, painted);
        });

        Assert.Empty(unmatched);
        Assert.True(count > 50, $"Only {count} brushes were repainted.");
    }

    [Theory]
    [InlineData("ToolWindowCaptionActiveBackground", "AccentColor")]
    [InlineData("ToolWindowCaptionActiveText", "OnAccentColor")]
    [InlineData("DocumentWellTabUnselectedText", "TextSecondaryColor")]
    [InlineData("PanelBorderBrush", "BorderColor")]
    [InlineData("Background", "BackgroundColor")]
    public void Classify_MapsKeyBrushesToTheExpectedToken(string id, string token) =>
        Assert.Equal(token, DockThemeTokens.Classify(id));
}
