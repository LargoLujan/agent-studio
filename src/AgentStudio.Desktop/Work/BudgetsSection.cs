using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>The budget a milestone was opened with, as its header keeps it: it never changes afterwards.</summary>
public sealed record MilestoneBudgetRow(string Name, string BudgetText)
{
    public override string ToString() => Name;
}

/// <summary>
/// «Presupuestos» (docs/16 D-05, F9-8; ADR-0040): the optional budget of the project, which the person sets and saves on this computer,
/// and the budgets of the milestones, read from their headers. The engine does not launch an attempt that would pass a limit, and says
/// so. A budget shares out the person's own use of their plan; it never widens the usage limit of the plan.
/// </summary>
public sealed partial class BudgetsSection : WorkSection
{
    public BudgetsSection(WorkContext context)
        : base(context, "Info_Work_Budgets")
    {
    }

    public static IReadOnlyList<Choice<BudgetPeriod>> Periods { get; } =
        [.. Enum.GetValues<BudgetPeriod>().Select(p => new Choice<BudgetPeriod>(p, WorkTexts.Period(p)))];

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string _maxTokens = string.Empty;

    [ObservableProperty]
    private string _maxRuns = string.Empty;

    [ObservableProperty]
    private Choice<BudgetPeriod> _selectedPeriod = Periods.First(p => p.Value == BudgetPeriod.Month);

    /// <summary>What is wrong with the budget typed, in words; empty when it can be saved.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public ObservableCollection<MilestoneBudgetRow> Milestones { get; } = [];

    public bool HasMilestones => Milestones.Count > 0;

    // What the person typed and has not saved: a reload (any write to the log makes every section read again) must not overwrite it.
    private bool _dirty;
    private bool _filling;

    partial void OnMaxTokensChanged(string value) => Edited();

    partial void OnMaxRunsChanged(string value) => Edited();

    partial void OnSelectedPeriodChanged(Choice<BudgetPeriod> value) => Edited();

    private void Edited()
    {
        _dirty |= !_filling;
        Refresh();
    }

    private void Fill(string maxTokens, string maxRuns, Choice<BudgetPeriod> period)
    {
        _filling = true;
        try
        {
            MaxTokens = maxTokens;
            MaxRuns = maxRuns;
            SelectedPeriod = period;
        }
        finally
        {
            _filling = false;
        }

        _dirty = false;
    }

    public override async Task ReloadAsync()
    {
        Notice = Context.Backend.Budgets is null ? Texts.Get("Work_Notice_NoBudgets") : null;
        Milestones.Clear();
        CurrentText = Texts.Get("Work_Budget_None");
        if (Context.Project is not { } project)
        {
            OnPropertyChanged(nameof(HasMilestones));
            Refresh();
            return;
        }

        await GuardAsync(async () =>
        {
            if (Context.Backend.Budgets is { } store)
            {
                WorkBudget? budget;
                try
                {
                    budget = await store.LoadAsync(project, CancellationToken.None).ConfigureAwait(true);
                }
                catch
                {
                    // The engine stops when the file cannot be read: the screen must not say "no budget" meanwhile.
                    CurrentText = Texts.Get("Work_Budget_Unreadable");
                    throw;
                }

                CurrentText = budget is null ? Texts.Get("Work_Budget_None") : MilestonesSection.BudgetLine(budget);
                if (!_dirty)
                {
                    Fill(
                        budget?.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        budget?.MaxAgentRuns?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        Periods.First(p => p.Value == (budget?.Period ?? BudgetPeriod.Month)));
                }
            }

            if (Context.Backend.Milestones is { } milestones)
            {
                foreach (var view in await milestones.ListAsync(project, CancellationToken.None).ConfigureAwait(true))
                {
                    Milestones.Add(new MilestoneBudgetRow(view.Header.Name, MilestonesSection.BudgetLine(view.Header.Budget)));
                }
            }
        }, "Work_Error_BudgetRead").ConfigureAwait(true);
        OnPropertyChanged(nameof(HasMilestones));
        Refresh();
    }

    protected override void ProjectChanged()
    {
        Milestones.Clear();
        CurrentText = Texts.Get("Work_Budget_None");
        Fill(string.Empty, string.Empty, SelectedPeriod);
        OnPropertyChanged(nameof(HasMilestones));
        Refresh();
    }

    private void Refresh()
    {
        Problems.Clear();
        if (Context.Project is null)
        {
            Problems.Add(Texts.Get("Work_Problem_NoProject"));
        }
        else if (Context.Backend.Budgets is null)
        {
            Problems.Add(Texts.Get("Work_Notice_NoBudgets"));
        }
        else if (Parse(out var budget) is { } problem)
        {
            Problems.Add(problem);
        }
        else if (budget is null)
        {
            Problems.Add(Texts.Get("Work_Problem_NoLimit"));
        }

        OnPropertyChanged(nameof(HasProblems));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private string? Parse(out WorkBudget? budget)
    {
        budget = null;
        long? tokens = null;
        int? runs = null;
        if (!string.IsNullOrWhiteSpace(MaxTokens))
        {
            if (!long.TryParse(MaxTokens.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t <= 0)
            {
                return Texts.Get("Work_Problem_BadTokens");
            }

            tokens = t;
        }

        if (!string.IsNullOrWhiteSpace(MaxRuns))
        {
            if (!int.TryParse(MaxRuns.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r <= 0)
            {
                return Texts.Get("Work_Problem_BadRuns");
            }

            runs = r;
        }

        budget = tokens is null && runs is null ? null : new WorkBudget(tokens, runs, SelectedPeriod.Value);
        return null;
    }

    private bool CanSave() => !IsBusy && Problems.Count == 0;

    /// <summary>«Guardar»: the budget of the project, kept on this computer. The engine reads it before launching an attempt.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Context.Backend.Budgets is not { } store || Context.Project is not { } project || Parse(out var budget) is not null || budget is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await GuardAsync(() => store.SaveAsync(project, budget, CancellationToken.None), "Work_Error_BudgetSave").ConfigureAwait(true))
            {
                _dirty = false;
                Message = Texts.Get("Work_Budget_Saved");
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Quitar presupuesto»: the project goes back to having no limit of its own.</summary>
    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Context.Backend.Budgets is not { } store || Context.Project is not { } project)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await GuardAsync(() => store.SaveAsync(project, null, CancellationToken.None), "Work_Error_BudgetSave").ConfigureAwait(true))
            {
                Message = Texts.Get("Work_Budget_Removed");
                Fill(string.Empty, string.Empty, SelectedPeriod);
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
