using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentStudio.Domain.Work;
using Nodify;
using NodifySpike.ViewModels;
using NodifySpike.Views;

namespace NodifySpike.Harness;

/// <summary>
/// The scripted run of the spike (measurement scaffolding, not part of the canvas): it drives the window with mouse and
/// keyboard messages, checks what the view model and the visual tree end up with, measures frames and takes the screenshots.
/// The window lives off-screen (<see cref="OffScreenHost"/>) and pictures are rendered into bitmaps (<see cref="Screenshot"/>),
/// so a run never shows anything on the user's display and never moves their cursor; the one thing that can reach the
/// user's session is WPF's keyboard focus, which is taken only when that cannot disturb anybody
/// (<see cref="EnsureKeyboardFocusAsync"/>). Every observation is a <see cref="CheckResult"/> in the JSON report.
/// </summary>
internal sealed partial class HarnessRunner
{
    private readonly MainWindow _window;
    private readonly CanvasViewModel _vm;
    private readonly HarnessOptions _options;
    private readonly HarnessReport _report = new();
    private readonly StreamWriter _log;
    private CanvasView _view = null!;
    private NodifyEditor _editor = null!;
    private WindowInput _input = null!;
    private readonly List<(double IdleSeconds, bool HandedBack)> _takeovers = [];
    private nint _lastOtherForeground;
    private bool _sawPopup;
    private bool _keyboardFocusUnavailable;

    // The first keyboard focus (see EnsureKeyboardFocusAsync): taken right after the user's own input, when Windows refuses to
    // let a background program take the foreground, or when nobody has used the machine for a while and nobody can be disturbed.
    private const double ActiveWithinSeconds = 1.5;
    private const double AwayAfterSeconds = 30;

    private HarnessRunner(MainWindow window, HarnessOptions options)
    {
        _window = window;
        _vm = (CanvasViewModel)window.DataContext;
        _options = options;
        Directory.CreateDirectory(Path.GetDirectoryName(options.ResultsPath)!);
        _log = new StreamWriter(Path.ChangeExtension(options.ResultsPath, ".log"), append: false) { AutoFlush = true };
    }

    /// <returns>0 when every check passed, 1 otherwise.</returns>
    public static async Task<int> RunAsync(MainWindow window, HarnessOptions options)
    {
        var runner = new HarnessRunner(window, options);
        try
        {
            await runner.ExecuteAsync();
        }
        catch (Exception ex)
        {
            runner._report.Fatal = ex.ToString();
            runner.Log($"FATAL {ex}");
        }
        finally
        {
            WindowInput.ReleaseMouse();
        }

        File.WriteAllText(options.ResultsPath, runner._report.ToJson());
        runner._log.Dispose();
        return runner._report is { Failures: 0, Fatal: null, Skipped.Count: 0 } ? 0 : 1;
    }

    private async Task ExecuteAsync()
    {
        _view = _window.CanvasHost;
        _editor = VisualTree.Descendants<NodifyEditor>(_view).Single();
        _input = new WindowInput(_window);
        await SettleAsync(300);
        RecordEnvironment();
        await SampleLoadAsync("run start");

        // The first keyboard focus is the one step that could take the foreground from the user: do it (or give up the stages
        // that press on elements) before anything else, while the conditions that make it safe are known to hold.
        if (_options.Wants("input") || _options.Wants("bench") || _options.Only == "exp")
        {
            try
            {
                await EnsureKeyboardFocusAsync();
                Keyboard.ClearFocus();
            }
            catch (StageSkippedException ex)
            {
                _keyboardFocusUnavailable = true;
                _report.Skipped.Add($"the stages that send mouse and key messages: {ex.Message}");
                Log($"SKIPPED [focus] {ex.Message}");
            }
        }

        if (_options.Wants("small") || _options.Wants("theme"))
        {
            await Guarded("small", SmallGraphAsync);
        }

        if (_options.Wants("input") && !_keyboardFocusUnavailable)
        {
            await Guarded("input", InteractionsAsync);
        }

        if (_options.Wants("bench"))
        {
            await Guarded("bench", StressAsync);
        }

        if (_options.Only == "exp" && !_keyboardFocusUnavailable)
        {
            await Guarded("exp", ExperimentAsync);
        }

        NoteForeground();
        var whileUsed = _takeovers.Count(t => t.IdleSeconds < AwayAfterSeconds);
        var summary = _takeovers.Count == 0 ? "no takeover at all; the foreground window was checked at every stage and at the end" : string.Join("; ", _takeovers.Select(t => $"one while nobody had touched the machine for {t.IdleSeconds:0} s, {(t.HandedBack ? "handed back at once" : "NOT handed back")}"));
        Check("session", "the harness window never took the foreground while somebody was using the machine", whileUsed == 0, summary);
        Check("session", "the harness window is never left as the foreground window", _takeovers.All(t => t.HandedBack) && WindowInput.GetForegroundWindow() != WindowHandle, $"{_takeovers.Count} takeover(s), the foreground belongs to another window at the end");
        Check("session", "no tool tip, menu or other popup window was ever created", !_sawPopup && PresentationSource.CurrentSources.OfType<HwndSource>().Count(s => !s.IsDisposed) == 1, "one native window (the off-screen host) existed at every checkpoint");
    }

    // One failing stage must not hide the others: record it and carry on.
    private async Task Guarded(string stage, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (StageSkippedException ex)
        {
            _report.Skipped.Add($"{stage}: {ex.Message}");
            Log($"SKIPPED [{stage}] {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(stage, "stage completed", false, ex.Message);
            Log(ex.ToString());
        }
        finally
        {
            NoteForeground();
        }
    }

    private void NoteForeground()
    {
        // The harness window is parked off-screen and must never keep the foreground. If WPF's keyboard focus (a Win32 SetFocus
        // inside this process) makes it the foreground window, hand the foreground back to the window that had it, at once, and
        // record the takeover with how long the user had been away (it is what decides whether Windows allows it).
        var foreground = WindowInput.GetForegroundWindow();
        if (foreground != WindowHandle)
        {
            _lastOtherForeground = foreground;
        }
        else
        {
            var idle = UserActivity.IdleSeconds();
            var target = _lastOtherForeground != 0 ? _lastOtherForeground : OffScreenHost.ForegroundBefore;
            var handedBack = target != 0 && WindowInput.IsWindow(target) && WindowInput.SetForegroundWindow(target) && WindowInput.GetForegroundWindow() != WindowHandle;
            _takeovers.Add((idle, handedBack));
            _report.ForegroundTakeovers.Add($"nobody had touched the machine for {idle:0} s; handed back: {handedBack}");
            Log($"WARNING the harness window became the foreground window ({_takeovers.Count}); nobody had touched the machine for {idle:0.0} s; it came from {WindowInput.Describe(target)}; handed back: {handedBack}; the foreground is now {WindowInput.Describe(WindowInput.GetForegroundWindow())}");
        }

        // Tool tips and menus are separate native windows that WPF keeps inside a monitor: none may ever exist.
        var windows = PresentationSource.CurrentSources.OfType<HwndSource>().Count(s => !s.IsDisposed);
        if (windows > 1 && !_sawPopup)
        {
            _sawPopup = true;
            Log($"WARNING {windows} native WPF windows exist: a popup was opened");
        }
    }

    private void RecordEnvironment()
    {
        var dpi = VisualTreeHelper.GetDpi(_window);
        _report.Environment = new EnvironmentInfo(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            RenderCapability.Tier >> 16,
            dpi.DpiScaleX,
            $"window client {_view.ActualWidth * dpi.DpiScaleX:0}x{_view.ActualHeight * dpi.DpiScaleY:0} px at {dpi.DpiScaleX * 96:0} dpi, off-screen; pictures rendered at {Screenshot.Dpi:0} dpi",
            typeof(NodifyEditor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?");
        Log($"environment: {_report.Environment}");
    }

    // ---------- Reporting ----------

    private async Task SampleLoadAsync(string label)
    {
        var load = await MachineLoad.MeasureAsync();
        _report.MachineLoad.Add($"{label}: {load:0} %");
        Log($"machine CPU load, {label}: {load:0} %");
    }

    private void Check(string area, string name, bool passed, string detail)
    {
        _report.Checks.Add(new CheckResult(area, name, passed, detail));
        Log($"{(passed ? "PASS" : "FAIL")} [{area}] {name} - {detail}");
    }

    private void Log(string message) => _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    private string ShotPath(string fileName) => Path.Combine(_options.OutDirectory, fileName);

    private void Shot(string fileName, CapturedImage? image = null)
    {
        var captured = image ?? Screenshot.Capture(_view);
        captured.Save(ShotPath(fileName));
        _report.Screenshots.Add(fileName);
        Log($"screenshot {fileName} ({captured.Width}x{captured.Height})");
    }

    // ---------- Waiting ----------

    /// <summary>Completes with true on the next rendered frame, or with false after the timeout (WPF may stop rendering a window nobody can see).</summary>
    private static async Task<bool> NextFrameAsync(int timeoutMilliseconds = 1500)
    {
        var done = new TaskCompletionSource();
        var seen = TimeSpan.MinValue;

        void OnRendering(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == seen)
            {
                return;
            }

            seen = time;
            CompositionTarget.Rendering -= OnRendering;
            done.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        if (await Task.WhenAny(done.Task, Task.Delay(timeoutMilliseconds)) == done.Task)
        {
            return true;
        }

        CompositionTarget.Rendering -= OnRendering;
        return false;
    }

    /// <summary>Lets layout, data binding and at least one frame finish.</summary>
    private static async Task SettleAsync(int extraMilliseconds = 150)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await NextFrameAsync();
        await Task.Delay(extraMilliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // Command states (Fit to screen, Delete) refresh on input; a script that loads graphs without input must ask for it.
        CommandManager.InvalidateRequerySuggested();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Runs <paramref name="load"/> and reports when the view model was built, a first frame drew and every wire got its anchors.</summary>
    private async Task<RenderTimes> MeasureLoadAsync(Action load, int wires)
    {
        var clock = Stopwatch.StartNew();
        load();
        var buildMs = clock.Elapsed.TotalMilliseconds;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await NextFrameAsync();
        var firstFrameMs = clock.Elapsed.TotalMilliseconds;
        await WaitUntilAsync(() => _vm.Connections.Count == wires && _vm.Connections.All(c => c.Source.Anchor != default && c.Target.Anchor != default), TimeSpan.FromSeconds(15));
        return new RenderTimes(Math.Round(buildMs, 1), Math.Round(firstFrameMs, 1), Math.Round(clock.Elapsed.TotalMilliseconds, 1));
    }

    // ---------- Input (window messages to this process's own window) ----------

    private nint WindowHandle => new WindowInteropHelper(_window).Handle;

    private async Task MoveToAsync((int X, int Y) point, int settleMilliseconds = 25)
    {
        _input.Move(point.X, point.Y);
        await Task.Delay(settleMilliseconds);
    }

    // Input delivered as window messages can, now and then, lose a press (WPF drops the mouse device whenever an element
    // releases the capture while the real cursor is elsewhere, and the user may be using the machine at the same time). A lost
    // press is easy to recognise: nothing captured the mouse. Clicks and drags are therefore retried when their effect is
    // missing, at most three times; a click is only repeated if its effect has not happened, so toggles cannot flip twice.

    /// <summary>Clicks until <paramref name="effect"/> holds (it is checked after each attempt); true when it did.</summary>
    private async Task<bool> ClickUntilAsync((int X, int Y) point, Func<bool> effect, MouseButton button = MouseButton.Left, int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await RetryPauseAsync(attempt);
            await MoveToAsync(point, 30);
            var before = InputState();
            _input.Click(button, point.X, point.Y);
            await Task.Delay(120);
            NoteForeground();
            if (effect())
            {
                return true;
            }

            Log($"click {button} at {point} had no effect (attempt {attempt} of {attempts}); the press was lost, trying again{Environment.NewLine}    before: {before}{Environment.NewLine}    after:  {InputState()}");
        }

        return false;
    }

    /// <summary>One line with what decides whether a mouse press is taken: who holds the capture, what is under the pointer, which buttons and keys WPF believes are down, and who has the focus.</summary>
    private string InputState()
    {
        static string Name(object? element) => element?.GetType().Name ?? "none";

        string Window(nint handle) => handle == 0 ? "none" : handle == WindowHandle ? "this window" : "another window";

        var chain = new List<string>();
        for (var current = Mouse.DirectlyOver as DependencyObject; current is not null && chain.Count < 6; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            chain.Add(current.GetType().Name);
        }

        return $"WPF capture {Name(Mouse.Captured)}; Win32 capture {Window(WindowInput.GetCapture())}; under the pointer {(chain.Count == 0 ? "nothing" : string.Join(" < ", chain))}; left {Mouse.LeftButton}, modifiers {Keyboard.Modifiers}; "
            + $"Win32 focus {Window(WindowInput.GetFocus())}, active {Window(WindowInput.GetActiveWindow())}, foreground {(WindowInput.GetForegroundWindow() == WindowHandle ? "this window" : "another window")}; keyboard focus on {Name(Keyboard.FocusedElement)}";
    }

    private Task ClickAsync((int X, int Y) point, MouseButton button = MouseButton.Left) => ClickUntilAsync(point, () => true, button, 1);

    /// <summary>
    /// A repeated press at the same spot within the system's double-click time is a double click to WPF (ClickCount 2): Nodify's
    /// gestures are plain clicks, so such a retry would be ignored however well it is delivered. Retries therefore wait it out.
    /// </summary>
    private static Task RetryPauseAsync(int attempt) => attempt > 1 ? Task.Delay((int)WindowInput.GetDoubleClickTime() + 100) : Task.CompletedTask;

    /// <summary>Press at <paramref name="from"/>, move in small steps to <paramref name="to"/>, optionally observe, release.</summary>
    /// <returns>True when the press was taken (something captured the mouse); a lost press is retried up to three times.</returns>
    private async Task<bool> DragAsync((int X, int Y) from, (int X, int Y) to, MouseButton button = MouseButton.Left, int steps = 24, int stepDelay = 12, Func<Task>? whileHeld = null)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await RetryPauseAsync(attempt);
            await MoveToAsync(from, 40);
            var before = InputState();
            _input.ButtonDown(button, from.X, from.Y);
            await Task.Delay(50);
            var began = Mouse.Captured is not null;
            for (var i = 1; i <= steps; i++)
            {
                _input.Move(from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps));
                await Task.Delay(stepDelay);
            }

            await Task.Delay(120);
            if (began && whileHeld is not null)
            {
                await whileHeld();
            }

            _input.ButtonUp(button, to.X, to.Y);
            await Task.Delay(150);
            NoteForeground();
            if (began)
            {
                return true;
            }

            Log($"drag {button} from {from} had no effect (attempt {attempt} of 3): the press was lost, trying again{Environment.NewLine}    before: {before}");
        }

        return false;
    }

    private async Task PressAsync(Key key, int settleMilliseconds = 120)
    {
        _input.Press(key);
        await Task.Delay(settleMilliseconds);
        NoteForeground();
    }

    /// <summary>
    /// Gives the editor keyboard focus. WPF then takes Win32 focus <i>inside this process</i> (the window is never activated and
    /// never becomes the foreground one), which is what lets it route key messages to the focused element.
    /// </summary>
    private async Task FocusEditorAsync()
    {
        await EnsureKeyboardFocusAsync();
        Check("input", "the editor has keyboard focus", _editor.IsKeyboardFocusWithin, $"focus within the editor: {_editor.IsKeyboardFocusWithin}; focused element {Keyboard.FocusedElement?.GetType().Name}; Win32 focus on the off-screen window: {WindowInput.GetFocus() == WindowHandle}");
    }

    /// <summary>
    /// WPF's keyboard focus is a Win32 <c>SetFocus</c> inside this process, and Windows can take it as a request to activate the
    /// window and make it the foreground one: the user's own program would lose the keyboard for a moment. It does so only when
    /// the foreground lock is off, and the lock is on for a few seconds after the user's own input (measured here: with the last
    /// input 0.2 s earlier nothing happened; with 10 s of silence the window did take the foreground, and the harness handed it
    /// back at once). Every later focus call is a no-op while the window keeps the focus of its own input queue, so only the first
    /// one matters. It is therefore taken (1) right after a keyboard or mouse input of the user, with a foreground window that
    /// does not belong to a program that launched this one (an editor or a terminal, which may take it regardless), or (2) when
    /// nobody has touched the machine for 30 s, when a takeover that is handed back within milliseconds disturbs nobody. If
    /// neither happens in 90 s the stages that press on elements are skipped (and reported as skipped) rather than risking it.
    /// </summary>
    private async Task EnsureKeyboardFocusAsync()
    {
        if (WindowInput.GetFocus() != WindowHandle)
        {
            var started = DateTime.UtcNow;
            string mode;
            while (true)
            {
                var ancestor = ProcessTree.IsAncestorWindow(WindowInput.GetForegroundWindow());
                var idle = UserActivity.IdleSeconds();
                if (!ancestor && idle < ActiveWithinSeconds)
                {
                    mode = $"the last keyboard or mouse input was {idle:0.00} s ago, so the foreground lock is on";
                    break;
                }

                if (idle >= AwayAfterSeconds)
                {
                    mode = $"nobody has touched the machine for {idle:0} s: a takeover, if Windows allows one, disturbs nobody and is handed back at once";
                    break;
                }

                var waited = (DateTime.UtcNow - started).TotalSeconds;
                if (waited > 90)
                {
                    throw new StageSkippedException(ancestor
                        ? "somebody is using the machine with a foreground window that belongs to a program that launched this one (an editor or a terminal): taking the keyboard focus could take the foreground from it. Run it again while another program is in front, or leave the machine alone for 30 s."
                        : $"somebody is around (the last input was {idle:0} s ago) but touched neither the keyboard nor the mouse in 90 s: the keyboard focus was not taken. Run it again and use the mouse for a moment, or leave the machine alone for 30 s.");
                }

                if (waited < 0.1 || waited % 10 < 0.05)
                {
                    Log($"waiting to take the keyboard focus: {(ancestor ? "the foreground window belongs to a program that launched this one" : $"the last keyboard or mouse input was {idle:0.0} s ago")}");
                }

                await Task.Delay(25);
            }

            Log($"taking the keyboard focus: {mode}");
        }

        _lastOtherForeground = WindowInput.GetForegroundWindow();
        var clock = Stopwatch.StartNew();
        _editor.Focus();
        NoteForeground(); // immediately, without yielding: if the window did take the foreground, hand it back at once
        var milliseconds = clock.Elapsed.TotalMilliseconds;
        await SettleAsync(60);
        Log($"the focus call (and the hand-back, if any) took {milliseconds:0.0} ms; Win32 focus on the off-screen window: {WindowInput.GetFocus() == WindowHandle}; foreground at the start {WindowInput.Describe(OffScreenHost.ForegroundBefore)}, after showing the host {WindowInput.Describe(OffScreenHost.ForegroundAfterShow)}, now {WindowInput.Describe(WindowInput.GetForegroundWindow())}");
        NoteForeground();
    }

    private sealed class StageSkippedException(string reason) : Exception(reason);

    // ---------- Locating elements ----------

    private (int X, int Y) EditorPoint(double x, double y) => VisualTree.ClientPoint(_editor, x, y);

    private Connector ConnectorOf(ConnectorViewModel connector) =>
        VisualTree.Descendants<Connector>(_editor).First(c => ReferenceEquals(c.DataContext, connector));

    private ItemContainer ContainerOf(NodeViewModel node) =>
        VisualTree.Descendants<ItemContainer>(_editor).First(c => ReferenceEquals(c.DataContext, node));

    /// <summary>The round thumb of a connector (inset from the side the wire leaves from).</summary>
    private (int X, int Y) ConnectorPoint(ConnectorViewModel connector)
    {
        var element = ConnectorOf(connector);
        var inset = Math.Min(11, element.ActualWidth / 2);
        return VisualTree.ClientPoint(element, connector.Flow == ConnectorFlow.Input ? inset : element.ActualWidth - inset, element.ActualHeight / 2);
    }

    /// <summary>A spot on the node header that is not a connector, where a left-drag moves the node.</summary>
    private (int X, int Y) NodeHeaderPoint(NodeViewModel node)
    {
        var container = ContainerOf(node);
        return VisualTree.ClientPoint(container, Math.Min(60, container.ActualWidth / 2), 16);
    }

    private System.Windows.Controls.Button PaletteButton(WorkTaskKind kind) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(_view).First(b => b.DataContext is PaletteItem item && item.Kind == kind);
}
