using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Decisions;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/21 D-25 and F10-6 (P0 of Phase 10), the site of the audit (<c>ProjectAuditService</c>): when what holds the project is a remote operation of Agent
/// Studio it waits for it, before the approval is asked and before anything is written; with another activity it refuses at once, with the code and the
/// message of always (<c>audit.project-busy</c>); and past the cap it refuses with <c>project.busy-remote</c>, having asked and written nothing.
/// </summary>
public sealed class ProjectAuditRemoteWaitTests
{
    private static async Task<ProjectAuditBench> ProjectWithClaudeRunAsync()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        return new ProjectAuditBench(run);
    }

    [Fact]
    public async Task An_audit_waits_for_a_remote_operation_and_then_runs_without_losing_anything()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        bench.Activity.Hold(ProjectActivityKind.RemoteOperation);
        var before = bench.Entries.Count;

        var auditing = bench.AuditAsync(proposal);
        Assert.False(auditing.IsCompleted);
        Assert.Empty(bench.Gate.Requests);
        Assert.Equal(before, bench.Entries.Count);

        bench.Activity.Release();
        await RemoteWaitClock.AdvanceUntilAsync(bench.World.Clock, auditing, ProjectActivityEntry.RemoteWaitPoll, () => bench.Activity.Entered.Count == 1);
        var report = await auditing;

        Assert.Single(bench.Gate.Requests);
        Assert.True(report.IsTrustworthy);
        Assert.Contains(bench.Entries, e => e.Kind == DecisionKind.ProjectAuditFinished);
        Assert.Equal([ProjectActivityKind.Audit], bench.Activity.Entered);
        Assert.Equal(1, bench.Activity.Released);
    }

    [Fact]
    public async Task What_was_read_before_a_wait_that_lasted_is_read_again_and_an_audit_that_is_no_longer_what_was_shown_is_refused_before_asking()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        bench.Activity.Hold(ProjectActivityKind.RemoteOperation);
        var before = bench.Entries.Count;

        var auditing = bench.AuditAsync(proposal);
        for (var spin = 0; bench.Activity.Polls < 1; spin++)
        {
            Assert.True(spin < 5_000_000, "The audit never waited.");
            await Task.Yield();
        }

        // While it waited, the repository became unreadable: what it would audit now is not what the person was shown.
        bench.Repository.InspectFailure = new InvalidOperationException("repository gone");
        bench.Activity.Release();
        await RemoteWaitClock.AdvanceUntilAsync(bench.World.Clock, auditing, ProjectActivityEntry.RemoteWaitPoll);

        var refused = await Assert.ThrowsAsync<UserTextException>(() => auditing);
        Assert.Equal(AuditUserTexts.ProposalOutdated, refused.Text.Code);
        Assert.Empty(bench.Gate.Requests);
        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Verifier.Calls);
    }

    [Theory]
    [InlineData(ProjectActivityKind.Run)]
    [InlineData(ProjectActivityKind.Planning)]
    [InlineData(ProjectActivityKind.Debate)]
    public async Task With_another_activity_in_the_way_the_audit_is_refused_at_once_with_the_code_of_always(ProjectActivityKind holder)
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        bench.Activity.Hold(holder);
        var before = bench.Entries.Count;
        var clock = bench.World.Clock.GetUtcNow();

        var refused = await Assert.ThrowsAsync<UserTextException>(() => bench.AuditAsync(proposal));

        Assert.Equal(AuditUserTexts.ProjectBusy, refused.Text.Code);
        Assert.Contains(holder.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Equal(clock, bench.World.Clock.GetUtcNow());
        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Gate.Requests);
        Assert.Empty(bench.Verifier.Calls);
    }

    [Fact]
    public async Task A_remote_operation_that_never_lets_go_refuses_the_audit_with_its_code_after_the_cap_having_asked_and_written_nothing()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        bench.Activity.Hold(ProjectActivityKind.RemoteOperation);
        var before = bench.Entries.Count;
        var started = bench.World.Clock.GetUtcNow();

        var auditing = bench.AuditAsync(proposal);
        await RemoteWaitClock.AdvanceUntilAsync(bench.World.Clock, auditing, ProjectActivityEntry.RemoteWaitPoll);
        var refused = await Assert.ThrowsAsync<UserTextException>(() => auditing);

        Assert.Equal(ProjectActivityEntry.BusyRemoteCode, refused.Text.Code);
        Assert.True(bench.World.Clock.GetUtcNow() - started >= ProjectActivityEntry.RemoteWaitCap);
        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Gate.Requests);
        Assert.Empty(bench.Verifier.Calls);
        Assert.Empty(bench.Judges.Created);
        Assert.Empty(bench.Activity.Entered);
    }
}
