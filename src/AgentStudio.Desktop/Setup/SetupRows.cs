using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Providers.Cli;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Setup;

/// <summary>One step of the configurator in the list of steps: its title, whether it is the current one and whether it is applied or skipped.</summary>
public sealed partial class SetupStepViewModel : ObservableObject
{
    internal SetupStepViewModel(SetupStep step, string title, bool canSkip)
    {
        Step = step;
        Title = title;
        CanSkip = canSkip;
        _isIncluded = canSkip;
    }

    public SetupStep Step { get; }

    public string Title { get; }

    /// <summary>The steps from «Ficha» to «Presupuesto» can be skipped; «Proyecto» only informs and the summary applies.</summary>
    public bool CanSkip { get; }

    /// <summary>The step is the one on screen.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>The step goes into «Aplicar»; off, it is skipped and nothing of it is written.</summary>
    [ObservableProperty]
    private bool _isIncluded;

    /// <summary>What a screen reader says of the step: its title.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// One block of the summary: a step with what it will be after «Aplicar», in words, whether it is saved or stays as it is, and,
/// when it is saved, what changes with what is there now. «Cambiar» goes back to the step.
/// </summary>
/// <param name="Lines">What the step will be, one thing per line.</param>
/// <param name="Changes">What changes, each with what is there now: «antes → después». Empty when the step stays as it is.</param>
/// <param name="ChangeName">What a screen reader says of «Cambiar»: «Cambiar Modelos».</param>
public sealed record SetupSummaryBlock(SetupStep Step, string Title, bool Saves, string State, IReadOnlyList<string> Lines, IReadOnlyList<string> Changes, string ChangeName)
{
    public bool HasChanges => Changes.Count > 0;

    /// <summary>What a screen reader says of the block: the step and whether it is saved.</summary>
    public override string ToString() => $"{Title}: {State}";
}

/// <summary>How many agents a program runs at once at most, whatever the limits of the project say (the adapters fix it).</summary>
public sealed record SetupProviderLimit(ProviderId Provider, int Limit);

/// <summary>What the configurator asks of the shell when the work can start: the line to show once it is there, or null.</summary>
public sealed class SetupWorkRequestedEventArgs(string? confirmation) : EventArgs
{
    /// <summary>«Guardado…» after a successful «Aplicar»; null for «Ir a trabajar».</summary>
    public string? Confirmation { get; } = confirmation;
}

/// <summary>A size of the template of agents the person can choose in step 3.</summary>
public sealed record SetupSizeChoice(SetupSize Size, string Label)
{
    /// <summary>What a screen reader says of the choice: its label.</summary>
    public override string ToString() => Label;
}

/// <summary>One agent of the template of step 3: what it is and whether it is added, changed or as it was.</summary>
public sealed class SetupAgentRowViewModel
{
    internal SetupAgentRowViewModel(SetupAgentChange change, string name, string line, string kind)
    {
        Change = change;
        Name = name;
        Line = line;
        Kind = kind;
    }

    internal SetupAgentChange Change { get; }

    public string Name { get; }

    /// <summary>Its role and its program, in a line.</summary>
    public string Line { get; }

    /// <summary>«Se añade», «Cambia» or «Sin cambios».</summary>
    public string Kind { get; }

    /// <summary>What a screen reader says of the row: the agent and what happens to it.</summary>
    public override string ToString() => $"{Name}: {Kind}";
}

/// <summary>One model the person can pick in step 4: the name the program takes, and that name with its level for the list.</summary>
public sealed record SetupModelOption(string Model, string Label)
{
    /// <summary>What a screen reader says of the option: its label.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// One agent of step 4: the model and the effort it will use, which the person can change right here, with the recommendation
/// and its reason next to them. The lists are the ones the orchestrator offers (<see cref="AgentModels.Names"/> and
/// <see cref="AgentModels.Efforts"/>); any other name can be typed and is checked as the program adapters check it before they
/// launch. The program of the agent stays as it is. Nothing is written until «Aplicar».
/// </summary>
public sealed partial class SetupModelRowViewModel : ObservableObject
{
    private readonly ModelCatalog? _catalog;
    private readonly IUiTextProvider _text;
    private readonly string _originalModel;
    private readonly ReasoningEffort? _originalEffort;

    internal SetupModelRowViewModel(SetupModelProposal proposal, ModelCatalog? catalog, IUiTextProvider text)
    {
        Proposal = proposal;
        _catalog = catalog;
        _text = text;
        var agent = proposal.Agent;
        Name = agent.Name;
        Program = SetupTexts.Provider(agent.Model.Provider);
        Current = text.Format("Setup_Models_Now", SetupTexts.Power(text, agent.Model.Model, agent.Effort));
        if (proposal.Recommendation is { } recommendation)
        {
            Recommended = text.Format("Setup_Models_Recommended", recommendation.Model, SetupTexts.Tier(text, recommendation.Tier), SetupTexts.Effort(text, recommendation.Effort));
            Why = proposal.Reason is { } reason ? text.Format("Setup_Models_Why", text.Render(reason)) : null;
        }
        else
        {
            Why = text.Get("Setup_Models_None");
        }

        Models = [.. AgentModels.Names(catalog, agent.Model.Provider).Select(n => new SetupModelOption(n, OptionLabel(n)))];
        _originalModel = Normalize(agent.Model.Model);
        _originalEffort = agent.Effort;
        _modelText = _originalModel;
        _efforts = BuildEfforts(_originalModel, agent.Effort);
        _selectedEffort = _efforts.First(e => e.Effort == agent.Effort);
    }

    /// <summary>Raised when the person changes the model or the effort.</summary>
    public event EventHandler? Changed;

    internal SetupModelProposal Proposal { get; }

    public string Name { get; }

    /// <summary>The program of the agent (Claude Code, Codex): it does not change here.</summary>
    public string Program { get; }

    /// <summary>«Programa: Claude Code».</summary>
    public string ProgramLine => _text.Format("Setup_Models_Program", Program);

    /// <summary>The model and the effort the agent has now, in words.</summary>
    public string Current { get; }

    /// <summary>The recommendation in words; null when the catalog has no model for its program.</summary>
    public string? Recommended { get; }

    /// <summary>Why, in one sentence; without recommendation, why there is none.</summary>
    public string? Why { get; }

    /// <summary>The models of the list; any other can be typed.</summary>
    public IReadOnlyList<SetupModelOption> Models { get; }

    /// <summary>What a screen reader says of the model box: «Modelo de Ejecutor».</summary>
    public string ModelName => _text.Format("Setup_Models_Model_Name", Name);

    /// <summary>What a screen reader says of the effort box.</summary>
    public string EffortName => _text.Format("Setup_Models_Effort_Name", Name);

    /// <summary>What a screen reader says of «Usar la recomendada».</summary>
    public string UseName => _text.Format("Setup_Models_Use_Name", Name);

    /// <summary>The model as the person leaves it; empty is the program's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid), nameof(IsEdited), nameof(Follows), nameof(FollowsText), nameof(CanUseRecommendation))]
    [NotifyCanExecuteChangedFor(nameof(UseRecommendedCommand))]
    private string _modelText;

    /// <summary>The efforts the model takes, with the program's own first.</summary>
    [ObservableProperty]
    private IReadOnlyList<EffortOption> _efforts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited), nameof(Follows), nameof(FollowsText), nameof(CanUseRecommendation))]
    [NotifyCanExecuteChangedFor(nameof(UseRecommendedCommand))]
    private EffortOption? _selectedEffort;

    /// <summary>Why the typed model cannot be given to the program, or empty.</summary>
    public string Problem => ProblemOf(ModelText);

    public bool IsValid => Problem.Length == 0;

    /// <summary>The person changed the model or the effort from what the agent has.</summary>
    public bool IsEdited => !string.Equals(Normalize(ModelText), _originalModel, StringComparison.Ordinal) || SelectedEffort?.Effort != _originalEffort;

    /// <summary>The agent as it would be stored with what the row says.</summary>
    public AgentDefinition Edited => Proposal.Agent with
    {
        Model = Proposal.Agent.Model with { Model = Normalize(ModelText) is { Length: > 0 } model ? model : null },
        Effort = SelectedEffort?.Effort,
    };

    /// <summary>What the row says is the recommendation (the same model by any of its names, and the same effort).</summary>
    public bool Follows => Proposal.Recommendation is { } recommendation && _catalog is not null && IsValid && ModelRecommender.IsFollowedBy(_catalog, recommendation, Edited);

    /// <summary>«Usa la recomendada» when it does, else empty.</summary>
    public string FollowsText => Follows ? _text.Get("Setup_Models_Follows") : string.Empty;

    /// <summary>There is a recommendation and the row does not say it yet.</summary>
    public bool CanUseRecommendation => Proposal.Recommendation is not null && !Follows;

    /// <summary>«Usar la recomendada»: the model and the effort of the recommendation, in this row.</summary>
    [RelayCommand(CanExecute = nameof(CanUseRecommendation))]
    private void UseRecommended()
    {
        if (Proposal.Recommendation is not { } recommendation)
        {
            return;
        }

        ModelText = recommendation.Model;
        SelectedEffort = Efforts.FirstOrDefault(e => e.Effort == recommendation.Effort) ?? SelectedEffort;
    }

    partial void OnModelTextChanged(string value)
    {
        // The effort stays when the new model takes it; otherwise the nearest one it takes, as the inspector of the agent does.
        var wanted = SelectedEffort?.Effort;
        if (wanted is { } effort && _catalog?.Find(Proposal.Agent.Model.Provider, Normalize(value)) is { } entry && !entry.Accepts(effort))
        {
            wanted = ModelRecommender.NearestEffort(entry, effort);
        }

        var efforts = BuildEfforts(Normalize(value), wanted);
        Efforts = efforts;
        SelectedEffort = efforts.First(e => e.Effort == wanted);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedEffortChanged(EffortOption? value)
    {
        // A list that is replaced can clear the selection through the binding for a moment: that is not the person's choice.
        if (value is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private IReadOnlyList<EffortOption> BuildEfforts(string model, ReasoningEffort? keep)
    {
        var offered = AgentModels.Efforts(_catalog, Proposal.Agent.Model.Provider, model).ToList();
        if (keep is { } effort && !offered.Contains(effort))
        {
            offered.Add(effort);
        }

        return [new EffortOption(SetupTexts.Effort(_text, null), null), .. offered.Select(e => new EffortOption(SetupTexts.Effort(_text, e), e))];
    }

    private string OptionLabel(string model) =>
        _catalog?.Find(Proposal.Agent.Model.Provider, model) is { } entry ? _text.Format("Setup_Models_Option", model, SetupTexts.Tier(_text, entry.Tier)) : model;

    private string ProblemOf(string? model)
    {
        if (Normalize(model) is not { Length: > 0 } name)
        {
            return string.Empty;
        }

        try
        {
            CliArgumentGuard.Model(name, nameof(model));
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return _text.Get("Setup_Models_Bad");
        }
    }

    private static string Normalize(string? model) => model?.Trim() ?? string.Empty;

    /// <summary>What a screen reader says of the row: the agent.</summary>
    public override string ToString() => Name;
}

/// <summary>One skill proposed to one agent in step 6, with its reasons; the person ticks the ones to activate.</summary>
public sealed partial class SetupSkillRowViewModel : ObservableObject
{
    internal SetupSkillRowViewModel(SetupSkillSuggestion suggestion, string agentName, string reasons, bool alreadyActive)
    {
        Suggestion = suggestion;
        AgentName = agentName;
        Reasons = reasons;
        AlreadyActive = alreadyActive;
        _isChosen = alreadyActive;
    }

    internal SetupSkillSuggestion Suggestion { get; }

    public string AgentName { get; }

    public string SkillName => Suggestion.Name;

    /// <summary>The reasons of the recommender, as it wrote them.</summary>
    public string Reasons { get; }

    /// <summary>The skill is active for the agent already: nothing to decide.</summary>
    public bool AlreadyActive { get; }

    /// <summary>The person can still decide: what is active already is not undone from here.</summary>
    public bool CanChoose => !AlreadyActive;

    /// <summary>The person decides to activate it for the agent.</summary>
    [ObservableProperty]
    private bool _isChosen;

    internal AgentId Agent => Suggestion.Agent;

    internal SkillId Skill => Suggestion.Skill;

    /// <summary>What a screen reader says of the row: the skill and the agent.</summary>
    public override string ToString() => $"{SkillName}: {AgentName}";
}
