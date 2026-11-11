using System.Collections.ObjectModel;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>One choice of the model of an agent: the CLI's default (<see cref="Model"/> null), a name, or «Otro modelo…» to type one.</summary>
public sealed record ModelOption(string Label, string? Model, bool IsOther = false)
{
    /// <summary>What a screen reader says of the option (A1-bis): its label, never the members of the record.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The models an agent of the template can be given (G1): the default of its CLI, the names the CLI is known to take, and any
/// other the person types, checked with the rule the CLI adapters apply before they launch (<see cref="CliArgumentGuard.Model"/>).
/// </summary>
public static class AgentModels
{
    public static string DefaultLabel => Texts.Get("Orchestrator_Roster_ModelDefault");

    public static string OtherLabel => Texts.Get("Orchestrator_Roster_ModelOther");

    /// <summary>
    /// The names offered for <paramref name="provider"/>: Claude Code's aliases for <c>--model</c> (<c>opus</c>, <c>sonnet</c>,
    /// <c>haiku</c>), and for Codex the model the account accepted (<see cref="DefaultAgentRoster.RevisionJudgeModel"/>). Any
    /// other name is typed.
    /// </summary>
    public static IReadOnlyList<string> Known(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? ["opus", "sonnet", "haiku"]
        : provider == ProviderId.CodexCli ? [DefaultAgentRoster.RevisionJudgeModel]
        : [];

    /// <summary>Why <paramref name="model"/> cannot be given to the CLI, in Spanish; null when it can.</summary>
    public static string? Problem(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Texts.Format("Orchestrator_Roster_ModelEmpty", DefaultLabel);
        }

        try
        {
            CliArgumentGuard.Model(model, nameof(model));
            return null;
        }
        catch (ArgumentException)
        {
            return Texts.Get("Orchestrator_Roster_ModelBad");
        }
    }

    /// <summary>The model in words: «el modelo sonnet», or «el modelo predeterminado del CLI».</summary>
    public static string Describe(string? model) => string.IsNullOrWhiteSpace(model) ? Texts.Get("Orchestrator_Roster_DescribeDefault") : Texts.Format("Orchestrator_Roster_DescribeModel", model);
}

/// <summary>
/// One agent of the template with what the preparation lets change: its model (G1), for every agent, and its «Memoria del
/// proyecto» switch (docs/14 D-23), on being <see cref="MemoryPolicy.ReadSelected"/> (selected trusted memory in its
/// prompts) and off <see cref="MemoryPolicy.None"/>. A judge or an auditor has no memory switch: it never receives memory,
/// so its verdict rests on the dossier alone.
/// </summary>
public sealed partial class AgentMemoryViewModel : ObservableObject
{
    private readonly RosterEditorViewModel _editor;
    private bool _restoring;

    internal AgentMemoryViewModel(AgentDefinition definition, RosterEditorViewModel editor)
    {
        Definition = definition;
        _editor = editor;
        _receivesMemory = definition.Memory != MemoryPolicy.None;
        ModelOptions = OptionsFor(definition);
        _selectedModel = OptionOf(definition.Model.Model);
    }

    /// <summary>The agent as the template stores it.</summary>
    public AgentDefinition Definition { get; }

    public AgentId Id => Definition.Id;

    public string Name => Definition.Name;

    /// <summary>Its CLI and vendor and its tools, in a line.</summary>
    public string Line => RosterText.Tools(Definition);

    /// <summary>Whether the agent may get memory at all: never a judge nor an auditor (<c>agent.judge-memory-none</c>).</summary>
    public bool CanReceiveMemory => Definition.Role is not (AgentRole.Judge or AgentRole.Auditor);

    /// <summary>The switch can be used now: the agent may get memory and the template can change.</summary>
    public bool CanEdit => CanReceiveMemory && _editor.CanEdit;

    /// <summary>The model can be changed now: the template can change (no run on screen, nothing in course).</summary>
    public bool CanChangeModel => _editor.CanEdit;

    /// <summary>The switch, as the user sets it; it goes back when the template could not be saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    private bool _receivesMemory;

    public string MemoryText => !CanReceiveMemory
        ? Texts.Get("Orchestrator_Roster_JudgeNoMemory")
        : ReceivesMemory ? Texts.Get("Orchestrator_Roster_MemoryOn") : Texts.Get("Orchestrator_Roster_MemoryOff");

    /// <summary>The models offered: the CLI's default, the names it is known to take, the one stored when it is another, and «Otro modelo…».</summary>
    public IReadOnlyList<ModelOption> ModelOptions { get; }

    /// <summary>The model chosen; choosing one saves it, and «Otro modelo…» opens the box to type it.</summary>
    [ObservableProperty]
    private ModelOption _selectedModel;

    /// <summary>The box to type a model is open («Otro modelo…»).</summary>
    [ObservableProperty]
    private bool _isTypingModel;

    /// <summary>The model typed in the box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypedModelProblem))]
    [NotifyCanExecuteChangedFor(nameof(UseTypedModelCommand))]
    private string _typedModel = string.Empty;

    /// <summary>Why the model typed cannot be used, while something is typed; null when it can.</summary>
    public string? TypedModelProblem => string.IsNullOrWhiteSpace(TypedModel) ? null : AgentModels.Problem(TypedModel.Trim());

    /// <summary>The model the agent runs on now, in words.</summary>
    public string ModelText => Texts.Format("Orchestrator_Roster_ModelText", AgentModels.Describe(Definition.Model.Model));

    /// <summary>The accessible name of the box of the model: «Modelo de &lt;agente&gt;».</summary>
    public string ModelAccessibleName => Texts.Format("Orchestrator_Prep_ModelName", Name);

    /// <summary>The accessible name of the box to type a model.</summary>
    public string TypedModelAccessibleName => Texts.Format("Orchestrator_Prep_TypedModelName", Name);

    /// <summary>The accessible name of the switch of the memory.</summary>
    public string MemoryAccessibleName => Texts.Format("Orchestrator_Prep_MemoryName", Name);

    partial void OnReceivesMemoryChanged(bool value)
    {
        if (!_restoring)
        {
            _editor.Switched(this, value);
        }
    }

    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_restoring || value is null)
        {
            return;
        }

        if (value.IsOther)
        {
            TypedModel = Definition.Model.Model ?? string.Empty;
            IsTypingModel = true;
            return;
        }

        IsTypingModel = false;
        if (!string.Equals(value.Model, Definition.Model.Model, StringComparison.Ordinal))
        {
            _editor.ModelChosen(this, value.Model);
        }
    }

    /// <summary>«Usar»: saves the model typed.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTypedModel))]
    private void UseTypedModel()
    {
        IsTypingModel = false;
        _editor.ModelChosen(this, TypedModel.Trim());
    }

    private bool CanUseTypedModel() =>
        CanChangeModel && AgentModels.Problem(TypedModel.Trim()) is null && !string.Equals(TypedModel.Trim(), Definition.Model.Model, StringComparison.Ordinal);

    /// <summary>«Cancelar»: closes the box and leaves the model as it is.</summary>
    [RelayCommand]
    private void CancelTypedModel()
    {
        IsTypingModel = false;
        RestoreModel();
    }

    /// <summary>Puts the switch back where the stored template has it, without saving anything.</summary>
    internal void Restore()
    {
        _restoring = true;
        try
        {
            ReceivesMemory = Definition.Memory != MemoryPolicy.None;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Puts the model chosen back to the one the stored template has, without saving anything.</summary>
    internal void RestoreModel()
    {
        _restoring = true;
        try
        {
            SelectedModel = OptionOf(Definition.Model.Model);
        }
        finally
        {
            _restoring = false;
        }
    }

    internal void RefreshCanEdit()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanChangeModel));
        UseTypedModelCommand.NotifyCanExecuteChanged();
    }

    private ModelOption OptionOf(string? model) =>
        ModelOptions.FirstOrDefault(o => !o.IsOther && string.Equals(o.Model, string.IsNullOrWhiteSpace(model) ? null : model, StringComparison.Ordinal))
        ?? ModelOptions[0];

    private static List<ModelOption> OptionsFor(AgentDefinition definition)
    {
        var options = new List<ModelOption> { new(AgentModels.DefaultLabel, null) };
        options.AddRange(AgentModels.Known(definition.Model.Provider).Select(name => new ModelOption(name, name)));
        if (definition.Model.Model is { Length: > 0 } stored && options.All(o => !string.Equals(o.Model, stored, StringComparison.Ordinal)))
        {
            options.Add(new ModelOption(stored, stored));
        }

        options.Add(new ModelOption(AgentModels.OtherLabel, null, IsOther: true));
        return options;
    }

    /// <summary>What a screen reader says of an agent of the template (V34): its name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The agents of the project's template as the preparation lets change them (docs/14 section 9, WP5; G1): the model of each
/// agent and its «Memoria del proyecto», each saved with the roster's <c>SaveAsync</c> (the domain checks the agent first, and
/// a model is checked with the rule of the CLI adapters). After a save the owner is told (the orchestrator tab withdraws the
/// approval of a plan that waits to run, since the roster hash changed, and reads the template again). Nothing changes while
/// <see cref="IsLocked"/> (the tab is busy or follows a run), nor without a roster store or a project.
/// </summary>
public sealed partial class RosterEditorViewModel : ObservableObject
{
    private readonly Func<ProjectContext, AgentDefinition, Task>? _save;
    private readonly Func<Task> _saved;
    private ProjectContext? _project;

    /// <param name="save">Saves one agent of the template of the project; null when there is no roster store.</param>
    /// <param name="saved">Awaited after a save (the owner withdraws a pending approval and reloads the template).</param>
    public RosterEditorViewModel(Func<ProjectContext, AgentDefinition, Task>? save, Func<Task> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        _save = save;
        _saved = saved;
    }

    /// <summary>The agents of the template, in its order.</summary>
    public ObservableCollection<AgentMemoryViewModel> Agents { get; } = [];

    /// <summary>The last save, completed once it and the reload after it are done.</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>The tab is busy or follows a run: nothing changes the template.</summary>
    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _notice;

    /// <summary>The template can change now.</summary>
    public bool CanEdit => !IsLocked && _save is not null && _project is not null;

    /// <summary>Shows the template of <paramref name="project"/> as it is stored.</summary>
    public void Load(ProjectContext? project, IReadOnlyList<AgentDefinition> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _project = project;
        Agents.Clear();
        foreach (var agent in roster)
        {
            Agents.Add(new AgentMemoryViewModel(agent, this));
        }

        OnPropertyChanged(nameof(CanEdit));
    }

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        foreach (var agent in Agents)
        {
            agent.RefreshCanEdit();
        }
    }

    internal void Switched(AgentMemoryViewModel agent, bool receives) => Saving = SaveMemoryAsync(agent, receives);

    internal void ModelChosen(AgentMemoryViewModel agent, string? model) => Saving = SaveModelAsync(agent, model);

    private Task SaveMemoryAsync(AgentMemoryViewModel agent, bool receives) => SaveAsync(
        agent,
        agent.CanEdit,
        agent.Definition with { Memory = receives ? MemoryPolicy.ReadSelected : MemoryPolicy.None },
        agent.Restore,
        receives
            ? Texts.Format("Orchestrator_Roster_MemoryNowOn", agent.Name)
            : Texts.Format("Orchestrator_Roster_MemoryNowOff", agent.Name));

    private Task SaveModelAsync(AgentMemoryViewModel agent, string? model)
    {
        if (model is not null && AgentModels.Problem(model) is { } problem)
        {
            agent.RestoreModel();
            ErrorMessage = problem;
            Notice = null;
            return Task.CompletedTask;
        }

        return SaveAsync(
            agent,
            agent.CanChangeModel,
            agent.Definition with { Model = agent.Definition.Model with { Model = model } },
            agent.RestoreModel,
            Texts.Format("Orchestrator_Roster_ModelChanged", agent.Name, AgentModels.Describe(model)));
    }

    private async Task SaveAsync(AgentMemoryViewModel agent, bool allowed, AgentDefinition updated, Action restore, string notice)
    {
        ErrorMessage = null;
        Notice = null;
        if (!allowed || _save is not { } save || _project is not { } project)
        {
            restore();
            return;
        }

        if (updated.Validate() is { Count: > 0 } problems)
        {
            restore();
            ErrorMessage = Texts.Format("Orchestrator_Roster_ChangeRefused", string.Join(", ", problems));
            return;
        }

        try
        {
            await save(project, updated).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The roster is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            restore();
            ErrorMessage = Texts.Format("Orchestrator_Roster_SaveFailed", ex.Message);
            return;
        }

        await _saved().ConfigureAwait(true);
        Notice = notice;
    }
}

/// <summary>The lines of the template of agents, as the preparation shows them.</summary>
public static class RosterText
{
    /// <summary>One agent in a line: id, name, CLI and vendor, model if it sets one, and tools.</summary>
    public static string Line(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var model = string.IsNullOrWhiteSpace(agent.Model.Model) ? string.Empty : $" · {Texts.Format("Orchestrator_Roster_LineModel", agent.Model.Model)}";
        return $"{agent.Id} · {agent.Name} · {agent.Model.Provider} ({agent.Model.Vendor}){model} · {ToolsOf(agent)}";
    }

    /// <summary>Its CLI and vendor and its tools: what the card of an agent says under its name.</summary>
    public static string Tools(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return $"{agent.Model.Provider} ({agent.Model.Vendor}) · {ToolsOf(agent)}";
    }

    private static string ToolsOf(AgentDefinition agent) =>
        agent.AllowedTools.Count > 0
            ? string.Join(", ", agent.AllowedTools)
            : agent.Role is AgentRole.Judge or AgentRole.Auditor ? Texts.Get("Orchestrator_Roster_ToolsReadOnly") : Texts.Get("Orchestrator_Roster_ToolsNone");
}
