using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;
using DebateProviders = AgentStudio.Tests.Debate.Fakes.FakeProjectAgentProviders;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// F8-5 in the debates (docs/16 WP1, the same policy of waits as the engine): a turn that waits for the usage limit of its
/// provider to reset (docs/15 D-22) waits on the wall clock, looking at it again every few seconds, so a sleep of the machine
/// neither leaves it waiting past the reset nor cuts it short. The arena runs on a <see cref="SuspendableClock"/>: awake
/// time goes by on the clock of the world, which also runs the timers, as .NET's do on Windows; a sleep moves only the wall
/// clock.
/// </summary>
public sealed class DebateQuotaWaitTests
{
    /// <summary>When the usage limit of the first turn of the architect resets, from the start.</summary>
    private static readonly TimeSpan Reset = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reset_that_passed_while_the_machine_slept_is_over_on_waking()
    {
        using var world = new ScriptedDebateWorld();
        var clock = world.ArenaOnSuspendableClock();
        LimitTheFirstTurnOfTheArchitect(world, clock);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitingForTheResetAsync(clock);
        var asleepAt = world.Clock.GetUtcNow();
        clock.Sleep(TimeSpan.FromMinutes(12)); // past the reset, and within the 30 minutes of the debate
        await WakeUntilAsync(world, () => world.Claude.Requests.Count == 3);
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
        Assert.True(world.Clock.GetUtcNow() - asleepAt < TimeSpan.FromMinutes(1), "The wait ended on waking, not ten minutes of awake time later.");
    }

    [Fact]
    public async Task A_reset_still_ahead_on_waking_is_waited_for_until_it_comes()
    {
        using var world = new ScriptedDebateWorld();
        var clock = world.ArenaOnSuspendableClock();
        LimitTheFirstTurnOfTheArchitect(world, clock);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitingForTheResetAsync(clock);
        clock.Sleep(TimeSpan.FromMinutes(4));
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await WaitingForTheResetAsync(clock, looks: 2); // it looked on waking, and waits on
        await RunTestSupport.WaitUntilAsync(() => world.Claude.Requests.Count >= 2, "the first turn of each participant");

        Assert.Equal(2, world.Claude.Requests.Count);

        world.Clock.Advance(TimeSpan.FromMinutes(6)); // four minutes asleep and six awake: the reset, by the wall clock
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
    }

    /// <summary>
    /// The turn works out how long to look from the clock, and then arms a timer for that long: a clock that reaches the reset
    /// in between (a test that advances it as soon as the turn waits, or a machine that falls asleep right then) leaves a timer
    /// that counts from later. On a fake clock nobody advances again it never came, and the debate waited for ever (docs/18
    /// P42); the turn now looks again at once. Its looks are the only timers of five seconds of the debate.
    /// </summary>
    [Fact]
    public async Task A_wait_for_a_reset_ends_when_the_clock_reaches_it_while_the_turn_arms_its_timer()
    {
        var clock = new JumpingFakeTimeProvider(AgentStudio.Tests.Domain.TestData.T0);
        using var world = new ScriptedDebateWorld(clock);
        var limited = 0;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref limited) == 1
            ? new ScriptedDebateReply(AgentRunStatus.RateLimited, null, null, "límite de uso", clock.GetUtcNow() + Reset)
            : ScriptedDebateProvider.Valid(r, finished: true);
        clock.JumpWhenArming(TimeSpan.FromSeconds(5), Reset);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var result = await CompletedAsync(run);

        Assert.True(clock.Jumped);
        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
    }

    /// <summary>
    /// The class of the end decides, not its status (docs/16 D-41): a window of Claude rejected without a 429 ends the turn as
    /// a failure with the class <c>WindowQuota</c>, and the turn waits for its reset instead of being retried.
    /// </summary>
    [Fact]
    public async Task A_window_that_ran_out_is_waited_for_in_a_turn_even_when_it_ends_as_a_failure()
    {
        using var world = new ScriptedDebateWorld();
        var resetsAt = world.Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        var failed = 0;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref failed) == 1
            ? new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "You've hit your session limit")
            : ScriptedDebateProvider.Valid(r, finished: true);
        var classified = 0;
        var claude = new ClassifiedEnds(world.Claude, (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref classified) == 1
            ? new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resetsAt)
            : null);
        world.Ports = world.Ports with
        {
            Providers = new DebateProviders(
                AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(claude, world.Journal, world.Clock),
                AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(world.Codex, world.Journal, world.Clock)),
        };
        var clock = world.ArenaOnSuspendableClock();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await RunTestSupport.WaitUntilAsync(() => world.Claude.Requests.Count >= 2, "the first turn of each participant");
        await WaitingForTheResetAsync(clock);

        Assert.Equal(2, world.Claude.Requests.Count); // waiting for the reset, not retried

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
    }

    /// <summary>
    /// An overloaded provider (the class <c>RateOrOverload</c>: a 529 of Claude, with no reset time) is no usage limit (docs/16
    /// D-41): the turn waits a short time and gets its one retry, as it did before Phase 8, and nobody is asked.
    /// </summary>
    [Fact]
    public async Task An_overloaded_turn_waits_a_short_time_and_gets_its_retry()
    {
        using var world = new ScriptedDebateWorld();
        var failed = 0;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref failed) == 1
            ? new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "Overloaded")
            : ScriptedDebateProvider.Valid(r, finished: true);
        var classified = 0;
        var claude = new ClassifiedEnds(world.Claude, (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref classified) == 1
            ? new AgentRunEnd(AttemptEndClass.RateOrOverload)
            : null);
        world.Ports = world.Ports with
        {
            Providers = new DebateProviders(
                AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(claude, world.Journal, world.Clock),
                AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(world.Codex, world.Journal, world.Clock)),
        };
        var clock = world.ArenaOnSuspendableClock();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await RunTestSupport.WaitUntilAsync(() => world.Claude.Requests.Count >= 2, "the first turn of each participant");
        await WaitingForTheResetAsync(clock);

        Assert.Equal(2, world.Claude.Requests.Count); // a short wait before the retry

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunTestSupport.WaitUntilAsync(() => world.Claude.Requests.Count >= 3 || run.Snapshot().Awaiting is not null, "the retry, or a wait for a person");

        Assert.Null(run.Snapshot().Awaiting); // nobody is asked
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.Equal(3, world.Claude.Requests.Count);
    }

    /// <summary>The first turn of the architect hits the usage limit of Claude, which resets <see cref="Reset"/> from now; every other turn answers.</summary>
    private static void LimitTheFirstTurnOfTheArchitect(ScriptedDebateWorld world, TimeProvider clock)
    {
        var limited = 0;
        world.Claude.Reply = (r, _) => r.Agent.Id == Arquitecto && Interlocked.Increment(ref limited) == 1
            ? new ScriptedDebateReply(AgentRunStatus.RateLimited, null, null, "límite de uso", clock.GetUtcNow() + Reset)
            : ScriptedDebateProvider.Valid(r, finished: true);
    }

    /// <summary>
    /// The turn waits for the reset: it armed <paramref name="looks"/> timers no longer than the wait (the only other timer of
    /// the arena, the deadline of the round, is due in about 30 minutes). Once one is armed, advancing the clock fires it.
    /// </summary>
    private static Task WaitingForTheResetAsync(SuspendableClock clock, int looks = 1) =>
        clock.ArmedAsync(due => due > TimeSpan.Zero && due <= Reset, looks, "the wait of the turn for the reset");

    /// <summary>The machine wakes: awake time goes by five seconds at a time, a minute at most, until <paramref name="condition"/> holds.</summary>
    private static async Task WakeUntilAsync(ScriptedDebateWorld world, Func<bool> condition)
    {
        for (var beat = 0; beat < 12; beat++)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(5));
            var settle = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            while (DateTime.UtcNow < settle)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10, Ct);
            }
        }

        throw new TimeoutException($"The turn did not run again within a minute of waking: {world.Claude.Requests.Count} requests to Claude.");
    }
}
