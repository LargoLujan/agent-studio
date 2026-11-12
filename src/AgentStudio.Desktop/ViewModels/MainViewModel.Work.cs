using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// «Trabajo» in the shell (docs/16 §4.6, docs/03): one entry of the activity bar and one tab for the session, which follows the open
// project like «Memoria» and «Documentación para clientes» do. The document reads the verified log itself; the shell only opens it.
public sealed partial class MainViewModel
{
    private readonly WorkDocumentViewModel? _workDocument;
    private string? _workDocumentRoot;

    /// <summary>«Trabajo» (milestones, backlog, board, chain, participants, links and budgets), only when its document was composed.</summary>
    private IEnumerable<ActivityItemViewModel> WorkActivities()
    {
        if (_workDocument is not null)
        {
            yield return new ActivityItemViewModel("work", Texts.Get("Work_Title"), "", null, null, OpenWorkCommand);
        }
    }

    /// <summary>Opens «Trabajo»: one tab for the session, which follows the open project.</summary>
    [RelayCommand]
    private async Task OpenWorkAsync()
    {
        if (_workDocument is null)
        {
            return;
        }

        AddDocument(_workDocument);
        if (!SameProject(_workDocumentRoot, ProjectPath))
        {
            _workDocumentRoot = ProjectPath;
            await _workDocument.SetProjectAsync(ProjectPath);
        }
    }

    /// <summary>The open project changed: the tab, when open, shows it at once; otherwise it does the next time it is opened.</summary>
    private async Task ShowProjectInWorkAsync(string projectPath)
    {
        _workDocumentRoot = null;
        if (_workDocument is not null && Documents.Contains(_workDocument))
        {
            _workDocumentRoot = projectPath;
            await _workDocument.SetProjectAsync(projectPath);
        }
    }
}
