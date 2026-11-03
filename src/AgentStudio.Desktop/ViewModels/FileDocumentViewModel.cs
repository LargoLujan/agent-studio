using System.IO;
using System.Text;
using AgentStudio.Desktop.Controls.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// A text file tab. <see cref="Editor"/> is edited in Monaco; <see cref="Text"/> backs the read-only fallback viewer.
/// </summary>
public sealed partial class FileDocumentViewModel : DocumentViewModel
{
    /// <summary>Larger files are not loaded into the plain viewer.</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    public FileDocumentViewModel(string filePath)
        : base(CreateId(filePath), Path.GetFileName(filePath), Path.GetFullPath(filePath))
    {
    }

    public static string CreateId(string filePath) => "file:" + Path.GetFullPath(filePath).ToUpperInvariant();

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Why the content is not shown (binary, too large, unreadable); null when it was loaded.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// The editable document shown by the Monaco editor. Null when the file could only be read as plain text
    /// (e.g. an encoding the editor does not save safely); the view then falls back to the read-only viewer.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareWithSavedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompareWithFileCommand))]
    private EditorDocument? _editor;

    private Func<CancellationToken, Task>? _saveHandler;
    private Func<DiffRequest, CancellationToken, Task>? _diffHandler;
    private bool _comparingWithSaved;

    /// <inheritdoc />
    public override bool CanSave => _saveHandler is not null;

    /// <summary>
    /// Registers (or clears, with null) the save capability. Only the view knows the current text of the editor,
    /// so it supplies the handler while the editor is shown.
    /// </summary>
    public void SetSaveHandler(Func<CancellationToken, Task>? handler) => _saveHandler = handler;

    /// <inheritdoc />
    public override Task SaveAsync(CancellationToken cancellationToken = default) =>
        _saveHandler is { } handler ? handler(cancellationToken) : base.SaveAsync(cancellationToken);

    /// <summary>True while the tab shows a comparison instead of the editor.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseDiffCommand))]
    private bool _isComparing;

    /// <summary>
    /// What the original side of the comparison on screen is (Spanish; the removed, red side); null while editing.
    /// The side-by-side view puts it on the left, the narrow unified view shows it as the red lines.
    /// </summary>
    [ObservableProperty]
    private string? _diffOriginalLabel;

    /// <summary>What the modified side of the comparison on screen is (Spanish; the added, green side); null while editing.</summary>
    [ObservableProperty]
    private string? _diffModifiedLabel;

    /// <summary>Why a comparison could not be shown (unreadable file, editor not answering); null when all went well.</summary>
    [ObservableProperty]
    private string? _diffError;

    /// <summary>
    /// Raised when the user asks to compare this file with another one. The owner offers a list of files and, once
    /// one is chosen, calls <see cref="CompareWithFileAsync"/>.
    /// </summary>
    public event EventHandler? CompareWithFileRequested;

    /// <summary>
    /// Registers (or clears, with null) the capability to show a diff in the editor. The view supplies it while the
    /// editor is shown, like the save handler. Replacing it drops any comparison: a different editor page is not
    /// showing the previous one.
    /// </summary>
    public void SetDiffHandler(Func<DiffRequest, CancellationToken, Task>? handler)
    {
        _diffHandler = handler;
        _comparingWithSaved = false;
        IsComparing = false;
        DiffOriginalLabel = null;
        DiffModifiedLabel = null;
        DiffError = null;
        CompareWithSavedCommand.NotifyCanExecuteChanged();
        CompareWithFileCommand.NotifyCanExecuteChanged();
    }

    private bool CanCompare() => _diffHandler is not null && Editor is not null;

    /// <summary>Compares the text saved on disk (original) with what the editor holds now (modified).</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareWithSavedAsync(CancellationToken cancellationToken)
    {
        var unsaved = Editor?.IsDirty == true ? " (con cambios sin guardar)" : string.Empty;
        await ShowDiffAsync(
            new CompareWithSavedRequest(),
            originalLabel: "guardado en disco",
            modifiedLabel: $"editor{unsaved}",
            comparingWithSaved: true,
            cancellationToken);
    }

    /// <summary>Asks the owner to pick another file; the comparison starts in <see cref="CompareWithFileAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void CompareWithFile() => CompareWithFileRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Compares another file (original) with what the editor holds now (modified).</summary>
    /// <param name="otherPath">The file to compare with.</param>
    /// <param name="label">How the other file is named on the screen; its file name when null.</param>
    public async Task CompareWithFileAsync(string otherPath, string? label = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(otherPath);
        if (!CanCompare())
        {
            return;
        }

        var name = Path.GetFileName(otherPath);
        var (text, problem) = await ReadForComparisonAsync(otherPath, cancellationToken);
        if (text is null)
        {
            DiffError = $"No se puede comparar con «{name}»: {problem}";
            return;
        }

        await ShowDiffAsync(
            new CompareWithTextRequest(text),
            originalLabel: label ?? name,
            modifiedLabel: $"{Title} (editor)",
            comparingWithSaved: false,
            cancellationToken);
    }

    private bool CanCloseDiff() => IsComparing;

    /// <summary>Leaves the comparison and shows the editor again.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseDiff))]
    private async Task CloseDiffAsync(CancellationToken cancellationToken)
    {
        var handler = _diffHandler;

        // Leave first: a page that fails to answer must not keep the toolbar stuck in "comparing".
        IsComparing = false;
        DiffOriginalLabel = null;
        DiffModifiedLabel = null;
        _comparingWithSaved = false;
        if (handler is not null)
        {
            await TryForwardAsync(handler, new HideDiffRequest(), cancellationToken);
        }
    }

    private async Task ShowDiffAsync(
        DiffRequest request,
        string originalLabel,
        string modifiedLabel,
        bool comparingWithSaved,
        CancellationToken cancellationToken)
    {
        if (_diffHandler is not { } handler)
        {
            return;
        }

        DiffError = null;
        if (!await TryForwardAsync(handler, request, cancellationToken))
        {
            return;
        }

        _comparingWithSaved = comparingWithSaved;
        DiffOriginalLabel = originalLabel;
        DiffModifiedLabel = modifiedLabel;
        IsComparing = true;
    }

    private async Task<bool> TryForwardAsync(Func<DiffRequest, CancellationToken, Task> handler, DiffRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await handler(request, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            DiffError = $"No se pudo mostrar la comparación: {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads a text file for a comparison; exactly one of the results is null (the reason when it cannot be used).</summary>
    private static async Task<(string? Text, string? Problem)> ReadForComparisonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (null, "el fichero no existe.");
            }

            if (info.Length > MaxBytes)
            {
                return (null, $"pesa {info.Length / (1024 * 1024)} MB y supera el límite ({MaxBytes / (1024 * 1024)} MB).");
            }

            var (text, _) = await TextFileStore.ReadAsync(path, cancellationToken);
            return text.Contains('\0', StringComparison.Ordinal) ? (null, "es un fichero binario.") : (text, null);
        }
        catch (NotSupportedException)
        {
            return (null, "solo se admiten ficheros UTF-8 (este empieza con una marca de orden de bytes UTF-16 o UTF-32).");
        }
        catch (InvalidDataException)
        {
            return (null, "no es un fichero UTF-8 válido.");
        }
        catch (IOException ex)
        {
            return (null, $"no se pudo leer: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, $"no se pudo leer: {ex.Message}");
        }
    }

    private void OnEditorSaved(object? sender, EventArgs e)
    {
        // The saved version just became the text of the editor, so a comparison against it is out of date.
        if (IsComparing && _comparingWithSaved)
        {
            CloseDiffCommand.Execute(null);
        }
    }

    private async Task OpenEditorAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var editor = await EditorDocument.OpenAsync(path, cancellationToken);
            editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorDocument.IsDirty))
                {
                    IsDirty = editor.IsDirty;
                }
            };
            editor.Saved += OnEditorSaved;
            Editor = editor;
        }
        catch (NotSupportedException ex)
        {
            ErrorMessage = $"Se muestra en solo lectura: {ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            ErrorMessage = $"Se muestra en solo lectura: {ex.Message}";
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = FilePath!;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                ErrorMessage = $"El fichero pesa {info.Length / (1024 * 1024)} MB y supera el límite del visor ({MaxBytes / (1024 * 1024)} MB).";
                return;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (content.Contains('\0', StringComparison.Ordinal))
            {
                ErrorMessage = "El fichero es binario y no se puede mostrar como texto.";
                return;
            }

            Text = content;
            ErrorMessage = null;
            await OpenEditorAsync(path, cancellationToken);
        }
        catch (IOException ex)
        {
            ErrorMessage = $"No se pudo leer el fichero: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorMessage = $"No se pudo leer el fichero: {ex.Message}";
        }
    }
}
