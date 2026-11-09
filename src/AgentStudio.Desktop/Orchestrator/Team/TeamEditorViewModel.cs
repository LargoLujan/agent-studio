using System.Collections.ObjectModel;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Team;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// Asks the person to confirm an action of the fixed barrier (docs/17 D-37, WP5) with Windows Hello or, without it, the lock of
/// Windows: true when it was confirmed. The document composes it from its reinforced confirmation; without one nothing that
/// grants a capability of high risk is done.
/// </summary>
public delegate Task<bool> PresenceConfirmer(PresenceAction action, string message, CancellationToken cancellationToken);

/// <summary>The tools whose grant changes the threat model, and the folders an agent writes in, as the editor judges them (docs/17 D-37, ADR-0018).</summary>
public static class AgentCapabilities
{
    /// <summary>The tools that, given to an agent, ask Windows Hello (shell and network).</summary>
    public static IReadOnlyList<string> HighRiskTools { get; } = ["Bash", "WebFetch", "WebSearch"];

    /// <summary>Whether <paramref name="tool"/> is of high risk.</summary>
    public static bool IsHighRisk(string tool) => HighRiskTools.Contains(tool, StringComparer.Ordinal);

    /// <summary>The folders an agent writes in; null means the whole project (it has no folders of its own).</summary>
    public static IReadOnlyList<string>? WriteFolders(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.PathGrants.Count == 0
            ? null
            : [.. agent.PathGrants.Where(g => g.Access == FileAccessLevel.ReadWrite).Select(g => TeamCanvasViewModel.AsFolder(g.Root) ?? g.Root).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether the agent writes in more places after the change than before it: the whole project is more than any list of folders, and a folder more than it had.</summary>
    public static bool Widens(AgentDefinition before, AgentDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var was = WriteFolders(before);
        var now = WriteFolders(after);
        if (now is null)
        {
            return was is not null;
        }

        return was is not null && now.Any(f => !was.Contains(f, StringComparer.Ordinal));
    }

    /// <summary>Whether the change gives the agent a tool of high risk it did not have.</summary>
    public static bool GrantsHighRiskTool(AgentDefinition before, AgentDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return after.AllowedTools.Any(t => IsHighRisk(t) && !before.AllowedTools.Contains(t, StringComparer.Ordinal));
    }
}

/// <summary>The agent the person is about to add: the role, the program and model it asks for (docs/17 D-09), and the name and id it would get.</summary>
public sealed partial class NewAgentViewModel : ObservableObject
{
    private readonly CanvasText _text;

    internal NewAgentViewModel(AgentRole role, IReadOnlyList<AgentDefinition> roster, ModelCatalog? catalog, CanvasText text)
    {
        _text = text;
        Role = role;
        Catalog = catalog;
        Providers = [ProviderId.ClaudeCode, ProviderId.CodexCli];
        _provider = DefaultProvider(role, roster);
        Id = FreeId(role, roster);
        Name = _text.Format("Canvas_Add_Name", _text.Role(role), Number(Id));
        _models = OptionsOf(_provider);
        _model = _models[0];
        _efforts = EffortsOf(_provider);
        _effort = _efforts[0];
    }

    public AgentRole Role { get; }

    public AgentId Id { get; }

    public string Name { get; }

    public string RoleText => _text.Role(Role);

    public ModelCatalog? Catalog { get; }

    public IReadOnlyList<ProviderId> Providers { get; }

    [ObservableProperty]
    private ProviderId _provider;

    [ObservableProperty]
    private IReadOnlyList<ModelOption> _models;

    [ObservableProperty]
    private ModelOption _model;

    [ObservableProperty]
    private IReadOnlyList<EffortOption> _efforts;

    [ObservableProperty]
    private EffortOption _effort;

    /// <summary>The company of the program: the judge has to be of one other than the orchestrator's and the executors'.</summary>
    public VendorId Vendor => VendorOf(Provider);

    partial void OnProviderChanged(ProviderId value)
    {
        Models = OptionsOf(value);
        Model = Models[0];
        Efforts = EffortsOf(value);
        Effort = Efforts[0];
        OnPropertyChanged(nameof(Vendor));
    }

    internal static VendorId VendorOf(ProviderId provider) => provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic;

    private IReadOnlyList<ModelOption> OptionsOf(ProviderId provider)
    {
        var options = new List<ModelOption> { new(_text.Get("Canvas_Model_ProgramDefault"), null) };
        var named = Catalog?.Entries.Where(e => e.Provider == provider).Select(e => e.Name).ToList() ?? [];
        foreach (var name in named.Count > 0 ? named : AgentModels.Known(provider))
        {
            options.Add(new ModelOption(name, name));
        }

        return options;
    }

    private IReadOnlyList<EffortOption> EffortsOf(ProviderId provider) =>
    [
        new EffortOption(_text.Get("Models_Effort_CliDefault"), null),
        .. ReasoningEfforts.For(provider).Select(e => new EffortOption(_text.Effort(e), e)),
    ];

    private static ProviderId DefaultProvider(AgentRole role, IReadOnlyList<AgentDefinition> roster)
    {
        if (role != AgentRole.Judge)
        {
            return ProviderId.ClaudeCode;
        }

        // A judge of the company that produced nothing: the other one than the orchestrator's and the executors' (ADR-0014).
        var producers = roster.Where(a => a.Role is AgentRole.Orchestrator || TeamRoles.Writes(a.Role)).Select(a => a.Model.Vendor).Distinct().ToList();
        return producers.Contains(VendorId.OpenAI) && !producers.Contains(VendorId.Anthropic) ? ProviderId.ClaudeCode : ProviderId.CodexCli;
    }

    private static AgentId FreeId(AgentRole role, IReadOnlyList<AgentDefinition> roster)
    {
        // The ids of the roles are the ones of the default template (ejecutor-2, juez-2…): they are identifiers, never shown as text.
        var baseId = role switch
        {
            AgentRole.Orchestrator => DefaultAgentRoster.Orchestrator.Value,
            AgentRole.Judge => DefaultAgentRoster.Judge.Value,
            _ => DefaultAgentRoster.Executor.Value,
        };
        var taken = roster.Select(a => a.Id.Value).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseId))
        {
            return new AgentId(baseId);
        }

        for (var n = 2; ; n++)
        {
            if (!taken.Contains($"{baseId}-{n}"))
            {
                return new AgentId($"{baseId}-{n}");
            }
        }
    }

    private static string Number(AgentId id)
    {
        var dash = id.Value.LastIndexOf('-');
        return dash > 0 && int.TryParse(id.Value[(dash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }
}

/// <summary>A level of effort of the list of a program, or «el del programa» (null).</summary>
public sealed record EffortOption(string Label, ReasoningEffort? Effort)
{
    public override string ToString() => Label;
}

/// <summary>
/// Adds, removes and configures the agents of the Team layer (docs/17 D-08, D-09 and D-42, F13-2). It edits the template, with the
/// consequences of always: saving changes its <c>RosterHash</c> and withdraws the approval of the plan, and the layer says it
/// before and after. The limits (one orchestrator, a judge of another company, up to 8 executors) are seen while assembling, and a
/// change that grants a capability of high risk (shell, network, more folders) is done only after Windows Hello (D-37). The
/// template is saved by the same callbacks the preparation uses (<see cref="RosterEditorViewModel"/>): nothing here writes anywhere.
/// </summary>
public sealed partial class TeamEditorViewModel : ObservableObject
{
    private readonly TeamCanvasViewModel _canvas;
    private readonly CanvasText _text;
    private readonly Func<ProjectContext, AgentDefinition, Task>? _save;
    private readonly Func<ProjectContext, AgentId, Task>? _remove;
    private readonly Func<Task> _saved;
    private PresenceConfirmer? _confirm;
    private ProjectContext? _project;
    private IReadOnlyList<AgentDefinition> _roster = [];

    /// <param name="canvas">The Team layer this edits.</param>
    /// <param name="save">Saves one agent of the template (the same callback as the preparation); null when it cannot be saved.</param>
    /// <param name="remove">Removes one agent from the template; null when the store cannot (no button is shown).</param>
    /// <param name="saved">Called after the template was saved: the document withdraws the approval and reads the template again.</param>
    public TeamEditorViewModel(
        TeamCanvasViewModel canvas,
        Func<ProjectContext, AgentDefinition, Task>? save,
        Func<ProjectContext, AgentId, Task>? remove,
        Func<Task> saved,
        CanvasText? text = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(saved);
        _canvas = canvas;
        _save = save;
        _remove = remove;
        _saved = saved;
        _text = text ?? new CanvasText();
        _canvas.FolderCableRequested += (_, request) => Saving = ApplyCableAsync(request);
        _canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TeamCanvasViewModel.SelectedAgent))
            {
                RebuildInspector();
            }

            if (e.PropertyName is nameof(TeamCanvasViewModel.IsReadOnly))
            {
                RefreshCommands();
            }
        };
    }

    /// <summary>The inspector of the agent selected; null when none is.</summary>
    [ObservableProperty]
    private AgentInspectorViewModel? _inspector;

    /// <summary>The agent being added, waiting for its program and model; null when none is.</summary>
    [ObservableProperty]
    private NewAgentViewModel? _pendingAdd;

    /// <summary>The last change in a sentence, with what it caused.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>Why the last change was not made.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The plan on the canvas is approved: changing the template withdraws it, and the layer says so before it is changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsequenceText))]
    private bool _isPlanApproved;

    /// <summary>The last save, for a test or a caller that has to wait for it.</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>The context of the recommendations and the data of the models, for the inspector; set by the document.</summary>
    public InspectorContext Context { get; set; } = InspectorContext.None;

    /// <summary>What editing this layer does, said before saving (D-08): it is the template, and the approval of the plan goes.</summary>
    public string ConsequenceText => IsPlanApproved ? _text.Get("Canvas_Editor_ConsequenceApproved") : _text.Get("Canvas_Editor_Consequence");

    public ObservableCollection<string> Limits => _canvas.Problems;

    public string LimitsText => _canvas.LimitsText;

    /// <summary>The store can remove an agent: only then is «Quitar» shown.</summary>
    public bool CanRemoveAgents => _remove is not null;

    public bool CanEdit => _save is not null && _project is not null && !_canvas.IsReadOnly;

    public bool CanAddExecutor => CanEdit && _roster.Count(a => a.Role == AgentRole.Executor) < TeamLimits.MaxExecutors;

    public bool CanAddJudge => CanEdit;

    public bool CanAddOrchestrator => CanEdit && _roster.All(a => a.Role != AgentRole.Orchestrator);

    /// <summary>Why an executor cannot be added now; empty when it can.</summary>
    public string AddExecutorHint => CanAddExecutor ? string.Empty : CanEdit ? _text.Get("Canvas_Problem_too_many_executors") : _text.Get("Canvas_Team_ReadOnly");

    /// <summary>Sets how the editor asks Windows Hello; null: nothing of high risk is granted.</summary>
    public void UseConfirmation(PresenceConfirmer? confirm) => _confirm = confirm;

    /// <summary>Takes the project and the template the layer shows; called whenever the context was read again.</summary>
    public void Load(ProjectContext? project, IReadOnlyList<AgentDefinition> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _project = project;
        _roster = roster;
        PendingAdd = null;
        RefreshCommands();
        RebuildInspector();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAddExecutor));
        OnPropertyChanged(nameof(CanAddJudge));
        OnPropertyChanged(nameof(CanAddOrchestrator));
        OnPropertyChanged(nameof(AddExecutorHint));
        OnPropertyChanged(nameof(LimitsText));
        OnPropertyChanged(nameof(CanRemoveAgents));
        StartAddExecutorCommand.NotifyCanExecuteChanged();
        StartAddJudgeCommand.NotifyCanExecuteChanged();
        StartAddOrchestratorCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RebuildInspector() =>
        Inspector = _canvas.SelectedAgent is { } node && _roster.FirstOrDefault(a => a.Id == node.Id) is { } agent
            ? new AgentInspectorViewModel(agent, this, _canvas, Context, _text)
            : null;

    // ---------- Adding ----------

    [RelayCommand(CanExecute = nameof(CanAddExecutor))]
    private void StartAddExecutor() => PendingAdd = new NewAgentViewModel(AgentRole.Executor, _roster, Context.Catalog, _text);

    [RelayCommand(CanExecute = nameof(CanAddJudge))]
    private void StartAddJudge() => PendingAdd = new NewAgentViewModel(AgentRole.Judge, _roster, Context.Catalog, _text);

    [RelayCommand(CanExecute = nameof(CanAddOrchestrator))]
    private void StartAddOrchestrator() => PendingAdd = new NewAgentViewModel(AgentRole.Orchestrator, _roster, Context.Catalog, _text);

    [RelayCommand]
    private void CancelAdd() => PendingAdd = null;

    /// <summary>
    /// «Añadir»: copies the template of the role (tools, memory and review) with a new id (<c>ejecutor-2</c>) and the program and
    /// model that were asked for, and saves it as part of the template.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAddAsync()
    {
        if (PendingAdd is not { } pending)
        {
            return;
        }

        var template = _roster.FirstOrDefault(a => a.Role == pending.Role)
            ?? DefaultAgentRoster.Create().FirstOrDefault(a => a.Role == pending.Role);
        if (template is null)
        {
            return;
        }

        var model = pending.Model.Model;
        var agent = template with
        {
            Id = pending.Id,
            Name = pending.Name,
            Model = new ModelBinding(pending.Provider, pending.Vendor, model),
            PathGrants = [],
            Effort = pending.Effort.Effort,
            AllowedTools = [.. template.AllowedTools.Where(t => !AgentCapabilities.IsHighRisk(t))],
        };
        // A judge of the company of the orchestrator is added all the same: the analysis band says it is not independent.
        await SaveAsync(agent, _text.Format("Canvas_Editor_Added", agent.Id.Value, agent.Name)).ConfigureAwait(true);
        if (ErrorMessage is null)
        {
            PendingAdd = null;
        }
    }

    // ---------- Configuring and removing ----------

    /// <summary>
    /// Saves <paramref name="updated"/> as the agent of its id in the template (the inspector and the cables go through here), after
    /// the confirmation of Windows Hello when it grants a capability of high risk. Returns whether it was saved.
    /// </summary>
    public async Task<bool> ApplyAsync(AgentDefinition updated, string notice)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var before = _roster.FirstOrDefault(a => a.Id == updated.Id);
        if (before is not null && (AgentCapabilities.Widens(before, updated) || AgentCapabilities.GrantsHighRiskTool(before, updated)))
        {
            var action = PresenceAction.GrantHighRiskCapability;
            if (_confirm is null || !await _confirm(action, _text.Format("Canvas_Hello_Grant", updated.Id.Value), CancellationToken.None).ConfigureAwait(true))
            {
                ErrorMessage = _text.Format("Canvas_Editor_NotConfirmed", updated.Id.Value);
                Notice = null;
                return false;
            }
        }

        await SaveAsync(updated, notice).ConfigureAwait(true);
        return ErrorMessage is null;
    }

    private async Task SaveAsync(AgentDefinition agent, string notice)
    {
        ErrorMessage = null;
        Notice = null;
        if (_save is not { } save || _project is not { } project || _canvas.IsReadOnly)
        {
            ErrorMessage = _text.Get("Canvas_Team_ReadOnly");
            return;
        }

        if (agent.Validate() is { Count: > 0 } problems)
        {
            ErrorMessage = _text.Format("Canvas_Editor_Refused", string.Join(", ", problems));
            return;
        }

        try
        {
            await save(project, agent).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The template is behind a port: a failure is shown in the layer, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = _text.Format("Canvas_Editor_NotSaved", ex.Message);
            return;
        }

        await _saved().ConfigureAwait(true);
        Notice = notice + " " + ConsequenceText;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync()
    {
        if (_canvas.SelectedAgent is not { } node || _remove is not { } remove || _project is not { } project)
        {
            return;
        }

        ErrorMessage = null;
        Notice = null;
        try
        {
            await remove(project, node.Id).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The template is behind a port: a failure is shown in the layer, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = _text.Format("Canvas_Editor_NotSaved", ex.Message);
            return;
        }

        await _saved().ConfigureAwait(true);
        Notice = _text.Format("Canvas_Editor_Removed", node.Id.Value) + " " + ConsequenceText;
    }

    private bool CanRemoveSelected() => CanEdit && _remove is not null && _canvas.SelectedAgent is not null;

    // ---------- Cables «Trabaja en» ----------

    private Task ApplyCableAsync(FolderCableRequest request)
    {
        if (_roster.FirstOrDefault(a => a.Id == request.Agent) is not { } agent)
        {
            return Task.CompletedTask;
        }

        var folders = AgentCapabilities.WriteFolders(agent)?.ToList() ?? [];
        if (request.Connect)
        {
            if (request.Folder is null)
            {
                folders.Clear();
            }
            else if (!folders.Contains(request.Folder, StringComparer.Ordinal))
            {
                folders.Add(request.Folder);
            }
        }
        else if (request.Folder is not null)
        {
            folders.Remove(request.Folder);
        }

        // An agent without folders has the whole project: removing its last cable gives it all of it again.
        var updated = agent with
        {
            PathGrants =
            [
                .. agent.PathGrants.Where(g => g.Access != FileAccessLevel.ReadWrite),
                .. folders.Select(f => new PathGrant(f, FileAccessLevel.ReadWrite)),
            ],
        };
        var notice = request.Connect
            ? _text.Format("Canvas_Editor_Connected", agent.Id.Value, request.Folder ?? _text.Get("Canvas_Repo_WholeProject"))
            : _text.Format("Canvas_Editor_Disconnected", agent.Id.Value, request.Folder ?? _text.Get("Canvas_Repo_WholeProject"));
        return ApplyAsync(updated, notice);
    }
}
