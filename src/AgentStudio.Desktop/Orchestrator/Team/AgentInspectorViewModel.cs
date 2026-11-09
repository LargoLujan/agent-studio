using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Models;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// What the inspector knows of the project to advise a model (docs/17 D-22 to D-24): the catalog of WP2, what the verified log
/// taught of each model, and the facts the recommendation reads (requirements, files, stack, budget, and whether the plans of
/// the project have tasks that only touch documentation or tests). Nothing here asks an agent: the recommendation is
/// deterministic and free.
/// </summary>
public sealed record InspectorContext(
    ModelCatalog? Catalog,
    LearnedModels? Learned,
    int Requirements,
    int MandatoryRequirements,
    int? ProjectFiles,
    string? Stack,
    int? MaxAgentRuns,
    bool PlansHaveDocsOrTestsOnlyTasks)
{
    /// <summary>Nothing known: no catalog, so no recommendation and a list of models with the names the preparation always offered.</summary>
    public static InspectorContext None { get; } = new(null, null, 0, 0, null, null, null, false);
}

/// <summary>A model of the list of the inspector: its name, its level in words and whether it is the one recommended.</summary>
public sealed record ModelChoice(string Label, string? Model, bool IsRecommended)
{
    public override string ToString() => Label;
}

/// <summary>A tool of the agent, with its risk said: a tool of high risk asks Windows Hello to be given (docs/17 D-46 and D-37).</summary>
public sealed partial class ToolChoice : ObservableObject
{
    internal ToolChoice(string name, bool isOn, bool isHighRisk, string riskText, bool canEdit)
    {
        Name = name;
        _isOn = isOn;
        IsHighRisk = isHighRisk;
        RiskText = riskText;
        CanEdit = canEdit;
    }

    public string Name { get; }

    public bool IsHighRisk { get; }

    public string RiskText { get; }

    public bool CanEdit { get; }

    [ObservableProperty]
    private bool _isOn;

    internal Func<ToolChoice, bool, Task>? Changed { get; set; }

    internal bool Restoring { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!Restoring)
        {
            _ = Changed?.Invoke(this, value);
        }
    }

    public string AutomationName => IsHighRisk ? $"{Name} · {RiskText}" : Name;

    public override string ToString() => AutomationName;
}

/// <summary>A folder the agent can work in (a main folder of the repository, or the whole project): the same cable «Trabaja en» as on the canvas, with the keyboard.</summary>
public sealed partial class FolderChoice : ObservableObject
{
    internal FolderChoice(TeamConnectorViewModel connector, bool isOn, bool canEdit)
    {
        Connector = connector;
        _isOn = isOn;
        CanEdit = canEdit;
    }

    public TeamConnectorViewModel Connector { get; }

    public string Label => Connector.Title;

    public bool CanEdit { get; }

    [ObservableProperty]
    private bool _isOn;

    internal Action<FolderChoice, bool>? Changed { get; set; }

    internal bool Restoring { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!Restoring)
        {
            Changed?.Invoke(this, value);
        }
    }

    public override string ToString() => Label;
}

/// <summary>
/// The inspector of the agent selected (docs/17 D-42 and F13-17): only the fields that govern the run — the name, role and
/// program, the model and its effort with the data of WP2 and the recommendation with its reason, the tools with their risk, the
/// folders (which govern with <c>WriteScopeOutsideAgentArea</c>), the memory and the review, the time of an attempt and, marked
/// «solo Claude», the turns and the cost, and the instructions of the agent, which reach its prompt as data. <c>MaxRounds</c> and
/// <c>MaxTotalTokens</c> are left out: they govern nothing yet. Each change goes through <see cref="TeamEditorViewModel"/>, so it
/// edits the template with its consequences.
/// </summary>
public sealed partial class AgentInspectorViewModel : ObservableObject
{
    /// <summary>The tools of the CLIs the inspector offers: files, search, shell and network.</summary>
    public static IReadOnlyList<string> OfferedTools { get; } = ["Read", "Grep", "Glob", "Edit", "Write", "Bash", "WebFetch", "WebSearch"];

    private readonly TeamEditorViewModel _editor;
    private readonly TeamCanvasViewModel _canvas;
    private readonly InspectorContext _context;
    private readonly CanvasText _text;
    private bool _restoring;

    internal AgentInspectorViewModel(AgentDefinition agent, TeamEditorViewModel editor, TeamCanvasViewModel canvas, InspectorContext context, CanvasText text)
    {
        Agent = agent;
        _editor = editor;
        _canvas = canvas;
        _context = context;
        _text = text;
        _restoring = true;
        Name = agent.Name;
        Instructions = agent.SystemInstructions ?? string.Empty;
        ReceivesMemory = agent.Memory != MemoryPolicy.None;
        RequiresIndependentReview = agent.Review.RequiresIndependentReview;
        RequiresSystemCapturedTests = agent.Review.RequiresSystemCapturedTests;
        MaxMinutes = ((int)agent.Budget.MaxDuration.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        MaxTurns = agent.Budget.MaxTurns.ToString(CultureInfo.InvariantCulture);
        MaxCostUsd = agent.Budget.MaxCostUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Models = BuildModels();
        _selectedModel = Models.FirstOrDefault(m => string.Equals(m.Model, NullIfBlank(agent.Model.Model), StringComparison.OrdinalIgnoreCase)) ?? Models[0];
        Efforts = BuildEfforts();
        _selectedEffort = Efforts.FirstOrDefault(e => e.Effort == agent.Effort) ?? Efforts[0];
        Tools = BuildTools();
        Folders = BuildFolders();
        _restoring = false;
    }

    /// <summary>The agent of the template this inspector shows.</summary>
    public AgentDefinition Agent { get; }

    public string Title => _text.Format("Canvas_Inspector_Title", Agent.Name);

    public string RoleText => _text.Role(Agent.Role);

    /// <summary>The program and its company: «Claude Code (Anthropic)».</summary>
    public string ProgramText => $"{Agent.Model.Provider} ({Agent.Model.Vendor})";

    /// <summary>The agent can be edited: a store, a project and a layer that is not read only.</summary>
    public bool CanEdit => _editor.CanEdit;

    /// <summary>Whether the agent writes in the repository: only then it has cables «Trabaja en» and folders.</summary>
    public bool IsWriter => Domain.Team.TeamRoles.Writes(Agent.Role);

    /// <summary>A judge or an auditor reads only: it never gets memory nor tools of writing.</summary>
    public bool IsJudge => Domain.Team.TeamRoles.Judges(Agent.Role);

    public bool CanReceiveMemory => !IsJudge;

    /// <summary>These fields reach only Claude Code (<c>--max-turns</c>, <c>--max-budget-usd</c>): they are marked «solo Claude».</summary>
    public bool IsClaude => Agent.Model.Provider == ProviderId.ClaudeCode;

    public string OnlyClaudeText => _text.Get("Canvas_Inspector_OnlyClaude");

    // ---------- Name, instructions and limits: written and applied with «Aplicar» ----------

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private string _maxMinutes = string.Empty;

    [ObservableProperty]
    private string _maxTurns = string.Empty;

    [ObservableProperty]
    private string _maxCostUsd = string.Empty;

    /// <summary>The text a field of the group above does not accept, or null.</summary>
    [ObservableProperty]
    private string? _fieldProblem;

    /// <summary>«Aplicar»: saves the name, the instructions and the limits of an attempt.</summary>
    [RelayCommand]
    private async Task ApplyFieldsAsync()
    {
        FieldProblem = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            FieldProblem = _text.Get("Canvas_Inspector_NameRequired");
            return;
        }

        if (!int.TryParse(MaxMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 1 or > 24 * 60)
        {
            FieldProblem = _text.Get("Canvas_Inspector_MinutesInvalid");
            return;
        }

        if (!int.TryParse(MaxTurns, NumberStyles.None, CultureInfo.InvariantCulture, out var turns) || turns < 1)
        {
            FieldProblem = _text.Get("Canvas_Inspector_TurnsInvalid");
            return;
        }

        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(MaxCostUsd))
        {
            if (!decimal.TryParse(MaxCostUsd, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                FieldProblem = _text.Get("Canvas_Inspector_CostInvalid");
                return;
            }

            cost = parsed;
        }

        var updated = Agent with
        {
            Name = Name.Trim(),
            SystemInstructions = Instructions.Trim(),
            Budget = Agent.Budget with { MaxDuration = TimeSpan.FromMinutes(minutes), MaxTurns = turns, MaxCostUsd = cost },
        };
        await _editor.ApplyAsync(updated, _text.Format("Canvas_Editor_Changed", Agent.Id.Value)).ConfigureAwait(true);
    }

    // ---------- Model and effort ----------

    public IReadOnlyList<ModelChoice> Models { get; }

    [ObservableProperty]
    private ModelChoice _selectedModel = null!;

    public IReadOnlyList<EffortOption> Efforts { get; private set; }

    [ObservableProperty]
    private EffortOption _selectedEffort = null!;

    partial void OnSelectedModelChanged(ModelChoice value)
    {
        if (_restoring || value is null || string.Equals(value.Model, NullIfBlank(Agent.Model.Model), StringComparison.Ordinal))
        {
            return;
        }

        // The effort stays if the new model takes it; if it does not, the nearest one it takes (never one the template refuses).
        var entry = _context.Catalog?.Find(Agent.Model.Provider, value.Model);
        var effort = Agent.Effort;
        if (effort is { } wanted && entry is not null && !entry.Accepts(wanted))
        {
            effort = ModelRecommender.NearestEffort(entry, wanted);
        }

        _ = ApplyModelAsync(Agent with { Model = Agent.Model with { Model = value.Model }, Effort = effort }, _text.Format("Canvas_Editor_ModelChanged", Agent.Id.Value, value.Label));
    }

    partial void OnSelectedEffortChanged(EffortOption value)
    {
        if (_restoring || value is null || value.Effort == Agent.Effort)
        {
            return;
        }

        _ = ApplyAndRestoreAsync(Agent with { Effort = value.Effort }, _text.Format("Canvas_Editor_EffortChanged", Agent.Id.Value, value.Label));
    }

    private async Task ApplyModelAsync(AgentDefinition updated, string notice)
    {
        if (!await _editor.ApplyAsync(updated, notice).ConfigureAwait(true))
        {
            Restore();
        }
    }

    private async Task ApplyAndRestoreAsync(AgentDefinition updated, string notice)
    {
        if (!await _editor.ApplyAsync(updated, notice).ConfigureAwait(true))
        {
            Restore();
        }
    }

    private void Restore()
    {
        _restoring = true;
        try
        {
            SelectedModel = Models.FirstOrDefault(m => string.Equals(m.Model, NullIfBlank(Agent.Model.Model), StringComparison.OrdinalIgnoreCase)) ?? Models[0];
            SelectedEffort = Efforts.FirstOrDefault(e => e.Effort == Agent.Effort) ?? Efforts[0];
            ReceivesMemory = Agent.Memory != MemoryPolicy.None;
            RequiresIndependentReview = Agent.Review.RequiresIndependentReview;
            RequiresSystemCapturedTests = Agent.Review.RequiresSystemCapturedTests;
            foreach (var tool in Tools)
            {
                tool.Restoring = true;
                tool.IsOn = Agent.AllowedTools.Contains(tool.Name, StringComparer.Ordinal);
                tool.Restoring = false;
            }

            foreach (var folder in Folders)
            {
                folder.Restoring = true;
                folder.IsOn = IsConnected(folder.Connector);
                folder.Restoring = false;
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private IReadOnlyList<ModelChoice> BuildModels()
    {
        var recommended = Recommendation?.Model;
        var choices = new List<ModelChoice> { new(_text.Get("Canvas_Model_ProgramDefault"), null, false) };
        var names = _context.Catalog?.Entries.Where(e => e.Provider == Agent.Model.Provider).ToList() ?? [];
        if (names.Count > 0)
        {
            choices.AddRange(names.Select(e => new ModelChoice(
                _text.Format("Canvas_Inspector_ModelFormat", e.Name, _text.Tier(e.Tier)),
                e.Name,
                recommended is not null && e.IsNamed(recommended))));
        }
        else
        {
            choices.AddRange(Preparation.AgentModels.Known(Agent.Model.Provider).Select(n => new ModelChoice(n, n, false)));
        }

        if (NullIfBlank(Agent.Model.Model) is { } current && choices.All(c => !string.Equals(c.Model, current, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new ModelChoice(current, current, false));
        }

        return choices;
    }

    private IReadOnlyList<EffortOption> BuildEfforts()
    {
        var entry = _context.Catalog?.Find(Agent.Model.Provider, Agent.Model.Model);
        IReadOnlyList<ReasoningEffort> offered = entry is { Efforts.Count: > 0 } ? entry.Efforts : ReasoningEfforts.For(Agent.Model.Provider);
        return [new EffortOption(_text.Get("Models_Effort_CliDefault"), null), .. offered.Select(e => new EffortOption(_text.Effort(e), e))];
    }

    /// <summary>The model and the effort recommended for this agent, deterministic (D-24); null without a catalog.</summary>
    public ModelRecommendation? Recommendation
    {
        get
        {
            if (_context.Catalog is not { } catalog)
            {
                return null;
            }

            var producers = _canvas.Roster
                .Where(a => a.Id != Agent.Id && (a.Role == AgentRole.Orchestrator || Domain.Team.TeamRoles.Writes(a.Role)))
                .Select(a => a.Model.Vendor)
                .Distinct()
                .ToList();
            return ModelRecommender.Recommend(
                catalog,
                new ModelRecommendationInput(
                    Agent.Role,
                    Agent.Model.Provider,
                    producers,
                    _context.ProjectFiles,
                    _context.Requirements,
                    _context.MandatoryRequirements,
                    _context.Stack,
                    _context.MaxAgentRuns,
                    _context.PlansHaveDocsOrTestsOnlyTasks,
                    _context.Learned?.Performances ?? []));
        }
    }

    /// <summary>«Recomendado: sonnet · esfuerzo medio»; empty without a recommendation.</summary>
    public string RecommendationText => Recommendation is { } r
        ? _text.Format("Canvas_Inspector_Recommended", r.Model, _text.Tier(r.Tier), _text.Effort(r.Effort))
        : _text.Get("Canvas_Inspector_NoRecommendation");

    /// <summary>Why, in one sentence: the reason of the recommendation with its figures («tus 9 tareas con sonnet…»).</summary>
    public string RecommendationWhy => Recommendation is { } r ? _text.Render(r.ReasonCode, r.ReasonArguments) : string.Empty;

    public bool HasRecommendation => Recommendation is not null;

    /// <summary>How the model of the agent did in this project, with its origin; says it has no data yet when it has none.</summary>
    public string PerformanceText
    {
        get
        {
            var learned = _context.Learned?.Find(Agent.Model.Provider, Agent.Model.Model);
            if (learned is null)
            {
                return _text.Get("Models_NoDataYet");
            }

            var p = learned.Performance;
            return p.IsKnown
                ? _text.Format("Models_Performance", p.Tasks, p.FirstPassApprovals, p.Retries, p.Rejections)
                : _text.Format("Models_Performance_Unknown", p.Tasks, ModelPerformance.MinimumReferences);
        }
    }

    /// <summary>The agent already uses the model and the effort recommended.</summary>
    public bool FollowsRecommendation => Recommendation is { } r && _context.Catalog is { } catalog && ModelRecommender.IsFollowedBy(catalog, r, Agent);

    /// <summary>«Usar la recomendación»: the model and the effort recommended become those of the agent.</summary>
    [RelayCommand(CanExecute = nameof(CanUseRecommendation))]
    private async Task UseRecommendationAsync()
    {
        if (Recommendation is not { } r)
        {
            return;
        }

        await ApplyModelAsync(
            Agent with { Model = Agent.Model with { Model = r.Model }, Effort = r.Effort },
            _text.Format("Canvas_Editor_ModelChanged", Agent.Id.Value, r.Model)).ConfigureAwait(true);
    }

    private bool CanUseRecommendation() => CanEdit && HasRecommendation && !FollowsRecommendation;

    // ---------- Memory and review ----------

    [ObservableProperty]
    private bool _receivesMemory;

    [ObservableProperty]
    private bool _requiresIndependentReview;

    [ObservableProperty]
    private bool _requiresSystemCapturedTests;

    partial void OnReceivesMemoryChanged(bool value)
    {
        if (_restoring || !CanReceiveMemory || value == (Agent.Memory != MemoryPolicy.None))
        {
            return;
        }

        _ = ApplyAndRestoreAsync(
            Agent with { Memory = value ? MemoryPolicy.ReadSelected : MemoryPolicy.None },
            _text.Format(value ? "Canvas_Editor_MemoryOn" : "Canvas_Editor_MemoryOff", Agent.Id.Value));
    }

    partial void OnRequiresIndependentReviewChanged(bool value) => ReviewChanged();

    partial void OnRequiresSystemCapturedTestsChanged(bool value) => ReviewChanged();

    private void ReviewChanged()
    {
        if (_restoring || (RequiresIndependentReview == Agent.Review.RequiresIndependentReview && RequiresSystemCapturedTests == Agent.Review.RequiresSystemCapturedTests))
        {
            return;
        }

        _ = ApplyAndRestoreAsync(
            Agent with { Review = new ReviewPolicy(RequiresIndependentReview, RequiresSystemCapturedTests) },
            _text.Format("Canvas_Editor_Changed", Agent.Id.Value));
    }

    // ---------- Tools and folders ----------

    public IReadOnlyList<ToolChoice> Tools { get; }

    public IReadOnlyList<FolderChoice> Folders { get; }

    private IReadOnlyList<ToolChoice> BuildTools()
    {
        var names = OfferedTools.Concat(Agent.AllowedTools).Distinct(StringComparer.Ordinal).ToList();
        var list = names.Select(name =>
        {
            var high = AgentCapabilities.IsHighRisk(name);
            var tool = new ToolChoice(
                name,
                Agent.AllowedTools.Contains(name, StringComparer.Ordinal),
                high,
                _text.Get(high ? "Canvas_Inspector_RiskHigh" : "Canvas_Inspector_RiskNormal"),
                !IsJudge);
            tool.Changed = ToolChangedAsync;
            return tool;
        }).ToList();
        return list;
    }

    private async Task ToolChangedAsync(ToolChoice tool, bool isOn)
    {
        var tools = Agent.AllowedTools.Where(t => !string.Equals(t, tool.Name, StringComparison.Ordinal)).ToList();
        if (isOn)
        {
            tools.Add(tool.Name);
        }

        var notice = _text.Format(isOn ? "Canvas_Editor_ToolGiven" : "Canvas_Editor_ToolTaken", Agent.Id.Value, tool.Name);
        await ApplyAndRestoreAsync(Agent with { AllowedTools = tools }, notice).ConfigureAwait(true);
    }

    private IReadOnlyList<FolderChoice> BuildFolders()
    {
        if (!IsWriter || _canvas.Repository is not { } repository)
        {
            return [];
        }

        var list = new[] { repository.WholeProject }.Concat(repository.Folders).Select(connector =>
        {
            var folder = new FolderChoice(connector, IsConnected(connector), CanEdit);
            folder.Changed = FolderChanged;
            return folder;
        }).ToList();
        return list;
    }

    private bool IsConnected(TeamConnectorViewModel connector)
    {
        var node = _canvas.Agents.FirstOrDefault(a => a.Id == Agent.Id);
        return node is not null && _canvas.Connections.Any(c => c.Kind == Domain.Team.TeamEdgeKind.WorksIn && ReferenceEquals(c.Source, node.Output) && ReferenceEquals(c.Target, connector));
    }

    private void FolderChanged(FolderChoice folder, bool isOn)
    {
        if (_canvas.Agents.FirstOrDefault(a => a.Id == Agent.Id) is not { } node)
        {
            return;
        }

        var refusal = isOn ? _canvas.RequestConnect(node, folder.Connector) : _canvas.RequestDisconnect(node, folder.Connector);
        if (refusal is not null)
        {
            _editor.ErrorMessage = refusal;
            Restore();
        }
    }

    // ---------- The context by parts ----------

    /// <summary>
    /// The bar of the context by parts (D-13): what Agent Studio put in the prompt before the agent started, each part a segment,
    /// «estimado por Agent Studio»; what the CLI reported beyond their sum is the conversation. In Codex only the parts before the
    /// start are known, and it says the occupancy in progress is not exposed.
    /// </summary>
    public MetricBarReading? PartsBar
    {
        get
        {
            var live = _canvas.Agents.FirstOrDefault(a => a.Id == Agent.Id)?.Live?.Context;
            if (live is null || live.Parts.Count == 0)
            {
                return null;
            }

            var breakdown = ContextBreakdown.Of(live.Parts, live.UsedTokens);
            var segments = breakdown.Parts
                .Select(p => new MetricBarSegment(ContextPartLabel(p.Kind), p.Tokens.Value ?? 0, FormatTokens(p.Tokens.Value ?? 0), true))
                .ToList();
            if (breakdown.Conversation.Value is { } conversation)
            {
                segments.Add(new MetricBarSegment(_text.Get(Localization.ContextPartTexts.ConversationKey), conversation, FormatTokens(conversation), true));
            }

            var total = segments.Sum(s => s.Value);
            var window = live.Window.Value is { } w && w > 0 ? w : (long?)null;
            var label = _text.Get("Canvas_Inspector_PartsBar");
            return new MetricBarReading
            {
                Label = label,
                Value = total,
                Maximum = window ?? Math.Max(1, total),
                ValueText = FormatTokens((long)total),
                Origin = _text.Get(breakdown.Used.HasValue ? "Canvas_Inspector_PartsOrigin" : "Canvas_Inspector_PartsOriginNotExposed"),
                OriginKind = MetricOrigin.EstimatedBySystem,
                IsEstimate = true,
                Segments = segments,
                Detail = breakdown.Used.HasValue ? null : _text.Get("Canvas_Inspector_PartsNotExposed"),
            };
        }
    }

    private string ContextPartLabel(ContextPartKind kind) => _text.Get(Localization.ContextPartTexts.KeyOf(kind));

    private string FormatTokens(double tokens) => _text.Format("Canvas_Inspector_Tokens", tokens.ToString("N0", _text.Provider.Culture));

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public override string ToString() => Title;
}
