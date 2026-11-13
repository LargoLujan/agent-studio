using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// docs/21 D-25 and F10-6 (P0 of Phase 10), the site of the coordinator of debates (<c>DebateCoordinator.Enter</c>): when what holds the project is a remote
/// operation of Agent Studio it waits for it before writing anything; with any other activity it refuses at once with the same text and code as ever
/// (<c>debate.project-busy</c>); and past the cap it refuses with <c>project.busy-remote</c>, having written nothing.
/// </summary>
public sealed class DebateRemoteWaitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_debate_waits_for_a_remote_operation_and_then_opens_without_losing_anything()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), Ct);
        var remote = world.Activity.TryEnter(Project, ProjectActivityKind.RemoteOperation)!;

        var opening = world.Coordinator.OpenAsync(proposal, Ct);
        Assert.False(opening.IsCompleted);
        Assert.Empty((await world.LogAsync()).Entries);

        remote.Dispose();
        await RemoteWaitClock.AdvanceUntilAsync(world.Clock, opening, ProjectActivityEntry.RemoteWaitPoll, () => world.Activity.Current(Project) == ProjectActivityKind.Debate);
        await using var run = await opening;
        await CompletedAsync(run);

        Assert.Contains((await world.LogAsync()).Entries, e => e.Kind == DecisionKind.DebateOpened);
        Assert.Null(world.Activity.Current(Project));
    }

    [Theory]
    [InlineData(ProjectActivityKind.Run)]
    [InlineData(ProjectActivityKind.Planning)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task With_another_activity_in_the_way_the_debate_is_refused_at_once_with_the_text_of_always(ProjectActivityKind holder)
    {
        using var world = new ScriptedDebateWorld();
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), Ct);
        using var held = world.Activity.TryEnter(Project, holder);

        var opening = world.Coordinator.OpenAsync(proposal, Ct);

        Assert.True(opening.IsFaulted, "It must refuse without waiting for the clock.");
        var refused = Assert.IsType<UserTextException>(opening.Exception!.GetBaseException());
        Assert.Equal(DebateUserTexts.ProjectBusy, refused.Text.Code);
        Assert.Contains($"busy ({holder})", refused.Message, StringComparison.Ordinal);
        Assert.Empty((await world.LogAsync()).Entries);
    }

    [Fact]
    public async Task A_remote_operation_that_never_lets_go_refuses_the_debate_with_its_code_after_the_cap_having_written_nothing()
    {
        using var world = new ScriptedDebateWorld();
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), Ct);
        using var remote = world.Activity.TryEnter(Project, ProjectActivityKind.RemoteOperation);
        var started = world.Clock.GetUtcNow();

        var opening = world.Coordinator.OpenAsync(proposal, Ct);
        await RemoteWaitClock.AdvanceUntilAsync(world.Clock, opening, ProjectActivityEntry.RemoteWaitPoll);
        var refused = await Assert.ThrowsAsync<UserTextException>(() => opening);

        Assert.Equal(ProjectActivityEntry.BusyRemoteCode, refused.Text.Code);
        Assert.True(world.Clock.GetUtcNow() - started >= ProjectActivityEntry.RemoteWaitCap);
        Assert.Empty((await world.LogAsync()).Entries);
        Assert.Empty(world.Prompt.Asked);
        Assert.Empty(world.Claude.Requests);
        Assert.Equal(ProjectActivityKind.RemoteOperation, world.Activity.Current(Project));
    }

    [Fact]
    public async Task A_resume_waits_for_a_remote_operation_too_and_is_refused_after_the_cap_with_the_same_code()
    {
        using var world = new ScriptedDebateWorld();
        var debate = new RunId("20261113-dbbdbb");
        using var remote = world.Activity.TryEnter(Project, ProjectActivityKind.RemoteOperation);

        var resuming = world.Coordinator.ResumeAsync(world.Context, debate, Ct);
        Assert.False(resuming.IsCompleted);
        await RemoteWaitClock.AdvanceUntilAsync(world.Clock, resuming, ProjectActivityEntry.RemoteWaitPoll);

        var refused = await Assert.ThrowsAsync<UserTextException>(() => resuming);
        Assert.Equal(ProjectActivityEntry.BusyRemoteCode, refused.Text.Code);
        Assert.Empty((await world.LogAsync()).Entries);
    }
}
