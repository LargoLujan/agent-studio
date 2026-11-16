using System.Collections.Specialized;
using System.ComponentModel;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

// docs/24 §1, row 15: while a run goes on the plan does not change, so the band of the plan folds to one line and the canvas and
// the monitor get its room; the person can unfold it, and it unfolds by itself when the run ends. The line says the truth: when
// the run waits for an answer or an approval of the person, it says so, and where (the monitor). What is busy and what failed stay
// outside the folded part, in view.
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

    /// <summary>The run waits for the person: an answer to a question, or an approval of a task.</summary>
    public bool ReviewWaitsForYou => Monitor.IsActive && (Monitor.HasPendingQuestions || Monitor.Phase == RunPhase.AwaitingHuman);

    /// <summary>The line of the folded band: that the plan runs, or that the run waits for the person, and where to answer.</summary>
    public string ReviewFoldedText => Texts.Get(ReviewWaitsForYou ? "Orchestrator_Review_FoldedWaiting" : "Orchestrator_Review_Folded");

    [RelayCommand]
    private void ToggleReview() => IsReviewFolded = !IsReviewFolded;

    /// <summary>A run started or ended in the monitor: the band of the plan folds or unfolds with it; its phase changes its line.</summary>
    private void FollowRunForReview(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunMonitorViewModel.IsActive))
        {
            IsReviewFolded = Monitor.IsActive;
            OnPropertyChanged(nameof(CanFoldReview));
        }

        if (e.PropertyName is nameof(RunMonitorViewModel.IsActive) or nameof(RunMonitorViewModel.Phase))
        {
            OnPropertyChanged(nameof(ReviewWaitsForYou));
            OnPropertyChanged(nameof(ReviewFoldedText));
        }
    }

    /// <summary>A question came or was answered: the line of the folded band follows it.</summary>
    private void FollowQuestionsForReview(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ReviewWaitsForYou));
        OnPropertyChanged(nameof(ReviewFoldedText));
    }
}
