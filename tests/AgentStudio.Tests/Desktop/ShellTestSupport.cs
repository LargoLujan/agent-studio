using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop;

/// <summary>Self-deleting temporary directory.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentstudio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Full path under this directory; accepts "/" and normalizes it.</summary>
    public string Combine(params string[] parts) => System.IO.Path.GetFullPath(System.IO.Path.Combine([Path, .. parts]));

    /// <summary>Creates a file (and its folders) under this directory and returns its full path.</summary>
    public string WriteFile(string relativePath, string content = "")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Best effort: links inside are removed as links, and a folder that cannot go is noted in the test output.</summary>
    public void Dispose() => TempFolders.Delete(Path);
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Result { get; set; }

    public int Calls { get; private set; }

    public string? PickFolder(string title)
    {
        Calls++;
        return Result;
    }
}

internal sealed class FakeUnsavedChangesPrompt(UnsavedChangesChoice answer = UnsavedChangesChoice.Cancel) : IUnsavedChangesPrompt
{
    public UnsavedChangesChoice Answer { get; set; } = answer;

    public List<(IReadOnlyList<string> Titles, bool CanSave)> Calls { get; } = [];

    public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave)
    {
        Calls.Add((titles, canSave));
        return Answer;
    }
}

internal sealed class InMemorySettingsStore(AppSettings? initial = null) : IAppSettingsStore
{
    public AppSettings Current { get; set; } = initial ?? AppSettings.Default;

    public int Saves { get; private set; }

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        Saves++;
    }
}

/// <param name="method">The way it authenticates, as the probe names it; null when it found none (Gemini without an API key).</param>
internal sealed class StubProbe(string name, InstallationState installation, AuthenticationState authentication, string? method = "oauth") : IProviderProbe
{
    public ProviderId Provider { get; } = new(name);

    public VendorId Vendor => VendorId.Anthropic;

    public string DisplayName => name;

    public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderProbeResult(Provider, name, installation, null, "1.0", authentication, method, []));
}

internal static class ShellFactory
{
    public static MainViewModel CreateMain(
        IAppSettingsStore? settings = null,
        IFolderPicker? picker = null,
        TimeProvider? time = null,
        params IProviderProbe[] probes)
    {
        return CreateMain(new FakeUnsavedChangesPrompt(), settings, picker, time, probes);
    }

    public static MainViewModel CreateMain(
        IUnsavedChangesPrompt prompt,
        IAppSettingsStore? settings = null,
        IFolderPicker? picker = null,
        TimeProvider? time = null,
        params IProviderProbe[] probes)
    {
        var clock = time ?? TimeProvider.System;
        return new MainViewModel(
            settings ?? new InMemorySettingsStore(),
            picker ?? new FakeFolderPicker(),
            prompt,
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics(probes), clock),
            new ExplorerViewModel(),
            new JournalViewModel(clock),
            new QuickOpenViewModel());
    }
}

/// <summary>Raises keyboard events on views that are built but never shown.</summary>
internal static class TestKeys
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>Raises the tunnelling key-down event on <paramref name="target"/>; returns whether it ended up handled.</summary>
    public static bool PreviewKeyDown(UIElement target, Key key)
    {
        // The input source is a real window. The default HwndSourceParameters style includes WS_VISIBLE, which showed
        // it on screen and gave it the foreground: this one is a popup tool window that cannot be activated, far off
        // screen, created without WS_VISIBLE and never shown.
        using var source = new HwndSource(new HwndSourceParameters("TestKeys")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
            PositionX = -20000,
            PositionY = -20000,
            Width = 1,
            Height = 1,
        });
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }
}

/// <summary>
/// One STA thread with a running Dispatcher and the application resources (theme and document templates),
/// shared by every WPF test: a process can only host one System.Windows.Application.
/// </summary>
internal static class WpfHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    public static T Run<T>(Func<T> action) => Host.Value.Invoke(action);

    public static void Run(Action action) => Host.Value.Invoke(action);

    /// <summary>
    /// Lets the pending data bindings and triggers of freshly built or re-bound views run. WPF queues their activation
    /// below the Normal priority a test body runs at, and spreads a big backlog (a window that was just shown, say)
    /// over several slices, so waiting for DataBind priority is not enough: wait until the dispatcher is idle.
    /// Call it on the UI thread, inside Run.
    /// </summary>
    public static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Runs async code on the UI thread without blocking it (continuations need the dispatcher).</summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> action) => Host.Value.InvokeAsync(action).Task.Unwrap();

    private static Dispatcher Start()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AgentStudio.Desktop;component/Themes/Dark.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AgentStudio.Desktop;component/Themes/DocumentTemplates.xaml") });
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
