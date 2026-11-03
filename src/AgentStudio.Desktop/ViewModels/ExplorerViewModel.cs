using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>Lazy file tree of the open project.</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    /// <summary>The project folder as the single root node; empty while no project is open.</summary>
    public ObservableCollection<ExplorerNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string? _rootPath;

    public bool HasProject => RootPath is not null;

    /// <summary>Raised when the user opens a file (double click or Enter).</summary>
    public event EventHandler<string>? FileActivated;

    public bool HasNoProject => RootPath is null;

    partial void OnRootPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasNoProject));
    }

    public void Load(string? projectPath)
    {
        Roots.Clear();
        RootPath = projectPath;
        if (projectPath is null)
        {
            return;
        }

        Roots.Add(new ExplorerNodeViewModel(projectPath, isDirectory: true) { IsExpanded = true });
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void Refresh()
    {
        foreach (var root in Roots)
        {
            root.Refresh();
        }
    }

    /// <summary>Opens a file node; folders and placeholder lines are ignored.</summary>
    [RelayCommand]
    private void Open(ExplorerNodeViewModel? node)
    {
        if (node is { IsDirectory: false, IsMessage: false })
        {
            FileActivated?.Invoke(this, node.FullPath);
        }
    }
}
