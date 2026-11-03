using System.Globalization;
using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Markdown;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// Read-only panel with today's journal (<c>.agentstudio/bitacora/YYYY-MM-DD.md</c>) of the open project. The day is read
/// through the checked storage reader (<see cref="IProjectStorageReader"/>, docs/14 D-31): a link planted as a day of the
/// journal, or a folder on the way that is one, is refused and nothing of it is shown. Without that reader the journal is
/// not read at all: no file of the project is ever opened directly.
/// </summary>
/// <param name="timeProvider">Its local day names the file.</param>
/// <param name="reader">The checked reader of <c>.agentstudio</c>; null when the shell was composed without it.</param>
public sealed partial class JournalViewModel(TimeProvider timeProvider, IProjectStorageReader? reader = null) : ObservableObject
{
    /// <summary>
    /// The most lines the readable view draws (V31): laying out a document blocks the UI thread for a time that grows with
    /// its length, so a longer day shows its latest entries, and all of it as plain text on demand.
    /// </summary>
    public const int ReadableLines = 200;

    /// <summary>The most characters the readable view draws.</summary>
    public const int ReadableCharacters = 40_000;

    private string? _projectPath;

    /// <summary>The day as read, whole; never changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlainText))]
    private string _text = string.Empty;

    /// <summary>What the readable view draws of <see cref="Text"/>: all of it, or its title and its latest entries when it is long.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderedText), nameof(IsTrimmed), nameof(TrimNote))]
    private JournalExcerpt _excerpt = JournalExcerpt.Empty;

    /// <summary>The whole day as plain text (fast, and complete) instead of the readable view of its latest entries.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReadable), nameof(TrimNote), nameof(ToggleText), nameof(PlainText))]
    private bool _showPlainText;

    public string RenderedText => Excerpt.Text;

    /// <summary>
    /// What the plain viewer holds: the whole day once «Ver todo» is asked, nothing before. A text box takes its text even
    /// while it is not shown, and a long day cost as much as the readable view (about a second for 5,000 lines).
    /// </summary>
    public string PlainText => ShowPlainText ? Text : string.Empty;

    public bool IsTrimmed => Excerpt.IsTrimmed;

    public bool ShowReadable => !ShowPlainText;

    /// <summary>Why not all of the day is drawn, and how to see it; null when all of it is.</summary>
    public string? TrimNote => !Excerpt.IsTrimmed ? null
        : ShowPlainText ? "Se ve el día completo como texto, sin formato."
        : Excerpt.ShownEntries > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Bitácora larga: se ven las {Excerpt.ShownEntries} entradas más recientes de {Excerpt.TotalEntries}.")
            : string.Create(CultureInfo.InvariantCulture, $"Bitácora larga: se ven las últimas {Excerpt.ShownLines} líneas de {Excerpt.TotalLines}.");

    public string ToggleText => ShowPlainText ? "Ver legible" : "Ver todo";

    partial void OnTextChanged(string value)
    {
        Excerpt = JournalExcerpt.Of(value, ReadableLines, ReadableCharacters);

        // A day short enough is drawn whole: the plain text has nothing more to show, and no way back would be offered.
        if (!Excerpt.IsTrimmed)
        {
            ShowPlainText = false;
        }
    }

    /// <summary>«Ver todo» (the whole day as plain text) and back to the readable view.</summary>
    [RelayCommand]
    private void TogglePlainText() => ShowPlainText = !ShowPlainText;

    /// <summary>Honest empty / error state; null when the journal was shown.</summary>
    [ObservableProperty]
    private string? _statusMessage = "Abre una carpeta para ver su bitácora.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Path of today's journal for the current project, or null without project (shown, never opened directly).</summary>
    public string? CurrentFilePath =>
        _projectPath is null ? null : Path.Combine(_projectPath, AgentStudioFiles.DataFolder, AgentStudioFiles.JournalFolder, TodaysFileName());

    public async Task SetProjectAsync(string? projectPath)
    {
        _projectPath = projectPath;
        ShowPlainText = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_projectPath is not { } project)
        {
            Text = string.Empty;
            StatusMessage = "Abre una carpeta para ver su bitácora.";
            return;
        }

        if (reader is null)
        {
            Text = string.Empty;
            StatusMessage = "La bitácora no se puede leer: falta la lectura comprobada de .agentstudio (sin ella no se abre ningún fichero del proyecto).";
            return;
        }

        var file = TodaysFileName();
        IsBusy = true;
        try
        {
            // The reader checks every folder on the way and the file itself, and shares the file for writing (the logging
            // hook may be appending right now).
            await using var stream = reader.OpenForRead(project, AgentStudioFiles.JournalFolder + "/" + file);
            using var text = new StreamReader(stream);
            Text = await text.ReadToEndAsync();
            StatusMessage = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Text = string.Empty;
            StatusMessage = $"No hay bitácora de hoy ({file}). Se crea con la primera petición del día.";
        }
        catch (IOException ex)
        {
            // Also a refused link or a path that reaches another place: nothing of it is shown.
            Text = string.Empty;
            StatusMessage = $"No se pudo leer la bitácora: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Text = string.Empty;
            StatusMessage = $"No se pudo leer la bitácora: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string TodaysFileName() => AgentStudioFiles.JournalDayFile(DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime));
}
