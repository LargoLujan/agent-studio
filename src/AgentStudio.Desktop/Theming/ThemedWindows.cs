using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentStudio.Desktop.Theming;

/// <summary>
/// What every window gets from one registration at startup (<see cref="Register()"/>), MainWindow included and without a
/// line in any window: the dark title bar of Windows (<see cref="DarkTitleBar"/>), set before the window is first drawn; and,
/// in the dialogs and panels of the app (<see cref="TakesFirstFocus"/>), the keyboard focus on their first control when they
/// are activated with nothing focused in them (<see cref="InitialFocus"/>), so Esc and Enter work at once and not after a Tab.
/// </summary>
/// <remarks>
/// <see cref="Window.SourceInitialized"/> is a plain event, which no class handler reaches. The first routed event a window
/// raises with its handle is <see cref="FrameworkElement.SizeChangedEvent"/>: <see cref="Window.Show"/> lays the window out
/// while it creates the handle, before <c>SourceInitialized</c> and before the window is on the screen, so the title bar is
/// dark from the first frame. <see cref="FrameworkElement.LoadedEvent"/> is the second chance, for a window whose handle
/// existed before it was first laid out. The windows of other libraries (the floating panes of AvalonDock) get the title bar
/// but keep their own focus.
/// The first focus is tried on every activation and on the first <see cref="Window.ContentRendered"/>: a question opened from
/// the menu of the tray is activated from another process, and WPF then gives the keyboard to the window itself, where Esc
/// and Enter reach no button. Whether it acts is <see cref="InitialFocus.NeedsFocus"/>; that the activation reaches it is
/// only seen live, because a window that is never shown is never activated.
/// </remarks>
public static class ThemedWindows
{
    private static readonly DependencyProperty TitleBarSetProperty =
        DependencyProperty.RegisterAttached("TitleBarSet", typeof(bool), typeof(ThemedWindows), new PropertyMetadata(false));

    private static readonly DependencyProperty FocusFollowedProperty =
        DependencyProperty.RegisterAttached("FocusFollowed", typeof(bool), typeof(ThemedWindows), new PropertyMetadata(false));

    private static DarkTitleBar _titleBar = new();
    private static bool _registered;

    /// <summary>Registers both for every window created from now on, with the real DWM call. Call it once, at startup.</summary>
    public static void Register() => Register(new DarkTitleBar());

    /// <summary>
    /// Registers both for every window created from now on. Again, it only changes the title bar used from then on: the class
    /// handlers are registered once, for the life of the process.
    /// </summary>
    public static void Register(DarkTitleBar titleBar)
    {
        ArgumentNullException.ThrowIfNull(titleBar);
        _titleBar = titleBar;
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnWindowEvent), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowEvent), handledEventsToo: true);
    }

    /// <summary>
    /// Gives <paramref name="window"/> what it does not have yet: the dark title bar once it has a handle (once per window,
    /// whether DWM takes it or not), and, if it takes the first focus, the first focus on each activation.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!(bool)window.GetValue(TitleBarSetProperty))
        {
            var handle = HandleOf(window);
            if (handle != 0)
            {
                window.SetValue(TitleBarSetProperty, true);
                _titleBar.Apply(handle);
            }
        }

        if (!(bool)window.GetValue(FocusFollowedProperty) && TakesFirstFocus(window))
        {
            window.SetValue(FocusFollowedProperty, true);
            window.Activated += OnActivatedOrRendered;
            window.ContentRendered += OnActivatedOrRendered;
            if (window.IsActive)
            {
                FocusLater(window);
            }
        }
    }

    /// <summary>A window of the app (AgentStudio.Desktop), not one of a library that manages its own focus.</summary>
    public static bool IsOfTheApp(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.GetType().Assembly == typeof(ThemedWindows).Assembly;
    }

    /// <summary>
    /// Whether <paramref name="window"/> takes the first focus: the dialogs and panels of the app, not MainWindow, which keeps
    /// the keyboard where its user left it (a pane, the editor) and would otherwise give it to «Abrir carpeta» on activation.
    /// </summary>
    public static bool TakesFirstFocus(Window window) => IsOfTheApp(window) && window is not MainWindow;

    private static void OnWindowEvent(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            Attach(window);
        }
    }

    private static void OnActivatedOrRendered(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            FocusLater(window);
        }
    }

    /// <summary>Once the activation is over: the focus WPF gives back to the element that had it comes first, and stays.</summary>
    private static void FocusLater(Window window) =>
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusIfNothingHasIt(window));

    private static void FocusIfNothingHasIt(Window window)
    {
        if (!window.IsActive || !InitialFocus.NeedsFocus(window, Keyboard.FocusedElement))
        {
            return;
        }

        // Only when the keyboard is in the window itself: a WebView2 holds the focus in a child window of its own, and that
        // focus stays where it is.
        var handle = HandleOf(window);
        if (handle == 0 || NativeMethods.GetFocus() != handle)
        {
            return;
        }

        InitialFocus.ChooseTarget(window)?.Focus();
    }

    private static nint HandleOf(Window window) =>
        PresentationSource.FromVisual(window) is HwndSource source ? source.Handle : new WindowInteropHelper(window).Handle;

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetFocus();
    }
}
