using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Application.Orchestration;

/// <summary>
/// docs/21 D-25 (P0 of Phase 10): entering a project when what may hold it is a remote operation of Agent Studio itself. The caller waits, up to the cap, polling
/// every 250 ms, and past it is refused with <c>project.busy-remote</c>; with a run, a planning, a debate or an audit in the way nothing changes and the answer is
/// at once. The time is a clock of the test, so nothing here waits for the real one.
/// </summary>
public sealed class ProjectActivityEntryTests
{
    private const string Project = "p1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_cap_the_poll_and_the_code_are_the_ones_of_the_design()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), ProjectActivityEntry.RemoteWaitCap);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ProjectActivityEntry.RemoteWaitPoll);
        Assert.Equal("project.busy-remote", ProjectActivityEntry.BusyRemoteCode);
        Assert.True(UserText.IsCode(ProjectActivityEntry.BusyRemoteCode));
        Assert.Equal("Text_project_busy_remote", UserText.KeyOf(ProjectActivityEntry.BusyRemoteCode));
    }

    [Fact]
    public async Task A_free_project_is_entered_at_once_without_asking_for_a_timer()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();

        using var lease = await ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Audit, clock, Ct);

        Assert.NotNull(lease);
        Assert.Equal(ProjectActivityKind.Audit, activity.Current(Project));
        Assert.Equal(0, clock.Timers);
    }

    [Theory]
    [InlineData(ProjectActivityKind.Run)]
    [InlineData(ProjectActivityKind.Planning)]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task With_anything_but_a_remote_operation_in_the_way_the_answer_is_null_at_once_as_before(ProjectActivityKind holder)
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        using var held = activity.TryEnter(Project, holder);

        var task = ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Debate, clock, Ct);

        Assert.True(task.IsCompletedSuccessfully, "It must answer without waiting for the clock.");
        Assert.Null(await task);
        Assert.Equal(0, clock.Timers);
        Assert.Equal(holder, activity.Current(Project));
    }

    [Fact]
    public async Task A_run_and_a_planning_share_the_project_as_ever_even_after_a_wait_is_possible()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        using var planning = activity.TryEnter(Project, ProjectActivityKind.Planning);

        using var run = await ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Run, clock, Ct);

        Assert.NotNull(run);
        Assert.Equal(ProjectActivityKind.Run, activity.Current(Project));
    }

    [Fact]
    public async Task It_waits_while_a_remote_operation_holds_the_project_and_enters_once_it_lets_go()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation)!;

        var waiting = ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Run, clock, Ct);
        await clock.WaitForTimerAsync(1);
        Assert.False(waiting.IsCompleted);
        clock.Advance(ProjectActivityEntry.RemoteWaitPoll);
        await clock.WaitForTimerAsync(2);
        Assert.False(waiting.IsCompleted, "Still held: it asks again, it does not give up.");

        remote.Dispose();
        clock.Advance(ProjectActivityEntry.RemoteWaitPoll);
        using var lease = await waiting;

        Assert.NotNull(lease);
        Assert.Equal(ProjectActivityKind.Run, activity.Current(Project));
        Assert.Equal(2, clock.Timers);
    }

    [Fact]
    public async Task A_remote_operation_that_does_not_let_go_is_waited_for_exactly_the_cap_and_then_refused_with_its_code()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        using var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation);
        var polls = (int)(ProjectActivityEntry.RemoteWaitCap / ProjectActivityEntry.RemoteWaitPoll);

        var waiting = ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Audit, clock, Ct);
        for (var poll = 1; poll < polls; poll++)
        {
            await clock.WaitForTimerAsync(poll);
            clock.Advance(ProjectActivityEntry.RemoteWaitPoll);
        }

        await clock.WaitForTimerAsync(polls);
        Assert.False(waiting.IsCompleted, "One poll before the cap it still waits.");
        clock.Advance(ProjectActivityEntry.RemoteWaitPoll);
        var refused = await Assert.ThrowsAsync<UserTextException>(() => waiting);

        Assert.Equal(ProjectActivityEntry.BusyRemoteCode, refused.Text.Code);
        Assert.Equal(polls, clock.Timers);
        Assert.Equal(ProjectActivityKind.RemoteOperation, activity.Current(Project));
    }

    [Fact]
    public async Task What_takes_the_project_after_the_remote_operation_lets_go_is_refused_at_once_not_waited_for()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation)!;

        var waiting = ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Debate, clock, Ct);
        await clock.WaitForTimerAsync(1);
        remote.Dispose();
        using var other = activity.TryEnter(Project, ProjectActivityKind.Audit);
        clock.Advance(ProjectActivityEntry.RemoteWaitPoll);

        Assert.Null(await waiting);
        Assert.Equal(1, clock.Timers);
    }

    [Fact]
    public async Task A_cancelled_wait_ends_with_the_cancellation_and_enters_nothing()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        using var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.Run, clock, cancel.Token);
        await clock.WaitForTimerAsync(1);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(ProjectActivityKind.RemoteOperation, activity.Current(Project));
    }

    [Fact]
    public async Task A_remote_operation_shares_the_project_with_nothing_and_does_not_wait_for_another()
    {
        var clock = new RemoteWaitClock();
        var activity = new OrchestrationActivity();
        using var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation);

        Assert.True(activity.IsBusy(Project));
        Assert.Null(activity.TryEnter(Project, ProjectActivityKind.RemoteOperation));
        Assert.Null(activity.TryEnter(Project, ProjectActivityKind.Run));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectActivityEntry.EnterAsync(activity, Project, ProjectActivityKind.RemoteOperation, clock, Ct));
        using var onlyAfter = activity.TryEnter("another-project", ProjectActivityKind.RemoteOperation);
        Assert.NotNull(onlyAfter);
    }

    [Fact]
    public void A_remote_operation_lets_the_project_go_once_and_only_once()
    {
        var activity = new OrchestrationActivity();
        var remote = activity.TryEnter(Project, ProjectActivityKind.RemoteOperation)!;

        remote.Dispose();
        remote.Dispose();

        Assert.False(activity.IsBusy(Project));
        Assert.NotNull(activity.TryEnter(Project, ProjectActivityKind.Run));
    }
}
