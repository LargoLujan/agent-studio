using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Metrics;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// F5-3: the limits measured by the system cut the debate and send it to judgment (docs/15 D-19 to D-22): rounds, the deadline
/// of the system clock, the tokens of the log (a missing figure is estimated and counts) and the launches allowed; and a turn
/// that fails gets its correction, its retry and then a person.
/// </summary>
public sealed class DebateLimitsTests
{
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static DebateRequest Mixed(ScriptedDebateWorld world, DebateLimits? limits = null, int? maxAgentRuns = null) =>
        world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel, limits: limits, maxAgentRuns: maxAgentRuns);

    [Fact]
    public async Task The_round_limit_sends_the_debate_to_judgment_after_its_last_round()
    {
        using var world = new ScriptedDebateWorld();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(limits: new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000)));
        var result = await CompletedAsync(run);

        Assert.Equal((DebateJudgmentReason.Limit, DebateLimitReached.Rounds), (result.JudgmentReason, result.LimitReached));
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(4, log.Contributions.Count);
        Assert.Equal((2, DebateLimitReached.Rounds), (log.RoundsClosed[^1].Payload.Round, log.RoundsClosed[^1].Payload.Limit));
    }

    [Fact]
    public async Task The_deadline_cancels_the_turns_in_course_and_publishes_with_its_answer_time_the_turn_that_already_answered()
    {
        using var world = new ScriptedDebateWorld();
        world.Codex.Hold = true;

        await using var run = await world.OpenAsync(Mixed(world, new DebateLimits(4, TimeSpan.FromMinutes(10), null)));
        await WaitUntilAsync(async () => (await world.DebateLogAsync(run.Id)).Turns.Any(t => t.Task!.Value.Value == "r1-arquitecto" && !t.IsOpen) && world.Codex.Runs.Count == 1);
        var answeredAt = (await world.DebateLogAsync(run.Id)).Turns.Single(t => t.Task!.Value.Value == "r1-arquitecto").FinishedEntry!.RecordedAt;

        world.Clock.Advance(TimeSpan.FromMinutes(11));
        var result = await CompletedAsync(run);

        Assert.Equal((DebateJudgmentReason.Limit, DebateLimitReached.Duration), (result.JudgmentReason, result.LimitReached));
        var log = await world.DebateLogAsync(run.Id);
        var published = Assert.Single(log.Contributions);
        Assert.Equal(Arquitecto, published.Payload.Author);
        Assert.Equal(answeredAt, published.Payload.AnsweredAt);
        var cancelled = log.Turns.Single(t => t.Task!.Value.Value == "r1-critico");
        Assert.Equal(AttemptOutcome.Cancelled, cancelled.Finished!.Status);
        Assert.Single(world.Codex.Requests);
        Assert.True(world.Codex.Runs[0].CancelRequested);
        Assert.Equal((1, DebateLimitReached.Duration), (log.RoundsClosed.Single().Payload.Round, log.RoundsClosed.Single().Payload.Limit));
    }

    [Fact]
    public async Task The_tokens_come_from_the_log_and_a_round_expected_to_pass_the_limit_is_not_launched()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, usage: ScriptedDebateProvider.Usage(59_000, 1_000));

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(limits: new DebateLimits(4, TimeSpan.FromMinutes(30), 200_000)));
        var result = await CompletedAsync(run);

        Assert.Equal((DebateJudgmentReason.Limit, DebateLimitReached.Tokens), (result.JudgmentReason, result.LimitReached));
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Count);
        Assert.Equal(2, world.Claude.Requests.Count);
        var cut = log.RoundsClosed[^1].Payload;
        Assert.Equal((2, DebateLimitReached.Tokens, 120_000L), (cut.Round, cut.Limit, DebateLog.TokensOf(cut.TokensUsed)));
        Assert.False(cut.TokensIncludeEstimates);
        Assert.DoesNotContain("estimación", cut.Reason, StringComparison.Ordinal);
        Assert.Equal(120_000, run.Snapshot().Tokens.Sum);
    }

    [Fact]
    public async Task A_missing_figure_is_estimated_counts_for_the_limit_and_the_cut_says_an_estimate_triggered_it()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r) with { Usage = null };

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(limits: new DebateLimits(4, TimeSpan.FromMinutes(30), 2_500)));
        var result = await CompletedAsync(run);

        Assert.Equal(DebateLimitReached.Tokens, result.LimitReached);
        var log = await world.DebateLogAsync(run.Id);
        var cut = log.RoundsClosed[^1].Payload;
        Assert.True(cut.TokensIncludeEstimates);
        Assert.Contains("disparado por una estimación", cut.Reason, StringComparison.Ordinal);
        Assert.Equal(MetricOrigin.EstimatedBySystem, cut.TokensUsed.Origin);
        var usages = log.Usages(id => world.Evidence.ContentText(id).Length);
        Assert.All(usages, u => Assert.True(u.Estimate > 0));
        Assert.Equal(DebateBudget.Used(usages).Sum, DebateLog.TokensOf(cut.TokensUsed));
    }

    [Fact]
    public async Task A_round_the_launches_allowed_cannot_cover_is_not_launched()
    {
        using var world = new ScriptedDebateWorld();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(maxAgentRuns: 3));
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.TurnMissing, result.JudgmentReason);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, world.Claude.Requests.Count);
        Assert.Contains("ejecuciones", log.RoundsClosed[^1].Payload.Reason, StringComparison.Ordinal);
        Assert.Equal(2, run.Snapshot().AgentRuns);
    }

    [Fact]
    public async Task An_answer_that_does_not_read_gets_one_correction_with_its_problems_as_data_and_nothing_of_it()
    {
        using var world = new ScriptedDebateWorld();
        var launches = 0;
        world.Claude.Reply = (r, _) =>
        {
            if (r.Agent.Id == Arquitecto && Interlocked.Increment(ref launches) == 1)
            {
                return ScriptedDebateProvider.Valid(r, customize: a => a["kind"] = "VOTO-SECRETO");
            }

            return ScriptedDebateProvider.Valid(r, finished: true);
        };

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        var correction = world.Claude.Requests.Where(r => r.Agent.Id == Arquitecto).ElementAt(1);
        Assert.Contains(ParticipantPrompt.ProblemsOpen, correction.Prompt, StringComparison.Ordinal);
        Assert.Contains("kind\\u0027 is not one of Proposal.", correction.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("VOTO-SECRETO", correction.Prompt, StringComparison.Ordinal);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Single(c => c.Payload.Author == Arquitecto).Payload.Attempt);
        Assert.Equal([1, 2], log.Turns.Where(t => t.Task!.Value.Value == "r1-arquitecto").Select(t => t.Attempt));
        Assert.DoesNotContain(log.Entries, e => e.PayloadJson.Contains("VOTO-SECRETO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_that_fails_twice_leaves_its_round_sealed_and_waits_for_a_person_who_retries_it()
    {
        using var world = new ScriptedDebateWorld();
        var fail = true;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Volatile.Read(ref fail)
            ? new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "el CLI falló")
            : ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => Task.FromResult(run.Snapshot().Awaiting is not null));

        var awaiting = run.Snapshot().Awaiting!;
        Assert.Equal((DebateAwaitReason.FailedTurn, "P1"), (awaiting.Reason, awaiting.Participant));
        Assert.Contains("el CLI falló", awaiting.Message, StringComparison.Ordinal);
        var waiting = await world.DebateLogAsync(run.Id);
        Assert.Empty(waiting.Contributions);
        Assert.Equal([1, 2], waiting.Turns.Where(t => t.Task!.Value.Value == "r1-arquitecto").Select(t => t.Attempt));

        Volatile.Write(ref fail, false);
        await run.RetryTurnAsync(Arquitecto);
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Count);
        Assert.Equal(3, log.Contributions.Single(c => c.Payload.Author == Arquitecto).Payload.Attempt);
    }

    [Fact]
    public async Task A_person_may_ask_for_judgment_with_what_there_is_when_a_turn_failed_twice()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto ? new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "fallo") : ScriptedDebateProvider.Valid(r);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => Task.FromResult(run.Snapshot().Awaiting is not null));
        await run.RequestJudgmentAsync();
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.TurnMissing, result.JudgmentReason);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(Revisor, Assert.Single(log.Contributions).Payload.Author);
        Assert.Contains("P1", log.RoundsClosed.Single().Payload.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aborting_while_a_turn_waits_publishes_nothing_sealed_and_closes_the_debate_as_aborted()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto ? new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "fallo") : ScriptedDebateProvider.Valid(r);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => Task.FromResult(run.Snapshot().Awaiting is not null));
        await run.AbortAsync("Ya no hace falta.");
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Aborted), (result.Phase, result.Outcome));
        var log = await world.DebateLogAsync(run.Id);
        Assert.Empty(log.Contributions);
        Assert.Equal((DebateOutcome.Aborted, DebateClosedPayload.AbortedRule, "Ya no hace falta."), (log.Closed!.Value.Payload.Outcome, log.Closed.Value.Payload.Rule, log.Closed.Value.Payload.Reason));
        Assert.Empty(world.Judgment.Calls);
    }

    [Fact]
    public async Task The_usage_limit_of_a_provider_is_waited_alone_when_the_cli_says_when_it_resets()
    {
        using var world = new ScriptedDebateWorld();
        var limited = 0;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref limited) == 1
            ? new ScriptedDebateReply(AgentRunStatus.RateLimited, null, null, "límite de uso", world.Clock.GetUtcNow() + TimeSpan.FromMinutes(5))
            : ScriptedDebateProvider.Valid(r, finished: true);
        var clock = world.ArenaOnSuspendableClock();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(async () => (await world.DebateLogAsync(run.Id)).Turns.Any(t => t.Finished?.Status == AttemptOutcome.RateLimited));
        await WaitUntilAsync(() => Task.FromResult(world.Claude.Requests.Count >= 2)); // the first turn of each participant
        await clock.ArmedAsync(due => due > TimeSpan.Zero && due <= TimeSpan.FromMinutes(5), 1, "the wait of the turn for the reset");
        Assert.Equal(2, world.Claude.Requests.Count);

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
    }

    [Fact]
    public async Task A_usage_limit_without_a_reset_time_waits_for_a_person()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto
            ? new ScriptedDebateReply(AgentRunStatus.RateLimited, null, null, "límite de uso")
            : ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => Task.FromResult(run.Snapshot().Awaiting is not null));

        Assert.Equal(DebateAwaitReason.ProviderLimit, run.Snapshot().Awaiting!.Reason);
        await run.AbortAsync("Sin cuota.");
        await CompletedAsync(run);
    }
}
