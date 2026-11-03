using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Arena;

/// <summary>One launch of an agent in a debate, as the log recorded it: its start and, unless it never ended, its end.</summary>
public sealed record DebateAttempt(TaskId? Task, int Attempt, DecisionEntry StartedEntry, AgentRunStartedPayload Started, DecisionEntry? FinishedEntry, AgentRunFinishedPayload? Finished)
{
    public bool IsOpen => Finished is null;
}

/// <summary>
/// What the decision log says about one debate (docs/15 D-01 and D-03): its entries (those whose payload names the debate),
/// read once. The state is never stored: it comes from these entries, through <see cref="Events"/> and
/// <see cref="DebateStateReplayer"/>, and the tokens through <see cref="Usages"/> and <see cref="DebateBudget"/>.
/// </summary>
public sealed class DebateLog
{
    private readonly List<(DecisionEntry Entry, DecisionPayload Payload)> _read = [];
    private readonly List<string> _problems = [];

    private DebateLog(RunId debate, IReadOnlyList<DecisionEntry> entries)
    {
        Debate = debate;
        Entries = entries;
        foreach (var entry in entries)
        {
            try
            {
                if (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson) is { } payload)
                {
                    _read.Add((entry, payload));
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _problems.Add($"Entry {entry.Sequence} ({entry.Kind}): its payload cannot be read ({ex.Message}).");
            }
        }

        Opened = _read.Select(r => r.Payload).OfType<DebateOpenedPayload>().FirstOrDefault();
        OpenedEntry = _read.FirstOrDefault(r => r.Payload is DebateOpenedPayload).Entry;
        Contributions = [.. _read.Where(r => r.Payload is DebateContributionRecordedPayload).Select(r => (r.Entry, (DebateContributionRecordedPayload)r.Payload))];
        RoundsClosed = [.. _read.Where(r => r.Payload is DebateRoundClosedPayload).Select(r => (r.Entry, (DebateRoundClosedPayload)r.Payload))];
        Closed = _read.Where(r => r.Payload is DebateClosedPayload).Select(r => ((DecisionEntry, DebateClosedPayload)?)(r.Entry, (DebateClosedPayload)r.Payload)).FirstOrDefault();
        Evidence = [.. _read.Where(r => r.Payload is EvidenceRegisteredPayload).Select(r => (r.Entry, (EvidenceRegisteredPayload)r.Payload))];
        Attempts = ReadAttempts();
    }

    public RunId Debate { get; }

    /// <summary>The entries of the debate, in log order.</summary>
    public IReadOnlyList<DecisionEntry> Entries { get; }

    /// <summary>What could not be read; a debate with a problem is not resumed.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public DebateOpenedPayload? Opened { get; }

    public DecisionEntry? OpenedEntry { get; }

    public IReadOnlyList<(DecisionEntry Entry, DebateContributionRecordedPayload Payload)> Contributions { get; }

    public IReadOnlyList<(DecisionEntry Entry, DebateRoundClosedPayload Payload)> RoundsClosed { get; }

    public (DecisionEntry Entry, DebateClosedPayload Payload)? Closed { get; }

    public IReadOnlyList<(DecisionEntry Entry, EvidenceRegisteredPayload Payload)> Evidence { get; }

    /// <summary>Every launch of an agent in the debate (the turns and the judges), in the order they started.</summary>
    public IReadOnlyList<DebateAttempt> Attempts { get; }

    /// <summary>The launches of the participants: purpose <see cref="AgentRunPurpose.DebateParticipant"/>, task <c>r&lt;round&gt;-&lt;agent&gt;</c>.</summary>
    public IEnumerable<DebateAttempt> Turns =>
        Attempts.Where(a => a.Started.Purpose == AgentRunPurpose.DebateParticipant && DebateIds.TryReadTurn(a.Task?.Value, out _, out _));

    /// <summary>The entries of <paramref name="debate"/> in <paramref name="log"/>.</summary>
    public static DebateLog Of(IReadOnlyList<DecisionEntry> log, RunId debate)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new DebateLog(debate, RunLog.EntriesOf(log, debate.Value));
    }

    /// <summary>The payloads of a kind, with their entries, in log order.</summary>
    public IEnumerable<(DecisionEntry Entry, T Payload)> Of<T>()
        where T : DecisionPayload =>
        _read.Where(r => r.Payload is T).Select(r => (r.Entry, (T)r.Payload));

    /// <summary>The SHA-256 the log anchored for <paramref name="evidence"/> (its first <c>EvidenceRegistered</c>), or null.</summary>
    public string? AnchoredSha256(EvidenceId evidence) => Evidence.FirstOrDefault(e => e.Payload.Evidence == evidence).Payload?.Sha256;

    /// <summary>
    /// The events the domain replays (<see cref="DebateStateReplayer"/>), in log order: every published contribution, every
    /// closed round and the end. A contribution is dated when its turn answered (D-20); a round closes at the start of the
    /// debate plus the whole seconds that had passed (<see cref="DebateRoundClosedPayload.ElapsedSeconds"/>), never later than
    /// the instant it closed, so a limit the live debate did not reach is not reached by the replay either. The verdicts of
    /// an end are those <paramref name="verdicts"/> gives for its ids that are valid; the decision of a judge or of a panel is
    /// theirs, and that of a person (rule <c>user</c>) is not in the log.
    /// </summary>
    public IReadOnlyList<DebateEvent> Events(DateTimeOffset startedAt, Func<IReadOnlyList<string>, IReadOnlyList<(Verdict Verdict, bool Valid)>> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        var events = new List<DebateEvent>();
        foreach (var (entry, payload) in _read)
        {
            switch (payload)
            {
                case DebateContributionRecordedPayload published:
                    events.Add(new ContributionPublished(entry.RecordedAt, ToContribution(published)));
                    break;
                case DebateRoundClosedPayload closed:
                    events.Add(new RoundClosed(
                        startedAt + TimeSpan.FromSeconds(Math.Max(0, closed.ElapsedSeconds)),
                        closed.Round,
                        TokensOf(closed.TokensUsed),
                        closed.Judgment,
                        closed.Limit));
                    break;
                case DebateClosedPayload ended when string.Equals(ended.Rule, DebateClosedPayload.AbortedRule, StringComparison.Ordinal):
                    events.Add(new DebateAborted(entry.RecordedAt, ended.Reason));
                    break;
                case DebateClosedPayload ended:
                    var all = verdicts(ended.Verdicts);
                    var valid = all.Where(v => v.Valid).Select(v => v.Verdict).ToList();
                    VerdictDecision? decision = ended.Rule is JudgingPanel.Single or JudgingPanel.Agree && valid.Count > 0 ? valid[0].Decision : null;
                    events.Add(new DebateEnded(entry.RecordedAt, new PanelOutcome(ended.Outcome, decision, ended.ChosenProposal, ended.Rule), valid));
                    break;
            }
        }

        return events;
    }

    /// <summary>
    /// The usage of every launch of a participant (D-19), from its <c>AgentRunFinished</c> version 2: the whole prompt (with the
    /// cache) and the output, as the CLI reported them, and the estimate of the attempt from the characters of its prompt and
    /// of the contribution it published (<paramref name="characters"/> gives them for an evidence, or null when it cannot be
    /// read). An attempt that never ended counts its estimate: a missing figure never lets the limit pass. The judges do not
    /// count.
    /// </summary>
    public IReadOnlyList<TurnUsage> Usages(Func<EvidenceId, long?> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        var answers = Contributions.ToDictionary(c => (c.Payload.Turn, c.Payload.Attempt), c => c.Payload.Evidence);
        var usages = new List<TurnUsage>();
        foreach (var attempt in Turns)
        {
            var task = attempt.Task!.Value.Value;
            var chars = characters(attempt.Started.PromptEvidenceId) ?? 0;
            if (answers.TryGetValue((task, attempt.Attempt), out var answer))
            {
                chars += characters(answer) ?? 0;
            }

            var usage = attempt.Finished?.Usage;
            usages.Add(new TurnUsage(
                task,
                attempt.Attempt,
                Figure(usage?.InputTokens, "input tokens"),
                Figure(usage?.OutputTokens, "output tokens"),
                DebateBudget.EstimateOf(chars)));
        }

        return usages;
    }

    /// <summary>A published contribution as the domain keeps it: its evidence as the reference, its citations, and the moment its turn answered.</summary>
    public static DebateContribution ToContribution(DebateContributionRecordedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new DebateContribution(payload.Round, payload.Author, payload.Kind, payload.Evidence.Value, payload.Cites ?? [], payload.AnsweredAt, payload.RepliesTo);
    }

    /// <summary>The tokens a round closed with, as a whole number.</summary>
    public static long TokensOf(MetricSnapshot? snapshot) =>
        snapshot?.Value is { } value ? decimal.ToInt64(decimal.Truncate(value)) : 0;

    private static MetricValue<long> Figure(MetricSnapshot? snapshot, string what)
    {
        if (snapshot is null)
        {
            return MetricValue<long>.Missing($"no {what}: the attempt reported no usage");
        }

        try
        {
            return snapshot.ToInt64();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return MetricValue<long>.Missing($"the {what} recorded cannot be read: {ex.Message}");
        }
    }

    private List<DebateAttempt> ReadAttempts()
    {
        var attempts = new List<DebateAttempt>();
        var open = new Dictionary<(string? Task, int Attempt), int>();
        foreach (var (entry, payload) in _read)
        {
            switch (payload)
            {
                case AgentRunStartedPayload started:
                    open[(started.Task?.Value, started.Attempt)] = attempts.Count;
                    attempts.Add(new DebateAttempt(started.Task, started.Attempt, entry, started, null, null));
                    break;
                case AgentRunFinishedPayload finished when open.Remove((finished.Task?.Value, finished.Attempt), out var index):
                    attempts[index] = attempts[index] with { FinishedEntry = entry, Finished = finished };
                    break;
            }
        }

        return attempts;
    }
}
