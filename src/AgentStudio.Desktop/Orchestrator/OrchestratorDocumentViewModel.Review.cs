using System.ComponentModel;
using AgentStudio.Desktop.Orchestrator.Monitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

// docs/24 §1, row 15: while a run goes on there is nothing to approve, so the band of the plan folds to one line and the canvas
// and the monitor get its room; the person can unfold it, and it unfolds by itself when the run ends.
public sealed partial class OrchestratorDocumentViewModel
{
    /// <summary>The band of the plan shows one line instead of the summary and the decision; it folds when a run starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsReview))]
    private bool _isReviewFolded;

    /// <summary>The summary, the state of the plan and the decision are on screen.</summary>
    public bool ShowsReview => !IsReviewFolded;

    /// <summary>A run goes on: the band of the plan offers to fold or unfold.</summary>
    public bool CanFoldReview => Monitor.IsActive;

    [RelayCommand]
    private void ToggleReview() => IsReviewFolded = !IsReviewFolded;

    /// <summary>A run started or ended in the monitor: the band of the plan folds or unfolds with it.</summary>
    private void FollowRunForReview(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RunMonitorViewModel.IsActive))
        {
            return;
        }

        IsReviewFolded = Monitor.IsActive;
        OnPropertyChanged(nameof(CanFoldReview));
    }
}
