using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// Builds the dossier of the judgment of a debate (docs/15 D-24) straight from Agent Studio's stores, through their ports,
/// and writes its package under the dossier root. Callers pass identifiers only: neither the coordinator nor a participant
/// can choose or filter what the judge sees.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The decision log is the source of truth: the entries of the debate (<see cref="RunLog.EntriesOf"/>), the head of
/// the whole log (which every verdict must echo, the same for both judges of a panel, D-14) and the protocol: the opening,
/// the contributions, the closes of the rounds and the registrations of evidence. The launches of the agents
/// (<c>AgentRunStarted</c>, <c>AgentRunFinished</c>) and the raw payloads, which name them, never reach the judge.</item>
/// <item>The header is a row of the store (<see cref="IDebateRegistry"/>): it must hash to what <c>DebateOpened</c>
/// anchored (<see cref="DebateHeaderHash.Of(DebateRecord)"/>) and, with the SHA-256 of the question, to the proposal the user
/// approved (<see cref="DebateHeaderHash.Proposal(DebateRecord, string)"/>). Either mismatch is a system check, and the
/// package says whether the header verified: a judge is never launched on one that did not (N-05).</item>
/// <item>The text is read only against the hash the log anchored (R3-08): the question against <c>DebateOpened</c>, each
/// contribution against its <c>DebateContributionRecorded</c>, every quote against its <c>EvidenceRegistered</c>. What does not
/// match, or cannot be read, is withheld with a system check.</item>
/// <item>Only the evidence the log registered for the debate is consumed (<see cref="RunLog.RegisteredEvidence"/>), with its
/// rules: the prompts of the launches are left out quietly, and so is any prompt that carried project memory, even when it
/// was registered (ADR-0022, decision 7: the judge never sees the memory).</item>
/// <item>Blind (D-15): participants by label and role; the titles and authors of the evidence of the debate scrubbed of every
/// agent, CLI, vendor and model of a participant (<see cref="DebateBlinding"/>); no excluded vendors nor disposition in the
/// document. When the subject is a run, its dossier of Phase 3 goes in whole (<see cref="IJudgeDossierPackageBuilder"/>):
/// its producers are part of what is judged.</item>
/// <item>The commit under review (<see cref="JudgeDossier.CandidateCommit"/>) is set only when the subject is a run (its
/// candidate) or an audit (its HEAD): in a debate that chooses there is no code to test (D-25), and the validator would
/// otherwise ask for a test run of the copy. The vendors no judge may run on are those of the arrangement
/// (<see cref="JudgingArrangement.ExcludedVendors"/>) and, whatever the header says, the producers of the subject (rule P,
/// never waived).</item>
/// <item>A challenge (subject <see cref="DebateSubjectKind.Verdict"/>, D-38): the challenged verdict is read from the store
/// and compared with its <c>VerdictIssued</c> (decision, validity, issues, task, unit and the hashes of the material); a
/// mismatch is a system check. Its object (a run, a debate or an audit) is rebuilt from the stores, and it decides the
/// family (<see cref="DebateDossierContext.Choice"/>, docs/04 §17.8): a run or an audit is reviewed with the rules of Phase
/// 3; a debate keeps its own family, mode and proposals, so the revision chooses among the proposals of the debate it
/// revises.</item>
/// <item>The package goes to <c>&lt;root&gt;/&lt;project&gt;/&lt;debate&gt;/&lt;first 16 characters of the head&gt;</c>: the
/// question, the dossier (and, for the second judge of a panel, the dossier with the proposals reversed), the subject run in
/// full, the content of every evidence and, when there is a commit and a way to copy it, the copy the judges work in
/// (<see cref="SnapshotFolder"/>). Nothing is deleted nor written through a link from the root down, and every file is new.</item>
/// </list>
/// </remarks>
/// <param name="git">Copies the commit the debate is about for the judges (<c>ArchiveCommitAsync</c>); without it they work in the package.</param>
public sealed class DebateDossierBuilder(
    IDecisionRecorder decisions,
    IDebateRegistry debates,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    IVerdictStore verdicts,
    IJudgeDossierPackageBuilder runs,
    JudgingOptions options,
    IWorktreeManager? git = null)
{
    /// <summary>The dossier in the order of the header, the one the first judge reads.</summary>
    public const string DossierFile = "dossier.json";

    /// <summary>The dossier with the proposals reversed, the one the second judge of a panel reads (D-14).</summary>
    public const string ReversedDossierFile = "dossier-reversed.json";

    /// <summary>The literal question, as the judge is shown it.</summary>
    public const string QuestionFile = "question.txt";

    /// <summary>The whole dossier of Phase 3 of the run the debate is about (or the object of a challenge), with the files of this package.</summary>
    public const string SubjectRunFile = "subject-run.json";

    /// <summary>The copy of the commit the judges work in, inside the package.</summary>
    public const string SnapshotFolder = "snapshot";

    private const string DebatePrefix = "deb-";
    private const string AuditPrefix = "aud-";
    private const int MaxChallengeDepth = 4;
    private const int MaxInlineBytes = 1024 * 1024;

    /// <exception cref="InvalidOperationException">The debate never opened, its header is not registered, or its requirements are not stored.</exception>
    public async Task<DebateDossierPackage> BuildPackageAsync(ProjectContext project, RunId debate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.ProjectId);
        var projectId = project.ProjectId;
        var checks = new List<string>();

        var log = await decisions.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            checks.Add(string.Create(CultureInfo.InvariantCulture, $"The decision log does not verify (entry {log.Verification.FirstInvalidSequence}): {log.Verification.Problem}"));
        }

        var blinding = new DebateBlinding();
        var read = await ReadDebateAsync(projectId, debate, log.Entries, blinding, checks, string.Empty, cancellationToken).ConfigureAwait(false);
        var header = read.Header;
        var frozen = await requirements.FindAsync(projectId, header.RequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The frozen requirement set {header.RequirementSetHash} of debate '{debate}' is not stored.");

        var judging = Arrangement(header);
        var participants = Participants(header);
        var excluded = new HashSet<VendorId>(judging.ExcludedVendors(participants));
        var context = new DebateDossierContext(header.Mode, read.Material.Proposals, DebateJudgingRules.IsChoice(header.Mode, header.Subject.Kind));
        var subjectEvidence = new List<EvidenceRecord>();
        RunSubject? run = null;
        ChallengeSubject? challenge = null;
        string? candidate = null;
        var code = header.Subject.Kind == DebateSubjectKind.Commit ? header.SnapshotCommit ?? header.Subject.Reference : null;

        switch (header.Subject.Kind)
        {
            case DebateSubjectKind.Run:
                run = await RunSubjectAsync(projectId, header.Subject.Reference, checks, "The run this debate is about", cancellationToken).ConfigureAwait(false);
                if (run is not null)
                {
                    candidate = run.Dossier.CandidateCommit;
                    excluded.UnionWith(run.Dossier.ExcludedVendors);
                }

                code = header.SnapshotCommit ?? candidate;
                break;
            case DebateSubjectKind.Verdict:
                challenge = await ChallengeAsync(projectId, header, debate, log.Entries, blinding, checks, cancellationToken).ConfigureAwait(false);
                switch (challenge.Object)
                {
                    case ChallengedObject.Run when challenge.Run is { } challengedRun:
                        candidate = challengedRun.Dossier.CandidateCommit;
                        code = candidate;
                        excluded.UnionWith(challengedRun.Dossier.ExcludedVendors);
                        break;
                    case ChallengedObject.Debate when challenge.Debate is { } challengedDebate:
                        context = new DebateDossierContext(
                            challengedDebate.Mode,
                            challengedDebate.Proposals,
                            await IsChoiceAsync(projectId, challengedDebate.Debate, log.Entries, 1, cancellationToken).ConfigureAwait(false));
                        subjectEvidence.AddRange(challengedDebate.Evidence);
                        excluded.UnionWith(await ExcludedOfDebateAsync(projectId, challengedDebate.Debate, cancellationToken).ConfigureAwait(false));
                        if (challenge.DebateRun is { } debateRun)
                        {
                            candidate = debateRun.Dossier.CandidateCommit;
                            excluded.UnionWith(debateRun.Dossier.ExcludedVendors);
                        }

                        code = challengedDebate.Subject == DebateSubjectKind.Commit ? challengedDebate.SnapshotCommit ?? challengedDebate.SubjectReference : candidate;
                        break;
                    case ChallengedObject.Audit when challenge.Audit is { } audit:
                        subjectEvidence.AddRange(audit.Evidence);
                        candidate = audit.HeadCommit;
                        code = audit.HeadCommit;
                        excluded.UnionWith(audit.Producers);
                        break;
                }

                if (challenge.Object != ChallengedObject.Debate)
                {
                    // A run, an audit or an unknown object is reviewed with the rules of Phase 3 (docs/04 §17.8).
                    context = new DebateDossierContext(header.Mode, [], Choice: false);
                }

                break;
        }

        var runDossier = run?.Dossier ?? challenge?.Run?.Dossier ?? challenge?.DebateRun?.Dossier;
        if (runDossier is not null)
        {
            subjectEvidence.InsertRange(0, runDossier.Evidence);
        }

        var allEvidence = subjectEvidence.Concat(read.Material.Evidence).DistinctBy(e => e.Id).ToList();
        var dossier = new JudgeDossier(
            read.Material.Question,
            frozen,
            runDossier?.OrchestratorPlan ?? string.Empty,
            runDossier?.Decisions ?? [],
            log.HeadHash,
            allEvidence,
            runDossier?.Tasks ?? [],
            excluded)
        {
            CandidateCommit = candidate,
            Debate = context,
        };

        var built = new DebateDossier(read.Material, dossier, run, challenge);
        var folder = DossierPackageWriter.PackageFolder(options.DossierRoot, projectId, debate.Value, log.HeadHash);
        var (written, files) = await WritePackageAsync(folder, projectId, debate, built, header.Judges.Count, checks, cancellationToken).ConfigureAwait(false);
        var snapshot = code is null ? null : await CopyCommitAsync(project, code, folder, cancellationToken).ConfigureAwait(false);
        return new DebateDossierPackage(written, files, header, read.HeaderVerified, log.Verification.IsValid, judging, participants, code, snapshot);
    }

    /// <summary>The arrangement of the header, as the domain checks it: the judges with the provider and vendor of their definitions.</summary>
    public static JudgingArrangement Arrangement(DebateRecord header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new JudgingArrangement(
            header.Judging,
            [.. header.Judges.Select(j => new JudgeIdentity(j.Id, j.Model))],
            new HashSet<VendorId>(header.ProducerVendors),
            new HashSet<VendorId>(header.WaivedParticipantVendors));
    }

    /// <summary>The participants of the header, as the domain sees them.</summary>
    public static IReadOnlyList<DebateParticipant> Participants(DebateRecord header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return [.. header.Participants.Select(p => new DebateParticipant(p.Agent.Id, p.Role, p.Agent.Model))];
    }

    private sealed record DebateRead(DebateRecord Header, bool HeaderVerified, DebateMaterial Material);

    /// <summary>
    /// One debate from the log and the stores, blind: the header (checked against <c>DebateOpened</c>), the question, the
    /// contributions, the rounds, the evidence and the protocol. <paramref name="prefix"/> names it in its system checks when
    /// it is not the debate being judged.
    /// </summary>
    private async Task<DebateRead> ReadDebateAsync(string projectId, RunId debate, IReadOnlyList<DecisionEntry> log, DebateBlinding blinding, List<string> checks, string prefix, CancellationToken cancellationToken)
    {
        void Check(string problem) => checks.Add(prefix + problem);

        var entries = RunLog.EntriesOf(log, debate.Value);
        var (openedEntry, opened) = entries
            .Where(e => e.Kind == DecisionKind.DebateOpened)
            .Select(e => (Entry: e, Payload: Read<DebateOpenedPayload>(e)))
            .FirstOrDefault(p => p.Payload is not null);
        if (opened is null)
        {
            throw new InvalidOperationException($"The decision log of project '{projectId}' has no DebateOpened for debate '{debate}'.");
        }

        var header = await debates.FindAsync(projectId, debate, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Debate '{debate}' of project '{projectId}' is not registered.");
        blinding.Add(header.Participants);

        var stored = DebateHeaderHash.Of(header);
        var verified = string.Equals(stored, opened.HeaderHash, StringComparison.Ordinal);
        if (!verified)
        {
            Check($"The header stored for debate '{debate}' hashes to {stored}, but DebateOpened anchored {opened.HeaderHash}: its participants, judges, disposition, waivers or limits changed after it opened, so no judge is launched on it.");
        }

        if (ContentHash.IsSha256Hex(opened.QuestionSha256))
        {
            var approved = DebateHeaderHash.Proposal(header, opened.QuestionSha256);
            if (!string.Equals(approved, opened.ApprovedProposalHash, StringComparison.Ordinal))
            {
                Check($"Debate '{debate}' opened with a header whose proposal hashes to {approved}, but the user approved {opened.ApprovedProposalHash}: what opened is not what was approved.");
            }
        }

        foreach (var disagreement in Disagreements(opened, header))
        {
            Check($"DebateOpened of debate '{debate}' and its stored header disagree on {disagreement}; the header is used.");
        }

        // A header rewritten behind the store could repeat an agent: it is already a system check, and it must not break the dossier.
        var labels = header.Participants.GroupBy(p => p.Agent.Id).ToDictionary(g => g.Key, g => g.First().Label);
        var question = await ReadAnchoredTextAsync(projectId, header.Question, opened.QuestionSha256, "the question", Check, cancellationToken).ConfigureAwait(false)
            ?? $"[Withheld by Agent Studio: the question (evidence '{header.Question}') is not the one DebateOpened anchored, or cannot be read. The system checks say why.]";

        var problems = new List<string>();
        var storedEvidence = await evidence.ListByRunAsync(projectId, debate.Value, cancellationToken).ConfigureAwait(false);
        var unannounced = AnchoredReasons(log, debate);
        unannounced.ExceptWith(entries.Where(e => e.Kind == DecisionKind.EvidenceRegistered).Select(Read<EvidenceRegisteredPayload>).OfType<EvidenceRegisteredPayload>().Select(p => p.Evidence));
        var registered = RunLog.RegisteredEvidence(entries, [.. storedEvidence.Where(e => !unannounced.Contains(e.Id))], problems);
        foreach (var problem in problems)
        {
            Check(problem);
        }

        var contributions = new List<DebateContributionMaterial>();
        var protocol = new List<DebateProtocolEntry> { new(openedEntry!.Sequence, openedEntry.RecordedAt, DecisionKind.DebateOpened, "apertura") };
        var rounds = new List<DebateRoundMaterial>();
        var byId = registered.ToDictionary(e => e.Id);
        var authorOf = new Dictionary<EvidenceId, (string Label, int Round, ContributionKind Kind, string Contribution)>();
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case DecisionKind.DebateContributionRecorded when Read<DebateContributionRecordedPayload>(entry) is { } recorded:
                    var label = ContributionLabel(recorded.Index);
                    if (!labels.TryGetValue(recorded.Author, out var author))
                    {
                        Check($"Contribution {label} of debate '{debate}' (entry {entry.Sequence}) is by someone who is not a participant.");
                        author = "?";
                    }

                    if (!byId.TryGetValue(recorded.Evidence, out var record) || !string.Equals(record.ContentSha256, recorded.Sha256, StringComparison.Ordinal))
                    {
                        Check($"Contribution {label} of debate '{debate}' (entry {entry.Sequence}): its evidence '{recorded.Evidence}' is not evidence the log registered for the debate with SHA-256 {recorded.Sha256}.");
                    }

                    var text = await ReadAnchoredTextAsync(projectId, recorded.Evidence, recorded.Sha256, $"contribution {label}", Check, cancellationToken).ConfigureAwait(false);
                    authorOf[recorded.Evidence] = (author, recorded.Round, recorded.Kind, label);
                    contributions.Add(new DebateContributionMaterial(
                        entry.Sequence,
                        label,
                        recorded.Index,
                        recorded.Round,
                        author,
                        recorded.Kind,
                        recorded.RepliesTo is { } target ? ContributionLabel(target) : null,
                        recorded.Evidence,
                        recorded.Sha256,
                        text,
                        recorded.Cites ?? [],
                        recorded.UnresolvedCitations ?? [],
                        recorded.Redactions,
                        recorded.Finished,
                        recorded.AnsweredAt));
                    protocol.Add(new DebateProtocolEntry(entry.Sequence, entry.RecordedAt, entry.Kind, label));
                    break;
                case DecisionKind.DebateRoundClosed when Read<DebateRoundClosedPayload>(entry) is { } closed:
                    rounds.Add(new DebateRoundMaterial(entry.Sequence, entry.RecordedAt, closed.Round, closed.Next, closed.Judgment, closed.Limit, closed.ElapsedSeconds));
                    protocol.Add(new DebateProtocolEntry(entry.Sequence, entry.RecordedAt, entry.Kind, string.Create(CultureInfo.InvariantCulture, $"ronda {closed.Round}")));
                    break;
                case DecisionKind.EvidenceRegistered when Read<EvidenceRegisteredPayload>(entry) is { } announced:
                    protocol.Add(new DebateProtocolEntry(entry.Sequence, entry.RecordedAt, entry.Kind, announced.Evidence.Value));
                    break;
            }
        }

        contributions.Sort((a, b) => a.Index.CompareTo(b.Index));
        var blinded = registered.Select(e => authorOf.TryGetValue(e.Id, out var by)
                ? blinding.Blind(e, string.Create(CultureInfo.InvariantCulture, $"Contribución {by.Contribution} de {by.Label} (ronda {by.Round}, {by.Kind})"), by.Label)
                : blinding.Blind(e, producedBy: labels.FirstOrDefault(l => string.Equals(l.Key.Value, e.ProducedBy, StringComparison.Ordinal)).Value))
            .ToList();

        var quotes = new Dictionary<EvidenceId, string>();
        foreach (var cited in contributions.SelectMany(c => c.Cites).Distinct())
        {
            if (byId.TryGetValue(cited, out var quote) && quote is { Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem }
                && await ReadAnchoredTextAsync(projectId, cited, quote.ContentSha256, $"the quote '{cited}'", Check, cancellationToken).ConfigureAwait(false) is { } lines)
            {
                quotes[cited] = lines;
            }
        }

        var material = new DebateMaterial(
            debate,
            header.Mode,
            header.Subject.Kind,
            header.Subject.Reference,
            header.SnapshotCommit,
            question,
            header.Question,
            openedEntry.Sequence,
            openedEntry.RecordedAt,
            [.. header.Participants.Select(p => new DebateSide(p.Label, p.Role))],
            Proposals(header, contributions),
            contributions,
            rounds,
            header.Limits,
            header.MaxAgentRuns,
            blinded,
            quotes,
            protocol);
        return new DebateRead(header, verified, material);
    }

    /// <summary>
    /// The labels that can be chosen (D-07 and D-25), in the order of the header: the participants with a proposal in round
    /// 1, all of them in a free debate and an architecture review and only the proponents in an adversarial one. A tribunal
    /// synthesises and a debate about a run reviews it: neither has a proposal to choose.
    /// </summary>
    private static IReadOnlyList<string> Proposals(DebateRecord header, IReadOnlyList<DebateContributionMaterial> contributions)
    {
        if (!DebateJudgingRules.IsChoice(header.Mode, header.Subject.Kind) || header.Mode == DebateMode.Tribunal)
        {
            return [];
        }

        var proposed = contributions.Where(c => c is { Round: 1, Kind: ContributionKind.Proposal }).Select(c => c.Author).ToHashSet(StringComparer.Ordinal);
        return [.. header.Participants
            .Where(p => header.Mode != DebateMode.Adversarial || p.Role == DebateRole.Proponent)
            .Where(p => proposed.Contains(p.Label))
            .Select(p => p.Label)];
    }

    /// <summary>What <c>DebateOpened</c> repeats of the header and does not say the same.</summary>
    private static IEnumerable<string> Disagreements(DebateOpenedPayload opened, DebateRecord header)
    {
        if (opened.Mode != header.Mode)
        {
            yield return $"the mode ({opened.Mode}, {header.Mode})";
        }

        if (!string.Equals(opened.RequirementSetHash, header.RequirementSetHash, StringComparison.Ordinal))
        {
            yield return $"the requirements ({opened.RequirementSetHash}, {header.RequirementSetHash})";
        }

        if (opened.Question != header.Question)
        {
            yield return $"the question ({opened.Question}, {header.Question})";
        }

        if (opened.Subject != header.Subject.Kind || !string.Equals(opened.SubjectRef, header.Subject.Reference, StringComparison.Ordinal))
        {
            yield return $"the subject ({opened.Subject} {opened.SubjectRef}, {header.Subject.Kind} {header.Subject.Reference})";
        }

        if (!string.Equals(opened.Challenges, header.Challenges, StringComparison.Ordinal))
        {
            yield return $"the challenged verdict ({opened.Challenges}, {header.Challenges})";
        }
    }

    /// <summary>The run a debate is about, or the object of a challenged verdict: its dossier of Phase 3, or null with a system check.</summary>
    private async Task<RunSubject?> RunSubjectAsync(string projectId, string? run, List<string> checks, string what, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run))
        {
            checks.Add($"{what} is not named, so its material is not in the dossier.");
            return null;
        }

        try
        {
            var package = await runs.BuildPackageAsync(new DossierRequest(projectId, AuditScope.CurrentPlan, run), cancellationToken).ConfigureAwait(false);
            foreach (var problem in package.Dossier.IntegrityProblems)
            {
                checks.Add($"Run '{run}': {problem}");
            }

            return new RunSubject(new RunId(run), package.Dossier);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            checks.Add($"{what} ('{run}') cannot be read from the stores, so its material is not in the dossier: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The challenge a revision debate answers (D-38): the challenged verdict from the store, compared with its
    /// <c>VerdictIssued</c>; the <c>VerdictChallenged</c> that opened the revision, with the reasons read against its hash;
    /// and the object of the verdict, rebuilt from the stores.
    /// </summary>
    private async Task<ChallengeSubject> ChallengeAsync(string projectId, DebateRecord header, RunId debate, IReadOnlyList<DecisionEntry> log, DebateBlinding blinding, List<string> checks, CancellationToken cancellationToken)
    {
        var verdictId = header.Challenges ?? header.Subject.Reference;
        if (string.IsNullOrWhiteSpace(verdictId))
        {
            checks.Add($"Revision debate '{debate}' names no challenged verdict.");
            return new ChallengeSubject(string.Empty, null, ChallengedObject.Unknown, null, null, null, null, null, null, null);
        }

        var stored = (await verdicts.ListAsync(projectId, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => string.Equals(v.Id, verdictId, StringComparison.Ordinal));
        var issued = log.Where(e => e.Kind == DecisionKind.VerdictIssued).Select(e => (Entry: e, Payload: Read<VerdictIssuedPayload>(e))).Where(i => i.Payload?.VerdictId == verdictId).ToList();
        if (stored is null)
        {
            checks.Add($"The challenged verdict '{verdictId}' is not in the verdict store: what was challenged cannot be shown, and this revision cannot be approved.");
        }
        else
        {
            CheckIssued(stored, issued, header, checks);
        }

        var challenged = log.Where(e => e.Kind == DecisionKind.VerdictChallenged)
            .Select(Read<VerdictChallengedPayload>)
            .FirstOrDefault(p => p is not null && string.Equals(p.RevisionDebate, debate.Value, StringComparison.Ordinal) && string.Equals(p.Verdict, verdictId, StringComparison.Ordinal));
        string? reasons = null;
        if (challenged is null)
        {
            checks.Add($"The decision log has no VerdictChallenged of verdict '{verdictId}' that opens revision debate '{debate}'.");
        }
        else
        {
            reasons = await ReadAnchoredTextAsync(projectId, challenged.Reasons, challenged.ReasonsSha256, "the reasons of the challenge", checks.Add, cancellationToken).ConfigureAwait(false);
        }

        var unit = stored?.RunId;
        var kind = ObjectOf(unit);
        if (stored is not null && kind == ChallengedObject.Unknown)
        {
            checks.Add($"The challenged verdict '{verdictId}' belongs to '{unit}', which is not a run, a debate nor an audit of the log: what it was about cannot be rebuilt.");
        }
        RunSubject? run = null;
        DebateMaterial? challengedDebate = null;
        RunSubject? debateRun = null;
        AuditSubject? audit = null;
        switch (kind)
        {
            case ChallengedObject.Run:
                run = await RunSubjectAsync(projectId, unit, checks, $"The run of the challenged verdict '{verdictId}'", cancellationToken).ConfigureAwait(false);
                break;
            case ChallengedObject.Debate:
                var original = await ReadDebateAsync(projectId, new RunId(unit!), log, blinding, checks, $"Challenged debate '{unit}': ", cancellationToken).ConfigureAwait(false);
                challengedDebate = original.Material;
                if (original.Header.Subject.Kind == DebateSubjectKind.Run)
                {
                    debateRun = await RunSubjectAsync(projectId, original.Header.Subject.Reference, checks, $"The run challenged debate '{unit}' is about", cancellationToken).ConfigureAwait(false);
                }

                break;
            case ChallengedObject.Audit:
                audit = await AuditSubjectAsync(projectId, unit!, log, checks, cancellationToken).ConfigureAwait(false);
                break;
        }

        return new ChallengeSubject(verdictId, stored, kind, challenged?.ChallengedBy, challenged?.Reasons, reasons, run, challengedDebate, debateRun, audit);
    }

    /// <summary>
    /// The challenged verdict as the store holds it must be the one <c>VerdictIssued</c> announced (D-38): that payload does
    /// not anchor the whole text, so its decision, validity, issues, task, unit and supersession are compared, and so are the
    /// hashes of the material it examined, except the one its own issues say it did not echo.
    /// </summary>
    private static void CheckIssued(StoredVerdict stored, List<(DecisionEntry Entry, VerdictIssuedPayload? Payload)> issued, DebateRecord header, List<string> checks)
    {
        if (issued.Count == 0)
        {
            checks.Add($"The challenged verdict '{stored.Id}' was never announced with VerdictIssued: the store holds a verdict the log does not know, so it is not what was challenged.");
            return;
        }

        var (entry, payload) = issued[0];
        var differences = new List<string>();
        void Compare(bool same, string field)
        {
            if (!same)
            {
                differences.Add(field);
            }
        }

        var codes = (stored.Issues ?? []).Select(i => i.Code).ToList();
        Compare(string.Equals(payload!.RunId, stored.RunId, StringComparison.Ordinal), "the unit");
        Compare(string.Equals(payload.Task?.Value, stored.TaskId, StringComparison.Ordinal), "the task");
        Compare(payload.Decision == stored.Verdict.Decision, "the decision");
        Compare(payload.Valid == stored.Valid && stored.Valid == (codes.Count == 0), "the validity");
        Compare((payload.Violations ?? []).SequenceEqual(codes), "the issues");
        Compare(string.Equals(payload.Supersedes, stored.Supersedes, StringComparison.Ordinal), "the verdict it supersedes");
        if (!codes.Contains(VerdictViolationCode.RequirementSetMismatch))
        {
            Compare(string.Equals(payload.RequirementSetHash, stored.Verdict.RequirementSetHash, StringComparison.Ordinal), "the requirements");
        }

        if (!codes.Contains(VerdictViolationCode.DecisionLogMismatch))
        {
            Compare(string.Equals(payload.DecisionLogHeadHash, stored.Verdict.DecisionLogHeadHash, StringComparison.Ordinal), "the head of the log");
        }

        if (differences.Count > 0)
        {
            checks.Add(string.Create(CultureInfo.InvariantCulture, $"The challenged verdict '{stored.Id}' in the store does not match its VerdictIssued (entry {entry.Sequence}) on {string.Join(", ", differences)}: it was rewritten after it was issued, so what is shown is not what was challenged."));
        }

        if (issued.Count > 1)
        {
            checks.Add($"The challenged verdict '{stored.Id}' was announced {issued.Count} times with VerdictIssued.");
        }

        if (!string.Equals(payload.RequirementSetHash, header.RequirementSetHash, StringComparison.Ordinal))
        {
            checks.Add($"The revision judges requirement set {header.RequirementSetHash}, but the challenged verdict '{stored.Id}' was about {payload.RequirementSetHash} (D-38: the requirements of a revision are those of the verdict it challenges).");
        }
    }

    /// <summary>What a stored verdict was about, from its unit: a debate, an audit or a run; unknown when the unit is not an id of the log.</summary>
    private static ChallengedObject ObjectOf(string? unit) => unit switch
    {
        _ when !IsUnit(unit) => ChallengedObject.Unknown,
        _ when unit!.StartsWith(DebatePrefix, StringComparison.Ordinal) => ChallengedObject.Debate,
        _ when unit.StartsWith(AuditPrefix, StringComparison.Ordinal) => ChallengedObject.Audit,
        _ => ChallengedObject.Run,
    };

    /// <summary>Whether <paramref name="unit"/> can be the id of a run, a debate or an audit (<see cref="RunId"/>): a row of the store can say anything.</summary>
    private static bool IsUnit(string? unit) =>
        unit is { Length: > 0 and <= RunId.MaxLength } && unit.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// Whether a debate chooses (<see cref="DebateJudgingRules.IsChoice"/>), and for a revision debate, whether the object of
    /// the verdict it challenges does, followed a few levels at most. Anything unknown is reviewed with the rules of Phase 3.
    /// </summary>
    private async Task<bool> IsChoiceAsync(string projectId, RunId debate, IReadOnlyList<DecisionEntry> log, int depth, CancellationToken cancellationToken)
    {
        var header = await debates.FindAsync(projectId, debate, cancellationToken).ConfigureAwait(false);
        if (header is null || depth > MaxChallengeDepth)
        {
            return false;
        }

        if (header.Subject.Kind != DebateSubjectKind.Verdict)
        {
            return DebateJudgingRules.IsChoice(header.Mode, header.Subject.Kind);
        }

        var challenged = header.Challenges ?? header.Subject.Reference;
        var stored = (await verdicts.ListAsync(projectId, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => string.Equals(v.Id, challenged, StringComparison.Ordinal));
        return ObjectOf(stored?.RunId) == ChallengedObject.Debate
            && await IsChoiceAsync(projectId, new RunId(stored!.RunId!), log, depth + 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The vendors no judge of a debate may run on (its arrangement with its participants): a revision inherits them (D-39).</summary>
    private async Task<IReadOnlySet<VendorId>> ExcludedOfDebateAsync(string projectId, RunId debate, CancellationToken cancellationToken)
    {
        var header = await debates.FindAsync(projectId, debate, cancellationToken).ConfigureAwait(false);
        return header is null ? new HashSet<VendorId>() : Arrangement(header).ExcludedVendors(Participants(header));
    }

    /// <summary>An audit that is the object of a challenged verdict: what <c>ProjectAuditStarted</c> says and the evidence its log registered.</summary>
    private async Task<AuditSubject> AuditSubjectAsync(string projectId, string audit, IReadOnlyList<DecisionEntry> log, List<string> checks, CancellationToken cancellationToken)
    {
        var entries = RunLog.EntriesOf(log, audit);
        var started = entries.Where(e => e.Kind == DecisionKind.ProjectAuditStarted).Select(Read<ProjectAuditStartedPayload>).FirstOrDefault(p => p is not null);
        if (started is null)
        {
            checks.Add($"The audit '{audit}' of the challenged verdict has no ProjectAuditStarted in the log, so what it audited is unknown.");
        }

        var problems = new List<string>();
        var registered = RunLog.RegisteredEvidence(entries, await evidence.ListByRunAsync(projectId, audit, cancellationToken).ConfigureAwait(false), problems);
        checks.AddRange(problems.Select(p => $"Audit '{audit}': {p}"));
        IReadOnlyList<VendorId> producers = [.. (started?.ProducerVendors ?? []).Concat(started?.DeclaredAuthors ?? []).Distinct()];
        return new AuditSubject(new RunId(audit), started?.HeadCommit, started?.RequirementSetHash, producers, registered);
    }

    /// <summary>
    /// The reasons of the challenges a revision debate answers. <c>VerdictChallenged</c> anchors their id and SHA-256, so a row
    /// of them that no <c>EvidenceRegistered</c> of the debate announces is not an unregistered row: its text is read against
    /// that hash for the challenge, but it is not evidence the judge can cite.
    /// </summary>
    private static HashSet<EvidenceId> AnchoredReasons(IReadOnlyList<DecisionEntry> log, RunId debate) =>
        [.. log.Where(e => e.Kind == DecisionKind.VerdictChallenged)
            .Select(Read<VerdictChallengedPayload>)
            .Where(p => p is not null && string.Equals(p.RevisionDebate, debate.Value, StringComparison.Ordinal))
            .Select(p => p!.Reasons)];

    /// <summary>
    /// The text of an evidence, only as the log anchored it (R3-08): its bytes must hash to <paramref name="anchoredSha256"/>.
    /// Null, with a system check, when it cannot be read, does not match, or is too large to go inline (the judge reads the
    /// file of the package, which says whether it matched).
    /// </summary>
    private async Task<string?> ReadAnchoredTextAsync(string projectId, EvidenceId id, string anchoredSha256, string what, Action<string> check, CancellationToken cancellationToken)
    {
        byte[] content;
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, id, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                content = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            check($"Evidence '{id}' ({what}): its content cannot be read ({ex.Message}); it is withheld and proves nothing.");
            return null;
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actual, anchoredSha256, StringComparison.OrdinalIgnoreCase))
        {
            check($"Evidence '{id}' ({what}) hashes to {actual}, not to {anchoredSha256}, the SHA-256 the decision log anchored for it: it was rewritten after it was registered, so it is withheld and proves nothing.");
            return null;
        }

        if (content.Length > MaxInlineBytes)
        {
            return null; // verified, but too large to go inline: the judge reads the file of the package
        }

        using var reader = new StreamReader(new MemoryStream(content, writable: false), Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies and checks every evidence first, so what the copies find is part of the dossier the validator checks, and
    /// then writes the question, the dossier (reversed too for the second judge of a panel) and the subject run.
    /// </summary>
    private async Task<(DebateDossier Dossier, DossierFiles Files)> WritePackageAsync(string folder, string projectId, RunId debate, DebateDossier dossier, int judges, List<string> checks, CancellationToken cancellationToken)
    {
        var evidenceFolder = DossierPackageWriter.Prepare(options.DossierRoot, folder);
        var copies = new List<DossierEvidenceFile>();
        for (var i = 0; i < dossier.Dossier.Evidence.Count; i++)
        {
            var record = dossier.Dossier.Evidence[i];
            var name = $"{i + 1:D3}-{DossierPackageWriter.Segment(record.Id.Value, 48)}{(record.Kind == EvidenceKind.CodeDiff ? ".diff" : ".txt")}";
            copies.Add(await CopyEvidenceAsync(projectId, record, evidenceFolder, name, checks, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<string> problems = [.. checks.Distinct(StringComparer.Ordinal)];
        var checkedDossier = dossier with { Dossier = dossier.Dossier with { IntegrityProblems = problems } };
        var files = new DossierFiles(folder, projectId, debate.Value, copies, problems);
        await DossierPackageWriter.WriteNewTextAsync(Path.Combine(folder, QuestionFile), checkedDossier.Debate.Question, cancellationToken).ConfigureAwait(false);
        await DossierPackageWriter.WriteNewTextAsync(Path.Combine(folder, DossierFile), DebateDossierDocument.ToJson(checkedDossier, files, reversed: false), cancellationToken).ConfigureAwait(false);
        if (judges > 1)
        {
            await DossierPackageWriter.WriteNewTextAsync(Path.Combine(folder, ReversedDossierFile), DebateDossierDocument.ToJson(checkedDossier, files, reversed: true), cancellationToken).ConfigureAwait(false);
        }

        if (DebateDossierDocument.SubjectRun(checkedDossier) is { } run)
        {
            await DossierPackageWriter.WriteNewTextAsync(Path.Combine(folder, SubjectRunFile), DebateDossierDocument.RunJson(run, files), cancellationToken).ConfigureAwait(false);
        }

        return (checkedDossier, files);
    }

    private async Task<DossierEvidenceFile> CopyEvidenceAsync(string projectId, EvidenceRecord record, string evidenceFolder, string name, List<string> checks, CancellationToken cancellationToken)
    {
        try
        {
            var source = await evidence.OpenContentAsync(projectId, record.Id, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var (size, written, sha256) = await DossierPackageWriter.CopyAsync(source, Path.Combine(evidenceFolder, name), options.MaxEvidenceFileBytes, cancellationToken).ConfigureAwait(false);
                var verified = string.Equals(sha256, record.ContentSha256, StringComparison.Ordinal);
                if (!verified)
                {
                    // The wording of the dossier of a run, so the same problem found by both builders is listed once.
                    checks.Add($"Evidence '{record.Id}': its content does not match the SHA-256 recorded at capture; it changed afterwards and proves nothing.");
                }

                return new DossierEvidenceFile(record.Id, "evidence/" + name, verified, written < size, size);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add($"Evidence '{record.Id}': its content cannot be read ({ex.Message}); it proves nothing.");
            return new DossierEvidenceFile(record.Id, null, false, false, 0);
        }
    }

    /// <summary>
    /// The copy of <paramref name="commit"/> the judges work in, inside the package, made by the hardened git layer (only
    /// files and folders, no configuration, hook or filter of the repository). Null when there is no git layer or the copy
    /// fails: the judges then work in the package, and the prompt says the code is not there.
    /// </summary>
    private async Task<string?> CopyCommitAsync(ProjectContext project, string commit, string folder, CancellationToken cancellationToken)
    {
        if (git is null || string.IsNullOrWhiteSpace(project.RootPath))
        {
            return null;
        }

        var snapshot = Path.Combine(folder, SnapshotFolder);
        try
        {
            DossierPackageWriter.RefuseLinks(options.DossierRoot, snapshot);
            Directory.CreateDirectory(snapshot);
            await git.ArchiveCommitAsync(project.RootPath, commit, snapshot, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                DossierPackageWriter.RefuseLinks(options.DossierRoot, snapshot);
                if (Directory.Exists(snapshot))
                {
                    Directory.Delete(snapshot, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A partial copy stays in the package; the judges are not pointed at it.
            }

            return null;
        }
    }

    private static string ContributionLabel(int index) => "C" + (index + 1).ToString(CultureInfo.InvariantCulture);

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
}
