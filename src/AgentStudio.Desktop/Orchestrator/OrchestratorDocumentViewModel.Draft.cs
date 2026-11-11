using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// The draft of the tab (B3): the request and the plan being prepared are kept on this machine (<see cref="IPlanDraftStore"/>)
/// a moment after each change, at once when the orchestrator proposes a plan (which spent quota), and when the tab or the app
/// closes; they come back when the tab opens again. A draft is never an approval. Nothing is kept while the canvas shows the
/// plan of a run, nor before the project's storage was opened, as with the positions of the canvas; and a plan kept is never
/// replaced by an empty canvas unless the person emptied it.
/// </summary>
public sealed partial class OrchestratorDocumentViewModel
{
    /// <summary>How long the draft waits after a change before it is saved: typing does not write on every key.</summary>
    public static readonly TimeSpan DraftSaveDelay = TimeSpan.FromSeconds(1);

    private readonly IPlanDraftStore? _drafts;
    private ITimer? _draftTimer;
    private int _draftSuppressed;

    /// <summary>A change of the draft waits to be saved (the pause after it has not ended yet).</summary>
    private bool _draftPending;

    /// <summary>The saved draft was looked at (and brought back when there was one): from then on what changes is saved.</summary>
    private bool _draftOpened;

    /// <summary>The draft kept has a plan: an empty canvas does not replace it unless the person emptied the canvas.</summary>
    private bool _savedDraftHasPlan;

    /// <summary>The person deleted every node of the canvas: an empty plan is what they want kept.</summary>
    private bool _planEmptiedByUser;

    /// <summary>
    /// Saves what is being prepared now, when it can be: the project's storage is open, the canvas is editable and no run is on
    /// screen (the plan there is that run's). A plan kept is not replaced by an empty canvas the person did not empty (a
    /// follow-up prepared, the canvas of a run that left none). A failure is said on the canvas, never thrown.
    /// </summary>
    public void SaveDraftNow()
    {
        _draftPending = false;
        _draftTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_drafts is not { } drafts || !_draftOpened || _draftSuppressed > 0 || Canvas.ProjectRoot is not { } root || Monitor.IsAttached || Canvas.IsReadOnly)
        {
            return;
        }

        var draft = CurrentDraft();
        if (draft.Tasks.Count == 0 && _savedDraftHasPlan && !_planEmptiedByUser)
        {
            return;
        }

        try
        {
            drafts.SaveDraft(root, draft);
            _savedDraftHasPlan = draft.Tasks.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Canvas.Notify(Texts.Format("Orchestrator_Doc_DraftSaveFailed", ex.Message), NoticeKind.Error);
        }
    }

    /// <summary>
    /// The tab or the app closes: what waits to be saved is saved now, and the timer of the pause is let go (a change made
    /// later, with the tab open again, makes a new one).
    /// </summary>
    public void FlushDraft()
    {
        if (_draftPending)
        {
            SaveDraftNow();
        }

        _draftTimer?.Dispose();
        _draftTimer = null;
    }

    /// <summary>Something of the draft changed: it is saved once the changes stop for <see cref="DraftSaveDelay"/>.</summary>
    private void DraftChanged()
    {
        if (_drafts is null || !_draftOpened || _draftSuppressed > 0)
        {
            return;
        }

        _draftPending = true;
        _draftTimer ??= _time.CreateTimer(_ => PostDraftSave(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _draftTimer.Change(DraftSaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void PostDraftSave()
    {
        if (_ui is null)
        {
            SaveDraftNow();
        }
        else
        {
            _ui.Post(_ => SaveDraftNow(), null);
        }
    }

    /// <summary>Nothing changed while it lasts is taken for the draft (the plan of a run shown on the canvas, a draft brought back).</summary>
    private DraftSuppression SuppressDraftSaves()
    {
        _draftSuppressed++;
        return new DraftSuppression(this);
    }

    private PlanDraft CurrentDraft()
    {
        PlanDraftProposal? kept = null;
        if (Proposal is { Plan: { } proposed } proposal && IsUnused(proposal.Run))
        {
            // What the orchestrator planned with goes along only for this proposal and only whole (V43).
            var origin = _planningOrigin is { RequirementsHash: not null } o && o.Run == proposal.Run ? o : null;
            kept = new PlanDraftProposal(proposal.Run, proposed.ContentHash, proposal.Corrected, proposal.HistoryToolNotice, origin?.Request, origin?.RequirementsHash);
        }

        return new(RequestText, PlanSummary, [.. Risks], Canvas.BuildTasks(), kept);
    }

    /// <summary>The run of a proposal has not started a run yet (none of the previous runs has its id).</summary>
    private bool IsUnused(RunId run) => PreviousRuns.All(r => r.Summary.Id != run);

    /// <summary>
    /// Brings back, once the project's storage is open, the draft this installation saved: its request into an empty box and,
    /// while no run is on screen and the canvas is empty, its plan, with the proposal it came from as the plan store keeps it (so
    /// «Ejecutar» keeps its run while the plan is the proposed one). A proposal the store does not have, or whose run already
    /// started, leaves the plan as the user's. It is never approved. A draft that is damaged, too big or not this installation's
    /// is ignored with a discreet notice, and nothing of it is applied.
    /// </summary>
    private async Task RestoreDraftAsync()
    {
        if (_draftOpened || _drafts is not { } drafts || Canvas.ProjectRoot is not { } root || Context?.Project is not { } project)
        {
            return;
        }

        _draftOpened = true;
        var reading = Read(drafts, root);
        if (reading.Ignored is { } why)
        {
            Canvas.Notify(Texts.Format("Orchestrator_Doc_DraftIgnored", why), NoticeKind.Info);
        }

        if (reading.Draft is not { } draft)
        {
            return;
        }

        _savedDraftHasPlan = draft.Tasks.Count > 0;
        PlanProposal? proposal = null;
        if (draft.Proposal is { } origin && IsUnused(origin.Run))
        {
            try
            {
                if (await _session.FindPlanAsync(project, origin.PlanHash, CancellationToken.None).ConfigureAwait(true) is { } proposed
                    && string.Equals(proposed.ContentHash, origin.PlanHash, StringComparison.Ordinal))
                {
                    proposal = new PlanProposal(origin.Run, proposed, [], origin.Corrected, AgentRunStatus.Succeeded, null, null)
                    {
                        HistoryToolNotice = origin.HistoryToolNotice,
                    };
                }
            }
#pragma warning disable CA1031 // The plan store is behind a port: without the proposal, the plan comes back as the user's.
            catch (Exception)
#pragma warning restore CA1031
            {
                proposal = null;
            }
        }

        using (SuppressDraftSaves())
        {
            if (string.IsNullOrWhiteSpace(RequestText))
            {
                RequestText = draft.Request;
            }

            if (Monitor.IsAttached || !Canvas.IsEmpty || draft.Tasks.Count == 0)
            {
                return;
            }

            // The plan came back with its proposal and the request and requirements the orchestrator planned with (V43): the answers
            // it got still reach its run while the request and the requirements are those. A draft of before does not say them, so
            // it sends none rather than guess.
            var planned = proposal is not null && draft.Proposal is { PlannedRequest: { } request, RequirementsHash: { } requirements }
                ? new PlanningOrigin(proposal.Run, request, requirements)
                : null;
            if (TryPutOnCanvas(new CanvasState(draft.Summary, draft.Risks, draft.Tasks, _layout.Load(ProjectRoot), proposal, null, ShowValidation: true, planned)))
            {
                ProposalStatus = proposal is null
                    ? null
                    : WithHistoryNotice(Texts.Get("Orchestrator_Doc_DraftRecoveredProposal"), proposal);
                Canvas.Notify(Texts.Get("Orchestrator_Doc_DraftRecovered"), NoticeKind.Info);
            }
        }
    }

    /// <summary>
    /// The draft as it is saved, as what «Cerrar» brings back when the canvas held a run that finished here when a previous
    /// one was opened; null without one this installation saved.
    /// </summary>
    private CanvasState? SavedDraftCanvas() =>
        _drafts is { } drafts && Canvas.ProjectRoot is { } root && Read(drafts, root).Draft is { Tasks.Count: > 0 } draft
            ? new CanvasState(draft.Summary, draft.Risks, draft.Tasks, _layout.Load(ProjectRoot), null, null, ShowValidation: true)
            : null;

    /// <summary>What the store says of the draft; a store that fails is a draft that cannot be read, never an error on screen.</summary>
    private static DraftReading Read(IPlanDraftStore drafts, string root)
    {
        try
        {
            return drafts.ReadDraft(root);
        }
#pragma warning disable CA1031 // The draft is a convenience: whatever its store throws, the tab opens without it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new DraftReading(null, Texts.Get("Orchestrator_Draft_Unreadable"));
        }
    }

    /// <summary>Ends a <see cref="SuppressDraftSaves"/>, once however often it is disposed.</summary>
    private sealed class DraftSuppression(OrchestratorDocumentViewModel owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (!_ended)
            {
                _ended = true;
                owner._draftSuppressed--;
            }
        }
    }
}
