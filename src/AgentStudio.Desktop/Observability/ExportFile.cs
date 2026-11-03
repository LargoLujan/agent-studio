using System.IO;
using Microsoft.Win32;

namespace AgentStudio.Desktop.Observability;

/// <summary>«Guardar como»: asks the user where to save a file. An interface so ViewModels stay testable without dialogs.</summary>
public interface ISaveFilePicker
{
    /// <summary>The chosen path, or null when the user cancelled.</summary>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="suggestedFileName">The name offered first.</param>
    /// <param name="filter">The file types, as <c>CSV (*.csv)|*.csv</c>.</param>
    string? PickSaveFile(string title, string suggestedFileName, string filter);
}

/// <summary>The Windows «Guardar como» dialog, owned by the main window; it asks before replacing a file.</summary>
public sealed class WindowsSaveFilePicker : ISaveFilePicker
{
    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedFileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        var owner = System.Windows.Application.Current?.MainWindow;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }
}

/// <summary>
/// Writes an export so that nobody ever sees it half-written (docs/14 D-07): into a temporary file next to the target,
/// then moved over it. If anything fails the temporary file is removed and the target stays as it was.
/// </summary>
internal static class ExportFile
{
    /// <param name="path">The file the user chose.</param>
    /// <param name="write">Writes the whole content to the stream it gets (the exporter's own bytes, BOM included).</param>
    internal static async Task WriteAtomicallyAsync(string path, Func<Stream, Task> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target) ?? throw new ArgumentException("The path has no folder.", nameof(path));
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temporary file is harmless and has a name of its own.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is harmless and has a name of its own.
        }
    }
}
