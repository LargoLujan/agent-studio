using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>A file open in the editor: path, language, dirty state and the save logic.</summary>
public sealed class EditorDocument : ObservableObject
{
    private bool _isDirty;
    private string _savedText;
    private readonly TextFileFormat _format;
    private FileFingerprint? _fingerprint;
    private bool _hasMixedLineEndings;

    /// <param name="fingerprint">
    /// The file as it was read; null for a file that does not exist yet (saving then refuses to overwrite a file
    /// that appeared meanwhile).
    /// </param>
    public EditorDocument(string path, string text, TextFileFormat format, bool isReadOnly = false, FileFingerprint? fingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Language = LanguageMap.FromPath(Path);
        IsReadOnly = isReadOnly;
        _savedText = text;
        _format = format;
        _fingerprint = fingerprint;
        _hasMixedLineEndings = format.HasMixedLineEndings;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Monaco language id derived from the extension.</summary>
    public string Language { get; }

    public bool IsReadOnly { get; }

    /// <summary>Text as it is on disk (what the editor starts from and what a diff compares against).</summary>
    public string SavedText => _savedText;

    public TextFileFormat Format => _format;

    /// <summary>The file mixed line endings when read; saving unifies them, after which this turns false.</summary>
    public bool HasMixedLineEndings => _hasMixedLineEndings;

    /// <summary>One-line notice (Spanish) for the editor tab; null when the line endings are uniform.</summary>
    public string? MixedLineEndingsNotice => _hasMixedLineEndings
        ? $"Este fichero mezcla finales de línea (CRLF y LF); al guardar se unificarán a {(_format.LineEnding == LineEnding.CrLf ? "CRLF" : "LF")}."
        : null;

    /// <summary>True while the editor holds changes that are not on disk. Updated by the editor bridge.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    /// <summary>Called by the bridge when the web editor reports its dirty state.</summary>
    public void SetDirty(bool isDirty) => IsDirty = isDirty;

    /// <summary>Raised after the text has been written to disk.</summary>
    public event EventHandler? Saved;

    /// <summary>Reads a UTF-8 file, remembering BOM and line endings.</summary>
    public static async Task<EditorDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var (text, format, fingerprint) = await TextFileStore.ReadWithFingerprintAsync(path, cancellationToken).ConfigureAwait(false);
        return new EditorDocument(path, text, format, fingerprint: fingerprint);
    }

    /// <summary>
    /// Writes <paramref name="text"/> atomically with the original BOM and line endings, then clears the dirty flag.
    /// </summary>
    /// <param name="overwriteExternalChanges">
    /// Skips the check that the file on disk is still the one that was loaded or last saved.
    /// </param>
    /// <exception cref="FileChangedOnDiskException">
    /// The file changed (or was deleted) on disk since it was loaded or last saved and
    /// <paramref name="overwriteExternalChanges"/> is false.
    /// </exception>
    public async Task SaveAsync(string text, CancellationToken cancellationToken = default, bool overwriteExternalChanges = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The document is read-only.");
        }

        if (!overwriteExternalChanges)
        {
            await EnsureUnchangedOnDiskAsync(cancellationToken);
        }

        await TextFileStore.WriteAtomicAsync(Path, text, _format, cancellationToken);
        _fingerprint = await FileFingerprint.ReadAsync(Path, cancellationToken);
        if (_hasMixedLineEndings)
        {
            _hasMixedLineEndings = false;
            OnPropertyChanged(nameof(HasMixedLineEndings));
            OnPropertyChanged(nameof(MixedLineEndingsNotice));
        }

        _savedText = TextFileStore.NormalizeLineEndings(text, _format.LineEnding);
        OnPropertyChanged(nameof(SavedText));
        IsDirty = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnsureUnchangedOnDiskAsync(CancellationToken cancellationToken)
    {
        var onDisk = await FileFingerprint.ReadAsync(Path, cancellationToken);
        if (_fingerprint is null)
        {
            // A new file: anything at the path now was created by someone else.
            if (onDisk is not null)
            {
                throw new FileChangedOnDiskException(Path, deleted: false);
            }

            return;
        }

        if (onDisk is null)
        {
            throw new FileChangedOnDiskException(Path, deleted: true);
        }

        // The hash of the bytes is the source of truth; length and time are only hints.
        if (!string.Equals(onDisk.Hash, _fingerprint.Hash, StringComparison.Ordinal))
        {
            throw new FileChangedOnDiskException(Path, deleted: false);
        }
    }
}
