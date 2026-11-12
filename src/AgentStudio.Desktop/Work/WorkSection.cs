using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// One section of the «Trabajo» document: its title (the same key as its ⓘ, <c>&lt;InfoKey&gt;_Title</c>), what is missing when a port was not
/// composed, a message and an error, and how it reads again from the verified log. A section never shows what it remembers after a write.
/// </summary>
public abstract partial class WorkSection(WorkContext context, string infoKey) : ObservableObject
{
    protected WorkContext Context { get; } = context;

    /// <summary>The key of the ⓘ of the section: <c>&lt;InfoKey&gt;_Title</c>, <c>_What</c> and <c>_Actions</c>.</summary>
    public string InfoKey { get; } = infoKey;

    public string Title => Texts.Get(InfoKey + "_Title");

    public bool HasProject => Context.Project is not null;

    /// <summary>What a screen reader says of the section in the list of sections (A1-bis): its title.</summary>
    public override string ToString() => Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    private bool _isBusy;

    private int _generation;

    /// <summary>
    /// Starts a read. Reads can overlap (a selection and a reload of the document at once), so each one builds what it read aside and
    /// applies it only if it is still the latest (<see cref="IsLatest"/>): two reads never both fill the same list.
    /// </summary>
    protected int BeginRead() => ++_generation;

    protected bool IsLatest(int read) => read == _generation;

    /// <summary>Reads the section again from the verified log (the project may have changed or something was written).</summary>
    public abstract Task ReloadAsync();

    /// <summary>The project open changed (or was closed): what the section holds of the other one goes.</summary>
    protected virtual void ProjectChanged()
    {
    }

    internal void RaiseHasProject() => OnPropertyChanged(nameof(HasProject));

    internal void OnProjectChanged()
    {
        Message = null;
        Error = null;
        ProjectChanged();
        OnPropertyChanged(nameof(HasProject));
    }

    /// <summary>Runs a read or a write: a refusal or a failure of a port is said in the section, never thrown into the UI.</summary>
    protected async Task<bool> GuardAsync(Func<Task> action, string errorKey = "Work_Error")
    {
        Error = null;
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
#pragma warning disable CA1031 // The services are behind ports: whatever they refuse is shown, never thrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = Texts.Format(errorKey, ex.Message);
            return false;
        }
    }
}
