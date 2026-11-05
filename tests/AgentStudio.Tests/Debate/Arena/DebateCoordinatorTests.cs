using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>Preparing, opening and closing a debate (docs/15 D-01 to D-07 and D-26).</summary>
public sealed class DebateCoordinatorTests
{
    [Fact]
    public async Task Preparing_shows_the_default_disposition_and_the_estimate_and_writes_nothing()
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), TestContext.Current.CancellationToken);
        var again = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), TestContext.Current.CancellationToken);

        Assert.Empty(proposal.Problems);
        Assert.Equal(DebateJudgingKind.IndependentJudge, proposal.Judging.Kind);
        Assert.Equal([JuezCodex], proposal.Judging.Judges.Select(j => j.Agent));
        Assert.Empty(proposal.Judging.WaivedParticipantVendors);
        Assert.Equal(2 * 4 + 2, proposal.Estimate.MaxAgentRuns);
        Assert.Equal(AgentStudio.Domain.Metrics.MetricOrigin.EstimatedBySystem, proposal.Estimate.Tokens.Origin);
        Assert.Equal(proposal.HeaderHash, again.HeaderHash);
        Assert.Contains("arquitecto", proposal.ApprovalText, StringComparison.Ordinal);
        Assert.Empty((await world.LogAsync()).Entries);
        Assert.Empty(world.Prompt.Asked);
    }

    [Fact]
    public async Task A_free_debate_runs_its_rounds_publishes_them_in_header_order_and_closes_with_its_verdict()
    {
        using var world = new ScriptedDebateWorld();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved), (result.Phase, result.Outcome));
        Assert.Equal((DebateLimitReached.Rounds, DebateJudgmentReason.Limit), (result.LimitReached, result.JudgmentReason));
        Assert.Equal(JudgingPanel.Single, result.Panel!.Rule);
        var log = await world.DebateLogAsync(run.Id);
        Assert.NotNull(log.Opened);
        Assert.Equal(8, log.Contributions.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], log.Contributions.Select(c => c.Payload.Index));
        Assert.Equal(
            [Arquitecto, Revisor, Arquitecto, Revisor, Arquitecto, Revisor, Arquitecto, Revisor],
            log.Contributions.Select(c => c.Payload.Author));
        Assert.Equal([1, 2, 3, 4], log.RoundsClosed.Select(r => r.Payload.Round));
        Assert.Equal(DebateOutcome.Resolved, log.Closed!.Value.Payload.Outcome);
        Assert.Equal(8, log.Turns.Count());
        Assert.All(log.Turns, t => Assert.Equal(AttemptOutcome.Succeeded, t.Finished!.Status));
        Assert.Equal([run.Id], world.Judgment.Calls);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task The_close_of_a_debate_rests_on_the_whole_log_and_the_debate_going_on_reads_its_tail()
    {
        using var world = new ScriptedDebateWorld();
        var spy = new LoadSpyRecorder(world.Recorder);
        world.Ports = world.Ports with { Decisions = spy };
        world.Coordinator = new DebateCoordinator(world.Ports);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var result = await CompletedAsync(run);

        // The close is a critical moment (ADR-0010, addendum of 2026-11-04): the load just before DebateClosed reads the whole log.
        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved), (result.Phase, result.Outcome));
        var calls = spy.Calls.ToList();
        var closed = calls.IndexOf("Record:" + DecisionKind.DebateClosed);
        Assert.True(closed > 0, string.Join(", ", calls));
        Assert.Equal(nameof(LogLoad.Full), calls.Take(closed).Last(c => !c.StartsWith("Record:", StringComparison.Ordinal)));
        Assert.Single(spy.Loads, l => l == LogLoad.Full);
    }

    [Fact]
    public async Task When_every_participant_marks_its_position_finished_the_debate_goes_to_judgment_after_that_round()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (request, _) => ScriptedDebateProvider.Valid(request, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Count);
        var closed = Assert.Single(log.RoundsClosed);
        Assert.Equal((1, DebatePhase.AwaitingJudgment, DebateJudgmentReason.Converged), (closed.Payload.Round, closed.Payload.Next, closed.Payload.Judgment));
    }
}
