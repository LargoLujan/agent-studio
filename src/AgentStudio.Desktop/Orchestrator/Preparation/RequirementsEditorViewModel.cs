using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>One acceptance criterion of a requirement being written.</summary>
public sealed partial class CriterionDraftViewModel : ObservableObject
{
    private readonly Action _changed;

    internal CriterionDraftViewModel(AcceptanceCriterion criterion, Action changed)
    {
        _id = criterion.Id;
        _description = criterion.Description;
        Method = criterion.Method;
        _selectedMethod = Methods.First(o => o.Value == criterion.Method);
        _changed = changed;
    }

    /// <summary>How a criterion is checked (only the first two give objective evidence: docs/07).</summary>
    public static IReadOnlyList<Choice<VerificationMethod>> Methods { get; } =
    [
        new(VerificationMethod.AutomatedTest, "Prueba automática"),
        new(VerificationMethod.CommandCheck, "Comprobación con un comando"),
        new(VerificationMethod.HumanReview, "Revisión de una persona"),
        new(VerificationMethod.JudgeAssessment, "Valoración del juez"),
    ];

    public IReadOnlyList<Choice<VerificationMethod>> MethodOptions => Methods;

    /// <summary>The method chosen last; the combo box can push null while it is torn down, which is never a choice.</summary>
    public VerificationMethod Method { get; private set; }

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private Choice<VerificationMethod> _selectedMethod;

    partial void OnIdChanged(string value) => _changed();

    partial void OnDescriptionChanged(string value) => _changed();

    partial void OnSelectedMethodChanged(Choice<VerificationMethod> value)
    {
        if (value is not null)
        {
            Method = value.Value;
            _changed();
        }
    }

    internal AcceptanceCriterion ToDomain() => new(Id.Trim(), Description.Trim(), Method);
}

/// <summary>One requirement being written: its id, its literal text, whether it is mandatory and its acceptance criteria.</summary>
public sealed partial class RequirementDraftViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Func<bool> _canEdit;

    internal RequirementDraftViewModel(Requirement requirement, Func<bool> canEdit, Action changed)
    {
        _id = requirement.Id.Value;
        _statement = requirement.Statement;
        _isMandatory = requirement.Priority == RequirementPriority.Mandatory;
        _canEdit = canEdit;
        _changed = changed;
        Criteria = [.. requirement.Criteria.Select(c => new CriterionDraftViewModel(c, changed))];
        Criteria.CollectionChanged += (_, _) => _changed();
    }

    public ObservableCollection<CriterionDraftViewModel> Criteria { get; }

    [ObservableProperty]
    private string _id;

    /// <summary>The literal text of the requirement: what the agents receive and the judge checks.</summary>
    [ObservableProperty]
    private string _statement;

    [ObservableProperty]
    private bool _isMandatory;

    partial void OnIdChanged(string value) => _changed();

    partial void OnStatementChanged(string value) => _changed();

    partial void OnIsMandatoryChanged(bool value) => _changed();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddCriterion()
    {
        var used = Criteria.Select(c => c.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var next = Enumerable.Range(1, Criteria.Count + 1).Select(n => "c" + n.ToString(CultureInfo.InvariantCulture)).First(id => !used.Contains(id));
        Criteria.Add(new CriterionDraftViewModel(new AcceptanceCriterion(next, string.Empty, VerificationMethod.AutomatedTest), _changed));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveCriterion(CriterionDraftViewModel? criterion)
    {
        if (criterion is not null)
        {
            Criteria.Remove(criterion);
        }
    }

    private bool CanEdit() => _canEdit();

    internal void RefreshCommands()
    {
        AddCriterionCommand.NotifyCanExecuteChanged();
        RemoveCriterionCommand.NotifyCanExecuteChanged();
    }

    internal Requirement ToDomain() => new(
        new RequirementId(Id.Trim()),
        Statement.Trim(),
        IsMandatory ? RequirementPriority.Mandatory : RequirementPriority.Desirable,
        [.. Criteria.Select(c => c.ToDomain())]);
}

/// <summary>
/// The «Requisitos» panel of the orchestrator (F3-8, step 1): the user writes the requirements and freezes them through
/// the requirement store. A frozen set never changes: «Editar» opens a draft of a new version, and the frozen set stays
/// the current one until the draft is frozen, which stores a new set with its own hash. Planning waits for a frozen set
/// and for any draft to be frozen or discarded (<see cref="HasPendingChanges"/>).
/// </summary>
public sealed partial class RequirementsEditorViewModel : ObservableObject
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly OrchestrationSession _session;
    private readonly TimeProvider _time;
    private readonly Func<FrozenRequirementSet, Task>? _frozenHandler;
    private ProjectContext? _project;
    private bool _loaded;
    private bool _rebuilding;

    /// <param name="session">Where the frozen sets are stored.</param>
    /// <param name="time">The clock of the freezing time.</param>
    /// <param name="frozen">Awaited after a new version was stored, with that version (the screen reloads its context).</param>
    public RequirementsEditorViewModel(OrchestrationSession session, TimeProvider time, Func<FrozenRequirementSet, Task>? frozen = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _time = time;
        _frozenHandler = frozen;
        _isEditing = true;
        Drafts.CollectionChanged += (_, _) => OnDraftChanged();
        Refresh();
    }

    /// <summary>The requirements on screen: a copy of the frozen set, or the draft of a new version while <see cref="IsEditing"/>.</summary>
    public ObservableCollection<RequirementDraftViewModel> Drafts { get; } = [];

    /// <summary>The project's current frozen set. It is never modified: freezing a draft replaces it with a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrozenHash))]
    [NotifyPropertyChangedFor(nameof(HasFrozen))]
    private FrozenRequirementSet? _frozen;

    public string? FrozenHash => Frozen?.ContentHash;

    public bool HasFrozen => Frozen is not null;

    /// <summary>True while a draft is open; false while the frozen set is shown, read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    private bool _isEditing;

    public bool IsReadOnly => !IsEditing;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Why the draft cannot be frozen, one Spanish line each; empty when it can.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => IsEditing && Problems.Count > 0;

    /// <summary>A draft differs from the frozen set (or there is a draft and nothing frozen yet): planning waits for it.</summary>
    public bool HasPendingChanges { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _notice;

    /// <summary>
    /// Shows the project's current version. A draft with changes is kept when the same project and version come again (a
    /// reload of the screen); otherwise the frozen set replaces it, and with nothing frozen an empty draft is open.
    /// </summary>
    public void Load(ProjectContext? project, FrozenRequirementSet? frozen)
    {
        var sameProject = Equals(project, _project);
        var sameVersion = string.Equals(frozen?.ContentHash, Frozen?.ContentHash, StringComparison.Ordinal);
        _project = project;
        Frozen = frozen;
        if (_loaded && sameProject && sameVersion && HasPendingChanges)
        {
            Refresh();
            return;
        }

        _loaded = true;
        ErrorMessage = null;
        Notice = null;
        ShowFrozen();
    }

    [RelayCommand(CanExecute = nameof(CanChangeDraft))]
    private void AddRequirement()
    {
        var used = Drafts.Select(d => d.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var next = Enumerable.Range(1, Drafts.Count + 1).Select(n => "RQ-" + n.ToString("00", CultureInfo.InvariantCulture)).First(id => !used.Contains(id));
        Drafts.Add(DraftOf(new Requirement(
            new RequirementId(next),
            string.Empty,
            RequirementPriority.Mandatory,
            [new AcceptanceCriterion("c1", string.Empty, VerificationMethod.AutomatedTest)])));
    }

    [RelayCommand(CanExecute = nameof(CanChangeDraft))]
    private void RemoveRequirement(RequirementDraftViewModel? draft)
    {
        if (draft is not null)
        {
            Drafts.Remove(draft);
        }
    }

    private bool CanChangeDraft() => IsEditing && !IsBusy;

    /// <summary>«Editar · nueva versión»: a draft of the frozen set, which stays current until the draft is frozen.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        ErrorMessage = null;
        Notice = null;
        IsEditing = true;
        Refresh();
    }

    private bool CanEdit() => !IsEditing && !IsBusy;

    /// <summary>«Descartar cambios»: back to the frozen set (or to an empty draft when nothing is frozen).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        ErrorMessage = null;
        Notice = null;
        ShowFrozen();
    }

    private bool CanDiscard() => IsEditing && !IsBusy && (Frozen is not null || Drafts.Count > 0);

    /// <summary>«Congelar requisitos»: stores the draft as the project's new version (or says nothing changed).</summary>
    [RelayCommand(CanExecute = nameof(CanFreeze))]
    private async Task FreezeAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Notice = null;
        FrozenRequirementSet? stored = null;
        try
        {
            var requirements = Drafts.Select(d => d.ToDomain()).ToList();
            var result = await _session.FreezeRequirementsAsync(project, requirements, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case FreezeOutcome.Frozen:
                    Frozen = result.Set;
                    ShowFrozen();
                    stored = result.Set;
                    break;
                case FreezeOutcome.Unchanged:
                    Frozen = result.Set;
                    ShowFrozen();
                    Notice = "Sin cambios: siguen congelados los mismos requisitos.";
                    break;
                default:
                    ErrorMessage = $"Ese contenido es el de una versión anterior ({Short(result.Set.ContentHash)}), y una versión congelada no vuelve a ser la vigente: cambia algo para congelar una versión nueva.";
                    break;
            }
        }
        catch (DomainRuleViolationException ex)
        {
            ErrorMessage = $"Los requisitos no se pueden congelar: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The store is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudieron congelar los requisitos: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }

        // Outside the store's error handling: the version is stored whatever the screen does with it.
        if (stored is not null && _frozenHandler is not null)
        {
            await _frozenHandler(stored).ConfigureAwait(true);
        }
    }

    private bool CanFreeze() => IsEditing && !IsBusy && _project is not null && _session.CanStoreRequirements && Problems.Count == 0;

    partial void OnIsBusyChanged(bool value) => Refresh();

    private RequirementDraftViewModel DraftOf(Requirement requirement) => new(requirement, CanChangeDraft, OnDraftChanged);

    private void ShowFrozen()
    {
        _rebuilding = true;
        try
        {
            Drafts.Clear();
            foreach (var requirement in Frozen?.Requirements ?? [])
            {
                Drafts.Add(DraftOf(requirement));
            }

            IsEditing = Frozen is null;
        }
        finally
        {
            _rebuilding = false;
        }

        Refresh();
    }

    private void OnDraftChanged()
    {
        if (!_rebuilding)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        Problems = Validate();
        HasPendingChanges = IsEditing && !MatchesFrozen();
        StatusText = Status();
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(StatusText));
        AddRequirementCommand.NotifyCanExecuteChanged();
        RemoveRequirementCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        FreezeCommand.NotifyCanExecuteChanged();
        foreach (var draft in Drafts)
        {
            draft.RefreshCommands();
        }
    }

    /// <summary>True when the drafts hold exactly the frozen content (compared by the hash the domain computes).</summary>
    private bool MatchesFrozen()
    {
        if (Frozen is null)
        {
            return Drafts.Count == 0;
        }

        try
        {
            var candidate = FrozenRequirementSet.Freeze([.. Drafts.Select(d => d.ToDomain())], Frozen.FrozenAt);
            return string.Equals(candidate.ContentHash, Frozen.ContentHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
        {
            return false;
        }
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (Drafts.Count == 0)
        {
            problems.Add("Añade al menos un requisito.");
            return problems;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Drafts.Count; i++)
        {
            var draft = Drafts[i];
            var id = draft.Id.Trim();
            var name = id.Length == 0 ? $"n.º {i + 1}" : id;
            if (id.Length == 0)
            {
                problems.Add($"El requisito n.º {i + 1} no tiene identificador.");
            }
            else if (!IdPattern.IsMatch(id))
            {
                problems.Add($"El identificador «{id}» solo admite letras, números, «.», «-» y «_», sin espacios y con 32 caracteres como mucho.");
            }
            else if (!ids.Add(id))
            {
                problems.Add($"El identificador {id} está repetido.");
            }

            if (string.IsNullOrWhiteSpace(draft.Statement))
            {
                problems.Add($"El requisito {name} no tiene texto.");
            }

            if (draft.IsMandatory && draft.Criteria.Count == 0)
            {
                problems.Add($"El requisito obligatorio {name} necesita al menos un criterio de aceptación.");
            }

            var criteria = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < draft.Criteria.Count; j++)
            {
                var criterion = draft.Criteria[j];
                var criterionId = criterion.Id.Trim();
                if (criterionId.Length == 0)
                {
                    problems.Add($"El criterio n.º {j + 1} de {name} no tiene identificador.");
                }
                else if (!criteria.Add(criterionId))
                {
                    problems.Add($"El criterio {criterionId} de {name} está repetido.");
                }

                if (string.IsNullOrWhiteSpace(criterion.Description))
                {
                    problems.Add($"El criterio {(criterionId.Length == 0 ? $"n.º {j + 1}" : criterionId)} de {name} no tiene descripción.");
                }
            }
        }

        return problems;
    }

    private string Status()
    {
        if (_project is null)
        {
            return "Abre el proyecto para congelar sus requisitos.";
        }

        if (!_session.CanStoreRequirements)
        {
            return "Falta el almacén de requisitos: no se pueden congelar.";
        }

        if (Frozen is not { } frozen)
        {
            return "Sin requisitos congelados: escríbelos y congélalos para poder planificar.";
        }

        if (IsEditing)
        {
            return $"Versión nueva sin congelar: sigue vigente la congelada (hash {Short(frozen.ContentHash)}).";
        }

        var mandatory = frozen.Mandatory.Count();
        var when = TimeZoneInfo.ConvertTime(frozen.FrozenAt, _time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        return $"Congelados el {when}: {Count(frozen.Requirements.Count, "requisito", "requisitos")} ({Count(mandatory, "obligatorio", "obligatorios")}) · hash {Short(frozen.ContentHash)}";
    }

    private static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
