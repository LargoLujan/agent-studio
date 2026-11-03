using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/17 D-02: every launch of the engine (the planner and each attempt of a task) carries the <c>AgentRunStarted</c> it
/// was announced with, and the journal ends its result line with it, so the derivation can match each entry of the journal
/// with its anchored start. One journal entry per start, and no entry without one.
/// </summary>
public sealed class JournalAnchorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<string> Sorted(IEnumerable<AgentRunAnchor?> anchors) =>
        [.. anchors.Select(a => a is null ? "(sin ancla)" : JournalResultAnchor.Format(a)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_planner_and_every_attempt_of_a_task_name_their_start_in_the_journal()
    {
        using var harness = new EngineHarness();
        var proposal = await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, Ct);
        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);

        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("boom"));
        await using var run = await harness.StartAsync(Plan(Work("T1") with { MaxRetries = 2 }));
        await run.CompleteAsync(harness);

        var started = harness.Payloads<AgentRunStartedPayload>().Select(p => new AgentRunAnchor(p.Payload.RunId, p.Payload.Task?.Value, p.Payload.Attempt)).ToList();
        Assert.Contains(started, a => a.Task is null); // the planner
        Assert.Contains(started, a => a is { Task: "T1", Attempt: 2 }); // the retry of the task
        Assert.Equal(Sorted(started), Sorted(harness.Journal.Outcomes.Select(o => o.Outcome.Anchor)));
        Assert.Equal(
            Sorted(started),
            Sorted(harness.Claude.Starts.Select(s => s.Request.Anchor).Concat(harness.Codex.Starts.Select(s => s.Request.Anchor))));
    }
}
