using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Orchestration;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// «Ejecuciones anteriores» opened in the monitor (B8): a run that finished or was interrupted is read back from its log
/// (<see cref="IRunInspector"/>) and shown in read-only, its plan on the canvas with the status of each task, its evidence,
/// its review and, for a run approved with its integration and not delivered, «Entregar a mi rama» as for a run followed
/// live. A run in progress in the monitor is never put away for it. «Cerrar» brings back what the canvas held.
/// </summary>
public sealed partial class OrchestratorDocumentViewModel
{
    /// <summary>The hash of the plan of the run the monitor shows (<see cref="ApprovalText"/>); null while it is not known.</summary>
    private string? _followedPlanHash;

    /// <summary>What the canvas held when a previous run was opened to look at it: «Cerrar» brings it back.</summary>
    private CanvasState? _beforeInspection;

    /// <summary>
    /// «Ver»: opens a previous run in the monitor, read back from its log, and its plan on the canvas in read-only. The draft on
    /// the canvas (or nothing, when it showed a run that finished here) comes back with «Cerrar». Offered while no run in
    /// progress is in the monitor, so none is left without one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanViewRun))]
    private async Task ViewRunAsync(RunListItem? item)
    {
        if (item is null || Context?.Project is not { } project)
        {
            return;
        }

        IsBusy = true;
        BusyText = $"Abriendo la ejecución {item.Summary.Id} desde el registro…";
        ErrorMessage = null;
        IOrchestrationRun? inspected = null;
        try
        {
            var plan = await _session.FindPlanAsync(project, item.Summary.PlanHash, CancellationToken.None).ConfigureAwait(true);
            inspected = await _session.InspectAsync(project, item.Summary.Id, CancellationToken.None).ConfigureAwait(true);
            if (Monitor.IsActive || Monitor.IsDelivering)
            {
                ErrorMessage = $"No se abrió la ejecución {item.Summary.Id}: el monitor de esta pestaña está ocupado con otra ejecución.";
                return;
            }

            if (!Monitor.IsAttached)
            {
                // What was changed in the last second before «Ver» is saved now: during the view nothing is saved over the draft.
                SaveDraftNow();
                _beforeInspection = CaptureCanvas();
            }

            // An approval is given to the plan on screen when it is given: none survives a run shown in its place, so the plan
            // that comes back with «Cerrar» is approved again (and confirmed again with Windows Hello), as after «Editar el plan».
            ForgetApproval();
            await Monitor.DetachAsync().ConfigureAwait(true);

            // Nothing of the run looked at is saved over the draft: neither its positions nor its plan.
            Canvas.ProjectRoot = null;
            Canvas.IsReadOnly = true;
            using (SuppressDraftSaves())
            {
                if (plan is null)
                {
                    ClearCanvasPlan();
                }
                else
                {
                    ShowPlan(plan);
                }
            }

            Canvas.ResetStatuses();
            Monitor.Inspect(inspected, project);
            inspected = null;
            NoteFollowedPlan(item.Summary.Id, item.Summary.PlanHash);
            MarkViewed(item.Summary.Id);
            CollapsePreparationWhenReady();
            RebuildEditor();
            RefreshCommands();
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo abrir la ejecución {item.Summary.Id}: {ex.Message}";
        }
        finally
        {
            if (inspected is not null)
            {
                await inspected.DisposeAsync().ConfigureAwait(true);
            }

            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanViewRun(RunListItem? item) =>
        _session.CanInspectRuns && !IsBusy && item is { CanView: true } && Context?.Project is not null
        && !Monitor.IsActive && !Monitor.IsDelivering && Monitor.RunId != item.Summary.Id;

    /// <summary>«Cerrar»: puts away the run looked at and brings back what the canvas held before it was opened.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseRunView))]
    private Task CloseRunViewAsync() => PutAwayInspectedRunAsync();

    private bool CanCloseRunView() => Monitor.IsInspecting && !Monitor.IsDelivering && !IsBusy;

    private async Task PutAwayInspectedRunAsync()
    {
        if (!Monitor.IsInspecting)
        {
            return;
        }

        var before = _beforeInspection;
        await Monitor.DetachAsync().ConfigureAwait(true);
        ForgetInspection();
        Canvas.IsReadOnly = false;
        Canvas.ResetStatuses();
        RestoreCanvas(before ?? SavedDraftCanvas());
        RebuildEditor();
        RefreshCommands();
    }

    /// <summary>No run looked at is on screen any more: the canvas saves its positions again and nothing is marked as viewed.</summary>
    private void ForgetInspection()
    {
        _beforeInspection = null;
        Canvas.ProjectRoot = Context?.Project?.RootPath;
        MarkViewed(null);
    }

    /// <summary>Says which plan the run the monitor shows follows, once it is known (<see cref="ApprovalText"/>).</summary>
    private void NoteFollowedPlan(RunId run, string? planHash)
    {
        if (Monitor.RunId == run)
        {
            _followedPlanHash = planHash;
            OnPropertyChanged(nameof(ApprovalText));
        }
    }

    private void MarkViewed(RunId? run)
    {
        foreach (var item in PreviousRuns)
        {
            item.IsViewed = run is not null && item.Summary.Id == run;
        }
    }

    private CanvasState CaptureCanvas() =>
        new(PlanSummary, [.. Risks], Canvas.BuildTasks(), Canvas.CaptureLayout(), Proposal, ProposalStatus, ShowValidation);

    /// <summary>Puts <paramref name="state"/> back on the canvas, or leaves it empty; never approved.</summary>
    private void RestoreCanvas(CanvasState? state)
    {
        if (state is not null)
        {
            TryPutOnCanvas(state);
            return;
        }

        ForgetApproval();
        Proposal = null;
        ProposalStatus = null;
        ClearCanvasPlan();
        Revalidate();
    }

    /// <summary>
    /// Puts <paramref name="state"/> on the canvas, never approved: a plan brought back is approved again, as after «Editar el
    /// plan». False, and the canvas left empty with a notice, when it cannot be drawn.
    /// </summary>
    private bool TryPutOnCanvas(CanvasState state)
    {
        ForgetApproval();
        try
        {
            Proposal = state.Proposal;
            ProposalStatus = state.ProposalStatus;
            Risks.Clear();
            foreach (var risk in state.Risks)
            {
                Risks.Add(risk);
            }

            PlanSummary = state.Summary;
            Canvas.LoadTasks(state.Tasks, state.Layout);
            ShowValidation = state.ShowValidation;
            Revalidate();
            return true;
        }
#pragma warning disable CA1031 // What comes back is a convenience: whatever it breaks, the canvas stays usable and empty.
        catch (Exception)
#pragma warning restore CA1031
        {
            Proposal = null;
            ProposalStatus = null;
            ClearCanvasPlan();
            Revalidate();
            Canvas.Notify("No se pudo recuperar el plan del borrador: el lienzo queda vacío.", NoticeKind.Error);
            return false;
        }
    }

    /// <summary>The plan on the canvas is approved no more: the next run asks for a new approval.</summary>
    private void ForgetApproval()
    {
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        _helloAbsent = null;
        PlanDecision = null;
    }

    private void ClearCanvasPlan()
    {
        Canvas.Clear();
        Risks.Clear();
        PlanSummary = string.Empty;
    }

    /// <summary>What the canvas held: the plan drawn, where its nodes were, and the proposal it came from.</summary>
    private sealed record CanvasState(
        string Summary,
        IReadOnlyList<string> Risks,
        IReadOnlyList<TaskSpec> Tasks,
        CanvasLayout Layout,
        PlanProposal? Proposal,
        string? ProposalStatus,
        bool ShowValidation);
}
