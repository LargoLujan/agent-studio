using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Desktop.Controls.Editor;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Views;

/// <summary>
/// Shows a file in the Monaco editor, or in the read-only viewer when it cannot be edited. The WebView2 inside the
/// editor survives tab switches (which unload the visual tree) and is released only when the document is closed.
/// </summary>
public partial class FileEditorView : UserControl
{
    private FileDocumentViewModel? _document;

    public FileEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Editor.SaveFailed += OnSaveFailed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Esc leaves the comparison, and only while one is on screen. It is not a KeyBinding on purpose: WPF marks a key
    /// handled as soon as a binding matches it, even when the bound command cannot run, so the page would never get
    /// its own Esc back (Monaco closes its find widget and suggestions with it). WebView2 hands Esc to WPF first, so
    /// this also works with the focus inside the editor.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _document is { IsComparing: true } document)
        {
            e.Handled = true;
            document.CloseDiffCommand.Execute(null);
        }
    }

    /// <summary>A failed save (read-only or locked file, ...) must be seen: the tab simply stays dirty otherwise.</summary>
    private void OnSaveFailed(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is FileChangedOnDiskException changed)
        {
            _ = OfferOverwriteAsync(changed);
            return;
        }

        var reason = (e.ExceptionObject as Exception)?.Message ?? "error desconocido";
        var text = $"No se pudo guardar {_document?.Title ?? "el fichero"}: {reason}\n\nLos cambios siguen en el editor sin guardar.";
        if (Window.GetWindow(this) is { } owner)
        {
            MessageBox.Show(owner, text, "Guardar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(text, "Guardar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The file changed on disk (typically an agent edited it). Saving over it needs an explicit yes; the editor text
    /// is kept either way.
    /// </summary>
    private async Task OfferOverwriteAsync(FileChangedOnDiskException changed)
    {
        var title = _document?.Title ?? "el fichero";
        var what = changed.Deleted ? "se ha eliminado en disco" : "ha cambiado en disco";
        var text = $"{title} {what} desde que se abrió.\n\n"
            + "¿Quieres sobrescribirlo con el contenido del editor? Los cambios hechos fuera se perderán.\n"
            + "Los cambios del editor siguen sin guardar si eliges No.";
        var owner = Window.GetWindow(this);
        var answer = owner is not null
            ? MessageBox.Show(owner, text, "Sobrescribir", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(text, "Sobrescribir", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Editor.SaveAsync(overwriteExternalChanges: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
            or InvalidOperationException)
        {
            OnSaveFailed(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
            _document.Closed -= OnDocumentClosed;
            _document.SetSaveHandler(null);
            _document.SetDiffHandler(null);
        }

        _document = e.NewValue as FileDocumentViewModel;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
            _document.Closed += OnDocumentClosed;
        }

        UpdateMode();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileDocumentViewModel.Editor))
        {
            UpdateMode();
        }
    }

    private void UpdateMode()
    {
        var editable = _document?.Editor is not null;
        _document?.SetSaveHandler(editable ? Editor.SaveAsync : null);
        _document?.SetDiffHandler(editable ? ForwardDiffRequestAsync : null);
        Editor.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        Fallback.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Passes a comparison the view model asked for on to the editor; the text itself lives in the page.</summary>
    private async Task ForwardDiffRequestAsync(DiffRequest request, CancellationToken cancellationToken)
    {
        if (Editor.IsUnavailable)
        {
            throw new InvalidOperationException("The editor is not available, so there is nothing to compare.");
        }

        switch (request)
        {
            case CompareWithSavedRequest:
                await Editor.ShowDiffAgainstSavedAsync(cancellationToken);
                break;
            case CompareWithTextRequest other:
                await Editor.ShowDiffAgainstAsync(other.OriginalText, language: null, cancellationToken);
                break;
            case HideDiffRequest:
                Editor.HideDiff();
                Editor.Focus();
                break;
        }
    }

    private void OnDocumentClosed(object? sender, EventArgs e)
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
            _document.Closed -= OnDocumentClosed;
            _document.SetSaveHandler(null);
            _document.SetDiffHandler(null);
            _document = null;
        }

        Editor.Dispose();
    }
}
