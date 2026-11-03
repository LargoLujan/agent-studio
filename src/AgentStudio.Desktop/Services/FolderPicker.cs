using Microsoft.Win32;

namespace AgentStudio.Desktop.Services;

/// <summary>Asks the user for a folder. An interface so ViewModels stay testable without dialogs.</summary>
public interface IFolderPicker
{
    /// <summary>The chosen folder, or null when the user cancelled.</summary>
    string? PickFolder(string title);
}

public sealed class WindowsFolderPicker : IFolderPicker
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
