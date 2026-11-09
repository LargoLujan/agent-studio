using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

// What the composition of the app gives the document at the end of Phase 13 (docs/04 sections 21.8 to 21.10): the sources of its
// monitor, the options of the project (step 7 of the configurator), the questions of the orchestrator before it plans and the
// entry of «Configurar proyecto». Everything here is optional: a document made without it behaves as it did before.
public sealed partial class OrchestratorDocumentViewModel
{
    private MonitorServices? _monitorServices;
    private IProjectOptionsStore? _projectOptionsStore;
    private readonly List<IDisposable> _monitorReleases = [];
    private string? _optionsSeededFor;
    private ProjectRunOptions _projectOptions = ProjectRunOptions.Defaults;
    private CancellationTokenSource? _questionWait;

    /// <summary>The questions the orchestrator asked instead of a plan, waiting for the person (one round at most); null when none wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlannerQuestions))]
    private PlannerQuestionSet? _plannerQuestions;

    public bool HasPlannerQuestions => PlannerQuestions is not null;

    /// <summary>The person pressed «Configurar proyecto» in the band of preparation: the shell opens the configurator of this project.</summary>
    public event EventHandler? ConfigureProjectRequested;

    private void UseComposition(MonitorServices? monitors, IProjectOptionsStore? projectOptions)
    {
        _monitorServices = monitors;
        _projectOptionsStore = projectOptions;
    }

    /// <summary>Gives a monitor of this document the sources, the quota, the analysis of consumption and the windows of its questions.</summary>
    private void EquipMonitor(RunMonitorViewModel monitor)
    {
        if (_monitorServices is { } services)
        {
            _monitorReleases.Add(services.Equip(monitor));
        }
    }

    /// <summary>What the monitor needs from the document before it follows a run: the template, and the dependencies of the plan on the canvas.</summary>
    private void GiveLiveContext(RunMonitorViewModel monitor)
    {
        monitor.Roster = Context?.Roster ?? [];
        monitor.PlanDependencies = Canvas.IsEmpty ? new Dictionary<TaskId, IReadOnlyList<TaskId>>() : DependenciesOf(CurrentPlan());
    }

    private static Dictionary<TaskId, IReadOnlyList<TaskId>> DependenciesOf(PlanDefinition plan) =>
        plan.Tasks.ToDictionary(t => t.Id, t => (IReadOnlyList<TaskId>)[.. t.DependsOn.Select(d => d.Prerequisite)]);

    // ---------- The options of the project (step 7 of the configurator) ----------

    /// <summary>
    /// The options the person chose for the project (<see cref="IProjectOptionsStore"/>), or the values of always when there are none
    /// or the store cannot say: a run takes them from here, so what the screen of the budget promises is what the run gets.
    /// </summary>
    private async Task<ProjectRunOptions> LoadProjectOptionsAsync(ProjectContext project)
    {
        if (_projectOptionsStore is not { } store)
        {
            return ProjectRunOptions.Defaults;
        }

        try
        {
            return await store.LoadAsync(project, CancellationToken.None).ConfigureAwait(true) ?? ProjectRunOptions.Defaults;
        }
#pragma warning disable CA1031 // The store is behind a port: without options the project goes on with the values of always.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ProjectRunOptions.Defaults;
        }
    }

    /// <summary>
    /// The budget of the project reaches the canvas when it opens (the analysis and «Presupuesto» read it there) and again after the
    /// configurator applied a change. A raise made on the canvas afterwards is the person's and is kept until then.
    /// </summary>
    private async Task SeedProjectOptionsAsync(ProjectContext project, bool force)
    {
        if (!force && string.Equals(_optionsSeededFor, project.ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        _optionsSeededFor = project.ProjectId;
        _projectOptions = await LoadProjectOptionsAsync(project).ConfigureAwait(true);
        if (Builder is { } builder && builder.MaxAgentRuns != _projectOptions.MaxAgentRuns)
        {
            builder.MaxAgentRuns = _projectOptions.MaxAgentRuns;
        }
    }

    /// <summary>The options of a run: those of the project, with the consent of the person for this run and the budget the canvas shows.</summary>
    private RunOptions RunOptionsFor(ProjectRunOptions stored) =>
        stored.ToRunOptions() with
        {
            AutoResumeConsent = AutoResumeConsent,
            MaxAgentRuns = Builder?.MaxAgentRuns ?? stored.MaxAgentRuns,
        };

    // ---------- «Configurar proyecto» ----------

    /// <summary>«Configurar proyecto»: asks the shell for the configurator of this project; the shell opens it.</summary>
    [RelayCommand]
    private void ConfigureProject() => ConfigureProjectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The configurator applied its changes: with a new template the tab reads it again and the approval of the plan that waits is
    /// withdrawn (the same as when a model is changed here); the budget of the project reaches the canvas.
    /// </summary>
    public async Task OnSetupAppliedAsync(SetupApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RosterChanged)
        {
            await OnRosterSavedAsync().ConfigureAwait(true);
        }

        if (Context?.Project is { } project)
        {
            await SeedProjectOptionsAsync(project, force: true).ConfigureAwait(true);
        }
    }

    // ---------- The questions of the orchestrator before it plans ----------

    /// <summary>
    /// The orchestrator asked instead of planning (docs/17 D-28): the questions open as windows (or the tray says so), and when
    /// every one has its answer the plan is asked again with them, once: a planning with answers never asks again.
    /// </summary>
    private void AskPlannerQuestions(PlanningRequest request, PlanProposal proposal)
    {
        CancelPlannerQuestions();
        var set = new PlannerQuestionSet(proposal.Run, proposal.Questions!);
        PlannerQuestions = set;
        _monitorServices?.PresentPlannerQuestions(set, activate: false);
        var wait = new CancellationTokenSource();
        _questionWait = wait;
        _ = ReplanWithAnswersAsync(request, set, wait);
    }

    private async Task ReplanWithAnswersAsync(PlanningRequest request, PlannerQuestionSet set, CancellationTokenSource wait)
    {
        IReadOnlyList<AnsweredQuestion> answers;
        try
        {
            answers = await set.Answers.WaitAsync(wait.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_questionWait, wait))
        {
            return;
        }

        _questionWait = null;
        wait.Dispose();
        PlannerQuestions = null;
        await PlanCoreAsync(request with { Answers = answers }).ConfigureAwait(true);
    }

    /// <summary>Stops waiting for the answers of the orchestrator: the person rejected, asked for another plan or the plan went on.</summary>
    private void CancelPlannerQuestions()
    {
        var wait = _questionWait;
        _questionWait = null;
        if (wait is not null)
        {
            wait.Cancel();
            wait.Dispose();
        }

        if (PlannerQuestions is { } set)
        {
            _monitorServices?.WithdrawPlannerQuestions(set);
            PlannerQuestions = null;
        }
    }

    /// <summary>«Responder» next to the status of the proposal: opens the windows of the questions of the orchestrator again.</summary>
    [RelayCommand]
    private void OpenPlannerQuestions()
    {
        if (PlannerQuestions is { } set)
        {
            _monitorServices?.PresentPlannerQuestions(set, activate: true);
        }
    }

    private string PlannerQuestionsStatus(PlanProposal proposal) =>
        Texts.Format("Shell_Planner_Questions", proposal.Questions?.Count ?? 0);
}
