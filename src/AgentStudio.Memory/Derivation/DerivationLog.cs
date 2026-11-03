using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Memory.Derivation;

/// <summary>An entry of the log with its typed payload.</summary>
internal sealed record LogItem(DecisionEntry Entry, DecisionPayload Payload)
{
    public long Sequence => Entry.Sequence;
}

/// <summary>
/// A verified decision log as the derivation reads it (docs/14 D-18 and D-20): the typed entries of each unit of the log (a
/// run, and since Phase 5 a debate <c>deb-…</c> or an audit <c>aud-…</c>, which the log selects the same way) in the order
/// of the log, the evidence the log anchored (<c>EvidenceRegistered</c>), the verdicts it announced, the messages of the
/// alerts already raised, and where the last history accepted as a new origin begins
/// (<see cref="DecisionRecorder.OriginAcceptedAlert"/>).
/// <list type="bullet">
/// <item>An entry whose payload cannot be read (one Agent Studio did not write) is left out.</item>
/// <item>Entries outside every run (<see cref="DecisionPayload.NoRun"/>: memory, reviews, the acceptance of a history)
/// belong to no run, so nothing is derived from them.</item>
/// <item>A run whose first entry is older than the last acceptance of a history as a new origin is inherited: it came with
/// a history nobody here recorded. Memory announced now is trusted whatever it describes, so deriving from such a run
/// would turn what a planted log says into trusted memory and bypass the quarantine of D-20. Inherited runs, and the
/// evidence they anchored, are never read.</item>
/// </list>
/// </summary>
internal sealed class DerivationLog
{
    private readonly List<(string Run, List<LogItem> Items)> _runs = [];
    private readonly Dictionary<string, List<LogItem>> _byRun = new(StringComparer.Ordinal);
    private readonly Dictionary<EvidenceId, (string Run, EvidenceRegisteredPayload Payload)> _anchors = new();
    private readonly Dictionary<string, (string Run, VerdictIssuedPayload Payload)> _verdicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _alerts = new(StringComparer.Ordinal);

    private DerivationLog()
    {
    }

    /// <summary>The sequence of the last acceptance of a history as a new origin, or null when there was none.</summary>
    public long? OriginAcceptance { get; private set; }

    /// <summary>The runs recorded here (not inherited), in the order they first appear, each with its entries in log order.</summary>
    public IEnumerable<(string Run, IReadOnlyList<LogItem> Items)> OwnRuns =>
        _runs.Where(r => !Inherited(r.Items[0].Sequence)).Select(r => (r.Run, (IReadOnlyList<LogItem>)r.Items));

    /// <summary>Reads <paramref name="entries"/>, a log that verified.</summary>
    public static DerivationLog Of(IReadOnlyList<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var log = new DerivationLog();
        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            if (Read(entry) is not { } payload)
            {
                continue;
            }

            if (payload is AuditAlertPayload alert)
            {
                if (string.Equals(alert.Code, DecisionRecorder.OriginAcceptedAlert, StringComparison.Ordinal))
                {
                    log.OriginAcceptance = entry.Sequence;
                }

                if (!log._alerts.TryGetValue(alert.Code, out var messages))
                {
                    log._alerts.Add(alert.Code, messages = new HashSet<string>(StringComparer.Ordinal));
                }

                messages.Add(alert.Message);
            }

            var run = payload.RunId;
            if (string.IsNullOrWhiteSpace(run))
            {
                continue;
            }

            if (!log._byRun.TryGetValue(run, out var items))
            {
                log._byRun.Add(run, items = []);
                log._runs.Add((run, items));
            }

            items.Add(new LogItem(entry, payload));
            switch (payload)
            {
                case EvidenceRegisteredPayload registered:
                    log._anchors.TryAdd(registered.Evidence, (run, registered));
                    break;
                case VerdictIssuedPayload issued:
                    log._verdicts.TryAdd(issued.VerdictId, (run, issued));
                    break;
            }
        }

        return log;
    }

    /// <summary>
    /// The first announcement of the verdict <paramref name="id"/> in a unit of its own (the verdicts of a debate are in the
    /// debate; those of a revision, in the run of the challenged verdict), or null: one this log does not announce, or one of
    /// an inherited unit.
    /// </summary>
    public VerdictIssuedPayload? Verdict(string id) =>
        _verdicts.TryGetValue(id, out var verdict) && IsOwn(verdict.Run) ? verdict.Payload : null;

    /// <summary>
    /// How the log anchored <paramref name="evidence"/>: its first <c>EvidenceRegistered</c>, in a run of its own and with a
    /// well-formed SHA-256. Null when the log never anchored it so (an evidence only named, like the judge's answer).
    /// </summary>
    public EvidenceRegisteredPayload? Anchor(EvidenceId evidence) =>
        _anchors.TryGetValue(evidence, out var anchor) && IsOwn(anchor.Run) && ContentHash.IsSha256Hex(anchor.Payload.Sha256) ? anchor.Payload : null;

    /// <summary>
    /// The plan with <paramref name="planHash"/> as the orchestrator proposed it in a run of its own (<c>PlanCreated</c>
    /// citing the evidence of its answer), or null: a plan the person wrote or edited, or one only <c>PlanRevised</c> names,
    /// cites no answer of the orchestrator.
    /// </summary>
    public PlanCreatedPayload? ProposedPlan(string planHash) =>
        OwnRuns
            .SelectMany(r => r.Items)
            .Select(i => i.Payload)
            .OfType<PlanCreatedPayload>()
            .FirstOrDefault(p => p.Source == PlanSource.OrchestratorAgent && p.RawPlanEvidence is not null && string.Equals(p.PlanHash, planHash, StringComparison.Ordinal));

    /// <summary>The messages of the alerts with <paramref name="code"/> the log holds, inside or outside a run.</summary>
    public HashSet<string> AlertMessages(string code) =>
        _alerts.TryGetValue(code, out var messages) ? new HashSet<string>(messages, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    private bool IsOwn(string run) => _byRun.TryGetValue(run, out var items) && !Inherited(items[0].Sequence);

    private bool Inherited(long sequence) => OriginAcceptance is { } accepted && sequence < accepted;

    /// <summary>The payload of <paramref name="entry"/>, or null when it has none or it cannot be read.</summary>
    private static DecisionPayload? Read(DecisionEntry entry)
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
