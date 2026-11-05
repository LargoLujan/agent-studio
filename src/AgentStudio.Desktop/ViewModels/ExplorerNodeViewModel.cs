using System.Collections.ObjectModel;
using System.IO;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>A file or folder of the explorer tree. Folder children are read the first time the node is expanded.</summary>
public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    private bool _loaded;

    public ExplorerNodeViewModel(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)) is { Length: > 0 } name ? name : fullPath;
        if (isDirectory)
        {
            // Placeholder so the expander is shown; replaced on the first expansion.
            Children.Add(CreateMessage(string.Empty));
        }
    }

    private ExplorerNodeViewModel(string message)
    {
        FullPath = string.Empty;
        Name = message;
        IsMessage = true;
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>True for the placeholder / error line shown under a folder; it cannot be opened.</summary>
    public bool IsMessage { get; }

    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = [];

    /// <summary>True once the children of this folder have been read from disk.</summary>
    public bool IsLoaded => _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !_loaded)
        {
            LoadChildren();
        }
    }

    /// <summary>Re-reads this folder (and the already loaded subfolders), keeping nodes that still exist.</summary>
    public void Refresh()
    {
        if (!IsDirectory || !_loaded)
        {
            return;
        }

        var existing = Children.Where(c => !c.IsMessage).ToDictionary(c => c.FullPath, StringComparer.OrdinalIgnoreCase);
        var fresh = ReadEntries().Select(e => existing.TryGetValue(e.FullPath, out var node) ? node : e).ToList();
        Children.Clear();
        foreach (var child in fresh)
        {
            Children.Add(child);
        }

        foreach (var child in fresh)
        {
            child.Refresh();
        }
    }

    private void LoadChildren()
    {
        _loaded = true;
        Children.Clear();
        foreach (var child in ReadEntries())
        {
            Children.Add(child);
        }

        OnPropertyChanged(nameof(IsLoaded));
    }

    private List<ExplorerNodeViewModel> ReadEntries()
    {
        var entries = new List<ExplorerNodeViewModel>();
        var parentName = Name;
        try
        {
            foreach (var info in new DirectoryInfo(FullPath).EnumerateFileSystemInfos())
            {
                var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                if (!ExplorerFilter.IsHidden(info.Name, isDirectory, parentName))
                {
                    entries.Add(new ExplorerNodeViewModel(info.FullName, isDirectory));
                }
            }
        }
        catch (IOException ex)
        {
            return [CreateMessage($"No se pudo leer la carpeta: {ex.Message}")];
        }
        catch (UnauthorizedAccessException)
        {
            return [CreateMessage("Sin permiso para leer la carpeta")];
        }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ExplorerNodeViewModel CreateMessage(string message) => new(message);

    /// <summary>What a screen reader says of a file or a folder of the explorer (V34): its name.</summary>
    public override string ToString() => Name;
}
