using AgentStudio.Application.Setup;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;

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

/// <summary>
/// One agent of step 4: the model and the effort it has, the recommendation and its reason. Accepting it puts the recommendation
/// into the template to apply; nothing is chosen until the person does.
/// </summary>
public sealed partial class SetupModelRowViewModel : ObservableObject
{
    internal SetupModelRowViewModel(SetupModelProposal proposal, string name, string current, string? recommended, string? why, string follows)
    {
        Proposal = proposal;
        Name = name;
        Current = current;
        Recommended = recommended;
        Why = why;
        Follows = follows;
        CanAccept = proposal.Recommendation is not null && !proposal.Follows;
    }

    internal SetupModelProposal Proposal { get; }

    public string Name { get; }

    /// <summary>The model and the effort it has now, in words.</summary>
    public string Current { get; }

    /// <summary>The recommendation in words; null when the catalog has no model for its program.</summary>
    public string? Recommended { get; }

    /// <summary>Why, in one sentence; null without recommendation.</summary>
    public string? Why { get; }

    /// <summary>«Ya usa lo recomendado» when it does, else empty.</summary>
    public string Follows { get; }

    /// <summary>There is a recommendation and the agent does not follow it yet.</summary>
    public bool CanAccept { get; }

    /// <summary>The person takes the recommendation for this agent.</summary>
    [ObservableProperty]
    private bool _isAccepted;

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
