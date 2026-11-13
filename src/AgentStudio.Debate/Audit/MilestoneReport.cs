using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Security;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Debate.Audit;

// The report of the close of a milestone (docs/16 D-49 and D-50, F9-10). It is a projection, like DailySummaryBuilder: nobody writes
// it, and the same log, board and audit give the same bytes. It holds no text of an agent (the verdict contributes its decision, its
// disposition and its assessment of each requirement, never its prose) and no clock: it is anchored to the head of the log it was read
// from. The consumption and the estimate against the real figure (D-34 and D-20) belong to the observability of Phase 9 (WP2) and
// are not in it, and the report says so (NotIncluded) instead of showing an empty section.

/// <summary>The milestone, as its header says: the person's name and objective, and when it was opened.</summary>
public sealed record MilestoneReportHeader(string Id, string Name, string Objective, DateTimeOffset OpenedAt);

/// <summary>
/// What the report rests on: the log read for the runs, debates and pending problems (how many entries, and its head), the commit the
/// audit judged, and the log the board of the requirements was read from (it can be longer by the entries written in between).
/// </summary>
public sealed record MilestoneReportAnchor(long LogEntries, string LogHeadHash, string? AuditedCommit, bool UncommittedChanges, long BoardLogEntries, string BoardLogHeadHash);

/// <summary>A frozen set of requirements the milestone has worked against, in order; the last one is the one audited.</summary>
public sealed record MilestoneReportRequirementSet(int Version, string Hash, bool Current);

/// <summary>
/// A requirement of the frozen set, and its states (D-50). <c>Implemented</c>: the trustworthy audit judged it satisfied (D-49: its
/// verdict is the functionality really implemented). <c>Reviewed</c> and <c>Integrated</c>: the board (D-19) has its card in «hecho» or in
/// «integrado» (by the local delivery or, in Phase 10, by a pull request merged with the commit the judge saw, docs/21 D-29). «Desplegado» (<c>Deployed</c>) is there only when
/// the platform said it deployed what the merge left (docs/21 D-29): Agent Studio deploys nothing, and without the platform's word that state does not appear (the key is
/// omitted, there is no «no desplegado»). <c>MergedWithUnjudgedChanges</c> (omitted when false) says its pull request was merged with something the judge did not see: it is not
/// integrated. <c>Column</c> is the card's column, or null when the board gave none.
/// </summary>
public sealed record MilestoneReportRequirement(
    string Id,
    string Priority,
    string Statement,
    string? Column,
    string Assessment,
    bool Implemented,
    bool Reviewed,
    bool Integrated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MergedWithUnjudgedChanges = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Deployed = null);

/// <summary>
/// A pull request of a run of the milestone, as the log last observed it (docs/21 D-28). <c>ModeDeduced</c>: the mode of the merge is a deduction (a merge made outside, or one
/// the platform leaves ambiguous), never a fact; <c>RevertSuspected</c>: a commit of the base branch says it reverts the merge (read from its message: deduced as well).
/// </summary>
public sealed record MilestoneReportPullRequest(string Id, string Run, string State, string? MergeCommit, string? MergeMode, bool ModeDeduced, bool RevertSuspected, bool Deployed);

/// <summary>
/// What the log holds of the remote for the milestone (Phase 10): its pull requests, and the entries of the remote the report left out because they did not validate (their
/// sequence in the log). The section is omitted when the log has nothing of the remote, so the report of a project without one is the one of Phase 9, byte for byte.
/// </summary>
public sealed record MilestoneReportRemote(
    IReadOnlyList<MilestoneReportPullRequest> PullRequests,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<long>? IgnoredEntries = null);

public sealed record MilestoneReportAgent(string Agent, string Role, string Provider, string Vendor);

public sealed record MilestoneReportVerdict(string Id, string? Task, string Decision, bool Valid, bool Challenged);

public sealed record MilestoneReportAlert(string Code, string Message);

/// <summary>A run of the milestone: the ones linked and, with <c>LinkedDirectly</c> false, their follow-ups (<c>RunStarted.Follows</c>).</summary>
public sealed record MilestoneReportRun(
    string Id,
    bool LinkedDirectly,
    DateTimeOffset? StartedAt,
    string? Follows,
    string? BaseCommit,
    string? Phase,
    string? IntegrationCommit,
    IReadOnlyList<string> Deliveries,
    IReadOnlyList<MilestoneReportAgent> Agents,
    IReadOnlyList<MilestoneReportVerdict> Verdicts,
    IReadOnlyList<MilestoneReportAlert> Alerts);

public sealed record MilestoneReportDebate(string Id, string? Mode, string Outcome, string? Rule);

public sealed record MilestoneReportEvidence(string Id, string Kind, string Title, string? SubjectCommit, int? Passed, int? Failed, int? Skipped, int? ExitCode);

/// <summary>The audit that closed the milestone: who judged and what, as the system kept it (the judge's own words are in its verdict, not here).</summary>
public sealed record MilestoneReportAudit(
    string Id,
    string? VerdictId,
    string Decision,
    string Disposition,
    string JudgeAgent,
    string JudgeVendor,
    string? JudgeModel,
    bool Trustworthy,
    bool Corrected,
    bool DecisionLogIntact,
    int ValidationIssues,
    IReadOnlyList<MilestoneReportEvidence> Verification,
    IReadOnlyList<MilestoneReportAlert> Alerts);

/// <summary>Where the milestone departs from «done»: a stable code, what it is about and the sentence the person reads (Spanish).</summary>
public sealed record MilestoneReportDeviation(string Code, string Subject, string Message);

public sealed record MilestoneReportPending(string Code, string Source, string Message);

/// <summary>A report an earlier close left: reopening keeps it (D-50).</summary>
public sealed record MilestoneReportEarlier(int Revision, string Audit, string Evidence, string Sha256, string? IntegratedCommit, DateTimeOffset At);

/// <summary>
/// The report (see the notes above). <see cref="ToJson"/> is its one rendering, with fixed key order and newlines, and
/// <see cref="Sha256"/> the SHA-256 of the UTF-8 bytes of it, which is what <c>MilestoneClosed</c> anchors.
/// </summary>
public sealed record MilestoneReport(
    string Format,
    int Revision,
    MilestoneReportHeader Milestone,
    MilestoneReportAnchor Anchor,
    IReadOnlyList<MilestoneReportRequirementSet> RequirementSets,
    IReadOnlyList<MilestoneReportRequirement> Requirements,
    IReadOnlyList<MilestoneReportRun> Runs,
    IReadOnlyList<MilestoneReportDebate> Debates,
    MilestoneReportAudit Audit,
    IReadOnlyList<MilestoneReportDeviation> Deviations,
    IReadOnlyList<MilestoneReportPending> Pending,
    IReadOnlyList<MilestoneReportEarlier> EarlierReports,
    IReadOnlyList<string> NotIncluded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MilestoneReportRemote? Remote = null)
{
    public const string CurrentFormat = "agent-studio/milestone-report/1";

    /// <summary>The title of the evidence that holds a report, followed by the milestone and the revision.</summary>
    public const string EvidenceTitle = "Informe de cierre del hito";

    // Codes of MilestoneReportDeviation, stable: the desktop and the client documentation read them.
    public const string RequirementNotSatisfied = "requirement.not-satisfied";
    public const string RequirementNotVerifiable = "requirement.not-verifiable";
    public const string RequirementNotAssessed = "requirement.not-assessed";
    public const string RequirementUncovered = "requirement.uncovered";
    public const string RequirementNotReviewed = "requirement.not-reviewed";
    public const string RequirementNotIntegrated = "requirement.not-integrated";

    // Phase 10 (docs/21 D-27 and D-29).
    public const string RequirementMergedUnjudged = "requirement.merged-unjudged-changes";
    public const string RevertSuspected = "remote.revert-suspected";
    public const string RunAlert = "run.alert";
    public const string VerdictChallenged = "verdict.challenged";
    public const string AuditNotTrustworthy = "audit.not-trustworthy";
    public const string AuditNotApproved = "audit.not-approved";
    public const string AuditAlert = "audit.alert";
    public const string UncommittedChanges = "audit.uncommitted-changes";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The bytes the evidence holds: UTF-8 without a byte order mark.</summary>
    public byte[] ToBytes() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToJson());

    public string Sha256() => ContentHash.Sha256Hex(ToBytes());
}

/// <summary>What the report is built from: all of it read already, so building it is a pure function.</summary>
/// <param name="Milestone">The milestone as it is before the close (its earlier reports are the ones it keeps).</param>
/// <param name="Requirements">The frozen set the milestone works against now, which the audit judged.</param>
/// <param name="Log">The whole verified log, read after the audit ended.</param>
/// <param name="Scope">The runs (linked and their follow-ups) and debates of the milestone.</param>
/// <param name="Audit">What the audit gave back.</param>
/// <param name="Board">The board of the milestone (<c>IBoardProjector</c>), or null when it could not be read.</param>
public sealed record MilestoneReportInput(
    MilestoneView Milestone,
    FrozenRequirementSet Requirements,
    IReadOnlyList<DecisionEntry> Log,
    AuditMilestoneScope Scope,
    AuditReport Audit,
    MilestoneBoard? Board);

/// <summary>Builds the <see cref="MilestoneReport"/> from what the verified log, the board and the audit say. No clock, no randomness, no IO.</summary>
public static class MilestoneReportBuilder
{
    private static readonly string[] NotIncludedSections =
    [
        "Consumo con su desglose y estimación frente a lo real (D-34 y D-20): los da la observabilidad de la Fase 9, no este informe.",
        "Despliegue (D-50): Agent Studio no despliega nada y ninguna plataforma lo ha informado; sin ese dato el estado no aparece.",
    ];

    /// <summary>The text of the log that goes into the report, without the paths of this machine (the report is shared: it feeds the client documentation).</summary>
    private static string Clean(string text) => LocalPaths.Remove(text, "[ruta local]").Text;

    public static MilestoneReport Build(MilestoneReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var milestone = input.Milestone;
        var items = input.Log.OrderBy(e => e.Sequence).Select(AuditedRun.Parse).ToList();
        var head = input.Log.Count == 0 ? DecisionLog.GenesisHash : input.Log[^1].Hash;
        var linked = milestone.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var challenged = items.Select(i => i.Payload).OfType<VerdictChallengedPayload>().Select(c => c.Verdict).ToHashSet(StringComparer.Ordinal);

        var runs = input.Scope.Runs.Order(StringComparer.Ordinal).Select(run => RunOf(run, linked.Contains(run), items, challenged)).ToList();
        var debates = input.Scope.Debates.Order(StringComparer.Ordinal).Select(id => DebateOf(id, items)).ToList();
        var audit = AuditOf(input.Audit, items);
        var remote = RemoteOf(items, input.Scope.Runs);
        var requirements = RequirementsOf(input, remote);
        var units = input.Scope.Runs.Concat(input.Scope.Debates).Append(input.Audit.Audit.Value).ToHashSet(StringComparer.Ordinal);
        var pending = PendingProblems.From(input.Log).Where(p => units.Contains(p.Source)).Select(p => new MilestoneReportPending(p.Code, p.Source, Clean(p.Message))).ToList();

        var sets = milestone.RequirementSetHistory.Select((hash, index) => new MilestoneReportRequirementSet(index + 1, hash, index == milestone.RequirementSetHistory.Count - 1)).ToList();
        var earlier = milestone.Reports.OrderBy(r => r.Revision).Select(r => new MilestoneReportEarlier(r.Revision, r.Audit, r.Report.Value, r.ReportSha256, r.IntegratedCommit, r.At)).ToList();
        var anchor = new MilestoneReportAnchor(input.Log.Count, head, input.Audit.HeadCommit, input.Audit.UncommittedChanges, input.Board?.LogEntries ?? 0, input.Board?.LogHeadHash ?? string.Empty);

        return new MilestoneReport(
            MilestoneReport.CurrentFormat,
            milestone.Reports.Count + 1,
            new MilestoneReportHeader(milestone.Header.Id.Value, milestone.Header.Name, milestone.Header.Objective, milestone.Header.CreatedAt),
            anchor,
            sets,
            requirements,
            runs,
            debates,
            audit,
            Deviations(input, requirements, runs, audit, remote),
            pending,
            earlier,

            // The deployment line says «ninguna plataforma lo ha informado»: it goes only when one did (D-29).
            remote is not null && remote.Observed.Any(p => p.Deployed) ? [NotIncludedSections[0]] : NotIncludedSections,
            remote?.Section);
    }

    /// <summary>The pull requests the log observed for the runs of the milestone (final observation of each, whatever the order of the entries) and the remote entries left out.</summary>
    private sealed record RemoteView(IReadOnlyList<ObservedPullRequest> Observed, IReadOnlyList<long> Ignored)
    {
        public MilestoneReportRemote Section => new(
            [.. Observed.Select(p => new MilestoneReportPullRequest(
                p.Latest.PullRequest.TraceId, p.Run, p.Latest.State.ToString(), p.Latest.MergeCommit, p.Latest.MergeMode?.ToString(), p.ModeDeduced, p.Latest.RevertCommits.Count > 0, p.Deployed))],
            Ignored.Count > 0 ? Ignored : null);
    }

    /// <summary>
    /// A pull request of a run of the milestone as the shared reading gives it. <paramref name="ModeDeduced"/> comes from the succession of commits, where the rule lives. Whether it
    /// integrated a requirement is not decided here: the card of the board says it (<see cref="BoardCard.IntegratedByPullRequest"/>), so the report cannot have a rule of its own.
    /// </summary>
    private sealed record ObservedPullRequest(string Run, RemoteSnapshot Latest, bool ModeDeduced)
    {
        /// <summary>What the platform says: a successful deployment of a commit the merge left.</summary>
        public bool Deployed => IntegrationByPullRequest.IsDeployed(Latest);
    }

    /// <summary>
    /// The remote entries of the log (docs/21 D-28) for the runs of the milestone, from the one reading of them (<see cref="RemoteLogReading"/>, in Application): of the observations of a
    /// pull request only the final one counts, so a repeated or out-of-order entry changes nothing. The section exists only when there is something to say: a valid pull request of one of
    /// the runs of the milestone, or an entry that does not validate and that names one of them (<c>IgnoredEntries</c>, by sequence). What cannot be attributed to a run of the milestone, an
    /// invalid entry that names no run or another one, is not in its report. Null: the report is the one of Phase 9.
    /// </summary>
    private static RemoteView? RemoteOf(List<RunItem> items, IReadOnlyList<string> runsOfMilestone)
    {
        var reading = RemoteLogReading.Read(items.Select(i => (i.Entry, i.Payload)));
        var scope = runsOfMilestone.ToHashSet(StringComparer.Ordinal);
        var succession = reading.Succession(_ => null);
        var observed = reading.PullRequests
            .Where(p => p.Run is not null && scope.Contains(p.Run))
            .Select(p => new ObservedPullRequest(p.Run!, p.Latest, succession.OfPullRequest(p.PullRequest)?.ModeIsDeduced ?? true))
            .ToList();
        var ignored = reading.Ignored.Where(i => i.Run is not null && scope.Contains(i.Run)).Select(i => i.Sequence).ToList();

        return observed.Count == 0 && ignored.Count == 0 ? null : new RemoteView(observed, ignored);
    }

    private static List<MilestoneReportRequirement> RequirementsOf(MilestoneReportInput input, RemoteView? remote)
    {
        var cards = (input.Board?.Requirements ?? [])
            .Where(c => c.Kind == BoardCardKind.Requirement)
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var columns = cards.ToDictionary(c => c.Key, c => c.Value.Column, StringComparer.Ordinal);
        var assessments = input.Audit.Verdict.Assessments
            .GroupBy(a => a.Requirement.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Status, StringComparer.Ordinal);
        var result = new List<MilestoneReportRequirement>();
        foreach (var requirement in input.Requirements.Requirements.OrderBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            BoardColumn? column = columns.TryGetValue(requirement.Id.Value, out var found) ? found : null;
            AssessmentStatus? status = assessments.TryGetValue(requirement.Id.Value, out var assessed) ? assessed : null;

            // Phase 10: the card says it (the board applies the rule of D-29), and «desplegado» exists only when the platform said so for the run that decided the card.
            var card = cards.GetValueOrDefault(requirement.Id.Value);
            var mergedUnjudged = string.Equals(card?.Reason, BoardCardReasons.MergedWithUnjudgedChanges, StringComparison.Ordinal);
            bool? deployed = column is BoardColumn.Integrated && card?.IntegratedByPullRequest is { } carrier && remote is not null
                && remote.Observed.Any(p => string.Equals(p.Latest.PullRequest.TraceId, carrier, StringComparison.Ordinal) && p.Deployed)
                ? true
                : null;
            result.Add(new MilestoneReportRequirement(
                requirement.Id.Value,
                requirement.Priority.ToString(),
                requirement.Statement,
                column?.ToString(),
                status?.ToString() ?? "NotAssessed",
                Implemented: input.Audit.IsTrustworthy && status == AssessmentStatus.Satisfied,
                Reviewed: column is BoardColumn.Done or BoardColumn.Integrated,
                Integrated: column is BoardColumn.Integrated,
                MergedWithUnjudgedChanges: mergedUnjudged,
                Deployed: deployed));
        }

        return result;
    }

    private static MilestoneReportRun RunOf(string run, bool linkedDirectly, List<RunItem> items, HashSet<string> challenged)
    {
        var started = items.Where(i => i.Payload is RunStartedPayload).FirstOrDefault(i => i.Payload!.RunId == run);
        var startedPayload = started?.Payload as RunStartedPayload;
        var finished = items.Select(i => i.Payload).OfType<RunFinishedPayload>().LastOrDefault(p => p.RunId == run);
        var deliveries = items.Select(i => i.Payload).OfType<IntegrationPerformedPayload>()
            .Where(p => p.RunId == run && p.IsDelivery && !string.IsNullOrEmpty(p.ResultCommit))
            .Select(p => p.ResultCommit!)
            .ToList();
        var verdicts = items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().Where(v => v.RunId == run)
            .Select(v => new MilestoneReportVerdict(v.VerdictId, v.Task?.Value, v.Decision.ToString(), v.Valid, challenged.Contains(v.VerdictId)))
            .ToList();
        var alerts = items.Select(i => i.Payload).OfType<AuditAlertPayload>().Where(a => a.RunId == run).Select(a => new MilestoneReportAlert(a.Code, Clean(a.Message))).ToList();
        var agents = (startedPayload?.Roster ?? [])
            .OrderBy(r => r.Agent.Value, StringComparer.Ordinal)
            .Select(r => new MilestoneReportAgent(r.Agent.Value, r.Role.ToString(), r.Provider.Value, r.Vendor.Value))
            .ToList();
        return new MilestoneReportRun(
            run,
            linkedDirectly,
            started?.Entry.RecordedAt,
            startedPayload?.Follows,
            startedPayload?.BaseCommit,
            finished?.Phase.ToString(),
            finished?.IntegrationCommit,
            deliveries,
            agents,
            verdicts,
            alerts);
    }

    private static MilestoneReportDebate DebateOf(string id, List<RunItem> items)
    {
        var opened = items.Select(i => i.Payload).OfType<DebateOpenedPayload>().FirstOrDefault(d => d.RunId == id);
        var closed = items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(c => c.RunId == id);
        return new MilestoneReportDebate(id, opened?.Mode.ToString(), closed?.Outcome.ToString() ?? "Open", closed?.Rule);
    }

    private static MilestoneReportAudit AuditOf(AuditReport audit, List<RunItem> items)
    {
        var judge = audit.Verdict.Judge;
        var alerts = items.Select(i => i.Payload).OfType<AuditAlertPayload>().Where(a => a.RunId == audit.Audit.Value).Select(a => new MilestoneReportAlert(a.Code, Clean(a.Message))).ToList();
        var verification = audit.Verification.OrderBy(e => e.Id.Value, StringComparer.Ordinal).Select(e => new MilestoneReportEvidence(
            e.Id.Value,
            e.Kind.ToString(),
            Clean(e.Title),
            e.SubjectCommit,
            e.TestRun?.Passed,
            e.TestRun?.Failed,
            e.TestRun?.Skipped,
            e.TestRun?.ExitCode)).ToList();
        return new MilestoneReportAudit(
            audit.Audit.Value,
            audit.VerdictId,
            audit.Verdict.Decision.ToString(),
            audit.Disposition.ToString(),
            judge.Agent.Value,
            judge.Model.Vendor.Value,
            judge.Model.Model,
            audit.IsTrustworthy,
            audit.Corrected,
            audit.DecisionLogIntact,
            audit.ValidationIssues.Count,
            verification,
            alerts);
    }

    /// <summary>The deviations (D-49), in a fixed order of kinds and, within each, in the order of the requirements, runs and alerts.</summary>
    private static List<MilestoneReportDeviation> Deviations(MilestoneReportInput input, List<MilestoneReportRequirement> requirements, List<MilestoneReportRun> runs, MilestoneReportAudit audit, RemoteView? remote)
    {
        var deviations = new List<MilestoneReportDeviation>();
        if (!audit.Trustworthy)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditNotTrustworthy, audit.Id, "El veredicto de la auditoría no es de fiar (no lo dio un juez, no pasó la validación o el registro no estaba íntegro): ningún requisito cuenta como implementado."));
        }

        if (!string.Equals(audit.Disposition, nameof(ReviewDisposition.Approved), StringComparison.Ordinal))
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditNotApproved, audit.Id, $"La auditoría no terminó aprobada (disposición {audit.Disposition}): una persona decide qué hacer con lo que señala."));
        }

        if (input.Audit.UncommittedChanges)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.UncommittedChanges, audit.Id, "Había cambios sin confirmar en el repositorio: no entran en lo auditado."));
        }

        foreach (var requirement in requirements)
        {
            var code = requirement.Assessment switch
            {
                nameof(AssessmentStatus.NotSatisfied) => MilestoneReport.RequirementNotSatisfied,
                nameof(AssessmentStatus.NotVerifiable) => MilestoneReport.RequirementNotVerifiable,
                nameof(AssessmentStatus.Satisfied) => null,
                _ => MilestoneReport.RequirementNotAssessed,
            };
            if (code is not null)
            {
                deviations.Add(new MilestoneReportDeviation(code, requirement.Id, $"El requisito {requirement.Id} no consta como satisfecho en la auditoría ({requirement.Assessment})."));
            }

            if (string.Equals(requirement.Column, nameof(BoardColumn.Backlog), StringComparison.Ordinal))
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementUncovered, requirement.Id, $"Ninguna tarea de las ejecuciones del hito cubre el requisito {requirement.Id}."));
            }
            else if (!requirement.Reviewed)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementNotReviewed, requirement.Id, $"Ningún veredicto aprobado y válido da por satisfecho el requisito {requirement.Id} (columna {requirement.Column ?? "desconocida"})."));
            }
            else if (requirement.MergedWithUnjudgedChanges)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementMergedUnjudged, requirement.Id, $"El requisito {requirement.Id} está revisado, pero su pull request se fusionó con cambios que el juez no vio: no cuenta como integrado."));
            }
            else if (!requirement.Integrated)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementNotIntegrated, requirement.Id, $"El requisito {requirement.Id} está revisado pero su ejecución no se entregó a tu rama."));
            }
        }

        foreach (var run in runs)
        {
            foreach (var alert in run.Alerts)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RunAlert, run.Id, $"Alerta {alert.Code} en la ejecución {run.Id}: {alert.Message}"));
            }

            foreach (var verdict in run.Verdicts.Where(v => v.Challenged))
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.VerdictChallenged, verdict.Id, $"El veredicto {verdict.Id} de la ejecución {run.Id} está impugnado."));
            }
        }

        foreach (var alert in audit.Alerts)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditAlert, audit.Id, $"Alerta {alert.Code} en la auditoría: {alert.Message}"));
        }

        // A revert read from the message of a commit of the base branch (D-27) is a deduction: it is said as one, and a person decides whether to reopen the milestone.
        foreach (var pullRequest in (remote?.Observed ?? []).Where(p => p.Latest.RevertCommits.Count > 0))
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.RevertSuspected, pullRequest.Latest.PullRequest.TraceId, $"Posible reversión de {pullRequest.Latest.PullRequest.TraceId} (deducida del mensaje de un commit de la rama base): una persona decide si reabre el hito."));
        }

        return deviations;
    }
}
