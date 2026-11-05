using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// The independent review of an orchestration run (docs/13, decision 6 and flow F3-8, steps 10 to 12). The engine only
/// passes identifiers; this use case builds the dossier, runs the judge and keeps every verdict.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><c>ReviewRequested</c> is recorded first, so the head the judge must echo already includes the request.</item>
/// <item>The log is read back from the store and verified. If it is not intact nothing can be anchored to it (the
/// recorder refuses to extend it), so the judge is not launched and the review needs a human.</item>
/// <item>The dossier comes from the stores (<see cref="IJudgeDossierPackageBuilder"/>), and the judge is the agent of
/// the run's frozen roster. That roster is a row of the store: the judge is launched only when the stored header hashes
/// to what <c>RunStarted</c> anchored (<see cref="RunHeaderHash"/>, version 2), so its instructions, tools and model are
/// the approved ones. A header that does not, or a run whose <c>RunStarted</c> anchors no hash (version 1), records an
/// alert (<c>RunHeaderMismatch</c>) and needs a human. A judge whose vendor produced part of the work is not launched
/// either: its verdict could never be valid (<c>JudgeNotIndependent</c>), and an alert is recorded.</item>
/// <item>The verdict is validated with <see cref="VerdictValidator"/> as it is. With violations the judge gets one
/// correction, never more.</item>
/// <item>Every verdict is stored append-only exactly as the judge gave it, with its issues; a correction points to the
/// verdict it supersedes. Each one is announced with <c>VerdictIssued</c>, which carries the head of the log the judge
/// examined.</item>
/// <item>The disposition: a valid Approved, ChangesRequested or Rejected is that; a valid EscalateToHuman or Unresolved,
/// a verdict still invalid after the correction, or a correction that could not be obtained, needs a human.</item>
/// <item>When the judge gives no verdict at all: a usage limit of its provider is <see cref="ReviewDisposition.RateLimited"/>,
/// with the time the limit resets if the provider said (<see cref="ReviewOutcome.RetryAfter"/>), so the caller waits and
/// asks again; an answer that cannot be read as a verdict needs a human. Nothing is stored as a verdict for either.</item>
/// </list>
/// Each launch of the judge is recorded around the run (<c>AgentRunStarted</c> before it, <c>AgentRunFinished</c>
/// after it), with the prompt and the raw answer captured as evidence: the payloads only carry their ids and hashes.
/// Both are version 2 (docs/14 section 5.2): the start says it is the judge's and never carries memory (the judge gets
/// none, ADR-0022); the end records the usage with origins and breakdown and what the judge's events said
/// (<see cref="JudgeRunRecorder"/>, with <see cref="JudgeRunScope.ForRunReview"/>). The verdicts are kept by
/// <see cref="VerdictKeeper"/>, in the run, the review task and <see cref="AuditScope.CurrentPlan"/>; both classes moved out
/// of this one in Phase 5 (docs/15 D-23) so the debate and the audit share them. When the review cannot ask the judge, <see cref="ReviewOutcome.Verdict"/> is an escalation
/// issued by the system itself (agent and vendor <c>agent-studio</c>), never stored as a judge's verdict, and
/// <see cref="ReviewOutcome.VerdictId"/> is empty.
/// </remarks>
/// <param name="providers">
/// The composed providers of the project, the ones the judge runs on: they say whether its CLI reports the context of each
/// step, and so whether its compactions are counted or not exposed. Without them, they are not exposed.
/// </param>
public sealed class IndependentReview(
    IDecisionRecorder decisions,
    IJudgeDossierPackageBuilder dossiers,
    IRunRegistry runs,
    IAgentJudgeFactory judges,
    IVerdictStore verdicts,
    IEvidenceStore evidence,
    TimeProvider timeProvider,
    IProjectAgentProviders? providers = null) : IIndependentReview
{
    /// <summary>The actor and producer the system records itself as.</summary>
    public const string SystemId = "agent-studio";

    /// <summary>The code of the alert recorded when the judge is not launched because the stored run header is not the one <c>RunStarted</c> anchored.</summary>
    public const string HeaderMismatchAlert = "RunHeaderMismatch";

    private static readonly Actor SystemActor = new(ActorKind.System, SystemId);

    /// <summary>Who signs the escalations the system issues when it cannot ask the judge.</summary>
    public static readonly JudgeIdentity SystemIdentity = new(new AgentId(SystemId), new ModelBinding(new ProviderId(SystemId), new VendorId(SystemId)));

    /// <exception cref="InvalidOperationException">The run is not registered, or the judge is not in its roster.</exception>
    /// <exception cref="JudgeRunException">The judge's first run ended without an answer: it failed or hit the limits of its own budget.</exception>
    public async Task<ReviewOutcome> ReviewRunAsync(ReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        var projectId = request.Project.ProjectId;
        var run = request.Run.Value;

        // 1 and 2: ask first, then make sure the log the verdict will be anchored to is intact.
        var (broken, log) = await RequestReviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (broken is not null)
        {
            var reason = $"No se ha pedido veredicto al juez: el registro de decisiones no está íntegro (entrada {broken.FirstInvalidSequence}: {broken.Problem}).";
            return new ReviewOutcome(string.Empty, Escalation(reason, string.Empty, string.Empty), [], Corrected: false, DecisionLogIntact: false, ReviewDisposition.NeedsHuman);
        }

        // 3: the dossier, from the stores only; the judge, from the run's own roster once the stored header is the one
        // RunStarted anchored in the verified log (N-05): rewritten behind the store's triggers, it could give the judge
        // tools, lenient instructions or another model.
        var package = await dossiers.BuildPackageAsync(new DossierRequest(projectId, AuditScope.CurrentPlan, run), cancellationToken).ConfigureAwait(false);
        var dossier = package.Dossier;
        var header = await runs.FindAsync(projectId, request.Run, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{run}' of project '{projectId}' is not registered.");
        if (UnverifiedHeader(request, header, RunLog.RunStarted(RunLog.EntriesOf(log, run))) is { } unverified)
        {
            await decisions.RecordAsync(projectId, SystemActor, DecisionKind.AuditAlertRaised, "Cabecera de la ejecución no verificada",
                new AuditAlertPayload(HeaderMismatchAlert, [request.ReviewTask], unverified) { RunId = run }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ReviewOutcome(string.Empty, Escalation(unverified, dossier.Requirements.ContentHash, dossier.DecisionLogHeadHash), [], Corrected: false, DecisionLogIntact: true, ReviewDisposition.NeedsHuman);
        }

        var agent = header.Roster.FirstOrDefault(a => a.Id == request.Judge)
            ?? throw new InvalidOperationException($"Judge '{request.Judge}' is not in the roster of run '{run}'.");
        var limits = new RateLimitWatch(progress);
        var reportsContext = providers?.For(request.Project.RootPath).FirstOrDefault(p => p.Provider == agent.Model.Provider)?.Capabilities.ReportsContextOccupancy ?? false;
        var launches = new JudgeRunRecorder(decisions, evidence, JudgeRunScope.ForRunReview(projectId, run, request.ReviewTask), agent, timeProvider, reportsContext, limits);
        var keeper = new VerdictKeeper(verdicts, decisions, projectId, run, request.ReviewTask, AuditScope.CurrentPlan);
        var judge = judges.Create(
            request.Project,
            new JudgeLaunch(agent, request.WorkspacePath, package.Files, launches, launches, $"Revisión independiente de la ejecución {run}", request.ReviewTask.Value));

        var vendor = judge.Identity.Model.Vendor;
        var producers = string.Join(", ", dossier.ExcludedVendors.Select(v => v.Value).Order(StringComparer.Ordinal));
        if (dossier.ExcludedVendors.Contains(vendor))
        {
            var reason = $"No se lanza el juez '{agent.Id}': corre en '{vendor}', que también produjo el trabajo revisado ({producers}).";
            await decisions.RecordAsync(projectId, SystemActor, DecisionKind.AuditAlertRaised, "Juez no independiente",
                new AuditAlertPayload("JudgeNotIndependent", [request.ReviewTask], reason) { RunId = run }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ReviewOutcome(string.Empty, Escalation(reason, dossier.Requirements.ContentHash, dossier.DecisionLogHeadHash), [], Corrected: false, DecisionLogIntact: true, ReviewDisposition.NeedsHuman);
        }

        var note = $"Juez en '{vendor}'; vendors que produjeron el trabajo: {producers} (ADR-0014).";

        // 4 to 7: the verdict as given, validated, stored and announced; one correction at most.
        Verdict verdict;
        try
        {
            verdict = await judge.EvaluateAsync(dossier, cancellationToken).ConfigureAwait(false);
        }
        catch (JudgeRunException ex) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended as cancelled because the review was: that is a cancellation, not an answer.
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (JudgeRunException ex) when (ex.Status == AgentRunStatus.RateLimited)
        {
            var reason = $"El juez '{agent.Id}' alcanzó el límite de uso de '{vendor}' antes de dar un veredicto; la revisión se repite cuando termine la espera.";
            return new ReviewOutcome(string.Empty, Escalation(reason, dossier.Requirements.ContentHash, dossier.DecisionLogHeadHash), [], Corrected: false, DecisionLogIntact: true, ReviewDisposition.RateLimited, limits.ResetsAt);
        }
        catch (Exception ex) when (ex is JudgeOutputException or JudgeRunException { Status: AgentRunStatus.Succeeded })
        {
            // The judge ran and answered, but not with a verdict: a person decides (its raw answer is in the evidence).
            var reason = $"El juez '{agent.Id}' respondió sin un veredicto que se pueda leer: {ex.Message}";
            return new ReviewOutcome(string.Empty, Escalation(reason, dossier.Requirements.ContentHash, dossier.DecisionLogHeadHash), [], Corrected: false, DecisionLogIntact: true, ReviewDisposition.NeedsHuman);
        }

        var violations = VerdictValidator.Validate(verdict, dossier);
        var verdictId = await keeper.KeepAsync(agent.Id, dossier, verdict, violations, supersedes: null, note).ConfigureAwait(false);
        if (violations.Count == 0)
        {
            return new ReviewOutcome(verdictId, verdict, violations, Corrected: false, DecisionLogIntact: true, Disposition(verdict));
        }

        Verdict corrected;
        try
        {
            corrected = await judge.ReviseAsync(dossier, verdict, violations, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JudgeRunException or JudgeOutputException)
        {
            // No correction to show: the first verdict stays, with its issues, for a person to decide.
            return new ReviewOutcome(verdictId, verdict, violations, Corrected: true, DecisionLogIntact: true, ReviewDisposition.NeedsHuman);
        }

        var remaining = VerdictValidator.Validate(corrected, dossier);
        var correctedId = await keeper.KeepAsync(agent.Id, dossier, corrected, remaining, supersedes: verdictId, note).ConfigureAwait(false);
        return new ReviewOutcome(correctedId, corrected, remaining, Corrected: true, DecisionLogIntact: true, remaining.Count == 0 ? Disposition(corrected) : ReviewDisposition.NeedsHuman);
    }

    private static ReviewDisposition Disposition(Verdict verdict) => verdict.Decision switch
    {
        VerdictDecision.Approved => ReviewDisposition.Approved,
        VerdictDecision.ChangesRequested => ReviewDisposition.ChangesRequested,
        VerdictDecision.Rejected => ReviewDisposition.Rejected,
        _ => ReviewDisposition.NeedsHuman,
    };

    /// <summary>
    /// Records <c>ReviewRequested</c> and verifies the stored log. Returns why it is broken (and no entries), or null and
    /// the entries of the log as read back and verified.
    /// </summary>
    private async Task<(DecisionLogVerification? Broken, IReadOnlyList<DecisionEntry> Log)> RequestReviewAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.Project.ProjectId;
        try
        {
            await decisions.RecordAsync(
                projectId,
                SystemActor,
                DecisionKind.ReviewRequested,
                $"Revisión independiente de la ejecución {request.Run} pedida al juez '{request.Judge}'",
                new ReviewRequestedPayload(request.ReviewTask, request.Judge) { RunId = request.Run.Value },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DecisionLogIntegrityException ex)
        {
            return (ex.Verification, []);
        }

        // The recorder appends to the log it holds in memory: only reading it back, whole and from its first entry, shows
        // a change made in the store (a critical moment, ADR-0010, addendum of 2026-11-04).
        var state = await decisions.LoadAsync(projectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        return state.Verification.IsValid ? (null, state.Entries) : (state.Verification, []);
    }

    /// <summary>
    /// Why the judge must not be launched on the stored header of the run, in Spanish (it is shown to the user), or null
    /// when the header hashes to what <c>RunStarted</c> anchored in the verified log (<see cref="RunHeaderHash"/>). A
    /// <c>RunStarted</c> of version 1 anchors no hash, so the judge's definition cannot be checked against the approval.
    /// </summary>
    private static string? UnverifiedHeader(ReviewRequest request, RunRecord header, RunStartedPayload? started)
    {
        if (started?.HeaderHash is not { } anchored)
        {
            var version = started is null ? "sin RunStarted en el registro" : $"con RunStarted versión {started.V}, que no ancla el hash de su cabecera";
            return $"No se lanza el juez '{request.Judge}': la ejecución {request.Run} empezó {version}, así que la definición del juez (instrucciones, herramientas y modelo) no se puede comprobar contra lo que se aprobó.";
        }

        var stored = RunHeaderHash.Of(header);
        return string.Equals(stored, anchored, StringComparison.Ordinal)
            ? null
            : $"No se lanza el juez '{request.Judge}': la cabecera guardada de la ejecución {request.Run} da el hash {stored}, pero RunStarted ancló {anchored}. La plantilla de agentes (el juez incluido: instrucciones, herramientas o modelo), las opciones o la base cambiaron después de empezar.";
    }

    private Verdict Escalation(string reason, string requirementSetHash, string decisionLogHeadHash) => new(
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
        SystemIdentity,
        requirementSetHash,
        decisionLogHeadHash,
        timeProvider.GetUtcNow());

    /// <summary>Passes the judge's events on, and remembers the last time its provider said the usage limit resets.</summary>
    private sealed class RateLimitWatch(IProgress<AgentEvent>? inner) : IProgress<AgentEvent>
    {
        private readonly Lock _gate = new();
        private DateTimeOffset? _resetsAt;

        public DateTimeOffset? ResetsAt
        {
            get
            {
                lock (_gate)
                {
                    return _resetsAt;
                }
            }
        }

        public void Report(AgentEvent value)
        {
            if (value is RateLimitSignaled { ResetsAt: { } at })
            {
                lock (_gate)
                {
                    _resetsAt = at;
                }
            }

            inner?.Report(value);
        }
    }
}
