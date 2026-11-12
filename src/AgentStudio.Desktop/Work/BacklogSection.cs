using System.Collections.ObjectModel;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Backlog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>An item of the backlog in force, as the list shows it (docs/16 D-18).</summary>
public sealed record BacklogRow(BacklogEntry Entry)
{
    public BacklogItemId Id => Entry.Item.Id;

    public string Statement => Entry.Item.Statement;

    public string PriorityText => WorkTexts.Priority(Entry.Item.Priority);

    public string CriteriaText => AgentStudio.Desktop.Debate.DebateTexts.Plural(Entry.Item.Criteria.Count, "Work_Count_Criterion");

    public string VersionText => Texts.Format("Work_Backlog_Version", Entry.Item.Version);

    /// <summary>What a screen reader says of the row (A1-bis): the statement, never the members of the record.</summary>
    public override string ToString() => Statement;
}

/// <summary>One acceptance criterion of the item being written: what must hold and how it is checked.</summary>
public sealed partial class CriterionRowViewModel : ObservableObject
{
    internal CriterionRowViewModel(string id, string description, VerificationMethod method)
    {
        Id = id;
        _description = description;
        _selectedMethod = Methods.First(m => m.Value == method);
    }

    public string Id { get; }

    public static IReadOnlyList<Choice<VerificationMethod>> Methods { get; } =
        [.. Enum.GetValues<VerificationMethod>().Select(m => new Choice<VerificationMethod>(m, WorkTexts.Method(m)))];

    public IReadOnlyList<Choice<VerificationMethod>> MethodOptions => Methods;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private Choice<VerificationMethod> _selectedMethod;

    public AcceptanceCriterion ToCriterion() => new(Id, Description.Trim(), SelectedMethod.Value);

    public override string ToString() => string.IsNullOrWhiteSpace(Description) ? Id : Description;
}

/// <summary>
/// «Backlog» (docs/16 D-18, F9-2): the requirements the person notes for later. Each change is a new version with all its content; the
/// rows are append-only and anchored, so nothing is edited in place and nothing is deleted: a retired item leaves the list and its
/// history stays. The statement, the priority and the criteria are the person's.
/// </summary>
public sealed partial class BacklogSection(WorkContext context) : WorkSection(context, "Info_Work_Backlog")
{
    public ObservableCollection<BacklogRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    private BacklogRow? _selectedItem;

    public bool IsEditing => SelectedItem is not null;

    public string SaveLabel => Texts.Get(IsEditing ? "Work_Backlog_SaveVersion" : "Work_Backlog_Add");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _statement = string.Empty;

    public static IReadOnlyList<Choice<RequirementPriority>> Priorities { get; } =
        [.. Enum.GetValues<RequirementPriority>().Select(p => new Choice<RequirementPriority>(p, WorkTexts.Priority(p)))];

    [ObservableProperty]
    private Choice<RequirementPriority> _selectedPriority = Priorities[0];

    public ObservableCollection<CriterionRowViewModel> Criteria { get; } = [];

    public override async Task ReloadAsync()
    {
        Notice = Context.Backend.Backlog is null ? Texts.Get("Work_Notice_NoBacklog") : null;
        if (Context.Backend.Backlog is not { } backlog || Context.Project is not { } project)
        {
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            return;
        }

        var keep = SelectedItem?.Id;
        await GuardAsync(async () =>
        {
            var entries = await backlog.ListAsync(project, CancellationToken.None).ConfigureAwait(true);
            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(new BacklogRow(entry));
            }
        }).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasItems));
        SelectedItem = keep is { } id ? Items.FirstOrDefault(r => r.Id == id) : null;
    }

    protected override void ProjectChanged()
    {
        Items.Clear();
        SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
    }

    partial void OnSelectedItemChanged(BacklogRow? oldValue, BacklogRow? newValue)
    {
        // The same item read again (after a version was saved) keeps the message and the form the person has.
        if (oldValue is not null && newValue is not null && oldValue.Id == newValue.Id)
        {
            return;
        }

        Message = null;
        if (newValue is null)
        {
            return;
        }

        Statement = newValue.Entry.Item.Statement;
        SelectedPriority = Priorities.First(p => p.Value == newValue.Entry.Item.Priority);
        Criteria.Clear();
        foreach (var criterion in newValue.Entry.Item.Criteria)
        {
            Criteria.Add(new CriterionRowViewModel(criterion.Id, criterion.Description, criterion.Method));
        }
    }

    private void ClearForm()
    {
        Statement = string.Empty;
        SelectedPriority = Priorities[0];
        Criteria.Clear();
    }

    /// <summary>«Nuevo elemento»: leaves the item selected and empties the form.</summary>
    [RelayCommand]
    private void NewItem()
    {
        SelectedItem = null;
        ClearForm();
        Message = null;
    }

    [RelayCommand]
    private void AddCriterion()
    {
        var used = Criteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var next = Enumerable.Range(1, 1000).Select(n => "C" + n).First(id => !used.Contains(id));
        Criteria.Add(new CriterionRowViewModel(next, string.Empty, VerificationMethod.JudgeAssessment));
    }

    [RelayCommand]
    private void RemoveCriterion(CriterionRowViewModel? criterion)
    {
        if (criterion is not null)
        {
            Criteria.Remove(criterion);
        }
    }

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Statement) && Context.Backend.Backlog is not null && Context.Project is not null;

    /// <summary>«Anotar» a new item, or «Guardar versión nueva» of the one selected: a version with all its content, announced in the log.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Context.Backend.Backlog is not { } backlog || Context.Project is not { } project)
        {
            return;
        }

        var draft = new BacklogDraft(
            Statement.Trim(),
            SelectedPriority.Value,
            [.. Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Description)).Select(c => c.ToCriterion())]);
        var editing = SelectedItem?.Id;
        IsBusy = true;
        try
        {
            BacklogItem? saved = null;
            if (await GuardAsync(async () =>
            {
                saved = editing is { } id
                    ? await backlog.ReviseAsync(project, id, draft, Context.Person, CancellationToken.None).ConfigureAwait(true)
                    : await backlog.AddAsync(project, draft, Context.Person, CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true) && saved is not null)
            {
                Message = Texts.Format(editing is null ? "Work_Backlog_Added" : "Work_Backlog_Revised", saved.Version);
                if (editing is null)
                {
                    // The item is noted: the form is ready for the next one, not left to add the same twice.
                    ClearForm();
                }

                Context.NotifyWritten();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Retirar»: the item leaves the list; its versions stay in the history.</summary>
    [RelayCommand]
    private async Task RetireAsync()
    {
        if (Context.Backend.Backlog is not { } backlog || Context.Project is not { } project || SelectedItem?.Id is not { } id)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await GuardAsync(() => backlog.RetireAsync(project, id, Context.Person, null, CancellationToken.None)).ConfigureAwait(true))
            {
                NewItem();
                Message = Texts.Get("Work_Backlog_Retired");
                Context.NotifyWritten();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Ver cadena»: the chain of the requirement this item became, or the one it will be.</summary>
    [RelayCommand]
    private void ViewChain()
    {
        if (SelectedItem?.Id is { } id)
        {
            Context.LookAt(TraceRef.Requirement(id.Value));
        }
    }
}
