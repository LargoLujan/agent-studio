using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Debate.Audit.Milestones;
using AgentStudio.Tests.Orchestration.WorkTracking;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>
/// The milestone service as the bench of the close has it (the header and the replay of the log), with the list the screen needs and the
/// reopening written to the same log: the close itself is the real <c>MilestoneCloser</c> with its scripted judge.
/// </summary>
internal sealed class BenchMilestones(MilestoneBench bench) : IMilestoneService
{
    public Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) => bench.Service.GetAsync(project, milestone, cancellationToken);

    public async Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) =>
        await bench.Service.GetAsync(project, MilestoneBench.Id, cancellationToken) is { } view ? [view] : [];

    public async Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken)
    {
        Assert.Equal((MilestoneReopenReason.ByPerson, null), (reason, evidence));
        await bench.ReopenAsync();
        return (await GetAsync(project, milestone, cancellationToken))!;
    }

    public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>
/// docs/16 D-17, D-49 and D-50 (F9-1, F9-10) on the screen: a milestone is opened with requirements frozen from the backlog and an optional
/// budget, its state is the replay of the log, runs and debates are linked to it, and closing it is the audit with a scripted judge: a close that
/// is refused says which rule refused it and writes nothing, and reopening keeps the report before.
/// </summary>
public sealed class WorkMilestonesViewModelTests
{
    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static async Task NoteAsync(WorkTestWorld test, string statement, RequirementPriority priority, params string[] criteria)
    {
        var backlog = test.Document.Backlog;
        backlog.NewItemCommand.Execute(null);
        backlog.Statement = statement;
        backlog.SelectedPriority = BacklogSection.Priorities.Single(p => p.Value == priority);
        foreach (var text in criteria)
        {
            backlog.AddCriterionCommand.Execute(null);
            backlog.Criteria[^1].Description = text;
        }

        await backlog.SaveCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();
    }

    [Fact]
    public async Task A_milestone_is_opened_from_the_backlog_with_its_requirements_frozen_and_an_optional_budget()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await NoteAsync(test, "Calc multiplica", RequirementPriority.Mandatory, "Multiply(2,3) da 6");
        await NoteAsync(test, "Calc está documentada", RequirementPriority.Desirable);
        var milestones = test.Document.Milestones;
        Assert.Equal(2, milestones.BacklogChoices.Count);

        // Nothing is missing but what the screen says is missing.
        Assert.True(milestones.HasOpenProblems);
        Assert.Contains(Texts.Get("Work_Problem_NoName"), milestones.OpenProblems);
        Assert.False(milestones.OpenCommand.CanExecute(null));

        milestones.NewName = "Primera entrega";
        milestones.NewObjective = "Que multiplique.";
        milestones.BacklogChoices.Single(c => c.Statement == "Calc multiplica").IsIncluded = true;
        milestones.BacklogChoices.Single(c => c.Statement == "Calc está documentada").IsIncluded = true;
        milestones.NewMaxTokens = "250000";
        Assert.False(milestones.HasOpenProblems);
        await milestones.OpenCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        var row = Assert.Single(milestones.Items);
        Assert.Equal("Primera entrega", row.Name);
        Assert.Equal(MilestoneStatus.Open, row.View.Status);
        Assert.Equal(new WorkBudget(250_000, null), row.View.Header.Budget);
        var frozen = await test.World.Instance.Requirements.FindAsync(test.World.ProjectId, row.View.CurrentRequirementSetHash, Ct);
        Assert.Equal(2, frozen!.Requirements.Count);
        Assert.Contains(frozen.Requirements, r => r.Statement == "Calc multiplica" && r.Priority == RequirementPriority.Mandatory && r.Criteria.Count == 1);
        milestones.SelectedItem = row;
        Assert.Equal(MilestonesSection.BudgetLine(new WorkBudget(250_000, null)), milestones.BudgetText);
        Assert.Equal("Que multiplique.", milestones.ObjectiveText);
    }

    [Fact]
    public async Task A_mandatory_requirement_without_criteria_and_a_wrong_limit_are_said_before_opening()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await NoteAsync(test, "Sin criterios", RequirementPriority.Mandatory);
        var milestones = test.Document.Milestones;
        milestones.NewName = "Hito";
        milestones.NewObjective = "Algo.";
        milestones.BacklogChoices.Single().IsIncluded = true;
        milestones.NewMaxRuns = "0";

        Assert.Contains(Texts.Format("Work_Problem_NeedsCriteria", "Sin criterios"), milestones.OpenProblems);
        Assert.Contains(Texts.Get("Work_Problem_BadRuns"), milestones.OpenProblems);
        Assert.False(milestones.OpenCommand.CanExecute(null));
        Assert.Empty(await test.World.Milestones.ListAsync(test.World.Project, Ct));
    }

    [Fact]
    public async Task A_run_is_linked_to_the_milestone_and_then_it_is_no_longer_offered()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var world = test.World;
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await test.Document.ReloadAsync();
        var milestones = test.Document.Milestones;
        milestones.SelectedItem = milestones.Items.Single();
        await milestones.RefreshLinkChoicesAsync();

        Assert.Equal([WorkWorld.Run1], milestones.RunChoices.Select(c => c.Value));
        await milestones.LinkRunCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();
        await milestones.RefreshLinkChoicesAsync();

        Assert.Equal(Texts.Format("Work_Milestone_TargetRun", WorkWorld.Run1), Assert.Single(milestones.Targets));
        Assert.Empty(milestones.RunChoices);
        Assert.Equal([new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1)], (await world.Milestones.GetAsync(world.Project, view.Header.Id, Ct))!.Targets);
    }

    private static async Task<(MilestoneBench Bench, WorkDocumentViewModel Document)> BenchDocumentAsync(bool linkRun)
    {
        var bench = await MilestoneBench.WithRunAsync(linkRun);
        var backend = new WorkBackend(new WorldWorkspace(bench.Project), new BenchMilestones(bench), bench.Closer, Evidence: bench.Audit.World.Evidence);
        var time = new FakeTimeProvider(AgentStudio.Tests.Domain.TestData.T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var document = new WorkDocumentViewModel(backend, time);
        await document.SetProjectAsync("C:\\work\\calc");
        document.Milestones.SelectedItem = document.Milestones.Items.Single();
        return (bench, document);
    }

    [Fact]
    public async Task Closing_a_milestone_is_the_audit_with_a_judge_that_did_not_write_the_code_and_leaves_a_report_that_can_be_read()
    {
        var (bench, document) = await BenchDocumentAsync(linkRun: true);
        using (bench)
        {
            var milestones = document.Milestones;
            Assert.True(milestones.CanClose);
            Assert.False(milestones.CanReopen);

            await milestones.CloseMilestoneCommand.ExecuteAsync(null);
            await document.ReloadAsync();

            // The approval of the audit was asked for the milestone, as «Auditar proyecto completo» asks it.
            Assert.Contains(MilestoneBench.Id.Value, Assert.Single(bench.Audit.Gate.Requests).Description, StringComparison.Ordinal);
            Assert.Null(milestones.Error);
            Assert.Equal(Texts.Get("Work_Status_Closed"), milestones.Items.Single().StatusText);
            var report = Assert.Single(milestones.Reports);
            Assert.Equal(Texts.Format("Work_Report_Row", 1, report.WhenText), report.Title);
            Assert.False(milestones.CanClose);
            Assert.True(milestones.CanReopen);

            await milestones.OpenReportCommand.ExecuteAsync(report);
            Assert.True(milestones.HasOpenedReport);
            Assert.Contains("agent-studio/milestone-report/1", milestones.OpenedReport!.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_close_that_is_refused_says_which_rule_and_writes_nothing()
    {
        var (bench, document) = await BenchDocumentAsync(linkRun: false);
        using (bench)
        {
            var before = bench.Entries.Count;
            var milestones = document.Milestones;

            await milestones.CloseMilestoneCommand.ExecuteAsync(null);

            Assert.Equal(Texts.Format("Work_Close_Refused", Texts.Get("Work_Refused_milestone_nothing_to_close")), milestones.Error);
            Assert.Equal(before, bench.Entries.Count);
            Assert.Empty(bench.Audit.Gate.Requests);
            Assert.Equal(MilestoneStatus.Open, milestones.SelectedItem!.View.Status);
        }
    }

    [Fact]
    public async Task Reopening_a_closed_milestone_keeps_the_report_it_had()
    {
        var (bench, document) = await BenchDocumentAsync(linkRun: true);
        using (bench)
        {
            var milestones = document.Milestones;
            await milestones.CloseMilestoneCommand.ExecuteAsync(null);
            await document.ReloadAsync();
            milestones.SelectedItem = milestones.Items.Single();
            Assert.True(milestones.CanReopen);

            milestones.ReopenNote = "Faltaba un caso";
            await milestones.ReopenCommand.ExecuteAsync(null);
            await document.ReloadAsync();

            Assert.Equal(MilestoneStatus.Open, milestones.Items.Single().View.Status);
            Assert.Single(milestones.Reports);
            Assert.True(milestones.CanClose);
        }
    }

    [Fact]
    public async Task Every_rule_a_close_can_be_refused_by_has_a_text_in_the_language_of_the_screen()
    {
        foreach (var rule in new[]
        {
            "milestone.not-found", "milestone.problems", "milestone.not-open", "milestone.nothing-to-close", "milestone.no-judge",
            "log.not-intact", "milestone.audit-not-run", "milestone.closing", "milestone.requirements",
        })
        {
            var text = WorkTexts.Refusal(rule);
            Assert.False(text.StartsWith('['), rule);
            Assert.DoesNotContain(rule, text, StringComparison.Ordinal);
        }

        Assert.Contains("other.rule", WorkTexts.Refusal("other.rule"), StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}
