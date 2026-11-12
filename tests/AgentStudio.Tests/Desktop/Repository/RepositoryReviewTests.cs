using System.IO;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.RepositoryScreen;

/// <summary>
/// Review of WP5 (docs/16 §4.6): what the screen says when the annotator or a reader fails (it did not check, it does not say «the log knows
/// nothing»), nothing is thrown out of a command, a refused filter drops a read in flight, the filters go with the project and «Actualizar»
/// is never left disabled.
/// </summary>
public sealed class RepositoryReviewTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2));

    private static readonly string Sha1 = FakeHistory.Sha('1');
    private static readonly string Sha2 = FakeHistory.Sha('2');

    private sealed class World
    {
        public FakeWorkspace Workspace { get; } = new();

        public FakeHistory History { get; } = new();

        public FakeAnnotator Annotator { get; } = new();

        public FakeActivity Activity { get; } = new();

        public FakeRemotes Remotes { get; } = new();

        public RepositoryViewModel Model() => new(Workspace, History, Annotator, Activity, Remotes, new FakeClock(Now));
    }

    private static AgentStudio.Application.Git.CommitNode Node(string id, string subject) =>
        new(id, [], new DateTimeOffset(2026, 11, 12, 8, 30, 0, TimeSpan.Zero), subject, "Manuel", ["main"], null);

    private static AgentStudio.Application.Git.RepositoryActivity Event(string run, long sequence) =>
        new(Now.AddMinutes(-1), AgentStudio.Application.Git.RepositoryActivityKind.Integration, run, "T1", null, true, null, sequence);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_version_that_could_not_be_checked_against_the_log_does_not_say_the_log_knows_nothing_of_it(bool ioError)
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        world.History.Commits.Add(Node(Sha2, "Segundo"));
        if (ioError)
        {
            world.Annotator.Failure = new IOException("disk");
        }
        else
        {
            world.Annotator.BrokenLog = true;
        }

        var model = world.Model();
        await model.SetProjectAsync(Root);

        Assert.Equal(2, model.Commits.Count);
        foreach (var row in model.Commits)
        {
            var line = Assert.Single(row.Origin);
            Assert.Equal(Texts.Get("Repository_Origin_Unchecked"), line.Text);
            Assert.NotEqual(Texts.Get("Repository_Origin_None"), line.Text);
        }

        // The count stays next to the warning.
        Assert.Contains(Texts.Format("Repository_Commits_Showing", "2"), model.CommitsNote, StringComparison.Ordinal);
        Assert.Contains(Texts.Get("Repository_Commits_NotAnnotated"), model.CommitsNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_annotator_the_versions_are_not_said_to_be_unknown_to_the_log()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        var model = new RepositoryViewModel(world.Workspace, world.History, null, world.Activity, world.Remotes, new FakeClock(Now));

        await model.SetProjectAsync(Root);

        Assert.Equal(Texts.Get("Repository_Origin_Unchecked"), Assert.Single(Assert.Single(model.Commits).Origin).Text);
    }

    [Fact]
    public async Task A_reader_that_fails_when_the_screen_is_refreshed_is_said_and_no_partial_data_stays()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        world.Activity.Events.Add(Event("20261112-a1b2c3", 1));
        var model = world.Model();
        await model.SetProjectAsync(Root);
        Assert.Single(model.Commits);

        world.History.SummaryFailure = new IOException("disk");
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(model.RefusalText);
        Assert.Empty(model.Commits);
        Assert.Empty(model.Branches);
        Assert.Null(model.BranchText);
        Assert.False(model.IsBusy);
        Assert.True(model.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_activity_reader_that_fails_with_anything_is_said_in_words_and_the_list_is_emptied()
    {
        var world = new World();
        world.Activity.Events.Add(Event("20261112-a1b2c3", 1));
        var model = world.Model();
        await model.SetProjectAsync(Root);
        Assert.Single(model.Activities);

        world.Activity.Failure = new IOException("disk");
        await model.ApplyFiltersCommand.ExecuteAsync(null);

        Assert.Empty(model.Activities);
        Assert.Equal(Texts.Get("Repository_Activity_Unreadable"), model.ActivityNote);
    }

    [Fact]
    public async Task A_filter_that_is_refused_drops_the_read_that_was_still_running()
    {
        var world = new World();
        world.Activity.Events.Add(Event("20261112-a1b2c3", 1));
        var model = world.Model();
        await model.SetProjectAsync(Root);
        world.Activity.Events.Add(Event("20261112-d4e5f6", 2));
        world.Activity.Hold = new TaskCompletionSource();
        var hold = world.Activity.Hold;

        var slow = model.ApplyFiltersCommand.ExecuteAsync(null);
        model.MilestoneFilter = "hito-uno";
        await model.ApplyFiltersCommand.ExecuteAsync(null);
        Assert.NotNull(model.FilterError);
        hold.SetResult();
        await slow;

        Assert.NotNull(model.FilterError);
        Assert.Single(model.Activities);
    }

    [Fact]
    public async Task The_filters_are_emptied_when_the_project_changes()
    {
        var world = new World();
        var model = world.Model();
        await model.SetProjectAsync(Root);
        model.RunFilter = "20261112-a1b2c3";
        model.AgentFilter = "ejecutor";
        model.SelectedProvider = model.Providers[1];
        model.SelectedPeriod = model.Periods[2];

        await model.SetProjectAsync(@"C:\proyectos\otro");

        Assert.Equal((string.Empty, string.Empty), (model.RunFilter, model.AgentFilter));
        Assert.Same(model.Providers[0], model.SelectedProvider);
        Assert.Same(model.Periods[0], model.SelectedPeriod);
        Assert.Null(world.Activity.Filters[^1]!.Run);
    }

    [Fact]
    public async Task Closing_the_project_while_a_read_is_running_does_not_leave_refresh_disabled()
    {
        var world = new World();
        var hold = new TaskCompletionSource();
        world.History.HoldSummary = hold;
        var model = world.Model();

        var slow = model.SetProjectAsync(Root);
        Assert.True(model.IsBusy);
        await model.SetProjectAsync(null);

        Assert.False(model.IsBusy);
        hold.SetResult();
        await slow;
        Assert.False(model.IsBusy);
        Assert.True(model.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_project_change_without_a_workspace_while_a_read_is_running_does_not_leave_refresh_disabled()
    {
        var world = new World();
        var hold = new TaskCompletionSource();
        world.History.HoldSummary = hold;
        var model = world.Model();

        var slow = model.SetProjectAsync(Root);
        await model.SetProjectAsync(null);
        hold.SetResult();
        await slow;

        Assert.False(model.IsBusy);
    }
}
