using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine.Questions;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// A running orchestration (docs/13 section 4.1): a single-threaded loop that is the only one to change the graph and
/// to record transitions. Workers (agents, tools, integrations, reviews, approvals) run in parallel and send signals;
/// the loop applies them, skips what can no longer run, retries, unblocks what waited for quota, cancels abandoned
/// attempts (their silence leaves out the time the machine slept), raises alerts once, dispatches ready tasks within the
/// limits, and when nothing runs decides whether the run ended or rests (waiting for a person, for quota, or paused).
/// Every transition is applied to the graph and then recorded, so the log replays to the same graph.
/// </summary>
internal sealed partial class OrchestrationRun : IOrchestrationRun
{
    /// <summary>The alert of a task ref that no longer points at the commit the system verified for it.</summary>
    internal const string SourceMovedAlert = "integration.source-moved";

    /// <summary>The alert of an integration whose evidence changed in the store between its capture and its read.</summary>
    internal const string EvidenceTamperedAlert = "integration.evidence-tampered";

    /// <summary>Who a person's decision about the dispatch is recorded as: the name the desktop gives whoever answers its dialogs.</summary>
    internal const string DecidedByUser = "user";

    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly RunContext _ctx;
    private readonly EnginePorts _ports;
    private readonly RunRecorder _recorder;
    private readonly TaskGraph _graph;
    private readonly RunLedger _ledger;
    private readonly WorkerContext _workers;
    private readonly Action<OrchestrationRun>? _onFinished;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<OrchestrationEvent> _events = Channel.CreateUnbounded<OrchestrationEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<TaskId, DateTimeOffset> _activity = new();
    private readonly Dictionary<TaskId, InFlight> _inFlight = [];
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _abort = new();
    private readonly SleepAwareClock _clock;
    private RunSnapshot _snapshot;
    private RunPhase _phase = RunPhase.Running;
    private GraphHealthReport? _lastHealth;
    private bool _paused;
    private bool _cancelling;
    private bool _integrityBreached;
    private bool _budgetBlocked;
    private string _cancelReason = string.Empty;
    private DateTimeOffset _cancelDeadline;
    private Task? _loop;

    /// <param name="guard">Checks the user's repository against the baseline captured before the run (or its resume) began.</param>
    public OrchestrationRun(RunContext context, EnginePorts ports, RunRecorder recorder, TaskGraph graph, RunLedger ledger, UserRepositoryGuard guard, Action<OrchestrationRun>? onFinished)
    {
        _ctx = context;
        _ports = ports;
        _recorder = recorder;
        _graph = graph;
        _ledger = ledger;
        _onFinished = onFinished;
        ports.BudgetGate?.Forget(recorder.RunValue); // a start or a resume reads the milestones of the run again
        _integrityBreached = ledger.RepositoryChanged || ledger.SourceMoved || ledger.IntegrationEvidenceTampered;
        _workers = new WorkerContext(
            context,
            ports,
            recorder,
            guard,
            signal => _signals.Writer.TryWrite(signal),
            task => _activity[task] = ports.Time.GetUtcNow(),
            Emit,
            _abort.Token);
        _clock = new SleepAwareClock(ports.Time);
        _snapshot = BuildSnapshot(_graph.Analyze(Now, _ctx.StallThreshold));
    }

    public RunId Id => _ctx.Run;

    public IAsyncEnumerable<OrchestrationEvent> Events => _events.Reader.ReadAllAsync();

    public Task<RunResult> Completion => _completion.Task;

    internal RunContext Context => _ctx;

    /// <summary>
    /// Why the loop stopped on a system error (the decision log could not be written, typically), or null: such a run was not
    /// cut by a close nobody asked for, and the scheduler of this session of the app does not resume it by itself.
    /// </summary>
    internal string? SystemError { get; private set; }

    private DateTimeOffset Now => _ports.Time.GetUtcNow();

    private CancellationToken Token => _abort.Token;

    public RunSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public Task PauseAsync() => SendAsync(CommandKind.Pause, null, null);

    public Task ResumeDispatchAsync() => SendAsync(CommandKind.ResumeDispatch, null, null);

    public Task RetryBlockedAsync(TaskId task) => SendAsync(CommandKind.RetryBlocked, task, null);

    public Task CancelAsync(string reason) => SendAsync(CommandKind.Cancel, null, string.IsNullOrWhiteSpace(reason) ? LoggedTexts.NoReasonGiven : reason);

    /// <summary>
    /// Phase 13 (docs/17 D-30): the answer of a person to the question the agent of its task asked. It goes to the loop through
    /// the channel, like every command; it completes once <c>QuestionAnswered</c> is in the log and the task is pending again,
    /// and fails with why it was refused (no such question, an option the question does not have, a run that ended).
    /// Answering is not approving: it asks no Windows Hello and grants nothing.
    /// </summary>
    public Task AnswerAsync(QuestionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_completion.Task.IsCompleted || !_signals.Writer.TryWrite(new AnswerSignal(answer, done)))
        {
            return Task.FromException(AnswerNotRecorded());
        }

        return done.Task;
    }

    private InvalidOperationException AnswerNotRecorded() =>
        new($"Run {Id} has ended: the answer was not recorded. Resume the run to answer the question of its agent.");

    /// <summary>
    /// Closing the app: the agents' processes are killed (their runs disposed) and nothing is recorded as cancelled; the
    /// run shows as interrupted and is resumed from its log when the user asks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_loop is null)
        {
            return;
        }

        if (!_completion.Task.IsCompleted)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A worker that does not honour cancellation must not hang the app's shutdown.
        }
    }

    /// <summary>
    /// Starts the loop. <paramref name="notices"/> are shown first (for example, that uncommitted changes are left out), and
    /// <paramref name="noticeTexts"/>, in step with them, are the same notices as coded texts for the screen (docs/17 D-47).
    /// </summary>
    internal void Start(IReadOnlyList<string>? notices = null, IReadOnlyList<UserText?>? noticeTexts = null)
    {
        var position = 0;
        foreach (var notice in notices ?? [])
        {
            Emit(new RunPhaseChangedEvent(Now, RunPhase.Running, notice, noticeTexts is not null && position < noticeTexts.Count ? noticeTexts[position] : null));
            position++;
        }

        AnnouncePendingQuestions();

        if (_ports.Power is { } power)
        {
            power.Changed += OnPowerChanged;
        }

        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The power events reach the loop as a signal: its single reader is the only one to touch the clock.</summary>
    private void OnPowerChanged(object? sender, PowerEvent change) => _signals.Writer.TryWrite(new PowerChanged(change));

    private void Emit(OrchestrationEvent orchestrationEvent) => _events.Writer.TryWrite(orchestrationEvent);

    private Task SendAsync(CommandKind kind, TaskId? task, string? reason)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_completion.Task.IsCompleted || !_signals.Writer.TryWrite(new UserCommand(kind, task, reason, done)))
        {
            return Unapplied(kind) is { } refusal ? Task.FromException(refusal) : Task.CompletedTask;
        }

        return done.Task;
    }

    /// <summary>
    /// Why a command the run never got to failed: a pause the log does not show would let the run resume by itself after a
    /// close (docs/16 D-39), and a retry that never happened is no retry. Null for one that needs nothing more of an ended run
    /// (resuming its dispatch, cancelling it).
    /// </summary>
    private InvalidOperationException? Unapplied(CommandKind kind) => kind switch
    {
        CommandKind.Pause => new InvalidOperationException($"Run {Id} has ended: its dispatch was not paused, and nothing was recorded."),
        CommandKind.RetryBlocked => new InvalidOperationException($"Run {Id} has ended; resume it to retry its tasks."),
        _ => null,
    };

    private async Task LoopAsync()
    {
        RunResult result;
        try
        {
            result = await RunLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            result = Interrupted("Interrumpida: la aplicación se cerró. Se reanuda desde el registro cuando lo pidas.");
        }
        catch (Exception ex)
        {
            // Typically the decision log could not be written: nothing more is recorded, and the log is the truth.
            SystemError = ex.Message;
            result = Interrupted("La ejecución se detuvo por un error del sistema y se puede reanudar desde el registro: " + ex.Message);
        }

        if (_ports.Power is { } power)
        {
            power.Changed -= OnPowerChanged;
        }

        await StopWorkersAsync().ConfigureAwait(false);
        _signals.Writer.TryComplete();
        while (_signals.Reader.TryRead(out var pending))
        {
            if (pending is UserCommand command)
            {
                // The loop stopped before it got to the command: it was not applied, and a pause or a retry says so.
                if (Unapplied(command.Kind) is { } refusal)
                {
                    command.Done.TrySetException(refusal);
                }
                else
                {
                    command.Done.TrySetResult();
                }
            }
            else if (pending is AnswerSignal answer)
            {
                answer.Done.TrySetException(AnswerNotRecorded()); // an answer the log does not show is no answer
            }
        }

        foreach (var flight in _inFlight.Values)
        {
            flight.Dispose();
        }

        // The project is released before anyone can see the end (the end of the events or the completion): what reacts to
        // the end of a run, such as deriving and exporting the memory (docs/14 D-18 and D-22), must find it no longer busy.
        _onFinished?.Invoke(this);
        _events.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    private RunResult Interrupted(string summary)
    {
        _phase = RunPhase.Interrupted;
        var health = _graph.Analyze(Now, _ctx.StallThreshold);
        Volatile.Write(ref _snapshot, BuildSnapshot(health));
        var result = new RunResult(Id, RunPhase.Interrupted, health, _ledger.IntegrationCommit, _ledger.VerdictId, summary);
        Emit(new RunPhaseChangedEvent(Now, RunPhase.Interrupted, summary));
        Emit(new RunFinishedEvent(Now, result));
        return result;
    }

    private async Task StopWorkersAsync()
    {
        var working = _inFlight.Values.Select(f => f.Work).OfType<Task>().ToList();
        if (working.Count == 0)
        {
            return;
        }

        await _abort.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(working).WaitAsync(StopGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Workers swallow their own failures; a timeout here only means one is slow to die.
        }
    }

    private async Task<RunResult> RunLoopAsync()
    {
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            while (_signals.Reader.TryRead(out var signal))
            {
                await ApplyAsync(signal).ConfigureAwait(false);
            }

            ApplyHeartbeats();
            await AdvanceAsync().ConfigureAwait(false);

            if (_cancelling)
            {
                if (_inFlight.Count == 0 || Now >= _cancelDeadline)
                {
                    return await FinishCancelledAsync().ConfigureAwait(false);
                }

                Publish();
                await WaitAsync(_cancelDeadline).ConfigureAwait(false);
                continue;
            }

            if (_inFlight.Count == 0)
            {
                var verdict = await ClassifyAsync().ConfigureAwait(false);
                if (verdict.Final)
                {
                    return await FinishAsync(verdict.Phase, verdict.Summary, verdict.Text).ConfigureAwait(false);
                }

                SetPhase(verdict.Phase, verdict.Summary, verdict.Text);
                Publish();
                await WaitAsync(verdict.WakeAt).ConfigureAwait(false);
                continue;
            }

            // Waiting only for a person's answer is not "running": the UI must say whose turn it is.
            var onlyPeople = _inFlight.Values.All(f => !f.CountsForParallel);
            SetPhase(
                _paused ? RunPhase.Paused : onlyPeople ? RunPhase.AwaitingHuman : RunPhase.Running,
                onlyPeople ? "Esperando una decisión tuya en: " + string.Join(", ", _inFlight.Keys) + "." : null,
                onlyPeople ? UserText.Of(OrchestrationUserTexts.VerdictAwaitingHuman, OrchestrationUserTexts.List([.. _inFlight.Keys.Select(k => k.Value)])) : null);
            Publish();
            await WaitAsync(QuotaWake()).ConfigureAwait(false);
        }
    }

    // ---- waiting ----

    /// <summary>
    /// Waits for a signal, a heartbeat at most, or until <paramref name="wakeAt"/> when it comes first (the end of a wait for
    /// quota, or of the grace of a cancellation), all on the clock of the run.
    /// </summary>
    /// <remarks>
    /// The timer counts from when it is armed, not from the moment the delay was worked out. A clock that moves on by the
    /// whole delay in between (a test that advances a fake clock as soon as the run shows the wait, or a machine that falls
    /// asleep right then) would leave a timer that comes late: by up to a heartbeat with the real clock, and never with a fake
    /// one nobody advances again (docs/18 P42). Then the loop looks again at once, as the timer would have made it.
    /// </remarks>
    private async Task WaitAsync(DateTimeOffset? wakeAt)
    {
        var from = Now;
        var delay = _ctx.Heartbeat;
        if (wakeAt is { } at)
        {
            var until = at - from;
            if (until <= TimeSpan.Zero)
            {
                return;
            }

            if (until < delay)
            {
                delay = until;
            }
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var signal = _signals.Reader.WaitToReadAsync(wake.Token).AsTask();
        var timer = Task.Delay(delay, _ports.Time, wake.Token);
        if (Now - from < delay)
        {
            await Task.WhenAny(signal, timer).ConfigureAwait(false);
        }

        await wake.CancelAsync().ConfigureAwait(false);
        Token.ThrowIfCancellationRequested();
    }

    // ---- signals ----

    private async Task ApplyAsync(Signal signal)
    {
        switch (signal)
        {
            case WorkerLaunched launched:
                await OnLaunchedAsync(launched).ConfigureAwait(false);
                break;
            case WorkerEnded ended:
                await OnEndedAsync(ended).ConfigureAwait(false);
                break;
            case UserCommand command:
                await OnCommandAsync(command).ConfigureAwait(false);
                break;
            case AnswerSignal answer:
                await OnAnswerAsync(answer).ConfigureAwait(false);
                break;
            case PowerChanged { Event: { Transition: PowerTransition.Resumed, Slept: { } slept } }:
                _clock.Report(slept); // left out at the next look for abandoned tasks, in this very turn
                break;
        }
    }

    private async Task OnLaunchedAsync(WorkerLaunched launched)
    {
        if (!_inFlight.TryGetValue(launched.Task, out var flight) || flight.Attempt != launched.Attempt)
        {
            return;
        }

        if (launched.Worktree is { } worktree)
        {
            _ledger.Worktrees.TryAdd(launched.Task, worktree);
        }

        if (_graph[launched.Task].Status == WorkTaskStatus.Pending)
        {
            await MoveAsync(launched.Task, WorkTaskStatus.Running, $"Arranca el intento {launched.Attempt}: el proveedor lanzó al agente.", text: UserText.Of(OrchestrationUserTexts.TaskAttemptLaunched, launched.Attempt)).ConfigureAwait(false);
        }
    }

    private async Task OnEndedAsync(WorkerEnded ended)
    {
        var id = ended.Task;
        if (!_inFlight.TryGetValue(id, out var flight) || flight.Attempt != ended.Attempt)
        {
            return;
        }

        _inFlight.Remove(id);
        flight.Dispose();
        var end = ended.End;
        if ((flight.Kind == WorkTaskKind.AgentWork && !end.AgentRunRecorded) || (flight.Kind == WorkTaskKind.Review && end.Unlaunched))
        {
            _ledger.AgentRunsUsed--; // the budget counts launches that reached the log; a check that stopped one launched nothing
        }

        // The checks in a row that found the limit still on (ADR-0034, decision 9): one more, or none since the task launched.
        if (end.Unlaunched && end.Kind == EndKind.RateLimited)
        {
            _ledger.StoppedChecks[id] = _ledger.StoppedChecks.GetValueOrDefault(id) + 1;
        }
        else if (flight.Kind == WorkTaskKind.AgentWork ? end.AgentRunRecorded : !end.Unlaunched)
        {
            _ledger.StoppedChecks.Remove(id);
        }

        if (end.Worktree is { } worktree)
        {
            _ledger.Worktrees.TryAdd(id, worktree);
        }

        if (end.Head is { } head)
        {
            _ledger.Heads[id] = head;
        }

        if (end.SessionId is { } session)
        {
            _ledger.Sessions[id] = session;
        }

        if (end.VerdictId is { } verdict)
        {
            _ledger.VerdictId = verdict;
        }

        if (end.Kind != EndKind.RateLimited)
        {
            _ledger.EstimatedWaits.Remove(id); // the attempt ended otherwise: the next wait without a reset time starts the steps again
        }

        if (flight.Kind == WorkTaskKind.AgentWork)
        {
            // What a handoff of a later attempt tells (F8-6): the last tests the system ran, and the agent's last report.
            _ledger.Notes[id] = AttemptNotes.Merge(_ledger.Notes.GetValueOrDefault(id), end.Tests, end.AgentSummary, end.AgentOpenIssues);
            if (end.Renew is { } renew)
            {
                _ledger.Renew[id] = renew;
            }
        }

        var status = _graph[id].Status;
        switch (end.Kind)
        {
            case EndKind.Completed:
                _ledger.Results[id] = end.Result ?? new TaskResult(null, [], new Dictionary<string, string>(), null, null, null);
                if (end.IntegrationCommit is { } commit)
                {
                    _ledger.IntegrationCommit = commit;
                }

                await MoveAsync(id, WorkTaskStatus.Completed, end.Reason, end.Evidence, text: end.Text).ConfigureAwait(false);
                break;
            case EndKind.Failed:
                await FailAsync(id, flight.Attempt, end.Reason, end.Evidence, end.Text).ConfigureAwait(false);
                break;
            case EndKind.AwaitingHuman:
                await MoveAsync(id, WorkTaskStatus.Blocked, end.Reason, end.Evidence, BlockReason.AwaitingHuman, text: end.Text).ConfigureAwait(false);
                break;
            case EndKind.RateLimited:
                // The wait follows the policy of the header (F8-2): the reset the provider announced (plus the margin, with
                // the consent), or the steps the system estimates when it did not say or gave one already past. Its other
                // tasks wait as long.
                var estimatedBefore = _ledger.EstimatedWaits.GetValueOrDefault(id);
                var overload = end.EndClass == AttemptEndClass.RateOrOverload;
                var announced = QuotaWaitPolicy.Announced(_ctx, Now, end.RetryAt);
                var limit = QuotaWaitPolicy.Block(_ctx, Now, announced, estimatedBefore, overload);
                if (limit.Estimated)
                {
                    _ledger.EstimatedWaits[id] = estimatedBefore + 1;
                }
                else
                {
                    _ledger.EstimatedWaits.Remove(id);
                }

                if ((flight.Provider ?? ProviderOf(_ctx.Specs[id])) is { } provider)
                {
                    _ledger.Cooldowns[provider] = limit.Until!.Value;
                }

                var why = announced is null ? QuotaWaitPolicy.Unannounced(_ctx.Specs[id], limit, overload, end.RetryAt) : end.Reason;
                await MoveAsync(id, WorkTaskStatus.Blocked, why, end.Evidence, limit, text: announced is null ? QuotaWaitPolicy.UnannouncedText(_ctx.Specs[id], limit, overload, end.RetryAt) : end.Text).ConfigureAwait(false);
                break;
            case EndKind.LaunchFailed or EndKind.Broken:
                await MoveAsync(id, WorkTaskStatus.Blocked, end.Reason, end.Evidence, BlockReason.ProviderUnavailable, text: end.Text).ConfigureAwait(false);
                break;
            case EndKind.RepositoryChanged:
                _integrityBreached = true;
                _ledger.RepositoryChanged = true;
                await AgentRunner.RecordRepositoryAlertAsync(_recorder, id, end.Differences, Token).ConfigureAwait(false);
                if (status == WorkTaskStatus.Running)
                {
                    await FailAsync(id, flight.Attempt, end.Reason, end.Evidence, end.Text).ConfigureAwait(false);
                }

                break;
            case EndKind.SourceMoved:
                _integrityBreached = true;
                _ledger.SourceMoved = true;
                await _recorder.RecordAsync(
                    DecisionKind.AuditAlertRaised,
                    $"La ref de {string.Join(", ", end.MovedSources)} se movió después de verificarse",
                    new AuditAlertPayload(SourceMovedAlert, [id, .. end.MovedSources], string.Join(" ", end.Differences)) { RunId = _recorder.RunValue },
                    Token).ConfigureAwait(false);
                if (status == WorkTaskStatus.Running)
                {
                    await FailAsync(id, flight.Attempt, end.Reason, end.Evidence, end.Text).ConfigureAwait(false);
                }

                break;
            case EndKind.IntegrationEvidenceTampered:
                // Read back moments after the capture, in this process: only someone rewriting the store explains it.
                _integrityBreached = true;
                _ledger.IntegrationEvidenceTampered = true;
                await _recorder.RecordAsync(
                    DecisionKind.AuditAlertRaised,
                    $"La evidencia de la integración {id} cambió en el almacén después de capturarse",
                    new AuditAlertPayload(
                        EvidenceTamperedAlert,
                        [id],
                        $"The evidence of integration '{id}' changed in the store between its capture and its read back, which only tampering explains: the integration is not accepted, and nothing more is retried or dispatched. " + string.Join(" ", end.Differences)) { RunId = _recorder.RunValue },
                    Token).ConfigureAwait(false);
                if (status == WorkTaskStatus.Running)
                {
                    await FailAsync(id, flight.Attempt, end.Reason, end.Evidence, end.Text).ConfigureAwait(false);
                }

                break;
            case EndKind.Cancelled:
                if (status is WorkTaskStatus.Pending or WorkTaskStatus.Running or WorkTaskStatus.Blocked or WorkTaskStatus.AwaitingApproval)
                {
                    await MoveAsync(id, WorkTaskStatus.Cancelled, end.Reason, end.Evidence, text: end.Text).ConfigureAwait(false);
                }

                break;
            case EndKind.ApprovalNeeded:
                await MoveAsync(id, WorkTaskStatus.AwaitingApproval, end.Reason, text: end.Text).ConfigureAwait(false);
                StartApproval(_ctx.Specs[id], flight.Attempt, Orchestrator.ReviewEscalationAction, $"La revisión de la tarea {id} necesita tu decisión: {end.Reason}");
                break;
            case EndKind.Approved:
                await MoveAsync(id, WorkTaskStatus.Completed, end.Reason, text: end.Text).ConfigureAwait(false);
                break;
            case EndKind.Denied:
                await FailAsync(id, flight.Attempt, end.Reason, [], end.Text).ConfigureAwait(false);
                break;
            case EndKind.AsksQuestion:
                await AskAsync(id, flight.Attempt, end).ConfigureAwait(false);
                break;
        }
    }

    private async Task FailAsync(TaskId id, int attempt, string reason, IReadOnlyList<EvidenceId> evidence, UserText? text = null)
    {
        await MoveAsync(id, WorkTaskStatus.Failed, reason, evidence, text: text).ConfigureAwait(false);
        _ledger.Failures[id] = new FailureNote(attempt, reason, evidence, text);
    }

    private async Task OnCommandAsync(UserCommand command)
    {
        string? refusal = null;
        try
        {
            switch (command.Kind)
            {
                case CommandKind.Pause when !_paused:
                    _paused = true;
                    await RecordDispatchAsync(RunDispatchChange.Paused).ConfigureAwait(false);
                    break;
                case CommandKind.ResumeDispatch when _paused:
                    _paused = false;
                    await RecordDispatchAsync(RunDispatchChange.Resumed).ConfigureAwait(false);
                    break;
                case CommandKind.RetryBlocked:
                    refusal = await UnblockAsync(command.Task!.Value).ConfigureAwait(false);
                    break;
                case CommandKind.Cancel when !_cancelling:
                    _cancelling = true;
                    _cancelReason = command.Reason ?? LoggedTexts.NoReasonGiven;
                    _cancelDeadline = Now + CancelGrace;
                    foreach (var flight in _inFlight.Values)
                    {
                        flight.Cancel(StopCause.User);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            // The caller must never wait forever: it learns why, and the loop handles the failure (the log is the truth).
            command.Done.TrySetException(ex);
            throw;
        }

        if (refusal is null)
        {
            command.Done.TrySetResult();
        }
        else
        {
            command.Done.TrySetException(new InvalidOperationException(refusal));
        }
    }

    /// <summary>
    /// A person paused or resumed the dispatch, from the monitor or from wherever the app offers it (docs/16 D-39): the log
    /// says who and when, and the last of these entries decides whether the run may resume by itself after a close nobody
    /// asked for. The person is <see cref="DecidedByUser"/>, as the desktop names whoever answers its dialogs.
    /// </summary>
    private Task RecordDispatchAsync(RunDispatchChange change) =>
        _recorder.RecordAsync(
            new Actor(ActorKind.User, DecidedByUser),
            DecisionKind.RunDispatchChanged,
            change == RunDispatchChange.Paused ? $"El usuario pausa el despacho de {Id}" : $"El usuario reanuda el despacho de {Id}",
            new RunDispatchChangedPayload(change, DecidedByUser, Now) { RunId = _recorder.RunValue },
            Token);

    /// <summary>The user's retry of a blocked task; returns why it is refused, or null.</summary>
    private async Task<string?> UnblockAsync(TaskId id)
    {
        if (!_ctx.Specs.ContainsKey(id))
        {
            return $"Task '{id}' is not in run {Id}.";
        }

        if (_graph[id].Status != WorkTaskStatus.Blocked)
        {
            return $"Task '{id}' is {_graph[id].Status}, not blocked.";
        }

        if (_ledger.Blocks.GetValueOrDefault(id) is { Kind: BlockKind.AwaitingAnswer })
        {
            // A retry without the answer would launch the agent knowing nothing of what the person decided (docs/17 §0.2).
            return $"Task '{id}' waits for the answer to the question of its agent: answer it (AnswerAsync) instead of retrying.";
        }

        if (_ledger.Blocks.GetValueOrDefault(id) is { Kind: BlockKind.RateLimitedUntil } && _ctx.Specs[id].Agent is { } agent
            && _ctx.Agents.TryGetValue(agent, out var definition))
        {
            _ledger.Cooldowns.Remove(definition.Model.Provider); // the user decided to try again now
            _ledger.CheckAccess.Add(id); // after a wait all the same: its access is checked before it is launched (F8-3)
        }

        // A person's retry starts the checks in a row again (ADR-0034, decision 9): the automatic waits are armed anew. The
        // transition is the person's in the log, so a resume rebuilds the count from it.
        _ledger.StoppedChecks.Remove(id);
        await MoveAsync(id, WorkTaskStatus.Pending, "El usuario pidió reintentarla.", actor: new Actor(ActorKind.User, DecidedByUser), text: UserText.Of(OrchestrationUserTexts.TaskRetryByUser)).ConfigureAwait(false);
        return null;
    }

    private void ApplyHeartbeats()
    {
        foreach (var (id, at) in _activity)
        {
            var task = _graph[id];
            if (task.Status == WorkTaskStatus.Running)
            {
                task.RecordActivity(at);
            }
        }
    }

    // ---- progress ----

    private async Task AdvanceAsync()
    {
        bool changed;
        do
        {
            changed = await PropagateSkipsAsync().ConfigureAwait(false);
            changed |= await AnswerByConsentAsync().ConfigureAwait(false);
            changed |= await RetryFailedAsync().ConfigureAwait(false);
            changed |= await UnblockQuotaAsync().ConfigureAwait(false);
            await WatchHealthAsync().ConfigureAwait(false);
            if (!_paused && !_cancelling && !_integrityBreached)
            {
                changed |= await DispatchAsync().ConfigureAwait(false);
            }
        }
        while (changed);
    }

    private async Task<bool> PropagateSkipsAsync()
    {
        var skipped = _graph.PropagateSkips(Now);
        foreach (var id in skipped)
        {
            var change = _graph[id].History[^1];
            await RecordTransitionAsync(id, change.From, WorkTaskStatus.Skipped, change.Reason, [], null).ConfigureAwait(false);
        }

        return skipped.Count > 0;
    }

    private async Task<bool> RetryFailedAsync()
    {
        if (_integrityBreached || _cancelling)
        {
            return false;
        }

        var changed = false;
        foreach (var task in _graph.Tasks.Where(t => t.Status == WorkTaskStatus.Failed && !t.RetriesExhausted && !_inFlight.ContainsKey(t.Id)).ToList())
        {
            if (UsesAgentRun(_ctx.Specs[task.Id]) && _ledger.AgentRunsUsed >= _ctx.Options.MaxAgentRuns)
            {
                _budgetBlocked = true;
                continue;
            }

            var reason = _ledger.Failures.TryGetValue(task.Id, out var failure) ? failure.Reason : "fallo";
            await MoveAsync(
                task.Id,
                WorkTaskStatus.Pending,
                string.Create(CultureInfo.InvariantCulture, $"Reintento {task.RetryCount + 1} de {task.MaxRetries} tras: {reason}"),
                text: UserText.Of(OrchestrationUserTexts.TaskRetry, task.RetryCount + 1, task.MaxRetries, (object?)failure?.Text ?? LoggedTexts.TaskReason(reason) ?? (object)reason)).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> UnblockQuotaAsync()
    {
        var changed = false;
        foreach (var (id, block) in _ledger.Blocks.Where(b => b.Value.Kind == BlockKind.RateLimitedUntil).ToList())
        {
            if (block.Until <= Now && AutoWait(id, block))
            {
                await MoveAsync(id, WorkTaskStatus.Pending, "Terminó la espera de cuota del proveedor.", text: UserText.Of(OrchestrationUserTexts.TaskQuotaWaitEnded)).ConfigureAwait(false);
                _ledger.CheckAccess.Add(id); // before it is launched again, its access is checked (F8-3)
                changed = true;
            }
        }

        return changed;
    }

    private bool AutoWait(TaskId id, BlockReason block) =>
        QuotaWaitPolicy.WaitsByItself(_ctx, block, _ledger.BlockedAt.GetValueOrDefault(id, Now), _ledger.StoppedChecks.GetValueOrDefault(id));

    /// <summary>
    /// When the machine slept since the loop last looked (F8-5), the silence of every running task leaves that time out:
    /// its last sign of life moves on by the time asleep (never past now), so a healthy attempt is not taken for abandoned
    /// on waking, before its CLI can say anything, and one still silent is after the stall threshold of awake time. Waits
    /// for quota stay on the wall clock (the reset of a provider came closer while the machine slept), and deadlines are
    /// timers, which do not count the time asleep. The monitor is told, with the time found.
    /// </summary>
    private void DiscountSleep()
    {
        var asleep = _clock.Observe();
        if (asleep <= TimeSpan.Zero)
        {
            return;
        }

        var now = Now;
        var running = _graph.Tasks.Where(t => t.Status == WorkTaskStatus.Running).ToList();
        foreach (var task in running)
        {
            var moved = task.LastActivityAt + asleep;
            task.RecordActivity(moved < now ? moved : now);
        }

        if (running.Count > 0)
        {
            Emit(new RunPhaseChangedEvent(
                now,
                _phase,
                string.Create(CultureInfo.InvariantCulture, $"El equipo estuvo suspendido (o su hora saltó) {asleep.TotalMinutes:0.#} min: el abandono de {string.Join(", ", running.Select(t => t.Id))} no cuenta ese tiempo.")));
        }
    }

    private async Task WatchHealthAsync()
    {
        DiscountSleep();
        var health = _graph.Analyze(Now, _ctx.StallThreshold);
        foreach (var id in health.StalledTasks)
        {
            if (_inFlight.TryGetValue(id, out var flight) && flight.Cause == StopCause.None)
            {
                flight.Cancel(StopCause.Stalled);
                await AlertOnceAsync(
                    $"task.stalled|{id}|{flight.Attempt}",
                    "task.stalled",
                    [id],
                    string.Create(CultureInfo.InvariantCulture, $"Task '{id}' showed no activity for more than {_ctx.StallThreshold.TotalMinutes:0.#} minutes; attempt {flight.Attempt} is cancelled.")).ConfigureAwait(false);
            }
        }

        foreach (var id in health.UnreachableTasks)
        {
            await AlertOnceAsync($"task.unreachable|{id}", "task.unreachable", [id], $"Task '{id}' can never run: a task it depends on failed for good or was cancelled.").ConfigureAwait(false);
        }

        foreach (var id in health.RetryExhaustedTasks)
        {
            await AlertOnceAsync($"task.retries-exhausted|{id}", "task.retries-exhausted", [id], $"Task '{id}' failed and has no retries left.").ConfigureAwait(false);
        }

        if (_lastHealth is null || !SameHealth(_lastHealth, health))
        {
            _lastHealth = health;
            Emit(new GraphHealthEvent(Now, health));
        }
    }

    private static bool SameHealth(GraphHealthReport a, GraphHealthReport b) =>
        a.IsDeadlocked == b.IsDeadlocked
        && a.StalledTasks.SequenceEqual(b.StalledTasks)
        && a.UnreachableTasks.SequenceEqual(b.UnreachableTasks)
        && a.RetryExhaustedTasks.SequenceEqual(b.RetryExhaustedTasks);

    private async Task AlertOnceAsync(string key, string code, IReadOnlyList<TaskId> tasks, string message)
    {
        if (_ledger.Alerts.Add(key))
        {
            await _recorder.RecordAsync(
                DecisionKind.AuditAlertRaised,
                $"Alerta {code}",
                new AuditAlertPayload(code, tasks, message) { RunId = _recorder.RunValue },
                Token).ConfigureAwait(false);
        }
    }

    // ---- dispatch ----

    private int ParallelInUse => _inFlight.Values.Count(f => f.CountsForParallel);

    private static bool UsesAgentRun(TaskSpec spec) => spec.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review;

    private int NextAttempt(TaskId id)
    {
        var attempt = _ledger.Attempts.GetValueOrDefault(id) + 1;
        _ledger.Attempts[id] = attempt;
        return attempt;
    }

    private async Task<bool> DispatchAsync()
    {
        var changed = false;
        var ready = _graph.ReadyTasks()
            .Where(t => !_inFlight.ContainsKey(t.Id))
            .OrderBy(t => _ctx.TopologicalIndex[t.Id])
            .ThenBy(t => t.Id.Value, StringComparer.Ordinal)
            .ToList();
        foreach (var task in ready)
        {
            var spec = _ctx.Specs[task.Id];
            switch (spec.Kind)
            {
                case WorkTaskKind.Condition:
                    await EvaluateConditionAsync(spec).ConfigureAwait(false);
                    changed = true;
                    break;
                case WorkTaskKind.HumanApproval:
                    var approvalAttempt = NextAttempt(spec.Id);
                    await MoveAsync(spec.Id, WorkTaskStatus.AwaitingApproval, "Esperando la aprobación del usuario.", text: UserText.Of(OrchestrationUserTexts.TaskAwaitingApproval)).ConfigureAwait(false);
                    StartApproval(spec, approvalAttempt, Orchestrator.HumanApprovalAction, HumanApprovalText(spec));
                    changed = true;
                    break;
                case WorkTaskKind.AgentWork:
                    changed |= await DispatchAgentAsync(spec).ConfigureAwait(false);
                    break;
                case WorkTaskKind.ToolRun or WorkTaskKind.Integration or WorkTaskKind.Review:
                    changed |= await DispatchSystemAsync(spec).ConfigureAwait(false);
                    break;
                default:
                    await MoveAsync(spec.Id, WorkTaskStatus.Blocked, $"Los nodos {spec.Kind} no están disponibles en la Fase 3.", null, BlockReason.AwaitingHuman, text: UserText.Of(OrchestrationUserTexts.TaskNodeUnavailable, spec.Kind.ToString())).ConfigureAwait(false);
                    changed = true;
                    break;
            }
        }

        // An approval that was pending when the app stopped is asked again (the question died with the process).
        foreach (var task in _graph.Tasks.Where(t => t.Status == WorkTaskStatus.AwaitingApproval && !_inFlight.ContainsKey(t.Id)).ToList())
        {
            var spec = _ctx.Specs[task.Id];
            var (action, text) = spec.Kind == WorkTaskKind.Review
                ? (Orchestrator.ReviewEscalationAction, $"La revisión de la tarea {spec.Id} necesita tu decisión.")
                : (Orchestrator.HumanApprovalAction, HumanApprovalText(spec));
            StartApproval(spec, _ledger.Attempts.GetValueOrDefault(spec.Id, 1), action, text);
            changed = true;
        }

        return changed;
    }

    private static string HumanApprovalText(TaskSpec spec) =>
        string.IsNullOrWhiteSpace(spec.Objective) ? spec.Title : $"{spec.Title}: {spec.Objective}";

    private void StartApproval(TaskSpec spec, int attempt, string action, string description)
    {
        var flight = new InFlight(spec.Id, attempt, spec.Kind, null, countsForParallel: false);
        _inFlight[spec.Id] = flight;
        flight.Work = Task.Run(() => SystemWorkers.ApprovalAsync(_workers, spec, attempt, action, description, flight));
    }

    private async Task<bool> DispatchAgentAsync(TaskSpec spec)
    {
        var id = spec.Id;
        if (ParallelInUse >= _ctx.Options.MaxParallelTasks)
        {
            return false;
        }

        if (_ledger.AgentRunsUsed >= _ctx.Options.MaxAgentRuns)
        {
            _budgetBlocked = true;
            return false;
        }

        var agent = _ctx.Agents[spec.Agent!.Value];
        var provider = _ports.ProviderFor(_ctx.Project.RootPath, agent.Model.Provider);
        if (provider is null)
        {
            await MoveAsync(id, WorkTaskStatus.Blocked, $"No hay ningún proveedor «{agent.Model.Provider}» para {agent.Id}.", null, BlockReason.ProviderUnavailable, text: UserText.Of(OrchestrationUserTexts.TaskNoProvider, agent.Model.Provider.ToString(), agent.Id.ToString())).ConfigureAwait(false);
            return true;
        }

        if (_ledger.Cooldowns.TryGetValue(provider.Provider, out var cooldown) && cooldown > Now)
        {
            return false;
        }

        var limit = (provider as ConcurrencyLimitedAgentProvider)?.Limit ?? provider.Capabilities.MaxConcurrentRuns;
        if (_inFlight.Values.Count(f => f.Provider == provider.Provider) >= limit)
        {
            return false; // another task of the same provider may still go first: no head-of-line blocking
        }

        // The budget of the project and of the milestone (docs/16 D-05, F9-8): checked once the attempt could launch, never when it
        // would wait anyway. A refusal (or a budget that cannot be read) blocks the task waiting for the person, like AwaitingHuman, and says
        // why; it launches nothing and nothing is final: raising the budget and retrying the task launches it, and a transient reading
        // failure is read again at the next launch.
        if (_ports.BudgetGate is { } budgetGate)
        {
            var inFlightAgents = _inFlight.Values.Count(f => f.Kind == WorkTaskKind.AgentWork);
            var estimate = new UsageEstimateRequest(_ctx.Project, agent.Id, provider.Provider, agent.Model.Model, AgentRunPurpose.Executor, null, agent.Effort);
            var verdict = await budgetGate.CheckAsync(_ctx.Project, _recorder.RunValue, estimate, inFlightAgents, Token).ConfigureAwait(false);
            if (!verdict.Allowed)
            {
                await AlertOnceAsync($"{verdict.Code}|{id}", verdict.Code!, [id], verdict.Reason!).ConfigureAwait(false);
                await MoveAsync(id, WorkTaskStatus.Blocked, verdict.Reason!, null, BlockReason.AwaitingHuman, text: verdict.Text).ConfigureAwait(false);
                return true;
            }
        }

        if (!TryBases(spec, out var taskBase, out var attemptBase, out var recordWorktree, out var problem))
        {
            await MoveAsync(id, WorkTaskStatus.Blocked, problem!, null, BlockReason.AwaitingHuman).ConfigureAwait(false);
            return true;
        }

        var attempt = NextAttempt(id);
        _ledger.AgentRunsUsed++;

        // A session to renew (F8-6) is never resumed: the attempt goes in a new one, with the handoff of what the log says.
        HandoffFacts? handoff = _ledger.Renew.Remove(id, out var renewal) ? HandoffOf(id, renewal) : null;
        var plan = new AgentAttemptPlan(
            spec,
            agent,
            provider,
            attempt,
            taskBase,
            attemptBase,
            recordWorktree,
            handoff is null ? _ledger.Sessions.GetValueOrDefault(id) : null,
            Upstream(id),
            _ledger.Failures.GetValueOrDefault(id),
            CheckAccess: _ledger.CheckAccess.Remove(id),
            Handoff: handoff,
            Answers: _ledger.Questions.AnswersFor(id)); // every answer the task got, as data (docs/17 D-30), after the orchestrator's (V43)
        var flight = new InFlight(id, attempt, WorkTaskKind.AgentWork, provider.Provider, countsForParallel: true);
        _inFlight[id] = flight;
        Emit(new TaskQueuedEvent(Now, id, provider.Provider));
        flight.Work = Task.Run(() => AgentAttemptWorker.RunAsync(_workers, plan, flight));
        return true;
    }

    /// <summary>What the handoff of the next attempt of <paramref name="id"/> tells of the task and the run (docs/16 D-42).</summary>
    private HandoffFacts HandoffOf(TaskId id, HandoffReason reason)
    {
        var task = _graph[id];
        return new HandoffFacts(
            reason,
            task.Status,
            task.RetryCount,
            task.MaxRetries,
            [.. _graph.Tasks.OrderBy(t => _ctx.TopologicalIndex[t.Id]).ThenBy(t => t.Id.Value, StringComparer.Ordinal).Select(t => (t.Id, t.Status))],
            _ledger.Failures.GetValueOrDefault(id)?.Reason,
            _ledger.Notes.GetValueOrDefault(id),
            _ledger.Questions.AnswersFor(id));
    }

    private async Task<bool> DispatchSystemAsync(TaskSpec spec)
    {
        var id = spec.Id;
        if (ParallelInUse >= _ctx.Options.MaxParallelTasks)
        {
            return false;
        }

        if (spec.Kind == WorkTaskKind.Review && _ledger.AgentRunsUsed >= _ctx.Options.MaxAgentRuns)
        {
            _budgetBlocked = true;
            return false;
        }

        // Every integration of a run merges into its one branch and verifies in its one _integration worktree: one at a
        // time, even if a plan (one that predates the validator rule, say) lets two run together.
        if (spec.Kind == WorkTaskKind.Integration && _inFlight.Values.Any(f => f.Kind == WorkTaskKind.Integration))
        {
            return false;
        }

        if (spec.Kind == WorkTaskKind.Review && ProviderOf(spec) is { } judgeProvider
            && _ledger.Cooldowns.TryGetValue(judgeProvider, out var cooldown) && cooldown > Now)
        {
            return false; // the judge's provider is waiting for its usage limit to reset
        }

        IReadOnlyList<IntegrationSource> sources = [];
        IReadOnlyList<VerifiedRef> verified = [];
        string? baseCommit = null;
        var recordWorktree = false;
        if (spec.Kind == WorkTaskKind.Integration)
        {
            // The commits the system verified for each task (ADR-0012), never the names of their refs: an agent with a
            // shell can move a ref. The worker checks that every task ref still points where it was verified.
            var frontier = Frontier(id);
            sources = [.. frontier.Select(f => new IntegrationSource(f, _ledger.Results[f].Commit ?? _ctx.BaseCommit))];
            verified = [.. frontier
                .Where(f => _ctx.Specs[f].Kind == WorkTaskKind.AgentWork && _ledger.Worktrees.ContainsKey(f) && _ledger.Results[f].Commit is not null)
                .Select(f => new VerifiedRef(f, _ledger.Worktrees[f].RefName, _ledger.Results[f].Commit!))];
        }
        else if (!TryBases(spec, out baseCommit, out _, out recordWorktree, out var problem))
        {
            await MoveAsync(id, WorkTaskStatus.Blocked, problem!, null, BlockReason.AwaitingHuman).ConfigureAwait(false);
            return true;
        }

        var attempt = NextAttempt(id);
        if (spec.Kind == WorkTaskKind.Review)
        {
            _ledger.AgentRunsUsed++;

            // The judge runs on its provider like any agent: the screens learn which before it runs, so the context of a judge
            // on Codex says «no expuesto» from the start (docs/03 §5.1).
            if (ProviderOf(spec) is { } reviewer)
            {
                Emit(new TaskQueuedEvent(Now, id, reviewer));
            }
        }

        await MoveAsync(id, WorkTaskStatus.Running, $"Arranca el intento {attempt}.", text: UserText.Of(OrchestrationUserTexts.TaskAttemptStarted, attempt)).ConfigureAwait(false);
        var flight = new InFlight(id, attempt, spec.Kind, null, countsForParallel: true);
        _inFlight[id] = flight;
        var baseForWorker = baseCommit;
        var checkAccess = _ledger.CheckAccess.Remove(id);
        flight.Work = spec.Kind switch
        {
            WorkTaskKind.ToolRun => Task.Run(() => SystemWorkers.ToolAsync(_workers, spec, attempt, baseForWorker!, recordWorktree, flight)),
            WorkTaskKind.Integration => Task.Run(() => SystemWorkers.IntegrationAsync(_workers, spec, attempt, sources, verified, flight)),
            _ => Task.Run(() => SystemWorkers.ReviewAsync(_workers, spec, attempt, baseForWorker!, recordWorktree, flight, checkAccess)),
        };
        return true;
    }

    /// <summary>
    /// The base of a task's worktree (docs/13 section 4.6): an existing worktree keeps its recorded base; a new one starts
    /// on the head of the task's code frontier (the run base with no producer, the producer's commit with one).
    /// </summary>
    private bool TryBases(TaskSpec spec, out string? taskBase, out string? attemptBase, out bool recordWorktree, out string? problem)
    {
        taskBase = null;
        attemptBase = null;
        recordWorktree = false;
        problem = null;
        if (spec.Kind == WorkTaskKind.AgentWork && spec.Workspace == WorkspaceMode.None)
        {
            return true;
        }

        if (_ledger.Worktrees.TryGetValue(spec.Id, out var worktree))
        {
            taskBase = worktree.BaseCommit;
            attemptBase = _ledger.Heads.GetValueOrDefault(spec.Id) ?? worktree.BaseCommit;
            return true;
        }

        var frontier = Frontier(spec.Id);
        if (frontier.Count > 1)
        {
            problem = $"La tarea {spec.Id} parte del código de varias tareas ({string.Join(", ", frontier)}) sin una integración.";
            return false;
        }

        taskBase = frontier.Count == 0 ? _ctx.BaseCommit : _ledger.Results[frontier[0]].Commit ?? _ctx.BaseCommit;
        attemptBase = taskBase;
        recordWorktree = true;
        return true;
    }

    /// <summary>The closest completed tasks that produce code, looking through the ones that do not; skipped work produced nothing.</summary>
    private IReadOnlyList<TaskId> Frontier(TaskId id)
    {
        var found = new HashSet<TaskId>();
        var visited = new HashSet<TaskId>();
        var stack = new Stack<TaskId>(_ctx.PrerequisitesOf(id));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current) || _graph[current].Status == WorkTaskStatus.Skipped)
            {
                continue;
            }

            if (RunContext.ProducesCode(_ctx.Specs[current]))
            {
                if (_graph[current].Status == WorkTaskStatus.Completed)
                {
                    found.Add(current);
                }

                continue;
            }

            foreach (var next in _ctx.PrerequisitesOf(current))
            {
                stack.Push(next);
            }
        }

        return [.. found
            .Where(x => !found.Any(y => y != x && _ctx.IsAncestor(x, y)))
            .OrderBy(x => _ctx.TopologicalIndex[x])
            .ThenBy(x => x.Value, StringComparer.Ordinal)];
    }

    private IReadOnlyList<(TaskId Task, EvidenceId Report, string Sha256)> Upstream(TaskId id) =>
        [.. _ctx.AncestorsInOrder(id)
            .Where(a => _ctx.Specs[a].Kind == WorkTaskKind.AgentWork)
            .Select(a => (Task: a, Result: _ledger.Results.GetValueOrDefault(a)))
            .Where(x => x.Result is { Report: not null, ReportSha256: not null })
            .Select(x => (x.Task, x.Result!.Report!.Value, x.Result.ReportSha256!))];

    // ---- conditions ----

    private async Task EvaluateConditionAsync(TaskSpec spec)
    {
        var id = spec.Id;
        NextAttempt(id);
        await MoveAsync(id, WorkTaskStatus.Running, "Evaluando la condición.", text: UserText.Of(OrchestrationUserTexts.TaskEvaluatingCondition)).ConfigureAwait(false);
        var predicate = spec.Condition!;
        var facts = _graph.Tasks.ToDictionary(t => t.Id, t => Facts(t.Id, t.Status));
        var result = ConditionEvaluator.Evaluate(predicate, facts);
        var branch = ConditionBranches.For(result);
        var text = Describe(predicate);
        await _recorder.RecordAsync(
            DecisionKind.ConditionEvaluated,
            $"Condición {id}: {text} → {branch}",
            new ConditionEvaluatedPayload(id, text, result, branch, FactList(facts[predicate.ReferencedTask])) { RunId = _recorder.RunValue },
            Token).ConfigureAwait(false);

        // Nothing about this task is recorded between ConditionEvaluated and its completion: replay pairs them.
        var reason = $"La condición {text} es {(result ? "cierta" : "falsa")}: rama {branch}.";
        _graph.CompleteCondition(id, branch, Now, reason);
        await RecordTransitionAsync(id, WorkTaskStatus.Running, WorkTaskStatus.Completed, reason, [], null).ConfigureAwait(false);
    }

    private TaskFacts Facts(TaskId id, WorkTaskStatus status)
    {
        var result = _ledger.Results.GetValueOrDefault(id);
        return new TaskFacts(id, status, result?.Tests, result?.Outputs ?? new Dictionary<string, string>(), result?.ChangedPaths ?? []);
    }

    private static IReadOnlyList<ConditionFact> FactList(TaskFacts facts)
    {
        var list = new List<ConditionFact>
        {
            new($"{facts.Task}.status", facts.Status.ToString()),
            new($"{facts.Task}.tests", facts.LatestTests is { } t
                ? string.Create(CultureInfo.InvariantCulture, $"passed={t.Passed} failed={t.Failed} skipped={t.Skipped} exit={t.ExitCode}")
                : "none"),
            new($"{facts.Task}.changedPaths", string.Join(",", facts.ChangedPaths)),
        };
        list.AddRange(facts.Outputs.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => new ConditionFact($"{facts.Task}.output.{o.Key}", o.Value)));
        return list;
    }

    private static string Describe(ConditionPredicate predicate) => predicate switch
    {
        TaskSucceeded p => $"TaskSucceeded({p.Task})",
        TestsPassed p => $"TestsPassed({p.Task})",
        OutputEquals p => $"OutputEquals({p.Task}, {p.Key}, {p.Value})",
        PathsChanged p => $"PathsChanged({p.Task}, {p.PathPrefix})",
        _ => predicate.GetType().Name,
    };

    // ---- the end ----

    private sealed record Verdict(RunPhase Phase, bool Final, string Summary, DateTimeOffset? WakeAt = null, UserText? Text = null);

    private async Task<Verdict> ClassifyAsync()
    {
        var health = _graph.Analyze(Now, _ctx.StallThreshold);
        var tasks = _graph.Tasks.ToList();
        if (tasks.All(t => t.Status is WorkTaskStatus.Completed or WorkTaskStatus.Skipped))
        {
            // The review policy of the agents that wrote the code holds at the end too: a review a condition skipped
            // reviewed nothing. A review is completed only when the judge, or the person it escalated to, approved.
            var unreviewed = Unreviewed();
            if (unreviewed.Count > 0)
            {
                var list = string.Join(", ", unreviewed);
                await AlertOnceAsync(
                    "run.review-required",
                    "run.review-required",
                    unreviewed,
                    $"The review policy of the agents that wrote {list} asks for an approved independent review after them, and none was completed (a skipped review does not count).").ConfigureAwait(false);
                return new Verdict(RunPhase.Failed, true, $"Falló: la política de revisión exige una revisión independiente aprobada de {list}, y no la hubo.", null, UserText.Of(OrchestrationUserTexts.VerdictReviewRequired, OrchestrationUserTexts.List([.. unreviewed.Select(u => u.Value)])));
            }

            // The test policy holds at the end too (N-06): the commit the run ends with must have passed tests the system
            // captured, not only a build, and as the log anchored them.
            var untested = await UntestedFinalCommitsAsync().ConfigureAwait(false);
            if (untested.Count > 0)
            {
                var commits = string.Join(", ", untested.Select(u => $"{u.Task} ({u.Commit ?? "sin commit"})"));
                await AlertOnceAsync(
                    "run.tests-required",
                    "run.tests-required",
                    [.. untested.Select(u => u.Task)],
                    $"The review policy of an agent that wrote code asks for tests captured by the system, and no passing system-captured test run the decision log anchored verified the final commit of {string.Join(", ", untested.Select(u => $"{u.Task} ({u.Commit ?? "no commit"})"))}.").ConfigureAwait(false);
                return new Verdict(
                    RunPhase.Failed,
                    true,
                    $"Falló: la política de pruebas exige que el commit final de la ejecución pase pruebas capturadas por el sistema, y no hay ninguna ejecución de pruebas que pase sobre {commits}.",
                    null,
                    UserText.Of(
                        OrchestrationUserTexts.VerdictTestsRequired,
                        OrchestrationUserTexts.List([.. untested.Select(u => u.Commit is null
                            ? UserText.Of(OrchestrationUserTexts.VerdictUntestedNoCommit, u.Task.Value)
                            : UserText.Of(OrchestrationUserTexts.VerdictUntestedCommit, u.Task.Value, u.Commit))])));
            }

            var completed = tasks.Count(t => t.Status == WorkTaskStatus.Completed);
            return new Verdict(
                RunPhase.Succeeded,
                true,
                string.Create(CultureInfo.InvariantCulture, $"Terminó bien: {completed} tareas completadas y {tasks.Count - completed} omitidas."),
                null,
                UserText.Of(OrchestrationUserTexts.VerdictSucceeded, completed, tasks.Count - completed));
        }

        if (tasks.All(t => WorkTaskTransitions.IsTerminal(t.Status)))
        {
            // Only a cancellation leaves every task terminal with some cancelled: the app stopped before RunFinished.
            return new Verdict(RunPhase.Cancelled, true, "Cancelada: la cancelación se había aplicado a todas las tareas antes de la interrupción.", null, UserText.Of(OrchestrationUserTexts.VerdictCancelledAfterStop));
        }

        if (_integrityBreached)
        {
            return new Verdict(
                RunPhase.Failed,
                true,
                _ledger.RepositoryChanged
                    ? "Se detuvo: el repositorio del usuario cambió durante la ejecución."
                    : _ledger.SourceMoved
                        ? "Se detuvo: la ref de una tarea ya no apunta al commit que se verificó, y no se integró nada de ella."
                        : "Se detuvo: una evidencia de la integración cambió en el almacén entre su captura y su lectura; esa integración no se da por buena, ni se revisa ni se entrega.",
                null,
                UserText.Of(_ledger.RepositoryChanged
                    ? OrchestrationUserTexts.VerdictStoppedRepository
                    : _ledger.SourceMoved ? OrchestrationUserTexts.VerdictStoppedSourceMoved : OrchestrationUserTexts.VerdictStoppedEvidence));
        }

        // Work the budget holds back (a ready agent task, or a failed one whose retry it refused) is always explained,
        // whatever else ended the run.
        var budgetHeld = (_budgetBlocked && _graph.ReadyTasks().Any(t => UsesAgentRun(_ctx.Specs[t.Id])))
            || tasks.Any(t => t.Status == WorkTaskStatus.Failed && !t.RetriesExhausted);
        if (budgetHeld)
        {
            await AlertOnceAsync(
                "run.budget-exhausted",
                "run.budget-exhausted",
                [.. tasks.Where(t => !WorkTaskTransitions.IsTerminal(t.Status)).Select(t => t.Id)],
                "The run used its whole agent-run budget (MaxAgentRuns) with work left.").ConfigureAwait(false);
        }

        if (health.RetryExhaustedTasks.Count > 0 || health.UnreachableTasks.Count > 0 || budgetHeld)
        {
            var parts = new List<string> { "Falló." };
            var coded = new List<UserText>();
            if (health.RetryExhaustedTasks.Count > 0)
            {
                parts.Add("Sin reintentos: " + string.Join(", ", health.RetryExhaustedTasks) + ".");
                coded.Add(UserText.Of(OrchestrationUserTexts.VerdictNoRetries, OrchestrationUserTexts.List([.. health.RetryExhaustedTasks.Select(t => t.Value)])));
            }

            if (health.UnreachableTasks.Count > 0)
            {
                parts.Add("Inalcanzables: " + string.Join(", ", health.UnreachableTasks) + ".");
                coded.Add(UserText.Of(OrchestrationUserTexts.VerdictUnreachable, OrchestrationUserTexts.List([.. health.UnreachableTasks.Select(t => t.Value)])));
            }

            if (budgetHeld)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"Se agotó el presupuesto de ejecuciones de agente ({_ctx.Options.MaxAgentRuns})."));
                coded.Add(UserText.Of(OrchestrationUserTexts.VerdictBudgetSpent, _ctx.Options.MaxAgentRuns));
            }

            return new Verdict(RunPhase.Failed, true, string.Join(" ", parts), null, UserText.Of(OrchestrationUserTexts.VerdictFailed, OrchestrationUserTexts.Sentences(coded)));
        }

        var blocked = tasks.Where(t => t.Status == WorkTaskStatus.Blocked).Select(t => (Task: t, Reason: _ledger.Blocks.GetValueOrDefault(t.Id))).ToList();
        if (blocked.Any(b => b.Reason?.Kind == BlockKind.Interrupted))
        {
            return new Verdict(RunPhase.Interrupted, false, "Interrumpida: reanúdala cuando quieras.", null, UserText.Of(OrchestrationUserTexts.VerdictInterrupted));
        }

        // A task waiting for the answer to its question waits for a person too (docs/17 D-29), and the consent of the run may
        // answer it by itself at a known time.
        if (blocked.Any(b => b.Reason?.Kind is BlockKind.AwaitingHuman or BlockKind.AwaitingAnswer) || tasks.Any(t => t.Status == WorkTaskStatus.AwaitingApproval))
        {
            var waiting = blocked.Where(b => b.Reason?.Kind is BlockKind.AwaitingHuman or BlockKind.AwaitingAnswer).Select(b => b.Task.Id).Concat(tasks.Where(t => t.Status == WorkTaskStatus.AwaitingApproval).Select(t => t.Id));
            return new Verdict(
                RunPhase.AwaitingHuman,
                false,
                "Esperando una decisión tuya en: " + string.Join(", ", waiting) + ".",
                ConsentWake(),
                UserText.Of(OrchestrationUserTexts.VerdictAwaitingHuman, OrchestrationUserTexts.List([.. waiting.Select(w => w.Value)])));
        }

        if (blocked.Any(b => b.Reason?.Kind == BlockKind.ProviderUnavailable))
        {
            var stuck = blocked.Where(b => b.Reason?.Kind == BlockKind.ProviderUnavailable).Select(b => b.Task.Id).ToList();
            await AlertOnceAsync("run.blocked|" + string.Join(",", stuck), "run.blocked", stuck, "The run cannot progress: these tasks could not be launched and wait for the user to retry them.").ConfigureAwait(false);
            return new Verdict(
                RunPhase.Blocked,
                false,
                "Bloqueada: no se pudo lanzar " + string.Join(", ", stuck) + ". Revisa el proveedor y reinténtalas.",
                null,
                UserText.Of(OrchestrationUserTexts.VerdictBlockedLaunch, OrchestrationUserTexts.List([.. stuck.Select(t => t.Value)])));
        }

        var quota = QuotaWake();
        if (quota is { } wake)
        {
            var estimated = _ledger.Blocks.Values.Any(b => b.Estimated && b.Until == wake) ? " (estimada)" : string.Empty;
            return new Verdict(
                RunPhase.WaitingForQuota,
                false,
                $"En espera de cuota hasta {wake.ToString("O", CultureInfo.InvariantCulture)}{estimated}.",
                wake,
                UserText.Of(estimated.Length > 0 ? OrchestrationUserTexts.VerdictWaitingQuotaEstimated : OrchestrationUserTexts.VerdictWaitingQuota, wake.ToUniversalTime()));
        }

        if (blocked.Any(b => b.Reason?.Kind == BlockKind.RateLimitedUntil))
        {
            var checkedOut = blocked
                .Where(b => b.Reason?.Kind == BlockKind.RateLimitedUntil && QuotaWaitPolicy.ChecksExhausted(_ctx, _ledger.StoppedChecks.GetValueOrDefault(b.Task.Id)))
                .Select(b => b.Task.Id)
                .ToList();
            return new Verdict(
                RunPhase.Paused,
                false,
                checkedOut.Count > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"En pausa: el límite de uso seguía activo en {_ctx.Options.MaxAgentRuns} comprobaciones seguidas de {string.Join(", ", checkedOut)}; reintenta la tarea cuando quieras.")
                    : "En pausa: el límite de uso del proveedor dura más de lo que se espera sola; reintenta la tarea cuando quieras.",
                null,
                checkedOut.Count > 0
                    ? UserText.Of(OrchestrationUserTexts.VerdictPausedChecks, _ctx.Options.MaxAgentRuns, OrchestrationUserTexts.List([.. checkedOut.Select(t => t.Value)]))
                    : UserText.Of(OrchestrationUserTexts.VerdictPausedLong));
        }

        if (_paused)
        {
            return new Verdict(RunPhase.Paused, false, "En pausa.", null, UserText.Of(OrchestrationUserTexts.VerdictPaused));
        }

        var unfinished = tasks.Where(t => !WorkTaskTransitions.IsTerminal(t.Status)).Select(t => t.Id).ToList();
        await AlertOnceAsync("graph.deadlock|" + string.Join(",", unfinished), "graph.deadlock", unfinished, "No task can progress and nothing explains why (deadlock).").ConfigureAwait(false);
        return new Verdict(RunPhase.Blocked, false, "Bloqueada: ninguna tarea puede avanzar.", null, UserText.Of(OrchestrationUserTexts.VerdictDeadlock));
    }

    /// <summary>
    /// The completed tasks that wrote code for an agent whose policy asks for an independent review, and that no
    /// completed review comes after, in topological then id order.
    /// </summary>
    private List<TaskId> Unreviewed() =>
        [.. _graph.Tasks
            .Where(t => t.Status == WorkTaskStatus.Completed
                && _ctx.Specs[t.Id] is { Kind: WorkTaskKind.AgentWork, Workspace: WorkspaceMode.Write, Agent: { } agent }
                && _ctx.Agents.TryGetValue(agent, out var definition) && definition.Review.RequiresIndependentReview)
            .Where(t => !_graph.Tasks.Any(r => r.Kind == WorkTaskKind.Review && r.Status == WorkTaskStatus.Completed && _ctx.IsAncestor(t.Id, r.Id)))
            .Select(t => t.Id)
            .OrderBy(id => _ctx.TopologicalIndex[id])
            .ThenBy(id => id.Value, StringComparer.Ordinal)];

    /// <summary>
    /// When the policy of an agent that wrote code (a completed write task of its) asks for system-captured tests, the
    /// commits the run ends with must each have passed them: the commits of the completed code producers that no other
    /// completed producer builds on (the final integration, or a writer that came after it). A test run counts only as
    /// the decision log anchored it (<c>EvidenceRegistered</c> version 2: a test run the system captured, passing, of that
    /// very commit), never as a row of the store says. Returns the final producers whose commit has none, in topological
    /// then id order; empty when no such policy applies.
    /// </summary>
    private async Task<IReadOnlyList<(TaskId Task, string? Commit)>> UntestedFinalCommitsAsync()
    {
        var policy = _graph.Tasks.Any(t => t.Status == WorkTaskStatus.Completed
            && _ctx.Specs[t.Id] is { Kind: WorkTaskKind.AgentWork, Workspace: WorkspaceMode.Write, Agent: { } agent }
            && _ctx.Agents.TryGetValue(agent, out var definition) && definition.Review.RequiresSystemCapturedTests);
        if (!policy)
        {
            return [];
        }

        var producers = _graph.Tasks
            .Where(t => t.Status == WorkTaskStatus.Completed && RunContext.ProducesCode(_ctx.Specs[t.Id]))
            .Select(t => t.Id)
            .ToList();
        var final = producers
            .Where(p => !producers.Any(q => q != p && _ctx.IsAncestor(p, q)))
            .OrderBy(id => _ctx.TopologicalIndex[id])
            .ThenBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => (Task: id, Commit: _ledger.Results.GetValueOrDefault(id)?.Commit))
            .ToList();

        var log = await _ports.Decisions.LoadAsync(_ctx.Project.ProjectId, Token).ConfigureAwait(false);
        var tested = !log.Verification.IsValid
            ? []
            : RunLog.Of(log.Entries, _recorder.RunValue)
                .Select(e => e.Payload)
                .OfType<EvidenceRegisteredPayload>()
                .Where(p => p.AnchorsMetadata()
                    && p is { Kind: EvidenceKind.TestRun, Origin: EvidenceOrigin.CapturedBySystem, SubjectCommit: not null, TestRun: not null }
                    && p.TestRun.ToSummary().Succeeded)
                .Select(p => p.SubjectCommit!)
                .ToHashSet(StringComparer.Ordinal);
        return [.. final.Where(f => f.Commit is null || !tested.Contains(f.Commit))];
    }

    /// <summary>When the next wait for quota ends: a rate-limited task that unblocks by itself, or a provider cooldown that holds ready work.</summary>
    private DateTimeOffset? QuotaWake()
    {
        DateTimeOffset? wake = null;
        foreach (var (id, block) in _ledger.Blocks)
        {
            if (block.Kind == BlockKind.RateLimitedUntil && AutoWait(id, block) && _graph[id].Status == WorkTaskStatus.Blocked)
            {
                wake = Min(wake, block.Until!.Value);
            }
        }

        foreach (var task in _graph.ReadyTasks())
        {
            if (_ctx.Specs[task.Id] is { Kind: WorkTaskKind.AgentWork or WorkTaskKind.Review } spec && ProviderOf(spec) is { } provider
                && _ledger.Cooldowns.TryGetValue(provider, out var until) && QuotaWaitPolicy.WaitsForCooldown(_ctx, until, Now))
            {
                wake = Min(wake, until);
            }
        }

        return wake;
    }

    /// <summary>The provider the agent of a task (the executor, or the judge of a review) runs on, or null.</summary>
    private Domain.Providers.ProviderId? ProviderOf(TaskSpec spec) =>
        spec.Agent is { } agent && _ctx.Agents.TryGetValue(agent, out var definition) ? definition.Model.Provider : null;

    private static DateTimeOffset Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;

    private async Task<RunResult> FinishCancelledAsync()
    {
        foreach (var task in _graph.Tasks.Where(t => t.Status is WorkTaskStatus.Pending or WorkTaskStatus.Running or WorkTaskStatus.Blocked or WorkTaskStatus.AwaitingApproval).ToList())
        {
            await MoveAsync(task.Id, WorkTaskStatus.Cancelled, "Cancelada por el usuario: " + _cancelReason, text: UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, LoggedTexts.CancelReason(_cancelReason))).ConfigureAwait(false);
        }

        return await FinishAsync(RunPhase.Cancelled, "Cancelada por el usuario: " + _cancelReason, UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, LoggedTexts.CancelReason(_cancelReason))).ConfigureAwait(false);
    }

    private async Task<RunResult> FinishAsync(RunPhase phase, string summary, UserText? text = null)
    {
        SetPhase(phase, summary, text);
        await _recorder.RecordAsync(
            DecisionKind.RunFinished,
            $"La ejecución {Id} termina: {phase}",
            new RunFinishedPayload(phase, summary, _ledger.IntegrationCommit, _ledger.VerdictId) { RunId = _recorder.RunValue },
            Token).ConfigureAwait(false);
        var health = _graph.Analyze(Now, _ctx.StallThreshold);
        Volatile.Write(ref _snapshot, BuildSnapshot(health));
        var result = new RunResult(Id, phase, health, _ledger.IntegrationCommit, _ledger.VerdictId, summary, text);
        Emit(new RunFinishedEvent(Now, result));
        return result;
    }

    // ---- transitions ----

    /// <summary>Applies a transition to the graph (the domain rejects an illegal one before anything is written) and records it.</summary>
    /// <param name="actor">Who moved the task: the system, unless a person decided it (their retry).</param>
    private async Task MoveAsync(TaskId id, WorkTaskStatus to, string reason, IReadOnlyList<EvidenceId>? evidence = null, BlockReason? block = null, Actor? actor = null, UserText? text = null)
    {
        var task = _graph[id];
        var from = task.Status;
        var at = Now;
        var items = evidence ?? [];
        if (to == WorkTaskStatus.Completed)
        {
            task.Complete(items, at, reason);
        }
        else if (from == WorkTaskStatus.Failed && to == WorkTaskStatus.Pending)
        {
            task.Retry(at, reason);
        }
        else
        {
            task.TransitionTo(to, at, reason);
        }

        await RecordTransitionAsync(id, from, to, reason, items, block, actor, text).ConfigureAwait(false);
    }

    private async Task RecordTransitionAsync(TaskId id, WorkTaskStatus from, WorkTaskStatus to, string reason, IReadOnlyList<EvidenceId> evidence, BlockReason? block, Actor? actor = null, UserText? text = null)
    {
        if (to == WorkTaskStatus.Blocked)
        {
            _ledger.Blocks[id] = block ?? BlockReason.AwaitingHuman;
            _ledger.BlockedAt[id] = Now;
        }
        else
        {
            _ledger.Blocks.Remove(id);
            _ledger.BlockedAt.Remove(id);
            _ledger.Questions.Drop(id); // out of its block (answered, cancelled): the task no longer waits for a question
        }

        await _recorder.RecordAsync(
            actor ?? RunRecorder.System,
            DecisionKind.TaskStatusChanged,
            $"{id}: {from} → {to}",
            new TaskStatusChangedPayload(id, from, to, reason, to == WorkTaskStatus.Blocked ? _ledger.Blocks[id].ToString() : null, evidence, _ledger.Attempts.GetValueOrDefault(id)) { RunId = _recorder.RunValue },
            Token).ConfigureAwait(false);
        Emit(new TaskStatusChangedEvent(Now, id, from, to, reason, text));
    }

    // ---- what the UI sees ----

    private void SetPhase(RunPhase phase, string? detail, UserText? detailText = null)
    {
        if (phase != _phase)
        {
            _phase = phase;
            Emit(new RunPhaseChangedEvent(Now, phase, detail, detailText));
        }
    }

    private void Publish() => Volatile.Write(ref _snapshot, BuildSnapshot(_lastHealth ?? _graph.Analyze(Now, _ctx.StallThreshold)));

    private RunSnapshot BuildSnapshot(GraphHealthReport health) =>
        new(
            Id,
            _phase,
            [.. _graph.Tasks.Select(t => new TaskView(
                t.Id,
                t.Title,
                t.Kind,
                t.Status,
                t.AssignedTo,
                _ledger.Attempts.GetValueOrDefault(t.Id),
                t.RetryCount,
                _ledger.Worktrees.GetValueOrDefault(t.Id)?.Path,
                _ledger.Worktrees.GetValueOrDefault(t.Id)?.RefName,
                _ledger.Blocks.GetValueOrDefault(t.Id)?.ToString(),
                [.. t.CompletionEvidence],
                _ledger.Questions.PendingView(t.Id)))],
            health,
            _ledger.AgentRunsUsed,
            _ctx.Options.MaxAgentRuns);
}
