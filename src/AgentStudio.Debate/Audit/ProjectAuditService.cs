using System.Globalization;
using System.Text;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// «Auditar proyecto completo» (docs/15 D-28 to D-31, ADR-0030): the commit HEAD of the project, judged against a frozen set
/// of requirements by a judge of a vendor that produced none of its code, on the isolated verification of that commit and an
/// index dossier (R8). The judge reads the code in a read-only copy of the commit.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Who produced the code: the vendors of every run of the log (<see cref="RunLog.ProducerVendors"/>, which never counts
/// the judges) and the authors the user declares. Rule P is never waived: a judge on one of them is not offered, and when no
/// judge is left the audit is not launched.</item>
/// <item>Nothing is asked, run or written when the log does not verify (a verdict cannot be anchored to a log that admits no
/// entries) or when nobody may judge (<see cref="ProjectAuditRecord"/> and <c>ProjectAuditStarted</c> name the judge, so an
/// audit without one has no header to anchor). The report then gives what the system checks without writing: the
/// integrity of the log and the pending problems, read from the log as it is.</item>
/// <item>Otherwise, in this order: the proposal is prepared again and must hash to the one shown; the project's activity is
/// entered (one activity with agents per project, D-05); the approval (<see cref="ApprovalAction"/>) is bound to that hash;
/// the request is captured; the header is stored and <c>ProjectAuditStarted</c> anchors its hash; the steps of the profile
/// run isolated on HEAD (on a copy the git layer takes, ADR-0017; a step without isolation never runs, D-27) and their
/// evidence is registered; the commit is copied for the judge; the dossier is built on the log as it is then and its index
/// is kept as evidence of the audit (a revision of the verdict rebuilds the audit from its log and evidence); the judge
/// answers with one correction at most; every verdict is kept and <c>ProjectAuditFinished</c> closes the audit. A started
/// audit that cannot go on (the copy fails, a judge runs on a producer vendor, it throws or is cancelled) still closes with
/// <c>ProjectAuditFinished</c>, unless the log itself stopped verifying. The copy of the commit is removed at the end.</item>
/// </list>
/// The texts the user reads (problems, the approval, summaries) are in Spanish.
/// </remarks>
public sealed class ProjectAuditService(
    IDecisionRecorder decisions,
    IRequirementStore requirements,
    IAgentRoster roster,
    IWorktreeManager worktrees,
    IVerificationRunner verification,
    IEvidenceStore evidence,
    IVerdictStore verdicts,
    IProjectAuditRegistry audits,
    IAgentJudgeFactory judges,
    IApprovalGate approvals,
    IProjectActivity activity,
    IAuditVerificationProfiles profiles,
    ProjectDossierBuilder dossiers,
    AuditOptions options,
    TimeProvider time,
    IProjectAgentProviders? providers = null) : IProjectAuditService
{
    /// <summary>The action of the approval of an audit (docs/15 D-06).</summary>
    public const string ApprovalAction = "audit.run";

    /// <summary>The title of the evidence that holds the request of an audit: what the person was shown and approved.</summary>
    public const string RequestTitle = "Petición de auditoría";

    /// <summary>
    /// The title of the evidence that holds the index the judge was given (<see cref="ProjectDossierBuilder.Format"/>): a
    /// revision of the audit's verdict rebuilds its material from the log and the evidence of the audit, not from a package.
    /// </summary>
    public const string IndexTitle = "Índice del dossier de la auditoría";

    /// <summary>The code of the alert of a judge the factory gave on a vendor that produced code (as <c>IndependentReview</c> raises it).</summary>
    public const string JudgeNotIndependentAlert = "JudgeNotIndependent";

    /// <summary>The tokens a judge of a whole-project audit may read, «estimado por Agent Studio» (docs/15 §12, decision 4: 150 000 to 500 000).</summary>
    public const long EstimatedJudgeTokens = 500_000;

    private static readonly Actor SystemActor = new(ActorKind.System, IndependentReview.SystemId);

    public async Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request, CancellationToken ct) =>
        (await ProposeAsync(request, ct).ConfigureAwait(false)).Proposal;

    public async Task<AuditReport> AuditAsync(ProjectAuditProposal proposal, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(proposal.Request);
        var project = proposal.Request.Project;
        var projectId = project.ProjectId;
        var log = await decisions.LoadAsync(projectId, ct).ConfigureAwait(false);
        var audit = AuditIds.New(time, log.Entries);
        var pending = PendingProblems.From(log.Entries);
        if (!log.Verification.IsValid)
        {
            return Unjudged(audit, proposal, logIntact: false, pending,
                $"El registro de decisiones no está íntegro ({Broken(log.Verification)}). No se lanza el juez y no se escribe nada: un veredicto no se puede anclar a un registro que no admite entradas. Los problemas pendientes se leen del registro tal como está, sin anclar.");
        }

        if (!proposal.CanJudge)
        {
            var reasons = proposal.Problems.Count > 0 ? proposal.Problems : ["Nadie puede juzgar esta auditoría."];
            return Unjudged(audit, proposal, logIntact: true, pending,
                $"No se lanza la auditoría, y no se ejecuta ni se escribe nada: {string.Join(" ", reasons)}");
        }

        var (fresh, material) = await ProposeAsync(proposal.Request, ct).ConfigureAwait(false);
        if (material is null || !fresh.CanJudge || !string.Equals(fresh.HeaderHash, proposal.HeaderHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "What the audit would do now is not what was shown (HEAD, the requirements, the judge, the producers or the profile changed): prepare it again.");
        }

        using var lease = activity.TryEnter(projectId, ProjectActivityKind.Audit)
            ?? throw new InvalidOperationException($"Project '{projectId}' is busy ({activity.Current(projectId)}): an audit shares the project with no other activity.");
        var decision = await approvals.RequestAsync(
            new ApprovalRequest(ApprovalAction, proposal.ApprovalText, RiskLevel.Critical, null, [], projectId, audit.Value, Binding: proposal.HeaderHash),
            ct).ConfigureAwait(false);
        if (!decision.Approved)
        {
            return Unjudged(audit, proposal, logIntact: true, pending,
                $"La auditoría no se aprobó ({decision.DecidedBy}{(string.IsNullOrWhiteSpace(decision.Comment) ? string.Empty : ": " + decision.Comment)}): no se ha ejecutado nada.");
        }

        if (!string.Equals(decision.Answers, proposal.HeaderHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approval received does not answer the proposal of this audit: nothing was run.");
        }

        return await RunAsync(project, audit, proposal, material, pending, progress, ct).ConfigureAwait(false);
    }

    private async Task<AuditReport> RunAsync(
        ProjectContext project,
        RunId audit,
        ProjectAuditProposal proposal,
        Material material,
        IReadOnlyList<PendingProblem> pending,
        IProgress<AgentEvent>? progress,
        CancellationToken ct)
    {
        var projectId = project.ProjectId;
        var run = audit.Value;
        var judge = material.Judge;
        var head = material.Head;

        // The request is the text the person was shown and approved, written by the system from what they chose: it is
        // captured by the system, so it can never pass for a person's review of a requirement (HumanReview).
        var request = await CaptureAsync(projectId, run, RequestTitle, material.Text, "text/plain", ct).ConfigureAwait(false);
        var header = new ProjectAuditRecord(audit, projectId, time.GetUtcNow(), head, material.Uncommitted, material.Requirements.ContentHash, request.Id, material.Profile, judge, material.Producers, material.Declared);
        if (!string.Equals(DebateHeaderHash.Proposal(header, request.ContentSha256), proposal.HeaderHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The header of the audit does not hash to the proposal that was approved: nothing more is run.");
        }

        await audits.CreateAsync(header, ct).ConfigureAwait(false);
        await RecordAsync(projectId, DecisionKind.ProjectAuditStarted, $"Arranca la auditoría {run} del commit {Short(head)}",
            new ProjectAuditStartedPayload(
                DebateHeaderHash.Of(header),
                AuditScope.WholeProject,
                head,
                material.Uncommitted,
                material.Requirements.ContentHash,
                material.Profile.ContentHash,
                new DebateJudgeEntry(judge.Id, judge.Model.Provider, judge.Model.Vendor, judge.Model.Model),
                material.Producers,
                material.Declared) { RunId = run },
            ct).ConfigureAwait(false);

        var snapshot = SnapshotFolder(projectId, audit);
        try
        {
            var verified = await VerifyAsync(project, audit, head, material.Profile, ct).ConfigureAwait(false);
            var checks = new List<string>();
            if (await CopyCommitAsync(project, head, snapshot, ct).ConfigureAwait(false) is { } copyProblem)
            {
                var reason = $"No se pudo extraer una copia del commit {Short(head)} para el juez ({copyProblem}): no se lanza, porque no podría leer el código que audita. La verificación de HEAD sí se hizo.";
                return await FinishAsync(audit, material, Escalation(reason, material.Requirements.ContentHash, string.Empty), null, [], false, ReviewDisposition.NeedsHuman, verified, pending, []).ConfigureAwait(false);
            }

            // The recorder appends to the log it holds in memory: only reading it back shows a change made in the store.
            var log = await decisions.LoadAsync(projectId, ct).ConfigureAwait(false);
            if (!log.Verification.IsValid)
            {
                var broken = $"El registro de decisiones dejó de verificar durante la auditoría ({Broken(log.Verification)}): no se lanza el juez y no se escribe nada más.";
                return Report(audit, material, Escalation(broken, material.Requirements.ContentHash, string.Empty), null, [], false, logIntact: false, ReviewDisposition.NeedsHuman, verified, pending);
            }

            var (package, index) = await dossiers.BuildAsync(
                new ProjectDossierRequest(projectId, audit, material.Text, material.Requirements, head, log.Entries, material.Producers, material.Declared, material.Skipped, snapshot, pending, checks),
                ct).ConfigureAwait(false);

            // The index the judge reads is kept as evidence of the audit, announced after the dossier was built (so it is not
            // in it): a revision of the verdict rebuilds the audit from its log and evidence (D-38), and finds it there.
            await CaptureAsync(projectId, run, IndexTitle, index, "application/json", ct).ConfigureAwait(false);
            return await JudgeAsync(project, audit, material, package, index, snapshot, verified, pending, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await InterruptedAsync(projectId, audit, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDelete(snapshot);
        }
    }

    private async Task<AuditReport> JudgeAsync(
        ProjectContext project,
        RunId audit,
        Material material,
        JudgeDossierPackage package,
        string index,
        string workingDirectory,
        IReadOnlyList<EvidenceRecord> verified,
        IReadOnlyList<PendingProblem> pending,
        IProgress<AgentEvent>? progress,
        CancellationToken ct)
    {
        var projectId = project.ProjectId;
        var run = audit.Value;
        var agent = material.Judge;
        var dossier = package.Dossier;
        var requirementSetHash = dossier.Requirements.ContentHash;
        var task = JudgeRunScope.JudgeTask(agent.Id);
        var reportsContext = providers?.For(project.RootPath).FirstOrDefault(p => p.Provider == agent.Model.Provider)?.Capabilities.ReportsContextOccupancy ?? false;
        var recorder = new JudgeRunRecorder(decisions, evidence, JudgeRunScope.ForAudit(projectId, run, agent.Id), agent, time, reportsContext, progress);
        var keeper = new VerdictKeeper(verdicts, decisions, projectId, run, task, AuditScope.WholeProject);
        var judge = judges.Create(project, new JudgeLaunch(agent, workingDirectory, package.Files, recorder, recorder, $"Auditoría del proyecto completo {run}", task.Value, new ProjectAuditPrompt(run, index)));

        var vendor = judge.Identity.Model.Vendor;
        var excluded = List(dossier.ExcludedVendors.Select(v => v.Value).Order(StringComparer.Ordinal));
        if (dossier.ExcludedVendors.Contains(vendor))
        {
            var reason = $"No se lanza el juez '{agent.Id}': corre en '{vendor}', que produjo código del proyecto o lo firma ({excluded}).";
            await decisions.RecordAsync(projectId, SystemActor, DecisionKind.AuditAlertRaised, "Juez no independiente",
                new AuditAlertPayload(JudgeNotIndependentAlert, [task], reason) { RunId = run }, cancellationToken: ct).ConfigureAwait(false);
            return await FinishAsync(audit, material, Escalation(reason, requirementSetHash, dossier.DecisionLogHeadHash), null, [], false, ReviewDisposition.NeedsHuman, verified, pending, []).ConfigureAwait(false);
        }

        var note = $"Juez en '{vendor}'; vendors que produjeron el código o lo firman: {excluded} (regla de los productores, ADR-0014).";
        Verdict verdict;
        try
        {
            verdict = await judge.EvaluateAsync(dossier, ct).ConfigureAwait(false);
        }
        catch (JudgeRunException ex) when (ct.IsCancellationRequested)
        {
            // The run ended as cancelled because the audit was: that is a cancellation, not an answer.
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (JudgeRunException ex) when (ex.Status == AgentRunStatus.RateLimited)
        {
            var reason = $"El juez '{agent.Id}' alcanzó el límite de uso de '{vendor}' antes de dar un veredicto: vuelve a auditar cuando termine la espera.";
            return await FinishAsync(audit, material, Escalation(reason, requirementSetHash, dossier.DecisionLogHeadHash), null, [], false, ReviewDisposition.RateLimited, verified, pending, []).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JudgeOutputException or JudgeRunException)
        {
            // Its raw answer, if it gave one, is in the evidence of its launch; a person decides.
            var reason = ex is JudgeRunException { Status: not AgentRunStatus.Succeeded } failed
                ? $"El juez '{agent.Id}' no terminó ({failed.Status}): {ex.Message}"
                : $"El juez '{agent.Id}' respondió sin un veredicto que se pueda leer: {ex.Message}";
            return await FinishAsync(audit, material, Escalation(reason, requirementSetHash, dossier.DecisionLogHeadHash), null, [], false, ReviewDisposition.NeedsHuman, verified, pending, []).ConfigureAwait(false);
        }

        var violations = VerdictValidator.Validate(verdict, dossier);
        var verdictId = await keeper.KeepAsync(agent.Id, dossier, verdict, violations, supersedes: null, note).ConfigureAwait(false);
        if (violations.Count == 0)
        {
            return await FinishAsync(audit, material, verdict, verdictId, violations, false, Disposition(verdict), verified, pending, [verdictId]).ConfigureAwait(false);
        }

        Verdict corrected;
        try
        {
            corrected = await judge.ReviseAsync(dossier, verdict, violations, ct).ConfigureAwait(false);
        }
        catch (JudgeRunException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (Exception ex) when (ex is JudgeRunException or JudgeOutputException)
        {
            // No correction to show: the first verdict stays, with its issues, for a person to decide.
            return await FinishAsync(audit, material, verdict, verdictId, violations, true, ReviewDisposition.NeedsHuman, verified, pending, [verdictId]).ConfigureAwait(false);
        }

        var remaining = VerdictValidator.Validate(corrected, dossier);
        var correctedId = await keeper.KeepAsync(agent.Id, dossier, corrected, remaining, supersedes: verdictId, note).ConfigureAwait(false);
        var disposition = remaining.Count == 0 ? Disposition(corrected) : ReviewDisposition.NeedsHuman;
        return await FinishAsync(audit, material, corrected, correctedId, remaining, true, disposition, verified, pending, [verdictId, correctedId]).ConfigureAwait(false);
    }

    /// <summary>Records <c>ProjectAuditFinished</c> with every verdict kept in this audit and gives the report.</summary>
    private async Task<AuditReport> FinishAsync(
        RunId audit,
        Material material,
        Verdict verdict,
        string? verdictId,
        IReadOnlyList<VerdictViolation> violations,
        bool corrected,
        ReviewDisposition disposition,
        IReadOnlyList<EvidenceRecord> verified,
        IReadOnlyList<PendingProblem> pending,
        IReadOnlyList<string> kept)
    {
        // Once there is a result it is recorded even if the caller is being cancelled: the quota was spent.
        var report = Report(audit, material, verdict, verdictId, violations, corrected, logIntact: true, disposition, verified, pending);
        var summary = verdictId is null
            ? $"Sin veredicto del juez: {verdict.Summary}"
            : $"Veredicto {verdict.Decision} {(violations.Count == 0 ? "válido" : $"inválido ({violations.Count} incidencias)")}; disposición {disposition}.";
        await RecordAsync(material.Request.Project.ProjectId, DecisionKind.ProjectAuditFinished, $"Termina la auditoría {audit}: {disposition}",
            new ProjectAuditFinishedPayload(kept, disposition, report.IsTrustworthy, summary) { RunId = audit.Value },
            CancellationToken.None).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Closes a started audit that threw or was cancelled with <c>ProjectAuditFinished</c> (the verdicts it kept, if any), so
    /// no audit stays open in the log. Nothing is written when it is already closed or the log does not verify, and a failure
    /// here never hides the one that interrupted the audit.
    /// </summary>
    private async Task InterruptedAsync(string projectId, RunId audit, Exception cause)
    {
        try
        {
            var log = await decisions.LoadAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            if (!log.Verification.IsValid)
            {
                return;
            }

            var entries = RunLog.EntriesOf(log.Entries, audit.Value);
            if (entries.Any(e => e.Kind == DecisionKind.ProjectAuditFinished))
            {
                return;
            }

            var kept = entries.Where(e => e.Kind == DecisionKind.VerdictIssued)
                .Select(e => AuditedRun.Parse(e).Payload)
                .OfType<VerdictIssuedPayload>()
                .Select(v => v.VerdictId)
                .ToList();
            var summary = cause is OperationCanceledException
                ? "La auditoría se canceló antes de terminar."
                : $"La auditoría se interrumpió: {cause.Message}";
            await RecordAsync(projectId, DecisionKind.ProjectAuditFinished, $"Termina la auditoría {audit}: interrumpida",
                new ProjectAuditFinishedPayload(kept, ReviewDisposition.NeedsHuman, false, summary) { RunId = audit.Value },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Debug.WriteLine($"Audit {audit}: its interruption could not be recorded: {ex.Message}");
        }
    }

    private AuditReport Report(
        RunId audit,
        Material material,
        Verdict verdict,
        string? verdictId,
        IReadOnlyList<VerdictViolation> violations,
        bool corrected,
        bool logIntact,
        ReviewDisposition disposition,
        IReadOnlyList<EvidenceRecord> verified,
        IReadOnlyList<PendingProblem> pending) =>
        new(audit, AuditScope.WholeProject, material.Head, material.Uncommitted, verdict, verdictId, violations, corrected, logIntact, disposition, verified, pending, time.GetUtcNow());

    /// <summary>The report of an audit that judged nothing and wrote nothing: the system's escalation, never a judge's verdict.</summary>
    private AuditReport Unjudged(RunId audit, ProjectAuditProposal proposal, bool logIntact, IReadOnlyList<PendingProblem> pending, string reason) =>
        new(
            audit,
            AuditScope.WholeProject,
            proposal.HeadCommit,
            proposal.UncommittedChanges,
            Escalation(reason, proposal.Request.RequirementSetHash, string.Empty),
            null,
            [],
            false,
            logIntact,
            ReviewDisposition.NeedsHuman,
            [],
            pending,
            time.GetUtcNow());

    /// <summary>The system's escalation when no judge gave a verdict (as <c>IndependentReview</c> gives it): never stored as a judge's.</summary>
    private Verdict Escalation(string reason, string requirementSetHash, string head) => new(
        VerdictDecision.EscalateToHuman,
        reason,
        [],
        [],
        [],
        [],
        [],
        new Confidence(ConfidenceLevel.Low, reason),
        [],
        RequiresHumanIntervention: true,
        IndependentReview.SystemIdentity,
        requirementSetHash,
        head,
        time.GetUtcNow());

    private static ReviewDisposition Disposition(Verdict verdict) => verdict.Decision switch
    {
        VerdictDecision.Approved => ReviewDisposition.Approved,
        VerdictDecision.ChangesRequested => ReviewDisposition.ChangesRequested,
        VerdictDecision.Rejected => ReviewDisposition.Rejected,
        _ => ReviewDisposition.NeedsHuman,
    };

    private static string Broken(DecisionLogVerification verification) =>
        verification.FirstInvalidSequence is { } sequence ? $"entrada {sequence}: {verification.Problem}" : verification.Problem ?? "no verifica";

    /// <summary>What the audit would do now, and the material it would do it with (null when it cannot judge).</summary>
    private async Task<(ProjectAuditProposal Proposal, Material? Material)> ProposeAsync(ProjectAuditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.DeclaredAuthors);
        var project = request.Project;
        var projectId = project.ProjectId;
        var problems = new List<string>();

        var log = await decisions.LoadAsync(projectId, ct).ConfigureAwait(false);
        var intact = log.Verification.IsValid;
        if (!intact)
        {
            problems.Add($"El registro de decisiones no está íntegro ({Broken(log.Verification)}): sin un registro que verifique no se lanza el juez.");
        }

        var frozen = await requirements.FindAsync(projectId, request.RequirementSetHash, ct).ConfigureAwait(false);
        if (frozen is null)
        {
            problems.Add($"El proyecto no tiene el conjunto de requisitos congelado {request.RequirementSetHash}: sin requisitos no se audita.");
        }

        RepositoryState? repository = null;
        try
        {
            repository = await worktrees.InspectAsync(project.RootPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            problems.Add($"No se puede leer el repositorio del proyecto: {ex.Message}");
        }

        VerificationProfile? profile = null;
        try
        {
            profile = await profiles.LoadAsync(project, ct).ConfigureAwait(false);
            if (profile is null)
            {
                problems.Add("El proyecto no tiene perfil de verificación de confianza: guárdalo antes de auditar.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            problems.Add($"El perfil de verificación del proyecto no se puede usar: {ex.Message}");
        }

        var producers = intact ? ProducersOf(log.Entries) : [];
        var declared = request.DeclaredAuthors.Distinct().OrderBy(v => v.Value, StringComparer.Ordinal).ToList();
        var judge = intact ? await JudgeAsync(request, producers, declared, problems, ct).ConfigureAwait(false) : null;

        var skipped = profile?.Steps.Where(s => s.Isolation == ProcessIsolation.None).Select(s => s.Id).ToList() ?? [];
        var text = RequestText(project, repository, frozen, request.RequirementSetHash, judge, producers, declared, profile, skipped);
        var textSha = ContentHash.Sha256Hex(text);
        Material? material = null;
        string headerHash;
        if (problems.Count == 0 && judge is not null && profile is not null && frozen is not null && repository is not null)
        {
            // What the person approves: the header without what only the start gives it (its id, project, time and the id of
            // the evidence of the request), with the hash of the request text instead (DebateHeaderHash.Proposal).
            var shown = new ProjectAuditRecord(new RunId(AuditIds.Prefix + "proposal"), projectId, DateTimeOffset.UnixEpoch, repository.HeadCommit, repository.HasUncommittedChanges, frozen.ContentHash, new EvidenceId("request"), profile, judge, producers, declared);
            headerHash = DebateHeaderHash.Proposal(shown, textSha);
            material = new Material(request, frozen, repository.HeadCommit, repository.HasUncommittedChanges, profile, judge, producers, declared, skipped, text);
        }
        else
        {
            headerHash = textSha; // nothing of it can be approved: the audit does not run
        }

        var estimate = MetricValue<long>.EstimatedBySystem(EstimatedJudgeTokens, "estimate: the judge of a whole-project audit reads the repository, plus one correction at most");
        var proposal = new ProjectAuditProposal(request, repository?.HeadCommit, repository?.HasUncommittedChanges ?? false, judge, producers, profile, skipped, estimate, problems, headerHash, text);
        return (proposal, material);
    }

    /// <summary>The judge of the template, when it may judge: a judge or an auditor of a vendor that produced none of the code (rule P).</summary>
    private async Task<AgentDefinition?> JudgeAsync(ProjectAuditRequest request, IReadOnlyList<VendorId> producers, IReadOnlyList<VendorId> declared, List<string> problems, CancellationToken ct)
    {
        var template = await roster.ListAsync(request.Project.ProjectId, ct).ConfigureAwait(false);
        var judge = template.FirstOrDefault(a => a.Id == request.Judge);
        if (judge is null)
        {
            problems.Add($"El juez '{request.Judge}' no está en la plantilla de agentes del proyecto.");
            return null;
        }

        if (judge.Role is not (AgentRole.Judge or AgentRole.Auditor))
        {
            problems.Add($"'{judge.Id}' es {judge.Role}: solo un juez o un auditor juzga.");
            return null;
        }

        if (judge.Validate() is { Count: > 0 } broken)
        {
            problems.Add($"La definición del juez '{judge.Id}' no es válida: {string.Join(", ", broken)}.");
            return null;
        }

        var vendor = judge.Model.Vendor;
        if (producers.Contains(vendor) || declared.Contains(vendor))
        {
            problems.Add($"El juez '{judge.Id}' corre en '{vendor}', que produjo código del proyecto (vendors productores según el registro: {List(producers.Select(v => v.Value))}; autores declarados: {List(declared.Select(v => v.Value))}). La regla de los productores no se dispensa: sin un juez de otro vendor la auditoría no se lanza.");
            return null;
        }

        return judge;
    }

    /// <summary>The vendors of everyone who produced work in a run of the log (the judges never count), in ordinal order.</summary>
    private static List<VendorId> ProducersOf(IReadOnlyList<DecisionEntry> log)
    {
        var runs = log.Where(e => e.Kind == DecisionKind.RunStarted)
            .Select(AuditedRun.Parse)
            .Select(i => i.Payload?.RunId)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        return [.. runs.SelectMany(run => RunLog.ProducerVendors(RunLog.EntriesOf(log, run))).Distinct().OrderBy(v => v.Value, StringComparer.Ordinal)];
    }

    /// <summary>What the person is shown and approves, and what is captured as the request of the audit (Spanish).</summary>
    private static string RequestText(
        ProjectContext project,
        RepositoryState? repository,
        FrozenRequirementSet? frozen,
        string requirementSetHash,
        AgentDefinition? judge,
        IReadOnlyList<VendorId> producers,
        IReadOnlyList<VendorId> declared,
        VerificationProfile? profile,
        IReadOnlyList<string> skipped)
    {
        var text = new StringBuilder();
        text.AppendLine("Auditoría del proyecto completo");
        text.AppendLine(CultureInfo.InvariantCulture, $"Proyecto: {project.ProjectId}");
        text.AppendLine(repository is null
            ? "Commit auditado (HEAD): no se pudo leer"
            : $"Commit auditado (HEAD): {repository.HeadCommit}{(repository.HasUncommittedChanges ? " (hay cambios sin confirmar: no entran en la auditoría)" : string.Empty)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Requisitos congelados: {requirementSetHash}{(frozen is null ? string.Empty : $" ({frozen.Requirements.Count} requisitos)")}");
        text.AppendLine(judge is null ? "Juez: ninguno puede juzgar" : $"Juez: {judge.Id} ({judge.Model.Provider}, {judge.Model.Vendor}, modelo {judge.Model.Model ?? "por defecto"}), de solo lectura");
        text.AppendLine(CultureInfo.InvariantCulture, $"Vendors que produjeron código según el registro: {List(producers.Select(v => v.Value))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Autores declarados: {List(declared.Select(v => v.Value))}");
        text.AppendLine(profile is null
            ? "Verificación: sin perfil"
            : $"Verificación aislada de HEAD: {List(profile.Steps.Where(s => !skipped.Contains(s.Id)).Select(s => s.Id))}; sin aislamiento, no se ejecutan: {List(skipped)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Estimación: hasta {EstimatedJudgeTokens} tokens del juez y una corrección como mucho, «estimado por Agent Studio». La verificación no gasta cuota.");
        return text.ToString();
    }

    /// <summary>Extracts the commit into a new folder for the judge; returns why it could not, or null.</summary>
    private async Task<string?> CopyCommitAsync(ProjectContext project, string head, string snapshot, CancellationToken ct)
    {
        try
        {
            TryDelete(snapshot);
            Directory.CreateDirectory(snapshot);
            await worktrees.ArchiveCommitAsync(project.RootPath, head, snapshot, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(snapshot);
            return ex.Message;
        }
    }

    /// <summary>
    /// Runs the steps of the profile that are isolated on HEAD and registers what they captured, bound to that commit. An
    /// isolated step runs on a copy of the commit that the git layer takes from the repository (ADR-0017), never in the
    /// working directory, which only a step without isolation would use, and those never run in an audit (D-27).
    /// </summary>
    private async Task<IReadOnlyList<EvidenceRecord>> VerifyAsync(ProjectContext project, RunId audit, string head, VerificationProfile profile, CancellationToken ct)
    {
        var steps = profile.Steps.Where(s => s.Isolation != ProcessIsolation.None).ToList();
        if (steps.Count == 0)
        {
            return [];
        }

        var results = await verification.RunAsync(project.RootPath, steps, new EvidenceScope(project.ProjectId, audit, null, null, head, project.RootPath), null, ct).ConfigureAwait(false);
        var records = new List<EvidenceRecord>();
        foreach (var result in results)
        {
            await RecordAsync(project.ProjectId, DecisionKind.EvidenceRegistered, $"Evidencia {result.Evidence.Id}: {result.Evidence.Title}",
                EvidenceRegisteredPayload.Anchoring(result.Evidence, audit.Value, null, null), ct).ConfigureAwait(false);
            records.Add(result.Evidence);
        }

        return records;
    }

    /// <summary>A document the system writes for the audit (the request, the index), captured and announced in the audit's unit.</summary>
    private async Task<EvidenceRecord> CaptureAsync(string projectId, string run, string title, string content, string mediaType, CancellationToken ct)
    {
        EvidenceRecord record;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false))
        {
            record = await evidence.CaptureAsync(
                projectId,
                new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, title, IndependentReview.SystemId, RunId: run, MediaType: mediaType),
                stream,
                ct).ConfigureAwait(false);
        }

        await RecordAsync(projectId, DecisionKind.EvidenceRegistered, $"Evidencia {record.Id}: {record.Title}", EvidenceRegisteredPayload.Anchoring(record, run, null, null), ct).ConfigureAwait(false);
        return record;
    }

    private Task<DecisionEntry> RecordAsync(string projectId, DecisionKind kind, string summary, DecisionPayload payload, CancellationToken ct) =>
        decisions.RecordAsync(projectId, SystemActor, kind, summary, payload, cancellationToken: ct);

    private string SnapshotFolder(string projectId, RunId audit) =>
        Path.Combine(Path.GetFullPath(options.SnapshotRoot), Segment(projectId), Segment(audit.Value), "snapshot");

    /// <summary>Removes the copy of the commit; a link in it is removed as a link, never followed.</summary>
    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Audit: the copy '{folder}' could not be removed: {ex.Message}");
        }
    }

    private static string Segment(string text)
    {
        var safe = new string([.. text.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        return safe.Length == 0 ? "_" : safe.Length > 64 ? safe[..64] : safe;
    }

    private static string Short(string commit) => commit.Length > 10 ? commit[..10] : commit;

    private static string List(IEnumerable<string> values)
    {
        var text = string.Join(", ", values);
        return text.Length == 0 ? "ninguno" : text;
    }

    /// <summary>The material of an audit that can judge, as it was when the proposal was made.</summary>
    private sealed record Material(
        ProjectAuditRequest Request,
        FrozenRequirementSet Requirements,
        string Head,
        bool Uncommitted,
        VerificationProfile Profile,
        AgentDefinition Judge,
        IReadOnlyList<VendorId> Producers,
        IReadOnlyList<VendorId> Declared,
        IReadOnlyList<string> Skipped,
        string Text);
}
