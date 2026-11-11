using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Team;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>
/// The brief of the project (docs/17 D-10, F13-17): a text of the person with what the project is, its conventions and its useful
/// addresses. It is kept as memory of the person (<see cref="IProjectBrief"/>: append-only and anchored, each version replaces the
/// one in force), reaches the orchestrator, the executors and the participants of the debates as data with its fingerprint, and
/// never reaches the judge nor the auditors. The addresses in it are text: the agents do not open them. Without the port the
/// section says it is not available, and no button does nothing.
/// </summary>
public sealed partial class ProjectBriefViewModel : ObservableObject
{
    private readonly IProjectBrief? _store;
    private readonly Func<ProjectContext?> _project;
    private readonly CanvasText _text;
    private ProjectBrief? _inForce;

    public ProjectBriefViewModel(IProjectBrief? store, Func<ProjectContext?> project, CanvasText text)
    {
        ArgumentNullException.ThrowIfNull(project);
        _store = store;
        _project = project;
        _text = text;
    }

    /// <summary>What the person is writing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _brief = string.Empty;

    /// <summary>What happened to the last read or save, in words.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>The brief can be read and saved: the memory is composed and a project is open.</summary>
    public bool IsAvailable => _store is not null && _project() is not null;

    public string UnavailableText => _text.Get("Canvas_Brief_NotComposed");

    /// <summary>The brief in force as last read or saved; what the draft of the montage counts in the context of each task (P49). Null when there is none.</summary>
    internal ProjectBrief? InForce => _inForce;

    /// <summary>The version in force, with its fingerprint (the first characters, as the screens say a hash); empty when there is none.</summary>
    public string VersionText => _inForce is { } brief
        ? _text.Format("Canvas_Brief_Version", brief.Sha256.Length > 12 ? brief.Sha256[..12] : brief.Sha256, brief.At.ToLocalTime().ToString("g", _text.Provider.Culture))
        : _text.Get("Canvas_Brief_None");

    /// <summary>Reads the brief in force when the project opens.</summary>
    public async Task LoadAsync()
    {
        if (_store is not { } store || _project() is not { } project)
        {
            return;
        }

        try
        {
            _inForce = await store.ReadAsync(project, CancellationToken.None).ConfigureAwait(true);
            Brief = _inForce?.Text ?? string.Empty;
            Status = null;
        }
#pragma warning disable CA1031 // The memory is behind a port: why it failed is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Canvas_Brief_Failed", ex.Message);
        }

        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(IsAvailable));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Guardar la ficha»: a new version that replaces the one in force; nothing is saved empty or past the limit of the memory.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_store is not { } store || _project() is not { } project)
        {
            return;
        }

        var text = Brief.Trim();
        if (text.Length == 0)
        {
            Status = _text.Get("Canvas_Brief_Empty");
            return;
        }

        if (text.Length > ProjectBriefs.MaxCharacters)
        {
            Status = _text.Format("Canvas_Brief_TooLong", ProjectBriefs.MaxCharacters);
            return;
        }

        try
        {
            _inForce = await store.SaveAsync(project, text, ApprovalPromptService.DecidedBy, CancellationToken.None).ConfigureAwait(true);
            Brief = _inForce.Text;
            Status = _text.Get("Canvas_Brief_Saved");
        }
#pragma warning disable CA1031 // The memory is behind a port: why it failed is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Canvas_Brief_Failed", ex.Message);
        }

        OnPropertyChanged(nameof(VersionText));
    }

    private bool CanSave() => IsAvailable && !string.IsNullOrWhiteSpace(Brief) && !string.Equals(Brief.Trim(), _inForce?.Text, StringComparison.Ordinal);
}
