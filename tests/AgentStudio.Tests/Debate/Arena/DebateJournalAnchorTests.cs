using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// docs/17 D-02: every turn of a debate carries the <c>AgentRunStarted</c> it was announced with (the debate, the task of
/// the turn and its attempt), and the journal ends its result line with it.
/// </summary>
public sealed class DebateJournalAnchorTests
{
    private static IReadOnlyList<string> Sorted(IEnumerable<AgentRunAnchor?> anchors) =>
        [.. anchors.Select(a => a is null ? "(sin ancla)" : JournalResultAnchor.Format(a)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Every_turn_names_its_start_in_the_journal()
    {
        using var world = new ScriptedDebateWorld();

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(limits: new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000)));
        await ScriptedDebateWorld.CompletedAsync(run);

        var started = world.LogStore.Of(ScriptedDebateWorld.Project)
            .Where(e => e.Kind == DecisionKind.AgentRunStarted)
            .Select(e => DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(e.PayloadJson))
            .Select(p => new AgentRunAnchor(p.RunId, p.Task?.Value, p.Attempt))
            .ToList();
        Assert.Equal(4, started.Count);
        Assert.All(started, a => Assert.Equal(run.Id.Value, a.Run));
        Assert.Contains(started, a => a.Task == "r1-arquitecto");
        Assert.Equal(Sorted(started), Sorted(world.Journal.Outcomes.Select(o => o.Outcome.Anchor)));
        Assert.Equal(Sorted(started), Sorted(world.Claude.Requests.Select(r => r.Anchor)));
    }
}
