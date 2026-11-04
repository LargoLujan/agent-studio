using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Controls.Terminal;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AvalonDock.Core.Serialization;
using AvalonDock.Layout;

namespace AgentStudio.Desktop;

public partial class MainWindow : Window
{
    /// <summary>The width of the right column of the default layout once its panels are pinned.</summary>
    private const double RightColumnWidth = 340;

    /// <summary>The id of that column in the saved layouts.</summary>
    private const string RightColumnId = "column:right";

    private readonly MainViewModel _viewModel;
    private readonly ILayoutStore _layoutStore;

    /// <summary>Panel views by ContentId; the serializer needs them to refill the panels of a restored layout.</summary>
    private readonly Dictionary<string, object?> _panelContent = [];

    private string _defaultLayout = string.Empty;
    private bool _layoutCaptured;
    private bool _closeConfirmed;
    private bool _closeInProgress;
    private readonly IQuickOpenPresenter _quickOpen;

    private readonly TerminalViewModel? _terminal;

    private readonly IInterfaceLock? _interfaceLock;

    /// <summary>The floating panes of the dock: hidden while locked, and while the window is hidden in the tray.</summary>
    private readonly FloatingPanesVisibility _panes;

    /// <summary>The window was shown once: hidden from then on, it lives in the tray.</summary>
    private bool _wasShown;

    /// <summary>The background of the app (the tray and its managed exit), once App attached it; null before.</summary>
    private BackgroundHost? _background;
    private IAppSettingsStore? _settings;

    /// <summary>The lock screen is in place of the content.</summary>
    private bool _lockApplied;

    /// <summary>The shortcuts of the window, taken out while the interface is locked.</summary>
    private List<InputBinding>? _suspendedInputs;
    private List<CommandBinding>? _suspendedCommands;

    /// <summary>The managed exit is deciding a close of the window.</summary>
    private bool _closing;

    /// <summary>The layout was saved and the terminal released: the app is ending.</summary>
    private bool _released;

    /// <summary>Writes «Salir ahora» for the work going on when the window closes the app without the tray.</summary>
    private readonly ExitWithoutTray? _exitWithoutTray;

    /// <summary>Where the placement of the window is kept between sessions; null keeps none (a window built by hand, in tests).</summary>
    private readonly IAppSettingsStore? _placementStore;

    /// <summary>The window was maximized before it was minimized: a minimized window is saved as maximized then.</summary>
    private bool _restoreMaximized;

    /// <param name="quickOpenPresenter">Shows the "go to file" list; defaults to a window of its own.</param>
    /// <param name="lockScreen">The lock screen (docs/16 D-13); without it, or without <paramref name="interfaceLock"/>, nothing locks.</param>
    /// <param name="interfaceLock">The lock of the interface the window follows: locked, the lock screen replaces the content.</param>
    /// <param name="floatingPanes">The floating windows of the dock; defaults to those of AvalonDock.</param>
    /// <param name="exitWithoutTray">
    /// The close of the app without the tray (ADR-0036): «Salir ahora» in the log of the work going on; without it, nothing is
    /// written then.
    /// </param>
    /// <param name="settings">
    /// The settings of the app, where the placement of the window is kept: restored now when it still falls on a monitor, and
    /// saved on the real exit. Without them the window opens centred and nothing is saved.
    /// </param>
    /// <param name="screens">The work areas of the monitors connected now; defaults to those Windows reports.</param>
    public MainWindow(
        MainViewModel viewModel,
        ILayoutStore layoutStore,
        TerminalViewModel? terminal = null,
        IQuickOpenPresenter? quickOpenPresenter = null,
        LockScreenViewModel? lockScreen = null,
        IInterfaceLock? interfaceLock = null,
        IFloatingPanes? floatingPanes = null,
        ExitWithoutTray? exitWithoutTray = null,
        IAppSettingsStore? settings = null,
        IScreenWorkAreas? screens = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(layoutStore);
        InitializeComponent();
        _viewModel = viewModel;
        _layoutStore = layoutStore;
        _terminal = terminal;
        _exitWithoutTray = exitWithoutTray;
        _placementStore = settings;
        RestorePlacement(screens ?? new SystemScreenWorkAreas());
        _quickOpen = quickOpenPresenter ?? new WindowQuickOpenPresenter();
        _panes = new FloatingPanesVisibility(floatingPanes ?? new DockFloatingPanes(DockManager));

        if (terminal is not null)
        {
            // The terminal starts in the open project (or the user profile until one is opened).
            terminal.WorkingDirectory = viewModel.ProjectPath ?? terminal.WorkingDirectory;
            TerminalHost.Content = new TerminalControl { DataContext = terminal };
        }

        if (System.Windows.Application.Current?.Resources is { } tokens)
        {
            DockThemeTokens.Apply(DockManager.Resources, tokens);

            // Nodify reads its colour keys with DynamicResource: written at the root of the application resources they
            // win over Themes/Nodify.xaml. No canvas exists yet (it lives in a document tab opened later).
            NodifyThemeTokens.Apply(tokens, tokens);
        }

        NameDockTabs();
        PrepareRightColumn();
        ExplorerPanel.DataContext = viewModel.Explorer;
        AutoDoctorPanel.DataContext = viewModel.Diagnostics;
        JournalPanel.DataContext = viewModel.Journal;
        DataContext = viewModel;
        CaptureDefaultLayout();

        viewModel.ProjectChanging += OnProjectChanging;
        viewModel.ProjectChanged += OnProjectChanged;
        viewModel.PanelToggleRequested += OnPanelToggleRequested;
        viewModel.QuickOpen.PropertyChanged += OnQuickOpenPropertyChanged;
        viewModel.Documents.CollectionChanged += OnDocumentsChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;

        if (lockScreen is not null && interfaceLock is not null)
        {
            // «Bloquear»: the button of the top bar, Ctrl+Alt+L and Ctrl+Shift+B run the command of the lock screen. Ctrl+Alt is
            // AltGr on many layouts: AltGr+L types nothing on the Spanish one, so Ctrl+Alt+L locks there too, but a layout where
            // it types a character (Polish «ł») would lock instead; Ctrl+Shift+B has no Alt, and the editor binds neither.
            _interfaceLock = interfaceLock;
            LockScreen.DataContext = lockScreen;
            LockButton.Command = lockScreen.LockCommand;
            LockButton.Visibility = Visibility.Visible;
            InputBindings.Add(new KeyBinding(lockScreen.LockCommand, Key.L, ModifierKeys.Control | ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(lockScreen.LockCommand, Key.B, ModifierKeys.Control | ModifierKeys.Shift));
            interfaceLock.Changed += OnInterfaceLockChanged;
            ApplyLock(interfaceLock.IsLocked);
        }
    }

    /// <summary>Whether the lock screen is in place of the content now.</summary>
    public bool ShowsLockScreen => _lockApplied;

    /// <summary>Whether the floating panes of the dock are hidden now (locked, or the window hidden in the tray).</summary>
    public bool HidesFloatingPanes => _panes.AreHidden;

    /// <summary>The window was shown once and is hidden now: it lives in the tray.</summary>
    private bool HiddenInTray => _wasShown && !IsVisible;

    /// <summary>
    /// The background of the app (ADR-0036), once App put it together: from now on closing the window goes through its managed
    /// exit with the setting <see cref="AppSettings.CloseWindow"/>, and the tray and its panel follow the lock.
    /// </summary>
    public void AttachBackground(BackgroundHost background, IAppSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(settings);
        _background = background;
        _settings = settings;
        background.IsLocked = _lockApplied;
    }

    /// <summary>
    /// The background could not be put together after all (ADR-0036): closing the window ends the app again, as it does before
    /// the tray exists.
    /// </summary>
    public void DetachBackground()
    {
        _background = null;
        _settings = null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.Diagnostics.RefreshCommand.Execute(null);
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Closing the window (docs/16 D-14, ADR-0036): with the background, the close is cancelled and the managed exit decides,
    /// with the setting: hide to the tray, ask, or exit. While the app is exiting (the managed exit, the end of the session or
    /// the dispatcher shutting down) it is not intercepted: the layout is saved and the terminal released, once. Before the
    /// background exists, the window closes as it did before Phase 8. Locked (D-13, ADR-0037), a close never asks nor exits:
    /// the X of the title bar, Alt+F4 and «Cerrar ventana» of the taskbar are outside the content the lock hides, and nothing of
    /// the work, nor the names of the files with unsaved changes, may show over the lock screen. With the tray the window goes
    /// there; without it the close does nothing. An end of the session still ends the app, and writes what it writes.
    /// </summary>
    /// <summary>
    /// The message of the status bar changed (V38): it is cut at 180 px, so a screen reader is told (it is a polite live
    /// region, and its name is the whole text).
    /// </summary>
    private void OnStatusMessageUpdated(object? sender, DataTransferEventArgs e) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusMessageText)
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_background is not { } background)
        {
            CloseWithoutBackground(e);
            return;
        }

        if (background.Exit.IsExiting || Dispatcher.HasShutdownStarted)
        {
            ReleaseForExit();
            return;
        }

        e.Cancel = true;
        if (_lockApplied)
        {
            // Once this close is cancelled, not inside it: the window is not hidden while it is closing.
            _ = Dispatcher.BeginInvoke(Hide);
            return;
        }

        if (!_closing)
        {
            _ = CloseThroughManagedExitAsync(background);
        }
    }

    private async Task CloseThroughManagedExitAsync(BackgroundHost background)
    {
        _closing = true;
        try
        {
            await background.Exit.WindowClosingAsync((_settings?.Load() ?? AppSettings.Default).CloseWindow);
        }
#pragma warning disable CA1031 // A close the managed exit could not decide leaves the window as it is, and says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _viewModel.StatusMessage = $"No se pudo cerrar la ventana: {ex.Message}";
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>
    /// The close without the background (before the tray exists, or when it could not start): unsaved changes are asked about,
    /// the work going on gets «Salir ahora» in its log (<see cref="ExitWithoutTray"/>), and the window closes the app. Locked,
    /// it does nothing and asks nothing, unless the app is shutting down.
    /// </summary>
    private void CloseWithoutBackground(CancelEventArgs e)
    {
        var shuttingDown = Dispatcher.HasShutdownStarted;
        if (_lockApplied && !shuttingDown)
        {
            e.Cancel = true;
            return;
        }

        if (!_closeConfirmed && !_lockApplied && _viewModel.HasUnsavedChanges)
        {
            // Cancel this close; the prompt (and a possible save) is asynchronous, then the window closes again.
            e.Cancel = true;
            if (!_closeInProgress)
            {
                _ = ConfirmCloseAsync();
            }

            return;
        }

        if (!_released && !shuttingDown)
        {
            // The person closed the app without the question of the managed exit: what goes on is left interrupted, by them.
            _exitWithoutTray?.Record(ExitWithoutTray.Budget);
        }

        ReleaseForExit();
    }

    /// <summary>The real exit: the layout of the project and the placement of the window are saved and the terminal released, once.</summary>
    private void ReleaseForExit()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        SaveLayout(_viewModel.ProjectPath);
        SavePlacement();

        // Kills the shell process tree; the Job Object also covers it if the app dies first.
        if (_terminal is not null)
        {
            _ = _terminal.DisposeAsync().AsTask();
        }
    }

    /// <summary>The unsaved changes of a close without the tray; the lock withdraws the question (its answer is a cancel).</summary>
    private async Task ConfirmCloseAsync()
    {
        _closeInProgress = true;
        try
        {
            using var withdrawal = new LockWithdrawal(_interfaceLock);
            if (await _viewModel.ConfirmDiscardOrSaveAsync(_viewModel.Documents.ToList(), withdrawal.Token) && !withdrawal.Token.IsCancellationRequested)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    /// <summary>The lock changed, on the thread that changed it: the window follows it on its own thread, at once when it is this one.</summary>
    private void OnInterfaceLockChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyLock(_interfaceLock!.IsLocked);
        }
        else if (!Dispatcher.HasShutdownStarted)
        {
            // What it is when the dispatcher gets to it, not what it was: a lock and an unlock in a row end unlocked.
            _ = Dispatcher.BeginInvoke(() => ApplyLock(_interfaceLock!.IsLocked));
        }
    }

    /// <summary>
    /// Puts the lock screen in place of the content, or the content back (docs/16 D-13, ADR-0037, decision 4). Locked, the
    /// content is hidden and disabled, not covered: the editor and the terminal are WebView2 windows, which no WPF layer covers,
    /// and a hidden one hides its window. The floating panes hide too, the list of files closes, the shortcuts of the window are
    /// taken out (Ctrl+P must not open the list of files), and the tray and its panel hide what goes on. Unlocked, all of it
    /// comes back, the floating panes included.
    /// </summary>
    private void ApplyLock(bool locked)
    {
        if (locked == _lockApplied)
        {
            return;
        }

        _lockApplied = locked;
        if (locked)
        {
            ShellContent.Visibility = Visibility.Hidden;
            ShellContent.IsEnabled = false;
            LockScreen.Visibility = Visibility.Visible;
            _viewModel.QuickOpen.Close();
            _panes.Update(locked: true, HiddenInTray);
            SuspendShortcuts();
            if (IsVisible)
            {
                LockScreen.FocusUnlock();
            }
        }
        else
        {
            LockScreen.Visibility = Visibility.Collapsed;
            ShellContent.IsEnabled = true;
            ShellContent.Visibility = Visibility.Visible;
            RestoreShortcuts();
            _panes.Update(locked: false, HiddenInTray);
        }

        if (_background is not null)
        {
            _background.IsLocked = locked;
        }

        // The bars of the status bar name tasks of the project and the quota of the account: locked, they show nothing.
        _viewModel.StatusStrip.IsLocked = locked;
    }

    private void SuspendShortcuts()
    {
        _suspendedInputs = [.. InputBindings.Cast<InputBinding>()];
        _suspendedCommands = [.. CommandBindings.Cast<CommandBinding>()];
        InputBindings.Clear();
        CommandBindings.Clear();
    }

    private void RestoreShortcuts()
    {
        foreach (var binding in _suspendedInputs ?? [])
        {
            InputBindings.Add(binding);
        }

        foreach (var binding in _suspendedCommands ?? [])
        {
            CommandBindings.Add(binding);
        }

        _suspendedInputs = null;
        _suspendedCommands = null;
    }

    /// <summary>
    /// The window hidden in the tray, or shown again: the floating panes go and come with it (Windows hides the windows of a
    /// window only when it is minimized), unless the interface is locked. Shown while locked, the keyboard goes to «Desbloquear
    /// con Windows Hello».
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _wasShown = true;
        }

        _panes.Update(_lockApplied, HiddenInTray);
        if (_lockApplied && IsVisible)
        {
            LockScreen.FocusUnlock();
        }
    }

    private void OnProjectChanging(object? sender, string? oldProject) => SaveLayout(oldProject);

    private void OnProjectChanged(object? sender, string? newProject)
    {
        if (_terminal is not null && newProject is not null)
        {
            // A shell running elsewhere (the user profile at startup, or the previous project) restarts in the project.
            // Start failures end up in the terminal's own status, so the task needs no observer here.
            _ = _terminal.ChangeWorkingDirectoryAsync(newProject);
        }

        // The serializer rebuilds the layout tree; document tabs are re-added from the view model afterwards.
        var documents = _viewModel.Documents.ToList();
        var active = _viewModel.ActiveContent;
        _viewModel.Documents.Clear();

        var saved = newProject is null ? null : _layoutStore.Load(newProject);
        if (saved is null || !TryRestore(saved))
        {
            TryRestore(_defaultLayout);
        }

        // A restored layout whose panels are folded on the right without their column (an older one, say) gets it back.
        PrepareRightColumn();

        foreach (var document in documents)
        {
            _viewModel.Documents.Add(document);
        }

        _viewModel.ActiveContent = active ?? documents.LastOrDefault();
    }

    private void OnQuickOpenPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickOpenViewModel.IsOpen))
        {
            return;
        }

        if (_viewModel.QuickOpen.IsOpen)
        {
            _quickOpen.Show(this, _viewModel.QuickOpen);
        }
        else
        {
            _quickOpen.Close();
        }
    }

    /// <summary>
    /// An entry of the activity bar, a shortcut or the provider summary asks for a panel (B1): one that is not in view (a tab
    /// behind another of its group, folded on a side, or closed) is shown and activated, which opens a folded one over the
    /// documents; only one in view is hidden, or, folded, its flyout closed.
    /// </summary>
    private void OnPanelToggleRequested(object? sender, string panelId)
    {
        var panel = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == panelId);
        if (panel is null)
        {
            return;
        }

        if (!IsInView(panel))
        {
            panel.Show();
            if (panel.IsAutoHidden && !IsVisible)
            {
                // Folded, selecting it opens its flyout, a window of AvalonDock that exists only while this one is on screen.
                return;
            }

            panel.IsSelected = true;
            panel.IsActive = true;

            // The keyboard (and a screen reader) goes where the panel opened, once it is laid out: its flyout, when folded.
            _ = Dispatcher.InvokeAsync(() => FocusPanel(panel), DispatcherPriority.ContextIdle);
            return;
        }

        var hadKeyboard = false;
        if (panel.IsAutoHidden)
        {
            // AvalonDock closes the flyout once the panel is not the active content any more, and gives the keyboard to the
            // document it activates; the panel keeps its tab on the side.
            if (DockManager.Layout.LastFocusedDocument is { Root: not null } document)
            {
                document.IsActive = true;
            }

            panel.IsActive = false;
        }
        else
        {
            // The keyboard may be in it (in the terminal, after Ctrl+Ñ): hidden, it would take the keyboard along, and the next
            // shortcut would be lost. It comes back to the active document, or to the window.
            hadKeyboard = panel.Content is UIElement content && content.IsKeyboardFocusWithin;
            panel.Hide();
        }

        _ = Dispatcher.InvokeAsync(() => RestoreKeyboardFocus(force: hadKeyboard), DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Gives the keyboard to the first control of a panel the person asked for (from the activity bar, a shortcut or the
    /// provider summary), so the keyboard and a screen reader are where it opened. Not while locked, with the window inactive,
    /// or once the panel is out of view again.
    /// </summary>
    private void FocusPanel(LayoutAnchorable panel)
    {
        if (_lockApplied || !IsActive || !IsInView(panel) || panel.Content is not UIElement { IsVisible: true } view)
        {
            return;
        }

        view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>
    /// Whether the panel is in view now: docked or floating, the selected tab of its group; folded on a side, its flyout open.
    /// A tab behind another of its group counts as visible for AvalonDock, but it is not in view.
    /// </summary>
    public bool IsInView(LayoutAnchorable panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return panel.IsAutoHidden
            ? ReferenceEquals(DockManager.AutoHideWindow?.Model, panel)
            : panel.IsVisible && panel.IsSelected;
    }

    /// <summary>
    /// Gives each tab of the dock the name a screen reader says (A1): the title of its panel, and of a document with whether it
    /// has unsaved changes. AvalonDock's tabs have no name, and UI Automation named them after their model
    /// («AvalonDock.Layout.LayoutDocument»). The pane styles of the theme are kept: their tabs only get the name.
    /// </summary>
    private void NameDockTabs()
    {
        var documentName = new PriorityBinding
        {
            Bindings =
            {
                new Binding($"{nameof(LayoutContent.Content)}.{nameof(DocumentViewModel.AccessibleTitle)}"),
                new Binding(nameof(LayoutContent.Title)),
            },
        };
        DockManager.DocumentPaneControlStyle = WithNamedTabs(DockManager.DocumentPaneControlStyle, documentName);
        DockManager.AnchorablePaneControlStyle = WithNamedTabs(DockManager.AnchorablePaneControlStyle, new Binding(nameof(LayoutContent.Title)));

        // The tabs of the panels folded on a side (Auto Doctor and the journal, by default) are named after their panel too. Their
        // look comes from their default style, which an implicit style that sets only the name keeps.
        var anchor = typeof(AvalonDock.Controls.LayoutAnchorControl);
        var namedAnchor = new Style(anchor, DockManager.TryFindResource(anchor) as Style);
        namedAnchor.Setters.Add(new Setter(
            AutomationProperties.NameProperty,
            new Binding($"{nameof(AvalonDock.Controls.LayoutAnchorControl.Model)}.{nameof(LayoutContent.Title)}") { RelativeSource = RelativeSource.Self }));
        DockManager.Resources[anchor] = namedAnchor;
    }

    /// <summary>
    /// The panels folded on the right edge of the default layout (Auto Doctor and the journal) dock, when pinned, in a column of
    /// their own to the right of the documents, as wide as the flyout of Auto Doctor. Without a column to go back to, AvalonDock
    /// docks them in one 100 px wide. The column is empty, so it takes no room, until they are pinned.
    /// </summary>
    private void PrepareRightColumn()
    {
        if (DockManager.Layout.RightSide?.Children.FirstOrDefault() is not ILayoutPreviousContainer group
            || group.PreviousContainer is { Root: not null }
            || DockManager.Layout.RootPanel is not { Orientation: Orientation.Horizontal } root)
        {
            return;
        }

        // With an id, the saved layouts keep the column and the group's way back to it (PreviousContainerId).
        var column = new LayoutAnchorablePane { DockWidth = new GridLength(RightColumnWidth), DockMinWidth = 240 };
        ((ILayoutPaneSerializable)column).Id = RightColumnId;
        root.Children.Add(column);
        group.PreviousContainer = column;
    }

    /// <summary>The pane style with its tab style (ItemContainerStyle) extended by the automation name; as it is when it has none.</summary>
    private static Style? WithNamedTabs(Style? paneStyle, BindingBase name)
    {
        var tabStyle = paneStyle is null ? null : ItemContainerStyleOf(paneStyle);
        if (tabStyle is null)
        {
            return paneStyle;
        }

        var namedTab = new Style(typeof(TabItem), tabStyle);
        namedTab.Setters.Add(new Setter(AutomationProperties.NameProperty, name));
        var pane = new Style(paneStyle!.TargetType, paneStyle);
        pane.Setters.Add(new Setter(ItemsControl.ItemContainerStyleProperty, namedTab));
        return pane;
    }

    private static Style? ItemContainerStyleOf(Style style)
    {
        for (var current = style; current is not null; current = current.BasedOn)
        {
            if (current.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == ItemsControl.ItemContainerStyleProperty)?.Value is Style found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A tab closed (V11): once the dock settled, the keyboard comes back to the window if the tab took it along. The editor is
    /// a WebView2 window, and closing it leaves the window active with the keyboard nowhere: its shortcuts (Ctrl+P) were dead
    /// until a click. The keyboard goes to the document that is active now, or to the window.
    /// </summary>
    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
        {
            _ = Dispatcher.InvokeAsync(() => RestoreKeyboardFocus(force: false), DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// Nothing moves while the keyboard is in the window (Windows says where it is: WPF loses track of it in a window inside
    /// this one, the WebView2 of the terminal or the flyout of a folded panel), while the window is not the active one, nor
    /// while locked. <paramref name="force"/>: a panel that had the keyboard was just hidden, and the keyboard comes back
    /// even though Windows may still place it in that panel's window.
    /// </summary>
    private void RestoreKeyboardFocus(bool force)
    {
        if (_lockApplied || !IsActive || (!force && WindowFocus.IsWithin(new WindowInteropHelper(this).Handle)))
        {
            return;
        }

        // From no focus, so that WPF takes the keyboard from Windows again even for the element it believes has it.
        Keyboard.ClearFocus();
        if (FocusTargetAfterClose() is { } view && view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            return;
        }

        Focus();
    }

    /// <summary>The view of the document that is active now, which gets the keyboard after a tab closed; null without one.</summary>
    public FrameworkElement? FocusTargetAfterClose()
    {
        var document = DockManager.Layout?.ActiveContent as LayoutDocument
            ?? DockManager.Layout?.LastFocusedDocument as LayoutDocument;
        return document is { Root: not null } ? DockManager.GetLayoutItemFromModel(document)?.View : null;
    }

    /// <summary>
    /// The placement of the last session (V19), when it still falls on a monitor connected now: its bounds, and maximized if it
    /// was. Otherwise the window opens centred on the work area of the primary monitor, with its default size cut down to fit
    /// it (<see cref="FirstPlacement"/>).
    /// </summary>
    private void RestorePlacement(IScreenWorkAreas screens)
    {
        // Before the window has a handle its DPI is the one of the system, which is the one the app places windows with.
        if (_placementStore?.Load().MainWindowPlacement?.FitTo(screens.Current(System.Windows.Media.VisualTreeHelper.GetDpi(this)), new Size(MinWidth, MinHeight)) is not { } placement)
        {
            // No placement to restore. «CenterScreen» would centre on the monitor under the mouse, where the default size may
            // not fit (a smaller monitor with a larger scale): the window then spills over two monitors and under the taskbar.
            var first = FirstPlacement(SystemParameters.WorkArea, new Size(Width, Height), new Size(MinWidth, MinHeight));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = first.Left;
            Top = first.Top;
            Width = first.Width;
            Height = first.Height;
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        _restoreMaximized = placement.Maximized;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Where the window opens when there is no placement to restore: centred on <paramref name="workArea"/> (the primary
    /// monitor without its taskbar), at <paramref name="preferred"/> size or smaller so that it fits with a margin, and never
    /// below <paramref name="minimum"/>.
    /// </summary>
    public static Rect FirstPlacement(Rect workArea, Size preferred, Size minimum)
    {
        const double margin = 24;
        var width = Math.Max(minimum.Width, Math.Min(preferred.Width, workArea.Width - (2 * margin)));
        var height = Math.Max(minimum.Height, Math.Min(preferred.Height, workArea.Height - (2 * margin)));
        var left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        return new Rect(left, top, width, height);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreMaximized = WindowState == WindowState.Maximized;
        }
    }

    /// <summary>
    /// Where the window is now: its bounds when it is neither maximized nor minimized (Windows keeps them while it is either),
    /// and whether it is maximized, or was before it was minimized. A window never shown has no handle: its properties say
    /// them, and a position that is not set yet (centred) is none.
    /// </summary>
    public WindowPlacement? CurrentPlacement()
    {
        var maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _restoreMaximized);
        var bounds = RestoreBounds;
        var placement = bounds.IsEmpty
            ? new WindowPlacement(Left, Top, Width, Height, maximized)
            : new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized);
        return placement.IsValid ? placement : null;
    }

    /// <summary>The real exit saves where the window is; hiding it in the tray does not (it is not an exit).</summary>
    private void SavePlacement()
    {
        if (_placementStore is not { } store || CurrentPlacement() is not { } placement)
        {
            return;
        }

        try
        {
            store.SaveWindowPlacement(placement);
        }
        catch (IOException ex)
        {
            _viewModel.StatusMessage = $"No se pudo guardar la posición de la ventana: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            _viewModel.StatusMessage = $"No se pudo guardar la posición de la ventana: {ex.Message}";
        }
    }

    /// <summary>Remembers the XAML layout so a project without saved layout (or with a corrupt one) gets it back.</summary>
    private void CaptureDefaultLayout()
    {
        if (_layoutCaptured)
        {
            return;
        }

        foreach (var anchorable in DockManager.Layout.Descendents().OfType<LayoutAnchorable>())
        {
            _panelContent[anchorable.ContentId] = anchorable.Content;
        }

        _defaultLayout = Serialize();
        _layoutCaptured = true;
    }

    private void SaveLayout(string? projectPath)
    {
        if (projectPath is null || !_layoutCaptured)
        {
            return;
        }

        try
        {
            _layoutStore.Save(projectPath, Serialize());
        }
        catch (IOException ex)
        {
            _viewModel.StatusMessage = $"No se pudo guardar el diseño de paneles: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            _viewModel.StatusMessage = $"No se pudo guardar el diseño de paneles: {ex.Message}";
        }
    }

    private string Serialize()
    {
        using var writer = new StringWriter();
        new XmlDockLayoutSerializer(DockManager).Serialize(writer);
        return writer.ToString();
    }

    private bool TryRestore(string layoutXml)
    {
        try
        {
            var serializer = new XmlDockLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                // Only the known panels come back; document tabs of the previous session are not restored.
                if (args.Model is ISerializableLayoutAnchorable && args.Model.ContentId is { } id && _panelContent.TryGetValue(id, out var content))
                {
                    args.Content = content;
                }
            };
            using var reader = new StringReader(layoutXml);
            serializer.Deserialize(reader);
            return true;
        }
#pragma warning disable CA1031 // A damaged layout file must never stop the IDE from starting.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
