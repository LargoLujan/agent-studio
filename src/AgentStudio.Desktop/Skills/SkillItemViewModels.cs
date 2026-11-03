using AgentStudio.Application.Skills;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Skills;

/// <summary>A folder of the tree: one sector, with its skills that pass the search and their count.</summary>
public sealed partial class SkillSectorNodeViewModel : ObservableObject
{
    public SkillSectorNodeViewModel(string sectorId, string title, IReadOnlyList<SkillItemViewModel> skills)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(skills);
        SectorId = sectorId;
        Title = title;
        Skills = skills;
    }

    public string SectorId { get; }

    public string Title { get; }

    public IReadOnlyList<SkillItemViewModel> Skills { get; }

    public int Count => Skills.Count;

    /// <summary>«Ingeniería de datos (6)».</summary>
    public string Header => $"{Title} ({Count})";

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>What a screen reader says of the folder in the tree (A1-bis): its header, never the name of the type.</summary>
    public override string ToString() => Header;
}

/// <summary>
/// One skill of the tree: its catalog metadata as the MCP server sees it (<see cref="Card"/>), its local health and a
/// short badge of where it is active. Selecting it (from the tree or from a recommendation) shows its card.
/// </summary>
public sealed partial class SkillItemViewModel : ObservableObject
{
    private readonly Action<SkillItemViewModel> _selected;

    internal SkillItemViewModel(CatalogSkill skill, SnapshotSkill card, SkillHealthReport health, Action<SkillItemViewModel> selected)
    {
        Skill = skill;
        Card = card;
        Health = health;
        _selected = selected;
    }

    public CatalogSkill Skill { get; }

    /// <summary>The metadata of the skill with the agents it is active for, refreshed after every decision.</summary>
    public SnapshotSkill Card { get; internal set; }

    public SkillHealthReport Health { get; }

    public string Id => Skill.Entry.Id.Value;

    public string Name => Skill.Entry.Name;

    public string Summary => Skill.Entry.Summary;

    /// <summary>No local copy: it cannot be given to an agent.</summary>
    public bool IsLinked => Skill.LocalPath is null;

    public SkillHealthStatus HealthStatus => Health.Status;

    public string HealthLabel => SkillsTexts.Health(HealthStatus);

    public string ToolTip => $"{Name} · {HealthLabel}{(IsLinked ? " · enlazada" : string.Empty)}\n{Summary}";

    /// <summary>«Proy.», «Global», «2 agentes» or nothing.</summary>
    [ObservableProperty]
    private string _activeBadge = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What a screen reader says of the skill in the tree (A1-bis): its name, never the name of the type.</summary>
    public override string ToString() => Name;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _selected(this);
        }
    }
}

/// <summary>One check of the local health, with its Spanish title and label.</summary>
public sealed record SkillCheckViewModel(string Code, string Title, SkillHealthStatus Status, string StatusLabel, string Message)
{
    /// <summary>What a screen reader says of the check (A1-bis): «Licencia: Sana», never the members of the record.</summary>
    public override string ToString() => $"{Title}: {StatusLabel}";
}

/// <summary>
/// The card of the selected skill (docs/14 D-26): source, tags, local health with its checks, estimated context cost,
/// one switch per scope and the warnings. Rebuilt whenever the selection or the decisions change.
/// </summary>
public sealed class SkillDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string SectorTitle { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    /// <summary>«anthropics/skills · skills/x · commit 8a1541c · MIT».</summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>«Copia local» or «Enlazada: sin copia local».</summary>
    public string ModeText { get; init; } = string.Empty;

    /// <summary>«#a #b #c», or «Sin etiquetas».</summary>
    public string TagsText { get; init; } = string.Empty;

    /// <summary>Always an estimate of Agent Studio (<see cref="SkillContextCost"/>).</summary>
    public MetricValue<long> Cost { get; init; }

    public string CostText { get; init; } = string.Empty;

    public SkillHealthStatus HealthStatus { get; init; }

    public string HealthLabel { get; init; } = string.Empty;

    public bool IsLinked { get; init; }

    public IReadOnlyList<SkillCheckViewModel> Checks { get; init; } = [];

    public IReadOnlyList<SkillScopeViewModel> Scopes { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// The switch of one scope for the selected skill. <see cref="Decision"/> is the last decision of that scope (null when it
/// has none, and then it inherits); <see cref="EffectiveText"/>, on an agent's switch, is what that agent really gets and
/// which scope decided it. Toggling appends a decision of the user; nothing is ever rewritten.
/// </summary>
public sealed class SkillScopeViewModel
{
    public SkillScopeKind Scope { get; init; }

    /// <summary>Null for Global, the project id for Project, the agent id for Agent.</summary>
    public string? ScopeId { get; init; }

    public string Title { get; init; } = string.Empty;

    public bool? Decision { get; init; }

    public bool IsChecked => Decision == true;

    /// <summary>«Sin decisión», «Activada» or «Desactivada».</summary>
    public string DecisionText => SkillsTexts.Decision(Decision);

    public string? EffectiveText { get; init; }

    public bool CanToggle { get; init; }

    public IAsyncRelayCommand ToggleCommand { get; init; } = new AsyncRelayCommand(() => Task.CompletedTask);

    /// <summary>What a screen reader says of the scope in its list (A1-bis): its title, never the name of the type.</summary>
    public override string ToString() => Title;
}

/// <summary>What one agent of the roster gets: its skills that reach it (active and with a local copy) and their estimated cost.</summary>
public sealed record AgentCostViewModel(string Agent, int Skills, MetricValue<long> Tokens)
{
    /// <summary>«ejecutor: 2 skills · ~37 tokens por ejecución (estimado)».</summary>
    public string Text => $"{Agent}: {Skills} {(Skills == 1 ? "skill" : "skills")} · ~{Tokens.Value ?? 0} tokens por ejecución (estimado)";

    /// <summary>What a screen reader says of the line (A1-bis): its text, never the members of the record.</summary>
    public override string ToString() => Text;
}

/// <summary>A recommended skill (deterministic or of the agent), with its reasons, and how to show it in the tree.</summary>
public sealed record SkillRecommendationItemViewModel(string SkillId, string Name, string Detail, IRelayCommand ShowCommand)
{
    /// <summary>What a screen reader says of the recommendation (A1-bis): its name, never the members of the record.</summary>
    public override string ToString() => Name;
}

/// <summary>The Spanish texts of the screen.</summary>
internal static class SkillsTexts
{
    public static string Health(SkillHealthStatus status) => status switch
    {
        SkillHealthStatus.Healthy => "Sana",
        SkillHealthStatus.Warning => "Aviso",
        SkillHealthStatus.Critical => "Crítico",
        _ => "Sin comprobar",
    };

    public static string Check(string code) => code switch
    {
        SkillHealthCodes.Frontmatter => "Cabecera",
        SkillHealthCodes.Size => "Tamaño",
        SkillHealthCodes.License => "Licencia",
        SkillHealthCodes.Pinned => "Fijada a un commit",
        SkillHealthCodes.Upstream => "Cambios en origen",
        SkillHealthCodes.Archived => "Origen archivado",
        SkillHealthCodes.Scripts => "Scripts",
        _ => code,
    };

    public static string Decision(bool? decision) => decision switch
    {
        null => "Sin decisión",
        true => "Activada",
        false => "Desactivada",
    };

    public static string Scope(SkillScopeKind scope, string? agent) => scope switch
    {
        SkillScopeKind.Global => "Global",
        SkillScopeKind.Project => "Proyecto",
        _ => $"Agente «{agent}»",
    };
}
