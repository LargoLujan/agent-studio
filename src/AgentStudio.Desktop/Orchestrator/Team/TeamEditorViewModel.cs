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

    /// <summary>
    /// Whether the change grants power that asks Windows Hello (docs/17 D-37): to an agent already in the template, more folders to
    /// write in or a tool of high risk it did not have; to a new agent (<paramref name="before"/> null), any tool of high risk or
    /// any folder of writing of its own (an agent without folders has the whole project, as every copy of a template does).
    /// </summary>
    public static bool GrantsPower(AgentDefinition? before, AgentDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return before is null
            ? after.AllowedTools.Any(IsHighRisk) || after.PathGrants.Any(g => g.Access == FileAccessLevel.ReadWrite)
            : Widens(before, after) || GrantsHighRiskTool(before, after);
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
        if (await ApplyAsync(agent, _text.Format("Canvas_Editor_Added", agent.Id.Value, agent.Name)).ConfigureAwait(true))
        {
            PendingAdd = null;
        }
    }

    // ---------- Configuring and removing ----------

    /// <summary>Saves <paramref name="updated"/> as the agent of its id in the template: <see cref="ApplyManyAsync"/> with one agent.</summary>
    public Task<bool> ApplyAsync(AgentDefinition updated, string notice)
    {
        ArgumentNullException.ThrowIfNull(updated);
        return ApplyManyAsync([updated], [], notice);
    }

    /// <summary>
    /// Writes a set of changes of the template all or nothing (the fixes of one click, and their undo and redo, change several
    /// agents at once): everything is worked out first (the template accepts each agent, the store can remove what has to go, and
    /// the team that is left still has its orchestrator and a judge: <see cref="TeamRemoval"/>) and Windows Hello is asked once for
    /// the whole set when any of it grants power (D-37), before anything is saved or removed. The store has no operation by lots, so
    /// the set is written as a sequence, the saves first and the removals last, and if the store fails halfway what was already
    /// written goes back (the previous version of each agent saved, and the ones removed saved again), so the template is never
    /// left in between; what a revert cannot give back is the place of an agent that was removed and saved again, which goes to the
    /// end of the list. Taking an agent out grants nothing, so on its own it asks for no Hello. The approval is withdrawn once, at the
    /// end. Returns whether everything was written.
    /// </summary>
    public async Task<bool> ApplyManyAsync(IReadOnlyList<AgentDefinition> updates, IReadOnlyList<AgentId> removals, string notice)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(removals);
        ErrorMessage = null;
        Notice = null;
        if (_save is not { } save || _project is not { } project || _canvas.IsReadOnly)
        {
            ErrorMessage = _text.Get("Canvas_Team_ReadOnly");
            return false;
        }

        if (removals.Count > 0 && _remove is null)
        {
            ErrorMessage = _text.Get("Canvas_Editor_CannotRemove");
            return false;
        }

        foreach (var update in updates)
        {
            if (update.Validate() is { Count: > 0 } problems)
            {
                ErrorMessage = _text.Format("Canvas_Editor_Refused", string.Join(", ", problems));
                return false;
            }
        }

        // What a removal leaves is looked at before anything is asked or written: the team keeps the orchestrator that plans and the
        // judge that reviews (P48). The store takes whatever it is told, so it is not its job.
        if (removals.Count > 0 && TeamRemoval.Problems(_roster, After(updates, removals)) is { Count: > 0 } missing)
        {
            ErrorMessage = _text.Format("Canvas_Editor_Refused", string.Join(", ", missing.Select(ProblemText)));
            return false;
        }

        // The agents already in the template go first and the new ones last: a failure on the first leaves nothing new behind.
        var ordered = updates.OrderBy(u => _roster.Any(a => a.Id == u.Id) ? 0 : 1).ToList();
        var power = ordered.Where(u => AgentCapabilities.GrantsPower(_roster.FirstOrDefault(a => a.Id == u.Id), u)).Select(u => u.Id.Value).ToList();
        if (power.Count > 0)
        {
            var names = string.Join(", ", power);
            if (_confirm is null || !await _confirm(PresenceAction.GrantHighRiskCapability, _text.Format("Canvas_Hello_Grant", names), CancellationToken.None).ConfigureAwait(true))
            {
                ErrorMessage = _text.Format("Canvas_Editor_NotConfirmed", names);
                return false;
            }
        }

        var written = new List<(AgentDefinition? Before, AgentDefinition After)>();
        var removed = new List<AgentDefinition>();
        try
        {
            foreach (var update in ordered)
            {
                var before = _roster.FirstOrDefault(a => a.Id == update.Id);
                await save(project, update).ConfigureAwait(true);
                written.Add((before, update));
            }

            foreach (var id in removals)
            {
                if (_roster.FirstOrDefault(a => a.Id == id) is { } gone)
                {
                    await _remove!(project, id).ConfigureAwait(true);
                    removed.Add(gone);
                }
            }
        }
#pragma warning disable CA1031 // The template is behind a port: a failure is shown in the layer, and what was saved goes back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await RevertAsync(project, written, removed).ConfigureAwait(true);
            ErrorMessage = _text.Format("Canvas_Editor_NotSaved", ex.Message);
            return false;
        }

        await _saved().ConfigureAwait(true);
        Notice = notice + " " + ConsequenceText;
        return true;
    }

    /// <summary>The template as it would be once <see cref="ApplyManyAsync"/> is done: the agents saved (new ones at the end), and then the ones taken out.</summary>
    private List<AgentDefinition> After(IReadOnlyList<AgentDefinition> updates, IReadOnlyList<AgentId> removals)
    {
        var result = _roster.ToList();
        foreach (var update in updates)
        {
            var at = result.FindIndex(a => a.Id == update.Id);
            if (at >= 0)
            {
                result[at] = update;
            }
            else
            {
                result.Add(update);
            }
        }

        result.RemoveAll(a => removals.Contains(a.Id));
        return result;
    }

    /// <summary>The sentence of a code of <see cref="TeamRemoval"/>, from the same texts as the problems of the layer, without its final stop (the message that holds it has its own).</summary>
    private string ProblemText(string code) =>
        _text.Get("Canvas_Problem_" + code[(code.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('-', '_')).TrimEnd('.');

    /// <summary>Puts back what was saved before a failure: the previous version of each agent, and out again the ones that were new (when the store can).</summary>
    private async Task RevertAsync(ProjectContext project, List<(AgentDefinition? Before, AgentDefinition After)> written, List<AgentDefinition> removed)
    {
        foreach (var (before, after) in Enumerable.Reverse(written))
        {
            try
            {
                if (before is not null)
                {
                    await _save!(project, before).ConfigureAwait(true);
                }
                else if (_remove is not null)
                {
                    await _remove(project, after.Id).ConfigureAwait(true);
                }
            }
#pragma warning disable CA1031 // A revert that fails cannot be reverted: the layer shows the template read again, as the store holds it.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Nothing more can be done here; the document reads the template again.
            }
        }

        foreach (var gone in removed)
        {
            try
            {
                await _save!(project, gone).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // See above.
            catch (Exception)
#pragma warning restore CA1031
            {
                // See above.
            }
        }

        await _saved().ConfigureAwait(true);
    }

    /// <summary>
    /// «Quitar del equipo»: a set of changes with nothing to save and one agent to take out, so it goes the way every change of the
    /// template does (<see cref="ApplyManyAsync"/>): the team that is left is checked first (the orchestrator and the last judge stay),
    /// and a failure of the store is shown in the layer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync()
    {
        if (_canvas.SelectedAgent is not { } node || _remove is null)
        {
            return;
        }

        await ApplyManyAsync([], [node.Id], _text.Format("Canvas_Editor_Removed", node.Id.Value)).ConfigureAwait(true);
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
