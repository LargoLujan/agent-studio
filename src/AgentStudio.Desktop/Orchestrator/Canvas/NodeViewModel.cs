using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// A task of the plan drawn on the canvas: one <see cref="WorkTaskKind"/> with its connectors and every field of its
/// <see cref="TaskSpec"/>. The dependencies are not stored here: they are the wires that end on <see cref="Inputs"/>.
/// </summary>
public sealed partial class NodeViewModel : ObservableObject
{
    /// <summary>Properties that are part of the plan: changing one changes the plan (and voids an approval).</summary>
    private static readonly HashSet<string> SpecProperties =
    [
        nameof(Title), nameof(Agent), nameof(Objective), nameof(Instructions), nameof(Join), nameof(Covers),
        nameof(WriteScope), nameof(VerificationSteps), nameof(Condition), nameof(ToolId), nameof(MaxRetries),
        nameof(Timeout), nameof(Workspace), nameof(ExtraDependencies),
    ];

    public NodeViewModel(TaskId id, WorkTaskKind kind, string title, Point location)
    {
        ArgumentNullException.ThrowIfNull(title);
        Id = id;
        Kind = kind;
        _title = title;
        _location = location;
        _workspace = NodeKinds.DefaultWorkspace(kind);
        _maxRetries = NodeKinds.DefaultRetries(kind);

        Inputs = [new ConnectorViewModel(this, ConnectorFlow.Input, "Entrada")];
        Outputs = kind == WorkTaskKind.Condition
            ?
            [
                new ConnectorViewModel(this, ConnectorFlow.Output, "Sí", ConditionBranches.Yes),
                new ConnectorViewModel(this, ConnectorFlow.Output, "No", ConditionBranches.No),
            ]
            : [new ConnectorViewModel(this, ConnectorFlow.Output, "Salida")];
        Problems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(ProblemSummary));
            OnPropertyChanged(nameof(AutomationName));
        };
    }

    /// <summary>Raised when a field that belongs to the plan changes (not for the position or the status).</summary>
    public event EventHandler? SpecChanged;

    public TaskId Id { get; }

    public WorkTaskKind Kind { get; }

    public string KindLabel => NodeKinds.Label(Kind);

    /// <summary>True for a kind the plan validator rejects (Debate until Phase 5): shown dimmed and never connected.</summary>
    public bool IsDisabled => !NodeKinds.IsAvailable(Kind);

    public IReadOnlyList<ConnectorViewModel> Inputs { get; }

    public IReadOnlyList<ConnectorViewModel> Outputs { get; }

    /// <summary>Top-left corner in graph coordinates; Nodify writes it back when the node is dragged.</summary>
    [ObservableProperty]
    private Point _location;

    /// <summary>
    /// The size the view measured for the node (Nodify writes it); empty until then. A new node is placed and «Ajustar vista»
    /// frames the nodes with it. Not part of the plan.
    /// </summary>
    [ObservableProperty]
    private Size _size;

    /// <summary>Status of the task in the run being followed; Pending while the plan is being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private WorkTaskStatus _status = WorkTaskStatus.Pending;

    public string StatusLabel => IsDisabled ? "Aplazado" : TaskStatusText.Label(Status);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title;

    [ObservableProperty]
    private AgentId? _agent;

    [ObservableProperty]
    private string _objective = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private JoinPolicy _join = JoinPolicy.AllCompleted;

    [ObservableProperty]
    private IReadOnlyList<RequirementId> _covers = [];

    /// <summary>Relative folders (ending in /) and files the task may change.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _writeScope = [];

    /// <summary>Ids of steps of the trusted verification profile, in the order they run.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _verificationSteps = [];

    [ObservableProperty]
    private ConditionPredicate? _condition;

    [ObservableProperty]
    private string? _toolId;

    [ObservableProperty]
    private int _maxRetries;

    [ObservableProperty]
    private TimeSpan? _timeout;

    [ObservableProperty]
    private WorkspaceMode _workspace;

    /// <summary>
    /// Dependencies of a loaded plan that no wire can draw: an unknown task, or a branch that does not match the
    /// connectors of the prerequisite. They stay in the plan, so the validator reports them, until the user removes them.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<DependencySpec> _extraDependencies = [];

    /// <summary>The validator's messages about this task, in Spanish; filled only after «Validar».</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public string ProblemSummary => Problems.Count == 1 ? "1 problema" : $"{Problems.Count} problemas";

    /// <summary>What a screen reader announces for the node (Nodify ships no automation peers).</summary>
    public string AutomationName => HasProblems
        ? $"{Id}, {KindLabel}, {Title}, {StatusLabel}, {ProblemSummary}"
        : $"{Id}, {KindLabel}, {Title}, {StatusLabel}";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && SpecProperties.Contains(name))
        {
            SpecChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
