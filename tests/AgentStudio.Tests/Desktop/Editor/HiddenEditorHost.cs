using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Interop;
using AgentStudio.Desktop.Controls.Editor;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentStudio.Tests.Desktop.Editor;

/// <summary>
/// The real <see cref="CodeEditorControl"/>, with the editor page and Monaco running in the installed WebView2 Runtime,
/// inside a window that is never shown. The window is created without WS_VISIBLE (which the default
/// <see cref="HwndSourceParameters"/> style includes), as a tool window that cannot be activated, far off screen, and
/// nothing calls Show, Activate or Focus on it. WPF still reports the root of a PresentationSource as visible, and the
/// WPF WebView2 control passes that on to its controller, so Chromium treats the page as visible: Monaco renders and
/// its timers are not throttled. (A controller left hidden, or one under an HWND_MESSAGE parent, keeps the page
/// "hidden": timers fire about once a second and nothing is rendered.)
/// <para>
/// While it runs, a background thread checks that the window never becomes visible and that neither it nor any window
/// of its WebView2 processes becomes the foreground window. <see cref="RunAsync{T}"/> fails the test if that happens,
/// if a WebView2 process outlives the editor or if the editor logged a warning or an error (a page message the bridge
/// could not parse, a failed WebView2 process). The WebView2 profile lives in the given work folder, never in the
/// application's, so a running Agent Studio is not touched.
/// </para>
/// </summary>
internal sealed class HiddenEditorHost
{
    /// <summary>Upper bound for any single wait. Starting the WebView2 browser process took 0.5 to 6 s when measured.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the WebView2 processes may take to exit once the editor is closed: 0.3 to 5 s when measured, more on a
    /// loaded machine (several test suites at once). Only a process still running after this counts as left behind.
    /// </summary>
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly string _userDataFolder;
    private readonly ITestOutputHelper? _output;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RecordingLogger _logger = new();
    private readonly List<string> _problems = [];
    private readonly List<string> _marks = [];
    private readonly Dictionary<int, TrackedProcess?> _processes = [];
    private readonly List<ForegroundSample> _foregroundSamples = [];
    private readonly TaskCompletionSource _processesExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _startedAt;
    private CoreWebView2Environment? _environment;
    private HwndSource? _window;
    private IntPtr _windowHandle;
    private CodeEditorControl? _control;
    private EditorDocument? _document;
    private WebView2? _webView;
    private Thread? _watcher;
    private int _stopWatching;
    private int _becameVisible;
    private IntPtr _foregroundBefore;
    private IntPtr _foregroundAfter;

    private HiddenEditorHost(string workFolder, ITestOutputHelper? output)
    {
        _userDataFolder = Path.Combine(workFolder, "webview2-" + Guid.NewGuid().ToString("N"));
        _output = output;
    }

    public CodeEditorControl Control => _control ?? throw new InvalidOperationException("The editor has not started.");

    public EditorDocument Document => _document ?? throw new InvalidOperationException("The editor has not started.");

    /// <summary>Skips the test on a machine without the WebView2 Runtime or without the Monaco resources.</summary>
    public static void SkipUnlessAvailable()
    {
        string? runtime = null;
        try
        {
            runtime = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Reported by the skip below.
        }

        Assert.SkipWhen(string.IsNullOrEmpty(runtime), "The WebView2 Runtime is not installed.");
        var unavailable = EditorAssets.Default.GetUnavailableReason();
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in a never-shown editor on the WPF test thread, runs <paramref name="body"/> there,
    /// closes everything (waiting for the WebView2 processes to exit) and then fails if the window was shown, took the
    /// foreground or left a process behind.
    /// </summary>
    public static Task<T> RunAsync<T>(string path, string workFolder, Func<HiddenEditorHost, Task<T>> body)
    {
        var output = TestContext.Current.TestOutputHelper;
        return WpfHost.RunAsync(async () =>
        {
            var host = new HiddenEditorHost(workFolder, output);
            T result;
            try
            {
                await host.StartAsync(path);
                result = await body(host);
            }
            finally
            {
                await host.CloseAsync();
            }

            host.AssertStayedHiddenAndInBackground();
            return result;
        });
    }

    /// <summary>Runs a script in the page and returns its result as JSON.</summary>
    public async Task<JsonElement> EvaluateAsync(string script)
    {
        ThrowIfUnavailable();
        var json = await _webView!.CoreWebView2.ExecuteScriptAsync(script);
        using var result = JsonDocument.Parse(json);
        return result.RootElement.Clone();
    }

    /// <summary>
    /// Evaluates <paramref name="script"/> until <paramref name="done"/> accepts its result or <see cref="Timeout"/>
    /// passes, and returns the last result either way so the test can assert on it.
    /// </summary>
    public async Task<JsonElement> WaitForAsync(string script, Func<JsonElement, bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var state = await EvaluateAsync(script);
            if (done(state) || deadline.Elapsed > Timeout)
            {
                return state;
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds on the WPF thread; false when it never did.</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            ThrowIfUnavailable();
            if (deadline.Elapsed > Timeout)
            {
                return false;
            }

            await Task.Delay(PollInterval);
        }

        return true;
    }

    private async Task StartAsync(string path)
    {
        _foregroundBefore = Native.GetForegroundWindow();
        _startedAt = DateTime.Now;
        _environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _userDataFolder);
        _environment.BrowserProcessExited += (_, e) =>
        {
            // Raised once every process of this browser has exited and the profile folder is released.
            Mark($"WebView2 processes exited ({e.BrowserProcessExitKind})");
            _processesExited.TrySetResult();
        };
        Mark("environment");
        _document = await EditorDocument.OpenAsync(path);

        _control = new CodeEditorControl();
        _control.Configure(assets: null, _logger, _environment);
        _webView = (WebView2)_control.FindName("WebView");
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _webView.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                initialized.TrySetResult();
            }
            else
            {
                initialized.TrySetException(e.InitializationException);
            }
        };
        _control.Document = _document;

        _window = new HwndSource(new HwndSourceParameters("Agent Studio editor test (never shown)")
        {
            WindowStyle = Native.WsPopup | Native.WsClipChildren,
            ExtendedWindowStyle = Native.WsExToolWindow | Native.WsExNoActivate,
            PositionX = -20000,
            PositionY = -20000,
            Width = 1024,
            Height = 768,
        });
        _windowHandle = _window.Handle;
        StartWatching();

        // Attaching the control to the window loads it: that is what creates the WebView2 and navigates to the page.
        _window.RootVisual = _control;
        await initialized.Task.WaitAsync(Timeout);
        Mark("WebView2 ready");
        Track((int)_webView.CoreWebView2.BrowserProcessId, "Browser");
    }

    private void Mark(string step) => _marks.Add($"{step} {_clock.ElapsedMilliseconds} ms");

    private async Task CloseAsync()
    {
        Mark("test done");
        TrackEnvironmentProcesses();
        if (_windowHandle != IntPtr.Zero)
        {
            if ((Native.GetWindowLongPtr(_windowHandle, Native.GwlStyle).ToInt64() & Native.WsVisible) != 0)
            {
                _problems.Add("The editor window has the WS_VISIBLE style.");
            }

            _problems.AddRange(VisibleWindowsOf(TrackedProcessIds()).Select(window => "A WebView2 process shows a window: " + window));
        }

        _control?.Dispose();
        if (_window is not null)
        {
            _window.RootVisual = null;
            _window.Dispose();
        }

        Volatile.Write(ref _stopWatching, 1);
        _watcher?.Join();
        _foregroundAfter = Native.GetForegroundWindow();

        await WaitForProcessesToExitAsync();
        await DeleteUserDataFolderAsync();
        _output?.WriteLine(
            $"{string.Join(", ", _marks)}; {TrackedProcessIds().Count()} WebView2 processes waited for; "
            + $"foreground before 0x{_foregroundBefore:X}, after 0x{_foregroundAfter:X}.");
    }

    private void AssertStayedHiddenAndInBackground()
    {
        var problems = new List<string>(_problems);
        if (Volatile.Read(ref _becameVisible) != 0)
        {
            problems.Add("The editor window became visible.");
        }

        lock (_foregroundSamples)
        {
            problems.AddRange(_foregroundSamples.Where(IsOurs).Select(sample =>
                $"Window 0x{sample.Window:X} of process {sample.ProcessId} became the foreground window."));
        }

        if (IsOurs(ForegroundSample.Of(_foregroundAfter)))
        {
            problems.Add($"The editor left window 0x{_foregroundAfter:X} in the foreground.");
        }

        problems.AddRange(_logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry =>
            $"The editor logged {entry.Level}: {entry.Message}"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private bool IsOurs(ForegroundSample sample) =>
        sample.Window != IntPtr.Zero
        && (sample.Window == _windowHandle || sample.RootOwner == _windowHandle || TrackedProcessIds().Contains(sample.ProcessId));

    private IEnumerable<int> TrackedProcessIds() => _processes.Where(entry => entry.Value is not null).Select(entry => entry.Key);

    private void StartWatching()
    {
        var window = _windowHandle;
        _watcher = new Thread(() =>
        {
            var last = IntPtr.MinValue;
            while (Volatile.Read(ref _stopWatching) == 0)
            {
                var foreground = Native.GetForegroundWindow();
                if (foreground != last)
                {
                    last = foreground;
                    lock (_foregroundSamples)
                    {
                        _foregroundSamples.Add(ForegroundSample.Of(foreground));
                    }
                }

                if (Native.IsWindowVisible(window))
                {
                    Volatile.Write(ref _becameVisible, 1);
                }

                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
            Name = "Editor test window watcher",
        };
        _watcher.Start();
    }

    private void TrackEnvironmentProcesses()
    {
        if (_environment is null)
        {
            return;
        }

        try
        {
            foreach (var process in _environment.GetProcessInfos())
            {
                Track(process.ProcessId, process.Kind.ToString());
            }
        }
        catch (COMException ex)
        {
            _problems.Add("Could not list the WebView2 processes: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens a process of this editor's WebView2 so that its id cannot be reused while the test waits for it. A process
    /// that exited may still be listed and its id may already belong to someone else: only an msedgewebview2 started
    /// after this editor is kept, so the test never waits for (or kills) a process that is not its own.
    /// </summary>
    private void Track(int processId, string kind)
    {
        if (_processes.ContainsKey(processId))
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            _ = process.SafeHandle;
            if (string.Equals(process.ProcessName, "msedgewebview2", StringComparison.OrdinalIgnoreCase)
                && process.StartTime >= _startedAt.AddSeconds(-1))
            {
                _processes[processId] = new TrackedProcess(process, kind);
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to open.
        }

        process?.Dispose();
        _processes[processId] = null;
    }

    private async Task WaitForProcessesToExitAsync()
    {
        var waited = Stopwatch.StartNew();
        if (_processes.Values.Any(tracked => tracked is not null))
        {
            try
            {
                await _processesExited.Task.WaitAsync(ProcessExitTimeout);
            }
            catch (TimeoutException)
            {
                // Each process still running is reported (and killed) below.
            }
        }

        foreach (var (processId, tracked) in _processes)
        {
            if (tracked is null)
            {
                continue;
            }

            using (tracked.Process)
            {
                var remaining = ProcessExitTimeout - waited.Elapsed;
                using var timeout = new CancellationTokenSource(remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                try
                {
                    await tracked.Process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    _problems.Add($"The WebView2 {tracked.Kind} process {processId} was still running {ProcessExitTimeout.TotalSeconds:0} s after the editor closed; it was killed.");
                    try
                    {
                        tracked.Process.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        // It exited meanwhile.
                    }
                }
            }
        }

        Mark("processes gone");
    }

    private async Task DeleteUserDataFolderAsync()
    {
        for (var attempt = 1; Directory.Exists(_userDataFolder); attempt++)
        {
            try
            {
                Directory.Delete(_userDataFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    _problems.Add("Could not delete the WebView2 profile folder: " + ex.Message);
                    return;
                }

                await Task.Delay(100);
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_control is { IsUnavailable: true })
        {
            throw new InvalidOperationException("The editor is unavailable: " + ((TextBlock)_control.FindName("UnavailableText")).Text);
        }
    }

    private static List<string> VisibleWindowsOf(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet();
        var visible = new List<string>();
        Native.EnumWindows(
            (window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var processId);
                if (ids.Contains((int)processId) && Native.IsWindowVisible(window))
                {
                    visible.Add($"0x{window:X} (process {processId})");
                }

                return true;
            },
            IntPtr.Zero);
        return visible;
    }

    private sealed record TrackedProcess(Process Process, string Kind);

    private readonly record struct ForegroundSample(IntPtr Window, int ProcessId, IntPtr RootOwner)
    {
        public static ForegroundSample Of(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return default;
            }

            Native.GetWindowThreadProcessId(window, out var processId);
            return new ForegroundSample(window, (int)processId, Native.GetAncestor(window, Native.GaRootOwner));
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static class Native
    {
        public const int GwlStyle = -16;
        public const int WsPopup = unchecked((int)0x80000000);
        public const int WsClipChildren = 0x02000000;
        public const long WsVisible = 0x10000000;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const uint GaRootOwner = 3;

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    }
}
