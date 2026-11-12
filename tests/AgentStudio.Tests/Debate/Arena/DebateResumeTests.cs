using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// Resuming a debate (docs/15 D-04): the launches the log shows open are closed as interrupted, the sealed answers the process
/// lost are asked again, nothing announced is repeated, and a header that changed, a closed debate or a resume the user did not
/// approve is refused.
/// </summary>
public sealed class DebateResumeTests
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

    private static DebateRequest Mixed(ScriptedDebateWorld world) =>
        world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel);

    private static bool IsRound2(string? task) => task!.StartsWith("r2-", StringComparison.Ordinal);

    /// <summary>
    /// A debate whose process ended in round 2: round 1 published, the turn of <c>arquitecto</c> answered (sealed, so lost) and
    /// the turn of <c>critico</c> in course. Every answer of round 2 marks its position finished.
    /// </summary>
    private static async Task<IDebateRun> CrashedInRound2Async(ScriptedDebateWorld world, Flag crashed)
    {
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: IsRound2(r.TaskId));
        world.Codex.Reply = (r, _) =>
        {
            if (r.TaskId == "r2-critico" && !crashed.Value)
            {
                world.Codex.Hold = true;
            }

            return ScriptedDebateProvider.Valid(r, finished: IsRound2(r.TaskId));
        };

        var run = await world.OpenAsync(Mixed(world));
        await WaitUntilAsync(async () => world.Codex.Runs.Count == 2
            && (await world.DebateLogAsync(run.Id)).Turns.Any(t => t.Task!.Value.Value == "r2-arquitecto" && !t.IsOpen));
        await run.DisposeAsync();
        crashed.Value = true;
        world.Codex.Hold = false;
        return run;
    }

    [Fact]
    public async Task Resuming_closes_the_open_turns_asks_again_the_sealed_answers_lost_and_repeats_nothing_announced()
    {
        using var world = new ScriptedDebateWorld();
        var first = await CrashedInRound2Async(world, new Flag());

        var stopped = await CompletedAsync(first);
        Assert.Equal(DebatePhase.Deliberation, stopped.Phase);
        var before = await world.DebateLogAsync(first.Id);
        Assert.Equal(2, before.Contributions.Count);
        Assert.True(before.Turns.Single(t => t.Task!.Value.Value == "r2-critico").IsOpen);
        Assert.True(Assert.Single(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken)).Interrupted);

        await using var resumed = await world.Coordinator.ResumeAsync(world.Context, first.Id, TestContext.Current.CancellationToken);
        var result = await CompletedAsync(resumed);

        Assert.Equal((DebatePhase.Closed, DebateJudgmentReason.Converged), (result.Phase, result.JudgmentReason));
        Assert.Contains(world.Prompt.Asked, a => a.Action == DebateCoordinator.ResumeAction && a.RunId == first.Id.Value);
        var log = await world.DebateLogAsync(first.Id);

        // The open launch is closed as interrupted, not counted as a retry; both turns of round 2 are asked again.
        Assert.Equal(
            [(1, AttemptOutcome.Interrupted), (2, AttemptOutcome.Succeeded)],
            log.Turns.Where(t => t.Task!.Value.Value == "r2-critico").Select(t => (t.Attempt, t.Finished!.Status)));
        Assert.Equal(
            [(1, AttemptOutcome.Succeeded), (2, AttemptOutcome.Succeeded)],
            log.Turns.Where(t => t.Task!.Value.Value == "r2-arquitecto").Select(t => (t.Attempt, t.Finished!.Status)));

        // Nothing announced is repeated: round 1 was asked once and published once.
        Assert.Single(world.Claude.Requests, r => r.TaskId == "r1-arquitecto");
        Assert.Single(world.Codex.Requests, r => r.TaskId == "r1-critico");
        Assert.Equal([0, 1, 2, 3], log.Contributions.Select(c => c.Payload.Index));
        Assert.Equal([1, 1, 2, 2], log.Contributions.Select(c => c.Payload.Round));
        Assert.All(log.Contributions.Where(c => c.Payload.Round == 2), c => Assert.Equal(2, c.Payload.Attempt));

        // The sealed answer the process lost never reached the store.
        Assert.DoesNotContain(world.Evidence.Drafts, d => d.Kind == EvidenceKind.AgentStatement && d.TaskId == "r2-arquitecto" && d.Attempt == 1);
        Assert.False(Assert.Single(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken)).Interrupted);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task A_debate_whose_stored_header_changed_is_not_resumed_and_nothing_is_written()
    {
        using var world = new ScriptedDebateWorld();
        var first = await CrashedInRound2Async(world, new Flag());
        world.Debates.Replace(first.Id, r => r with { Limits = r.Limits with { MaxRounds = 9 } });
        var entries = (await world.LogAsync()).Entries.Count;

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.ResumeAsync(world.Context, first.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DebateUserTexts.CannotResume, Assert.IsType<UserTextException>(refused).Text.Code);
        Assert.Equal(entries, (await world.LogAsync()).Entries.Count);
        Assert.DoesNotContain(world.Prompt.Asked, a => a.Action == DebateCoordinator.ResumeAction);
        Assert.False(world.Activity.IsBusy(Project));
        var listed = Assert.Single(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken));
        Assert.Contains(listed.Problems, p => p.Contains("cabecera", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resume_the_user_does_not_approve_closes_nothing_and_launches_nothing()
    {
        using var world = new ScriptedDebateWorld();
        var first = await CrashedInRound2Async(world, new Flag());
        world.Prompt.Approve = a => a.Action != DebateCoordinator.ResumeAction;
        var launches = world.Claude.Requests.Count + world.Codex.Requests.Count;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.ResumeAsync(world.Context, first.Id, TestContext.Current.CancellationToken));

        var log = await world.DebateLogAsync(first.Id);
        Assert.True(log.Turns.Single(t => t.Task!.Value.Value == "r2-critico").IsOpen);
        Assert.DoesNotContain(log.Turns, t => t.Finished?.Status == AttemptOutcome.Interrupted);
        Assert.Equal(launches, world.Claude.Requests.Count + world.Codex.Requests.Count);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task A_closed_debate_is_not_resumed()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.ResumeAsync(world.Context, run.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DebateUserTexts.AlreadyFinished, Assert.IsType<UserTextException>(refused).Text.Code);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task A_debate_followed_by_this_process_is_not_resumed_twice()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        await using var run = await world.OpenAsync(world.FreeOnAnthropic());

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.ResumeAsync(world.Context, run.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DebateUserTexts.AlreadyRunning, Assert.IsType<UserTextException>(refused).Text.Code);
        world.Claude.Release();
        await CompletedAsync(run);
    }

    /// <summary>A flag the scripted CLIs read from their threads.</summary>
    private sealed class Flag
    {
        private volatile bool _value;

        public bool Value
        {
            get => _value;
            set => _value = value;
        }
    }
}
