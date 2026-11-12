using System.Collections.Specialized;
using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>A backlog whose first read after <see cref="Arm"/> takes its snapshot at once and hands it over only when the test lets it: a read that arrives late.</summary>
internal sealed class LateBacklog(IBacklogService inner) : IBacklogService
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;
    private int _taken;

    public void Arm() => _armed = 1;

    public void Release() => _gate.TrySetResult();

    public Task<BacklogItem> AddAsync(ProjectContext project, BacklogDraft draft, string addedBy, CancellationToken cancellationToken) => inner.AddAsync(project, draft, addedBy, cancellationToken);

    public Task<BacklogItem> ReviseAsync(ProjectContext project, BacklogItemId item, BacklogDraft draft, string revisedBy, CancellationToken cancellationToken) => inner.ReviseAsync(project, item, draft, revisedBy, cancellationToken);

    public Task RetireAsync(ProjectContext project, BacklogItemId item, string retiredBy, string? reason, CancellationToken cancellationToken) => inner.RetireAsync(project, item, retiredBy, reason, cancellationToken);

    public Task ReorderAsync(ProjectContext project, IReadOnlyList<BacklogItemId> items, string decidedBy, CancellationToken cancellationToken) => inner.ReorderAsync(project, items, decidedBy, cancellationToken);

    public Task<IReadOnlyList<BacklogItem>> HistoryAsync(ProjectContext project, BacklogItemId item, CancellationToken cancellationToken) => inner.HistoryAsync(project, item, cancellationToken);

    public async Task<IReadOnlyList<BacklogEntry>> ListAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        var snapshot = await inner.ListAsync(project, cancellationToken);
        if (_armed == 1 && Interlocked.Increment(ref _taken) == 1)
        {
            await _gate.Task;
        }

        return snapshot;
    }
}

/// <summary>A milestone service that counts the links written and holds the first one until the test lets it go.</summary>
internal sealed class SlowLinkMilestones(IMilestoneService inner) : IMilestoneService
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Links { get; private set; }

    public void Release() => _gate.TrySetResult();

    public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);

    public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) =>
        inner.ReviseRequirementsAsync(project, milestone, requirements, revisedBy, reason, cancellationToken);

    public async Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken)
    {
        Links++;
        await _gate.Task;
        return await inner.LinkAsync(project, milestone, target, linkedBy, cancellationToken);
    }

    public Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, AgentStudio.Domain.Common.EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken) =>
        inner.ReopenAsync(project, milestone, reason, evidence, reopenedBy, note, cancellationToken);

    public Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) => inner.GetAsync(project, milestone, cancellationToken);

    public Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => inner.ListAsync(project, cancellationToken);
}

/// <summary>
/// Review of WP5 (docs/16 §4.6): a reload keeps the selection by identity and does not count as the person choosing something else (the list
/// box of the screen puts the selection to null when its items are cleared: the tests do the same on the collection), two reloads that overlap
/// never both fill the lists, and a reload does not overwrite what the person is typing.
/// </summary>
public sealed class WorkReloadTests
{
    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static readonly AcceptanceCriterion Criterion = new("C1", "Multiply(2,3) da 6", VerificationMethod.JudgeAssessment);

    /// <summary>What the two-way binding of the list box does: clearing the items leaves it with nothing selected.</summary>
    private static void SimulateBinding<T>(System.Collections.ObjectModel.ObservableCollection<T> items, Action clear)
        where T : class =>
        items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                clear();
            }
        };

    private static async Task AddBacklogAsync(WorkTestWorld test, string statement) =>
        await test.World.Backlog.AddAsync(test.World.Project, new BacklogDraft(statement, RequirementPriority.Mandatory, [Criterion]), "test", Ct);

    [Fact]
    public async Task A_milestone_reload_keeps_the_selection_and_what_the_person_was_told_even_when_the_binding_clears_it()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await test.World.OpenMilestoneAsync();
        await test.World.StartRunAsync();
        await test.Document.ReloadAsync();
        var milestones = test.Document.Milestones;
        SimulateBinding(milestones.Items, () => milestones.SelectedItem = null);
        milestones.SelectedItem = milestones.Items.Single();
        await milestones.RefreshLinkChoicesAsync();

        await milestones.LinkRunCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        Assert.Equal(Texts.Get("Work_Milestone_RunLinked"), milestones.Message);
        Assert.NotNull(milestones.SelectedItem);
        Assert.Equal(Texts.Format("Work_Milestone_TargetRun", WorkWorld.Run1), Assert.Single(milestones.Targets));
        Assert.Empty(milestones.RunChoices);
    }

    [Fact]
    public async Task A_milestone_reload_keeps_the_items_the_person_ticked_for_the_next_milestone()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await AddBacklogAsync(test, "Calc multiplica");
        await test.Document.ReloadAsync();
        var milestones = test.Document.Milestones;
        milestones.BacklogChoices.Single().IsIncluded = true;

        await test.Document.ReloadAsync();

        Assert.True(milestones.BacklogChoices.Single().IsIncluded);
    }

    [Fact]
    public async Task Two_overlapped_reloads_of_the_milestones_never_duplicate_the_choices_and_a_read_that_arrives_late_does_not_win()
    {
        LateBacklog? late = null;
        using var test = await WorkTestWorld.OpenAsync((world, backend) => backend with { Backlog = late = new LateBacklog(world.Backlog) });
        await AddBacklogAsync(test, "Primero");
        var milestones = test.Document.Milestones;

        late!.Arm();
        var first = milestones.ReloadAsync();
        await AddBacklogAsync(test, "Segundo");
        await milestones.ReloadAsync();
        late.Release();
        await first;

        Assert.Equal(["Primero", "Segundo"], milestones.BacklogChoices.Select(c => c.Statement).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_second_link_while_the_first_is_being_written_is_not_sent()
    {
        SlowLinkMilestones? slow = null;
        using var test = await WorkTestWorld.OpenAsync((world, backend) => backend with { Milestones = slow = new SlowLinkMilestones(world.Milestones) });
        await test.World.OpenMilestoneAsync();
        await test.World.StartRunAsync();
        await test.Document.ReloadAsync();
        var milestones = test.Document.Milestones;
        milestones.SelectedItem = milestones.Items.Single();
        await milestones.RefreshLinkChoicesAsync();
        milestones.SelectedDebate = new Choice<string>("D1", "D1");

        var first = milestones.LinkRunCommand.ExecuteAsync(null);
        var second = milestones.LinkDebateCommand.ExecuteAsync(null);
        slow!.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, slow.Links);
    }

    [Fact]
    public async Task A_backlog_reload_keeps_the_message_of_the_version_just_saved_and_the_form_the_person_is_editing()
    {
        using var test = await WorkTestWorld.OpenAsync();
        await AddBacklogAsync(test, "Calc multiplica");
        await test.Document.ReloadAsync();
        var backlog = test.Document.Backlog;
        SimulateBinding(backlog.Items, () => backlog.SelectedItem = null);
        backlog.SelectedItem = backlog.Items.Single();
        backlog.Statement = "Calc multiplica y divide";

        await backlog.SaveCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        Assert.Equal(Texts.Format("Work_Backlog_Revised", 2), backlog.Message);
        Assert.Equal("Calc multiplica y divide", backlog.Statement);
        Assert.True(backlog.IsEditing);

        // Another section reading again (a reload comes from anywhere) does not rebuild the form over what is being typed.
        backlog.Statement = "Calc multiplica, divide y suma";
        await test.Document.ReloadAsync();
        Assert.Equal("Calc multiplica, divide y suma", backlog.Statement);
    }

    [Fact]
    public async Task The_message_of_the_budget_file_never_carries_the_folder_of_the_person()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var file = Path.Combine(test.Budgets.Folder, test.World.ProjectId + ".json");
        Directory.CreateDirectory(test.Budgets.Folder);
        await File.WriteAllTextAsync(file, "{ esto no es un presupuesto", Ct);

        var corrupt = await Assert.ThrowsAsync<InvalidDataException>(() => test.Budgets.LoadAsync(test.World.Project, Ct));
        Assert.DoesNotContain(test.Budgets.Folder, corrupt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFileName(file), corrupt.Message, StringComparison.Ordinal);

        // A file that is there but cannot be opened says it the same way.
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var locked = await Assert.ThrowsAsync<InvalidDataException>(() => test.Budgets.LoadAsync(test.World.Project, Ct));
        Assert.DoesNotContain(test.Budgets.Folder, locked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_budget_that_cannot_be_read_is_not_shown_as_no_budget_and_a_reload_does_not_overwrite_what_is_typed()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var budgets = test.Document.Budgets;
        budgets.MaxRuns = "7";

        await test.Document.ReloadAsync();
        Assert.Equal("7", budgets.MaxRuns);

        Directory.CreateDirectory(test.Budgets.Folder);
        await File.WriteAllTextAsync(Path.Combine(test.Budgets.Folder, test.World.ProjectId + ".json"), "{ esto no es un presupuesto", Ct);
        await budgets.ReloadAsync();

        Assert.Equal(Texts.Get("Work_Budget_Unreadable"), budgets.CurrentText);
        Assert.NotEqual(Texts.Get("Work_Budget_None"), budgets.CurrentText);
        Assert.True(budgets.HasError);
    }
}
