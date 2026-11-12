using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// What the shell shows of «Repositorio y cambios» (docs/16 F9-9, D-44): the entry «Repositorio» of the activity bar and the one tab for
// the session, which follows the open project. The screen itself, with its four sections, is in Desktop/Repository.
public sealed partial class MainViewModel
{
    private RepositoryViewModel? _repository;

    /// <summary>The project «Repositorio y cambios» last showed; null until it is first opened.</summary>
    private ProjectShown? _repositoryShown;

    private void InitializeRepository(RepositoryViewModel? repository) => _repository = repository;

    /// <summary>
    /// The «Repositorio y cambios» document of the session, when it was composed; null otherwise. Whoever shows the chain of a version
    /// subscribes to <see cref="RepositoryViewModel.ChainRequested"/> on it (the screen of the chain of «Trabajo» does).
    /// </summary>
    public RepositoryViewModel? Repository => _repository;

    /// <summary>«Repositorio», only when its document was composed.</summary>
    private IEnumerable<ActivityItemViewModel> RepositoryActivities()
    {
        if (_repository is not null)
        {
            yield return new ActivityItemViewModel("repository", Texts.Get("Main_Activity_Repository"), "", null, null, OpenRepositoryCommand);
        }
    }

    /// <summary>Opens «Repositorio y cambios»: one tab for the session, which follows the open project.</summary>
    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        if (_repository is null)
        {
            return;
        }

        AddDocument(_repository);
        if (_repositoryShown is null || !SameProject(_repositoryShown.Path, ProjectPath))
        {
            _repositoryShown = new ProjectShown(ProjectPath);
            await _repository.SetProjectAsync(ProjectPath);
        }
    }

    /// <summary>The open project changed: «Repositorio y cambios» shows it at once when its tab is open, or the next time it is opened.</summary>
    private async Task ShowProjectInRepositoryAsync(string projectPath)
    {
        if (_repository is null)
        {
            return;
        }

        _repositoryShown = null;
        if (Documents.Contains(_repository))
        {
            _repositoryShown = new ProjectShown(projectPath);
            await _repository.SetProjectAsync(projectPath);
        }
    }
}
