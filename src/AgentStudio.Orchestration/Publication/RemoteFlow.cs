using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Orchestration.Publication;

/// <summary>How a run of a flow ended (docs/21 D-18 and D-26).</summary>
internal enum FlowEnd
{
    /// <summary>Everything the flow had to do is done.</summary>
    Done,

    /// <summary>It stopped at something that will not mend by itself: the status says why, and nothing more is attempted.</summary>
    Failed,

    /// <summary>The project is held by a run, a debate or an audit (or the mailbox let it go a moment ago): the same stage is tried again.</summary>
    WaitProject,

    /// <summary>It waits to synchronise (no connection, the platform asked to wait): the same stage is tried again at <c>RetryIn</c>.</summary>
    WaitSync,
}

internal readonly record struct FlowResult(FlowEnd End, PendingReason? Reason = null, TimeSpan? RetryIn = null, UserText? Message = null)
{
    public static FlowResult Done { get; } = new(FlowEnd.Done);

    public static FlowResult Failed(string code) => new(FlowEnd.Failed, Message: UserText.Of(code));

    /// <summary>Nothing left to do, with something to say: the fact was done by another way, so this flow credits itself with nothing.</summary>
    public static FlowResult DoneWith(string code) => new(FlowEnd.Done, Message: UserText.Of(code));

    public static FlowResult WaitForProject() => new(FlowEnd.WaitProject, PendingReason.ProjectBusy);

    public static FlowResult WaitForSync(PendingReason reason, TimeSpan retryIn, string? code = null) =>
        new(FlowEnd.WaitSync, reason, retryIn, code is null ? null : UserText.Of(code));
}

/// <summary>
/// A piece of work of the person that the mailbox of a project runs in order (docs/21 D-25): a publication, a merge, closing a pull request… It is resumable: after a
/// <see cref="FlowEnd.WaitProject"/> or a <see cref="FlowEnd.WaitSync"/> the mailbox calls <see cref="RunAsync"/> again and the flow goes on from the stage it saved, asking the platform
/// what happened before it repeats anything (D-18, step 5). Every operation it does on the remote passes the gate of the policies just before it runs.
/// </summary>
internal abstract class RemoteFlow(RemoteWorkTicket ticket, string key, HostingOperation? operation)
{
    public RemoteWorkTicket Ticket { get; } = ticket;

    /// <summary>What the work is about, for the order: the pull request (or the run, before it has one). Two annotations of the same key never cross.</summary>
    public string Key { get; } = key;

    /// <summary>The operation the flow does, for the list of pending work; null when it is not one.</summary>
    public HostingOperation? Operation { get; } = operation;

    public abstract Task<FlowResult> RunAsync(FlowContext context);
}

/// <summary>What a flow may use of its mailbox while it runs, always on the consumer of the mailbox.</summary>
internal sealed class FlowContext(ProjectRemoteMailbox mailbox)
{
    public ProjectContext Project => mailbox.Project;

    public IDecisionRecorder Recorder => mailbox.Recorder;

    public TimeProvider Time => mailbox.Time;

    /// <summary>Cancels the waits for the platform when the app closes. Git never gets it: a push is not cut halfway.</summary>
    public CancellationToken Stopping => mailbox.Stopping;

    /// <summary>The app is closing: end the stretch and leave the rest pending.</summary>
    public bool Closing => mailbox.Closing;

    /// <summary>
    /// Takes the project for a remote operation, or null when something else holds it (or the mailbox let it go less than a second ago, the courtesy of D-25). Git and the log are
    /// touched only with the lease in hand; the platform's CLI, never.
    /// </summary>
    public IDisposable? Take() => mailbox.Take();

    /// <summary>
    /// The take of something the person just asked for and is waiting to see (the card of the publication, the click, a line of Auto Doctor). The courtesy does not apply to it: the
    /// courtesy is the pause between two takes of the mailbox's own work, so that whoever waits for the project gets in, and a click right after the card would otherwise be answered
    /// «busy» with nothing holding the project. A project that something else holds is still busy: null.
    /// </summary>
    public IDisposable? TakeNow() => mailbox.TakeNow();

    /// <summary>
    /// Annotates something in the log without holding the consumer: <paramref name="record"/> takes the project, writes and returns true, or returns false when the project was not free;
    /// then it is parked (in memory) behind the earlier annotations of the same key and the timer of the mailbox tries it again (docs/21 D-25).
    /// </summary>
    public Task<bool> NoteAsync(string key, Func<Task<bool>> record) => mailbox.NoteAsync(key, record);

    /// <summary>The repository of the project, as this session saw it with the project in hand: only then do the pull requests of the poll count as of this project.</summary>
    public void Trust(RemoteRepository repository) => mailbox.Trust(repository);

    /// <summary>Starts to follow a pull request (docs/21 D-24); false when it is not of the repository of the project.</summary>
    public bool Follow(PullRequestRef pullRequest, string? run) => mailbox.FollowNow(pullRequest, run);

    /// <summary>Asks the platform for the pull request and annotates what it saw, after something that may or may not have happened (a merge, a close).</summary>
    public Task<RemoteSnapshot?> ObserveAsync(PullRequestRef pullRequest) => mailbox.ObserveNowAsync(pullRequest);

    /// <summary>How the platform's last call ended, for the health of the screen.</summary>
    public void Outcome(HostingOutcome outcome) => mailbox.NoteOutcome(outcome);
}

/// <summary>The codes of this package (texts: <c>Text_&lt;code&gt;</c> in <c>OrchestratorStrings</c>), and the pauses of docs/21 D-24 to D-26.</summary>
public static class PublicationCodes
{
    public const string LogNotVerified = "hosting.refused.log-not-verified";
    public const string VerdictNotApproved = "hosting.refused.verdict-not-approved";
    public const string NoVerdict = "hosting.refused.no-verdict";
    public const string CommitNotJudged = "hosting.refused.commit-not-judged";
    public const string ConfigRefused = "hosting.refused.config-refused";
    public const string NoProvider = "hosting.refused.no-provider";
    public const string CapabilityMissing = "hosting.refused.capability-missing";
    public const string MergeMethodNotAllowed = "hosting.refused.merge-method";
    public const string NotApproved = "hosting.refused.not-approved";
    public const string ApprovalMismatch = "hosting.refused.approval-mismatch";
    public const string StateChanged = "hosting.refused.state-changed";
    public const string NeedsApproval = "hosting.refused.needs-approval";
    public const string ProjectBusy = "hosting.refused.project-busy";
    public const string NoRemote = "hosting.refused.no-remote";
    public const string OtherRepository = "hosting.refused.other-repository";
    public const string UnknownPullRequest = "hosting.refused.unknown-pull-request";
    public const string BranchMoved = "hosting.refused.branch-moved";
    public const string PullRequestUnreadable = "hosting.refused.pr-unreadable";
    public const string BaseChanged = "hosting.refused.base-changed";
    public const string AnnotationPending = "hosting.pending.annotation";
    public const string AlreadyMerged = "hosting.done.already-merged";
    public const string AlreadyClosed = "hosting.done.already-closed";
    public const string HistoryUnreadable = "hosting.refused.history-unreadable";

    public const string PushRefused = "hosting.failed.push";
    public const string PushRejected = "hosting.failed.push-rejected";
    public const string CredentialsNeeded = "hosting.failed.credentials";
    public const string Unreachable = "hosting.failed.unreachable";
    public const string SignInNeeded = "hosting.failed.sign-in";
    public const string RateLimited = "hosting.failed.rate-limited";
    public const string Redirected = "hosting.failed.redirected";
    public const string MergeBlocked = "hosting.failed.merge-blocked";
    public const string ProviderRefused = "hosting.failed.provider-refused";
    public const string Internal = "hosting.failed.internal";
    public const string Expired = "hosting.expired";

    public const string StatusApproved = "hosting.verdict.status-approved";
    public const string StatusRejected = "hosting.verdict.status-rejected";
    public const string StatusInReview = "hosting.verdict.status-in-review";

    /// <summary>How long a parked or waiting piece of work lives (docs/21 D-26).</summary>
    public static readonly TimeSpan PendingLife = TimeSpan.FromHours(24);

    /// <summary>How often the mailbox asks whether the project got free: <c>IProjectActivity</c> has no events (docs/21 D-25).</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The most the last attempt of an orderly close waits to write what is parked (docs/21 D-25).</summary>
    public static readonly TimeSpan ClosingLimit = TimeSpan.FromSeconds(5);

    /// <summary>The courtesy after letting the project go, so that whoever waited gets in (docs/21 D-25).</summary>
    public static readonly TimeSpan Courtesy = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait of the platform that is kept: more than that is cut (docs/21 D-24).</summary>
    public static readonly TimeSpan RateLimitCap = TimeSpan.FromHours(2);

    /// <summary>Without a connection: 1, 2, 5 and 15 minutes, and 15 from then on (docs/21 D-26).</summary>
    public static readonly TimeSpan[] OfflineBackoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public static TimeSpan OfflineWait(int attempt) => OfflineBackoff[Math.Clamp(attempt, 0, OfflineBackoff.Length - 1)];

    /// <summary>A merged or closed pull request is still looked at for this long, once an hour (docs/21 D-24).</summary>
    public static readonly TimeSpan FinishedWatch = TimeSpan.FromDays(30);

    public static readonly TimeSpan FinishedInterval = TimeSpan.FromHours(1);
}
