using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Which projects have an activity with agents in this process (docs/15 D-05, decision 12): a planning, a start, a resume or
/// a run of the orchestrator, a debate or an audit. It is the <see cref="IProjectActivity"/> of the process, and so the
/// <see cref="IRunActivity"/> the memory waits on (docs/14 D-19 and D-22): while anything holds a project, the memory does
/// not write to its log and is not exported.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The orchestrator's own activities, its plannings and its runs, share a project between them, counted; a debate or
/// an audit shares it with nothing, not even another of its kind. <see cref="TryEnter"/> returns null, and enters nothing,
/// when the project is held otherwise: the caller refuses before writing anything.</item>
/// <item>Each lease releases its hold once, at once and only once, however often it is disposed; a run of the orchestrator
/// disposes its lease before its end can be seen (<c>OrchestrationRun</c> calls back before it completes its events and
/// its result), so what reacts to that end finds the project free.</item>
/// <item>It depends on nothing, so the container builds it before both the memory and the orchestrator without a cycle:
/// the orchestrator needs the context packer, the packer the project memory, and the memory this.</item>
/// </list>
/// </remarks>
public sealed class OrchestrationActivity : IProjectActivity
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Holders> _projects = new(StringComparer.Ordinal);

    public bool IsBusy(string projectId) => Current(projectId) is not null;

    public ProjectActivityKind? Current(string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        lock (_gate)
        {
            return _projects.TryGetValue(projectId, out var holders) ? holders.Current : null;
        }
    }

    public IDisposable? TryEnter(string projectId, ProjectActivityKind kind)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown project activity.");
        }

        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var holders))
            {
                holders = new Holders();
                _projects.Add(projectId, holders);
            }

            if (!holders.TryAdd(kind))
            {
                return null;
            }
        }

        return new Lease(this, projectId, kind);
    }

    private void Leave(string projectId, ProjectActivityKind kind)
    {
        lock (_gate)
        {
            if (_projects.TryGetValue(projectId, out var holders))
            {
                holders.Remove(kind);
                if (holders.Current is null)
                {
                    _projects.Remove(projectId);
                }
            }
        }
    }

    /// <summary>What holds one project: counted runs and plannings of the orchestrator, or a debate or an audit alone.</summary>
    private sealed class Holders
    {
        private int _runs;
        private int _plannings;
        private ProjectActivityKind? _exclusive;

        /// <summary>What holds the project now; a debate or an audit is alone, and a run wins over a planning that shares it.</summary>
        public ProjectActivityKind? Current =>
            _exclusive ?? (_runs > 0 ? ProjectActivityKind.Run : _plannings > 0 ? ProjectActivityKind.Planning : null);

        public bool TryAdd(ProjectActivityKind kind)
        {
            switch (kind)
            {
                case ProjectActivityKind.Run when _exclusive is null:
                    _runs++;
                    return true;
                case ProjectActivityKind.Planning when _exclusive is null:
                    _plannings++;
                    return true;
                case ProjectActivityKind.Debate or ProjectActivityKind.Audit when Current is null:
                    _exclusive = kind;
                    return true;
                default:
                    return false;
            }
        }

        public void Remove(ProjectActivityKind kind)
        {
            switch (kind)
            {
                case ProjectActivityKind.Run:
                    _runs = Math.Max(0, _runs - 1);
                    break;
                case ProjectActivityKind.Planning:
                    _plannings = Math.Max(0, _plannings - 1);
                    break;
                default:
                    if (_exclusive == kind)
                    {
                        _exclusive = null;
                    }

                    break;
            }
        }
    }

    /// <summary>One hold of a project, released once however often it is disposed.</summary>
    private sealed class Lease(OrchestrationActivity owner, string projectId, ProjectActivityKind kind) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(projectId, kind);
            }
        }
    }
}

/// <summary>
/// Plans and runs orchestrations (docs/13 section 3.3). The orchestrator agent only plans (<see cref="Planner"/>); the
/// rest is the system's: a single-threaded loop mutates the graph, workers run agents, tools, integrations, reviews and
/// approvals in parallel, and every state change is an entry of the decision log, so a run is resumed by replaying it.
/// </summary>
/// <remarks>
/// It reports what goes on to the <see cref="IProjectActivity"/> of the process (<see cref="OrchestrationActivity"/>, also
/// the <see cref="IRunActivity"/> the memory waits on; docs/14 D-19 and D-22, docs/15 D-05): a project is held from the
/// moment a planning, a start or a resume is asked for (before any of their entries is written) until the planning ends,
/// the start or the resume is refused, or the run ends; a run that rests, waiting for a person or for quota, still holds
/// it. While a debate or an audit holds the project, the orchestrator does not plan, start or resume: it refuses before
/// writing anything (one activity with agents per project). The orchestrator is not the activity itself: it needs the
/// context packer, which needs the memory, which needs the activity.
/// <para>
/// Phase 12 (docs/17 D-06, F12-9): with a <see cref="IProjectHistoryTool"/>, the planner may look up what is trusted of
/// the previous days with <c>search_project_history</c>, served by the local MCP server over a snapshot; only its read-only
/// launch gets it, never an executor, the review or an auditor.
/// </para>
/// </remarks>
public sealed class Orchestrator : IOrchestrator, IRunInspector
{
    /// <summary>The action of the approval of a plan, recorded when its run starts (the memory derivation reads it).</summary>
    public const string PlanApprovalAction = "plan.approve";

    /// <summary>The action of the approval a «human approval» task of the plan asks for.</summary>
    public const string HumanApprovalAction = "orchestration.human-approval";

    /// <summary>The action of the approval a review that needs a person asks for (the review escalation of a task).</summary>
    public const string ReviewEscalationAction = "orchestration.review-escalation";

    private readonly EnginePorts _ports;
    private readonly Planner _planner;

    public Orchestrator(
        IDecisionRecorder decisions,
        IEvidenceStore evidence,
        IPlanStore plans,
        IRunRegistry runs,
        IRequirementStore requirements,
        IAgentRoster roster,
        IProjectAgentProviders providers,
        IWorktreeManager worktrees,
        IVerificationRunner verification,
        IIntegrator integrator,
        IIndependentReview review,
        IApprovalGate approvals,
        ISkillProvisioner skills,
        OrchestrationPaths paths,
        TimeProvider time,
        IWriteConfinementProbe? writeConfinement = null,
        IContextPacker? contextPacker = null,
        IProjectActivity? activity = null,
        IEnumerable<IProviderProbe>? probes = null,
        IEnumerable<IProviderQuotaReader>? quotaReaders = null,
        IPowerEvents? power = null,
        IProjectHistoryTool? history = null,
        IProjectBrief? brief = null,
        ISecretRedactor? redactor = null,
        Application.Usage.IUsageEstimator? estimator = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(integrator);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        _ports = new EnginePorts(decisions, evidence, plans, runs, requirements, roster, providers, worktrees, verification, integrator, review, approvals, skills, paths, time, writeConfinement, contextPacker)
        {
            Probes = [.. probes ?? []],
            QuotaReaders = [.. quotaReaders ?? []],
            Power = power,
            History = history,
            Brief = brief,
            Redactor = redactor,
            Estimator = estimator,
        };
        _planner = new Planner(_ports);
        Activity = activity ?? new OrchestrationActivity();
    }

    /// <summary>
    /// The activity this orchestrator reports its plannings, starts, resumes and runs to, shared with the debates and the
    /// audits of the process.
    /// </summary>
    public IProjectActivity Activity { get; }

    public async Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        using (Enter(request.Project.ProjectId, ProjectActivityKind.Planning))
        {
            return await _planner.PlanAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a run on a plan the user approved (nothing runs, and no quota is spent, without that approval). The stored
    /// roster must be exactly the one the plan was validated against and approved with, the plan is validated again, the
    /// requirements, plan, request and run header are stored, and <c>RunStarted</c> anchors the run in the log before the
    /// first task is dispatched.
    /// </summary>
    public async Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Plan);

        // The waits for quota of a new run must make sense, or every wait would end at once (ADR-0034): refused before
        // anything is written. A stored header is never refused for them (RunOptions.Resolved…).
        request.Options.EnsureValid();

        // Held before anything is written (refused while a debate or an audit holds the project); once the run is tracked,
        // the run itself keeps the project held.
        using var busy = Enter(request.Project.ProjectId, ProjectActivityKind.Run);
        if (request.PlanApproval is not { Approved: true })
        {
            throw new InvalidOperationException("The plan was not approved: nothing runs without the user's approval.");
        }

        var project = request.Project;
        var log = await LoadVerifiedAsync(project.ProjectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);

        // The run header freezes the roster the store holds now, and RunStarted anchors it: it must be the one the user
        // validated and approved the plan with, or an agent edited in between (given Bash, say) would run as approved.
        var roster = await _ports.Roster.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        EnsureValidatedRoster(request.ValidatedRoster, roster);
        var steps = request.Verification.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var violations = PlanValidator.Validate(request.Plan, new PlanContext(request.Requirements, roster, steps, steps, new PlanLimits(), TestRunStepsOf(request.Verification)));
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The plan is not valid: " + string.Join(" ", violations.Select(v => $"[{v.Code}] {v.Message}")));
        }

        // A follow-up builds on the last integration of the run it follows; one stopped because its integration evidence
        // was rewritten behind the store (ADR-0010) is not built on.
        if (request.Follows is { } followed
            && RunLog.Of(log.Entries, followed.Value).Any(e => e.Payload is AuditAlertPayload { Code: OrchestrationRun.EvidenceTamperedAlert }))
        {
            throw new InvalidOperationException(
                $"Run {followed} stopped with the alert '{OrchestrationRun.EvidenceTamperedAlert}': its integration evidence was rewritten after it was captured, so no follow-up run builds on it. Start a new run from your branch instead.");
        }

        var taken = RunLog.RunIds(log.Entries);
        var run = request.Run ?? RunIds.New(_ports.Time, id => taken.Contains(id.Value));
        var entries = RunLog.Of(log.Entries, run.Value);
        if (entries.Any(e => e.Entry.Kind == Domain.Decisions.DecisionKind.RunStarted))
        {
            throw new InvalidOperationException($"Run {run} has already started; resume it instead.");
        }

        var recorder = new RunRecorder(_ports.Decisions, _ports.Evidence, project.ProjectId, run);

        // The questions the orchestrator asked before it planned, and their answers (V43): every executor gets them as data. They
        // are in the run in which it planned: this one, or the one the plan came from when it was edited (or its proposal had
        // started a run already) and this run got an id of its own. They are read from the log before anything of the run is
        // written, and only as the log anchored them.
        var plannedIn = PlannedElsewhere(request, run);
        var ledger = new RunLedger();
        await Questions.QuestionRecovery.LoadOrchestratorAsync(recorder, entries, log.Entries, plannedIn?.Value, ledger.Questions, cancellationToken).ConfigureAwait(false);
        var repository = await _ports.Worktrees.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false);

        // What every step of the run checks the user's repository against: it is captured before anything runs. An ignored
        // folder the capture cannot verify blocks only when a step of this run can write there at medium integrity.
        var claudeWritesConfined = await _ports.ClaudeWritesConfinedAsync(cancellationToken).ConfigureAwait(false);
        var guard = new UserRepositoryGuard(_ports.Worktrees, project.RootPath, UserRepositoryGuard.CanWriteAtMediumIntegrity(request.Plan, roster, request.Verification, claudeWritesConfined), recorder);
        var writers = MediumIntegrityWriters.Find(request.Plan, roster, request.Verification, claudeWritesConfined);
        var warning = VerificationStepText.PlanWarning(writers);
        EnsureApprovedWarning(request.ApprovedWarning, warning, request.PlanApproval, writers);

        await guard.EstablishBaselineAsync(cancellationToken).ConfigureAwait(false);
        var notices = new List<string>();

        // Only the user's own changes: what Agent Studio keeps in .agentstudio/ (the journal, the memory export) does not
        // count (RepositoryState.HasUncommittedChanges), the same answer the notes of a debate and of an audit get.
        if (repository.HasUncommittedChanges)
        {
            notices.Add("Tienes cambios sin confirmar: no entran en la ejecución, que parte del último commit.");
        }

        // A plan that may run but whose work no integration reviewed by the judge merges can never be delivered.
        if (PlanWarningText.Notice(PlanValidator.Warnings(request.Plan)) is { } undeliverable)
        {
            notices.Add(undeliverable);
        }

        var baseCommit = await BaseCommitAsync(request, run, log.Entries, entries, repository, recorder, cancellationToken).ConfigureAwait(false);
        var createdAt = _ports.Time.GetUtcNow();
        if (await _ports.Requirements.FindAsync(project.ProjectId, request.Requirements.ContentHash, cancellationToken).ConfigureAwait(false) is null)
        {
            await _ports.Requirements.SaveAsync(project.ProjectId, request.Requirements, cancellationToken).ConfigureAwait(false);
        }

        var planHash = await _ports.Plans.SaveAsync(project.ProjectId, request.Plan, cancellationToken).ConfigureAwait(false);
        var original = await recorder.CaptureAsync(
            Domain.Evidence.EvidenceKind.Document,
            Domain.Evidence.EvidenceOrigin.ProvidedByHuman,
            "Petición original",
            request.PlanApproval.DecidedBy,
            request.OriginalRequest,
            "text/plain",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        var header = new RunRecord(run, project.ProjectId, createdAt, planHash, request.Requirements.ContentHash, original.Id, baseCommit, repository.HeadBranch, request.Verification, roster, request.Options, request.Follows);
        await _ports.Runs.CreateAsync(header, cancellationToken).ConfigureAwait(false);

        await RecordStartAsync(request, recorder, entries, header, writers, warning, plannedIn, cancellationToken).ConfigureAwait(false);

        var context = new RunContext(project, run, request.Plan, request.Requirements, request.Verification, roster, request.Options, baseCommit, createdAt);
        var orchestration = new OrchestrationRun(context, _ports, recorder, request.Plan.BuildGraph(createdAt), ledger, guard, Forget);
        Track(orchestration);
        orchestration.Start(notices);
        return orchestration;
    }

    /// <summary>
    /// Resumes a run after a restart (the user asks: it spends quota). The verified log is replayed, interrupted attempts
    /// are closed and their tasks reopened, and the loop continues; completed work is never repeated. A run whose writers
    /// at medium integrity are not the ones it was approved with is refused before any of that
    /// (<see cref="RunNeedsApprovalException"/>, G-02).
    /// </summary>
    public async Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var busy = Enter(project.ProjectId, ProjectActivityKind.Run);
        using var reservation = Reserve(project.ProjectId, run);
        var log = await LoadVerifiedAsync(project.ProjectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        return await GoOnAsync(project, run, log, RunResumeCause.UserRequested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a run by the consent of its header (<see cref="ResumeScheduler"/>, docs/16 D-39), or says why not without
    /// writing anything. It decides here, under the hold of the project and the reservation of the run, on the log it has
    /// just read and verified (<see cref="ResumeScheduler.Decide"/>): what a pass of the scheduler read before may be stale, a
    /// person may have paused the run or chosen to leave since. A run that stopped in this session of the app on a system
    /// error is never resumed this way: the error is here, and a person decides. <c>RunResumed</c> records the cause before
    /// anything is launched.
    /// </summary>
    internal async Task<ConsentResume> ResumeByConsentAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var busy = Enter(project.ProjectId, ProjectActivityKind.Run);
        using var reservation = Reserve(project.ProjectId, run);
        var log = await LoadVerifiedAsync(project.ProjectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        var (cause, reason) = _stoppedByError.TryGetValue(Key(project.ProjectId, run), out var error)
            ? (null, $"Se detuvo en esta sesión de la aplicación por un error del sistema ({error}): la reanudas tú.")
            : await _ports.Runs.FindAsync(project.ProjectId, run, cancellationToken).ConfigureAwait(false) is { } record
                ? ResumeScheduler.Decide(record, RunLog.Of(log.Entries, run.Value))
                : ((RunResumeCause?)null, "No está registrada.");
        return cause is { } why
            ? new ConsentResume(await GoOnAsync(project, run, log, why, cancellationToken).ConfigureAwait(false), why, reason)
            : new ConsentResume(null, null, reason);
    }

    /// <summary>
    /// The resume of <paramref name="run"/> for <paramref name="cause"/>, on the verified <paramref name="log"/>: the resumer
    /// checks the header (a consent cause needs <c>AutoResumeConsent</c>) and builds the guard of the resumed run (its
    /// baseline is the user's repository as it is now) before its recovery touches any worktree; the run is tracked and goes.
    /// </summary>
    private async Task<IOrchestrationRun> GoOnAsync(ProjectContext project, RunId run, DecisionLogState log, RunResumeCause cause, CancellationToken cancellationToken)
    {
        await EnsurePresenceOfApprovedWritersAsync(project, run, log, cancellationToken).ConfigureAwait(false);
        var resumed = await new RunResumer(_ports).ResumeAsync(project, run, log.Entries, cause, cancellationToken).ConfigureAwait(false);
        var orchestration = new OrchestrationRun(resumed.Context, _ports, resumed.Recorder, resumed.Graph, resumed.Ledger, resumed.Guard, Forget);
        Track(orchestration);
        orchestration.Start([RunResumer.Notice(cause)]);
        return orchestration;
    }

    /// <summary>Holds <paramref name="run"/> for one resume of this process until the returned reservation is disposed; refused while it runs here or another resume holds it.</summary>
    private Reservation Reserve(string projectId, RunId run)
    {
        var key = Key(projectId, run);
        lock (_reserved)
        {
            if (_live.ContainsKey(key) || !_reserved.Add(key))
            {
                throw new InvalidOperationException($"Run {run} is already running in this process.");
            }
        }

        return new Reservation(this, key);
    }

    /// <summary>The runs of a project. The phase comes from the log: the last <c>RunFinished</c>, the live phase of a run of this process, or Interrupted.</summary>
    public async Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await LoadVerifiedAsync(project.ProjectId, LogLoad.Incremental, cancellationToken).ConfigureAwait(false);
        var records = await _ports.Runs.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        var summaries = new List<RunSummary>();
        foreach (var record in records.OrderBy(r => r.CreatedAt))
        {
            var entries = RunLog.Of(log.Entries, record.Id.Value);
            RunPhase phase;
            string summary;
            if (_live.TryGetValue(Key(project.ProjectId, record.Id), out var live))
            {
                var snapshot = live.Snapshot();
                phase = snapshot.Phase;
                summary = "En curso.";
            }
            else if (LastFinished(entries) is { } finished)
            {
                phase = finished.Phase;
                summary = finished.Summary;
            }
            else
            {
                phase = RunPhase.Interrupted;
                summary = "Interrumpida: no terminó y no hay ningún proceso detrás. Se puede reanudar.";
            }

            summaries.Add(new RunSummary(record.Id, record.CreatedAt, phase, record.PlanHash, summary, record.Follows));
        }

        return summaries;
    }

    /// <summary>
    /// A run of the project that does not run in this process, read back from the verified log to look at it
    /// (<see cref="RunInspector"/>): nothing is launched or written. A run of this process that is running is refused: its
    /// events have one reader, the monitor that follows it.
    /// </summary>
    public async Task<IOrchestrationRun> InspectAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_live.ContainsKey(Key(project.ProjectId, run)))
        {
            throw new InvalidOperationException($"Run {run} is running in this process: the monitor that follows it shows it.");
        }

        var log = await LoadVerifiedAsync(project.ProjectId, LogLoad.Incremental, cancellationToken).ConfigureAwait(false);
        return await new RunInspector(_ports).InspectAsync(project, run, log.Entries, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses to start, before anything is written, unless the stored roster is exactly the one the plan was validated
    /// against and approved with (<see cref="RunRequest.ValidatedRoster"/>), compared by <see cref="RosterHash"/>: the
    /// order a store lists it in does not matter, and every member of every agent does. A request that does not carry it
    /// is refused too.
    /// </summary>
    private static void EnsureValidatedRoster(IReadOnlyList<AgentDefinition>? validated, IReadOnlyList<AgentDefinition> stored)
    {
        if (validated is null)
        {
            throw new InvalidOperationException(
                $"The run request does not say which roster the plan was validated against ({nameof(RunRequest.ValidatedRoster)}): nothing runs with agents the user did not approve.");
        }

        var approved = RosterHash.Of(validated);
        var current = RosterHash.Of(stored);
        if (!string.Equals(approved, current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The agent roster of the project is not the one the plan was validated against and approved ({string.Join("; ", RosterChanges(validated, stored))}): it hashes to {current} in the store, and {approved} was approved. Nothing runs: review the agents and approve the plan again.");
        }
    }

    /// <summary>One line for every agent the store holds otherwise than the validated roster: added, removed or changed, in id order.</summary>
    private static IEnumerable<string> RosterChanges(IReadOnlyList<AgentDefinition> validated, IReadOnlyList<AgentDefinition> stored)
    {
        static Dictionary<string, string> ById(IReadOnlyList<AgentDefinition> roster) =>
            roster.GroupBy(a => a.Id.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => RosterHash.Of([.. g]), StringComparer.Ordinal);

        var before = ById(validated);
        var after = ById(stored);
        foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!after.TryGetValue(id, out var now))
            {
                yield return $"agent '{id}' was removed";
            }
            else if (!before.TryGetValue(id, out var then))
            {
                yield return $"agent '{id}' was added";
            }
            else if (!string.Equals(then, now, StringComparison.Ordinal))
            {
                yield return $"agent '{id}' changed";
            }
        }
    }

    /// <summary>
    /// Refuses to start, before anything is written, unless the warning about writers at medium integrity the engine reads
    /// when the run starts (<paramref name="warning"/>, null when there is none) is the one shown next to «Aprobar plan»
    /// when the user approved the plan (<see cref="RunRequest.ApprovedWarning"/>, ADR-0018, A-04), empty when the review
    /// showed none. A request that does not carry it was approved without being shown one, so it starts only when there
    /// is none to show (fail closed). A plan with writers is on the list of the fixed barrier of Windows Hello (docs/17 D-37,
    /// ADR-0053): its approval must have confirmed the person, with Windows Hello or the verified lock of Windows
    /// (<c>PresencePolicy.IsSatisfiedBy</c>, judged with the floor no setting lowers), or nothing starts. A backup of the
    /// check the desktop already makes: the engine never sees Windows, only the method the approval records.
    /// </summary>
    private static void EnsureApprovedWarning(string? approved, string? warning, ApprovalDecision approval, IReadOnlyList<MediumIntegrityWriter> writers)
    {
        if (writers.Count > 0 && !PlanApprovalConfirmsPresence(approval.Verification))
        {
            throw new InvalidOperationException(
                $"The plan has writers at medium integrity, so approving it always asks for Windows Hello (docs/17 D-37), and this approval was given with '{approval.Verification}': nothing runs until it is approved with Windows Hello or the verified lock of Windows.");
        }

        if (approved is null)
        {
            if (!string.IsNullOrEmpty(warning))
            {
                throw new InvalidOperationException(
                    $"The run request does not say which warning about writers at medium integrity (aviso) was shown when the plan was approved ({nameof(RunRequest.ApprovedWarning)}), and this plan has such writers: nothing runs with writers the user was not shown. Now: «{warning}».");
            }

            return;
        }

        if (!string.Equals(approved, warning ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The warning about writers at medium integrity (aviso) is not the one shown when the plan was approved (the profile or the installed Claude Code changed, say): nothing runs until it is approved again. "
                + $"Approved: «{(approved.Length == 0 ? "none" : approved)}». Now: «{warning ?? "none"}».");
        }
    }

    /// <summary>
    /// A run whose plan has writers at medium integrity is resumed only when its approval confirmed the person (docs/17 D-37,
    /// ADR-0053): a run approved with the dialog alone, before the fixed barrier of Windows Hello, is not. The alert
    /// <c>run.not-resumable</c> says why (once per refusal, <see cref="NotResumableAlert.AlreadyRecorded"/>), nothing else is
    /// written, and <see cref="RunNeedsApprovalException"/> asks for the plan to be approved again, now with Windows Hello,
    /// which starts a follow-up run. A run without writers, or one whose <c>RunStarted</c> anchored none, is left to the
    /// resumer and its own checks.
    /// </summary>
    private async Task EnsurePresenceOfApprovedWritersAsync(ProjectContext project, RunId run, DecisionLogState log, CancellationToken cancellationToken)
    {
        var entries = RunLog.Of(log.Entries, run.Value);
        if (entries.Select(e => e.Payload).OfType<RunStartedPayload>().FirstOrDefault() is not { } started
            || !started.AnchorsApprovedWriters()
            || started.ApprovedWriters is not { Count: > 0 })
        {
            return;
        }

        var approval = entries.Select(e => e.Payload).OfType<ApprovalAnsweredPayload>()
            .LastOrDefault(a => a.Approved && string.Equals(a.Action, PlanApprovalAction, StringComparison.Ordinal));
        if (approval is not null && PlanApprovalConfirmsPresence(approval.ResolvedVerification()))
        {
            return;
        }

        var problem = $"Its plan has writers at medium integrity, and its approval did not confirm the person with Windows Hello or the verified lock of Windows (it was given with '{approval?.ResolvedVerification().ToString() ?? "no recorded approval"}'). "
            + "Since the fixed barrier of Windows Hello (docs/17 D-37) such a plan is not run on that approval: approve its plan again, with Windows Hello, which starts a follow-up run.";
        // Once per refusal: a run that resumes by consent is tried at every start of the app, and the same refusal, with nothing
        // approved since, is not written again.
        if (!NotResumableAlert.AlreadyRecorded(entries, problem))
        {
            var recorder = new RunRecorder(_ports.Decisions, _ports.Evidence, project.ProjectId, run);
            await recorder.RecordAsync(
                Domain.Decisions.DecisionKind.AuditAlertRaised,
                $"La ejecución {run} no se puede reanudar",
                new AuditAlertPayload(NotResumableAlert.Code, [], problem) { RunId = recorder.RunValue },
                cancellationToken).ConfigureAwait(false);
        }

        throw new RunNeedsApprovalException($"Run {run} cannot be resumed: {problem}", run, started.PlanHash, warningAnchored: true, started.ApprovedWarning, started.ApprovedWarning);
    }

    /// <summary>
    /// Whether an approval of a plan with writers recorded with <paramref name="verification"/> confirmed the person, by the one
    /// rule of the domain (<c>PresencePolicy.IsSatisfiedBy</c>) and the proof the method claims (<see cref="PresenceProofs.Claimed"/>),
    /// with the floor nothing lowers: the setting off and Hello not there.
    /// </summary>
    private static bool PlanApprovalConfirmsPresence(ApprovalVerification verification) =>
        Domain.Security.PresencePolicy.IsSatisfiedBy(
            Domain.Security.PresenceAction.ApprovePlanWithHighRiskCapability,
            importantApprovalsSetting: false,
            helloAvailable: false,
            PresenceProofs.Claimed(verification));

    /// <summary>The steps of a profile whose evidence is a test run: what a policy of system-captured tests needs a task to run.</summary>
    internal static IReadOnlySet<string> TestRunStepsOf(VerificationProfile profile) =>
        profile.Steps.Where(s => s.Kind == Domain.Evidence.EvidenceKind.TestRun).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OrchestrationRun> _live = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> _holds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    /// <summary>The runs of this process whose loop stopped on a system error, and why: none is resumed by consent here.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stoppedByError = new(StringComparer.Ordinal);

    private static string Key(string projectId, RunId run) => projectId + "/" + run.Value;

    /// <summary>
    /// Holds the project for <paramref name="kind"/> until the returned lease is disposed, or refuses, before anything is
    /// written, while another activity holds it (a debate or an audit; docs/15 D-05).
    /// </summary>
    private IDisposable Enter(string projectId, ProjectActivityKind kind) =>
        Activity.TryEnter(projectId, kind)
        ?? throw new InvalidOperationException(
            $"Project '{projectId}' is held by {Activity.Current(projectId)?.ToString() ?? "another activity"} in this process: one activity with agents per project (docs/15 D-05), so the orchestrator does not plan, start or resume until it ends.");

    /// <summary>
    /// A run of this process: tracked, and its project held until it ends. The start or the resume that tracks it already
    /// holds the project for a run, and runs share it, so its own hold always enters.
    /// </summary>
    private void Track(OrchestrationRun run)
    {
        var projectId = run.Context.Project.ProjectId;
        var key = Key(projectId, run.Id);
        _holds[key] = Enter(projectId, ProjectActivityKind.Run);
        _live[key] = run;
        _stoppedByError.TryRemove(key, out _); // going again: what stops it now is what counts
    }

    /// <summary>
    /// The run ended: it is called back before its end can be seen, so the project is released first. One whose loop stopped
    /// on a system error is remembered as such for the rest of this process.
    /// </summary>
    private void Forget(OrchestrationRun run)
    {
        var key = Key(run.Context.Project.ProjectId, run.Id);
        if (run.SystemError is { } error)
        {
            _stoppedByError[key] = error;
        }

        _live.TryRemove(key, out _);
        if (_holds.TryRemove(key, out var hold))
        {
            hold.Dispose();
        }
    }

    /// <summary>One reservation of a run for its resume, released once however often it is disposed.</summary>
    private sealed class Reservation(Orchestrator owner, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                lock (owner._reserved)
                {
                    owner._reserved.Remove(key);
                }
            }
        }
    }

    /// <summary>The <c>RunFinished</c> of a run. It is only recorded for a final phase, and a finished run is never resumed.</summary>
    private static RunFinishedPayload? LastFinished(IReadOnlyList<(Domain.Decisions.DecisionEntry Entry, DecisionPayload Payload)> entries) =>
        entries.Select(e => e.Payload).OfType<RunFinishedPayload>().LastOrDefault();

    /// <summary>
    /// The verified log, or <see cref="DecisionLogIntegrityException"/>. <see cref="LogLoad.Full"/> for the start of a run and
    /// its resumes, which launch agents on it (a critical moment, ADR-0010, addendum of 2026-11-04); incremental for listing
    /// and inspecting.
    /// </summary>
    private async Task<DecisionLogState> LoadVerifiedAsync(string projectId, LogLoad load, CancellationToken cancellationToken)
    {
        var log = await _ports.Decisions.LoadAsync(projectId, load, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(projectId, log.Verification);
        }

        return log;
    }

    /// <summary>
    /// The commit the run builds on: for a follow-up run, the last integration of the run it follows (also when the
    /// orchestrator planned it, reading its <c>_base</c> worktree on HEAD); else the base the plan was made on (its
    /// <c>_base</c> worktree); or else HEAD (the user's uncommitted changes never enter).
    /// </summary>
    private async Task<string> BaseCommitAsync(
        RunRequest request,
        RunId run,
        IReadOnlyList<Domain.Decisions.DecisionEntry> all,
        IReadOnlyList<(Domain.Decisions.DecisionEntry Entry, DecisionPayload Payload)> entries,
        RepositoryState repository,
        RunRecorder recorder,
        CancellationToken cancellationToken)
    {
        if (request.Follows is { } follows
            && RunLog.Of(all, follows.Value).Select(e => e.Payload).OfType<IntegrationPerformedPayload>().LastOrDefault(i => !i.IsDelivery && i.ResultCommit is not null) is { } integration)
        {
            return integration.ResultCommit!;
        }

        if (entries.Select(e => e.Payload).OfType<WorktreeCreatedPayload>().FirstOrDefault(w => w.Name == "_base" && w.Task is null) is { } planned)
        {
            return planned.BaseCommit;
        }

        var lease = await _ports.Worktrees.EnsureAsync(new WorktreeRequest(request.Project, run, "_base", repository.HeadCommit), cancellationToken).ConfigureAwait(false);
        await recorder.RecordAsync(
            Domain.Decisions.DecisionKind.WorktreeCreated,
            $"Worktree _base en {lease.BaseCommit}",
            new WorktreeCreatedPayload(null, lease.Name, lease.Path, lease.RefName, lease.BaseCommit) { RunId = run.Value },
            cancellationToken).ConfigureAwait(false);
        return lease.BaseCommit;
    }

    /// <summary>
    /// The run in which the orchestrator planned (V43) when it is not <paramref name="run"/> itself: the one the request names
    /// as <see cref="RunRequest.PlannedIn"/>, null when there is none or it is the run's own (its entries are the run's).
    /// </summary>
    private static RunId? PlannedElsewhere(RunRequest request, RunId run) =>
        request.PlannedIn is { } planned && planned != run ? planned : null;

    /// <summary>
    /// Records the approval of the plan (unless the gate already did) and <c>RunStarted</c>, version 3: besides the hashes
    /// of the plan, the requirements and the profile, it anchors the hash of the whole stored header (<see cref="RunHeaderHash"/>),
    /// so a resume never runs with a roster, options or base other than the ones that started, and the writers at medium
    /// integrity the plan was approved with, with their warning (G-02), so a resume never runs with other writers either.
    /// </summary>
    /// <param name="writers">The writers at medium integrity of the plan, read when the run starts (<see cref="MediumIntegrityWriters.Find"/>).</param>
    /// <param name="warning">The warning that names them, the one the user approved, or null when there is none (<see cref="VerificationStepText.PlanWarning(IReadOnlyList{MediumIntegrityWriter})"/>).</param>
    /// <param name="plannedIn">The run in which the orchestrator planned when it is not this one (V43), anchored in <c>RunStarted</c> so a resume reads the answers from there; null otherwise.</param>
    private static async Task RecordStartAsync(
        RunRequest request,
        RunRecorder recorder,
        IReadOnlyList<(Domain.Decisions.DecisionEntry Entry, DecisionPayload Payload)> entries,
        RunRecord header,
        IReadOnlyList<MediumIntegrityWriter> writers,
        string? warning,
        RunId? plannedIn,
        CancellationToken cancellationToken)
    {
        var run = recorder.RunValue;
        var decision = request.PlanApproval;
        var planHash = header.PlanHash;

        // The approval the user gave in the plan review; recorded here unless the approval gate already recorded it. What
        // was approved includes the writers at medium integrity of the plan (ADR-0018, decision 3): the request says so,
        // with the warning the review showed next to «Aprobar plan», read from the same writers as the check of the
        // user's repository of this run.
        if (!entries.Any(e => e.Entry.Kind == Domain.Decisions.DecisionKind.ApprovalGranted))
        {
            var description = $"Ejecutar el plan {planHash[..12]} ({request.Plan.Tasks.Count} tareas)" + (warning is null ? string.Empty : ". " + warning);
            await recorder.RecordAsync(
                Domain.Decisions.DecisionKind.ApprovalRequested,
                "Aprobación del plan",
                new ApprovalRequestedPayload(PlanApprovalAction, description, RiskLevel.Critical, null, []) { RunId = run },
                cancellationToken).ConfigureAwait(false);
            await recorder.RecordAsync(
                new Domain.Decisions.Actor(Domain.Decisions.ActorKind.User, decision.DecidedBy),
                Domain.Decisions.DecisionKind.ApprovalGranted,
                "Plan aprobado",
                ApprovalAnswers.Payload(PlanApprovalAction, true, decision.DecidedBy, decision.Comment, null, decision.Verification, run),
                cancellationToken).ConfigureAwait(false);
        }

        await recorder.RecordAsync(
            Domain.Decisions.DecisionKind.RunStarted,
            $"Arranca la ejecución {run}",
            new RunStartedPayload(
                planHash,
                header.RequirementSetHash,
                header.BaseCommit,
                header.BaseRef,
                header.Verification.ContentHash,
                [.. header.Roster.Select(a => new RosterEntry(a.Id, a.Role, a.Model.Provider, a.Model.Vendor))],
                header.Follows?.Value,
                RunHeaderHash.Of(header),
                warning,
                MediumIntegrityWriters.Anchor(writers),
                plannedIn?.Value)
            {
                RunId = run,
                V = RunStartedPayload.WritersVersion,
            },
            cancellationToken).ConfigureAwait(false);

        var assigner = request.Plan.Source == PlanSource.OrchestratorAgent && request.Plan.Author is { } author
            ? new Domain.Decisions.Actor(Domain.Decisions.ActorKind.Orchestrator, author.Value)
            : new Domain.Decisions.Actor(Domain.Decisions.ActorKind.User, decision.DecidedBy);
        foreach (var task in request.Plan.Tasks)
        {
            await recorder.RecordAsync(
                Domain.Decisions.DecisionKind.TaskCreated,
                $"Tarea {task.Id}: {task.Title}",
                new TaskCreatedPayload(task.Id, task.Title, task.Kind, task.Join, task.MaxRetries) { RunId = run },
                cancellationToken).ConfigureAwait(false);
            if (task.Agent is { } agent)
            {
                await recorder.RecordAsync(
                    assigner,
                    Domain.Decisions.DecisionKind.TaskAssigned,
                    $"{task.Id} asignada a {agent}",
                    new TaskAssignedPayload(task.Id, agent) { RunId = run },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
