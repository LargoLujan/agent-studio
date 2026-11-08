using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Observability.Context;

/// <summary>One part of the bar of context by parts (docs/17 D-13): its kind, its characters (exact) and its tokens, «estimado por Agent Studio».</summary>
public sealed record ContextPartFigure(ContextPartKind Kind, int Characters, MetricValue<long> Tokens);

/// <summary>
/// The context of a step by parts (docs/17 D-13, ADR-0052): what Agent Studio put in the prompt before the agent started, part by
/// part, and what the CLI reports beyond it, the conversation and the tools.
/// <list type="bullet">
/// <item>Each part is estimated by Agent Studio from its characters (divided by 4 and rounded up, docs/14 D-24); its
/// characters are exact.</item>
/// <item>The conversation is the occupancy the CLI reported for the step minus the parts, an estimate of Agent Studio (its
/// weakest figure is the estimate of the parts). Below zero it is zero: the parts are an estimate, and they then fill the bar.
/// When the CLI does not expose the occupancy (<c>codex exec</c>) the conversation is «no expuesto» and only the parts are
/// known; while no step came, it is missing. Never a zero that stands for a figure that is not there.</item>
/// </list>
/// </summary>
/// <param name="Parts">The parts, in the order of <see cref="ContextPartKind"/>; empty when the start recorded none (before version 3).</param>
/// <param name="Preamble">The tokens of all the parts, estimated; missing when there are no parts.</param>
/// <param name="Conversation">What the CLI reported beyond the parts.</param>
/// <param name="Used">The occupancy of the step as the CLI reported it (or why there is none).</param>
public sealed record ContextBreakdown(IReadOnlyList<ContextPartFigure> Parts, MetricValue<long> Preamble, MetricValue<long> Conversation, MetricValue<long> Used)
{
    /// <summary>Where the tokens of a part come from.</summary>
    public const string PartSource = "estimate: 4 chars/token (parts of the prompt measured by Agent Studio)";

    /// <summary>Where the conversation comes from.</summary>
    public const string ConversationSource = "estimate: occupancy reported by the CLI minus the parts of the prompt";

    /// <summary>Why the conversation is not exposed.</summary>
    public const string NotExposedReason = "the CLI does not expose the occupancy of the step: only the parts before the start are known";

    /// <summary>The tokens <paramref name="characters"/> are estimated to take: divided by 4, rounded up.</summary>
    public static long TokensOf(long characters) => (characters + 3) / 4;

    /// <summary>
    /// The breakdown of a step: the parts of its start (null or empty for a start without them) against the occupancy the CLI
    /// reported for it (the prompt tokens of its <c>ContextObserved</c>, or «no expuesto», or missing).
    /// </summary>
    public static ContextBreakdown Of(IReadOnlyList<ContextPartSnapshot>? parts, MetricValue<long> used)
    {
        var figures = (parts ?? [])
            .Where(p => p.Characters > 0)
            .OrderBy(p => p.Kind)
            .Select(p => new ContextPartFigure(p.Kind, p.Characters, MetricValue<long>.EstimatedBySystem(TokensOf(p.Characters), PartSource)))
            .ToList();
        var preamble = figures.Count == 0
            ? MetricValue<long>.Missing("the start of the attempt recorded no parts (before AgentRunStarted version 3)")
            : MetricValue<long>.EstimatedBySystem(figures.Sum(f => f.Tokens.Value!.Value), PartSource);

        MetricValue<long> conversation;
        if (used.Value is { } reported && preamble.Value is { } before)
        {
            conversation = MetricValue<long>.EstimatedBySystem(Math.Max(0, reported - before), ConversationSource);
        }
        else if (used.Origin == MetricOrigin.NotExposed)
        {
            conversation = MetricValue<long>.NotExposed(NotExposedReason);
        }
        else if (used.HasValue)
        {
            conversation = MetricValue<long>.Missing("without the parts of the prompt the conversation cannot be told from the rest");
        }
        else
        {
            conversation = MetricValue<long>.Missing(used.Source);
        }

        return new ContextBreakdown(figures, preamble, conversation, used);
    }

    /// <summary>The characters of the parts of <paramref name="kind"/>; 0 when the prompt had none.</summary>
    public int CharactersOf(ContextPartKind kind) => Parts.Where(p => p.Kind == kind).Sum(p => p.Characters);
}

/// <summary>The parts of one attempt as its start recorded them (<c>AgentRunStarted</c> version 3), with who it was and what it was for.</summary>
/// <param name="Sequence">The entry of the start in the log.</param>
public sealed record AttemptContextParts(
    string Run,
    TaskId? Task,
    int Attempt,
    AgentId Agent,
    AgentRunPurpose? Purpose,
    IReadOnlyList<ContextPartSnapshot> Parts,
    long Sequence);

/// <summary>
/// The parts of the prompts of a run, as the verified log recorded them before each launch (docs/17 D-13): what the bar by
/// parts of an attempt that already started and the analysis of consumption read. A start of version 2 or before has no parts
/// and is left out: they are never made up.
/// </summary>
public static class ContextPartsLog
{
    /// <summary>The parts of every start of <paramref name="run"/> that recorded them, in log order; an entry that cannot be read is left out.</summary>
    public static IReadOnlyList<AttemptContextParts> Of(IReadOnlyList<DecisionEntry> entries, string run)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        var found = new List<AttemptContextParts>();
        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.AgentRunStarted).OrderBy(e => e.Sequence))
        {
            AgentRunStartedPayload started;
            try
            {
                started = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(entry.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
            {
                continue;
            }

            if (string.Equals(started.RunId, run, StringComparison.Ordinal) && started.RecordsContextParts() && started.ContextParts is { Count: > 0 } parts)
            {
                found.Add(new AttemptContextParts(started.RunId, started.Task, started.Attempt, started.Agent, started.Purpose, parts, entry.Sequence));
            }
        }

        return found;
    }

    /// <summary>The parts of the last start of <paramref name="task"/> (null for the planner) and <paramref name="attempt"/> of the run, or null when it recorded none.</summary>
    public static AttemptContextParts? Find(IReadOnlyList<DecisionEntry> entries, string run, TaskId? task, int attempt) =>
        Of(entries, run).LastOrDefault(p => p.Task == task && p.Attempt == attempt);

    /// <summary>For logs: the parts as <c>Protocol=6000, Requirements=1800</c>.</summary>
    public static string Describe(IReadOnlyList<ContextPartSnapshot> parts) =>
        string.Join(", ", parts.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Kind}={p.Characters}")));
}
