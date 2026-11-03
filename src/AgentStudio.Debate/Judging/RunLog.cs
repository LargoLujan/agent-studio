using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Reads what the decision log says about one orchestration run: which entries are its own, who produced its work and
/// the events that rebuild the state of its tasks. Everything comes from the (hash-chained) log, never from a caller.
/// </summary>
public static class RunLog
{
    /// <summary>
    /// The entries of the run, in log order: those whose payload names the run (<c>runId</c>) and the corrections of
    /// those entries. Entries whose payload is not a JSON object name no run.
    /// </summary>
    public static IReadOnlyList<DecisionEntry> EntriesOf(IEnumerable<DecisionEntry> log, string runId)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var included = new HashSet<long>();
        var entries = new List<DecisionEntry>();
        foreach (var entry in log.OrderBy(e => e.Sequence))
        {
            var ofRun = NamesRun(entry.PayloadJson, runId)
                || (entry.Kind == DecisionKind.Correction && entry.Corrects is { } corrected && included.Contains(corrected));
            if (ofRun)
            {
                included.Add(entry.Sequence);
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// The vendors of everyone who produced the work of the run, which the judge must not share (docs/13, decision 7).
    /// It mirrors <see cref="PlanValidator"/> on the recorded facts: the orchestrators of the roster and whoever wrote
    /// the plan, the agents assigned to any task that is not a review, and every agent that was launched for the run
    /// (with the vendor it ran on). Judges and auditors of the roster review the work; they never count, so an earlier
    /// run of the judge does not make it exclude itself.
    /// </summary>
    public static IReadOnlySet<VendorId> ProducerVendors(IReadOnlyList<DecisionEntry> runEntries)
    {
        ArgumentNullException.ThrowIfNull(runEntries);

        var roster = new Dictionary<AgentId, RosterEntry>();
        foreach (var entry in runEntries.Where(e => e.Kind == DecisionKind.RunStarted))
        {
            foreach (var member in Read<RunStartedPayload>(entry)?.Roster ?? [])
            {
                roster.TryAdd(member.Agent, member);
            }
        }

        bool Reviews(AgentId agent) => roster.TryGetValue(agent, out var member) && member.Role is AgentRole.Judge or AgentRole.Auditor;

        var vendors = new HashSet<VendorId>(roster.Values.Where(m => m.Role == AgentRole.Orchestrator).Select(m => m.Vendor));
        void AddRosterVendor(AgentId agent)
        {
            if (!Reviews(agent) && roster.TryGetValue(agent, out var member))
            {
                vendors.Add(member.Vendor);
            }
        }

        var kinds = new Dictionary<TaskId, WorkTaskKind>();
        foreach (var entry in runEntries)
        {
            switch (entry.Kind)
            {
                case DecisionKind.PlanCreated when Read<PlanCreatedPayload>(entry) is { Author: { } author }:
                    AddRosterVendor(author);
                    break;
                case DecisionKind.TaskCreated when Read<TaskCreatedPayload>(entry) is { } created:
                    kinds[created.Task] = created.Kind;
                    break;
                case DecisionKind.TaskAssigned when Read<TaskAssignedPayload>(entry) is { } assigned
                    && kinds.GetValueOrDefault(assigned.Task, WorkTaskKind.AgentWork) != WorkTaskKind.Review:
                    AddRosterVendor(assigned.Agent);
                    break;
                case DecisionKind.AgentRunStarted when Read<AgentRunStartedPayload>(entry) is { } started && !Reviews(started.Agent):
                    vendors.Add(started.Vendor);
                    break;
            }
        }

        return vendors;
    }

    /// <summary>
    /// The events <see cref="RunStateReplayer"/> needs, in log order: every <c>TaskStatusChanged</c> and every
    /// <c>ConditionEvaluated</c>. A payload that cannot be read ends the list there, with a problem, so the replay
    /// stops at the last state the log really describes.
    /// </summary>
    public static IReadOnlyList<RunEvent> ReplayEvents(IReadOnlyList<DecisionEntry> runEntries, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(runEntries);
        ArgumentNullException.ThrowIfNull(problems);

        var events = new List<RunEvent>();
        foreach (var entry in runEntries)
        {
            try
            {
                switch (entry.Kind)
                {
                    case DecisionKind.TaskStatusChanged:
                        var change = DecisionPayloadSerializer.Deserialize<TaskStatusChangedPayload>(entry.PayloadJson);
                        events.Add(new TaskTransitioned(entry.RecordedAt, change.Task, change.From, change.To, change.Reason, change.Evidence ?? []));
                        break;
                    case DecisionKind.ConditionEvaluated:
                        var condition = DecisionPayloadSerializer.Deserialize<ConditionEvaluatedPayload>(entry.PayloadJson);
                        events.Add(new ConditionResolved(entry.RecordedAt, condition.Task, condition.Branch));
                        break;
                }
            }
            catch (JsonException ex)
            {
                problems.Add($"Entry {entry.Sequence} ({entry.Kind}): the payload cannot be read, so the replay stops before it: {ex.Message}");
                break;
            }
        }

        return events;
    }

    /// <summary>
    /// The evidence of the run the judge may consume, in the order the store lists it (capture order): the rows an
    /// <c>EvidenceRegistered</c> entry of the run announced with the same id, SHA-256, kind and origin and, from version
    /// 2, the same title, author, capture time, test counters and commit. The log is the source of truth, so what the
    /// judge gets is the evidence as the log anchored it, and nothing else gets in:
    /// <list type="bullet">
    /// <item>a row the log never registered (inserting one needs no UPDATE nor DELETE, so no trigger stops it), a row
    /// that disagrees with an entry that registered it (one rewritten behind the triggers, say), and an entry whose row
    /// the store does not hold for the run are integrity problems, added to <paramref name="problems"/>;</item>
    /// <item>a row announced only by version-1 entries, which anchor its content but none of its metadata, reaches the
    /// judge as the store holds it, with an integrity problem: what decides an approval about it is unanchored, so it
    /// can never support one;</item>
    /// <item>the prompt and the answer of an agent launch that <c>AgentRunStarted</c> (with the prompt's hash) and
    /// <c>AgentRunFinished</c> announce instead (those of the judge) are records of the review, not evidence of the
    /// work: they are left out quietly, unless the prompt's hash disagrees with its entry;</item>
    /// <item>the prompt of a launch whose <c>AgentRunStarted</c> carried a pack of project memory is left out quietly too,
    /// even when it was also registered as evidence of its task: the judge never sees the memory (ADR-0022, decision 7).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<EvidenceRecord> RegisteredEvidence(IReadOnlyList<DecisionEntry> runEntries, IReadOnlyList<EvidenceRecord> stored, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(runEntries);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(problems);

        var registered = new Dictionary<EvidenceId, List<(long Sequence, EvidenceRegisteredPayload Payload)>>();
        var registrationOrder = new List<EvidenceId>();
        var launchPrompts = new Dictionary<EvidenceId, (long Sequence, string Sha256)>();
        var launchAnswers = new HashSet<EvidenceId>();

        // A prompt that carried project memory never reaches the judge, even when it was also registered as evidence of its
        // task (ADR-0022, decision 7: the judge never sees the memory). It is left out quietly: it is not a problem of the run.
        var promptsWithMemory = new HashSet<EvidenceId>();
        foreach (var entry in runEntries)
        {
            switch (entry.Kind)
            {
                case DecisionKind.EvidenceRegistered:
                    if (Read<EvidenceRegisteredPayload>(entry) is not { } payload)
                    {
                        problems.Add($"Entry {entry.Sequence} (EvidenceRegistered): its payload cannot be read, so the evidence it registered is unknown.");
                        break;
                    }

                    if (!registered.TryGetValue(payload.Evidence, out var announced))
                    {
                        registered[payload.Evidence] = announced = [];
                        registrationOrder.Add(payload.Evidence);
                    }

                    announced.Add((entry.Sequence, payload));
                    break;
                case DecisionKind.AgentRunStarted when Read<AgentRunStartedPayload>(entry) is { } started:
                    launchPrompts.TryAdd(started.PromptEvidenceId, (entry.Sequence, started.PromptSha256));
                    if (started.ContextPack is not null)
                    {
                        promptsWithMemory.Add(started.PromptEvidenceId);
                    }

                    break;
                case DecisionKind.AgentRunFinished when Read<AgentRunFinishedPayload>(entry) is { ReportEvidenceId: { } answer }:
                    launchAnswers.Add(answer);
                    break;
            }
        }

        var admitted = new List<EvidenceRecord>();
        var inStore = new HashSet<EvidenceId>();
        foreach (var record in stored)
        {
            inStore.Add(record.Id);
            if (promptsWithMemory.Contains(record.Id))
            {
                continue;
            }

            if (registered.TryGetValue(record.Id, out var announced))
            {
                if (Admit(record, announced, problems) is { } evidence)
                {
                    admitted.Add(evidence);
                }
            }
            else if (launchPrompts.TryGetValue(record.Id, out var prompt))
            {
                if (!string.Equals(prompt.Sha256, record.ContentSha256, StringComparison.Ordinal))
                {
                    problems.Add($"Evidence '{record.Id}': AgentRunStarted (entry {prompt.Sequence}) announced the prompt with SHA-256 {prompt.Sha256}, but the store holds {record.ContentSha256}.");
                }
            }
            else if (!launchAnswers.Contains(record.Id))
            {
                problems.Add($"Evidence '{record.Id}' ({record.Kind}, {record.Origin}) is in the store for this run, but the decision log never registered it: it is left out and proves nothing.");
            }
        }

        foreach (var id in registrationOrder.Where(id => !inStore.Contains(id)))
        {
            problems.Add($"Evidence '{id}' is registered in the decision log (entry {registered[id][0].Sequence}) but is not in the store for this run.");
        }

        return admitted;
    }

    /// <summary>
    /// The commit under review: the result of the run's last clean integration (<c>IntegrationPerformed</c> with a
    /// result and no conflict), as the log records it. A delivery to the user's branch comes after the review and does
    /// not count. Null when the run integrated nothing.
    /// </summary>
    public static string? CandidateCommit(IReadOnlyList<DecisionEntry> runEntries)
    {
        ArgumentNullException.ThrowIfNull(runEntries);
        // Only integrations that moved the run's branch were verified: a candidate that failed its verification is
        // recorded with its candidate ref as the target (ADR-0012) and must never be what the judge reviews.
        return runEntries
            .Where(e => e.Kind == DecisionKind.IntegrationPerformed)
            .Select(Read<IntegrationPerformedPayload>)
            .LastOrDefault(p => p is { IsDelivery: false, ResultCommit: not null, ConflictPaths.Count: 0 }
                && string.Equals(p.TargetRef, "agentstudio/run-" + p.RunId, StringComparison.Ordinal))
            ?.ResultCommit;
    }

    /// <summary>
    /// The evidence a registered row gives the judge, or null when it proves nothing. Every entry that announced it must
    /// agree with the row; then the first version-2 entry is what the judge gets, built from the log. Without one, the
    /// row is admitted as the store holds it and its unanchored metadata is reported.
    /// </summary>
    private static EvidenceRecord? Admit(EvidenceRecord record, List<(long Sequence, EvidenceRegisteredPayload Payload)> announced, ICollection<string> problems)
    {
        foreach (var (sequence, payload) in announced)
        {
            var disagreements = payload.Disagreements(record);
            if (disagreements.Count > 0)
            {
                problems.Add($"Evidence '{record.Id}' ({record.Kind}, {record.Origin}) does not match its EvidenceRegistered entry {sequence}: {string.Join("; ", disagreements)}. It was changed after it was registered: it is left out and proves nothing.");
                return null;
            }
        }

        var (anchoredAt, anchored) = announced.FirstOrDefault(a => a.Payload.AnchorsMetadata());
        if (anchored is null)
        {
            problems.Add($"Evidence '{record.Id}' ({record.Kind}, {record.Origin}) was registered only by version 1 of EvidenceRegistered (entry {announced[0].Sequence}), which anchors its content but not its title, author, capture time, test counters nor commit: those come from the store alone, so it cannot support an approval.");
            return record;
        }

        try
        {
            return anchored.ToRecord(record.Covers);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DomainRuleViolationException)
        {
            problems.Add($"Evidence '{record.Id}': its EvidenceRegistered entry {anchoredAt} cannot be read back as evidence ({ex.Message}); it is left out and proves nothing.");
            return null;
        }
    }

    /// <summary>
    /// The SHA-256 the decision log anchored for <paramref name="evidence"/>: the one of the first <c>EvidenceRegistered</c>
    /// entry of the run that names it (every version anchors it), or null when none does. Content that feeds the judge is
    /// read only against it, never against what the row of the store says now (R3-08).
    /// </summary>
    public static string? AnchoredSha256(IReadOnlyList<DecisionEntry> runEntries, EvidenceId evidence)
    {
        ArgumentNullException.ThrowIfNull(runEntries);
        return runEntries
            .Where(e => e.Kind == DecisionKind.EvidenceRegistered)
            .Select(Read<EvidenceRegisteredPayload>)
            .FirstOrDefault(p => p is not null && p.Evidence == evidence)
            ?.Sha256;
    }

    /// <summary>The payload of the run's first <c>RunStarted</c> entry, or null if the run never started (or it cannot be read).</summary>
    public static RunStartedPayload? RunStarted(IReadOnlyList<DecisionEntry> runEntries)
    {
        ArgumentNullException.ThrowIfNull(runEntries);
        return runEntries.Where(e => e.Kind == DecisionKind.RunStarted).Select(Read<RunStartedPayload>).FirstOrDefault(p => p is not null);
    }

    private static T? Read<T>(DecisionEntry entry)
        where T : DecisionPayload
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);
        }
        catch (JsonException)
        {
            return null; // A payload that cannot be read tells nothing; the hash chain says whether it was tampered with.
        }
    }

    private static bool NamesRun(string payloadJson, string runId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("runId", out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), runId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
