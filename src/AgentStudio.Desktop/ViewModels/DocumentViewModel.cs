using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// A tab of the central document area. The view for each concrete type is picked by a DataTemplate
/// (see <c>MainWindow.xaml</c>), so swapping a view means swapping one template.
/// </summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    /// <summary>The marker the tab of a document with unsaved changes shows after its title.</summary>
    public const string UnsavedMarker = "●";

    protected DocumentViewModel(string contentId, string title, string? filePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        ContentId = contentId;
        _title = title;
        FilePath = filePath;
    }

    /// <summary>Stable identifier used by the docking layout.</summary>
    public string ContentId { get; }

    public string? FilePath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(AccessibleTitle))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(AccessibleTitle))]
    private bool _isDirty;

    /// <summary>What its tab shows: the title, with <see cref="UnsavedMarker"/> while there are unsaved changes. <see cref="Title"/> stays as it is.</summary>
    public string TabTitle => IsDirty ? $"{Title} {UnsavedMarker}" : Title;

    /// <summary>What a screen reader says for its tab: the title, and that it has unsaved changes when it has.</summary>
    public string AccessibleTitle => IsDirty ? $"{Title}, con cambios sin guardar" : Title;

    /// <summary>Raised when the document asks to be closed; the owner removes it from its collection.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>True when <see cref="SaveAsync"/> can persist the unsaved changes of this document.</summary>
    public virtual bool CanSave => false;

    /// <summary>Saves the unsaved changes; throws when <see cref="CanSave"/> is false.</summary>
    public virtual Task SaveAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"'{Title}' cannot be saved from the view model.");

    /// <summary>Raised once the owner has removed the document; views release their resources here.</summary>
    public event EventHandler? Closed;

    internal void NotifyClosed() => Closed?.Invoke(this, EventArgs.Empty);
}
