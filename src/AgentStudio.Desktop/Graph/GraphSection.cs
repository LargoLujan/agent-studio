using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Graph;

/// <summary>
/// One section of the «Auto Graph» document (docs/20 section 6.4): the contract between the host (<see cref="GraphDocumentViewModel"/>) and what each
/// package puts in it. WP-G brings «Grafo»; WP-D brings «Diagramas» and «Discrepancias». The host knows no concrete section: the composition
/// gives it the list.
/// </summary>
/// <param name="infoKey">The key of the ⓘ of the section: <c>&lt;InfoKey&gt;_Title</c>, <c>_What</c> and <c>_Actions</c> in the resources of its area.</param>
public abstract partial class GraphSection(string infoKey) : ObservableObject
{
    /// <summary>The key of the ⓘ of the section.</summary>
    public string InfoKey { get; } = infoKey;

    /// <summary>The title of the section, the same key as its ⓘ (<c>&lt;InfoKey&gt;_Title</c>).</summary>
    public string Title => Texts.Get(InfoKey + "_Title");

    /// <summary>The project the section shows; null when none is open.</summary>
    public ProjectContext? Project { get; private set; }

    public bool HasProject => Project is not null;

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

    /// <summary>What a screen reader says of the section in the list of sections: its title.</summary>
    public override string ToString() => Title;

    /// <summary>Reads what the section shows again (the project changed, or the commit did, or something was written).</summary>
    public abstract Task ReloadAsync(CancellationToken ct);

    /// <summary>The document closed or the project changed: what the section is still waiting for (an analysis, a copy of a commit) is cancelled and cleans up after itself.</summary>
    public virtual void CancelPending()
    {
    }

    /// <summary>The project open changed (or was closed): what the section holds of the other one goes. Called by the host before <see cref="ReloadAsync"/>.</summary>
    protected virtual void ProjectChanged()
    {
    }

    internal void SetProject(ProjectContext? project)
    {
        Project = project;
        Message = null;
        Error = null;
        ProjectChanged();
        OnPropertyChanged(nameof(HasProject));
    }
}
