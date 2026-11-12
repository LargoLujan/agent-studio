using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Memory.Store;
using AgentStudio.Memory.Summary;

namespace AgentStudio.Memory.Derivation;

/// <summary>
/// The memory derived from the verified decision log (docs/14 D-18 to D-20; ADR-0022). What it derives, and from where, is
/// the table of D-18 with the additions of docs/15 D-46 (<see cref="DerivationRules"/>); the texts of the system are its
/// own (<see cref="DerivationTexts"/>).
/// <list type="bullet">
/// <item><b>When.</b> Never while an activity with agents of the project goes on (<see cref="IRunActivity"/>, which is the
/// <see cref="IProjectActivity"/> of the app: a run, a planning, a debate or an audit): it does not wait, it says why and
/// writes nothing; and if one starts while it derives, it stops before its next write and the next derivation completes the
/// rest. Only from a log that verifies against its anchor; on any other, nothing is written. Derivations of a project go
/// one at a time.</item>
/// <item><b>Idempotent.</b> A fact whose source (with its origin) the log already announced with a row is left as it is,
/// whatever became of it (a person may have rejected or retracted it), and the evidence it quotes is not read again. Only
/// the rest goes to <see cref="IProjectMemory.RecordAsync"/>, which redacts, cuts to the limit, announces and stores it,
/// and which completes the row of an announcement the app left without one. So deriving twice in a row writes nothing
/// the second time.</item>
/// <item><b>Text of agents.</b> Only from evidence the log anchored (<c>EvidenceRegistered</c>), read as it anchored it:
/// the bytes the store gives must hash to the SHA-256 of the log, since whoever rewrote the content may have rewritten
/// its row too. Evidence missing from the store, or with another hash, is not quoted: it is listed in
/// <see cref="MemoryDerivationResult.Problems"/> and raises an integrity alert outside every run
/// (<see cref="EvidenceTamperedAlert"/>), once per problem, and the rest is derived. The text of a judge is never quoted:
/// its answer is not announced with <c>EvidenceRegistered</c>.</item>
/// <item><b>The question of a debate.</b> The summary of a closed debate quotes the question the person wrote, read the
/// same way: anchored by <c>EvidenceRegistered</c> as provided by a person, with the hash <c>DebateOpened</c> anchored.
/// Otherwise the summary is recorded without it and says why; a question that does not match its anchor is a problem with
/// its integrity alert, like the text of an agent.</item>
/// <item><b>Inherited history.</b> Runs older than the last history accepted as a new origin are not derived: what they say
/// was not recorded here, and memory announced now would be trusted (D-20).</item>
/// <item><b>The journal</b> (docs/17 D-02; ADR-0047 and its addendum of 2026-11-03). After the facts of the log, each entry
/// of the journal (<c>.agentstudio/bitacora/</c>, read through the checked storage before the log, <see cref="JournalFiles"/>) becomes a
/// <see cref="MemoryKind.JournalEntry"/>, entry by entry, with <see cref="MemorySources.Journal"/> as its source: the day and
/// the SHA-256 of its text, so deriving again takes nothing twice and changing the file afterwards never changes what was
/// anchored (<see cref="JournalEntries"/>). What Agent Studio wrote, and the receipt of its launch in a run of its own
/// vouches for, is <see cref="MemoryOrigin.System"/>; anything else is <see cref="MemoryOrigin.Agent"/>, in quarantine until a
/// person accepts it or trusts the journal (<see cref="JournalFacts"/>, <see cref="JournalTrust"/>). A day that cannot be
/// read safely is listed in <see cref="MemoryDerivationResult.Problems"/> and raises an integrity alert, once
/// (<see cref="JournalUnreadableAlert"/>). The journal is data: nothing of it is interpreted, and it is never exported to
/// Markdown (<see cref="MemoryKinds.IsExported"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <see cref="IRunActivity"/> only says whether the project is busy now: a run that starts between that answer and the
/// write sees the write, which <see cref="IProjectMemory.RecordAsync"/> then holds until the run ends. The window is the
/// same few milliseconds <see cref="ProjectMemory"/> accepts.
/// <para>
/// The texts say their times in UTC (<see cref="DerivationTexts"/>): nothing of the zone of the machine reaches the content
/// that is anchored, so an announcement the app left without its row (it died between the two steps) is completed with the
/// same entry wherever and whenever it is derived again. The one exception is an announcement left without its row by a
/// version with other words (before V27): recording it again gives another text, so <see cref="IProjectMemory.RecordAsync"/>
/// announces it as a new entry and the old announcement stays without a row.
/// </para>
/// </remarks>
/// <param name="journal">Reads the days of the journal with the policy of the storage of the project.</param>
public sealed class MemoryDerivation(
    IDecisionRecorder recorder,
    IEvidenceStore evidence,
    IMemoryStore store,
    IProjectMemory memory,
    IRunActivity activity,
    IProjectStorageReader journal) : IMemoryDerivation, IMemoryEntryTexts
{
    /// <summary>The action of the approval of a plan, as the orchestrator records it when the run starts (<c>Orchestrator.RecordStartAsync</c>).</summary>
    public const string PlanApprovalAction = "plan.approve";

    /// <summary>The action of the approval of a delivery to the user's branch (<c>DeliveryService.ApprovalAction</c>).</summary>
    public const string DeliveryApprovalAction = "deliver-integration";

    /// <summary>The action of the acceptance of a revision that turned towards an approval (<c>DeliveryService.RevisionAcceptanceAction</c>, docs/15 D-40).</summary>
    public const string RevisionAcceptanceAction = "verdict.revision.accept";

    /// <summary>The action of the dismissal of a request to replan (<c>DeliveryService.ReplanDismissalAction</c>, docs/15 D-35).</summary>
    public const string ReplanDismissalAction = "replan.dismiss";

    /// <summary>
    /// Where an integration the run did not accept is recorded: a hidden ref of the run, the candidate of its task
    /// (<c>IntegrationNames.HiddenRefPrefix</c>), never the run's branch.
    /// </summary>
    public const string CandidateRefPrefix = "refs/agentstudio/";

    /// <summary>The integrity alert of an evidence the memory would quote that is missing or is not the content the log anchored.</summary>
    public const string EvidenceTamperedAlert = "memory.evidence-tampered";

    /// <summary>The integrity alert of a day of the journal (or its folder) that could not be read safely: a link, a second name, too large.</summary>
    public const string JournalUnreadableAlert = "memory.journal-unreadable";

    /// <summary>Agent Studio raises the alerts, as it signs every memory announcement.</summary>
    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<MemoryDerivationResult> DeriveAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var projectId = project.ProjectId;
        if (activity.IsBusy(projectId))
        {
            return MemoryDerivationResult.NotDerived(DerivationTexts.BusyReason, DerivationUserTexts.BusyReason());
        }

        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (activity.IsBusy(projectId))
            {
                return MemoryDerivationResult.NotDerived(DerivationTexts.BusyReason, DerivationUserTexts.BusyReason());
            }

            // The journal first, then the log: a launch that ends in between (an auxiliary run is no activity of the
            // project) wrote its result line before the receipt its end keeps, so the log read after the files has it.
            var read = JournalFiles.Read(journal, project.RootPath);
            var state = await recorder.LoadAsync(projectId, ct).ConfigureAwait(false);
            if (!state.Verification.IsValid)
            {
                return state.Unanchored is not null
                    ? MemoryDerivationResult.NotDerived(DerivationTexts.UnanchoredReason, DerivationUserTexts.UnanchoredReason())
                    : MemoryDerivationResult.NotDerived(DerivationTexts.NotVerifiedReason(state.Verification), DerivationUserTexts.NotVerifiedReason(state.Verification));
            }

            return await DeriveAsync(project, state.Entries, read, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The coded form of the entries the derivation wrote (<see cref="IMemoryEntryTexts"/>), from the same facts of the verified log
    /// <see cref="DerivationRules"/> reads: a part is given only when its Spanish text is exactly the one the entry holds, so the
    /// anchored words are what decides, never a guess. The entries of the journal have only their topic coded, read from the topic
    /// itself (the day and the key it names).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MemoryEntryText>> ReadAsync(ProjectContext project, IReadOnlyList<MemoryEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(entries);
        var texts = new Dictionary<string, MemoryEntryText>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Kind == MemoryKind.JournalEntry))
        {
            if (DerivationUserTexts.TryJournalTopic(entry.Topic) is { } topic)
            {
                texts[entry.Id] = new MemoryEntryText(topic, null);
            }
        }

        // The daily summary the system wrote is read from its own text: its lines are templates (DailySummaryTexts).
        foreach (var entry in entries.Where(e => e is { Kind: MemoryKind.DailySummary, Origin: MemoryOrigin.System }))
        {
            texts[entry.Id] = new MemoryEntryText(DailySummaryTexts.TryTopic(entry.Topic), DailySummaryTexts.Coded(entry.Content));
        }

        var derived = entries
            .Where(e => e.Kind is not (MemoryKind.JournalEntry or MemoryKind.DailySummary or MemoryKind.ProjectBrief)
                && (e.Source.StartsWith(MemorySources.LogEntryPrefix, StringComparison.Ordinal) || e.Source.StartsWith(MemorySources.EvidencePrefix, StringComparison.Ordinal)))
            .ToList();
        if (derived.Count == 0)
        {
            return texts;
        }

        var state = await recorder.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!state.Verification.IsValid)
        {
            return texts;
        }

        var facts = DerivationRules.Facts(DerivationLog.Of(state.Entries)).ToDictionary(f => (f.Origin, f.Source));
        foreach (var entry in derived)
        {
            if (!facts.TryGetValue((entry.Origin, entry.Source), out var fact))
            {
                continue;
            }

            var topic = fact.TopicText is not null && string.Equals(entry.Topic, fact.Topic, StringComparison.Ordinal) ? fact.TopicText : null;
            var content = ContentText(fact, entry);
            if (topic is not null || content is not null)
            {
                texts[entry.Id] = new MemoryEntryText(topic, content);
            }
        }

        return texts;
    }

    /// <summary>The coded content of <paramref name="fact"/> when the entry holds exactly its Spanish text (a debate summary also with the sentence about its question).</summary>
    private static UserText? ContentText(DerivedFact fact, MemoryEntry entry)
    {
        switch (fact)
        {
            case SystemFact { ContentText: not null } system when string.Equals(entry.Content, system.Content, StringComparison.Ordinal):
                return system.ContentText;
            case DebateSummaryFact { ContentText: not null } debate when entry.Content.StartsWith(debate.Content, StringComparison.Ordinal):
                return QuestionText(debate, entry.Content[debate.Content.Length..]) is { } question ? DerivationUserTexts.WithQuestion(debate.ContentText, question) : null;
            default:
                return null;
        }
    }

    /// <summary>The sentence about the question of a debate, from what follows its summary in the entry; null when it is none of the five the derivation writes.</summary>
    private static UserText? QuestionText(DebateSummaryFact debate, string tail)
    {
        var evidence = debate.QuestionEvidence.Value;
        const string QuotedStart = " Pregunta: «";
        if (tail.StartsWith(QuotedStart, StringComparison.Ordinal) && tail.EndsWith('»') && tail.Length >= QuotedStart.Length + 1)
        {
            return DerivationUserTexts.QuestionQuoted(tail[QuotedStart.Length..^1]);
        }

        foreach (var (state, text) in new[]
        {
            (QuestionState.Empty, DerivationUserTexts.QuestionEmpty()),
            (QuestionState.NotAnchored, DerivationUserTexts.QuestionNotAnchored(evidence)),
            (QuestionState.NotByAPerson, DerivationUserTexts.QuestionNotByAPerson(evidence)),
            (QuestionState.Tampered, DerivationUserTexts.QuestionTampered(evidence)),
        })
        {
            if (string.Equals(tail, DerivationTexts.WithQuestion(string.Empty, state, null, debate.QuestionEvidence), StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    private async Task<MemoryDerivationResult> DeriveAsync(ProjectContext project, IReadOnlyList<DecisionEntry> entries, JournalRead read, CancellationToken ct)
    {
        var projectId = project.ProjectId;
        var log = DerivationLog.Of(entries);
        var ledger = MemoryLedger.From(entries);
        var rows = await store.SearchAsync(projectId, new MemorySearch(), ct).ConfigureAwait(false);
        var known = Known(ledger, rows);
        var raised = log.AlertMessages(EvidenceTamperedAlert);
        var recorded = new List<MemoryEntry>();
        var already = new List<string>();
        var problems = new List<string>();
        MemoryDerivationResult Stop(string reason) => new(recorded, already, problems, reason, DerivationUserTexts.ReasonOf(reason));

        foreach (var fact in DerivationRules.Facts(log))
        {
            if (known.Contains((fact.Origin, fact.Source)))
            {
                already.Add(fact.Source);
                continue;
            }

            string content;
            if (fact is AgentQuote quote)
            {
                var (bytes, problem) = await ReadAnchoredAsync(projectId, quote.Anchor, ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems.Add(DerivationTexts.Problem(quote));
                    if (await RaiseOnceAsync(projectId, DerivationTexts.TamperedAlert(quote, problem), raised, ct).ConfigureAwait(false) is { } stopped)
                    {
                        return Stop(stopped);
                    }

                    continue;
                }

                if (Quoted(quote.What, bytes!) is not { } text)
                {
                    // The agent wrote nothing there (no open issues, no summary): nothing to remember.
                    continue;
                }

                content = text;
            }
            else if (fact is DebateSummaryFact summary)
            {
                var (state, question, problem) = await QuestionAsync(projectId, summary, ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems.Add(DerivationTexts.QuestionProblem(summary));
                    if (await RaiseOnceAsync(projectId, DerivationTexts.QuestionTamperedAlert(summary, problem), raised, ct).ConfigureAwait(false) is { } stopped)
                    {
                        return Stop(stopped);
                    }
                }

                content = DerivationTexts.WithQuestion(summary.Content, state, question, summary.QuestionEvidence);
            }
            else
            {
                content = ((SystemFact)fact).Content;
            }

            if (activity.IsBusy(projectId))
            {
                return Stop(DerivationTexts.StoppedReason);
            }

            try
            {
                recorded.Add(await memory.RecordAsync(project, new MemoryDraft(fact.Kind, fact.Topic, content, fact.Origin, fact.Source), ct).ConfigureAwait(false));
            }
            catch (DecisionLogIntegrityException)
            {
                return Stop(DerivationTexts.BrokenMidwayReason);
            }
        }

        if (await DeriveJournalAsync(project, read, log, ledger, rows, recorded, already, problems, ct).ConfigureAwait(false) is { } stoppedInJournal)
        {
            return Stop(stoppedInJournal);
        }

        return new MemoryDerivationResult(recorded, already, problems);
    }

    /// <summary>
    /// The entries of the journal (docs/17 D-02): what cannot be read safely is a problem with its alert, once; each entry
    /// the memory does not hold yet is recorded with its origin and the version it supersedes (<see cref="JournalFacts"/>).
    /// Returns why it stopped (a run started, or the log stopped verifying), or null.
    /// </summary>
    private async Task<string?> DeriveJournalAsync(
        ProjectContext project,
        JournalRead read,
        DerivationLog log,
        MemoryLedger ledger,
        IReadOnlyList<MemoryRow> rows,
        List<MemoryEntry> recorded,
        List<string> already,
        List<string> problems,
        CancellationToken ct)
    {
        var projectId = project.ProjectId;
        var raised = log.AlertMessages(JournalUnreadableAlert);
        foreach (var problem in read.Problems)
        {
            problems.Add(problem.Problem);
            if (await RaiseOnceAsync(projectId, JournalUnreadableAlert, DerivationTexts.JournalAlertSummary, problem.Alert, raised, ct).ConfigureAwait(false) is { } stopped)
            {
                return stopped;
            }
        }

        foreach (var fact in JournalFacts.Of(JournalEntries.Parse(read.Days), log, ledger, rows))
        {
            if (fact.Known)
            {
                already.Add(fact.Source);
                continue;
            }

            if (activity.IsBusy(projectId))
            {
                return DerivationTexts.StoppedReason;
            }

            try
            {
                var draft = new MemoryDraft(MemoryKind.JournalEntry, fact.Topic, fact.Entry.Text, fact.Origin, fact.Source, fact.Supersedes);
                recorded.Add(await memory.RecordAsync(project, draft, ct).ConfigureAwait(false));
            }
            catch (DecisionLogIntegrityException)
            {
                return DerivationTexts.BrokenMidwayReason;
            }
        }

        return null;
    }

    /// <summary>
    /// Raises the integrity alert <paramref name="message"/> of an evidence outside every run unless the log already holds it
    /// (once per problem). Returns why the derivation stops (a run started, or the log stopped verifying), or null.
    /// </summary>
    private Task<string?> RaiseOnceAsync(string projectId, string message, HashSet<string> raised, CancellationToken ct) =>
        RaiseOnceAsync(projectId, EvidenceTamperedAlert, DerivationTexts.AlertSummary, message, raised, ct);

    /// <summary>
    /// Raises the integrity alert <paramref name="code"/> with <paramref name="message"/> outside every run unless the log
    /// already holds it (once per problem). Returns why the derivation stops (a run started, or the log stopped verifying), or null.
    /// </summary>
    private async Task<string?> RaiseOnceAsync(string projectId, string code, string summary, string message, HashSet<string> raised, CancellationToken ct)
    {
        if (raised.Contains(message))
        {
            return null;
        }

        if (activity.IsBusy(projectId))
        {
            return DerivationTexts.StoppedReason;
        }

        try
        {
            await recorder.RecordAsync(
                projectId,
                SystemActor,
                DecisionKind.AuditAlertRaised,
                summary,
                new AuditAlertPayload(code, [], message) { RunId = DecisionPayload.NoRun },
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (DecisionLogIntegrityException)
        {
            return DerivationTexts.BrokenMidwayReason;
        }

        raised.Add(message);
        return null;
    }

    /// <summary>
    /// The question of a debate as the person wrote it (D-46), read as the log anchored it: its <c>EvidenceRegistered</c>,
    /// provided by a person and with the SHA-256 <c>DebateOpened</c> anchored, and bytes that hash to it. Otherwise why it is
    /// not quoted; <c>Problem</c>, in English, only when the content does not match its anchors.
    /// </summary>
    private async Task<(QuestionState State, string? Question, string? Problem)> QuestionAsync(string projectId, DebateSummaryFact summary, CancellationToken ct)
    {
        if (summary.Question is not { } anchor)
        {
            return (QuestionState.NotAnchored, null, null);
        }

        if (anchor.Origin != EvidenceOrigin.ProvidedByHuman)
        {
            return (QuestionState.NotByAPerson, null, null);
        }

        if (!string.Equals(anchor.Sha256, summary.QuestionSha256, StringComparison.OrdinalIgnoreCase))
        {
            return (QuestionState.Tampered, null, $"DebateOpened anchors SHA-256 {summary.QuestionSha256} and EvidenceRegistered anchors {anchor.Sha256}.");
        }

        var (bytes, problem) = await ReadAnchoredAsync(projectId, anchor, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return (QuestionState.Tampered, null, problem);
        }

        var question = Encoding.UTF8.GetString(bytes!).TrimStart('﻿').Trim();
        return question.Length == 0 ? (QuestionState.Empty, null, null) : (QuestionState.Quoted, question, null);
    }

    /// <summary>
    /// The facts already in the memory: the origin and the source of each entry the log announced (outside every run) that
    /// has its row. An announcement without a row is not among them, so recording the fact again completes it.
    /// </summary>
    private static HashSet<(MemoryOrigin Origin, string Source)> Known(MemoryLedger ledger, IReadOnlyList<MemoryRow> rows)
    {
        var withRow = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return [.. ledger.Announcements.Where(a => withRow.Contains(a.Payload.Entry)).Select(a => (a.Payload.Origin, a.Payload.Source))];
    }

    /// <summary>
    /// The content of an evidence exactly as the log anchored it (like <c>RunRecorder.ReadBytesAsync</c>): the bytes the store
    /// gives must hash to <paramref name="anchor"/>'s SHA-256. Otherwise, why not, in English: the store has no such evidence,
    /// it found its content missing or changed, or the content is another one.
    /// </summary>
    private async Task<(byte[]? Content, string? Problem)> ReadAnchoredAsync(string projectId, EvidenceRegisteredPayload anchor, CancellationToken ct)
    {
        byte[] content;
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, anchor.Evidence, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                content = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException)
        {
            return (null, ex.Message);
        }

        var actual = ContentHash.Sha256Hex(content);
        return string.Equals(actual, anchor.Sha256, StringComparison.OrdinalIgnoreCase)
            ? (content, null)
            : (null, $"its content hashes to {actual}, not to {anchor.Sha256}.");
    }

    /// <summary>
    /// The text the agent wrote in a JSON answer: the <c>summary</c> of a plan, or the <c>openIssues</c> of a report as one
    /// line each. Null when there is none, or the answer is not that JSON.
    /// </summary>
    private static string? Quoted(QuotedText what, byte[] content)
    {
        var json = content.AsMemory();
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            switch (what)
            {
                case QuotedText.PlanSummary when root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String:
                    return summary.GetString()?.Trim() is { Length: > 0 } text ? text : null;
                case QuotedText.OpenIssues when root.TryGetProperty("openIssues", out var issues) && issues.ValueKind == JsonValueKind.Array:
                    var lines = issues.EnumerateArray()
                        .Where(i => i.ValueKind == JsonValueKind.String)
                        .Select(i => i.GetString()!.Trim())
                        .Where(i => i.Length > 0)
                        .Select(i => "- " + i)
                        .ToList();
                    return lines.Count == 0 ? null : string.Join("\n", lines);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
