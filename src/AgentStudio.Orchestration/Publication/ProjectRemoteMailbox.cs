using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Orchestration.Publication;

/// <summary>What a mailbox needs of the rest of the app (docs/21 D-25). <paramref name="Policy"/> is optional: without one the network is never allowed in the background.</summary>
internal sealed record MailboxServices(
    IDecisionRecorder Recorder,
    IProjectActivity Activity,
    IHostingProviders Providers,
    IRemoteSyncStore Store,
    IRemoteSyncPolicy? Policy,
    TimeProvider Time,
    Action<string> Changed);

/// <summary>
/// The mailbox of one project (docs/21 D-25): a <see cref="Channel{T}"/> with one consumer, so that everything that touches the remote of the project (the poll, the work of the
/// person, the annotations in the log) happens one thing at a time and in order, without locks. The races are solved by the queue.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Three kinds of message:</b> observe a pull request, run a piece of work of the person (a <see cref="RemoteFlow"/>) and annotate in the log. A few more are only the plumbing
/// of those (the tick of the timer, a cancel, following a pull request).</item>
/// <item><b>The project is taken only to touch git or the log</b> (<see cref="Take"/>, with <see cref="ProjectActivityKind.RemoteOperation"/>), never to talk to the platform. When it is
/// held by something else the message is <b>parked</b>, not waited for: the consumer goes on with the next one, so the poll by the CLI continues during a run and an action of the person
/// goes first. What is parked is tried again by a timer every 500 ms, because <see cref="IProjectActivity"/> has no events; the observations are melted by pull request (the last one
/// stays) and the order of one pull request is kept.</item>
/// <item><b>The consumer never sleeps:</b> the courtesy second after letting the project go is a «not before» of <see cref="Take"/>; the message that finds it parks and the timer picks it up.</item>
/// <item><b>The log is written with <see cref="IDecisionRecorder"/> directly</b>, with the project taken, and only from what the platform just said: nothing in the local files is ever
/// written to the log.</item>
/// <item><b>When the app closes</b> the current stretch ends (a <c>push</c> is never cut: git runs with no token that can be cancelled) and the mailbox stops.</item>
/// </list>
/// </remarks>
internal sealed class ProjectRemoteMailbox : IAsyncDisposable
{
    private static readonly RemoteChangeKind[] OnlyChecksOrReviews = [RemoteChangeKind.ChecksChanged, RemoteChangeKind.ReviewsChanged];

    private readonly MailboxServices _services;
    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _consumer;
    private readonly FlowContext _context;
    private readonly ConcurrentDictionary<string, RemoteWorkStatus> _statuses = new(StringComparer.Ordinal);
    private readonly List<Parked> _parked = [];
    private readonly Dictionary<string, FollowedPullRequest> _followed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteSnapshot> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPolled = new(StringComparer.Ordinal);
    private readonly List<PendingRemoteWork> _restored = [];
    private readonly ITimer? _pollTimer;
    private ITimer? _retryTimer;
    private int _retryQueued;
    private int _inFlight;
    private volatile bool _stopped;
    private bool _dirty;
    private RemoteRepository? _trusted;
    private RemoteRepository? _hint;
    private DateTimeOffset _noTakeBefore = DateTimeOffset.MinValue;
    private DateTimeOffset? _offlineUntil;
    private int _offlineStep;
    private bool _signInStopped;
    private RemoteSyncHealth _health = new(null, null, null);

    public ProjectRemoteMailbox(ProjectContext project, MailboxServices services, RemoteSyncState restored)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(restored);
        Project = project;
        _services = services;
        _context = new FlowContext(this);
        _hint = restored.Repository;
        var now = services.Time.GetUtcNow();

        // What the file says is read as not trusted: the cursors start without ETags (the first round asks without a condition, so the composed snapshot is the platform's), the
        // wait of the platform is cut to its cap, and an observation parked before the close is not written: it is asked for again.
        foreach (var follow in restored.Followed.Take(RemoteSyncLimits.MaxFollowed))
        {
            _followed[follow.PullRequest.TraceId] = follow with { Cursor = new ObservationCursor(new Dictionary<ObservedResource, string>(), follow.Cursor.LastSnapshot) };
        }

        var until = restored.RateLimitedUntil is { } wait && wait > now ? (wait > now + PublicationCodes.RateLimitCap ? now + PublicationCodes.RateLimitCap : wait) : (DateTimeOffset?)null;
        _health = new RemoteSyncHealth(restored.LastGoodSync, restored.LastOutcome, until, restored.LastOutcomeAt);
        if (until is not null && restored.Repository is { } repository && services.Providers.For(repository) is { } provider)
        {
            provider.RestoreRateLimit(until);
        }

        var toObserve = new List<PullRequestRef>();
        foreach (var work in restored.Pending.Take(RemoteSyncLimits.MaxPending))
        {
            if (now - work.Since > PublicationCodes.PendingLife)
            {
                continue;
            }

            if (work.Observation is { } observation)
            {
                toObserve.Add(observation.PullRequest);
            }
            else
            {
                // An action that had not happened: it is listed (and expires or is cancelled), never run again by itself after a restart.
                _restored.Add(work);
                _statuses[work.Ticket.Id] = new RemoteWorkStatus(work.Ticket, RemoteWorkState.PendingSync, work.Reason, work.RetryAt, null);
            }
        }

        _consumer = Task.Run(ConsumeAsync);
        foreach (var pullRequest in toObserve.DistinctBy(p => p.TraceId))
        {
            Post(new ObserveMessage(pullRequest, false));
        }

        if (services.Policy is { } policy && policy.Interval > TimeSpan.Zero)
        {
            _pollTimer = services.Time.CreateTimer(_ => PostQuiet(new PollMessage()), null, policy.Interval, policy.Interval);
        }
    }

    public ProjectContext Project { get; }

    public IDecisionRecorder Recorder => _services.Recorder;

    public TimeProvider Time => _services.Time;

    public CancellationToken Stopping => _stopping.Token;

    /// <summary>The app is closing: a flow that sees it ends its stretch and leaves the rest pending (a stretch of git is never cut).</summary>
    public bool Closing => _stopped;

    public RemoteSyncHealth Health => Volatile.Read(ref _health);

    public IReadOnlyList<RemoteWorkStatus> Pending() =>
        [.. _statuses.Values.Where(s => s.State is RemoteWorkState.Queued or RemoteWorkState.WaitingForProject or RemoteWorkState.Running or RemoteWorkState.PendingSync).OrderBy(s => s.Ticket.Id, StringComparer.Ordinal)];

    /// <summary>Every status the mailbox knows, for the tests and for a screen that wants the finished ones too.</summary>
    public RemoteWorkStatus? StatusOf(string ticketId) => _statuses.GetValueOrDefault(ticketId);

    /// <summary>Returns once nothing is queued or running (the parked work waits for its timer and does not count). A test calls it after it moved the clock, never a wall clock.</summary>
    public async Task WhenIdleAsync()
    {
        for (var spin = 0; Volatile.Read(ref _inFlight) != 0; spin++)
        {
            if (spin > 20_000_000)
            {
                throw new TimeoutException("The mailbox never went idle.");
            }

            await Task.Yield();
        }
    }

    /// <summary>How many things are parked, waiting for the project or for a moment to try again.</summary>
    public int ParkedCount => Volatile.Read(ref _parkedCount);

    private int _parkedCount;

    // ---- what the callers ask (any thread): each one only enqueues ----

    /// <summary>Enqueues a piece of work of the person. The task ends with its ticket when its first stretch ended (done, failed or parked).</summary>
    public Task<RemoteWorkTicket> RunAsync(RemoteFlow flow)
    {
        var first = new TaskCompletionSource<RemoteWorkTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statuses[flow.Ticket.Id] = new RemoteWorkStatus(flow.Ticket, RemoteWorkState.Queued, null, null, null);
        Post(new FlowMessage(flow, first));
        return first.Task;
    }

    /// <summary>Runs something on the consumer, in its turn, with the context of a flow (to look at the project with the lease in hand and give back what it saw).</summary>
    public Task<T> ExecAsync<T>(Func<FlowContext, Task<T>> work)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ExecMessage(async context => await work(context).ConfigureAwait(false), done));
        return done.Task.ContinueWith(t => (T)t.GetAwaiter().GetResult()!, TaskScheduler.Default);
    }

    /// <summary>Annotates in the log from outside the consumer: true when it was written, false when the project was not free and it is parked (D-16: it does not count until then).</summary>
    public Task<bool> NoteFromOutsideAsync(string key, Func<Task<bool>> record)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new NoteMessage(key, record, done));
        return done.Task;
    }

    public Task SyncNowAsync() => Signal(done => new SyncMessage(done));

    public Task FollowAsync(PullRequestRef pullRequest, string? run) => Signal(done => new FollowMessage(pullRequest, run, done));

    public Task UnfollowAsync(PullRequestRef pullRequest) => Signal(done => new UnfollowMessage(pullRequest, done));

    public Task CancelAsync(string ticketId) => Signal(done => new CancelMessage(ticketId, done));

    private Task Signal(Func<TaskCompletionSource, Message> message)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(message(done));
        return done.Task;
    }

    private void Post(Message message)
    {
        Interlocked.Increment(ref _inFlight);
        if (!_channel.Writer.TryWrite(message))
        {
            Interlocked.Decrement(ref _inFlight);
            throw new ObjectDisposedException(nameof(ProjectRemoteMailbox), "The mailbox of the project is closed.");
        }
    }

    private void PostQuiet(Message message)
    {
        // A tick of a timer: it is never an error that the mailbox closed meanwhile; and one tick waits at most.
        if (message is RetryMessage && Interlocked.Exchange(ref _retryQueued, 1) == 1)
        {
            return;
        }

        try
        {
            Post(message);
        }
        catch (ObjectDisposedException)
        {
            // closed
        }
    }

    // ---- what the flows use (only on the consumer) ----

    /// <summary>See <see cref="FlowContext.Take"/>.</summary>
    public IDisposable? Take()
    {
        if (_services.Time.GetUtcNow() < _noTakeBefore)
        {
            return null;
        }

        return _services.Activity.TryEnter(Project.ProjectId, ProjectActivityKind.RemoteOperation) is { } lease ? new Lease(this, lease) : null;
    }

    private sealed class Lease(ProjectRemoteMailbox mailbox, IDisposable inner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                inner.Dispose();
                mailbox._noTakeBefore = mailbox._services.Time.GetUtcNow() + PublicationCodes.Courtesy;
            }
        }
    }

    public void Trust(RemoteRepository repository)
    {
        _trusted = repository;
        _hint = repository;
        _dirty = true;
    }

    public bool FollowNow(PullRequestRef pullRequest, string? run)
    {
        if (_trusted is null || pullRequest.Repository != _trusted)
        {
            return false;
        }

        if (!_followed.ContainsKey(pullRequest.TraceId))
        {
            _followed[pullRequest.TraceId] = new FollowedPullRequest(pullRequest, run, new ObservationCursor(new Dictionary<ObservedResource, string>(), null), RemoteDisplayTexts.None, null);
            _dirty = true;
        }

        return true;
    }

    public void NoteOutcome(HostingOutcome outcome)
    {
        var now = _services.Time.GetUtcNow();
        var health = Health;
        _health = health with
        {
            LastOutcome = outcome,
            LastOutcomeAt = now,
            LastGoodSync = outcome is HostingOutcome.Ok or HostingOutcome.NotModified ? now : health.LastGoodSync,
        };
        if (outcome is HostingOutcome.Ok or HostingOutcome.NotModified)
        {
            _offlineStep = 0;
            _offlineUntil = null;
        }
        else if (outcome == HostingOutcome.Unreachable)
        {
            _offlineUntil = now + PublicationCodes.OfflineWait(_offlineStep);
            _offlineStep++;
        }
        else if (outcome == HostingOutcome.SignInNeeded)
        {
            _signInStopped = true;
        }

        _dirty = true;
    }

    public async Task<bool> NoteAsync(string key, Func<Task<bool>> record)
    {
        if (_parked.Any(p => p.Key == key))
        {
            Park(Parked.OfNote(key, record, _services.Time.GetUtcNow()));
            return false;
        }

        if (await record().ConfigureAwait(false))
        {
            return true;
        }

        Park(Parked.OfNote(key, record, _services.Time.GetUtcNow()));
        return false;
    }

    /// <summary>Observes a pull request now (after a merge or a close that may or may not have happened) and annotates what was seen; the snapshot, or null when the platform did not answer.</summary>
    public async Task<RemoteSnapshot?> ObserveNowAsync(PullRequestRef pullRequest) => await ObserveAsync(pullRequest, manual: true).ConfigureAwait(false);

    // ---- the consumer ----

    private async Task ConsumeAsync()
    {
        await foreach (var message in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_stopped)
                {
                    Abandon(message);
                }
                else
                {
                    await HandleAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(message, ex);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            await SaveIfDirtyAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(Message message)
    {
        switch (message)
        {
            case FlowMessage flow:
                await StartFlowAsync(flow.Flow, flow.First).ConfigureAwait(false);
                break;
            case ExecMessage exec:
                exec.Done.TrySetResult(await exec.Work(_context).ConfigureAwait(false));
                break;
            case NoteMessage note:
                note.Done.TrySetResult(await NoteAsync(note.Key, note.Record).ConfigureAwait(false));
                break;
            case ObserveMessage observe:
                await ObserveAsync(observe.PullRequest, observe.Manual).ConfigureAwait(false);
                break;
            case PollMessage:
                await PollAsync().ConfigureAwait(false);
                break;
            case SyncMessage sync:
                _signInStopped = false;
                _offlineUntil = null;
                foreach (var follow in _followed.Values.ToList())
                {
                    await ObserveAsync(follow.PullRequest, manual: true).ConfigureAwait(false);
                }

                sync.Done.TrySetResult();
                break;
            case FollowMessage follow:
                await FollowFromOutsideAsync(follow).ConfigureAwait(false);
                break;
            case UnfollowMessage unfollow:
                _followed.Remove(unfollow.PullRequest.TraceId);
                _recorded.Remove(unfollow.PullRequest.TraceId);
                _dirty = true;
                unfollow.Done.TrySetResult();
                break;
            case CancelMessage cancel:
                Cancel(cancel.Id);
                cancel.Done.TrySetResult();
                break;
            case RetryMessage:
                Volatile.Write(ref _retryQueued, 0);
                await RetryParkedAsync().ConfigureAwait(false);
                break;
        }
    }

    private void Fail(Message message, Exception ex)
    {
        var failure = UserText.Of(PublicationCodes.Internal);
        switch (message)
        {
            case FlowMessage flow:
                SetStatus(flow.Flow.Ticket, RemoteWorkState.Failed, null, null, failure);
                flow.First.TrySetResult(flow.Flow.Ticket);
                break;
            case ExecMessage exec:
                exec.Done.TrySetException(ex);
                break;
            case NoteMessage note:
                note.Done.TrySetException(ex);
                break;
            case SyncMessage sync:
                sync.Done.TrySetResult();
                break;
            case FollowMessage follow:
                follow.Done.TrySetResult();
                break;
            case UnfollowMessage unfollow:
                unfollow.Done.TrySetResult();
                break;
            case CancelMessage cancel:
                cancel.Done.TrySetResult();
                break;
        }
    }

    private void Abandon(Message message)
    {
        var closed = new OperationCanceledException("The mailbox of the project is closed.");
        switch (message)
        {
            case FlowMessage flow:
                SetStatus(flow.Flow.Ticket, RemoteWorkState.Cancelled, null, null, null);
                flow.First.TrySetResult(flow.Flow.Ticket);
                break;
            case ExecMessage exec:
                exec.Done.TrySetException(closed);
                break;
            case NoteMessage note:
                note.Done.TrySetException(closed);
                break;
            case SyncMessage sync:
                sync.Done.TrySetResult();
                break;
            case FollowMessage follow:
                follow.Done.TrySetResult();
                break;
            case UnfollowMessage unfollow:
                unfollow.Done.TrySetResult();
                break;
            case CancelMessage cancel:
                cancel.Done.TrySetResult();
                break;
        }
    }

    // ---- flows ----

    private async Task StartFlowAsync(RemoteFlow flow, TaskCompletionSource<RemoteWorkTicket> first)
    {
        var now = _services.Time.GetUtcNow();
        try
        {
            if (_parked.Any(p => p.Key == flow.Key))
            {
                // The order of one pull request is kept: behind what is already waiting for it.
                Park(Parked.OfFlow(flow, now, null, PendingReason.ProjectBusy));
                SetStatus(flow.Ticket, RemoteWorkState.WaitingForProject, PendingReason.ProjectBusy, null, null);
                return;
            }

            SetStatus(flow.Ticket, RemoteWorkState.Running, null, null, null);
            var result = await flow.RunAsync(_context).ConfigureAwait(false);
            Apply(flow, result, now, parked: null);
        }
        finally
        {
            first.TrySetResult(flow.Ticket);
        }
    }

    private void Apply(RemoteFlow flow, FlowResult result, DateTimeOffset since, Parked? parked)
    {
        var now = _services.Time.GetUtcNow();
        switch (result.End)
        {
            case FlowEnd.Done:
                SetStatus(flow.Ticket, RemoteWorkState.Done, null, null, null);
                Unpark(parked);
                break;
            case FlowEnd.Failed:
                SetStatus(flow.Ticket, RemoteWorkState.Failed, null, null, result.Message);
                Unpark(parked);
                break;
            case FlowEnd.WaitProject:
                SetStatus(flow.Ticket, RemoteWorkState.WaitingForProject, PendingReason.ProjectBusy, null, result.Message);
                KeepParked(parked ?? Parked.OfFlow(flow, since, null, PendingReason.ProjectBusy), null, PendingReason.ProjectBusy);
                break;
            default:
                var retryAt = now + (result.RetryIn ?? PublicationCodes.OfflineWait(0));
                SetStatus(flow.Ticket, RemoteWorkState.PendingSync, result.Reason, retryAt, result.Message);
                KeepParked(parked ?? Parked.OfFlow(flow, since, retryAt, result.Reason ?? PendingReason.Offline), retryAt, result.Reason ?? PendingReason.Offline);
                break;
        }
    }

    private void SetStatus(RemoteWorkTicket ticket, RemoteWorkState state, PendingReason? reason, DateTimeOffset? retryAt, UserText? message)
    {
        _statuses[ticket.Id] = new RemoteWorkStatus(ticket, state, reason, retryAt, message);
        _dirty = true;
        _services.Changed(Project.ProjectId);
    }

    // ---- parked work ----

    private void Park(Parked item)
    {
        _parked.Add(item);
        Volatile.Write(ref _parkedCount, _parked.Count);
        _dirty = true;
        _retryTimer ??= _services.Time.CreateTimer(_ => PostQuiet(new RetryMessage()), null, PublicationCodes.RetryInterval, PublicationCodes.RetryInterval);
    }

    private void KeepParked(Parked item, DateTimeOffset? notBefore, PendingReason reason)
    {
        item.NotBefore = notBefore;
        item.Reason = reason;
        if (!_parked.Contains(item))
        {
            Park(item);
        }

        _dirty = true;
    }

    private void Unpark(Parked? item)
    {
        if (item is not null)
        {
            _parked.Remove(item);
            Volatile.Write(ref _parkedCount, _parked.Count);
            _dirty = true;
        }

        if (_parked.Count == 0 && _retryTimer is not null)
        {
            _retryTimer.Dispose();
            _retryTimer = null;
        }
    }

    private async Task RetryParkedAsync()
    {
        var now = _services.Time.GetUtcNow();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _parked.ToList())
        {
            if (now - item.Since > PublicationCodes.PendingLife)
            {
                if (item.Flow is { } expired)
                {
                    SetStatus(expired.Ticket, RemoteWorkState.Expired, null, null, UserText.Of(PublicationCodes.Expired));
                }

                Unpark(item);
                continue;
            }

            if (blocked.Contains(item.Key))
            {
                continue;
            }

            if (item.NotBefore is { } notBefore && notBefore > now)
            {
                blocked.Add(item.Key);
                continue;
            }

            if (item.Flow is { } flow)
            {
                SetStatus(flow.Ticket, RemoteWorkState.Running, null, null, null);
                try
                {
                    Apply(flow, await flow.RunAsync(_context).ConfigureAwait(false), item.Since, item);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    SetStatus(flow.Ticket, RemoteWorkState.Failed, null, null, UserText.Of(PublicationCodes.Internal));
                    Unpark(item);
                }

                if (_parked.Contains(item))
                {
                    blocked.Add(item.Key);
                }
            }
            else if (item.Note is { } note)
            {
                if (await note().ConfigureAwait(false))
                {
                    Unpark(item);
                }
                else
                {
                    blocked.Add(item.Key);
                }
            }
            else if (item.Observation is { } observation)
            {
                var step = await AnnotateObservationAsync(observation, item.Run).ConfigureAwait(false);
                if (step)
                {
                    Unpark(item);
                }
                else
                {
                    blocked.Add(item.Key);
                }
            }
        }

        if (_parked.Count == 0 && _retryTimer is not null)
        {
            _retryTimer.Dispose();
            _retryTimer = null;
        }

        Volatile.Write(ref _parkedCount, _parked.Count);
    }

    private void Cancel(string ticketId)
    {
        foreach (var item in _parked.Where(p => p.Flow?.Ticket.Id == ticketId).ToList())
        {
            Unpark(item);
        }

        if (_restored.RemoveAll(r => r.Ticket.Id == ticketId) > 0 || _statuses.ContainsKey(ticketId))
        {
            if (_statuses.TryGetValue(ticketId, out var status) && status.State is RemoteWorkState.Queued or RemoteWorkState.WaitingForProject or RemoteWorkState.PendingSync)
            {
                SetStatus(status.Ticket, RemoteWorkState.Cancelled, null, null, null);
            }
        }
    }

    // ---- following and the poll ----

    private async Task FollowFromOutsideAsync(FollowMessage follow)
    {
        var pullRequest = follow.PullRequest;
        var followed = await NoteAsync(pullRequest.TraceId, async () =>
        {
            if (!await EnsureRepositoryAsync().ConfigureAwait(false))
            {
                return false;
            }

            FollowNow(pullRequest, follow.Run);
            return true;
        }).ConfigureAwait(false);
        _ = followed;
        follow.Done.TrySetResult();
    }

    /// <summary>
    /// The repository of the project, with the project in hand: the poll does not launch git, so the identifier of the remote comes from a stretch of this session that did (a publication, or this
    /// one). A repository that only the local file names is a hint and never what decides which pull requests count as of the project.
    /// </summary>
    private async Task<bool> EnsureRepositoryAsync()
    {
        if (_trusted is not null)
        {
            return true;
        }

        using var lease = Take();
        if (lease is null)
        {
            return false;
        }

        var repository = await _services.Providers.IdentifyAsync(Project, "origin", CancellationToken.None).ConfigureAwait(false);
        if (repository is null)
        {
            return true;
        }

        Trust(repository);
        return true;
    }

    private async Task PollAsync()
    {
        if (_services.Policy?.PeriodicSyncAllowed(Project.ProjectId) != true || _signInStopped)
        {
            return;
        }

        var now = _services.Time.GetUtcNow();
        if (_offlineUntil is { } until && until > now)
        {
            return;
        }

        foreach (var follow in _followed.Values.OrderBy(f => f.PullRequest.TraceId, StringComparer.Ordinal).ToList())
        {
            var last = _recorded.GetValueOrDefault(follow.PullRequest.TraceId) ?? follow.Cursor.LastSnapshot;
            if (last is { State: PullRequestState.Merged or PullRequestState.Closed })
            {
                if (now - last.UpdatedAt > PublicationCodes.FinishedWatch)
                {
                    _followed.Remove(follow.PullRequest.TraceId);
                    _dirty = true;
                    continue;
                }

                if (_lastPolled.TryGetValue(follow.PullRequest.TraceId, out var polled) && now - polled < PublicationCodes.FinishedInterval)
                {
                    continue;
                }
            }

            _lastPolled[follow.PullRequest.TraceId] = now;
            await ObserveAsync(follow.PullRequest, manual: false).ConfigureAwait(false);
            if (_signInStopped || (_offlineUntil is { } wait && wait > _services.Time.GetUtcNow()))
            {
                return;
            }
        }
    }

    private async Task<RemoteSnapshot?> ObserveAsync(PullRequestRef pullRequest, bool manual)
    {
        if (_stopped)
        {
            return null;
        }

        if (_trusted is null && !await EnsureRepositoryAsync().ConfigureAwait(false))
        {
            return null;
        }

        // Only the pull requests of the repository of the project count.
        if (_trusted is null || pullRequest.Repository != _trusted || _services.Providers.For(pullRequest.Repository) is not { } provider)
        {
            return null;
        }

        var now = _services.Time.GetUtcNow();
        if (provider.RateLimitedUntil is { } limited && limited > now)
        {
            SyncRateLimit(provider);
            return null;
        }

        if (!manual && (_signInStopped || (_offlineUntil is { } until && until > now)))
        {
            return null;
        }

        _followed.TryGetValue(pullRequest.TraceId, out var follow);
        var result = await provider.ObserveAsync(pullRequest, follow?.Cursor, _stopping.Token).ConfigureAwait(false);
        NoteOutcome(result.Outcome);
        SyncRateLimit(provider);
        if (result.Outcome != HostingOutcome.Ok || result.Value is not { } observation || observation.Snapshot.PullRequest != pullRequest)
        {
            return null;
        }

        try
        {
            observation.Snapshot.EnsureValid();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (follow is not null)
        {
            _followed[pullRequest.TraceId] = follow with { Cursor = observation.Cursor, Display = observation.Display };
            _dirty = true;
        }

        await AnnotateObservationOrParkAsync(observation.Snapshot, follow?.Run).ConfigureAwait(false);
        return observation.Snapshot;
    }

    private void SyncRateLimit(IHostingProvider provider)
    {
        var now = _services.Time.GetUtcNow();
        var until = provider.RateLimitedUntil;
        if (until is { } value && value > now + PublicationCodes.RateLimitCap)
        {
            until = now + PublicationCodes.RateLimitCap;
        }

        if (until is { } later && later <= now)
        {
            until = null;
        }

        if (Health.RateLimitedUntil != until)
        {
            _health = Health with { RateLimitedUntil = until };
            _dirty = true;
        }
    }

    // ---- the annotation of an observation (docs/21 D-23 and D-25) ----

    private async Task AnnotateObservationOrParkAsync(RemoteSnapshot snapshot, string? run)
    {
        var key = snapshot.PullRequest.TraceId;
        var waiting = _parked.FirstOrDefault(p => p.Observation is not null && p.Key == key);
        if (waiting is not null)
        {
            // Melted by pull request: the later of the two stays, and it keeps the place of the first.
            if (RemoteSnapshotOrder.Compare(snapshot, waiting.Observation!) >= 0)
            {
                waiting.Observation = snapshot;
                waiting.Run = run ?? waiting.Run;
                _dirty = true;
            }

            return;
        }

        if (_parked.Any(p => p.Key == key) || !await AnnotateObservationAsync(snapshot, run).ConfigureAwait(false))
        {
            Park(Parked.OfObservation(snapshot, run, _services.Time.GetUtcNow()));
        }
    }

    /// <summary>True when the observation was dealt with (written, or nothing to write); false when the project was not free.</summary>
    private async Task<bool> AnnotateObservationAsync(RemoteSnapshot snapshot, string? run)
    {
        var pullRequest = snapshot.PullRequest;
        if (_trusted is null || pullRequest.Repository != _trusted)
        {
            return true;
        }

        var key = pullRequest.TraceId;
        _followed.TryGetValue(key, out var follow);
        var pending = follow?.PendingConfirmation;

        // Without the project: what the session already recorded decides whether there is anything new (docs/21 D-25: a round with nothing new does not take the project).
        if (_recorded.TryGetValue(key, out var known))
        {
            var early = Decide(known, snapshot, pending);
            if (early.Kind != Decision.Write)
            {
                Settle(follow, snapshot, early, known);
                return true;
            }
        }

        using var lease = Take();
        if (lease is null)
        {
            return false;
        }

        var log = await _services.Recorder.LoadAsync(Project.ProjectId, CancellationToken.None).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return true;
        }

        var reading = RemoteLogReading.ReadEntries(log.Entries);
        var recorded = reading.Find(pullRequest)?.Latest;
        var decision = Decide(recorded, snapshot, pending);
        if (decision.Kind == Decision.Write && LastFingerprintOf(log.Entries, pullRequest) is { } lastFingerprint && string.Equals(lastFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
        {
            // The same observation is the last the log holds (the cache was lost): nothing is written twice.
            decision = new Verdict(Decision.Same, []);
        }

        if (decision.Kind == Decision.Write)
        {
            var payload = RemoteStateObservedPayload.Observing(snapshot, run, recorded?.Fingerprint, decision.Changes);
            await _services.Recorder.RecordAsync(
                Project.ProjectId,
                new Actor(ActorKind.System, "remote-sync"),
                DecisionKind.RemoteStateObserved,
                $"Observa {pullRequest.TraceId}: {string.Join(", ", decision.Changes)}",
                payload,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }

        Settle(follow, snapshot, decision, recorded ?? snapshot);
        return true;
    }

    private enum Decision
    {
        Same,
        Discard,
        Confirm,
        Write,
    }

    private sealed record Verdict(Decision Kind, IReadOnlyList<RemoteChangeKind> Changes);

    private static Verdict Decide(RemoteSnapshot? recorded, RemoteSnapshot observed, RemoteSnapshot? pendingConfirmation)
    {
        var result = RemoteReconciler.Reconcile(recorded, observed);
        if (result.Discarded)
        {
            return new Verdict(Decision.Discard, []);
        }

        if (result.Changes.Count == 0)
        {
            return new Verdict(Decision.Same, []);
        }

        // Checks and reviews carry no reliable mark: a change that only touches them is written when it repeats in two rounds in a row (docs/21 D-23).
        if (recorded is not null && result.Changes.All(c => OnlyChecksOrReviews.Contains(c))
            && (pendingConfirmation is null || !string.Equals(pendingConfirmation.Fingerprint, observed.Fingerprint, StringComparison.Ordinal)))
        {
            return new Verdict(Decision.Confirm, result.Changes);
        }

        return new Verdict(Decision.Write, result.Changes);
    }

    /// <summary>What the session knows after dealing with an observation.</summary>
    private void Settle(FollowedPullRequest? follow, RemoteSnapshot observed, Verdict decision, RemoteSnapshot recordedNow)
    {
        var key = observed.PullRequest.TraceId;
        switch (decision.Kind)
        {
            case Decision.Confirm:
                _recorded[key] = recordedNow;
                if (follow is not null)
                {
                    _followed[key] = follow with { PendingConfirmation = observed };
                }

                break;
            case Decision.Discard:
                _recorded[key] = recordedNow;
                break;
            default:
                _recorded[key] = decision.Kind == Decision.Write ? observed : recordedNow;
                if (follow is not null && follow.PendingConfirmation is not null)
                {
                    _followed[key] = follow with { PendingConfirmation = null };
                }

                break;
        }

        _dirty = true;
        _services.Changed(Project.ProjectId);
    }

    private static string? LastFingerprintOf(IReadOnlyList<DecisionEntry> entries, PullRequestRef pullRequest)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind != DecisionKind.RemoteStateObserved)
            {
                continue;
            }

            try
            {
                if (DecisionPayloadSerializer.Deserialize(entries[i].Kind, entries[i].PayloadJson) is RemoteStateObservedPayload payload
                    && payload.PullRequest == pullRequest.Number && payload.Owner == pullRequest.Repository.Owner && payload.Repository == pullRequest.Repository.Name)
                {
                    return payload.Fingerprint;
                }
            }
            catch (Exception ex) when (RemoteLogReading.IsInvalidData(ex) || ex is NotSupportedException)
            {
                // an entry that cannot be read is not the last observation
            }
        }

        return null;
    }

    // ---- the file ----

    private async Task SaveIfDirtyAsync()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var now = _services.Time.GetUtcNow();
        var pending = new List<PendingRemoteWork>();
        foreach (var item in _parked)
        {
            if (item.Observation is { } observation)
            {
                pending.Add(new PendingRemoteWork(new RemoteWorkTicket(item.Id, null, observation.PullRequest), null, observation, "observe", item.Reason, item.Since, item.NotBefore));
            }
            else if (item.Flow is { } flow)
            {
                pending.Add(new PendingRemoteWork(flow.Ticket, flow.Operation, null, "flow", item.Reason, item.Since, item.NotBefore));
            }
        }

        pending.AddRange(_restored.Where(r => _statuses.TryGetValue(r.Ticket.Id, out var s) && s.State == RemoteWorkState.PendingSync && now - r.Since <= PublicationCodes.PendingLife));
        var health = Health;
        var state = new RemoteSyncState(_hint, [.. _followed.Values], pending, health.RateLimitedUntil, health.LastGoodSync, health.LastOutcome, health.LastOutcomeAt);
        try
        {
            await _services.Store.SaveAsync(Project.ProjectId, state, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The local state is a cache: failing to write it costs a reading, nothing else.
        }
    }

    // ---- closing ----

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _pollTimer?.Dispose();
        _retryTimer?.Dispose();
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class Parked
    {
        private static long _next;

        private Parked(string key, DateTimeOffset since)
        {
            Key = key;
            Since = since;
            Id = "p" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public string Id { get; }

        public string Key { get; }

        public DateTimeOffset Since { get; }

        public DateTimeOffset? NotBefore { get; set; }

        public PendingReason Reason { get; set; } = PendingReason.ProjectBusy;

        public RemoteFlow? Flow { get; private init; }

        public Func<Task<bool>>? Note { get; private init; }

        public RemoteSnapshot? Observation { get; set; }

        public string? Run { get; set; }

        public static Parked OfFlow(RemoteFlow flow, DateTimeOffset since, DateTimeOffset? notBefore, PendingReason reason) =>
            new(flow.Key, since) { Flow = flow, NotBefore = notBefore, Reason = reason };

        public static Parked OfNote(string key, Func<Task<bool>> note, DateTimeOffset since) => new(key, since) { Note = note };

        public static Parked OfObservation(RemoteSnapshot snapshot, string? run, DateTimeOffset since) =>
            new(snapshot.PullRequest.TraceId, since) { Observation = snapshot, Run = run };
    }

    private abstract record Message;

    private sealed record FlowMessage(RemoteFlow Flow, TaskCompletionSource<RemoteWorkTicket> First) : Message;

    private sealed record ExecMessage(Func<FlowContext, Task<object?>> Work, TaskCompletionSource<object?> Done) : Message;

    private sealed record NoteMessage(string Key, Func<Task<bool>> Record, TaskCompletionSource<bool> Done) : Message;

    private sealed record ObserveMessage(PullRequestRef PullRequest, bool Manual) : Message;

    private sealed record SyncMessage(TaskCompletionSource Done) : Message;

    private sealed record FollowMessage(PullRequestRef PullRequest, string? Run, TaskCompletionSource Done) : Message;

    private sealed record UnfollowMessage(PullRequestRef PullRequest, TaskCompletionSource Done) : Message;

    private sealed record CancelMessage(string Id, TaskCompletionSource Done) : Message;

    private sealed record PollMessage : Message;

    private sealed record RetryMessage : Message;
}

/// <summary>The bounds of the local state, shared by the mailbox and the store.</summary>
internal static class RemoteSyncLimits
{
    public const int MaxFollowed = 100;
    public const int MaxPending = 100;
}
