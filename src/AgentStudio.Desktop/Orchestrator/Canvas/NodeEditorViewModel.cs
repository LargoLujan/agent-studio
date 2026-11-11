using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>What the properties panel can offer: the project's agents, frozen requirements and verification steps.</summary>
public sealed record EditorOptions(
    IReadOnlyList<AgentDefinition> Roster,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<(string Id, string Title)> Steps)
{
    public static EditorOptions None { get; } = new([], [], []);
}

/// <summary>One choice of a list (agent, workspace, join policy, tool, predicate kind or task).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    /// <summary>What a screen reader says of the choice (A1-bis): its label, never the members of the record.</summary>
    public override string ToString() => Label;
}

/// <summary>One entry of a check list (a requirement the task covers, a verification step it runs).</summary>
public sealed partial class CheckOption : ObservableObject
{
    private readonly Action _changed;

    public CheckOption(string value, string label, bool isChecked, bool isKnown, Action changed)
    {
        Value = value;
        Label = label;
        IsKnown = isKnown;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Value { get; }

    public string Label { get; }

    /// <summary>False for an entry of a loaded plan that the project does not have: kept, but marked.</summary>
    public bool IsKnown { get; }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();

    /// <summary>What a screen reader says of an entry of a check list (V34): its label.</summary>
    public override string ToString() => Label;
}

public enum PredicateKind
{
    None,
    TaskSucceeded,
    TestsPassed,
    OutputEquals,
    PathsChanged,
}

/// <summary>
/// The properties panel of one node: every field of its <see cref="TaskSpec"/> that the canvas does not draw. It edits
/// the node directly; the lists come from <see cref="EditorOptions"/>, plus whatever a loaded plan names that the
/// project does not have (kept and marked, so the validator can report it).
/// </summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    private readonly bool _initialized;

    public NodeEditorViewModel(NodeViewModel node, EditorOptions options, IReadOnlyList<NodeViewModel> nodes, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nodes);
        Node = node;
        IsReadOnly = isReadOnly;

        AgentOptions = BuildAgentOptions(node, options.Roster);
        _selectedAgent = AgentOptions.First(o => o.Value == node.Agent);
        WorkspaceOptions =
        [
            new(WorkspaceMode.None, Texts.Get("Orchestrator_Node_WorkspaceNone")),
            new(WorkspaceMode.ReadOnly, Texts.Get("Orchestrator_Node_WorkspaceReadOnly")),
            new(WorkspaceMode.Write, Texts.Get("Orchestrator_Node_WorkspaceWrite")),
        ];
        _selectedWorkspace = WorkspaceOptions.First(o => o.Value == node.Workspace);
        JoinOptions =
        [
            new(JoinPolicy.AllCompleted, Texts.Get("Orchestrator_Node_JoinAll")),
            new(JoinPolicy.AnyCompleted, Texts.Get("Orchestrator_Node_JoinAny")),
        ];
        _selectedJoin = JoinOptions.First(o => o.Value == node.Join);
        ToolOptions = BuildToolOptions(node, options.Steps);
        _selectedTool = ToolOptions.First(o => o.Value == node.ToolId);

        foreach (var requirement in options.Requirements)
        {
            var priority = requirement.Priority == RequirementPriority.Mandatory ? Texts.Get("Orchestrator_Node_Mandatory") : Texts.Get("Orchestrator_Node_Desirable");
            CoverOptions.Add(new CheckOption(requirement.Id.Value, $"{requirement.Id} · {requirement.Statement} ({priority})", node.Covers.Contains(requirement.Id), isKnown: true, ApplyCovers));
        }

        foreach (var unknown in node.Covers.Where(r => options.Requirements.All(q => q.Id != r)))
        {
            CoverOptions.Add(new CheckOption(unknown.Value, Texts.Format("Orchestrator_Node_CoverUnknown", unknown), isChecked: true, isKnown: false, ApplyCovers));
        }

        foreach (var (id, title) in options.Steps)
        {
            StepOptions.Add(new CheckOption(id, $"{id} · {title}", node.VerificationSteps.Contains(id), isKnown: true, ApplySteps));
        }

        foreach (var unknown in node.VerificationSteps.Where(s => options.Steps.All(p => p.Id != s)))
        {
            StepOptions.Add(new CheckOption(unknown, Texts.Format("Orchestrator_Node_StepUnknown", unknown), isChecked: true, isKnown: false, ApplySteps));
        }

        PredicateKindOptions =
        [
            new(PredicateKind.None, Texts.Get("Orchestrator_Node_PredicateNone")),
            new(PredicateKind.TaskSucceeded, Texts.Get("Orchestrator_Node_PredicateSucceeded")),
            new(PredicateKind.TestsPassed, Texts.Get("Orchestrator_Node_PredicateTests")),
            new(PredicateKind.OutputEquals, Texts.Get("Orchestrator_Node_PredicateOutput")),
            new(PredicateKind.PathsChanged, Texts.Get("Orchestrator_Node_PredicatePaths")),
        ];
        PredicateTaskOptions = [.. nodes.Where(n => !ReferenceEquals(n, node)).Select(n => new Choice<TaskId?>(n.Id, $"{n.Id} · {n.Title}"))];
        LoadPredicate(node.Condition);

        _writeScopeText = string.Join(Environment.NewLine, node.WriteScope);
        _maxRetriesText = node.MaxRetries.ToString(CultureInfo.InvariantCulture);
        _timeoutMinutesText = node.Timeout is { } timeout ? timeout.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _initialized = true;
    }

    public NodeViewModel Node { get; }

    public bool IsReadOnly { get; }

    public bool IsEditable => !IsReadOnly;

    public string Header => $"{Node.Id} · {Node.KindLabel}";

    public bool ShowsAgent => Node.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review || Node.Agent is not null;

    public bool ShowsWorkspace => Node.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review or WorkTaskKind.ToolRun or WorkTaskKind.Integration
        || Node.Workspace != WorkspaceMode.None;

    public bool ShowsWriteScope => Node.Workspace == WorkspaceMode.Write || Node.WriteScope.Count > 0;

    public bool ShowsSteps => Node.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Integration || Node.VerificationSteps.Count > 0;

    public bool ShowsTool => Node.Kind == WorkTaskKind.ToolRun || Node.ToolId is not null;

    public bool ShowsCondition => Node.Kind == WorkTaskKind.Condition || Node.Condition is not null;

    public string Title
    {
        get => Node.Title;
        set
        {
            if (!string.Equals(Node.Title, value, StringComparison.Ordinal))
            {
                Node.Title = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string Objective
    {
        get => Node.Objective;
        set
        {
            if (!string.Equals(Node.Objective, value, StringComparison.Ordinal))
            {
                Node.Objective = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string Instructions
    {
        get => Node.Instructions;
        set
        {
            if (!string.Equals(Node.Instructions, value, StringComparison.Ordinal))
            {
                Node.Instructions = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<Choice<AgentId?>> AgentOptions { get; }

    [ObservableProperty]
    private Choice<AgentId?> _selectedAgent;

    // A combo box being torn down (the selection moved to another node) may push null: that is never a choice.
    partial void OnSelectedAgentChanged(Choice<AgentId?> value)
    {
        if (value is not null)
        {
            Node.Agent = value.Value;
        }
    }

    public IReadOnlyList<Choice<WorkspaceMode>> WorkspaceOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWriteScope))]
    private Choice<WorkspaceMode> _selectedWorkspace;

    partial void OnSelectedWorkspaceChanged(Choice<WorkspaceMode> value)
    {
        if (value is not null)
        {
            Node.Workspace = value.Value;
        }
    }

    public IReadOnlyList<Choice<JoinPolicy>> JoinOptions { get; }

    [ObservableProperty]
    private Choice<JoinPolicy> _selectedJoin;

    partial void OnSelectedJoinChanged(Choice<JoinPolicy> value)
    {
        if (value is not null)
        {
            Node.Join = value.Value;
        }
    }

    public IReadOnlyList<Choice<string?>> ToolOptions { get; }

    [ObservableProperty]
    private Choice<string?> _selectedTool;

    partial void OnSelectedToolChanged(Choice<string?> value)
    {
        if (value is not null)
        {
            Node.ToolId = value.Value;
        }
    }

    public ObservableCollection<CheckOption> CoverOptions { get; } = [];

    public ObservableCollection<CheckOption> StepOptions { get; } = [];

    /// <summary>One entry per line: a folder ends in / (everything below it), anything else is one file.</summary>
    [ObservableProperty]
    private string _writeScopeText;

    partial void OnWriteScopeTextChanged(string value) =>
        Node.WriteScope = [.. (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetriesError))]
    private string _maxRetriesText;

    public string? RetriesError => int.TryParse(MaxRetriesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries) && retries >= 0
        ? null
        : Texts.Get("Orchestrator_Node_RetriesError");

    partial void OnMaxRetriesTextChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries) && retries >= 0)
        {
            Node.MaxRetries = retries;
        }
    }

    /// <summary>Minutes; empty means the engine's default timeout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeoutError))]
    private string _timeoutMinutesText;

    public string? TimeoutError => TryParseTimeout(TimeoutMinutesText, out _) ? null : Texts.Get("Orchestrator_Node_TimeoutError");

    partial void OnTimeoutMinutesTextChanged(string value)
    {
        if (TryParseTimeout(value, out var timeout))
        {
            Node.Timeout = timeout;
        }
    }

    public IReadOnlyList<Choice<PredicateKind>> PredicateKindOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PredicateNeedsKeyAndValue))]
    [NotifyPropertyChangedFor(nameof(PredicateNeedsPrefix))]
    [NotifyPropertyChangedFor(nameof(PredicateNeedsTask))]
    private Choice<PredicateKind> _selectedPredicateKind = null!;

    public bool PredicateNeedsTask => SelectedPredicateKind?.Value is not (null or PredicateKind.None);

    public bool PredicateNeedsKeyAndValue => SelectedPredicateKind?.Value == PredicateKind.OutputEquals;

    public bool PredicateNeedsPrefix => SelectedPredicateKind?.Value == PredicateKind.PathsChanged;

    public IReadOnlyList<Choice<TaskId?>> PredicateTaskOptions { get; private set; }

    [ObservableProperty]
    private Choice<TaskId?>? _selectedPredicateTask;

    [ObservableProperty]
    private string _predicateKey = string.Empty;

    [ObservableProperty]
    private string _predicateValue = string.Empty;

    [ObservableProperty]
    private string _predicatePrefix = string.Empty;

    partial void OnSelectedPredicateKindChanged(Choice<PredicateKind> value)
    {
        if (value is not null)
        {
            ApplyPredicate();
        }
    }

    partial void OnSelectedPredicateTaskChanged(Choice<TaskId?>? value)
    {
        if (value is not null)
        {
            ApplyPredicate();
        }
    }

    partial void OnPredicateKeyChanged(string value) => ApplyPredicate();

    partial void OnPredicateValueChanged(string value) => ApplyPredicate();

    partial void OnPredicatePrefixChanged(string value) => ApplyPredicate();

    /// <summary>Dependencies of a loaded plan that no wire can draw (an unknown task or a wrong branch).</summary>
    public IReadOnlyList<string> ExtraDependencyTexts => [.. Node.ExtraDependencies.Select(d => d.Branch is null ? d.Prerequisite.Value : Texts.Format("Orchestrator_Node_ExtraDependencyBranch", d.Prerequisite, d.Branch))];

    public bool HasExtraDependencies => Node.ExtraDependencies.Count > 0;

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void RemoveExtraDependencies()
    {
        Node.ExtraDependencies = [];
        OnPropertyChanged(nameof(ExtraDependencyTexts));
        OnPropertyChanged(nameof(HasExtraDependencies));
    }

    private static List<Choice<AgentId?>> BuildAgentOptions(NodeViewModel node, IReadOnlyList<AgentDefinition> roster)
    {
        var options = new List<Choice<AgentId?>> { new(null, Texts.Get("Orchestrator_Node_NoAgent")) };
        options.AddRange(roster.Select(a => new Choice<AgentId?>(a.Id, $"{a.Id} · {RoleLabel(a.Role)} · {a.Model.Provider}")));
        if (node.Agent is { } current && roster.All(a => a.Id != current))
        {
            options.Add(new Choice<AgentId?>(current, Texts.Format("Orchestrator_Node_AgentMissing", current)));
        }

        return options;
    }

    private static List<Choice<string?>> BuildToolOptions(NodeViewModel node, IReadOnlyList<(string Id, string Title)> steps)
    {
        var options = new List<Choice<string?>> { new(null, Texts.Get("Orchestrator_Node_NoTool")) };
        options.AddRange(steps.Select(s => new Choice<string?>(s.Id, $"{s.Id} · {s.Title}")));
        if (node.ToolId is { } current && steps.All(s => s.Id != current))
        {
            options.Add(new Choice<string?>(current, Texts.Format("Orchestrator_Node_StepUnknown", current)));
        }

        return options;
    }

    public static string RoleLabel(AgentRole role) => role switch
    {
        AgentRole.Orchestrator or AgentRole.Executor or AgentRole.Architect or AgentRole.Tester
            or AgentRole.Documenter or AgentRole.Critic or AgentRole.Judge or AgentRole.Auditor => Texts.Get("Canvas_Role_" + role),
        _ => role.ToString(),
    };

    private static bool TryParseTimeout(string? text, out TimeSpan? timeout)
    {
        timeout = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            && double.IsFinite(minutes) && minutes > 0 && minutes <= TimeSpan.MaxValue.TotalMinutes)
        {
            timeout = TimeSpan.FromMinutes(minutes);
            return true;
        }

        return false;
    }

    private void LoadPredicate(ConditionPredicate? predicate)
    {
        var (kind, task) = predicate switch
        {
            TaskSucceeded p => (PredicateKind.TaskSucceeded, (TaskId?)p.Task),
            TestsPassed p => (PredicateKind.TestsPassed, p.Task),
            OutputEquals p => (PredicateKind.OutputEquals, p.Task),
            PathsChanged p => (PredicateKind.PathsChanged, p.Task),
            _ => (PredicateKind.None, (TaskId?)null),
        };

        // Called before _initialized is set, so these setters do not rebuild the node's predicate.
        SelectedPredicateKind = PredicateKindOptions.First(o => o.Value == kind);
        var selected = task is null ? null : PredicateTaskOptions.FirstOrDefault(o => o.Value == task);
        if (task is not null && selected is null)
        {
            // The predicate reads a task that is not on the canvas: keep it visible so the user sees what it reads.
            selected = new Choice<TaskId?>(task, Texts.Format("Orchestrator_Node_TaskMissing", task));
            PredicateTaskOptions = [.. PredicateTaskOptions, selected];
        }

        SelectedPredicateTask = selected;
        if (predicate is OutputEquals output)
        {
            PredicateKey = output.Key;
            PredicateValue = output.Value;
        }

        if (predicate is PathsChanged paths)
        {
            PredicatePrefix = paths.PathPrefix;
        }
    }

    /// <summary>The predicate exists once its kind and its task are chosen; until then the node has none.</summary>
    private void ApplyPredicate()
    {
        if (!_initialized)
        {
            return;
        }

        Node.Condition = (SelectedPredicateKind?.Value, SelectedPredicateTask?.Value) switch
        {
            (PredicateKind.TaskSucceeded, { } task) => new TaskSucceeded(task),
            (PredicateKind.TestsPassed, { } task) => new TestsPassed(task),
            (PredicateKind.OutputEquals, { } task) when !string.IsNullOrWhiteSpace(PredicateKey) => new OutputEquals(task, PredicateKey.Trim(), PredicateValue ?? string.Empty),
            (PredicateKind.PathsChanged, { } task) => new PathsChanged(task, (PredicatePrefix ?? string.Empty).Trim()),
            _ => null,
        };
    }

    private void ApplyCovers()
    {
        if (_initialized)
        {
            Node.Covers = [.. CoverOptions.Where(o => o.IsChecked).Select(o => new RequirementId(o.Value))];
        }
    }

    private void ApplySteps()
    {
        if (_initialized)
        {
            Node.VerificationSteps = [.. StepOptions.Where(o => o.IsChecked).Select(o => o.Value)];
        }
    }
}
