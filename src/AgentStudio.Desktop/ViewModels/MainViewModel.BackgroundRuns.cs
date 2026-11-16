using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly BackgroundRunsDocumentViewModel _backgroundRuns = new();

    /// <summary>The session's background monitors remain available after their runs finish.</summary>
    public BackgroundRunsDocumentViewModel BackgroundRuns => _backgroundRuns;

    private IEnumerable<ActivityItemViewModel> BackgroundRunActivities()
    {
        yield return new ActivityItemViewModel("background-runs", Texts.Get("BackgroundRuns_Title"), "\uE9F9", null, null, OpenBackgroundRunsCommand) { Group = ActivityGroup.Project };
    }

    [RelayCommand]
    private void OpenBackgroundRuns() => AddDocument(_backgroundRuns);
}
