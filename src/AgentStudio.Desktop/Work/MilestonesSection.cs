using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>A milestone of the list: its name and the state its entries replay to.</summary>
public sealed record MilestoneRow(MilestoneView View)
{
    public string Id => View.Header.Id.Value;

    public string Name => View.Header.Name;

    public string StatusText => View.Problems.Count > 0 ? Texts.Get("Work_Status_Problems") : WorkTexts.Status(View.Status);

    public string TargetsText => Texts.Format("Work_Milestone_Targets", View.Targets.Count(t => t.Kind == MilestoneTargetKind.Run), View.Targets.Count(t => t.Kind == MilestoneTargetKind.Debate));

    /// <summary>What a screen reader says of the row (A1-bis): its name, never the members of the record.</summary>
    public override string ToString() => Name;
}

/// <summary>A report the milestone has: the revision, when, the audit that made it and the commit it audited. Reopening keeps every one.</summary>
public sealed record ReportRow(MilestoneReportReference Reference, string WhenText)
{
    public string Title => Texts.Format("Work_Report_Row", Reference.Revision, WhenText);

    public string AuditText => Texts.Format("Work_Report_Audit", Reference.Audit);

    public string CommitText => Reference.IntegratedCommit is { } commit ? Texts.Format("Work_Report_Commit", DebateTexts.Short(commit)) : Texts.Get("Work_Report_NoCommit");

    public override string ToString() => Title;
}

/// <summary>An item of the backlog that may go into the frozen requirements of a new milestone.</summary>
public sealed partial class BacklogChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    internal BacklogChoiceViewModel(BacklogEntry entry, Action changed)
    {
        Entry = entry;
        _changed = changed;
    }

    public BacklogEntry Entry { get; }

    public string Statement => Entry.Item.Statement;

    public string PriorityText => WorkTexts.Priority(Entry.Item.Priority);

    [ObservableProperty]
    private bool _isIncluded;

    partial void OnIsIncludedChanged(bool value) => _changed();

    public override string ToString() => Statement;
}

/// <summary>
/// «Hitos» (docs/16 D-17, D-49 and D-50; F9-1, F9-10): the milestones of the project. A milestone is a name, an objective and the
/// requirements frozen when it opens (the ones of the backlog the person picks), and optionally a budget, which is part of its header
/// and never changes. Its state is replayed from the verified log. Runs and debates are linked to it, and closing it is an audit: the
/// same approval and the same consent as «Auditar proyecto completo», a judge that did not write the code and a report that is the
/// same whoever reads it. Reopening keeps the report before.
/// </summary>
public sealed partial class MilestonesSection : WorkSection
{
    // True only while a reload swaps the lists: the list box (two-way binding) puts the selection to null when its items are cleared, and
    // that is not the person choosing something else.
    private bool _loading;
    private int _linkRead;

    // The milestone the person has selected, by identity: a reload gives new row objects and, with a binding, a null in between.
    private string? _selectedId;

    public MilestonesSection(WorkContext context)
        : base(context, "Info_Work_Milestones")
    {
        Authors = [.. new[] { VendorId.Anthropic, VendorId.OpenAI, VendorId.Google }.Select(v => new AuthorChoiceViewModel(v, () => { }))];
    }

    public ObservableCollection<MilestoneRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    [NotifyPropertyChangedFor(nameof(CanReopen))]
    private MilestoneRow? _selectedItem;

    public bool HasSelection => SelectedItem is not null;

    // ---- What the selected milestone is ----

    [ObservableProperty]
    private string _objectiveText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _budgetText = string.Empty;

    [ObservableProperty]
    private string _requirementsText = string.Empty;

    public ObservableCollection<string> Targets { get; } = [];

    public bool HasTargets => Targets.Count > 0;

    public ObservableCollection<ReportRow> Reports { get; } = [];

    public bool HasReports => Reports.Count > 0;

    /// <summary>What the replay of the milestone found wrong: a milestone with problems is shown as such and nothing more is written to it.</summary>
    public ObservableCollection<string> DetailProblems { get; } = [];

    public bool HasDetailProblems => DetailProblems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedReport))]
    private EvidenceViewer? _openedReport;

    public bool HasOpenedReport => OpenedReport is not null;

    // ---- Open a new milestone ----

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newObjective = string.Empty;

    [ObservableProperty]
    private string _newMaxTokens = string.Empty;

    [ObservableProperty]
    private string _newMaxRuns = string.Empty;

    public ObservableCollection<BacklogChoiceViewModel> BacklogChoices { get; } = [];

    public bool HasBacklogChoices => BacklogChoices.Count > 0;

    /// <summary>What is missing before «Abrir hito» can go on, in words; empty when it can.</summary>
    public ObservableCollection<string> OpenProblems { get; } = [];

    public bool HasOpenProblems => OpenProblems.Count > 0;

    partial void OnNewNameChanged(string value) => RefreshOpenProblems();

    partial void OnNewObjectiveChanged(string value) => RefreshOpenProblems();

    partial void OnNewMaxTokensChanged(string value) => RefreshOpenProblems();

    partial void OnNewMaxRunsChanged(string value) => RefreshOpenProblems();

    // ---- Link runs and debates ----

    public ObservableCollection<Choice<string>> RunChoices { get; } = [];

    [ObservableProperty]
    private Choice<string>? _selectedRun;

    public ObservableCollection<Choice<string>> DebateChoices { get; } = [];

    [ObservableProperty]
    private Choice<string>? _selectedDebate;

    // ---- Close and reopen ----

    /// <summary>The judges of the template: the one that closes the milestone cannot share a vendor with whoever wrote the code (rule P).</summary>
    public ObservableCollection<AgentChoice> Judges { get; } = [];

    [ObservableProperty]
    private AgentChoice? _selectedJudge;

    /// <summary>The vendors the person declares as authors of the code: the log does not know who wrote what Agent Studio did not.</summary>
    public IReadOnlyList<AuthorChoiceViewModel> Authors { get; }

    public bool CanClose => SelectedItem is { View: { Status: MilestoneStatus.Open, Problems.Count: 0 } } && Context.Backend.Closer is not null && !IsBusy;

    public bool CanReopen => SelectedItem is { View: { Status: MilestoneStatus.Closed, Problems.Count: 0 } } && Context.Backend.Milestones is not null && !IsBusy;

    [ObservableProperty]
    private string _reopenNote = string.Empty;

    [ObservableProperty]
    private string? _closeStatus;

    public override async Task ReloadAsync()
    {
        Notice = Context.Backend.Milestones is null ? Texts.Get("Work_Notice_NoMilestones") : null;
        if (Context.Backend.Milestones is not { } milestones || Context.Project is not { } project)
        {
            ProjectChanged();
            return;
        }

        // Everything is read aside and applied only if this is still the latest read: two overlapped reloads never both fill the lists.
        var read = BeginRead();
        var keep = _selectedId;
        var included = BacklogChoices.Where(c => c.IsIncluded).Select(c => c.Entry.Item.Id.Value).ToHashSet(StringComparer.Ordinal);
        var judgeId = SelectedJudge?.Agent.Id.Value;
        var rows = new List<MilestoneRow>();
        var entries = new List<BacklogEntry>();
        var judges = new List<AgentChoice>();
        var ok = await GuardAsync(async () =>
        {
            rows.AddRange((await milestones.ListAsync(project, CancellationToken.None).ConfigureAwait(true)).Select(view => new MilestoneRow(view)));
            if (Context.Backend.Backlog is { } backlog)
            {
                entries.AddRange(await backlog.ListAsync(project, CancellationToken.None).ConfigureAwait(true));
            }

            if (Context.Backend.Roster is { } roster)
            {
                judges.AddRange((await roster.ListAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(true)).Where(a => a.Role == AgentRole.Judge).OrderBy(a => a.Id.Value, StringComparer.Ordinal).Select(a => new AgentChoice(a)));
            }
        }).ConfigureAwait(true);
        if (!ok || !IsLatest(read))
        {
            return;
        }

        MilestoneRow? row;
        _loading = true;
        try
        {
            Items.Clear();
            foreach (var item in rows)
            {
                Items.Add(item);
            }

            BacklogChoices.Clear();
            foreach (var entry in entries)
            {
                BacklogChoices.Add(new BacklogChoiceViewModel(entry, RefreshOpenProblems) { IsIncluded = included.Contains(entry.Item.Id.Value) });
            }

            Judges.Clear();
            foreach (var judge in judges)
            {
                Judges.Add(judge);
            }

            SelectedJudge = Judges.FirstOrDefault(j => j.Agent.Id.Value == judgeId) ?? Judges.FirstOrDefault();
            row = keep is null ? null : Items.FirstOrDefault(r => r.Id == keep);
            SelectedItem = row;
        }
        finally
        {
            _loading = false;
        }

        _selectedId = row?.Id;
        if (keep is not null && row is null)
        {
            // The milestone the person had is gone: what was said about it goes with it.
            Message = null;
            CloseStatus = null;
            OpenedReport = null;
            Context.SelectMilestone(null);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasBacklogChoices));
        ShowSelected();
        RefreshOpenProblems();
        await GuardAsync(() => LoadLinkChoicesAsync(project)).ConfigureAwait(true);
    }

    protected override void ProjectChanged()
    {
        BeginRead();
        _linkRead++;
        _selectedId = null;
        Items.Clear();
        BacklogChoices.Clear();
        RunChoices.Clear();
        DebateChoices.Clear();
        Judges.Clear();
        SelectedItem = null;
        ShowSelected();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasBacklogChoices));
        RefreshOpenProblems();
    }

    partial void OnSelectedItemChanged(MilestoneRow? oldValue, MilestoneRow? newValue)
    {
        if (_loading)
        {
            return;
        }

        _selectedId = newValue?.Id;

        // The same milestone read again (after something was written) keeps what the person was told about it.
        var same = oldValue is not null && newValue is not null && oldValue.Id == newValue.Id;
        if (!same)
        {
            Message = null;
            CloseStatus = null;
            OpenedReport = null;
        }

        ShowSelected();
        if (!same)
        {
            Context.SelectMilestone(newValue is null ? null : newValue.View.Header.Id);
            _ = RefreshLinkChoicesAsync();
        }
    }

    /// <summary>Reads again the runs and debates that can still be linked to the milestone selected.</summary>
    public async Task RefreshLinkChoicesAsync()
    {
        if (Context.Project is { } project)
        {
            await GuardAsync(() => LoadLinkChoicesAsync(project)).ConfigureAwait(true);
        }
    }

    private async Task LoadLinkChoicesAsync(AgentStudio.Application.Projects.ProjectContext project)
    {
        var generation = ++_linkRead;
        var runChoices = new List<Choice<string>>();
        var debateChoices = new List<Choice<string>>();
        var linked = SelectedItem?.View.Targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        if (Context.Backend.Runs is { } runs)
        {
            // A follow-up comes with the run it follows, so only the first run of each chain is offered.
            foreach (var run in (await runs.ListAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(true)).Where(r => r.Follows is null && !linked.Contains(r.Id.Value)).OrderByDescending(r => r.CreatedAt))
            {
                runChoices.Add(new Choice<string>(run.Id.Value, $"{run.Id.Value} · {Context.LocalTime(run.CreatedAt)}"));
            }
        }

        if (Context.Backend.Debates is { } debates)
        {
            foreach (var debate in (await debates.ListAsync(project, CancellationToken.None).ConfigureAwait(true)).Where(d => !linked.Contains(d.Debate.Value)).OrderByDescending(d => d.OpenedAt))
            {
                var question = debate.Question is { Length: > 0 } q ? (q.Length > 60 ? q[..60] + "…" : q) : debate.Debate.Value;
                debateChoices.Add(new Choice<string>(debate.Debate.Value, $"{question} · {debate.Debate.Value}"));
            }
        }

        if (generation != _linkRead)
        {
            return;
        }

        RunChoices.Clear();
        foreach (var run in runChoices)
        {
            RunChoices.Add(run);
        }

        DebateChoices.Clear();
        foreach (var debate in debateChoices)
        {
            DebateChoices.Add(debate);
        }

        SelectedRun = RunChoices.FirstOrDefault();
        SelectedDebate = DebateChoices.FirstOrDefault();
    }

    private void ShowSelected()
    {
        Targets.Clear();
        Reports.Clear();
        DetailProblems.Clear();
        if (SelectedItem?.View is not { } view)
        {
            ObjectiveText = string.Empty;
            StatusText = string.Empty;
            BudgetText = string.Empty;
            RequirementsText = string.Empty;
        }
        else
        {
            ObjectiveText = view.Header.Objective;
            StatusText = SelectedItem!.StatusText;
            BudgetText = BudgetLine(view.Header.Budget);
            RequirementsText = Texts.Format("Work_Milestone_Requirements", view.RequirementSetHistory.Count);
            foreach (var target in view.Targets)
            {
                Targets.Add(Texts.Format(target.Kind == MilestoneTargetKind.Run ? "Work_Milestone_TargetRun" : "Work_Milestone_TargetDebate", target.Id));
            }

            foreach (var report in view.Reports)
            {
                Reports.Add(new ReportRow(report, Context.LocalTime(report.At)));
            }

            foreach (var problem in view.Problems)
            {
                DetailProblems.Add(Texts.Format("Work_Milestone_Problem", problem));
            }
        }

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasReports));
        OnPropertyChanged(nameof(HasDetailProblems));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CanReopen));
    }

    /// <summary>The budget in words: «Sin presupuesto», or its limits and the period.</summary>
    public static string BudgetLine(WorkBudget? budget)
    {
        if (budget is null)
        {
            return Texts.Get("Work_Budget_None");
        }

        var parts = new List<string>();
        if (budget.MaxTokens is { } tokens)
        {
            parts.Add(Texts.Format("Work_Budget_Tokens", tokens.ToString("N0", Texts.Current.Culture)));
        }

        if (budget.MaxAgentRuns is { } runs)
        {
            parts.Add(Texts.Format("Work_Budget_Runs", runs));
        }

        return Texts.Format("Work_Budget_Line", string.Join(", ", parts), WorkTexts.Period(budget.Period));
    }

    // ---- Open ----

    private void RefreshOpenProblems()
    {
        OpenProblems.Clear();
        foreach (var problem in OpenProblemList())
        {
            OpenProblems.Add(problem);
        }

        OnPropertyChanged(nameof(HasOpenProblems));
        OpenCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<string> OpenProblemList()
    {
        if (Context.Project is null)
        {
            yield return Texts.Get("Work_Problem_NoProject");
            yield break;
        }

        if (Context.Backend.Milestones is null)
        {
            yield return Texts.Get("Work_Notice_NoMilestones");
        }

        if (string.IsNullOrWhiteSpace(NewName))
        {
            yield return Texts.Get("Work_Problem_NoName");
        }

        if (string.IsNullOrWhiteSpace(NewObjective))
        {
            yield return Texts.Get("Work_Problem_NoObjective");
        }

        var included = BacklogChoices.Where(c => c.IsIncluded).ToList();
        if (included.Count == 0)
        {
            yield return Texts.Get("Work_Problem_NoRequirements");
        }

        foreach (var choice in included.Where(c => c.Entry.Item.Priority == RequirementPriority.Mandatory && c.Entry.Item.Criteria.Count == 0))
        {
            yield return Texts.Format("Work_Problem_NeedsCriteria", choice.Statement);
        }

        if (!TryParseLimit(NewMaxTokens, out _))
        {
            yield return Texts.Get("Work_Problem_BadTokens");
        }

        if (!TryParseLimit(NewMaxRuns, out var runs) || runs > int.MaxValue)
        {
            yield return Texts.Get("Work_Problem_BadRuns");
        }
    }

    private static bool TryParseLimit(string text, out long? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private bool CanOpen() => !IsBusy && OpenProblems.Count == 0 && Context.Backend.Milestones is not null && Context.Project is not null;

    /// <summary>«Abrir hito»: freezes the requirements picked, stores the header and records <c>MilestoneOpened</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (Context.Backend.Milestones is not { } milestones || Context.Project is not { } project || OpenProblemList().Any())
        {
            return;
        }

        TryParseLimit(NewMaxTokens, out var tokens);
        TryParseLimit(NewMaxRuns, out var runs);
        WorkBudget? budget = tokens is null && runs is null ? null : new WorkBudget(tokens, (int?)runs, BudgetPeriod.Total);
        IsBusy = true;
        try
        {
            MilestoneView? opened = null;
            if (await GuardAsync(async () =>
            {
                var requirements = FrozenRequirementSet.Freeze(
                    BacklogChoices.Where(c => c.IsIncluded).Select(c => c.Entry.Item.ToRequirement(new RequirementId(c.Entry.Item.Id.Value))),
                    Context.Time.GetUtcNow());
                opened = await milestones.OpenAsync(new OpenMilestoneRequest(project, NewName.Trim(), NewObjective.Trim(), requirements, budget, Context.Person), CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true) && opened is not null)
            {
                Message = Texts.Format("Work_Milestone_Opened", opened.Header.Name);
                NewName = string.Empty;
                NewObjective = string.Empty;
                NewMaxTokens = string.Empty;
                NewMaxRuns = string.Empty;
                foreach (var choice in BacklogChoices)
                {
                    choice.IsIncluded = false;
                }

                Context.NotifyWritten();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Link ----

    [RelayCommand]
    private Task LinkRunAsync() => LinkAsync(SelectedRun, MilestoneTargetKind.Run);

    [RelayCommand]
    private Task LinkDebateAsync() => LinkAsync(SelectedDebate, MilestoneTargetKind.Debate);

    private async Task LinkAsync(Choice<string>? choice, MilestoneTargetKind kind)
    {
        if (IsBusy || Context.Backend.Milestones is not { } milestones || Context.Project is not { } project || SelectedItem is not { } row || choice is null)
        {
            // A second click while the first link is being written would send the same link twice.
            return;
        }

        IsBusy = true;
        try
        {
            if (await GuardAsync(() => milestones.LinkAsync(project, row.View.Header.Id, new MilestoneTarget(kind, choice.Value), Context.Person, CancellationToken.None)).ConfigureAwait(true))
            {
                Message = Texts.Get(kind == MilestoneTargetKind.Run ? "Work_Milestone_RunLinked" : "Work_Milestone_DebateLinked");
                Context.NotifyWritten();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Close and reopen ----

    /// <summary>
    /// «Cerrar hito»: closing is auditing it. The audit asks the person's approval and the consent for the quota, as «Auditar proyecto
    /// completo» does, with a judge that did not write the code; the report is built from the log and kept as a saved proof. A close
    /// that was refused says which rule refused it, and nothing was written as a close.
    /// </summary>
    [RelayCommand]
    private async Task CloseMilestoneAsync()
    {
        if (!CanClose || Context.Backend.Closer is not { } closer || Context.Project is not { } project || SelectedItem is not { } row)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Message = null;
        CloseStatus = Texts.Get("Work_Close_Waiting");
        OnPropertyChanged(nameof(CanClose));
        try
        {
            var request = new CloseMilestoneRequest(project, row.View.Header.Id, Context.Person, SelectedJudge?.Agent.Id, [.. Authors.Where(a => a.IsDeclared).Select(a => a.Vendor)]);
            try
            {
                var closed = await closer.CloseAsync(request, null, CancellationToken.None).ConfigureAwait(true);
                Message = Texts.Format("Work_Close_Done", closed.Reports.Count);
                Context.NotifyWritten();
            }
            catch (MilestoneCloseRefusedException refused)
            {
                // Not an error of the app: the rules of a close said no, and nothing was closed.
                Error = Texts.Format("Work_Close_Refused", WorkTexts.Refusal(refused.Rule));
            }
#pragma warning disable CA1031 // The close is behind a port: a failure is shown, never thrown into the dispatcher.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Error = Texts.Format("Work_Error", ex.Message);
            }
        }
        finally
        {
            CloseStatus = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanClose));
            OnPropertyChanged(nameof(CanReopen));
        }
    }

    /// <summary>«Reabrir»: a closed milestone is reopened by the person; the report before stays among its reports.</summary>
    [RelayCommand]
    private async Task ReopenAsync()
    {
        if (!CanReopen || Context.Backend.Milestones is not { } milestones || Context.Project is not { } project || SelectedItem is not { } row)
        {
            return;
        }

        var note = string.IsNullOrWhiteSpace(ReopenNote) ? null : ReopenNote.Trim();
        IsBusy = true;
        try
        {
            if (await GuardAsync(() => milestones.ReopenAsync(project, row.View.Header.Id, MilestoneReopenReason.ByPerson, null, Context.Person, note, CancellationToken.None)).ConfigureAwait(true))
            {
                ReopenNote = string.Empty;
                Message = Texts.Get("Work_Milestone_Reopened");
                Context.NotifyWritten();
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanClose));
            OnPropertyChanged(nameof(CanReopen));
        }
    }

    /// <summary>«Ver informe»: the report of a close, read from the saved proofs and compared with its fingerprint.</summary>
    [RelayCommand]
    private async Task OpenReportAsync(ReportRow? report)
    {
        if (report is null || Context.Backend.Evidence is not { } store || Context.Project is not { } project)
        {
            Error = report is null ? null : Texts.Get("Work_Notice_NoEvidence");
            return;
        }

        await GuardAsync(async () => OpenedReport = await SavedProofReader.ReadAsync(store, project, report.Reference.Report.Value, CancellationToken.None).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseReport() => OpenedReport = null;

    /// <summary>«Ver cadena»: the chain from the milestone to its delivery.</summary>
    [RelayCommand]
    private void ViewChain()
    {
        if (SelectedItem is { } row)
        {
            Context.LookAt(TraceRef.Milestone(row.Id));
        }
    }
}
