using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>
/// docs/17 D-13 and F13-6 (ADR-0052): the bar of context by parts. The parts are estimated by Agent Studio from their exact
/// characters; the conversation is what the CLI reported beyond them; «no expuesto» and «sin dato» are never a zero.
/// </summary>
public sealed class ContextBreakdownTests
{
    private static readonly IReadOnlyList<ContextPartSnapshot> Parts =
    [
        new(ContextPartKind.Requirements, 1800),
        new(ContextPartKind.Protocol, 6001),
        new(ContextPartKind.Skills, 400),
    ];

    [Fact]
    public void Each_part_is_its_characters_divided_by_four_and_the_conversation_is_the_rest_of_the_reported_step()
    {
        var breakdown = ContextBreakdown.Of(Parts, MetricValue<long>.Exact(10_000, "claude stream-json"));

        Assert.Equal([ContextPartKind.Protocol, ContextPartKind.Requirements, ContextPartKind.Skills], breakdown.Parts.Select(p => p.Kind));
        Assert.Equal([1501L, 450L, 100L], breakdown.Parts.Select(p => p.Tokens.Value!.Value));
        Assert.All(breakdown.Parts, p => Assert.Equal(MetricOrigin.EstimatedBySystem, p.Tokens.Origin));
        Assert.Equal((2051L, MetricOrigin.EstimatedBySystem), (breakdown.Preamble.Value!.Value, breakdown.Preamble.Origin));
        Assert.Equal((7949L, MetricOrigin.EstimatedBySystem), (breakdown.Conversation.Value!.Value, breakdown.Conversation.Origin));
        Assert.Equal(1800, breakdown.CharactersOf(ContextPartKind.Requirements));
        Assert.Equal(0, breakdown.CharactersOf(ContextPartKind.Memory));
    }

    [Fact]
    public void Codex_shows_its_parts_and_says_the_occupancy_is_not_exposed()
    {
        var breakdown = ContextBreakdown.Of(Parts, MetricValue<long>.NotExposed("codex exec"));

        Assert.Equal(3, breakdown.Parts.Count);
        Assert.Equal(MetricOrigin.NotExposed, breakdown.Conversation.Origin);
        Assert.Null(breakdown.Conversation.Value);
    }

    [Fact]
    public void Before_the_first_step_the_conversation_is_missing_never_zero()
    {
        var breakdown = ContextBreakdown.Of(Parts, MetricValue<long>.Missing("aún no ha llegado ningún paso"));

        Assert.Equal(MetricOrigin.Missing, breakdown.Conversation.Origin);
        Assert.Null(breakdown.Conversation.Value);
        Assert.Equal("aún no ha llegado ningún paso", breakdown.Conversation.Source);
    }

    [Fact]
    public void A_start_without_parts_has_no_preamble_and_cannot_tell_the_conversation_apart()
    {
        var breakdown = ContextBreakdown.Of(null, MetricValue<long>.Exact(10_000, "claude stream-json"));

        Assert.Empty(breakdown.Parts);
        Assert.Equal(MetricOrigin.Missing, breakdown.Preamble.Origin);
        Assert.Equal(MetricOrigin.Missing, breakdown.Conversation.Origin);
        Assert.Equal(10_000, breakdown.Used.Value);
    }

    [Fact]
    public void Parts_larger_than_the_reported_step_leave_a_conversation_of_zero_estimated()
    {
        var breakdown = ContextBreakdown.Of(Parts, MetricValue<long>.Exact(1_000, "claude stream-json"));

        Assert.Equal((0L, MetricOrigin.EstimatedBySystem), (breakdown.Conversation.Value!.Value, breakdown.Conversation.Origin));
    }

    [Fact]
    public void The_log_gives_the_parts_of_each_start_of_version_3_of_the_run_and_nothing_of_before()
    {
        var log = new DecisionLog();
        var actor = new Actor(ActorKind.System, "agent-studio");
        void Start(string run, string? task, int attempt, int version, IReadOnlyList<ContextPartSnapshot>? parts) =>
            log.Append(T0.AddMinutes(log.Entries.Count), actor, DecisionKind.AgentRunStarted, "Arranca", DecisionPayloadSerializer.Serialize(
                new AgentRunStartedPayload(task is null ? null : new TaskId(task), attempt, new AgentId("ejecutor"), ProviderId.ClaudeCode, VendorId.Anthropic, null, "wt", new EvidenceId("ev"), HashA, [], AgentRunPurpose.Executor, null, parts)
                {
                    RunId = run,
                    V = version,
                }));

        Start("20261107-a1b2c3", "T1", 1, 2, null);
        Start("20261107-a1b2c3", "T1", 2, 3, Parts);
        Start("20261107-ffffff", "T1", 1, 3, Parts);
        Start("20261107-a1b2c3", null, 1, 3, [new ContextPartSnapshot(ContextPartKind.Protocol, 100)]);

        var found = ContextPartsLog.Of(log.Entries, "20261107-a1b2c3");

        Assert.Equal([(new TaskId("T1"), 2), (null, 1)], found.Select(f => (f.Task, f.Attempt)));
        Assert.Equal(Parts, ContextPartsLog.Find(log.Entries, "20261107-a1b2c3", new TaskId("T1"), 2)!.Parts);
        Assert.Null(ContextPartsLog.Find(log.Entries, "20261107-a1b2c3", new TaskId("T1"), 1));
        Assert.Equal("Requirements=1800, Protocol=6001, Skills=400", ContextPartsLog.Describe(Parts));
    }
}
