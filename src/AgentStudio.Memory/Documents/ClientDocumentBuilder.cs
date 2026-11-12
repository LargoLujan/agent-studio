using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Memory.Store;

namespace AgentStudio.Memory.Documents;

/// <summary>
/// Builds the model of a document for clients (docs/17 D-58; ADR-0057) from what the system verified: the decision log (read whole
/// and verified, so a log that does not verify fails closed), the frozen requirements, the evidence the system captured, the verdicts of
/// the judge and the memory of trust. It is deterministic: the same project in the same state, and the same clock, give the same model.
/// <list type="bullet">
/// <item>Every string passes through <see cref="DocumentTextCleaner"/> before it becomes a text of the model (secrets redacted, paths of
/// the machine taken out), and what an agent drafted passes through <see cref="ClientDraftRules.Check"/> again here, whoever called
/// before: the model never holds what the rules refuse.</item>
/// <item>Prompts, transcripts, the journal and the consumption never enter: the builder has no way to read them.</item>
/// <item>The states of a requirement are three of D-50: implemented (a test run the system captured covers it and passes), reviewed (the
/// last verdict that assesses it is valid, approved and says satisfied) and integrated (the log records a delivery). «Desplegado» is not
/// shown, and neither are pull requests or the files changed by zone (they come with the phases that have the data).</item>
/// </list>
/// </summary>
public sealed class ClientDocumentBuilder(
    IDecisionRecorder recorder,
    ProjectMemory memory,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    IVerdictStore verdicts,
    ISecretRedactor redactor,
    TimeProvider timeProvider) : IClientDocumentBuilder
{
    private static readonly MemoryKind[] DecisionKinds = [MemoryKind.Decision, MemoryKind.Architecture, MemoryKind.Convention];

    public async Task<ClientDocumentBuild> BuildAsync(ClientDocumentBuildRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.AgentDrafts);
        var projectId = request.Project.ProjectId;
        var state = await recorder.LoadAsync(projectId, LogLoad.Full, ct).ConfigureAwait(false);
        if (!state.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(projectId, state.Verification);
        }

        var cleaner = new DocumentTextCleaner(redactor);
        var projectName = cleaner.Clean(Path.GetFileName(Path.TrimEndingDirectorySeparator(request.Project.RootPath)));
        if (projectName.Length == 0)
        {
            projectName = "proyecto";
        }

        var milestone = cleaner.Clean(request.Milestone);
        var milestoneName = milestone.Length == 0 ? null : milestone;

        var set = await requirements.LatestAsync(projectId, ct).ConfigureAwait(false);

        // Only what the verified log announces (EvidenceRegistered, with the same SHA-256) counts: the store also holds evidence that
        // belongs to no run, such as the tests of HEAD, and a document for a client must rest on anchored evidence.
        var anchored = state.Entries
            .Where(e => e.Kind == DecisionKind.EvidenceRegistered)
            .Select(e => DecisionPayloadSerializer.Deserialize<EvidenceRegisteredPayload>(e.PayloadJson))
            .Select(p => (Id: p.Evidence.Value, p.Sha256))
            .ToHashSet();
        var records = (await evidence.ListAsync(projectId, ct).ConfigureAwait(false))
            .Where(r => anchored.Contains((r.Id.Value, r.ContentSha256)))
            .ToList();
        var stored = await verdicts.ListAsync(projectId, null, ct).ConfigureAwait(false);
        var snapshot = await memory.SnapshotAsync(projectId, ct).ConfigureAwait(false);
        var trusted = snapshot.Entries
            .Where(e => e.Trust == MemoryTrust.Trusted && e.SupersededBy is null && MemoryKinds.IsExported(e.Kind))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var deliveries = state.Entries
            .Where(e => e.Kind == DecisionKind.IntegrationPerformed)
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize<IntegrationPerformedPayload>(e.PayloadJson)))
            .Where(d => d.Payload.IsDelivery && d.Payload.ResultCommit is not null)
            .ToList();

        var commit = deliveries.Count == 0 ? null : deliveries[^1].Payload.ResultCommit!.ToLowerInvariant();
        if (commit is not null && !ClientDocumentRules.IsCommit(commit))
        {
            commit = null;
        }

        var version = 1 + state.Entries
            .Where(e => e.Kind == DecisionKind.ClientDocumentExported)
            .Select(e => DecisionPayloadSerializer.Deserialize<ClientDocumentExportedPayload>(e.PayloadJson))
            .Where(p => string.Equals(p.Milestone, milestoneName, StringComparison.Ordinal))
            .Select(p => p.Version)
            .DefaultIfEmpty(0)
            .Max();

        var systemRuns = records
            .Where(r => r.Kind == EvidenceKind.TestRun && r.Origin == EvidenceOrigin.CapturedBySystem && r.TestRun is not null)
            .OrderBy(r => r.CapturedAt).ThenBy(r => r.Id.Value, StringComparer.Ordinal)
            .ToList();
        var verdictOf = LastAssessments(stored);

        var delivered = deliveries.Select(d => d.Payload.ResultCommit!.ToLowerInvariant()).ToList();
        var scope = BuildScope(set, systemRuns, verdictOf, delivered, cleaner);
        var verification = BuildVerification(set, systemRuns, verdictOf, cleaner);
        var decisions = BuildDecisions(trusted, cleaner);
        var changes = BuildChanges(deliveries, trusted, cleaner);
        var (risks, riskCount) = BuildRisks(set, verdictOf, state.Entries, trusted, cleaner);
        var satisfied = set is null ? 0 : set.Requirements.Count(r => verdictOf.TryGetValue(r.Id.Value, out var v) && v.Assessment.Status == AssessmentStatus.Satisfied && v.Stored.Valid);
        var passed = systemRuns.Sum(r => r.TestRun!.Passed);
        var failed = systemRuns.Sum(r => r.TestRun!.Failed);

        var summary = new List<ClientDocumentBlock>
        {
            new ClientParagraph(set is null
                ? ClientText.OfSystem(ClientDocumentTexts.SummaryNoRequirements)
                : ClientText.OfSystem(ClientDocumentTexts.SummaryFigures, set.Requirements.Count, satisfied, set.Requirements.Count - satisfied, passed, failed, riskCount)),
        };
        var annexes = BuildAnnexes(records, cleaner);

        // What an agent drafted and the person accepted is checked here again: a paragraph with a path or a secret never enters.
        foreach (var draft in request.AgentDrafts)
        {
            var check = ClientDraftRules.Check(draft.Paragraphs, redactor);
            var target = ClientDraftKinds.SectionOf(draft.Kind) == ClientDocumentSectionKind.Summary ? summary : annexes;
            target.AddRange(check.Paragraphs.Select(p => new ClientParagraph(ClientText.ByAgent(p, new ClientAgentMark(draft.Run, draft.ReviewedByPerson)))));
        }

        var document = new ClientDocument(
            new ClientDocumentCover(projectName, milestoneName, timeProvider.GetUtcNow(), version, commit),
            [
                new ClientDocumentSection(ClientDocumentSectionKind.Summary, summary),
                new ClientDocumentSection(ClientDocumentSectionKind.Scope, scope),
                new ClientDocumentSection(ClientDocumentSectionKind.Verification, verification),
                new ClientDocumentSection(ClientDocumentSectionKind.Decisions, decisions),
                new ClientDocumentSection(ClientDocumentSectionKind.Changes, changes),
                new ClientDocumentSection(ClientDocumentSectionKind.RisksAndPending, risks),
                new ClientDocumentSection(ClientDocumentSectionKind.Annexes, annexes),
            ]);
        return new ClientDocumentBuild(document, ClientDocumentDisclosure.Of(document, cleaner.SecretsRedacted, cleaner.PathsRemoved, riskCount, includesConsumption: false));
    }

    /// <summary>For each requirement, the last verdict that assesses it (valid or not) and its assessment.</summary>
    private static Dictionary<string, (StoredVerdict Stored, RequirementAssessment Assessment)> LastAssessments(IReadOnlyList<StoredVerdict> stored)
    {
        var last = new Dictionary<string, (StoredVerdict, RequirementAssessment)>(StringComparer.Ordinal);
        foreach (var verdict in stored)
        {
            foreach (var assessment in verdict.Verdict.Assessments)
            {
                last[assessment.Requirement.Value] = (verdict, assessment);
            }
        }

        return last;
    }

    private static ClientText Instant(DateTimeOffset at) => ClientText.OfSystem(ClientDocumentTexts.DateValue, at.ToUniversalTime());

    private static ClientText YesNo(bool value) => ClientText.OfSystem(value ? ClientDocumentTexts.Yes : ClientDocumentTexts.No);

    private static ClientText Header(string code) => ClientText.OfSystem(code);

    private static ClientText? Written(DocumentTextCleaner cleaner, string? text, bool multiline = false) =>
        cleaner.Clean(text, multiline) is { Length: > 0 } clean ? ClientText.Written(clean) : null;

    private static List<ClientDocumentBlock> BuildScope(
        FrozenRequirementSet? set,
        IReadOnlyList<EvidenceRecord> systemRuns,
        IReadOnlyDictionary<string, (StoredVerdict Stored, RequirementAssessment Assessment)> verdictOf,
        IReadOnlyList<string> delivered,
        DocumentTextCleaner cleaner)
    {
        if (set is null)
        {
            return [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ScopeNone))];
        }

        var rows = new List<IReadOnlyList<ClientText>>();
        var unknown = false;
        foreach (var requirement in set.Requirements)
        {
            var id = requirement.Id.Value;

            // Integrated only when what the system verified is the delivered commit itself, as the judge's dossier counts it: a test run
            // it captured, that passes, covers the requirement and was run on a commit that was delivered. A delivery with no such run
            // says nothing about this requirement: «sin dato», never «sí».
            ClientText integrated;
            if (delivered.Count == 0)
            {
                integrated = YesNo(false);
            }
            else if (systemRuns.Any(r => r.TestRun!.Succeeded && r.Covers.Contains(requirement.Id) && r.SubjectCommit is not null && delivered.Contains(r.SubjectCommit, StringComparer.OrdinalIgnoreCase)))
            {
                integrated = YesNo(true);
            }
            else
            {
                integrated = ClientText.OfSystem(ClientDocumentTexts.Unknown);
                unknown = true;
            }

            var implemented = systemRuns.Any(r => r.TestRun!.Succeeded && r.Covers.Contains(requirement.Id));
            var reviewed = verdictOf.TryGetValue(id, out var v)
                && v.Stored.Valid && v.Stored.Verdict.Decision == VerdictDecision.Approved && v.Assessment.Status == AssessmentStatus.Satisfied;
            rows.Add(
            [
                Written(cleaner, id) ?? ClientText.Written("?"),
                Written(cleaner, requirement.Statement, multiline: true) ?? ClientText.Written("?"),
                ClientText.OfSystem(requirement.Priority == RequirementPriority.Mandatory ? ClientDocumentTexts.PriorityMandatory : ClientDocumentTexts.PriorityDesirable),
                YesNo(implemented),
                YesNo(reviewed),
                integrated,
            ]);
        }

        var blocks = new List<ClientDocumentBlock>
        {
            new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ScopeIntro)),
            new ClientTable(
                [Header(ClientDocumentTexts.ColId), Header(ClientDocumentTexts.ColRequirement), Header(ClientDocumentTexts.ColPriority), Header(ClientDocumentTexts.ColImplemented), Header(ClientDocumentTexts.ColReviewed), Header(ClientDocumentTexts.ColIntegrated)],
                rows),
        };
        if (unknown)
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ScopeDeliveryNote, delivered[^1][..Math.Min(12, delivered[^1].Length)])));
        }

        return blocks;
    }

    private static List<ClientDocumentBlock> BuildVerification(
        FrozenRequirementSet? set,
        IReadOnlyList<EvidenceRecord> systemRuns,
        IReadOnlyDictionary<string, (StoredVerdict Stored, RequirementAssessment Assessment)> verdictOf,
        DocumentTextCleaner cleaner)
    {
        var blocks = new List<ClientDocumentBlock>();
        if (systemRuns.Count == 0)
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.VerificationNoTests)));
        }
        else
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.VerificationTestsIntro)));
            blocks.Add(new ClientTable(
                [Header(ClientDocumentTexts.ColTest), Header(ClientDocumentTexts.ColPassed), Header(ClientDocumentTexts.ColFailed), Header(ClientDocumentTexts.ColSkipped), Header(ClientDocumentTexts.ColExit), Header(ClientDocumentTexts.ColDate)],
                [
                    .. systemRuns.Select(r => (IReadOnlyList<ClientText>)
                    [
                        Written(cleaner, r.Title) ?? ClientText.Written("?"),
                        ClientText.Written(r.TestRun!.Passed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ClientText.Written(r.TestRun.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ClientText.Written(r.TestRun.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ClientText.Written(r.TestRun.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Instant(r.CapturedAt),
                    ]),
                ]));
        }

        var rows = new List<IReadOnlyList<ClientText>>();
        foreach (var requirement in set?.Requirements ?? [])
        {
            if (!verdictOf.TryGetValue(requirement.Id.Value, out var v))
            {
                continue;
            }

            rows.Add(
            [
                Written(cleaner, requirement.Id.Value) ?? ClientText.Written("?"),
                ClientText.OfSystem(v.Assessment.Status switch
                {
                    AssessmentStatus.Satisfied => ClientDocumentTexts.StatusSatisfied,
                    AssessmentStatus.NotSatisfied => ClientDocumentTexts.StatusNotSatisfied,
                    _ => ClientDocumentTexts.StatusNotVerifiable,
                }),
                ClientText.OfSystem(v.Stored.Verdict.Confidence.Level switch
                {
                    ConfidenceLevel.High => ClientDocumentTexts.ConfidenceHigh,
                    ConfidenceLevel.Medium => ClientDocumentTexts.ConfidenceMedium,
                    _ => ClientDocumentTexts.ConfidenceLow,
                }),
                YesNo(v.Stored.Valid),
            ]);
        }

        if (rows.Count == 0)
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.VerificationNoVerdict)));
        }
        else
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.VerificationVerdictsIntro)));
            blocks.Add(new ClientTable([Header(ClientDocumentTexts.ColId), Header(ClientDocumentTexts.ColStatus), Header(ClientDocumentTexts.ColConfidence), Header(ClientDocumentTexts.ColValid)], rows));
        }

        return blocks;
    }

    private static List<ClientDocumentBlock> BuildDecisions(IReadOnlyList<MemoryEntry> trusted, DocumentTextCleaner cleaner)
    {
        var rows = new List<IReadOnlyList<ClientText>>();
        foreach (var entry in trusted.Where(e => DecisionKinds.Contains(e.Kind)))
        {
            var topic = Written(cleaner, entry.Topic);
            var content = Written(cleaner, entry.Content, multiline: true);
            if (topic is null || content is null)
            {
                continue;
            }

            rows.Add(
            [
                topic,
                ClientText.OfSystem(entry.Kind switch
                {
                    MemoryKind.Architecture => ClientDocumentTexts.KindArchitecture,
                    MemoryKind.Convention => ClientDocumentTexts.KindConvention,
                    _ => ClientDocumentTexts.KindDecision,
                }),
                content,
            ]);
        }

        if (rows.Count == 0)
        {
            return [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.DecisionsNone))];
        }

        return
        [
            new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.DecisionsIntro)),
            new ClientTable([Header(ClientDocumentTexts.ColTopic), Header(ClientDocumentTexts.ColCategory), Header(ClientDocumentTexts.ColContent)], rows),
        ];
    }

    private static List<ClientDocumentBlock> BuildChanges(
        IReadOnlyList<(DecisionEntry Entry, IntegrationPerformedPayload Payload)> deliveries,
        IReadOnlyList<MemoryEntry> trusted,
        DocumentTextCleaner cleaner)
    {
        var blocks = new List<ClientDocumentBlock>();
        var items = new List<ClientText>();
        foreach (var (entry, payload) in deliveries)
        {
            var commit = payload.ResultCommit!.ToLowerInvariant();
            if (!ClientDocumentRules.IsCommit(commit))
            {
                continue;
            }

            items.Add(ClientText.OfSystem(ClientDocumentTexts.ChangeDelivery, cleaner.Clean(payload.TargetRef), commit, entry.RecordedAt.ToUniversalTime()));
        }

        if (items.Count > 0)
        {
            blocks.Add(new ClientList(Ordered: false, items));
        }

        var notes = trusted.Where(e => e.Kind == MemoryKind.Change)
            .Select(e => Written(cleaner, $"{e.Topic}: {e.Content}", multiline: false))
            .OfType<ClientText>()
            .ToList();
        if (notes.Count > 0)
        {
            blocks.Add(new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ChangesMemoryIntro)));
            blocks.Add(new ClientList(Ordered: false, notes));
        }

        return blocks.Count == 0 ? [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ChangesNone))] : blocks;
    }

    private static (List<ClientDocumentBlock> Blocks, int Count) BuildRisks(
        FrozenRequirementSet? set,
        IReadOnlyDictionary<string, (StoredVerdict Stored, RequirementAssessment Assessment)> verdictOf,
        IReadOnlyList<DecisionEntry> log,
        IReadOnlyList<MemoryEntry> trusted,
        DocumentTextCleaner cleaner)
    {
        var blocks = new List<ClientDocumentBlock>();
        var count = 0;

        var unmet = new List<ClientText>();
        foreach (var requirement in set?.Requirements ?? [])
        {
            if (verdictOf.TryGetValue(requirement.Id.Value, out var v) && v.Assessment.Status != AssessmentStatus.Satisfied)
            {
                unmet.Add(ClientText.OfSystem(
                    v.Assessment.Status == AssessmentStatus.NotSatisfied ? ClientDocumentTexts.RiskUnsatisfied : ClientDocumentTexts.RiskNotVerifiable,
                    cleaner.Clean(requirement.Id.Value)));
            }
        }

        var challenges = log.Count(e => e.Kind == DecisionKind.VerdictChallenged);
        if (challenges > 0)
        {
            unmet.Add(ClientText.OfSystem(ClientDocumentTexts.RiskChallenges, challenges));
        }

        if (unmet.Count > 0)
        {
            blocks.Add(new ClientList(Ordered: false, unmet));
            count += unmet.Count;
        }

        var alerts = log.Where(e => e.Kind == DecisionKind.AuditAlertRaised)
            .Select(e => DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(e.PayloadJson))
            .Select(a => Written(cleaner, $"{a.Code}: {a.Message}"))
            .OfType<ClientText>()
            .ToList();
        AddList(blocks, ClientDocumentTexts.RisksAlertsIntro, alerts, ref count);

        var errors = trusted.Where(e => e.Kind == MemoryKind.KnownError)
            .Select(e => Written(cleaner, $"{e.Topic}: {e.Content}"))
            .OfType<ClientText>()
            .ToList();
        AddList(blocks, ClientDocumentTexts.RisksErrorsIntro, errors, ref count);

        // The pending objections are the judge's own words: an agent wrote them, so they are marked as such.
        var objections = new List<ClientText>();
        foreach (var (stored, _) in verdictOf.Values.DistinctBy(v => v.Stored.Id))
        {
            if (!stored.Valid || stored.RunId is not { } run || !TryRunId(run, out var runId))
            {
                continue;
            }

            foreach (var objection in stored.Verdict.PendingObjections)
            {
                if (cleaner.Clean(objection, multiline: true) is { Length: > 0 } clean)
                {
                    objections.Add(ClientText.ByAgent(clean, new ClientAgentMark(runId, ReviewedByPerson: false)));
                }
            }
        }

        AddList(blocks, ClientDocumentTexts.RisksObjectionsIntro, objections, ref count);
        return (blocks.Count == 0 ? [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.RisksNone))] : blocks, count);
    }

    private static void AddList(List<ClientDocumentBlock> blocks, string introCode, List<ClientText> items, ref int count)
    {
        if (items.Count == 0)
        {
            return;
        }

        blocks.Add(new ClientParagraph(ClientText.OfSystem(introCode)));
        blocks.Add(new ClientList(Ordered: false, items));
        count += items.Count;
    }

    private static bool TryRunId(string value, out RunId run)
    {
        try
        {
            run = new RunId(value);
            return true;
        }
        catch (ArgumentException)
        {
            run = default;
            return false;
        }
    }

    private static List<ClientDocumentBlock> BuildAnnexes(IReadOnlyList<EvidenceRecord> records, DocumentTextCleaner cleaner)
    {
        var references = new List<ClientEvidenceReference>();
        foreach (var record in records
            .Where(r => r.Origin == EvidenceOrigin.CapturedBySystem && r.Kind is EvidenceKind.TestRun or EvidenceKind.BuildLog or EvidenceKind.CommandOutput or EvidenceKind.CodeDiff)
            .OrderBy(r => r.CapturedAt).ThenBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            references.Add(new ClientEvidenceReference(new EvidenceId(cleaner.Clean(record.Id.Value) is { Length: > 0 } id ? id : "?"), record.ContentSha256, Written(cleaner, record.Title) ?? ClientText.Written("?")));
        }

        return
        [
            new ClientParagraph(ClientText.OfSystem(references.Count == 0 ? ClientDocumentTexts.AnnexEvidenceNone : ClientDocumentTexts.AnnexEvidenceIntro)),
            .. references.Count == 0 ? Array.Empty<ClientDocumentBlock>() : [new ClientEvidenceList(references)],
            new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.AnnexGlossaryIntro)),
            new ClientList(
                Ordered: false,
                [
                    ClientText.OfSystem(ClientDocumentTexts.GlossaryRequirement),
                    ClientText.OfSystem(ClientDocumentTexts.GlossaryEvidence),
                    ClientText.OfSystem(ClientDocumentTexts.GlossaryVerdict),
                    ClientText.OfSystem(ClientDocumentTexts.GlossaryFingerprint),
                ]),

            // What D-58 describes and this version cannot yet say, said in the document itself: the client is not left to guess.
            new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.NotIncludedIntro)),
            new ClientList(
                Ordered: false,
                [
                    ClientText.OfSystem(ClientDocumentTexts.NotIncludedPhase9),
                    ClientText.OfSystem(ClientDocumentTexts.NotIncludedPullRequests),
                    ClientText.OfSystem(ClientDocumentTexts.NotIncludedFiles),
                    ClientText.OfSystem(ClientDocumentTexts.NotIncludedDeployed),
                ]),
        ];
    }
}
