using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentStudio.Desktop.Localization;
using Microsoft.Web.WebView2.Core;

namespace AgentStudio.Desktop.Controls.Terminal;

/// <summary>
/// Shows a <see cref="TerminalViewModel"/> with xterm.js inside WebView2. The page is served from a virtual host
/// (no CDN, no network), navigation away from it is blocked, and the bridge only exchanges JSON messages.
/// </summary>
public partial class TerminalControl : UserControl
{
    internal const string HostName = "agentstudio.terminal";
    private const int MaxChunkChars = 64 * 1024;

    private readonly TerminalOutputBatcher _batcher = new();
    private TerminalViewModel? _viewModel;
    private bool _initializing;
    private bool _flushScheduled;
    private bool _pageReady;

    public TerminalControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.OutputReceived -= OnOutputReceived;
            _viewModel.Cleared -= OnCleared;
        }

        _viewModel = e.NewValue as TerminalViewModel;
        if (_viewModel is not null)
        {
            _viewModel.OutputReceived += OnOutputReceived;
            _viewModel.Cleared += OnCleared;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializing || WebView.CoreWebView2 is not null)
        {
            return;
        }

        _initializing = true;
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError(Texts.Get("Terminal_WebViewMissing"));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or ArgumentException)
        {
            ShowError(Texts.Format("Terminal_ViewerStartFailed", ex.Message));
        }
        finally
        {
            _initializing = false;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var pageFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
        if (!File.Exists(Path.Combine(pageFolder, "xterm", "xterm.js")) || !File.Exists(Path.Combine(pageFolder, "index.html")))
        {
            ShowError(Texts.Get("Terminal_AssetsMissing"));
            return;
        }

        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await WebView.EnsureCoreWebView2Async(environment);

        var core = WebView.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.SetVirtualHostNameToFolderMapping(HostName, pageFolder, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, args) =>
        {
            if (!IsTerminalUri(args.Uri))
            {
                args.Cancel = true;
                return;
            }

            // A (re)load: the page sends "ready" again once xterm.js is up; output waits until then.
            _pageReady = false;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;

        core.Navigate($"https://{HostName}/index.html");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _pageReady = false;
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && WebView.CoreWebView2 is { } core)
        {
            // The renderer died: reload the page. The shell keeps running; the screen starts blank and shows new output.
            core.Reload();
            return;
        }

        ShowError(Texts.Get("Terminal_ViewerFailed"));
    }

    internal static bool IsTerminalUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTerminalUri(e.Source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _pageReady = true;
                    _viewModel?.ViewReady(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                    ScheduleFlush();
                    break;
                case "resize":
                    _viewModel?.ViewResized(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                    break;
                case "data":
                    if (_viewModel is not null && root.GetProperty("data").GetString() is { } data)
                    {
                        _ = _viewModel.SendInputAsync(data);
                    }

                    break;
                case "error":
                    ShowError(root.GetProperty("message").GetString() ?? "Error desconocido en la terminal.");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Malformed message from the page: ignore it.
        }
    }

    private void OnOutputReceived(string text)
    {
        if (_batcher.Append(text))
        {
            Dispatcher.BeginInvoke(ScheduleFlush, DispatcherPriority.Normal);
        }
    }

    private void OnCleared()
    {
        _batcher.Clear();
        Dispatcher.BeginInvoke(() => Post(new { type = "reset" }), DispatcherPriority.Normal);
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled || !_pageReady)
        {
            return;
        }

        _flushScheduled = true;
        // Background priority lets output that arrives meanwhile join the same message.
        Dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        _flushScheduled = false;
        if (WebView.CoreWebView2 is null)
        {
            return;
        }

        if (_batcher.Take(MaxChunkChars) is { } chunk)
        {
            Post(new { type = "write", data = chunk });
        }

        if (_batcher.HasPending)
        {
            ScheduleFlush();
        }
    }

    private void Post(object message)
    {
        if (WebView.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    private void ShowError(string text)
    {
        ErrorBlock.Text = text;
        ErrorBlock.Visibility = Visibility.Visible;
    }
}
