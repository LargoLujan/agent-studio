using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// «Auto Graph» in the shell (docs/20 section 6.4, docs/03): one entry of the activity bar and one tab for the session, which follows the open project like
// «Trabajo» does. The document reads the analysis of the saved version itself; the shell only opens it, and carries what the person asks from a node:
// to its file in the editor and to the work of a task in «Trabajo».
public sealed partial class MainViewModel
{
    private readonly GraphDocumentViewModel? _graphDocument;
    private string? _graphDocumentRoot;

    /// <summary>«Auto Graph», only when its document was composed.</summary>
    private IEnumerable<ActivityItemViewModel> GraphActivities()
    {
        if (_graphDocument is not null)
        {
            yield return new ActivityItemViewModel("graph", Texts.Get("Graph_Title"), "", null, null, OpenGraphCommand);
        }
    }

    /// <summary>Opens «Auto Graph»: one tab for the session, which follows the open project.</summary>
    [RelayCommand]
    private async Task OpenGraphAsync()
    {
        if (_graphDocument is null)
        {
            return;
        }

        AddDocument(_graphDocument);
        if (!SameProject(_graphDocumentRoot, ProjectPath))
        {
            _graphDocumentRoot = ProjectPath;
            await _graphDocument.SetProjectRootAsync(ProjectPath, CancellationToken.None);
        }
    }

    /// <summary>What a node of the graph asks of the shell: a file in the editor and the work of a task. Without «Trabajo» the task offers nowhere to go and says so.</summary>
    private void ConnectGraph()
    {
        if (_graphDocument is null)
        {
            return;
        }

        foreach (var section in _graphDocument.Sections.OfType<GraphMapSection>())
        {
            section.FileRequested += (_, path) => _ = OpenFileAsync(path);
            section.TaskRequested += OnGraphTaskRequested;
        }

        // «Discrepancias» (WP-D): the file of a discrepancy opens in the editor, and a requirement with no task goes to «Trabajo», where the board is.
        foreach (var section in _graphDocument.Sections.OfType<DocsDiscrepanciesSection>())
        {
            section.FileRequested += (_, path) => _ = OpenFileAsync(path);
            section.BoardRequested += (_, milestone) => _ = OpenWorkFromDiscrepanciesAsync(milestone);
        }
    }

    /// <summary>Opens «Trabajo» and, when the discrepancy names a milestone, chooses it (<c>SelectMilestone</c>, as its own list does), so its board is the one in view.</summary>
    private async Task OpenWorkFromDiscrepanciesAsync(string? milestone)
    {
        if (_workDocument is null)
        {
            StatusMessage = Texts.Get("Graph_NoWork");
            return;
        }

        try
        {
            await OpenWorkAsync();
            if (milestone is { Length: > 0 } id)
            {
                // The id came from the log through a finding: it is checked again, and one that is not an id of a milestone chooses nothing.
                try
                {
                    _workDocument.Context.SelectMilestone(new AgentStudio.Domain.Work.Milestones.MilestoneId(id));
                }
                catch (ArgumentException)
                {
                    // Opened «Trabajo» without a milestone chosen.
                }
            }
        }
#pragma warning disable CA1031 // A handler of the UI: a failure is said on the status bar, never thrown at the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = Texts.Format("Shell_Work_ChainFailed", ex.Message);
        }
    }

    private async void OnGraphTaskRequested(object? sender, TraceRef task)
    {
        if (_workDocument is null)
        {
            StatusMessage = Texts.Get("Graph_NoWork");
            return;
        }

        try
        {
            await OpenWorkAsync();
            _workDocument.Context.LookAt(task);
        }
#pragma warning disable CA1031 // A handler of the UI: a failure is said on the status bar, never thrown at the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = Texts.Format("Shell_Work_ChainFailed", ex.Message);
        }
    }

    /// <summary>The open project changed: the tab, when open, shows it at once; otherwise it does the next time it is opened.</summary>
    private async Task ShowProjectInGraphAsync(string projectPath)
    {
        _graphDocumentRoot = null;
        if (_graphDocument is not null && Documents.Contains(_graphDocument))
        {
            _graphDocumentRoot = projectPath;
            await _graphDocument.SetProjectRootAsync(projectPath, CancellationToken.None);
        }
    }
}
