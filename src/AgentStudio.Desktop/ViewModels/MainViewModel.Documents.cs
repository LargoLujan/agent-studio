using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// «Documentación para clientes» in the shell (docs/17 D-58 to D-60): one entry of the activity bar and one tab for the session, which
// follows the open project like «Memoria» does. The document keeps its own preview and approval; the shell only opens it.
public sealed partial class MainViewModel
{
    private readonly ClientDocumentsViewModel? _clientDocuments;
    private string? _clientDocumentsRoot;

    /// <summary>«Documentación para clientes», only when its screen was composed.</summary>
    private IEnumerable<ActivityItemViewModel> ClientDocumentActivities()
    {
        if (_clientDocuments is not null)
        {
            yield return new ActivityItemViewModel("documents", Texts.Get("Documents_Title"), "", null, null, OpenClientDocumentsCommand);
        }
    }

    /// <summary>Opens «Documentación para clientes»: one tab for the session, which follows the open project.</summary>
    [RelayCommand]
    private async Task OpenClientDocumentsAsync()
    {
        if (_clientDocuments is null)
        {
            return;
        }

        AddDocument(_clientDocuments);
        if (!SameProject(_clientDocumentsRoot, ProjectPath))
        {
            _clientDocumentsRoot = ProjectPath;
            await _clientDocuments.SetProjectAsync(ProjectPath);
        }
    }

    /// <summary>The open project changed: the tab, when open, shows it at once; otherwise it does the next time it is opened.</summary>
    private async Task ShowProjectInDocumentsAsync(string projectPath)
    {
        _clientDocumentsRoot = null;
        if (_clientDocuments is not null && Documents.Contains(_clientDocuments))
        {
            _clientDocumentsRoot = projectPath;
            await _clientDocuments.SetProjectAsync(projectPath);
        }
    }
}
