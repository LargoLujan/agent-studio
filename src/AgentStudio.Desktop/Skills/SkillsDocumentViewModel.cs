using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The «Skills» document (docs/14 D-26, F4-7, and D-29, F4-8): the catalog by sector with a deterministic search, the
/// card of a skill with its source, local health, estimated context cost and one switch per scope (Global, Proyecto and
/// one per agent of the roster), the cost of what each agent gets, and the two recommendations.
/// <list type="bullet">
/// <item>A switch appends a decision of the user (<see cref="DecidedBy"/>) to <see cref="ISkillActivationStore"/>; the
/// effective state comes from <see cref="SkillActivationResolver"/>. A linked skill has no switch: it cannot be given to
/// an agent.</item>
/// <item>The deterministic recommendation is free: the text of the task and the stack of the project, detected from the
/// names of its files (<see cref="ProjectStackDetector"/>).</item>
/// <item>The assisted one is off until the user allows it in this screen (<see cref="IsAssistedAllowed"/>, not kept
/// between sessions), and every use asks for the user's OK through the approval gate, which records it (<see cref="IApprovalGate"/>); its answer is a proposal
/// shown next to the deterministic one. Nothing is ever activated except by a switch.</item>
/// </list>
/// Any missing piece of <see cref="SkillsBackend"/> becomes a line of <see cref="Notices"/>: nothing is made up. The warnings of
/// the index about its entries (one per linked skill, for instance, often dozens) are <see cref="CatalogWarnings"/>, which the
/// screen sums up in one line (<see cref="CatalogWarningsSummary"/>) and shows on demand, so the catalog stays in view.
/// </summary>
public sealed partial class SkillsDocumentViewModel : DocumentViewModel
{
    public const string DocumentId = "document:skills";

    /// <summary>Who decides an activation or gives the consent: the person at the desktop, as the approvals record it.</summary>
    public const string DecidedBy = ApprovalPromptService.DecidedBy;

    /// <summary>The action of the approval asked before every assisted recommendation.</summary>
    public const string AssistedAction = "skills.recomendacion-asistida";

    /// <summary>What the person is asked before every assisted recommendation; it becomes the description of the consent.</summary>
    public const string AssistedDescription =
        "Pedir a un agente que recomiende skills para esta tarea. Se lanza Claude Code con haiku, de solo lectura, sin red y "
        + "en una carpeta vacía, con el servidor MCP local del catálogo como única herramienta extra. Gasta un poco de cuota "
        + "de tu suscripción (una ejecución corta). Solo propone: activar sigue siendo cosa tuya.";

    private readonly SkillsBackend _backend;
    private readonly Func<string, CancellationToken, IReadOnlyList<string>> _projectFiles;
    private IReadOnlyList<SkillItemViewModel> _items = [];
    private IReadOnlyList<AgentDefinition> _roster = [];
    private IReadOnlyList<SkillActivation> _activations = [];
    private bool _opened;

    /// <param name="projectRoot">The open project, or null: then the catalog is only browsed.</param>
    /// <param name="backend">The ports; any may be missing.</param>
    /// <param name="time">The clock (the screen reads no time today; kept for the composition, like the other documents).</param>
    /// <param name="projectFiles">The relative paths of the project's files; by default the explorer's index (names only).</param>
    public SkillsDocumentViewModel(string? projectRoot, SkillsBackend backend, TimeProvider time, Func<string, CancellationToken, IReadOnlyList<string>>? projectFiles = null)
        : base(DocumentId, "Skills")
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        ProjectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
        _backend = backend;
        _projectFiles = projectFiles ?? ((root, ct) => ProjectFileIndexer.Build(root, ProjectFileIndexer.DefaultMaxFiles, ct).RelativePaths);
    }

    public string? ProjectRoot { get; }

    /// <summary>The project once opened; null without one, or when it could not be opened.</summary>
    public ProjectContext? Project { get; private set; }

    /// <summary>What the person should know: missing pieces, the warnings about the whole catalog and the note on Codex's own skills.</summary>
    public ObservableCollection<string> Notices { get; } = [];

    /// <summary>
    /// The warnings of the index about its entries, one per line: a linked skill, a vendored one without a usable copy, an
    /// entry left out. Shown on demand under <see cref="CatalogWarningsSummary"/>.
    /// </summary>
    public ObservableCollection<string> CatalogWarnings { get; } = [];

    /// <summary>
    /// <see cref="CatalogWarnings"/> in one line with their count («27 skills enlazadas no se pueden dar a un agente.»), or the
    /// warning itself when there is only one; null without any.
    /// </summary>
    [ObservableProperty]
    private string? _catalogWarningsSummary;

    /// <summary>Whether there is more than the summary says: then the detail can be shown.</summary>
    public bool HasCatalogWarningDetail => CatalogWarnings.Count > 1;

    /// <summary>Whether the detail of <see cref="CatalogWarnings"/> is shown; off until the person asks for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogWarningsToggleText))]
    private bool _showCatalogWarnings;

    /// <summary>What the button of the detail says: «Ver el detalle (28)» or «Ocultar el detalle».</summary>
    public string CatalogWarningsToggleText => ShowCatalogWarnings
        ? "Ocultar el detalle"
        : string.Create(CultureInfo.InvariantCulture, $"Ver el detalle ({CatalogWarnings.Count})");

    public ObservableCollection<SkillSectorNodeViewModel> Sectors { get; } = [];

    public ObservableCollection<AgentCostViewModel> AgentCosts { get; } = [];

    public ObservableCollection<SkillRecommendationItemViewModel> Recommendations { get; } = [];

    public ObservableCollection<SkillRecommendationItemViewModel> AssistedRecommendations { get; } = [];

    public ObservableCollection<string> AssistedWarnings { get; } = [];

    /// <summary>The context tags of the project, from the names of its files (D-28).</summary>
    public IReadOnlyList<string> Stack { get; private set; } = [];

    /// <summary>Decisions can be appended: there is a project and a store of decisions.</summary>
    public bool CanActivate => Project is not null && _backend.Activations is not null;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = "Cargando el catálogo…";

    [ObservableProperty]
    private string _stackText = "Sin proyecto: no hay stack que detectar.";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SkillDetailViewModel? _detail;

    /// <summary>Why the last decision could not be saved; null when it was.</summary>
    [ObservableProperty]
    private string? _activationProblem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecommendCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskAgentCommand))]
    private string _taskText = string.Empty;

    /// <summary>The assisted recommendation is allowed in this screen; off until the person turns it on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskAgentCommand))]
    private bool _isAssistedAllowed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskAgentCommand))]
    private bool _isAsking;

    [ObservableProperty]
    private string? _recommendationStatus;

    [ObservableProperty]
    private string? _assistedStatus;

    private SkillItemViewModel? _selected;

    /// <summary>Loads the catalog, the project, its roster and its decisions once; called when the tab is opened.</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        await LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Reads everything again: the index, the roster and the decisions may have changed elsewhere.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    private async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var notices = new List<string>();
            var entryWarnings = new List<string>();
            Project = await OpenProjectAsync(notices, ct).ConfigureAwait(true);
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(CanActivate));
            AskAgentCommand.NotifyCanExecuteChanged();

            var catalog = await ReadCatalogAsync(notices, entryWarnings, ct).ConfigureAwait(true);
            _roster = await ReadRosterAsync(notices, ct).ConfigureAwait(true);
            _activations = await ReadActivationsAsync(notices, ct).ConfigureAwait(true);
            var health = await ReadHealthAsync(catalog, notices, ct).ConfigureAwait(true);
            if (_backend.Recommendations is null)
            {
                notices.Add("Falta la recomendación asistida: solo está la determinista, que no gasta cuota.");
            }
            else if (_backend.Approvals is null)
            {
                notices.Add("Falta el diálogo de aprobación: sin él no se puede pedir el OK de la recomendación asistida.");
            }

            if (_roster.Any(agent => agent.Model.Provider == ProviderId.CodexCli))
            {
                notices.Add("Codex carga además tus skills globales (~/.agents/skills): activar una aquí solo añade, no oculta las que ya tienes.");
            }

            await DetectStackAsync(notices, ct).ConfigureAwait(true);

            var selectedId = _selected?.Id;
            _selected = null;
            var snapshot = SkillCatalogSnapshotBuilder.Build(DateTimeOffset.UnixEpoch, Project?.ProjectId, catalog, _activations, Agents(), health.ToDictionary(h => h.Key, h => h.Value.Status));
            var byId = catalog.GroupBy(c => c.Entry.Id.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _items = [.. snapshot.Skills.Select(card => new SkillItemViewModel(byId[card.Id], card, health[byId[card.Id].Entry.Id], Select))];

            Notices.Clear();
            foreach (var notice in notices)
            {
                Notices.Add(notice);
            }

            CatalogWarnings.Clear();
            foreach (var warning in entryWarnings)
            {
                CatalogWarnings.Add(warning);
            }

            CatalogWarningsSummary = SummarizeCatalogWarnings(
                entryWarnings,
                catalog.Count(c => c.Entry.Source.Mode == SkillDistribution.Linked),
                catalog.Count(c => c.LocalPath is null && c.Entry.Source.Mode != SkillDistribution.Linked));
            ShowCatalogWarnings &= HasCatalogWarningDetail;
            OnPropertyChanged(nameof(HasCatalogWarningDetail));
            OnPropertyChanged(nameof(CatalogWarningsToggleText));

            RefreshActivationState();
            ApplyFilter();
            if (selectedId is not null && _items.FirstOrDefault(i => i.Id == selectedId) is { } again)
            {
                again.IsSelected = true;
            }
            else
            {
                Detail = null;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<ProjectContext?> OpenProjectAsync(List<string> notices, CancellationToken ct)
    {
        if (ProjectRoot is null)
        {
            notices.Add("Abre un proyecto para activar skills: sin él solo se consulta el catálogo.");
            return null;
        }

        if (_backend.Workspace is null)
        {
            notices.Add("Falta el almacén del proyecto: no se puede activar ni ver qué está activo en él.");
            return null;
        }

        try
        {
            return await _backend.Workspace.OpenAsync(ProjectRoot, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notices.Add("No se pudo abrir el proyecto, así que no se puede activar nada: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The warnings of the index in one line with their count: the skills that cannot be given to an agent because they are
    /// linked (<paramref name="linked"/>) or have no usable copy (<paramref name="withoutCopy"/>), and the rest. A single
    /// warning is said as it is, and none is null.
    /// </summary>
    public static string? SummarizeCatalogWarnings(IReadOnlyList<string> warnings, int linked, int withoutCopy)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (warnings.Count <= 1)
        {
            return warnings.Count == 0 ? null : warnings[0];
        }

        var unusable = (linked, withoutCopy) switch
        {
            ( > 0, > 0) => $"{Count(linked, "skill enlazada", "skills enlazadas")} y {withoutCopy} sin copia local válida no se pueden dar a un agente",
            ( > 0, _) => $"{Count(linked, "skill enlazada no se puede", "skills enlazadas no se pueden")} dar a un agente",
            (_, > 0) => $"{Count(withoutCopy, "skill sin copia local válida no se puede", "skills sin copia local válida no se pueden")} dar a un agente",
            _ => null,
        };
        var rest = warnings.Count - Math.Max(0, linked) - Math.Max(0, withoutCopy);
        var others = rest <= 0 ? null
            : unusable is null ? $"{Count(rest, "aviso", "avisos")} del índice de skills"
            : $"{Count(rest, "aviso más", "avisos más")} del índice";
        return string.Join(" · ", new[] { unusable, others }.OfType<string>()) + ".";

        static string Count(int count, string one, string many) =>
            string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
    }

    private async Task<IReadOnlyList<CatalogSkill>> ReadCatalogAsync(List<string> notices, List<string> entryWarnings, CancellationToken ct)
    {
        if (_backend.Catalog is null)
        {
            notices.Add("Falta el catálogo de skills: no hay nada que mostrar.");
            return [];
        }

        try
        {
            var report = await _backend.Catalog(ct).ConfigureAwait(true);
            foreach (var warning in report.Warnings)
            {
                (IsAboutAnEntry(warning) ? entryWarnings : notices).Add(warning);
            }

            return report.Skills;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notices.Add("No se pudo leer el catálogo de skills: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Whether a warning of the index is about one of its entries rather than about the whole catalog: the index names the
    /// entry first, as «name» or «Entrada #n» (<c>SkillCatalog</c> and <c>SkillsIndexCatalogImporter</c>). A warning about the
    /// whole catalog (no index, an index that cannot be read) stays among the notices, in full.
    /// </summary>
    private static bool IsAboutAnEntry(string warning) =>
        warning.StartsWith('«') || warning.StartsWith("Entrada #", StringComparison.Ordinal);

    private async Task<IReadOnlyList<AgentDefinition>> ReadRosterAsync(List<string> notices, CancellationToken ct)
    {
        if (Project is null)
        {
            return [];
        }

        if (_backend.Roster is null)
        {
            notices.Add("Falta la plantilla de agentes: solo se activan los ámbitos Global y Proyecto.");
            return [];
        }

        try
        {
            var roster = await _backend.Roster.ListAsync(Project.ProjectId, ct).ConfigureAwait(true);
            if (roster.Count == 0)
            {
                notices.Add("El proyecto aún no tiene plantilla de agentes (el Orquestador crea la de por defecto al abrirlo): hasta entonces solo hay ámbitos Global y Proyecto.");
            }

            return [.. roster.OrderBy(agent => agent.Id.Value, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notices.Add("No se pudo leer la plantilla de agentes: " + ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<SkillActivation>> ReadActivationsAsync(List<string> notices, CancellationToken ct)
    {
        if (Project is null)
        {
            return [];
        }

        if (_backend.Activations is null)
        {
            notices.Add("Falta el almacén de activaciones: no se puede activar ni ver qué está activo.");
            return [];
        }

        try
        {
            return await _backend.Activations.ListAsync(Project.ProjectId, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notices.Add("No se pudieron leer las activaciones: " + ex.Message);
            return [];
        }
    }

    private async Task<Dictionary<SkillId, SkillHealthReport>> ReadHealthAsync(IReadOnlyList<CatalogSkill> catalog, List<string> notices, CancellationToken ct)
    {
        var health = new Dictionary<SkillId, SkillHealthReport>();
        if (_backend.Health is null && catalog.Count > 0)
        {
            notices.Add("Falta el lector de salud de las skills: todas salen «sin comprobar».");
        }

        foreach (var skill in catalog)
        {
            var report = new SkillHealthReport([]);
            if (_backend.Health is not null)
            {
                try
                {
                    report = await _backend.Health.ReadLocalAsync(skill, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report = new SkillHealthReport([new SkillHealthCheck(SkillHealthCodes.Frontmatter, SkillHealthStatus.Unknown, "No se pudo comprobar: " + ex.Message)]);
                }
            }

            health.TryAdd(skill.Entry.Id, report);
        }

        return health;
    }

    private async Task DetectStackAsync(List<string> notices, CancellationToken ct)
    {
        Stack = [];
        if (ProjectRoot is { } root)
        {
            try
            {
                var files = await Task.Run(() => _projectFiles(root, ct), ct).ConfigureAwait(true);
                Stack = [.. ProjectStackDetector.Detect(files)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notices.Add("No se pudieron listar los ficheros del proyecto para detectar su stack: " + ex.Message);
            }
        }

        OnPropertyChanged(nameof(Stack));
        StackText = ProjectRoot is null
            ? "Sin proyecto: no hay stack que detectar."
            : Stack.Count == 0
                ? "No se reconoce ningún stack por los nombres de los ficheros."
                : string.Join(' ', Stack.Select(tag => "#" + tag));
    }

    private List<AgentId> Agents() => [.. _roster.Select(agent => agent.Id)];

    // ---- the tree and the search ----

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText;
        var visible = _items.Where(item => SkillSearch.Matches(item.Card, query)).ToList();
        Sectors.Clear();
        foreach (var group in visible.GroupBy(item => item.Card.SectorId, StringComparer.Ordinal))
        {
            var first = group.First().Card;
            Sectors.Add(new SkillSectorNodeViewModel(first.SectorId, first.SectorTitle, [.. group]));
        }

        var sectors = Sectors.Count;
        Summary = string.IsNullOrWhiteSpace(query)
            ? _items.Count == 0
                ? "El catálogo está vacío."
                : $"{_items.Count} skills en {sectors} {(sectors == 1 ? "sector" : "sectores")}"
            : visible.Count == 0
                ? $"Ninguna skill coincide con «{query.Trim()}»."
                : $"{visible.Count} de {_items.Count} skills coinciden con «{query.Trim()}»";
    }

    private void Select(SkillItemViewModel item)
    {
        if (!ReferenceEquals(_selected, item) && _selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = item;
        foreach (var sector in Sectors.Where(s => s.Skills.Contains(item)))
        {
            sector.IsExpanded = true;
        }

        Detail = BuildDetail(item);
    }

    /// <summary>Selects a skill in the tree, clearing the search when it hides it.</summary>
    private void Show(string skillId)
    {
        if (_items.FirstOrDefault(item => item.Id == skillId) is not { } item)
        {
            return;
        }

        if (!Sectors.Any(sector => sector.Skills.Contains(item)))
        {
            SearchText = string.Empty;
        }

        if (item.IsSelected)
        {
            Select(item);
        }
        else
        {
            item.IsSelected = true;
        }
    }

    // ---- the card and the switches ----

    private SkillDetailViewModel BuildDetail(SkillItemViewModel item)
    {
        var entry = item.Skill.Entry;
        var source = entry.Source;
        var commit = source.PinnedCommit is { Length: >= 7 } pinned ? "commit " + pinned[..7] : "sin commit fijado";
        var license = string.IsNullOrWhiteSpace(source.License) ? "sin licencia declarada" : source.License;
        var cost = SkillContextCost.Of(entry);
        var warnings = new List<string>();
        if (item.IsLinked)
        {
            warnings.Add("Está enlazada: el índice no guarda copia local, así que no se puede dar a ningún agente ni activar aquí. Para usarla, vendorízala en tu índice.");
        }

        return new SkillDetailViewModel
        {
            Id = item.Id,
            Name = entry.Name,
            SectorTitle = entry.Sector.Title,
            Summary = entry.Summary,
            SourceText = $"{source.Repository} · {source.Path} · {commit} · {license}",
            ModeText = item.IsLinked ? "Enlazada: sin copia local" : "Copia local revisada (vendorizada)",
            TagsText = item.Card.Tags.Count == 0 ? "Sin etiquetas" : string.Join(' ', item.Card.Tags.Select(tag => "#" + tag)),
            Cost = cost,
            CostText = $"~{cost.Value} tokens en cada ejecución en la que esté activa (estimado por Agent Studio: nombre y descripción entre {SkillContextCost.CharactersPerToken}).",
            HealthStatus = item.HealthStatus,
            HealthLabel = item.HealthLabel,
            IsLinked = item.IsLinked,
            Checks = [.. item.Health.Checks.Select(check => new SkillCheckViewModel(check.Code, SkillsTexts.Check(check.Code), check.Status, SkillsTexts.Health(check.Status), check.Message))],
            Scopes = Scopes(item),
            Warnings = warnings,
        };
    }

    private List<SkillScopeViewModel> Scopes(SkillItemViewModel item)
    {
        var skill = item.Skill.Entry.Id;
        var projectId = Project?.ProjectId;
        var canToggle = CanActivate && !item.IsLinked;
        var scopes = new List<SkillScopeViewModel>
        {
            Switch(item, SkillScopeKind.Global, null, LastDecision(skill, SkillScopeKind.Global, null), null, canToggle),
            Switch(item, SkillScopeKind.Project, projectId, projectId is null ? null : LastDecision(skill, SkillScopeKind.Project, projectId), null, canToggle),
        };
        foreach (var agent in _roster)
        {
            var id = agent.Id.Value;
            scopes.Add(Switch(item, SkillScopeKind.Agent, id, LastDecision(skill, SkillScopeKind.Agent, id), EffectiveText(skill, id), canToggle));
        }

        return scopes;
    }

    private SkillScopeViewModel Switch(SkillItemViewModel item, SkillScopeKind scope, string? scopeId, bool? decision, string? effective, bool canToggle) => new()
    {
        Scope = scope,
        ScopeId = scopeId,
        Title = SkillsTexts.Scope(scope, scopeId),
        Decision = decision,
        EffectiveText = effective,
        CanToggle = canToggle,
        ToggleCommand = new AsyncRelayCommand(() => ToggleAsync(item, scope, scopeId, decision, canToggle)),
    };

    /// <summary>Appends the user's decision for that scope (the opposite of the current one) and reads the decisions again.</summary>
    private async Task ToggleAsync(SkillItemViewModel item, SkillScopeKind scope, string? scopeId, bool? decision, bool canToggle)
    {
        if (!canToggle || !CanActivate || Project is not { } project || _backend.Activations is not { } store || item.IsLinked)
        {
            return;
        }

        var activation = new SkillActivation(item.Skill.Entry.Id, scope, scope == SkillScopeKind.Global ? null : scopeId, !(decision ?? false));
        try
        {
            await store.AppendAsync(project.ProjectId, activation, DecidedBy, CancellationToken.None).ConfigureAwait(true);
            _activations = await store.ListAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(true);
            ActivationProblem = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActivationProblem = "No se pudo guardar la decisión: " + ex.Message;
        }

        RefreshActivationState();
        if (ReferenceEquals(_selected, item))
        {
            Detail = BuildDetail(item);
        }
    }

    private bool? LastDecision(SkillId skill, SkillScopeKind scope, string? scopeId) =>
        _activations.LastOrDefault(a => a.Skill == skill && a.Scope == scope && (scope == SkillScopeKind.Global || string.Equals(a.ScopeId, scopeId, StringComparison.Ordinal)))?.Enabled;

    /// <summary>
    /// What an agent gets and which scope decided it, with the precedence of <see cref="SkillActivationResolver"/> (the
    /// resolver gives the answer; this only names the scope).
    /// </summary>
    private string EffectiveText(SkillId skill, string agentId)
    {
        var projectId = Project?.ProjectId;
        var enabled = SkillActivationResolver.IsEnabled(skill, projectId, agentId, _activations);
        var source = LastDecision(skill, SkillScopeKind.Agent, agentId) is not null
            ? "por el agente"
            : projectId is not null && LastDecision(skill, SkillScopeKind.Project, projectId) is not null
                ? "por el proyecto"
                : LastDecision(skill, SkillScopeKind.Global, null) is not null ? "global" : null;
        return source is null ? "Sin activar (ninguna decisión)" : $"{(enabled ? "Activada" : "Desactivada")} ({source})";
    }

    /// <summary>The badges of the tree and the cost of each agent, after the decisions changed.</summary>
    private void RefreshActivationState()
    {
        var projectId = Project?.ProjectId;
        var agents = Agents();
        foreach (var item in _items)
        {
            item.Card = SkillCatalogSnapshotBuilder.Describe(item.Skill, projectId, agents, _activations, item.HealthStatus);
            var skill = item.Skill.Entry.Id;
            var forProject = SkillActivationResolver.IsEnabled(skill, projectId, null, _activations);
            item.ActiveBadge = forProject
                ? projectId is not null && LastDecision(skill, SkillScopeKind.Project, projectId) is not null ? "Proy." : "Global"
                : item.Card.ActiveAgents.Count switch
                {
                    0 => string.Empty,
                    1 => "1 agente",
                    var n => n.ToString(CultureInfo.InvariantCulture) + " agentes",
                };
        }

        AgentCosts.Clear();
        foreach (var agent in _roster)
        {
            var reaching = _items
                .Where(item => !item.IsLinked && item.Card.ActiveAgents.Contains(agent.Id.Value, StringComparer.Ordinal))
                .Select(item => item.Skill.Entry)
                .ToList();
            AgentCosts.Add(new AgentCostViewModel(agent.Id.Value, reaching.Count, SkillContextCost.Total(reaching)));
        }
    }

    // ---- the deterministic recommendation ----

    private bool HasTask() => !string.IsNullOrWhiteSpace(TaskText);

    /// <summary>The free recommendation: the task and the project's stack, scored by the domain recommender.</summary>
    [RelayCommand(CanExecute = nameof(HasTask))]
    private void Recommend()
    {
        if (!HasTask())
        {
            return;
        }

        var query = new SkillQuery(TaskText, Stack.ToHashSet(StringComparer.Ordinal), null);
        var catalog = _items.Select(item => item.Skill).ToList();
        var recommended = _backend.Recommendations?.Recommend(query, catalog)
            ?? SkillRecommender.Recommend(query, catalog.Select(skill => skill.Entry), SkillRecommendationService.DeterministicTop);
        Recommendations.Clear();
        foreach (var recommendation in recommended)
        {
            var id = recommendation.Skill.Id.Value;
            var points = recommendation.Score.ToString("0.#", CultureInfo.InvariantCulture);
            var unit = points == "1" ? "punto" : "puntos";
            Recommendations.Add(new SkillRecommendationItemViewModel(id, recommendation.Skill.Name, $"{points} {unit} · {string.Join(" · ", recommendation.Reasons)}", new RelayCommand(() => Show(id))));
        }

        RecommendationStatus = Recommendations.Count == 0
            ? "Ninguna skill coincide con la tarea ni con el stack del proyecto."
            : $"{Recommendations.Count} recomendadas, sin gastar cuota. Nada se ha activado.";
    }

    // ---- the assisted recommendation ----

    private bool CanAskAgent() =>
        IsAssistedAllowed
        && !IsAsking
        && Project is not null
        && _backend.Recommendations is not null
        && _backend.Approvals is not null
        && HasTask();

    /// <summary>
    /// Asks for the OK through the approval gate, which records it (<c>ApprovalRequested</c>, then <c>ApprovalGranted</c> or
    /// <c>ApprovalDenied</c>; docs/17 §0.2 and F13-20) as every approval, and, with it, asks the agent; shows its proposals and
    /// warnings. Never activates anything. It grants nothing, so it never asks Windows Hello (docs/17 D-37).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAskAgent))]
    private async Task AskAgentAsync()
    {
        if (!CanAskAgent() || Project is not { } project || _backend.Recommendations is not { } service || _backend.Approvals is not { } gate)
        {
            return;
        }

        IsAsking = true;
        try
        {
            AssistedStatus = "Esperando tu OK…";
            var question = new ApprovalRequest(AssistedAction, AssistedDescription, RiskLevel.Low, null, [], project.ProjectId);
            var answer = await gate.RequestAsync(question, CancellationToken.None).ConfigureAwait(true);
            if (!answer.Approved)
            {
                AssistedStatus = "No se pidió nada: no diste el OK, así que no se gastó cuota.";
                return;
            }

            AssistedStatus = "Preguntando al agente…";
            var consent = new QuotaConsent(answer.DecidedBy, answer.At, question.Description);
            var result = await service.AskAgentAsync(project, AssistedTask(), consent, CancellationToken.None).ConfigureAwait(true);

            AssistedRecommendations.Clear();
            foreach (var recommendation in result.Recommendations)
            {
                var id = recommendation.Skill.Value;
                var name = _items.FirstOrDefault(item => item.Id == id)?.Name ?? id;
                var scope = SkillsTexts.Scope(recommendation.Scope, recommendation.Agent?.Value);
                AssistedRecommendations.Add(new SkillRecommendationItemViewModel(id, name, $"{scope} · Razón del agente: {recommendation.Reason}", new RelayCommand(() => Show(id))));
            }

            AssistedWarnings.Clear();
            foreach (var warning in result.Warnings)
            {
                AssistedWarnings.Add(warning);
            }

            var run = result.Run is { } aux ? $" (ejecución {aux.Value})" : string.Empty;
            AssistedStatus = AssistedRecommendations.Count == 0
                ? $"El agente no propuso ninguna skill{run}."
                : $"El agente propuso {AssistedRecommendations.Count} {(AssistedRecommendations.Count == 1 ? "skill" : "skills")}{run}. Nada se ha activado: actívalas tú en su ficha si te convencen.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AssistedStatus = "No se pudo pedir la recomendación: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            AssistedStatus = "Se canceló la pregunta: no se pidió nada.";
        }
        finally
        {
            IsAsking = false;
        }
    }

    /// <summary>The task for the agent: the person's text and, when one was detected, the stack of the project (tags from file names).</summary>
    private string AssistedTask()
    {
        var task = TaskText.Trim();
        if (Stack.Count == 0)
        {
            return task;
        }

        var withStack = $"{task}\n\nContexto del proyecto (detectado por los nombres de sus ficheros): {string.Join(' ', Stack.Select(tag => "#" + tag))}";
        return withStack.Length <= SkillRecommendationService.MaxTaskLength ? withStack : task;
    }
}
