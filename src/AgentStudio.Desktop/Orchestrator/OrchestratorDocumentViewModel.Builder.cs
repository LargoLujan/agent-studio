using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Team;

namespace AgentStudio.Desktop.Orchestrator;

// The visual builder of the document (docs/17 section 4, ADR-0048, WP6 of Phase 13): the Team layer next to the plan, the strip of
// the team, the inspector, the live analysis band, «Revisar el orden» and the chat of preparation. It is all optional: a document
// created without BuilderServices has none of it and behaves as it did before Phase 13, so nothing here is simulated.
public sealed partial class OrchestratorDocumentViewModel
{
    private bool _teamLoaded;
    private string? _proposalRosterHash;

    /// <summary>
    /// The visual builder: the editor of the team and its inspector, the analysis band, «Revisar el orden» and the chat of
    /// preparation. Null when the document was created without <see cref="BuilderServices"/>.
    /// </summary>
    public VisualBuilder? Builder { get; private set; }

    /// <summary>
    /// Gives the document the visual builder. The composition passes <paramref name="services"/> (the ones it has; none of them is
    /// required) and, for «Reducir movimiento», <paramref name="reduceMotion"/>, which reads the setting each time the animations
    /// would start. Called once, by the constructor.
    /// </summary>
    private void InitializeBuilder(BuilderServices services, Func<bool>? reduceMotion)
    {
        var text = new CanvasText();
        var host = new DraftHost(this, text, services);
        var band = new AnalysisBandViewModel(host, text, _time, _ui);
        var editor = new TeamEditorViewModel(
            Canvas.Team,
            _session.CanSaveAgents ? (project, agent) => _session.SaveAgentAsync(project, agent, CancellationToken.None) : null,
            null,
            OnRosterSavedAsync,
            text);
        editor.UseConfirmation(_confirmation is null ? null : ConfirmPresenceAsync);
        var order = new OrderReviewViewModel(services, OrderSubject, band.ApplyFixAsync, text, _time);
        var chat = new PreparationChatViewModel(services, ChatSubject, Requirements.CopySuggestion, band.ApplyFixAsync, text, _time);
        var brief = new ProjectBriefViewModel(services.Brief, () => Context?.Project, text);
        Builder = new VisualBuilder(Canvas, services, text, _time, reduceMotion ?? (() => false), () => Monitor.ContextThresholds, editor, band, order, chat, brief);
        Canvas.Team.FolderCableRequested += (_, _) => band.Schedule();
        Closed += (_, _) =>
        {
            _ = chat.EndCommand.ExecuteAsync(null);
            Builder.Dispose();
        };
    }

    /// <summary>
    /// A confirmation of the fixed barrier (docs/17 D-37, WP5): Windows Hello, or the lock of Windows when it is not there, for an
    /// action that always asks. Only a confirmation counts; a click alone never does.
    /// </summary>
    private async Task<bool> ConfirmPresenceAsync(PresenceAction action, string message, CancellationToken cancellationToken)
    {
        if (_confirmation is not { } confirmation)
        {
            return false;
        }

        var plan = await confirmation.PlanAsync(action, cancellationToken).ConfigureAwait(true);
        if (!plan.ConfirmsAfterYes)
        {
            return ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof);
        }

        var outcome = await confirmation.ConfirmAsync(plan, message, cancellationToken).ConfigureAwait(true);
        return ReinforcedConfirmation.Approves(plan, outcome.Proof);
    }

    /// <summary>What the document does when its context is read again: the Team layer shows the template, the repository is read, and what the log taught is read.</summary>
    private void BuilderContextChanged(SessionContext? value)
    {
        if (Builder is not { } builder)
        {
            return;
        }

        var roster = value?.Roster ?? [];
        var firstShown = !_teamLoaded;
        if (firstShown)
        {
            _teamLoaded = true;
            if (Canvas.IsEmpty)
            {
                // «Equipo» while the person prepares, «Plan» as soon as a proposal arrives (D-07).
                Canvas.Layer = CanvasLayer.Team;
            }

            Canvas.Team.Load(roster, null, _layout.Load(ProjectRoot));
        }
        else
        {
            Canvas.Team.Refresh(roster);
        }

        builder.Editor.Load(value?.Project, roster);
        builder.Editor.Context = InspectorContextNow(builder);
        if (firstShown && value?.Project is { } project)
        {
            BuilderData = LoadBuilderDataAsync(builder, project);
        }

        builder.Band.Reset();
        builder.Band.Schedule();
    }

    /// <summary>
    /// «Agentes» of the side bar opens the document of the project on the Team layer (docs/17 D-42, F13-17): the management of agents
    /// is the layer «Equipo», with the inspector of each agent. The shell calls it after it opens or focuses the tab; without the
    /// builder there is no layer and nothing changes.
    /// </summary>
    public void ShowTeamLayer()
    {
        if (Builder is not null)
        {
            Canvas.Layer = CanvasLayer.Team;
        }
    }

    /// <summary>What the builder reads once when the project opens: the repository (no network), what the log taught of each model, and the quota. The last read, for a test that has to wait for it.</summary>
    public Task BuilderData { get; private set; } = Task.CompletedTask;

    private async Task LoadBuilderDataAsync(VisualBuilder builder, ProjectContext project)
    {
        await builder.LoadRepositoryAsync(project.RootPath).ConfigureAwait(true);
        await builder.LoadLearnedAsync(project.RootPath).ConfigureAwait(true);
        builder.Editor.Context = InspectorContextNow(builder);
        await builder.Brief.LoadAsync().ConfigureAwait(true);
        await builder.RefreshQuotaAsync().ConfigureAwait(true);
    }

    private InspectorContext InspectorContextNow(VisualBuilder builder)
    {
        var requirements = Context?.Requirements?.Requirements ?? [];
        return new InspectorContext(
            builder.Services.Catalog?.Current.Catalog,
            builder.Learned,
            requirements.Count,
            requirements.Count(r => r.Priority == Domain.Requirements.RequirementPriority.Mandatory),
            null,
            Canvas.Team.Repository?.Node?.Stack,
            builder.MaxAgentRuns,
            false);
    }

    /// <summary>What the builder keeps in step with the plan and the approval: «Revisar el orden» offers itself when the notes may be old.</summary>
    private void RefreshBuilder()
    {
        if (Builder is not { } builder)
        {
            return;
        }

        builder.Editor.IsPlanApproved = PlanDecision is { Approved: true };
        var stale = Proposal?.Plan is { } proposed
            && (!string.Equals(proposed.ContentHash, CurrentHash, StringComparison.Ordinal)
                || !string.Equals(_proposalRosterHash, RosterHash.Of(Context?.Roster), StringComparison.Ordinal));
        builder.OrderReview.Refresh(stale);
    }

    private OrderReviewSubject? OrderSubject()
    {
        if (Proposal is not { Plan: not null } proposal || Context is not { Project: { } project } context || context.Orchestrator is not { } orchestrator || Canvas.IsEmpty)
        {
            return null;
        }

        return new OrderReviewSubject(project, proposal.Run, CurrentPlan(), context.Roster, orchestrator.Id);
    }

    private PreparationChatSubject? ChatSubject() =>
        Context is { Project: { } project } context && context.Orchestrator is { } orchestrator
            ? new PreparationChatSubject(project, context.Roster, orchestrator, Builder?.Services.Catalog?.Current.Catalog)
            : null;

    /// <summary>The bridge between the analysis band and the document: what it reads and where it writes (<see cref="IDraftHost"/>).</summary>
    private sealed class DraftHost(OrchestratorDocumentViewModel owner, CanvasText text, BuilderServices services) : IDraftHost
    {
        public FixEnvironment Environment => new(
            text.Get("Canvas_Fix_IntegrationTitle"),
            Steps(),
            owner.Context?.Roster.FirstOrDefault(a => a.Role == AgentRole.Judge) is null ? DefaultAgentRoster.Create().First(a => a.Role == AgentRole.Judge) : null);

        public MontageEditState ReadState() => new(
            owner.Canvas.IsEmpty ? null : owner.CurrentPlan(),
            owner.Context?.Roster ?? [],
            owner.Builder?.MaxAgentRuns ?? 12);

        public MontageDraft? BuildDraft(long generation)
        {
            if (owner.Context is not { } context || owner.Builder is not { } builder || context.PlanContext(Limits) is not { } planContext)
            {
                return null;
            }

            var quota = builder.QuotaObservations
                .Select(o => new QuotaWindowFacts(o.Provider, o.Window, o.Utilization, o.ResetsAt))
                .ToList();
            return new MontageDraft(
                generation,
                new TeamGraph([.. context.Roster.Select(a => new AgentNode(a))], owner.Canvas.Team.Repository?.Node, [.. owner.Canvas.Team.Connections.Select(c => c.Edge)]),
                owner.Canvas.IsEmpty ? null : owner.CurrentPlan(),
                planContext,
                new MontageRunFacts(new RunOptions().MaxParallelTasks, builder.MaxAgentRuns, new Dictionary<Domain.Providers.ProviderId, int>()),
                [],
                quota,
                owner.Proposal?.OrderNotes ?? OrderNotes.None,
                services.Catalog?.Current.Catalog,
                builder.Learned is { } learned ? new MontageLearnedFacts(learned.Performances, null) : MontageLearnedFacts.None,
                owner.Monitor.ContextThresholds)
            {
                Steps = Steps(),
            };
        }

        public async Task<bool> ApplyAsync(MontageEditState before, MontageEditState after, Fix? fix)
        {
            var editor = owner.Builder!.Editor;

            // The template first: a fix that grants a capability asks Windows Hello there, and if it is refused nothing changes.
            foreach (var agent in after.Roster)
            {
                var old = before.Roster.FirstOrDefault(a => a.Id == agent.Id);
                if (old is not null && old.Equals(agent))
                {
                    continue;
                }

                if (!await editor.ApplyAsync(agent, text.Format("Canvas_Editor_Changed", agent.Id.Value)).ConfigureAwait(true))
                {
                    return false;
                }
            }

            if (before.Roster.Any(a => after.Roster.All(n => n.Id != a.Id)))
            {
                // An agent that disappears needs the store to remove it: without that port it is not done.
                return false;
            }

            if (after.MaxAgentRuns != owner.Builder.MaxAgentRuns)
            {
                owner.Builder.MaxAgentRuns = after.MaxAgentRuns;
            }

            if (!string.Equals(before.Plan?.ContentHash, after.Plan?.ContentHash, StringComparison.Ordinal) && after.Plan is { } plan)
            {
                owner.Canvas.LoadTasks(plan.Tasks, owner.Canvas.CaptureLayout());
            }

            return true;
        }

        public string? DescribeValidator(Finding finding)
        {
            if (owner.Context?.PlanContext(Limits) is not { } planContext)
            {
                return null;
            }

            var code = Enum.GetValues<PlanViolationCode>().FirstOrDefault(c => FindingCodes.ForViolation(c) == finding.Code);
            var violation = owner.Violations.FirstOrDefault(v => v.Code == code && (finding.Nodes.Count == 0 || v.Task?.Value == finding.Nodes[0]));
            return violation is null ? null : PlanViolationText.Describe([violation], owner.CurrentPlan(), planContext)[0];
        }

        private List<VerificationStepFacts> Steps() =>
        [
            .. (owner.Context?.Verification?.Steps ?? []).Select(s => new VerificationStepFacts(
                s.Id,
                s.Kind == EvidenceKind.BuildLog,
                s.Kind == EvidenceKind.TestRun,
                s.Requires ?? [])),
        ];
    }
}
