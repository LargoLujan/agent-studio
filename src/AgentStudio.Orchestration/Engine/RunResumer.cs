using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Orchestration.Engine;

/// <summary>A run rebuilt from its log, ready to continue, with the guard of the user's repository its steps are checked by.</summary>
internal sealed record ResumedRun(RunContext Context, TaskGraph Graph, RunLedger Ledger, RunRecorder Recorder, UserRepositoryGuard Guard);

/// <summary>
/// Resuming is replaying (docs/13 section 3.7, ADR-0010). The verified log is replayed through the domain
/// (<see cref="RunStateReplayer"/>); the engine's own memory (attempts, sessions, worktrees, results) is rebuilt from the
/// log and from the attempt records. Then recovery and the user's resume are recorded:
/// <list type="number">
/// <item>every attempt the app never saw finish gets <c>AgentRunFinished(Interrupted)</c>, and its task goes to
/// <c>Blocked(Interrupted)</c> without spending a retry (the restart is not the agent's fault);</item>
/// <item>the partial diff of each interrupted attempt is captured as evidence and its worktree goes back to the base of
/// the attempt (the agent's process died with the app, so nothing is running in it);</item>
/// <item><c>Blocked(Interrupted)</c> goes back to <c>Pending</c>. What was completed is never repeated.</item>
/// </list>
/// The run goes on exactly as it was approved, or not at all: its stored header (roster, options, base commit, the plan
/// and requirements it names, the profile) must hash to what <c>RunStarted</c> anchored, and the plan replayed is the one
/// <c>RunStarted</c> names. Any problem in the replay, or a header that does not match, leaves the run not resumable,
/// with an audit alert. So do writers at medium integrity other than the ones it was approved with, read with the
/// Claude Code installed now (G-02): then the alert comes with <see cref="RunNeedsApprovalException"/>, and the plan has
/// to be approved again.
/// </summary>
internal sealed class RunResumer(EnginePorts ports)
{
    /// <summary>What the monitor is told first when a run is resumed for <paramref name="cause"/>, in Spanish.</summary>
    public static string Notice(RunResumeCause cause) => cause switch
    {
        RunResumeCause.ConsentAfterQuotaWait => "Reanudada sola por el consentimiento de su cabecera: esperaba cuota cuando se cerró la aplicación.",
        RunResumeCause.ConsentAfterUnrequestedClose => "Reanudada sola por el consentimiento de su cabecera: la cortó un cierre que no pediste.",
        RunResumeCause.ConsentAfterSessionEnded => "Reanudada sola por el consentimiento de su cabecera: Windows cerró la sesión.",
        _ => "Reanudada desde el registro de decisiones.",
    };

    public async Task<ResumedRun> ResumeAsync(ProjectContext project, RunId run, IReadOnlyList<DecisionEntry> log, RunResumeCause cause, CancellationToken cancellationToken)
    {
        var record = await ports.Runs.FindAsync(project.ProjectId, run, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run {run} is not registered in project {project.ProjectId}.");
        if (cause != RunResumeCause.UserRequested && !record.Options.AutoResumeConsent)
        {
            throw new InvalidOperationException($"Run {run} has no consent in its header to resume by itself (AutoResumeConsent): only a person can resume it.");
        }

        var entries = RunLog.Of(log, run.Value);
        if (entries.All(e => e.Entry.Kind != DecisionKind.RunStarted))
        {
            throw new InvalidOperationException($"Run {run} never started.");
        }

        if (entries.Any(e => e.Entry.Kind == DecisionKind.RunFinished))
        {
            throw new InvalidOperationException($"Run {run} has finished; start a follow-up run instead.");
        }

        var recorder = new RunRecorder(ports.Decisions, ports.Evidence, project.ProjectId, run);

        // The run goes on with the profile it started with, or not at all: RunStarted, in the verified log, holds the hash
        // of the profile the user approved, and a stored header whose profile hashes otherwise (a step that lost its
        // "isolation": "none", a header rewritten behind the store's back) would run other commands, or run them otherwise.
        var started = entries.Select(e => e.Payload).OfType<RunStartedPayload>().First();
        if (!string.Equals(record.Verification.ContentHash, started.VerificationProfileHash, StringComparison.Ordinal))
        {
            await NotResumableAsync(
                recorder,
                $"The verification profile stored with run {run} hashes to {record.Verification.ContentHash}, but the run started with the profile {started.VerificationProfileHash}: it is not resumed with steps other than the approved ones.",
                cancellationToken).ConfigureAwait(false);
        }

        // The same for the whole header (N-05): the roster with its instructions, tools and budgets, the options, the base
        // commit and the plan and requirements it names are stored rows, and RunStarted anchored the hash of all of them.
        await CheckHeaderAsync(recorder, record, started, cancellationToken).ConfigureAwait(false);
        var plan = await ports.Plans.FindAsync(project.ProjectId, started.PlanHash, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            await NotResumableAsync(recorder, $"The plan {started.PlanHash} that run {run} started with is not stored.", cancellationToken).ConfigureAwait(false);
        }

        var requirements = await ports.Requirements.FindAsync(project.ProjectId, started.RequirementSetHash, cancellationToken).ConfigureAwait(false);
        if (requirements is null || !string.Equals(requirements.ContentHash, started.RequirementSetHash, StringComparison.Ordinal))
        {
            await NotResumableAsync(recorder, $"The requirement set {started.RequirementSetHash} that run {run} started with is not stored as it was frozen.", cancellationToken).ConfigureAwait(false);
        }

        var replay = RunStateReplayer.Replay(plan!, record.CreatedAt, Events(entries), started.PlanHash);
        if (!replay.IsResumable)
        {
            await NotResumableAsync(recorder, string.Join(" ", replay.Problems), cancellationToken).ConfigureAwait(false);
        }

        var context = new RunContext(project, run, plan!, requirements!, record.Verification, record.Roster, record.Options, record.BaseCommit, record.CreatedAt);

        // The baseline of the resumed run is the user's repository as it is now, taken before the recovery below touches
        // any worktree. What changed while the app was closed is not attributed to the run: the user may have worked
        // meanwhile, and so a change an agent made just before the app stopped (after its last check) is not seen either.
        // What the capture cannot verify is weighed by what this run, as its checked header says, can write.
        // The Claude Code installed now, not the one the run started with, is what the resumed run's executors run on.
        var claudeWritesConfined = await ports.ClaudeWritesConfinedAsync(cancellationToken).ConfigureAwait(false);

        // And it writes at medium integrity exactly as the user approved, or it is not resumed (G-02): the writers are read
        // again from the checked plan, roster and profile with that answer, before the baseline is taken and before
        // anything is recovered or launched.
        await CheckApprovedWritersAsync(recorder, entries, started, MediumIntegrityWriters.Find(context.Plan, record.Roster, context.Profile, claudeWritesConfined), cancellationToken).ConfigureAwait(false);
        var guard = new UserRepositoryGuard(ports.Worktrees, project.RootPath, UserRepositoryGuard.CanWriteAtMediumIntegrity(context.Plan, record.Roster, context.Profile, claudeWritesConfined), recorder);
        await guard.EstablishBaselineAsync(cancellationToken).ConfigureAwait(false);
        var graph = replay.Graph;
        var ledger = Ledger(entries, context, graph);
        try
        {
            await LoadAttemptRecordsAsync(recorder, entries, graph, ledger, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await NotResumableAsync(recorder, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        // Every check passed: the resume, and why, goes in the log before anything of the recovery and before any launch, also
        // when no task was interrupted (a run that only waited for quota). By the consent, the header that carried it is named.
        var byConsent = cause != RunResumeCause.UserRequested;
        var who = byConsent ? RunRecorder.System : new Actor(ActorKind.User, OrchestrationRun.DecidedByUser);
        await recorder.RecordAsync(
            who,
            DecisionKind.RunResumed,
            byConsent ? $"La ejecución {run} se reanuda sola por el consentimiento de su cabecera ({cause})" : $"La ejecución {run} se reanuda a petición del usuario",
            new RunResumedPayload(cause, who.Id, ports.Time.GetUtcNow(), byConsent ? started.HeaderHash : null) { RunId = recorder.RunValue },
            cancellationToken).ConfigureAwait(false);

        ledger.IntegrationCommit = LastIntegration(entries, context, ledger);
        await RecoverAsync(recorder, entries, context, graph, ledger, cancellationToken).ConfigureAwait(false);
        await ReopenInterruptedAsync(recorder, context, graph, ledger, cause, cancellationToken).ConfigureAwait(false);
        return new ResumedRun(context, graph, ledger, recorder, guard);
    }

    /// <summary>
    /// The stored header must hash to what <c>RunStarted</c> anchored (<see cref="RunHeaderHash"/>). A run whose
    /// <c>RunStarted</c> is older than that hash (version 1) cannot have its header checked, so it is not resumed either.
    /// </summary>
    private static async Task CheckHeaderAsync(RunRecorder recorder, RunRecord record, RunStartedPayload started, CancellationToken cancellationToken)
    {
        if (started.HeaderHash is not { } anchored)
        {
            await NotResumableAsync(
                recorder,
                $"Run {recorder.Run} started with RunStarted version {started.V}, before the header of a run was anchored in the decision log: its roster, options, base commit, plan and requirements cannot be checked against what was approved, so it is not resumed. Start a follow-up run instead.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var stored = RunHeaderHash.Of(record);
        if (!string.Equals(stored, anchored, StringComparison.Ordinal))
        {
            await NotResumableAsync(
                recorder,
                $"The header stored for run {recorder.Run} hashes to {stored}, but RunStarted anchored {anchored}: its roster (instructions, tools, budgets), options, base commit, plan, requirements or request changed after the run started, and it is not resumed with anything but what was approved.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The writers at medium integrity the plan of the run has now (<paramref name="writers"/>) must be the ones
    /// <c>RunStarted</c> anchored when it was approved (version 3, <see cref="MediumIntegrityWriters.AreTheApproved"/>),
    /// none included. A <c>RunStarted</c> older than that anchored nothing, so its run goes on only while its plan has
    /// none. Otherwise the alert says why (once per refusal, <see cref="NotResumableAlert.AlreadyRecorded"/>: a run that resumes
    /// by consent is tried at every start of the app), nothing more is written, and <see cref="RunNeedsApprovalException"/>
    /// carries the warning the plan has now: the way forward is approving it again, which starts a follow-up run.
    /// </summary>
    private static async Task CheckApprovedWritersAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        RunStartedPayload started,
        IReadOnlyList<MediumIntegrityWriter> writers,
        CancellationToken cancellationToken)
    {
        var anchored = started.AnchorsApprovedWriters();
        if (anchored ? MediumIntegrityWriters.AreTheApproved(started.ApprovedWriters ?? [], writers) : writers.Count == 0)
        {
            return;
        }

        var now = VerificationStepText.PlanWarning(writers);
        var problem = anchored
            ? "The writers at medium integrity its plan has now are not the ones it was approved with (ADR-0018, decision 3): "
                + "what the confinement probe answers of the installed Claude Code changed since the run started (another version, or no answer), or this version of Agent Studio reads them otherwise. "
                + $"Approved: «{started.ApprovedWarning ?? "none"}». Now: «{now ?? "none"}». It is not resumed with writers the user did not approve: approve its plan again, which starts a follow-up run."
            : string.Create(CultureInfo.InvariantCulture, $"It started with RunStarted version {started.V}, before the writers at medium integrity a run was approved with were anchored in the decision log, and its plan has such writers now: «{now}». ")
                + "It is not resumed with writers nobody can say the user approved: approve its plan again, which starts a follow-up run.";
        if (!NotResumableAlert.AlreadyRecorded(entries, problem))
        {
            await recorder.RecordAsync(
                DecisionKind.AuditAlertRaised,
                $"La ejecución {recorder.Run} no se puede reanudar",
                new AuditAlertPayload(NotResumableAlert.Code, [], problem) { RunId = recorder.RunValue },
                cancellationToken).ConfigureAwait(false);
        }

        throw new RunNeedsApprovalException($"Run {recorder.Run} cannot be resumed: {problem}", recorder.Run, started.PlanHash, anchored, anchored ? started.ApprovedWarning : null, now);
    }

    /// <summary>A run that cannot be rebuilt faithfully is not resumed: the alert says why, and nothing more is written.</summary>
    private static async Task NotResumableAsync(RunRecorder recorder, string problem, CancellationToken cancellationToken)
    {
        await recorder.RecordAsync(
            DecisionKind.AuditAlertRaised,
            $"La ejecución {recorder.Run} no se puede reanudar",
            new AuditAlertPayload("run.not-resumable", [], problem) { RunId = recorder.RunValue },
            cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"Run {recorder.Run} cannot be resumed: {problem}");
    }

    /// <summary>The events of the domain in the entries of a run, in log order: what <see cref="RunStateReplayer"/> replays.</summary>
    internal static IEnumerable<RunEvent> Events(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries)
    {
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case TaskStatusChangedPayload p:
                    yield return new TaskTransitioned(entry.RecordedAt, p.Task, p.From, p.To, p.Reason, p.Evidence);
                    break;
                case ConditionEvaluatedPayload p:
                    yield return new ConditionResolved(entry.RecordedAt, p.Task, p.Branch);
                    break;
            }
        }
    }

    /// <summary>
    /// The engine's memory of a run rebuilt from its entries (attempts, agent runs, blocks, sessions, worktrees, failures and its
    /// verdict): what a resume goes on with, and what the inspection of a run shows (<see cref="RunInspector"/>).
    /// </summary>
    internal static RunLedger Ledger(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, RunContext context, TaskGraph graph)
    {
        var ledger = new RunLedger();
        var status = new Dictionary<TaskId, WorkTaskStatus>(); // where each task stands at this point of the log
        var stoppedReviews = new HashSet<TaskId>(); // the reviews whose current launch a check stopped
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case AgentRunStartedPayload { Task: { } task } started:
                    ledger.Attempts[task] = Math.Max(ledger.Attempts.GetValueOrDefault(task), started.Attempt);
                    ledger.AgentRunsUsed++;
                    ledger.Renew.Remove(task); // a renewal was for the launch after its end, which this is
                    ledger.StoppedChecks.Remove(task); // it launched: the checks in a row start again
                    break;
                case AccessCheckedPayload { Task: { } task } check when StoppedTheLaunch(check, context, task, status):
                    // A check that stopped the launch it came before (F8-3), counted as the live run counts it: a review it
                    // stopped spent no launch, and a limit still on is one more check in a row (ADR-0034, decision 9).
                    if (context.Specs[task].Kind == WorkTaskKind.Review)
                    {
                        ledger.AgentRunsUsed--;
                        stoppedReviews.Add(task);
                    }

                    if (check.Result == AccessCheckResult.StillLimited)
                    {
                        ledger.StoppedChecks[task] = ledger.StoppedChecks.GetValueOrDefault(task) + 1;
                    }

                    break;
                case AgentRunFinishedPayload { Task: { } task } finished:
                    if (finished.SessionId is { } session)
                    {
                        ledger.Sessions[task] = session;
                    }

                    // Whether the next attempt renews the session (D-42): by the class of a version 3 end; a version 2
                    // end has no class, and only the peak of its context can say so.
                    if (finished.End?.Class == Application.Providers.AttemptEndClass.ContextExhausted)
                    {
                        ledger.Renew[task] = HandoffReason.ContextExhausted;
                    }
                    else if (finished.Status is not (AttemptOutcome.Succeeded or AttemptOutcome.RateLimited or AttemptOutcome.Interrupted)
                        && Handoff.NearlyFull(finished.ContextPeak))
                    {
                        ledger.Renew[task] = HandoffReason.ContextNearlyFull;
                    }
                    else
                    {
                        ledger.Renew.Remove(task);
                    }

                    break;
                case WorktreeCreatedPayload { Task: { } task } worktree:
                    ledger.Worktrees.TryAdd(task, new WorktreeInfo(worktree.Name, worktree.Path, worktree.RefName, worktree.BaseCommit));
                    break;
                case TaskStatusChangedPayload change:
                    ledger.Attempts[change.Task] = Math.Max(ledger.Attempts.GetValueOrDefault(change.Task), change.Attempt);
                    if (change.To == WorkTaskStatus.Blocked)
                    {
                        ledger.Blocks[change.Task] = BlockReason.Parse(change.BlockReason) ?? BlockReason.AwaitingHuman;
                        ledger.BlockedAt[change.Task] = entry.RecordedAt;
                    }
                    else
                    {
                        ledger.Blocks.Remove(change.Task);
                        ledger.BlockedAt.Remove(change.Task);
                    }

                    if (change.To == WorkTaskStatus.Failed)
                    {
                        ledger.Failures[change.Task] = new FailureNote(change.Attempt, change.Reason, change.Evidence);
                    }

                    // An attempt ended: from Running, or from Pending when a check stopped it before its launch (an executor
                    // runs only once its agent is launched). A wait without a reset time is one more step (D-37); any other
                    // end starts them again.
                    if (change.From == WorkTaskStatus.Running || change is { From: WorkTaskStatus.Pending, To: WorkTaskStatus.Blocked })
                    {
                        if (change.To == WorkTaskStatus.Blocked && BlockReason.Parse(change.BlockReason) is { Estimated: true })
                        {
                            ledger.EstimatedWaits[change.Task] = ledger.EstimatedWaits.GetValueOrDefault(change.Task) + 1;
                        }
                        else
                        {
                            ledger.EstimatedWaits.Remove(change.Task);
                        }
                    }

                    if (change is { From: WorkTaskStatus.Pending, To: WorkTaskStatus.Running } && context.Specs[change.Task].Kind == WorkTaskKind.Review)
                    {
                        ledger.AgentRunsUsed++;
                    }

                    // A review whose judge was launched (no check stopped it) starts its checks in a row again; an executor
                    // does so at its AgentRunStarted. So does a person's retry, the one Blocked → Pending of a person.
                    if ((change.From == WorkTaskStatus.Running && context.Specs[change.Task].Kind == WorkTaskKind.Review && !stoppedReviews.Remove(change.Task))
                        || (change is { From: WorkTaskStatus.Blocked, To: WorkTaskStatus.Pending } && entry.Actor.Kind == ActorKind.User))
                    {
                        ledger.StoppedChecks.Remove(change.Task);
                    }

                    status[change.Task] = change.To;
                    break;
                case VerdictIssuedPayload verdict:
                    ledger.VerdictId = verdict.VerdictId;
                    break;
                case AuditAlertPayload alert:
                    foreach (var task in alert.Tasks)
                    {
                        ledger.Alerts.Add($"{alert.Code}|{task}");
                    }

                    ledger.RepositoryChanged |= alert.Code == "user-repository.changed";
                    ledger.SourceMoved |= alert.Code == OrchestrationRun.SourceMovedAlert;
                    ledger.IntegrationEvidenceTampered |= alert.Code == OrchestrationRun.EvidenceTamperedAlert;
                    break;
            }
        }

        foreach (var task in graph.Tasks.Where(t => t.Status != WorkTaskStatus.Blocked))
        {
            ledger.Blocks.Remove(task.Id);
            ledger.BlockedAt.Remove(task.Id);
        }

        // A provider whose limit still holds a task holds its other tasks too, as it did in the live run (F8-2): until the
        // latest end of the blocks of its tasks.
        foreach (var (task, block) in ledger.Blocks)
        {
            if (block is { Kind: BlockKind.RateLimitedUntil, Until: { } until } && context.Specs[task].Agent is { } agent
                && context.Agents.TryGetValue(agent, out var definition)
                && (!ledger.Cooldowns.TryGetValue(definition.Model.Provider, out var held) || held < until))
            {
                ledger.Cooldowns[definition.Model.Provider] = until;
            }
        }

        return ledger;
    }

    /// <summary>
    /// Whether <paramref name="check"/> stopped the launch it came before (F8-3): a session signed out, or a quota still
    /// limited, for a review (all its checks come before its judge is launched) or for a task not running yet (an executor
    /// runs once its agent is launched, and the read of the quota when its window ran out comes while it runs).
    /// </summary>
    private static bool StoppedTheLaunch(AccessCheckedPayload check, RunContext context, TaskId task, Dictionary<TaskId, WorkTaskStatus> status) =>
        check is { Source: AccessCheckSource.SessionProbe, Result: AccessCheckResult.SignedOut } or { Source: AccessCheckSource.QuotaRead, Result: AccessCheckResult.StillLimited }
        && context.Specs.TryGetValue(task, out var spec)
        && (spec.Kind == WorkTaskKind.Review || status.GetValueOrDefault(task) != WorkTaskStatus.Running);

    /// <summary>
    /// The attempt records give back what the log does not carry: the result of every completed task (its first completion
    /// evidence) and the head of every worktree (the latest record of the task). A completed task without its record
    /// cannot be resumed on: its commit and outputs would be guesses. Each record must still hash to what the log recorded
    /// when it was captured (<c>EvidenceRegistered</c>): the store only checks it against its own row, which whoever
    /// rewrote the file may have rewritten too.
    /// </summary>
    /// <remarks>
    /// The log, not the store, says what each evidence of the run is: every evidence it registered must still be a row of
    /// the run that matches the metadata its <c>EvidenceRegistered</c> entry anchored (version 2: title, author, capture
    /// time, test counters, commit), and the attempt records are told apart by the title the log anchored. A row missing
    /// from the run's listing (removed, or given to another run, R3-08), a row changed after it was registered, or an
    /// entry that anchors no metadata (version 1), leaves the run not resumable.
    /// </remarks>
    private static async Task LoadAttemptRecordsAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        TaskGraph graph,
        RunLedger ledger,
        CancellationToken cancellationToken)
    {
        var evidence = (await recorder.ListEvidenceAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.Id);
        var registrations = entries.Select(e => e.Payload).OfType<EvidenceRegisteredPayload>().ToList();
        foreach (var registration in registrations)
        {
            var which = registration.Task is { } of
                ? string.Create(CultureInfo.InvariantCulture, $"Evidence {registration.Evidence} of task '{of}' (attempt {registration.Attempt})")
                : $"Evidence {registration.Evidence}";
            if (!registration.AnchorsMetadata())
            {
                throw new InvalidOperationException(
                    $"{which} was registered by version 1 of EvidenceRegistered, which does not anchor its metadata: the run is not resumed on what the store alone says about it.");
            }

            // Every evidence the log registered for the run must still be a row of the run (R3-08): one that is gone from
            // the run's listing (removed, or given to another run) cannot be checked against its entry, and would still be
            // read by its id.
            if (!evidence.TryGetValue(registration.Evidence, out var row))
            {
                throw new InvalidOperationException(
                    $"{which} is registered in the decision log, but the store does not list it for run {recorder.Run}: its row was removed or given to another run afterwards, so it cannot be checked against what the log anchored.");
            }

            if (registration.Disagreements(row) is { Count: > 0 } changed)
            {
                throw new InvalidOperationException(
                    $"{which} no longer matches what the decision log anchored when it was registered: {string.Join("; ", changed)}. Its row was changed afterwards.");
            }
        }

        var candidates = registrations
            .Where(p => p is { Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem, Task: not null }
                && p.Title is { } title && title.StartsWith(AttemptRecord.TitlePrefix, StringComparison.Ordinal))
            .ToList();

        var latest = new Dictionary<TaskId, AttemptRecord>();
        var byEvidence = new Dictionary<EvidenceId, AttemptRecord>();
        foreach (var candidate in candidates)
        {
            byte[] content;
            try
            {
                // Read only as the log anchored it: a record rewritten together with the hash of its row is refused.
                content = await recorder.ReadBytesAsync(candidate.Evidence, candidate.Sha256, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The attempt record {candidate.Evidence} of task '{candidate.Task}' cannot be used as the decision log anchored it: {ex.Message}", ex);
            }

            var parsed = AttemptRecord.TryParse(System.Text.Encoding.UTF8.GetString(content));
            if (parsed is null || parsed.Task != candidate.Task!.Value.Value || parsed.Run != recorder.RunValue)
            {
                continue;
            }

            byEvidence[candidate.Evidence] = parsed;
            var task = candidate.Task.Value;
            if (!latest.TryGetValue(task, out var current) || parsed.Attempt >= current.Attempt)
            {
                latest[task] = parsed;
            }
        }

        foreach (var (task, record) in latest)
        {
            if (record.Head is { } head)
            {
                ledger.Heads[task] = head;
            }
        }

        await LoadNotesAsync(recorder, byEvidence.Values, ledger, cancellationToken).ConfigureAwait(false);

        foreach (var task in graph.Tasks.Where(t => t.Status == WorkTaskStatus.Completed && t.Kind is WorkTaskKind.AgentWork or WorkTaskKind.ToolRun or WorkTaskKind.Integration))
        {
            if (task.CompletionEvidence.Count == 0 || !byEvidence.TryGetValue(task.CompletionEvidence[0], out var record))
            {
                throw new InvalidOperationException($"The result of completed task '{task.Id}' cannot be rebuilt: its attempt record is missing.");
            }

            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var output in record.Outputs)
            {
                outputs[output.Key] = output.Value;
            }

            ledger.Results[task.Id] = new TaskResult(
                record.Head,
                record.ChangedPaths,
                outputs,
                record.Tests,
                record.ReportEvidence is { } report ? new EvidenceId(report) : null,
                record.ReportSha256);
        }
    }

    /// <summary>
    /// What a handoff after the resume tells of each task (F8-6): the counters of the last tests the system ran, and the last
    /// report of its agent, read as the log anchored it; a report that cannot be read so leaves out only the agent's part.
    /// </summary>
    private static async Task LoadNotesAsync(RunRecorder recorder, IEnumerable<AttemptRecord> records, RunLedger ledger, CancellationToken cancellationToken)
    {
        foreach (var attempts in records.GroupBy(r => new TaskId(r.Task)))
        {
            var tests = attempts.Where(r => r.Tests is not null).MaxBy(r => r.Attempt)?.Tests;
            string? summary = null;
            IReadOnlyList<string> openIssues = [];
            if (attempts.Where(r => r.ReportEvidence is not null && r.ReportSha256 is not null).MaxBy(r => r.Attempt) is { } reported)
            {
                try
                {
                    var text = await recorder.ReadTextAsync(new EvidenceId(reported.ReportEvidence!), reported.ReportSha256!, cancellationToken).ConfigureAwait(false);
                    if (ExecutorReport.Parse(text, out _) is { } report)
                    {
                        (summary, openIssues) = (report.Summary, report.OpenIssues);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
                {
                    // The tests still go in the handoff; what the agent said does not.
                }
            }

            ledger.Notes[attempts.Key] = new AttemptNotes(tests, summary, openIssues);
        }
    }

    /// <summary>
    /// Closes every agent launch the app never saw finish (an executor's attempt, and also the planner's and the judge's,
    /// which carry no task), and settles every task that was running: a review whose judge had already given its verdict
    /// ends as that verdict says (the judge is never asked again); any other task is blocked as interrupted (no retry is
    /// spent).
    /// </summary>
    private async Task RecoverAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        RunContext context,
        TaskGraph graph,
        RunLedger ledger,
        CancellationToken cancellationToken)
    {
        // Launches and ends are paired in log order: the planner and the judge both number their launches from 1 and
        // carry no task, so (task, attempt) alone cannot tell them apart.
        var open = new List<AgentRunStartedPayload>();
        foreach (var payload in entries.Select(e => e.Payload))
        {
            switch (payload)
            {
                case AgentRunStartedPayload started:
                    open.Add(started);
                    break;
                case AgentRunFinishedPayload finished:
                    var index = open.FindIndex(o => o.Task == finished.Task && o.Attempt == finished.Attempt);
                    if (index >= 0)
                    {
                        open.RemoveAt(index);
                    }

                    break;
            }
        }

        foreach (var attempt in open)
        {
            var whose = attempt.Task is { } of ? of.Value : attempt.Agent.Value;
            await recorder.RecordAsync(
                DecisionKind.AgentRunFinished,
                $"El intento {attempt.Attempt} de {whose} se interrumpió (la aplicación se cerró)",
                new AgentRunFinishedPayload(attempt.Task, attempt.Attempt, AttemptOutcome.Interrupted, null, null, "La aplicación se cerró durante el intento.", null, null) { RunId = recorder.RunValue },
                cancellationToken).ConfigureAwait(false);
        }

        var openTasks = open.Where(o => o.Task is not null).Select(o => o.Task!.Value).ToHashSet();
        foreach (var task in graph.Tasks.ToList())
        {
            if (task is { Status: WorkTaskStatus.Running, Kind: WorkTaskKind.Review } && VerdictOf(entries, task.Id) is { } verdict)
            {
                await ConcludeReviewAsync(recorder, graph, ledger, task.Id, verdict, context, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var interrupted = task.Status == WorkTaskStatus.Running
                || (task.Status == WorkTaskStatus.Pending && openTasks.Contains(task.Id));
            if (interrupted)
            {
                await MoveAsync(recorder, graph, ledger, task.Id, WorkTaskStatus.Blocked, "Interrumpida: la aplicación se cerró mientras se trabajaba en ella.", BlockReason.Interrupted, context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The commit of the last integration that completed, which <c>RunFinished</c> carries and a delivery takes: as the
    /// live run keeps it, an integration that failed its verification or hit a conflict does not count, even though it
    /// recorded the commit it merged.
    /// </summary>
    private static string? LastIntegration(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, RunContext context, RunLedger ledger) =>
        entries
            .Select(e => e.Payload)
            .OfType<TaskStatusChangedPayload>()
            .Where(p => p.To == WorkTaskStatus.Completed && context.Specs.TryGetValue(p.Task, out var spec) && spec.Kind == WorkTaskKind.Integration)
            .Select(p => ledger.Results.GetValueOrDefault(p.Task)?.Commit)
            .LastOrDefault(commit => commit is not null);

    /// <summary>The last verdict recorded for a review since it last started running, or null when it got none.</summary>
    private static VerdictIssuedPayload? VerdictOf(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, TaskId review)
    {
        VerdictIssuedPayload? verdict = null;
        foreach (var (_, payload) in entries)
        {
            switch (payload)
            {
                case TaskStatusChangedPayload { To: WorkTaskStatus.Running } change when change.Task == review:
                    verdict = null;
                    break;
                case VerdictIssuedPayload issued when issued.Task == review:
                    verdict = issued;
                    break;
            }
        }

        return verdict;
    }

    /// <summary>
    /// The judge answered before the app stopped, but the end of the review never reached the log: the review ends now
    /// as that verdict says, as it would have live. Only a valid approval completes it; a valid request for changes or a
    /// valid rejection fails it; anything else (an escalation, or a verdict still invalid) goes to a person.
    /// </summary>
    private async Task ConcludeReviewAsync(RunRecorder recorder, TaskGraph graph, RunLedger ledger, TaskId id, VerdictIssuedPayload verdict, RunContext context, CancellationToken cancellationToken)
    {
        const string Resumed = "; se concluye al reanudar con el veredicto que ya estaba en el registro";
        switch (verdict)
        {
            case { Valid: true, Decision: VerdictDecision.Approved }:
                await MoveAsync(recorder, graph, ledger, id, WorkTaskStatus.Completed, $"El juez aprobó (veredicto {verdict.VerdictId}){Resumed}.", null, context, cancellationToken).ConfigureAwait(false);
                break;
            case { Valid: true, Decision: VerdictDecision.ChangesRequested or VerdictDecision.Rejected }:
                var what = verdict.Decision == VerdictDecision.Rejected ? "rechazó el trabajo" : "pidió cambios";
                var reason = $"El juez {what} (veredicto {verdict.VerdictId}){Resumed}.";
                await MoveAsync(recorder, graph, ledger, id, WorkTaskStatus.Failed, reason, null, context, cancellationToken).ConfigureAwait(false);
                ledger.Failures[id] = new FailureNote(ledger.Attempts.GetValueOrDefault(id), reason, []);
                break;
            default:
                await MoveAsync(recorder, graph, ledger, id, WorkTaskStatus.AwaitingApproval, $"El veredicto {verdict.VerdictId} necesita a una persona{Resumed}.", null, context, cancellationToken).ConfigureAwait(false);
                break;
        }

        ledger.VerdictId = verdict.VerdictId;
    }

    /// <summary>The resume: the partial work of each interrupted attempt is kept as evidence and its worktree reset; the task is pending again.</summary>
    private async Task ReopenInterruptedAsync(RunRecorder recorder, RunContext context, TaskGraph graph, RunLedger ledger, RunResumeCause cause, CancellationToken cancellationToken)
    {
        foreach (var task in graph.Tasks.Where(t => t.Status == WorkTaskStatus.Blocked && ledger.Blocks.GetValueOrDefault(t.Id)?.Kind == BlockKind.Interrupted).ToList())
        {
            var attempt = ledger.Attempts.GetValueOrDefault(task.Id);
            var evidence = new List<EvidenceId>();
            if (ledger.Worktrees.TryGetValue(task.Id, out var worktree))
            {
                var lease = await ports.Worktrees.EnsureAsync(new WorktreeRequest(context.Project, context.Run, worktree.Name, worktree.BaseCommit), cancellationToken).ConfigureAwait(false);
                var attemptBase = ledger.Heads.GetValueOrDefault(task.Id) ?? worktree.BaseCommit;
                if (context.Specs[task.Id].Kind == WorkTaskKind.AgentWork)
                {
                    var commit = await ports.Worktrees.CommitAllAsync(lease, $"{task.Id}: trabajo parcial del intento {attempt} (interrumpido)\n\nAgent-Studio-Run: {context.Run}\nAgent-Studio-Attempt: {task.Id}/{attempt}", cancellationToken).ConfigureAwait(false);
                    if (commit.Commit != attemptBase)
                    {
                        using var diff = new MemoryStream();
                        await ports.Worktrees.WriteDiffAsync(lease.RepositoryRoot, attemptBase, commit.Commit, diff, cancellationToken).ConfigureAwait(false);
                        var captured = await recorder.CaptureBytesAsync(
                            EvidenceKind.CodeDiff,
                            EvidenceOrigin.CapturedBySystem,
                            $"Diff parcial de {task.Id} (intento {attempt}, interrumpido)",
                            RunRecorder.ProducedBySystem,
                            diff.ToArray(),
                            "text/x-diff",
                            task.Id,
                            attempt,
                            cancellationToken).ConfigureAwait(false);
                        evidence.Add(captured.Id);
                    }
                }

                // No agent of this task is alive: its process died with the app.
                await ports.Worktrees.CheckoutAsync(lease, attemptBase, cancellationToken).ConfigureAwait(false);
                ledger.Heads[task.Id] = attemptBase;
            }

            ledger.Failures[task.Id] = new FailureNote(
                attempt,
                string.Create(CultureInfo.InvariantCulture, $"El intento {attempt} se interrumpió al cerrarse la aplicación; el worktree volvió a la base del intento.") + (evidence.Count > 0 ? " Su diff parcial está en la evidencia." : string.Empty),
                evidence);

            // Never the session of an interrupted attempt, nor one before it (D-43): the next attempt renews it with a handoff.
            ledger.Sessions.Remove(task.Id);
            if (context.Specs[task.Id].Kind == WorkTaskKind.AgentWork)
            {
                ledger.Renew[task.Id] = HandoffReason.Interrupted;
            }
            var why = cause == RunResumeCause.UserRequested ? "Reanudada a petición del usuario." : "Reanudada sola por el consentimiento de la cabecera de su ejecución.";
            await MoveAsync(recorder, graph, ledger, task.Id, WorkTaskStatus.Pending, why, null, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MoveAsync(
        RunRecorder recorder,
        TaskGraph graph,
        RunLedger ledger,
        TaskId id,
        WorkTaskStatus to,
        string reason,
        BlockReason? block,
        RunContext context,
        CancellationToken cancellationToken)
    {
        var task = graph[id];
        var from = task.Status;
        if (to == WorkTaskStatus.Completed)
        {
            task.Complete([], ports.Time.GetUtcNow(), reason); // only a review is completed here, and it proves nothing with evidence
        }
        else
        {
            task.TransitionTo(to, ports.Time.GetUtcNow(), reason);
        }
        if (to == WorkTaskStatus.Blocked)
        {
            ledger.Blocks[id] = block ?? BlockReason.AwaitingHuman;
            ledger.BlockedAt[id] = task.History[^1].At;
        }
        else
        {
            ledger.Blocks.Remove(id);
            ledger.BlockedAt.Remove(id);
        }

        await recorder.RecordAsync(
            DecisionKind.TaskStatusChanged,
            $"{id}: {from} → {to}",
            new TaskStatusChangedPayload(id, from, to, reason, block?.ToString(), [], ledger.Attempts.GetValueOrDefault(id)) { RunId = recorder.RunValue },
            cancellationToken).ConfigureAwait(false);
    }
}
