using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>
/// Monaco hosted in WebView2. Bind <see cref="Document"/>; the control loads it, keeps
/// <see cref="EditorDocument.IsDirty"/> up to date and saves on Ctrl+S. Use one control per open document: the
/// undo history and unsaved text live inside the page. Call <see cref="Dispose"/> when the document is closed
/// (not on <c>Unloaded</c>: tab switches unload and reload the visual tree).
/// </summary>
public partial class CodeEditorControl : UserControl, IDisposable
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document),
        typeof(EditorDocument),
        typeof(CodeEditorControl),
        new PropertyMetadata(null, (d, e) => ((CodeEditorControl)d).OnDocumentChanged((EditorDocument?)e.NewValue)));

    private static Task<CoreWebView2Environment>? sharedEnvironment;

    private readonly EditorBridge _bridge;
    private EditorAssets _assets = EditorAssets.Default;
    private ILogger _logger = NullLogger.Instance;
    private CoreWebView2Environment? _environment;
    private bool _initializing;
    private bool _disposed;

    public CodeEditorControl()
    {
        InitializeComponent();
        _bridge = new EditorBridge(PostToPage);
        _bridge.Changed += OnChanged;
        _bridge.SaveRequested += OnSaveRequested;
        _bridge.Failed += (_, message) => ShowUnavailable("Editor no disponible: " + message);
        UnavailableText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Loaded += OnLoaded;
    }

    public EditorDocument? Document
    {
        get => (EditorDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>True when the editor cannot run (missing Monaco, missing WebView2 runtime, page failure).</summary>
    public bool IsUnavailable => UnavailableText.Visibility == Visibility.Visible;

    /// <summary>Raised when saving fails (read-only file, locked file, ...). The text stays dirty.</summary>
    public event EventHandler<UnhandledExceptionEventArgs>? SaveFailed;

    /// <summary>
    /// Optional: where Monaco is, the shell logger for bridge diagnostics and the WebView2 environment (by default one
    /// shared by every editor, with its profile in %LOCALAPPDATA%\AgentStudio\WebView2). Set before the control is loaded.
    /// </summary>
    public void Configure(EditorAssets? assets, ILogger? logger, CoreWebView2Environment? environment = null)
    {
        _assets = assets ?? EditorAssets.Default;
        _logger = logger ?? NullLogger.Instance;
        _bridge.Logger = _logger;
        _environment = environment;
    }

    /// <summary>Shows a read-only comparison of two texts. <see cref="HideDiff"/> returns to the editor.</summary>
    public void ShowDiff(string original, string modified, string? language = null) =>
        _bridge.ShowDiff(original, modified, language ?? Document?.Language ?? LanguageMap.PlainText);

    public void HideDiff() => _bridge.HideDiff();

    /// <summary>Compares the text on disk with what is currently in the editor.</summary>
    public Task ShowDiffAgainstSavedAsync(CancellationToken cancellationToken = default) =>
        Document is { } document ? ShowDiffAgainstAsync(document.SavedText, null, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Shows <paramref name="original"/> next to what is currently in the editor, unsaved edits included, as a
    /// read-only comparison. <see cref="HideDiff"/> returns to the editor.
    /// </summary>
    /// <exception cref="TimeoutException">The page did not return the editor text in time.</exception>
    public async Task ShowDiffAgainstAsync(string original, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (Document is null)
        {
            return;
        }

        var current = await _bridge.RequestContentAsync(cancellationToken: cancellationToken);
        ShowDiff(original, current.Text, language);
    }

    public new bool Focus()
    {
        _bridge.Focus();
        return base.Focus();
    }

    /// <summary>Fetches the text from the page and writes it to disk, then clears the dirty state.</summary>
    /// <exception cref="FileChangedOnDiskException">The file changed on disk since it was loaded or last saved.</exception>
    public Task SaveAsync(CancellationToken cancellationToken = default) => SaveAsync(false, cancellationToken);

    /// <summary>
    /// Same as <see cref="SaveAsync(CancellationToken)"/>; with <paramref name="overwriteExternalChanges"/> the file is
    /// written even if it changed on disk.
    /// </summary>
    public async Task SaveAsync(bool overwriteExternalChanges, CancellationToken cancellationToken = default)
    {
        var document = Document;
        if (document is null || document.IsReadOnly || _disposed)
        {
            return;
        }

        var content = await _bridge.RequestContentAsync(cancellationToken: cancellationToken);
        await document.SaveAsync(content.Text, cancellationToken, overwriteExternalChanges);
        if (!_disposed)
        {
            _bridge.MarkSaved(content.VersionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge.CancelPending();
        WebView.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnDocumentChanged(EditorDocument? document)
    {
        if (document is not null)
        {
            _bridge.Open(document.SavedText, document.Language, document.IsReadOnly);
        }
    }

    private void OnChanged(object? sender, ChangedMessage message)
    {
        Document?.SetDirty(message.IsDirty);
    }

    private async void OnSaveRequested(object? sender, EventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
            or ObjectDisposedException)
        {
            if (_disposed)
            {
                return; // The tab was closed while saving: nobody is left to tell.
            }

            _logger.LogError(ex, "Saving {Path} failed", Document?.Path);
            SaveFailed?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
        }
    }

    private void PostToPage(string json)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The WebView was disposed or its renderer died meanwhile; ProcessFailed reports the latter.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializing || WebView.CoreWebView2 is not null || _disposed)
        {
            return;
        }

        _initializing = true;
        try
        {
            if (_assets.GetUnavailableReason() is { } reason)
            {
                ShowUnavailable(reason);
                return;
            }

            var environment = _environment ?? await GetEnvironmentAsync();
            if (_disposed)
            {
                return; // The tab was closed while the environment was being created.
            }

            await WebView.EnsureCoreWebView2Async(environment);
            if (_disposed || WebView.CoreWebView2 is not { } core)
            {
                return; // The tab was closed during initialization: the WebView is gone.
            }

            ConfigureCore(core);
            core.Navigate(_assets.PageUrl);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogError(ex, "The editor could not start");
            ShowUnavailable("Editor no disponible: no se pudo iniciar WebView2 (" + ex.Message + ")");
        }
        finally
        {
            _initializing = false;
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.SetVirtualHostNameToFolderMapping(
            EditorAssets.HostName,
            _assets.Folder,
            CoreWebView2HostResourceAccessKind.DenyCors);

        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreHostObjectsAllowed = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

        core.NavigationStarting += (_, args) =>
        {
            if (!EditorAssets.IsAllowedNavigation(args.Uri))
            {
                _logger.LogWarning("Blocked editor navigation to {Uri}", args.Uri);
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.ProcessFailed += (_, args) => OnProcessFailed(args);
        core.WebMessageReceived += (_, args) =>
        {
            if (!EditorAssets.IsAllowedNavigation(args.Source))
            {
                _logger.LogWarning("Ignored editor message from {Source}", args.Source);
                return;
            }

            _bridge.HandleWebMessage(args.WebMessageAsJson);
        };
    }

    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogError("The editor WebView2 process failed: {Kind}", args.ProcessFailedKind);
        // Reloading would silently drop the text that is not saved yet, so say what happened instead.
        ShowUnavailable("El editor se ha detenido de forma inesperada (el proceso del visor WebView2 ha fallado)."
            + " Los cambios sin guardar de esta pestaña pueden haberse perdido. Cierra la pestaña y vuelve a abrir el fichero.");
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (sharedEnvironment is { IsFaulted: false })
        {
            return sharedEnvironment;
        }

        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStudio",
            "WebView2");
        return sharedEnvironment = CoreWebView2Environment.CreateAsync(null, userData);
    }

    private void ShowUnavailable(string message)
    {
        WebView.Visibility = Visibility.Collapsed;
        UnavailableText.Text = message;
        UnavailableText.Visibility = Visibility.Visible;
    }
}
