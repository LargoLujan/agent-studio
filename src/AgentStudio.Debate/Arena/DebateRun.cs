using System.Globalization;
using System.Text;
using System.Threading.Channels;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using static System.FormattableString;

namespace AgentStudio.Debate.Arena;

/// <summary>A participant and the provider of its CLI, as the debate seats it.</summary>
public sealed record DebateSeat(DebateParticipantDefinition Definition, IAgentProvider Provider)
{
    public string Label => Definition.Label;

    public AgentDefinition Agent => Definition.Agent;
}

/// <summary>What one debate works with, fixed when it opens or resumes.</summary>
/// <param name="SubjectFolder">The folder of the dossier package of the run the debate is about, or null.</param>
/// <param name="SubjectEvidence">The evidence of that dossier, which the participants may cite.</param>
public sealed record DebateContext(
    ProjectContext Project,
    DebateRecord Record,
    DebateOpenedPayload Opened,
    string Question,
    FrozenRequirementSet Requirements,
    JudgingArrangement Judging,
    IReadOnlyList<DebateSeat> Seats,
    DebateFolders Folders,
    string? SubjectFolder,
    IReadOnlyList<EvidenceRecord> SubjectEvidence)
{
    public RunId Debate => Record.Id;

    public string? Snapshot => Record.SnapshotCommit is null ? null : Folders.Snapshot;

    public DateTimeOffset StartedAt => Record.CreatedAt;
}

/// <summary>
/// A debate going on in this process (docs/15 D-16 to D-22): sealed rounds of stateless turns, the limits measured by the
/// system, publication in the order of the header, the judgment through <see cref="IDebateJudgment"/>, and the actions of the
/// room. The state is the domain's <see cref="DebateSession"/>, which this run drives exactly as
/// <see cref="DebateStateReplayer"/> will replay it from the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>A turn</b> is a new session that does not persist (<see cref="AgentRunRequest.PersistSession"/> false), read-only
/// whatever its agent says (no writes, shell, network, MCP or skills), with its material in a folder of its own and its
/// prompt captured before the launch. An answer that does not read gets one correction with its problems; a turn that
/// fails is retried once with a new attempt; if it fails again the debate waits for a person (D-22).</item>
/// <item><b>A round</b> launches every turn at once (the limit of each provider may queue some), and nothing of it is
/// captured or announced until every turn ended: <c>AgentRunFinished</c> carries no answer, and the contributions are
/// captured, announced and published in the order of the header when the last turn ends (D-17).</item>
/// <item><b>The limits</b> (D-20): the deadline cancels the turns in course, and those that already answered are published;
/// a round is not launched when the tokens it is expected to use would pass the limit, nor when the launches allowed would
/// not cover it. The tokens are always those of the log (<see cref="DebateLog.Usages"/>), never a counter.</item>
/// <item><b>The user's repository</b> is checked around every round and every judgment: a difference stops the debate.</item>
/// </list>
/// </remarks>
public sealed class DebateRun : IDebateRun
{
    /// <summary>The action of the approval of «Decidir yo» (D-26).</summary>
    public const string HumanResolutionAction = "debate.human-resolution";

    private static readonly string[] ReadTools = ["Read", "Grep", "Glob"];

    /// <summary>The longest a turn waiting for quota goes without looking at the wall clock: the heartbeat of a run.</summary>
    private static readonly TimeSpan QuotaLook = TimeSpan.FromSeconds(5);

    /// <summary>The wait before the retry of a turn whose provider was overloaded: the first short step of the engine (docs/16 D-41).</summary>
    private static readonly TimeSpan OverloadWait = TimeSpan.FromMinutes(1);

    private readonly DebateArenaPorts _ports;
    private readonly DebateContext _context;
    private readonly DebateTurnRecorder _recorder;
    private readonly DebateRepositoryGuard _guard;
    private readonly IDisposable _activity;
    private readonly Action<DebateRun>? _ended;
    private readonly Lock _gate = new();
    private readonly Channel<DebateProgress> _events = Channel.CreateUnbounded<DebateProgress>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<DebateResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<EvidenceId, string> _texts = [];
    private readonly Dictionary<EvidenceId, long> _characters = [];
    private readonly List<DebateContributionView> _contributions = [];
    private readonly List<JudgedVerdict> _verdicts = [];
    private readonly CodedMessages _problems = new();
    private readonly DebateSession _session;

    private Task _loop = Task.CompletedTask;
    private int _launches;
    private int _inFlight;
    private int _tests;
    private bool _judgmentRequested;
    private string? _abortReason;
    private CancellationTokenSource? _roundStop;
    private CancellationTokenSource? _judgmentStop;
    private DebateAwaiting? _awaiting;
    private TaskCompletionSource<HumanAction>? _human;
    private MetricAggregate<long> _tokens = MetricAggregate.Sum(Array.Empty<MetricValue<long>>());
    private int _reportedTurns;
    private PanelOutcome? _panel;
    private bool _canRunTests;
    private int _disposed;

    public DebateRun(DebateArenaPorts ports, DebateContext context, DebateSession session, DebateTurnRecorder recorder, DebateRepositoryGuard guard, IDisposable activity, Action<DebateRun>? ended = null)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(activity);
        _ports = ports;
        _context = context;
        _session = session;
        _recorder = recorder;
        _guard = guard;
        _activity = activity;
        _ended = ended;
    }

    public RunId Id => _context.Debate;

    public DebateContext Context => _context;

    public IAsyncEnumerable<DebateProgress> Events => _events.Reader.ReadAllAsync();

    public Task<DebateResult> Completion => _completion.Task;

    /// <summary>Starts following the debate: its loop runs until it closes, is aborted or this process stops following it.</summary>
    public void Start(bool canRunTests)
    {
        _canRunTests = canRunTests;
        _loop = Task.Run(RunAsync);
    }

    public DebateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DebateSnapshot(
                Id,
                _session.Mode,
                _session.Subject,
                _session.Phase,
                _session.Outcome,
                _session.CurrentRound,
                _context.StartedAt,
                _ports.Time.GetUtcNow() - _context.StartedAt,
                _session.Limits,
                _tokens,
                _reportedTurns,
                _launches,
                _context.Record.MaxAgentRuns,
                DebateViews.Participants(_context.Opened),
                [.. _contributions],
                Volatile.Read(ref _inFlight),
                _session.LimitReached,
                _session.JudgmentReason,
                _awaiting,
                _canRunTests && _session.Phase != DebatePhase.Closed,
                _problems.Messages,
                _problems.Texts);
        }
    }

    /// <summary>
    /// «Pedir juicio ahora» (D-20): the turns in course are cancelled and those that already answered are published; or, while
    /// the debate waits for a person after a turn failed twice, «Pedir juicio con lo que hay» (D-22).
    /// </summary>
    /// <exception cref="InvalidOperationException">The debate is already in judgment or closed, or nothing could be judged yet.</exception>
    public Task RequestJudgmentAsync()
    {
        lock (_gate)
        {
            EnsureDebating();
            if (_human is { } human && _awaiting?.Reason is DebateAwaitReason.FailedTurn or DebateAwaitReason.ProviderLimit)
            {
                human.TrySetResult(new HumanAction.Judge());
                return Task.CompletedTask;
            }

            if (_session.Contributions.Count == 0 && _session.CurrentRound == 1)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.JudgmentTooEarly, "There is no contribution to judge yet: round 1 is still running.");
            }

            _judgmentRequested = true;
            _roundStop?.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>«Reintentar turno» (D-22): a new attempt of the turn of that participant, while the debate waits for it.</summary>
    /// <exception cref="InvalidOperationException">The debate is not waiting for a turn of that participant.</exception>
    public Task RetryTurnAsync(AgentId participant)
    {
        lock (_gate)
        {
            if (_human is not { } human || _awaiting?.Reason is not (DebateAwaitReason.FailedTurn or DebateAwaitReason.ProviderLimit))
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.NotWaitingRetry, "The debate is not waiting for a turn to retry.");
            }

            human.TrySetResult(new HumanAction.Retry(participant));
        }

        return Task.CompletedTask;
    }

    /// <summary>«Añadir evidencia» (D-27): a note of the user, captured as <c>HumanNote</c>/<c>ProvidedByHuman</c> and announced.</summary>
    /// <exception cref="InvalidOperationException">The debate is closed.</exception>
    public async Task AddHumanEvidenceAsync(string title, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            if (_session.Phase == DebatePhase.Closed)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.ClosedEvidence, "The debate is closed: it no longer admits evidence.");
            }
        }

        await _writeGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var record = await _recorder.CaptureRegisteredAsync(
                EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, title.Trim(), "usuario", Encoding.UTF8.GetBytes(text), "text/plain", null, null, null, _stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _characters[record.Id] = text.Length;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// «Ejecutar pruebas» (D-27): the trusted verification profile on the commit of the subject, isolated at Low integrity,
    /// with the steps without isolation left out; every result is evidence of the debate bound to that commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">The debate has no commit, the project has no profile, or the debate is closed.</exception>
    public async Task RunTestsAsync()
    {
        string snapshot;
        string commit;
        lock (_gate)
        {
            if (_session.Phase == DebatePhase.Closed)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.ClosedTests, "The debate is closed: tests are no longer run.");
            }

            if (_context.Snapshot is not { } copy || _context.Record.SnapshotCommit is not { } subject || _ports.Verification is null || _ports.TestProfiles is null)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.NoTestCommit, "This debate has no commit to run tests on, or the project has no verification profile.");
            }

            snapshot = copy;
            commit = subject;
        }

        var profile = await _ports.TestProfiles.FindAsync(_context.Project, _stop.Token).ConfigureAwait(false)
            ?? throw DebateUserTexts.Refusal(DebateUserTexts.NoTrustProfile, "The project has no trusted verification profile.");
        var steps = profile.Steps.Where(s => s.Isolation != ProcessIsolation.None).ToList();
        if (steps.Count == 0)
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.NoIsolatedStep, "The verification profile has no isolated step: a debate runs nothing with the permissions of the person.");
        }

        await _writeGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var attempt = Interlocked.Increment(ref _tests);
            var task = new TaskId("pruebas");
            var results = await _ports.Verification.RunAsync(
                snapshot,
                steps,
                new EvidenceScope(_context.Project.ProjectId, Id, task, attempt, commit, _context.Project.RootPath),
                null,
                _stop.Token).ConfigureAwait(false);
            foreach (var result in results)
            {
                // The verifier already stored these rows with the id of the debate: they are announced even when it stops now.
                await _recorder.RegisterAsync(result.Evidence, task, attempt, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// «Decidir yo» (D-26), when the judgment needs a person: asked through the approval gate (action
    /// <c>debate.human-resolution</c>, bound to the verdicts and the decision), then <c>DebateClosed</c> with the rule <c>user</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The debate is not waiting for a person to decide, or the decision was not approved.</exception>
    public Task DecideAsync(VerdictDecision? decision, string? chosenProposal, string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_human is not { } human || _awaiting?.Reason != DebateAwaitReason.HumanResolution)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.NotWaitingDecision, "The debate is not waiting for a decision.");
            }

            human.TrySetResult(new HumanAction.Decide(decision, chosenProposal, comment, done));
        }

        return done.Task;
    }

    /// <summary>«Abortar»: the turns in course are cancelled, nothing sealed is published and the debate is closed as aborted.</summary>
    /// <exception cref="InvalidOperationException">The debate is already closed.</exception>
    public Task AbortAsync(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_session.Phase == DebatePhase.Closed)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.AlreadyClosed, "The debate is already closed.");
            }

            _abortReason ??= reason.Trim();
            _roundStop?.Cancel();
            _judgmentStop?.Cancel();
            _human?.TrySetResult(new HumanAction.Abort());
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops following the debate in this process: the turns in course are killed, and nothing more is recorded (the list shows it interrupted).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // The loop records how it ended; disposing never throws.
        }

        _stop.Dispose();
        _writeGate.Dispose();
    }

    // ---- the loop ----

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            await _guard.EstablishBaselineAsync(ct).ConfigureAwait(false);
            await LoadAsync(ct).ConfigureAwait(false);
            while (Phase() != DebatePhase.Closed)
            {
                if (TakeAbort() is { } reason)
                {
                    await CloseAbortedAsync(reason, ct).ConfigureAwait(false);
                    continue;
                }

                if (Phase() == DebatePhase.AwaitingJudgment)
                {
                    await JudgeAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (await StopBeforeRoundAsync(ct).ConfigureAwait(false))
                {
                    continue;
                }

                await RunRoundAsync(ct).ConfigureAwait(false);
            }

            // The last figures of the room come from the log as the debate ended: the launches after its last closed round
            // (those an abort cancelled) count too (D-19). The debate is closed already: nothing here changes how it ends.
            try
            {
                await UpdateTokensAsync(await LoadLogAsync(CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DecisionLogIntegrityException or IOException or InvalidDataException or InvalidOperationException)
            {
                lock (_gate)
                {
                    _problems.Add("Las cifras finales del debate no se pudieron leer del registro: " + ex.Message, UserText.Of(DebateUserTexts.ProblemFinalFigures));
                }
            }

            Finish(interrupted: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(interrupted: true);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _problems.Add("El debate se detuvo por un error: " + ex.Message, UserText.Of(DebateUserTexts.ProblemStopped, ex.Message));
            }

            Finish(interrupted: true);
        }
    }

    /// <summary>What the log already holds, when the debate is resumed: the launches, the attempts, the contributions and the tokens.</summary>
    private async Task LoadAsync(CancellationToken ct)
    {
        var log = await LoadLogAsync(ct).ConfigureAwait(false);
        foreach (var attempt in log.Turns)
        {
            var task = attempt.Task!.Value.Value;
            _attempts[task] = Math.Max(_attempts.GetValueOrDefault(task), attempt.Attempt);
            _launches++;
            await CharactersAsync(attempt.Started.PromptEvidenceId, attempt.Started.PromptSha256, ct).ConfigureAwait(false);
        }

        foreach (var (_, payload) in log.Contributions)
        {
            var text = await ContributionTextAsync(payload.Evidence, payload.Sha256, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _contributions.Add(DebateViews.Contribution(_context.Opened, payload, text));
            }
        }

        await UpdateTokensAsync(log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The cuts decided before a round (D-20): the deadline, the tokens the round is expected to use, the launches it would
    /// need, and a judgment a person asked for between rounds. True when the debate went to judgment.
    /// </summary>
    private async Task<bool> StopBeforeRoundAsync(CancellationToken ct)
    {
        int round;
        bool requested;
        lock (_gate)
        {
            round = _session.CurrentRound;
            requested = _judgmentRequested;
            _judgmentRequested = false;
        }

        if (requested && Contributions() > 0)
        {
            await CloseRoundAsync(round, DebateJudgmentReason.Requested, DebateLimitReached.None, "Pediste juicio entre rondas.", ct).ConfigureAwait(false);
            return true;
        }

        if (_ports.Time.GetUtcNow() - _context.StartedAt > _session.Limits.MaxDuration)
        {
            if (!AbortWhenNothingToJudge())
            {
                await CloseRoundAsync(round, DebateJudgmentReason.Limit, DebateLimitReached.Duration, "Se agotó el tiempo del debate antes de lanzar la ronda.", ct).ConfigureAwait(false);
            }

            return true;
        }

        if (round == 1)
        {
            return false;
        }

        var log = await LoadLogAsync(ct).ConfigureAwait(false);
        var usages = await UsagesAsync(log, ct).ConfigureAwait(false);
        var used = DebateBudget.Used(usages);
        var previous = usages.Where(u => DebateIds.TryReadTurn(u.Turn, out var r, out _) && r == round - 1).ToList();
        var estimate = DebateBudget.NextRoundEstimate(previous, _context.Seats.Count);
        if (DebateBudget.WouldExceed(_session.Limits, used, estimate))
        {
            var estimated = DebateBudget.ReportedTurns(usages) < usages.Count || DebateBudget.ReportedTurns(previous) < previous.Count;
            var text = Invariant($"No se lanza la ronda {round}: con {used.Sum ?? 0} tokens usados, se esperan {estimate} más y el límite es {_session.Limits.MaxTotalTokens}")
                + (estimated ? " (disparado por una estimación)." : ".");
            await CloseRoundAsync(round, DebateJudgmentReason.Limit, DebateLimitReached.Tokens, text, ct).ConfigureAwait(false);
            return true;
        }

        if (_context.Record.MaxAgentRuns - Volatile.Read(ref _launches) < _context.Seats.Count)
        {
            await CloseRoundAsync(round, DebateJudgmentReason.TurnMissing, DebateLimitReached.None,
                Invariant($"No se lanza la ronda {round}: quedan {_context.Record.MaxAgentRuns - Volatile.Read(ref _launches)} ejecuciones de las {_context.Record.MaxAgentRuns} permitidas y la ronda necesita {_context.Seats.Count}."), ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task RunRoundAsync(CancellationToken ct)
    {
        int round;
        lock (_gate)
        {
            round = _session.CurrentRound;
        }

        if (!await RepositoryUnchangedAsync(Invariant($"before round {round}"), ct).ConfigureAwait(false))
        {
            return;
        }

        var log = await LoadLogAsync(ct).ConfigureAwait(false);
        var published = await PublishedBeforeAsync(log, round, ct).ConfigureAwait(false);
        var material = await MaterialEvidenceAsync(log, ct).ConfigureAwait(false);
        var rules = ContributionRules.For(_session.Mode, round, published.Count, [.. material.Select(m => m.Record.Id).Distinct()]);
        var schema = ContributionSchema.Build(rules);
        var answeredThisRound = log.Contributions.Where(c => c.Payload.Round == round).Select(c => c.Payload.Author).ToHashSet();
        var turns = _context.Seats.Where(s => !answeredThisRound.Contains(s.Agent.Id)).Select(s => new Turn(s, round)).ToList();

        var deadlineAt = _context.StartedAt + _session.Limits.MaxDuration;
        var remaining = deadlineAt - _ports.Time.GetUtcNow();
        using var roundStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _ports.Time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(roundStop.Token, deadline.Token);
        lock (_gate)
        {
            _roundStop = roundStop;
            if (_abortReason is not null || _judgmentRequested)
            {
                roundStop.Cancel();
            }
        }

        try
        {
            Emit(new DebateRoundSealed(Now(), round, turns.Count));
            var setting = new TurnSetting(round, published, material, rules, schema);
            await Task.WhenAll(turns.Select(t => RunTurnAsync(t, setting, retried: false, stop.Token, ct))).ConfigureAwait(false);
            if (!await RepositoryUnchangedAsync(Invariant($"during round {round}"), ct).ConfigureAwait(false))
            {
                return;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Aborting())
                {
                    return; // the loop closes it as aborted; nothing sealed is published
                }

                if (deadline.IsCancellationRequested || _ports.Time.GetUtcNow() > deadlineAt)
                {
                    await PublishAsync(turns, setting, ct).ConfigureAwait(false);
                    if (!AbortWhenNothingToJudge())
                    {
                        await CloseRoundAsync(round, DebateJudgmentReason.Limit, DebateLimitReached.Duration, "Se agotó el tiempo del debate: se publica lo que ya había respondido.", ct).ConfigureAwait(false);
                    }

                    return;
                }

                if (TakeJudgmentRequest())
                {
                    await PublishAsync(turns, setting, ct).ConfigureAwait(false);
                    if (Contributions() > 0)
                    {
                        await CloseRoundAsync(round, DebateJudgmentReason.Requested, DebateLimitReached.None, "Pediste juicio: se publica lo que ya había respondido.", ct).ConfigureAwait(false);
                        return;
                    }
                }

                var failed = turns.Where(t => t.End is TurnEnd.Failed or TurnEnd.ProviderLimit or TurnEnd.NoBudget).ToList();
                if (failed.Count == 0)
                {
                    await PublishAsync(turns, setting, ct).ConfigureAwait(false);
                    await DecideNextAsync(round, ct).ConfigureAwait(false);
                    return;
                }

                if (round > 1 && failed.Any(t => t.End == TurnEnd.NoBudget))
                {
                    await PublishAsync(turns, setting, ct).ConfigureAwait(false);
                    await CloseRoundAsync(round, DebateJudgmentReason.TurnMissing, DebateLimitReached.None, "Se agotaron las ejecuciones permitidas a mitad de ronda: se juzga con lo que hay.", ct).ConfigureAwait(false);
                    return;
                }

                var first = failed[0];
                var awaiting = new DebateAwaiting(
                    first.End == TurnEnd.ProviderLimit ? DebateAwaitReason.ProviderLimit : DebateAwaitReason.FailedTurn,
                    string.Join(" ", failed.Select(t => $"{t.Seat.Label}: {t.Failure}")),
                    first.Seat.Label,
                    first.ResetsAt,
                    [.. failed.Where(t => t.FailureText is not null).Select(t => t.FailureText!)]);
                var action = await WaitForPersonAsync(awaiting, deadline.Token, ct).ConfigureAwait(false);
                switch (action)
                {
                    case HumanAction.Retry retry when failed.FirstOrDefault(t => t.Seat.Agent.Id == retry.Participant) is { } turn:
                        turn.Reset();
                        using (var again = CancellationTokenSource.CreateLinkedTokenSource(roundStop.Token, deadline.Token))
                        {
                            await RunTurnAsync(turn, setting, retried: true, again.Token, ct).ConfigureAwait(false);
                        }

                        if (!await RepositoryUnchangedAsync(Invariant($"during round {round}"), ct).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case HumanAction.Judge:
                        await PublishAsync(turns, setting, ct).ConfigureAwait(false);
                        if (Contributions() == 0)
                        {
                            lock (_gate)
                            {
                                _problems.Add("No hay ninguna contribución que juzgar: reintenta el turno o aborta el debate.", UserText.Of(DebateUserTexts.ProblemNothingToJudge));
                            }

                            break;
                        }

                        await CloseRoundAsync(round, DebateJudgmentReason.TurnMissing, DebateLimitReached.None,
                            $"Pediste juicio con lo que hay: falta la contribución de {string.Join(", ", failed.Select(t => t.Seat.Label))}.", ct).ConfigureAwait(false);
                        return;
                    case HumanAction.Abort:
                        return;
                    default:
                        break; // the deadline, or a retry of someone who did not fail: look again
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _roundStop = null;
            }
        }
    }

    /// <summary>
    /// One turn: as many attempts as it takes, within the launches allowed: the first answer, its one correction when it does
    /// not read, one retry when the turn fails (after a short wait when its provider was overloaded), and a wait of the
    /// provider's usage limit when the CLI said when it resets.
    /// </summary>
    private async Task RunTurnAsync(Turn turn, TurnSetting setting, bool retried, CancellationToken stop, CancellationToken ct)
    {
        IReadOnlyList<string>? problems = null;
        var corrected = false;
        while (true)
        {
            if (stop.IsCancellationRequested)
            {
                turn.End = TurnEnd.Cancelled;
                return;
            }

            if (!TryReserveLaunch())
            {
                turn.End = TurnEnd.NoBudget;
                turn.Failure = Invariant($"se agotaron las {_context.Record.MaxAgentRuns} ejecuciones permitidas");
                turn.FailureText = UserText.Of(DebateUserTexts.TurnNoBudget, turn.Seat.Label, _context.Record.MaxAgentRuns);
                return;
            }

            var attempt = NextAttempt(turn.Task);
            var launched = await LaunchAsync(turn, setting, attempt, problems, stop, ct).ConfigureAwait(false);
            switch (launched.Status)
            {
                case AttemptOutcome.Succeeded when launched.Answer is { } json:
                    var parsed = ContributionParser.Parse(json, setting.Rules);
                    if (parsed.Answer is { } answer)
                    {
                        turn.Answer = answer;
                        turn.AnswerAttempt = attempt;
                        turn.AnsweredAt = launched.FinishedAt;
                        turn.End = TurnEnd.Answered;
                        return;
                    }

                    if (!corrected)
                    {
                        corrected = true;
                        problems = parsed.Problems;
                        continue;
                    }

                    turn.Failure = "su respuesta no es una contribución válida tras su corrección";
                    turn.FailureText = UserText.Of(DebateUserTexts.TurnInvalidAnswer, turn.Seat.Label);
                    break;
                case AttemptOutcome.Cancelled when stop.IsCancellationRequested:
                    turn.End = TurnEnd.Cancelled;
                    return;
                case var _ when launched.IsLimit:
                    if (launched.ResetsAt is { } resets && resets - _ports.Time.GetUtcNow() <= _ports.Options.MaxAutoWaitForQuota)
                    {
                        try
                        {
                            await WaitForResetAsync(resets, stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested)
                        {
                            turn.End = TurnEnd.Cancelled;
                            return;
                        }

                        continue; // the same attempt kind again, once the limit reset: a wait is not a retry
                    }

                    turn.End = TurnEnd.ProviderLimit;
                    turn.ResetsAt = launched.ResetsAt;
                    turn.Failure = launched.ResetsAt is { } at
                        ? Invariant($"el límite de uso de {turn.Seat.Provider.Provider} se reinicia a las {at:HH:mm} UTC, después de lo que el debate espera solo")
                        : $"{turn.Seat.Provider.Provider} alcanzó su límite de uso sin decir cuándo se reinicia";
                    turn.FailureText = launched.ResetsAt is { } resetsAt
                        ? UserText.Of(DebateUserTexts.TurnLimitResets, turn.Seat.Label, ProviderNames.Of(turn.Seat.Provider.Provider), resetsAt.ToUniversalTime())
                        : UserText.Of(DebateUserTexts.TurnLimitUnknown, turn.Seat.Label, ProviderNames.Of(turn.Seat.Provider.Provider));
                    return;
                case var _ when launched.IsOverload:
                    // An overloaded provider is no usage limit (docs/16 D-41): a short wait in the same way, and the turn's
                    // one retry, as any failed turn has.
                    turn.Failure = launched.Error is { Length: > 0 } overloaded ? $"el turno terminó {launched.Status}: {overloaded}" : $"el turno terminó {launched.Status}";
                    turn.FailureText = UserText.Of(DebateUserTexts.TurnOverloaded, turn.Seat.Label, ProviderNames.Of(turn.Seat.Provider.Provider));
                    if (!retried)
                    {
                        try
                        {
                            await WaitForResetAsync(_ports.Time.GetUtcNow() + OverloadWait, stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested)
                        {
                            turn.End = TurnEnd.Cancelled;
                            return;
                        }
                    }

                    break;
                default:
                    turn.Failure = launched.Error is { Length: > 0 } error ? $"el turno terminó {launched.Status}: {error}" : $"el turno terminó {launched.Status}";
                    turn.FailureText = UserText.Of(DebateUserTexts.TurnEndedBadly, turn.Seat.Label);
                    break;
            }

            if (!retried)
            {
                retried = true;
                corrected = false;
                problems = null;
                continue;
            }

            turn.End = TurnEnd.Failed;
            return;
        }
    }

    /// <summary>
    /// Waits for the reset of a usage limit on the wall clock (F8-5, docs/16 section 2.14), looking at it again every
    /// <see cref="QuotaLook"/> at most, as the loop of a run does. One timer for the whole wait would not do: those of .NET do
    /// not count the time the machine sleeps, so after a sleep past the reset it would go on waiting for the awake time it
    /// still had to run.
    /// </summary>
    /// <remarks>
    /// Each timer counts from when it is armed, not from the moment its look was worked out. A clock that moves on by the
    /// whole look in between (a test that advances a fake clock as soon as the turn waits, or a machine that falls asleep
    /// right then) would leave a timer that comes late: by up to a look with the real clock, and never with a fake one nobody
    /// advances again (docs/18 P42). Then the wait looks again at once, as the timer would have made it, and as the loop of a
    /// run does.
    /// </remarks>
    private async Task WaitForResetAsync(DateTimeOffset resetsAt, CancellationToken stop)
    {
        for (var from = _ports.Time.GetUtcNow(); from < resetsAt; from = _ports.Time.GetUtcNow())
        {
            var left = resetsAt - from;
            var look = left < QuotaLook ? left : QuotaLook;
            using var armed = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timer = Task.Delay(look, _ports.Time, armed.Token);
            if (_ports.Time.GetUtcNow() - from < look)
            {
                await timer.ConfigureAwait(false);
            }
            else
            {
                await armed.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Launched> LaunchAsync(Turn turn, TurnSetting setting, int attempt, IReadOnlyList<string>? problems, CancellationToken stop, CancellationToken ct)
    {
        var seat = turn.Seat;
        var folder = _context.Folders.CreateTurnFolder(turn.Task.Value, attempt);
        var participants = _context.Seats.Select(s => (s.Label, s.Definition.Role)).ToList();
        var material = await DebateMaterial.WriteAsync(
            _context.Folders,
            folder,
            new DebateMaterialInput(
                Id,
                _session.Mode,
                setting.Round,
                seat.Label,
                seat.Definition.Role,
                participants,
                _context.Question,
                _context.Requirements,
                _session.Subject,
                _context.Record.Subject.Reference,
                _context.Record.SnapshotCommit,
                _context.Snapshot,
                _context.SubjectFolder,
                setting.Rules.Kinds,
                setting.Published,
                setting.Material),
            ct).ConfigureAwait(false);
        var prompt = ParticipantPrompt.Build(_session.Mode, seat.Definition.Role, seat.Label, setting.Round, material, setting.Published, folder, _context.Snapshot, problems, ResponseLanguage.Resolve(_ports.Language));
        var title = Invariant($"Prompt de {seat.Label}, ronda {setting.Round} (intento {attempt})");
        var promptRecord = await _recorder.CapturePromptAsync(turn.Task, attempt, title, prompt, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _characters[promptRecord.Id] = prompt.Length;
        }

        var workingDirectory = _context.Snapshot ?? folder;
        var directories = new List<string>();
        if (_context.Snapshot is not null)
        {
            directories.Add(folder);
        }

        if (_context.SubjectFolder is { } subject)
        {
            directories.Add(subject);
        }

        // The prompt is in the store: its AgentRunStarted announces it even when the debate stops now (an unannounced row of the
        // debate is a system check of its judgment). A launch that never ends is closed as interrupted when it is resumed.
        await _recorder.RecordTurnStartedAsync(turn.Task, attempt, seat.Agent, seat.Provider, workingDirectory, promptRecord, seat.Label, CancellationToken.None).ConfigureAwait(false);
        Emit(new DebateTurnStarted(Now(), seat.Label, setting.Round, attempt));

        var agent = seat.Agent with
        {
            Budget = seat.Agent.Budget with
            {
                MaxTurns = Math.Min(seat.Agent.Budget.MaxTurns, _ports.Options.MaxTurnCliTurns),
                MaxDuration = seat.Agent.Budget.MaxDuration < _ports.Options.MaxTurnDuration ? seat.Agent.Budget.MaxDuration : _ports.Options.MaxTurnDuration,
            },
        };
        var request = new AgentRunRequest(
            agent,
            prompt,
            workingDirectory,
            new PermissionProfile(AllowFileWrites: false, AllowShell: false, AllowNetwork: false, ReadTools, [], directories),
            OutputJsonSchema: setting.Schema,
            ResumeSessionId: null,
            Purpose: Invariant($"Debate {Id}: turno de {seat.Label}, ronda {setting.Round}"),
            TaskId: turn.Task.Value,
            Tags: null,
            SkillsDirectory: null,
            McpServers: null,
            PersistSession: false,
            DebateId: Id.Value)
        {
            // The start recorded above, so the journal can name it (docs/17 D-02).
            Anchor = new AgentRunAnchor(_recorder.Debate.Value, turn.Task.Value, attempt),
        };

        var seen = new AgentRunObservations(seat.Provider.Capabilities.ReportsContextOccupancy);
        AttemptOutcome status;
        AgentRunOutcome? outcome = null;
        DateTimeOffset? resets = null;
        string? error = null;
        Interlocked.Increment(ref _inFlight);
        try
        {
            IAgentRun? run = null;
            try
            {
                run = await seat.Provider.StartAsync(request, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                error = "Se canceló antes de arrancar.";
            }
            catch (Exception ex)
            {
                error = "No se pudo lanzar el agente: " + ex.Message;
            }

            if (run is null)
            {
                status = stop.IsCancellationRequested ? AttemptOutcome.Cancelled : AttemptOutcome.Failed;
            }
            else
            {
                seen = new AgentRunObservations(seat.Provider.Capabilities.ReportsContextOccupancy, null, _ports.Time.GetUtcNow());
                await using (run.ConfigureAwait(false))
                {
                    using var cancelOnStop = stop.Register(() => _ = run.CancelAsync());
                    try
                    {
                        await foreach (var agentEvent in run.Events.WithCancellation(ct).ConfigureAwait(false))
                        {
                            seen.Observe(agentEvent, _ports.Time.GetUtcNow());
                            if (agentEvent is RateLimitSignaled limited)
                            {
                                resets = limited.ResetsAt ?? resets;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        // A broken event stream does not decide the outcome: the completion does.
                    }

                    outcome = await run.Completion.WaitAsync(ct).ConfigureAwait(false);
                    status = AttemptOutcomes.From(outcome.Status);
                    error = outcome.Error;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        var finished = await _recorder.RecordTurnFinishedAsync(turn.Task, attempt, status, outcome?.ExitCode, outcome?.SessionId, error, seen, seat.Label, outcome?.Journal, ct).ConfigureAwait(false);
        Emit(new DebateTurnFinished(Now(), seat.Label, setting.Round, attempt, status));

        // The answer stays in this turn, sealed, until its round closes: nothing of it reaches the store or the log now.
        var answer = status == AttemptOutcome.Succeeded ? outcome?.StructuredOutputJson : null;
        var end = outcome?.Classified();
        return new Launched(status, answer, error, end?.ResetsAt ?? resets, finished.RecordedAt, end);
    }

    /// <summary>
    /// Publishes the answers of the round in the order of the header (D-17): each contribution resolved in the copy of the
    /// commit, redacted, captured and announced, with its citations, and only then recorded and submitted to the domain. A
    /// contribution answered after the deadline is never published.
    /// </summary>
    private async Task PublishAsync(IReadOnlyList<Turn> turns, TurnSetting setting, CancellationToken ct)
    {
        var deadline = _context.StartedAt + _session.Limits.MaxDuration;
        foreach (var seat in _context.Seats)
        {
            var turn = turns.FirstOrDefault(t => t.Seat == seat);
            if (turn is not { End: TurnEnd.Answered, Answer: { } answer } || turn.Published || turn.AnsweredAt > deadline)
            {
                continue;
            }

            turn.Published = true;
            var resolutions = _context.Snapshot is { } snapshot && answer.Citations.Count > 0
                ? await CitationResolver.ResolveAsync(snapshot, answer.Citations, ct).ConfigureAwait(false)
                : answer.Citations.Select(c => new CitationResolution(c, CitationProblem.NotFound, null, c.Locator + ": the debate is about no commit, so no code can be cited.")).ToList();
            var redacted = answer.Redact(_ports.Redactor);
            var json = ContributionParser.ToJson(redacted.Answer);
            var record = await _recorder.CaptureRegisteredAsync(
                EvidenceKind.AgentStatement,
                EvidenceOrigin.ReportedByAgent,
                Invariant($"Contribución de {seat.Label}, ronda {setting.Round}"),
                seat.Agent.Id.Value,
                Encoding.UTF8.GetBytes(json),
                "application/json",
                turn.Task,
                turn.AnswerAttempt,
                null,
                ct).ConfigureAwait(false);

            var cites = new List<EvidenceId>();
            var unresolved = new List<string>();
            foreach (var resolution in resolutions)
            {
                if (resolution is { Resolved: true, Text: { } lines })
                {
                    var citation = await _recorder.CaptureRegisteredAsync(
                        EvidenceKind.Document,
                        EvidenceOrigin.CapturedBySystem,
                        $"Cita {resolution.Citation.Id} de {seat.Label}: {resolution.Citation.Locator}",
                        DebateTurnRecorder.SystemId,
                        Encoding.UTF8.GetBytes(lines),
                        "text/plain",
                        turn.Task,
                        turn.AnswerAttempt,
                        _context.Record.SnapshotCommit,
                        ct).ConfigureAwait(false);
                    cites.Add(citation.Id);
                }
                else
                {
                    unresolved.Add($"{resolution.Citation.Id}: {resolution.Problem}");
                }
            }

            var material = setting.Material.Select(m => m.Record.Id.Value).ToHashSet(StringComparer.Ordinal);
            cites.AddRange(answer.Claims.SelectMany(c => c.Support).Where(material.Contains).Distinct(StringComparer.Ordinal).Select(s => new EvidenceId(s)));

            int index;
            lock (_gate)
            {
                index = _session.Contributions.Count;
            }

            var payload = new DebateContributionRecordedPayload(
                index,
                setting.Round,
                seat.Agent.Id,
                answer.Kind,
                answer.RepliesToIndex,
                record.Id,
                record.ContentSha256,
                cites,
                unresolved,
                redacted.RedactedTexts,
                answer.Finished,
                turn.Task.Value,
                turn.AnswerAttempt,
                turn.AnsweredAt) { RunId = Id.Value };
            await _recorder.RecordAsync(DecisionKind.DebateContributionRecorded, Invariant($"Contribución {ContributionLabels.Of(index)} de {seat.Label} (ronda {setting.Round})"), payload, ct).ConfigureAwait(false);

            DebateContributionView view;
            lock (_gate)
            {
                _session.Submit(DebateLog.ToContribution(payload));
                _texts[record.Id] = json;
                _characters[record.Id] = json.Length;
                view = DebateViews.Contribution(_context.Opened, payload, json);
                _contributions.Add(view);
            }

            Emit(new DebateContributionPublished(Now(), view));
        }
    }

    /// <summary>Checks the user's repository; a difference stops the debate (false), with its alert.</summary>
    private async Task<bool> RepositoryUnchangedAsync(string when, CancellationToken ct)
    {
        var differences = await _guard.CheckAsync(ct).ConfigureAwait(false);
        if (differences.Count == 0)
        {
            return true;
        }

        await StopForRepositoryAsync(when, differences, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>After a whole round was published: convergence, the limits, the synthesis of a tribunal, or the next round (D-18 and D-20).</summary>
    private async Task DecideNextAsync(int round, CancellationToken ct)
    {
        if (Aborting() || Phase() is not (DebatePhase.Proposals or DebatePhase.Deliberation))
        {
            return;
        }

        // Every participant published in this round, and every one marked its contribution as finished (also those a
        // resumed debate had already published in it).
        var log = await LoadLogAsync(ct).ConfigureAwait(false);
        var ofRound = log.Contributions.Where(c => c.Payload.Round == round).Select(c => c.Payload).ToList();
        var converged = _context.Seats.All(s => ofRound.Any(c => c.Author == s.Agent.Id)) && ofRound.All(c => c.Finished);
        if (converged)
        {
            await CloseRoundAsync(round, DebateJudgmentReason.Converged, DebateLimitReached.None, "Todos los participantes dieron por terminadas sus posiciones en la misma ronda.", ct).ConfigureAwait(false);
            return;
        }

        await CloseRoundAsync(round, null, DebateLimitReached.None, string.Empty, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records <c>DebateRoundClosed</c> and applies it to the domain, exactly as the replay will (D-20): with a reason, the debate
    /// goes to judgment for it; without one, the domain advances the round at the start plus the whole seconds that passed,
    /// with the tokens of the log, and itself sends a tribunal to its synthesis or a debate past a limit to judgment.
    /// </summary>
    private async Task CloseRoundAsync(int round, DebateJudgmentReason? reason, DebateLimitReached limit, string text, CancellationToken ct)
    {
        var log = await LoadLogAsync(ct).ConfigureAwait(false);
        var (tokens, reported) = await UpdateTokensAsync(log, ct).ConfigureAwait(false);
        var elapsed = (long)Math.Floor(Math.Max(0, (_ports.Time.GetUtcNow() - _context.StartedAt).TotalSeconds));
        var at = _context.StartedAt + TimeSpan.FromSeconds(elapsed);

        DebateJudgmentReason? judgment = reason;
        var reached = limit;
        lock (_gate)
        {
            if (judgment is null)
            {
                reached = _session.CheckLimits(at, tokens.Sum ?? 0, round + 1);
                if (reached != DebateLimitReached.None)
                {
                    judgment = DebateJudgmentReason.Limit;
                }
                else if (_session.Mode == DebateMode.Tribunal)
                {
                    judgment = DebateJudgmentReason.Tribunal;
                }
            }
        }

        var next = judgment is null ? DebatePhase.Deliberation : DebatePhase.AwaitingJudgment;
        var reasonText = text.Length > 0 ? text : judgment switch
        {
            DebateJudgmentReason.Limit => Invariant($"Se alcanzó el límite de {LimitText(reached)}."),
            DebateJudgmentReason.Tribunal => "Un tribunal no delibera: el juez sintetiza las evaluaciones.",
            _ => Invariant($"Sigue la ronda {round + 1}."),
        };

        var snapshot = tokens.Sum is { } sum
            ? new MetricSnapshot(sum, tokens.Provenance, "debate tokens: input + output of the participants", tokens.Origin)
            : new MetricSnapshot(null, MetricProvenance.Unavailable, "debate tokens: no attempt yet", MetricOrigin.Missing);
        var payload = new DebateRoundClosedPayload(round, next, judgment, reached, snapshot, reported < tokens.Count, elapsed, reasonText) { RunId = Id.Value };
        await _recorder.RecordAsync(DecisionKind.DebateRoundClosed, Invariant($"Ronda {round} cerrada"), payload, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (reason is { } explicitReason)
            {
                _session.RequestJudgment(explicitReason, limit);
            }
            else
            {
                _session.AdvanceRound(at, tokens.Sum ?? 0);
            }
        }

        Emit(new DebateRoundEnded(Now(), round, next, judgment, reached, tokens));
    }

    /// <summary>
    /// The judgment (D-23 to D-26): the user's repository is checked around it, the module of the judgment builds the blind
    /// dossier and runs the judges, and the debate closes with their outcome or waits for a person to decide.
    /// </summary>
    private async Task JudgeAsync(CancellationToken ct)
    {
        var before = await _guard.CheckAsync(ct).ConfigureAwait(false);
        if (before.Count > 0)
        {
            await StopForRepositoryAsync("before the judgment", before, ct).ConfigureAwait(false);
            return;
        }

        foreach (var judge in _context.Judging.Judges)
        {
            Emit(new DebateJudgeLaunched(Now(), judge.Agent, 1));
        }

        DebateJudgmentResult? result = null;
        string? failure = null;
        var failureCode = DebateUserTexts.AwaitNeedsPerson;
        using (var judgment = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            lock (_gate)
            {
                _judgmentStop = judgment;
                if (_abortReason is not null)
                {
                    judgment.Cancel();
                }
            }

            try
            {
                result = await _ports.Judgment.JudgeAsync(_context.Project, Id, null, judgment.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (Aborting())
            {
                return;
            }
            catch (Exception ex)
            {
                failure = "El juicio no dio veredicto: " + ex.Message;
                failureCode = DebateUserTexts.AwaitNoVerdict;
            }
            finally
            {
                lock (_gate)
                {
                    _judgmentStop = null;
                }
            }
        }

        if (Aborting())
        {
            return;
        }

        var after = await _guard.CheckAsync(ct).ConfigureAwait(false);
        if (after.Count > 0)
        {
            await StopForRepositoryAsync("during the judgment", after, ct).ConfigureAwait(false);
            return;
        }

        if (result is not null)
        {
            lock (_gate)
            {
                _verdicts.Clear();
                _verdicts.AddRange(result.Verdicts);
            }

            foreach (var verdict in result.Verdicts)
            {
                Emit(new DebateVerdictReceived(Now(), verdict));
            }

            if (!result.Outcome.NeedsHuman)
            {
                var valid = result.Verdicts.Where(v => v.Violations.Count == 0).Select(v => v.Verdict).ToList();
                try
                {
                    await CloseAsync(result.Outcome, valid, result.Outcome.Rule, Reason(result.Outcome, valid), ct).ConfigureAwait(false);
                    return;
                }
                catch (DomainRuleViolationException ex)
                {
                    failure = "Los veredictos no permiten cerrar el debate: " + ex.Message;
                    failureCode = DebateUserTexts.AwaitVerdictsUnusable;
                }
            }
            else
            {
                failure = result.Outcome.Rule == JudgingPanel.Disagree
                    ? "Los jueces del jurado no coinciden: decides tú."
                    : "El juicio necesita a una persona: decides tú.";
                failureCode = result.Outcome.Rule == JudgingPanel.Disagree ? DebateUserTexts.AwaitJuryDisagrees : DebateUserTexts.AwaitNeedsPerson;
            }
        }

        await AwaitDecisionAsync(failure ?? "El juicio necesita a una persona.", UserText.Of(failureCode), ct).ConfigureAwait(false);
    }

    /// <summary>Waits for «Decidir yo» (or «Abortar») and applies it, through the approval gate, bound to the verdicts and the decision.</summary>
    private async Task AwaitDecisionAsync(string message, UserText text, CancellationToken ct)
    {
        while (true)
        {
            var action = await WaitForPersonAsync(new DebateAwaiting(DebateAwaitReason.HumanResolution, message, Texts: [text]), CancellationToken.None, ct).ConfigureAwait(false);
            switch (action)
            {
                case HumanAction.Abort:
                    return;
                case HumanAction.Decide decide:
                    try
                    {
                        await DecideNowAsync(decide, ct).ConfigureAwait(false);
                        decide.Done.TrySetResult();
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        decide.Done.TrySetException(ex);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private async Task DecideNowAsync(HumanAction.Decide decide, CancellationToken ct)
    {
        List<JudgedVerdict> verdicts;
        lock (_gate)
        {
            verdicts = [.. _verdicts];
        }

        var ids = verdicts.Select(v => v.Id).ToList();
        var binding = ContentHash.Sha256Hex(ContentHash.Canonical(["debate.human-resolution/1", Id.Value, string.Join(",", ids), decide.Decision?.ToString(), decide.ChosenProposal, decide.Comment]));
        var description = Invariant($"Decides el debate {Id}: {decide.Decision?.ToString() ?? "sin resolver"}")
            + (decide.ChosenProposal is { } chosen ? $", propuesta {chosen}" : string.Empty)
            + $". Comentario: {decide.Comment}";
        var approval = await _ports.Approvals.RequestAsync(
            new ApprovalRequest(HumanResolutionAction, description, RiskLevel.Critical, null, [], _context.Project.ProjectId, Id.Value, null, binding),
            ct).ConfigureAwait(false);
        if (approval is not { Approved: true } || !string.Equals(approval.Answers, binding, StringComparison.Ordinal))
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.DecisionNotApproved, "The decision was not approved: the debate keeps waiting for it.");
        }

        var valid = verdicts.Where(v => v.Violations.Count == 0).Select(v => v.Verdict).ToList();
        var panel = JudgingPanel.DecidedByUser(decide.Decision, decide.ChosenProposal);
        await CloseAsync(panel, valid, JudgingPanel.User, $"Decisión tuya ({approval.DecidedBy}): {decide.Decision?.ToString() ?? "sin resolver"}.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records <c>DebateClosed</c> and closes the domain. The domain decides first, on the state the log gives (the replay of
    /// the debate as it stands), so a close it would not allow is never recorded.
    /// </summary>
    /// <exception cref="DomainRuleViolationException">The verdicts do not support that close.</exception>
    private async Task CloseAsync(PanelOutcome outcome, IReadOnlyList<Verdict> valid, string rule, string reason, CancellationToken ct)
    {
        // A critical moment: the close rests on the whole log, verified from its first entry (ADR-0010, addendum of 2026-11-04).
        var log = await LoadLogAsync(LogLoad.Full, ct).ConfigureAwait(false);
        var probe = DebateStateReplayer.Replay(Header(), log.Events(_context.StartedAt, _ => [])).Session
            ?? throw new InvalidOperationException("The header of the debate does not open: it cannot be closed.");
        probe.Close(outcome, valid);

        List<string> ids;
        lock (_gate)
        {
            ids = [.. _verdicts.Select(v => v.Id)];
        }

        await _recorder.RecordAsync(
            DecisionKind.DebateClosed,
            Invariant($"Debate cerrado: {outcome.Outcome}"),
            new DebateClosedPayload(outcome.Outcome, ids, rule, outcome.ChosenProposal, reason) { RunId = Id.Value },
            ct).ConfigureAwait(false);
        lock (_gate)
        {
            _session.Close(outcome, valid);
            _panel = outcome;
            _awaiting = null;
        }
    }

    /// <summary>The header of the debate as the domain replays it.</summary>
    private DebateHeader Header() =>
        new(_session.Id, _session.Mode, _session.Subject, _session.Question, _session.Requirements, _session.Participants, _session.Judging, _session.Limits, _session.StartedAt);

    private async Task CloseAbortedAsync(string reason, CancellationToken ct)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _verdicts.Select(v => v.Id)];
        }

        await _recorder.RecordAsync(
            DecisionKind.DebateClosed,
            "Debate abortado",
            new DebateClosedPayload(DebateOutcome.Aborted, ids, DebateClosedPayload.AbortedRule, null, reason) { RunId = Id.Value },
            ct).ConfigureAwait(false);
        lock (_gate)
        {
            _session.Abort(reason);
            _awaiting = null;
        }
    }

    /// <summary>A difference in the user's repository stops the debate (docs/15 §7): an alert, and the debate is aborted with the reason.</summary>
    private async Task StopForRepositoryAsync(string when, IReadOnlyList<string> differences, CancellationToken ct)
    {
        await _guard.RecordChangedAsync(when, differences, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _abortReason ??= "El repositorio del usuario cambió durante el debate: " + string.Join(" ", differences);
            _roundStop?.Cancel();
        }
    }

    private async Task<HumanAction> WaitForPersonAsync(DebateAwaiting awaiting, CancellationToken deadline, CancellationToken ct)
    {
        TaskCompletionSource<HumanAction> human;
        lock (_gate)
        {
            if (_abortReason is not null)
            {
                return new HumanAction.Abort();
            }

            human = new TaskCompletionSource<HumanAction>(TaskCreationOptions.RunContinuationsAsynchronously);
            _human = human;
            _awaiting = awaiting;
        }

        Emit(new DebateAwaitingUser(Now(), awaiting));
        try
        {
            using var onDeadline = deadline.Register(() => human.TrySetResult(new HumanAction.Deadline()));
            return await human.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _human = null;
                _awaiting = null;
            }
        }
    }

    private void Finish(bool interrupted)
    {
        DebateResult result;
        lock (_gate)
        {
            var summary = _session.Phase switch
            {
                DebatePhase.Closed when _session.Outcome == DebateOutcome.Aborted => "Abortado: " + _session.CloseReason,
                DebatePhase.Closed => _session.CloseReason ?? "Cerrado.",
                _ => "Interrumpido: este proceso dejó de seguirlo. Se puede reanudar.",
            };
            result = new DebateResult(Id, _session.Phase, _session.Outcome, _session.Panel ?? _panel, [.. _verdicts], _session.LimitReached, _session.JudgmentReason, summary);
        }

        if (!interrupted && result.Phase == DebatePhase.Closed)
        {
            try
            {
                _context.Folders.RemoveAll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lock (_gate)
                {
                    _problems.Add("No se pudo borrar la copia del commit: " + ex.Message, UserText.Of(DebateUserTexts.ProblemCopyNotDeleted));
                }
            }
        }

        _activity.Dispose();
        Emit(new DebateFinished(Now(), result));
        _events.Writer.TryComplete();
        _ended?.Invoke(this);
        _completion.TrySetResult(result);
    }

    // ---- what the log says ----

    /// <summary>The debate as the verified log has it, loaded incrementally: so is every load of a debate going on but its close (<see cref="CloseAsync"/>).</summary>
    private Task<DebateLog> LoadLogAsync(CancellationToken ct) => LoadLogAsync(LogLoad.Incremental, ct);

    private async Task<DebateLog> LoadLogAsync(LogLoad load, CancellationToken ct)
    {
        var log = await _ports.Decisions.LoadAsync(_context.Project.ProjectId, load, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(_context.Project.ProjectId, log.Verification);
        }

        return DebateLog.Of(log.Entries, Id);
    }

    /// <summary>The tokens of the log (D-19) and how many attempts reported both figures; the room shows them.</summary>
    private async Task<(MetricAggregate<long> Used, int Reported)> UpdateTokensAsync(DebateLog log, CancellationToken ct)
    {
        var usages = await UsagesAsync(log, ct).ConfigureAwait(false);
        var used = DebateBudget.Used(usages);
        var reported = DebateBudget.ReportedTurns(usages);
        lock (_gate)
        {
            _tokens = used;
            _reportedTurns = reported;
        }

        return (used, reported);
    }

    private async Task<IReadOnlyList<TurnUsage>> UsagesAsync(DebateLog log, CancellationToken ct)
    {
        foreach (var attempt in log.Turns)
        {
            await CharactersAsync(attempt.Started.PromptEvidenceId, attempt.Started.PromptSha256, ct).ConfigureAwait(false);
        }

        foreach (var (_, payload) in log.Contributions)
        {
            await CharactersAsync(payload.Evidence, payload.Sha256, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return log.Usages(id => _characters.TryGetValue(id, out var count) ? count : null);
        }
    }

    private async Task CharactersAsync(EvidenceId id, string sha256, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_characters.ContainsKey(id))
            {
                return;
            }
        }

        try
        {
            var text = await _recorder.ReadTextAsync(id, sha256, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _characters[id] = text.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            // Not readable against its hash: the estimate of its attempt counts what can be read.
        }
    }

    private async Task<string?> ContributionTextAsync(EvidenceId id, string sha256, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_texts.TryGetValue(id, out var known))
            {
                return known;
            }
        }

        try
        {
            var text = await _recorder.ReadTextAsync(id, sha256, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _texts[id] = text;
                _characters[id] = text.Length;
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            lock (_gate)
            {
                _problems.Add($"La contribución {id} no se puede leer contra su hash: {ex.Message}", UserText.Of(DebateUserTexts.ProblemContributionUnreadable, id.ToString()));
            }

            return null;
        }
    }

    /// <summary>The contributions published before <paramref name="round"/>, with their text read against their hashes (D-18: nothing of the round in course).</summary>
    private async Task<IReadOnlyList<PublishedContribution>> PublishedBeforeAsync(DebateLog log, int round, CancellationToken ct)
    {
        var published = new List<PublishedContribution>();
        foreach (var (_, payload) in log.Contributions.Where(c => c.Payload.Round < round).OrderBy(c => c.Payload.Index))
        {
            var text = await ContributionTextAsync(payload.Evidence, payload.Sha256, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"The contribution {ContributionLabels.Of(payload.Index)} cannot be read against its hash: no turn is given a text the log does not vouch for.");
            published.Add(new PublishedContribution(
                ContributionLabels.Of(payload.Index),
                payload.Index,
                payload.Round,
                DebateViews.LabelOf(_context.Opened, payload.Author),
                payload.Kind,
                payload.RepliesTo is { } target ? ContributionLabels.Of(target) : null,
                payload.Sha256,
                text));
        }

        return published;
    }

    /// <summary>
    /// The evidence of the material (D-08): what the debate registered that is neither the question nor a contribution (the
    /// citations, the tests, the notes of a person, a challenged verdict), and the evidence of the dossier of the run it is
    /// about; each with its content when it reads against the hash the log anchored. What a turn left registered is material
    /// only once a published contribution cites it: the evidence of an answer whose publication a stop cut short (its
    /// contribution or its citations, announced before its <c>DebateContributionRecorded</c>) stays sealed (D-17).
    /// </summary>
    private async Task<IReadOnlyList<MaterialEvidence>> MaterialEvidenceAsync(DebateLog log, CancellationToken ct)
    {
        var contributions = log.Contributions.Select(c => c.Payload.Evidence).ToHashSet();
        var cited = log.Contributions.SelectMany(c => c.Payload.Cites ?? []).ToHashSet();
        var registered = log.Evidence
            .Where(e => e.Payload.Evidence != _context.Opened.Question && !contributions.Contains(e.Payload.Evidence) && e.Payload.AnchorsMetadata())
            .Where(e => e.Payload.Task is not { } task || !DebateIds.TryReadTurn(task.Value, out _, out _) || cited.Contains(e.Payload.Evidence))
            .GroupBy(e => e.Payload.Evidence)
            .Select(g => g.First())
            .ToList();
        var material = new List<MaterialEvidence>();
        foreach (var (_, payload) in registered)
        {
            EvidenceRecord record;
            try
            {
                record = payload.ToRecord(null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DomainRuleViolationException)
            {
                continue;
            }

            byte[]? content = null;
            try
            {
                content = await _recorder.ReadBytesAsync(payload.Evidence, payload.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                // Named in the material without a file: it does not read against its hash.
            }

            material.Add(new MaterialEvidence(record, content));
        }

        material.AddRange(_context.SubjectEvidence.Select(e => new MaterialEvidence(e, null)));
        return material;
    }

    // ---- small state helpers ----

    private DebatePhase Phase()
    {
        lock (_gate)
        {
            return _session.Phase;
        }
    }

    private int Contributions()
    {
        lock (_gate)
        {
            return _session.Contributions.Count;
        }
    }

    private bool Aborting()
    {
        lock (_gate)
        {
            return _abortReason is not null;
        }
    }

    /// <summary>
    /// The time ran out with nothing published: the domain cannot send such a debate to judgment (and no turn can answer
    /// any more), so it is aborted with that reason instead of recording a cut nobody can judge. True when it was.
    /// </summary>
    private bool AbortWhenNothingToJudge()
    {
        lock (_gate)
        {
            if (_session.Contributions.Count > 0)
            {
                return false;
            }

            _abortReason ??= "Se agotó el tiempo del debate sin ninguna contribución que juzgar.";
            return true;
        }
    }

    private string? TakeAbort()
    {
        lock (_gate)
        {
            return _abortReason;
        }
    }

    private bool TakeJudgmentRequest()
    {
        lock (_gate)
        {
            var requested = _judgmentRequested;
            _judgmentRequested = false;
            return requested;
        }
    }

    private void EnsureDebating()
    {
        if (_session.Phase is DebatePhase.AwaitingJudgment or DebatePhase.Closed)
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.CannotJudgeNow, $"Judgment cannot be requested: the debate is in {_session.Phase}.");
        }
    }

    private bool TryReserveLaunch()
    {
        while (true)
        {
            var current = Volatile.Read(ref _launches);
            if (current >= _context.Record.MaxAgentRuns)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _launches, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private int NextAttempt(TaskId task)
    {
        lock (_gate)
        {
            var next = _attempts.GetValueOrDefault(task.Value) + 1;
            _attempts[task.Value] = next;
            return next;
        }
    }

    /// <summary>
    /// Why the debate closed, written by the system (docs/04 §17.2: no payload carries text of an agent, and a verdict's
    /// summary is the judge's): the rule that combined the verdicts, how many were valid, the decision and the outcome.
    /// </summary>
    private static string Reason(PanelOutcome outcome, IReadOnlyList<Verdict> valid) =>
        Invariant($"Juicio por la regla {outcome.Rule} con {valid.Count} veredicto(s) válido(s): {outcome.Decision?.ToString() ?? "sin decisión"}, {outcome.Outcome}.");

    private static string LimitText(DebateLimitReached limit) => limit switch
    {
        DebateLimitReached.Rounds => "rondas",
        DebateLimitReached.Duration => "tiempo",
        DebateLimitReached.Tokens => "tokens",
        _ => "nada",
    };

    private DateTimeOffset Now() => _ports.Time.GetUtcNow();

    private void Emit(DebateProgress progress) => _events.Writer.TryWrite(progress);

    private enum TurnEnd
    {
        None,
        Answered,
        Failed,
        Cancelled,
        NoBudget,
        ProviderLimit,
    }

    /// <summary>What every turn of a round is given: the round, what was published before it, the material, the rules and the schema.</summary>
    private sealed record TurnSetting(int Round, IReadOnlyList<PublishedContribution> Published, IReadOnlyList<MaterialEvidence> Material, ContributionRules Rules, string Schema);

    /// <summary>How one launch ended.</summary>
    /// <summary>
    /// How a launch of a turn ended. <paramref name="End"/> is the class its adapter gave the end (docs/16 D-41), null when the
    /// turn never ran: a limit is a limit by its class, whatever the status (a window of Claude rejected without a 429 ends
    /// Failed with the class WindowQuota).
    /// </summary>
    private sealed record Launched(AttemptOutcome Status, string? Answer, string? Error, DateTimeOffset? ResetsAt, DateTimeOffset FinishedAt, AgentRunEnd? End = null)
    {
        /// <summary>A usage limit of the provider: by its status, or by the class of a window that ran out.</summary>
        public bool IsLimit => Status == AttemptOutcome.RateLimited || End?.Class == AttemptEndClass.WindowQuota;

        /// <summary>A provider overloaded, or limiting the rate, that did not end the turn as a usage limit (a 529 of Claude, say).</summary>
        public bool IsOverload => !IsLimit && End?.Class == AttemptEndClass.RateOrOverload;
    }

    /// <summary>One turn of a round: its seat, its task and how it ended; its answer stays here, sealed, until the round closes.</summary>
    private sealed class Turn(DebateSeat seat, int round)
    {
        public DebateSeat Seat { get; } = seat;

        public TaskId Task { get; } = DebateIds.Turn(round, seat.Agent.Id);

        public TurnEnd End { get; set; }

        public ContributionAnswer? Answer { get; set; }

        public int AnswerAttempt { get; set; }

        public DateTimeOffset AnsweredAt { get; set; }

        public string? Failure { get; set; }

        /// <summary>What <see cref="Failure"/> says, as a coded text for the person.</summary>
        public UserText? FailureText { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public bool Published { get; set; }

        public void Reset()
        {
            End = TurnEnd.None;
            Failure = null;
            FailureText = null;
            ResetsAt = null;
        }
    }

    /// <summary>What a person did while the debate waited.</summary>
    private abstract record HumanAction
    {
        public sealed record Retry(AgentId Participant) : HumanAction;

        public sealed record Judge : HumanAction;

        public sealed record Decide(VerdictDecision? Decision, string? ChosenProposal, string Comment, TaskCompletionSource Done) : HumanAction;

        public sealed record Abort : HumanAction;

        public sealed record Deadline : HumanAction;
    }
}
