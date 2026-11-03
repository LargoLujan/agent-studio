using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

/// <summary>
/// Decorator that makes the prompt journal mandatory: every prompt Agent Studio launches to an agent (the
/// orchestrator included) is recorded BEFORE the inner provider is called, and its outcome is recorded when the run
/// completes. If the entry cannot be recorded the agent is not launched (fail closed) and the exception propagates.
/// A failure recording the outcome never hides the agent's result.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 composition rule: real <see cref="IAgentProvider"/> adapters must be registered ONLY through this
/// decorator, never directly, so no prompt can bypass the journal.
/// </para>
/// <para>
/// The tokens of the result line are the last usage the run reported. None at all is «sin dato»
/// (<see cref="MetricOrigin.Missing"/>): every CLI offers it, it just did not come. A run that resumed a session reports
/// the total of the whole session, not its own (docs/14 D-02), so its line says «(acumulado de la sesión)».
/// </para>
/// <para>
/// The note of the result line also says the class of the end (docs/16 D-41) when the adapter recognised one that says more
/// than the status: <c>clase WindowQuota five_hour, reinicio 2026-10-15 17:20 UTC</c>, <c>clase AttemptBudget Turns</c>,
/// <c>clase ContextExhausted</c>… An end with the class its status implies by itself (a success, a cancellation, a failure
/// nobody recognised) adds nothing, so the line of every such end is written as before.
/// </para>
/// <para>
/// When the request carries the <c>AgentRunStarted</c> it was announced with (<see cref="AgentRunRequest.Anchor"/>), the
/// line of the entry names it with its day and the result line ends with its run, task, attempt and entry
/// (<see cref="JournalResultAnchor"/>, docs/17 D-02), even when the launch itself failed. When the run ends, the receipt
/// of the entry the journal gives (<see cref="JournalReceipt"/>, the SHA-256 of the exact text it wrote) goes with the
/// outcome (<see cref="AgentRunOutcome.Journal"/>), so whoever records the end keeps it in <c>AgentRunFinished</c>: the
/// derivation of the journal as memory gives <c>System</c> only to the entry with that text. A launch that fails to start
/// has no outcome to carry it, so its entry is never matched.
/// </para>
/// </remarks>
public sealed class JournalingAgentProvider(
    IAgentProvider inner,
    IPromptJournal journal,
    TimeProvider timeProvider,
    TimeSpan? eventDrainGrace = null) : IAgentProvider
{
    /// <summary>How long to wait for the inner event stream to finish after the run completes.</summary>
    private readonly TimeSpan _eventDrainGrace = eventDrainGrace ?? TimeSpan.FromSeconds(5);

    private const int MaxSummaryLength = 120;

    /// <summary>The note of a result line whose tokens are the total of a resumed session (docs/14 D-02).</summary>
    public const string SessionTotalNote = "(acumulado de la sesión)";

    /// <summary>The start of the part of the note of a result line that says the class of the end (docs/16 D-41).</summary>
    public const string EndClassNotePrefix = "clase ";

    /// <summary>The prefix of the id of a debate (docs/15 D-01, <c>deb-yyyyMMdd-xxxxxx</c>), whose runs the journal tags <c>#debate</c>.</summary>
    public const string DebatePrefix = "deb-";

    /// <summary>The prefix of the id of a whole-project audit (<c>aud-yyyyMMdd-xxxxxx</c>), whose runs the journal tags <c>#auditoria</c>.</summary>
    public const string AuditPrefix = "aud-";

    /// <summary>The provider the journal decorates (an adapter, or the quota observation around it).</summary>
    public IAgentProvider Inner => inner;

    public ProviderId Provider => inner.Provider;

    public VendorId Vendor => inner.Vendor;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = timeProvider.GetUtcNow();
        var tags = new List<string>(request.Tags ?? []);
        if (UnitTag(request.DebateId) is { } unit && !tags.Contains(unit, StringComparer.Ordinal))
        {
            tags.Add(unit);
        }

        tags.Add(AgentTag(request));
        var entry = new PromptJournalEntry(
            startedAt,
            request.Agent.Name,
            inner.Provider,
            request.Agent.Model.Model,
            Summarize(request),
            tags,
            request.TaskId,
            request.DebateId)
        {
            Anchor = request.Anchor,
        };

        // Fail closed: if this throws, the agent is never started.
        var id = await journal.RecordPromptAsync(entry, cancellationToken).ConfigureAwait(false);

        IAgentRun run;
        try
        {
            run = await inner.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var status = ex is OperationCanceledException ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            await TryRecordOutcomeAsync(id, startedAt, status, null, ex.Message, resumed: false, request.Anchor).ConfigureAwait(false);
            throw;
        }

        return new JournaledRun(this, run, id, startedAt, resumed: !string.IsNullOrWhiteSpace(request.ResumeSessionId), request.Anchor);
    }

    private static string AgentTag(AgentRunRequest request) =>
        "#agente-" + request.Agent.Id.Value.Trim().ToLowerInvariant().Replace(' ', '-');

    /// <summary>The tag of the unit a run belongs to (docs/15 D-45): <c>#debate</c> or <c>#auditoria</c>; none outside them.</summary>
    private static string? UnitTag(string? unit) => unit switch
    {
        null => null,
        _ when unit.StartsWith(DebatePrefix, StringComparison.Ordinal) => "#debate",
        _ when unit.StartsWith(AuditPrefix, StringComparison.Ordinal) => "#auditoria",
        _ => null,
    };

    private static string Summarize(AgentRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Purpose))
        {
            return request.Purpose.Trim();
        }

        var firstLine = (request.Prompt ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return firstLine.Length > MaxSummaryLength ? firstLine[..MaxSummaryLength].TrimEnd() : firstLine;
    }

    /// <summary>
    /// The class of the end as the result line says it, or null when it is the one <paramref name="status"/> implies by
    /// itself (docs/16 D-41): a token of the class, then the window and its reset, or which budget.
    /// </summary>
    private static string? EndClassNote(AgentRunStatus status, AgentRunEnd? end)
    {
        if (end is null || end == AgentRunEnd.Implied(status))
        {
            return null;
        }

        var text = new StringBuilder(EndClassNotePrefix).Append(end.Class);
        if (end.LimitType is { } window)
        {
            text.Append(' ').Append(window);
        }

        if (end.Budget is { } budget)
        {
            text.Append(' ').Append(budget);
        }

        if (end.ResetsAt is { } resetsAt)
        {
            text.Append(", reinicio ").Append(resetsAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Records the outcome of the entry; returns its receipt, or null (none, or the journal could not write it).</summary>
    private async Task<JournalReceipt?> TryRecordOutcomeAsync(
        JournalEntryId id,
        DateTimeOffset startedAt,
        AgentRunStatus status,
        TokenUsage? usage,
        string? note,
        bool resumed,
        AgentRunAnchor? anchor,
        AgentRunEnd? end = null)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var missing = MetricValue<long>.Missing("no reportado");
            var input = usage?.InputTokens ?? missing;
            var output = usage?.OutputTokens ?? missing;
            List<string> parts = [];
            if (resumed && (input.HasValue || output.HasValue))
            {
                parts.Add(SessionTotalNote);
            }

            if (EndClassNote(status, end) is { } endClass)
            {
                parts.Add(endClass);
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                parts.Add(note);
            }

            note = parts.Count == 0 ? note : string.Join(" · ", parts);
            var outcome = new PromptOutcome(now, status, now - startedAt, input, output, note) { Anchor = anchor };

            // The outcome must be written even when the caller cancelled the run.
            return await journal.RecordReceiptedOutcomeAsync(id, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Prompt journal: could not record the outcome of {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Wraps the inner run. It consumes the inner <c>Events</c> itself from the start (pumping them into an
    /// unbounded channel) so the last reported usage is always known, whether or not the caller reads the events.
    /// </summary>
    /// <remarks>
    /// <see cref="Events"/> can be enumerated only once, by a single consumer; events are buffered until read.
    /// </remarks>
    private sealed class JournaledRun : IAgentRun
    {
        private const string DiscardedNote = "descartado antes de terminar";

        private readonly JournalingAgentProvider _owner;
        private readonly IAgentRun _run;
        private readonly JournalEntryId _id;
        private readonly DateTimeOffset _startedAt;
        private readonly bool _resumed;
        private readonly AgentRunAnchor? _anchor;
        private readonly Lock _usageLock = new();
        private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private readonly CancellationTokenSource _pumpCancellation = new();
        private readonly Task _pump;
        private TokenUsage? _lastUsage;
        private AgentRunOutcome? _finishedOutcome;
        private int _outcomeRecorded;

        public JournaledRun(JournalingAgentProvider owner, IAgentRun run, JournalEntryId id, DateTimeOffset startedAt, bool resumed, AgentRunAnchor? anchor)
        {
            _owner = owner;
            _run = run;
            _id = id;
            _startedAt = startedAt;
            _resumed = resumed;
            _anchor = anchor;
            _pump = Task.Run(PumpAsync);
            Completion = ObserveCompletionAsync();
        }

        public string RunId => _run.RunId;

        public IAsyncEnumerable<AgentEvent> Events => _channel.Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion { get; }

        public Task CancelAsync() => _run.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            // The run may have finished without the caller awaiting Completion (e.g. it read Events to the end): then
            // its real outcome is journaled. The wait is bounded; the wrapper's own observer normally records it.
            var finished = Volatile.Read(ref _finishedOutcome);
            if (_run.Completion.IsCompleted || finished is not null)
            {
                try
                {
                    await Completion.WaitAsync(_owner._eventDrainGrace).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Timeout, or a faulted/cancelled run (already journaled by the completion observer).
                }

                finished = Volatile.Read(ref _finishedOutcome);
            }

            // Otherwise it was discarded before finishing: the outcome is written once, as cancelled.
            if (finished is not null)
            {
                await RecordOnceAsync(finished.Status, finished.Error, finished.End).ConfigureAwait(false);
            }
            else
            {
                await RecordOnceAsync(AgentRunStatus.Cancelled, DiscardedNote).ConfigureAwait(false);
            }

            await _pumpCancellation.CancelAsync().ConfigureAwait(false);
            await _run.DisposeAsync().ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var e in _run.Events.WithCancellation(_pumpCancellation.Token).ConfigureAwait(false))
                {
                    if (e is UsageReported usage)
                    {
                        lock (_usageLock)
                        {
                            _lastUsage = usage.Usage;
                        }
                    }

                    if (e is RunFinished runFinished)
                    {
                        Volatile.Write(ref _finishedOutcome, runFinished.Outcome);
                    }

                    _channel.Writer.TryWrite(e);
                }

                _channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Surface the failure to the consumer; the pump task itself never faults.
                _channel.Writer.TryComplete(ex);
            }
        }

        private async Task<AgentRunOutcome> ObserveCompletionAsync()
        {
            AgentRunOutcome result;
            try
            {
                result = await _run.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await WaitForPumpAsync().ConfigureAwait(false);
                await RecordOnceAsync(AgentRunStatus.Cancelled, null).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await WaitForPumpAsync().ConfigureAwait(false);
                await RecordOnceAsync(AgentRunStatus.Failed, ex.Message).ConfigureAwait(false);
                throw;
            }

            // Wait for the pump so the last usage is final before the outcome is written. The receipt of the entry goes
            // with the outcome, for whoever records the end of the launch (docs/17 D-02).
            await WaitForPumpAsync().ConfigureAwait(false);
            var receipt = await RecordOnceAsync(result.Status, result.Error, result.End).ConfigureAwait(false);
            return receipt is null ? result : result with { Journal = receipt };
        }

        /// <summary>
        /// Providers must complete <see cref="IAgentRun.Events"/> when the run ends; a faulty one must not leave the
        /// outcome unrecorded forever, so the wait is bounded and the outcome uses the usage seen so far.
        /// </summary>
        private async Task WaitForPumpAsync()
        {
            try
            {
                await _pump.WaitAsync(_owner._eventDrainGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"Prompt journal: events of {_id} did not finish within {_owner._eventDrainGrace}.");
            }
        }

        private async Task<JournalReceipt?> RecordOnceAsync(AgentRunStatus status, string? note, AgentRunEnd? end = null)
        {
            if (Interlocked.Exchange(ref _outcomeRecorded, 1) != 0)
            {
                return null;
            }

            TokenUsage? usage;
            lock (_usageLock)
            {
                usage = _lastUsage;
            }

            return await _owner.TryRecordOutcomeAsync(_id, _startedAt, status, usage, note, _resumed, _anchor, end).ConfigureAwait(false);
        }
    }
}
