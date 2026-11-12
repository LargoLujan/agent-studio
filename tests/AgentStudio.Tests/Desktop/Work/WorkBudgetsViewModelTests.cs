using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Usage;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Observability.Usage;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>The usage the gate reads: the attempts a test gives, counted as the panel counts them.</summary>
internal sealed class AttemptsUsage(params UsageAttempt[] attempts) : IUsageReport
{
    public Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct) =>
        Task.FromResult(new UsageReport(UsageAggregator.Select(attempts, query), [], [], new UsageReconciliation(null, attempts.Length, [])));
}

/// <summary>
/// docs/16 D-05 and F9-8 (ADR-0040) on the screen: the budget of the project is saved on this computer, the gate of the engine reads the same
/// one and refuses an attempt that would pass it, a file that cannot be read stops the engine instead of lifting the limit, and the budget
/// of a milestone is shown as its header keeps it.
/// </summary>
public sealed class WorkBudgetsViewModelTests
{
    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static UsageEstimateRequest Request(WorkTestWorld test) => new(test.World.Project, new AgentId("ejecutor"), ProviderId.ClaudeCode, "claude-sonnet-5-5", AgentRunPurpose.Executor);

    private static ProjectBudgetGate Gate(WorkTestWorld test, params UsageAttempt[] attempts) =>
        new(test.Budgets, new AttemptsUsage(attempts), null, null, null, test.Time);

    [Fact]
    public async Task A_budget_that_is_saved_is_the_one_the_gate_reads_and_it_refuses_the_attempt_that_would_pass_it()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var budgets = test.Document.Budgets;
        budgets.MaxRuns = "2";
        Assert.False(budgets.HasProblems);
        await budgets.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Texts.Get("Work_Budget_Saved"), budgets.Message);
        Assert.Equal(new WorkBudget(null, 2, BudgetPeriod.Month), await test.Budgets.LoadAsync(test.World.Project, Ct));
        Assert.Equal(MilestonesSection.BudgetLine(new WorkBudget(null, 2, BudgetPeriod.Month)), budgets.CurrentText);

        // Two attempts in the month and a third about to launch: it passes the limit of two, and the gate says so.
        var attempts = new[] { UsageAttempts.Make(test.Time.GetUtcNow().AddHours(-2)), UsageAttempts.Make(test.Time.GetUtcNow().AddHours(-1)) };
        var refused = await Gate(test, attempts).CheckAsync(test.World.Project, "R1", Request(test), 0, Ct);
        Assert.False(refused.Allowed);
        Assert.Equal(ProjectBudgetGate.ExceededCode, refused.Code);
        Assert.Equal(BudgetLimit.AgentRuns, refused.Reached);

        // With one attempt only, the second launch is within the limit.
        Assert.True((await Gate(test, attempts[0]).CheckAsync(test.World.Project, "R1", Request(test), 0, Ct)).Allowed);
    }

    [Fact]
    public async Task Removing_the_budget_lifts_the_limit_and_a_new_screen_reads_what_was_saved()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await test.Budgets.SaveAsync(test.World.Project, new WorkBudget(5_000, null, BudgetPeriod.Day), Ct);
        await test.Document.SetProjectAsync(test.World.Instance.Root);

        Assert.Equal(("5000", string.Empty), (test.Document.Budgets.MaxTokens, test.Document.Budgets.MaxRuns));
        Assert.Equal(WorkTexts.Period(BudgetPeriod.Day), test.Document.Budgets.SelectedPeriod.Label);

        await test.Document.Budgets.RemoveCommand.ExecuteAsync(null);

        Assert.Null(await test.Budgets.LoadAsync(test.World.Project, Ct));
        Assert.Equal(Texts.Get("Work_Budget_None"), test.Document.Budgets.CurrentText);
        Assert.True((await Gate(test).CheckAsync(test.World.Project, "R1", Request(test), 3, Ct)).Allowed);
    }

    [Fact]
    public async Task A_budget_that_does_not_validate_is_not_saved_and_the_screen_says_what_is_wrong()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var budgets = test.Document.Budgets;

        Assert.Equal([Texts.Get("Work_Problem_NoLimit")], budgets.Problems);
        Assert.False(budgets.SaveCommand.CanExecute(null));
        budgets.MaxTokens = "mucho";
        Assert.Equal([Texts.Get("Work_Problem_BadTokens")], budgets.Problems);
        budgets.MaxTokens = "-5";
        Assert.Equal([Texts.Get("Work_Problem_BadTokens")], budgets.Problems);
        Assert.Null(await test.Budgets.LoadAsync(test.World.Project, Ct));
        Assert.False(File.Exists(Path.Combine(test.Budgets.Folder, test.World.ProjectId + ".json")));
    }

    [Fact]
    public async Task A_budget_file_that_cannot_be_read_stops_the_engine_instead_of_lifting_the_limit()
    {
        using var test = await WorkTestWorld.OpenAsync();
        Directory.CreateDirectory(test.Budgets.Folder);
        await File.WriteAllTextAsync(Path.Combine(test.Budgets.Folder, test.World.ProjectId + ".json"), "{ esto no es un presupuesto", Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Budgets.LoadAsync(test.World.Project, Ct));
        var verdict = await Gate(test).CheckAsync(test.World.Project, "R1", Request(test), 0, Ct);

        Assert.False(verdict.Allowed);
        Assert.Equal(ProjectBudgetGate.UnreadableCode, verdict.Code);

        // The screen says it too, in words, and does not pretend there is no budget.
        await test.Document.Budgets.ReloadAsync();
        Assert.StartsWith(Texts.Format("Work_Error_BudgetRead", string.Empty).TrimEnd(), test.Document.Budgets.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_budget_of_each_milestone_is_shown_as_its_header_keeps_it()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await test.World.OpenMilestoneAsync(name: "Con límite", budget: new WorkBudget(1_000, 3));
        await test.World.OpenMilestoneAsync(name: "Sin límite");
        await test.Document.ReloadAsync();

        Assert.Equal(
            [("Con límite", MilestonesSection.BudgetLine(new WorkBudget(1_000, 3))), ("Sin límite", Texts.Get("Work_Budget_None"))],
            test.Document.Budgets.Milestones.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (m.Name, m.BudgetText)));
    }

    [Fact]
    public async Task The_store_refuses_a_budget_with_no_limit_and_a_project_id_that_is_not_safe_to_name_a_file()
    {
        using var test = await WorkTestWorld.OpenAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => test.Budgets.SaveAsync(test.World.Project, new WorkBudget(null, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => test.Budgets.SaveAsync(new AgentStudio.Application.Projects.ProjectContext("..\\fuera", "C:\\x"), new WorkBudget(1, null), Ct));
        Assert.Null(await test.Budgets.LoadAsync(new AgentStudio.Application.Projects.ProjectContext("..\\fuera", "C:\\x"), Ct));
    }
}
