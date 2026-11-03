using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Orchestration;

/// <summary>
/// A run is not resumed because the writers at medium integrity its plan has now (read from its checked plan, roster and
/// profile and from what the confinement probe answers of the installed Claude Code) are not the ones it was approved
/// with (ADR-0018, decision 3; ADR-0010, addendum G-02). Nothing was recovered or launched, and the decision log has the
/// alert <c>run.not-resumable</c>. It stays so until approved again: the plan approved next to the warning it has now
/// starts a follow-up run (<see cref="RunRequest.Follows"/>); the run itself is never resumed with writers nobody approved.
/// </summary>
public sealed class RunNeedsApprovalException : InvalidOperationException
{
    /// <param name="message">Why, in English, with both warnings.</param>
    /// <param name="run">The run that was not resumed.</param>
    /// <param name="planHash">The hash of the plan it started with: the one to approve again.</param>
    /// <param name="warningAnchored">
    /// Whether its <c>RunStarted</c> anchored the writers it was approved with (version 3). False for a run started before
    /// they were anchored, which is refused only when its plan has such writers now.
    /// </param>
    /// <param name="approvedWarning">The warning it was approved with; null when it had none, or when nothing was anchored.</param>
    /// <param name="currentWarning">The warning its plan has now, in Spanish as the review shows it; null when it has none.</param>
    public RunNeedsApprovalException(string message, RunId run, string planHash, bool warningAnchored, string? approvedWarning, string? currentWarning)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        Run = run;
        PlanHash = planHash;
        WarningAnchored = warningAnchored;
        ApprovedWarning = approvedWarning;
        CurrentWarning = currentWarning;
    }

    public RunNeedsApprovalException()
    {
    }

    public RunNeedsApprovalException(string message)
        : base(message)
    {
    }

    public RunNeedsApprovalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The run that was not resumed; null only when built without one.</summary>
    public RunId? Run { get; }

    /// <summary>The hash of the plan the run started with; null only when built without one.</summary>
    public string? PlanHash { get; }

    /// <summary>Whether the run anchored the writers it was approved with (<c>RunStarted</c> version 3).</summary>
    public bool WarningAnchored { get; }

    /// <summary>The warning the run was approved with; null when it had none, or when nothing was anchored.</summary>
    public string? ApprovedWarning { get; }

    /// <summary>The warning the plan of the run has now; null when it has none.</summary>
    public string? CurrentWarning { get; }
}
