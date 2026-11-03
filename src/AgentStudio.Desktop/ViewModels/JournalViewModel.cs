using System.Globalization;
using System.IO;
using AgentStudio.Application.Projects;
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
    private string? _projectPath;

    [ObservableProperty]
    private string _text = string.Empty;

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
