using System.Globalization;
using System.Text;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.Cli;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>The codes a refusal of the adapter carries (<see cref="HostingResult{T}.RefusalCode"/>): stable, closed, never a text of the remote. The screen turns each into its own sentence.</summary>
public static class GitHubRefusal
{
    public const string GhNotInstalled = "gh.not-installed";
    public const string CapabilityMissing = "capability.not-declared";
    public const string InvalidAnswer = "answer.invalid";
    public const string AccountInvalid = "account.login-invalid";
    public const string Moved = "remote.moved";
    public const string ServerError = "server.error";
    public const string TitleInvalid = "text.title-invalid";
    public const string BodyInvalid = "text.body-invalid";
    public const string CommitInvalid = "commit.invalid";
    public const string MergeMethodNotDeclared = "merge.method-not-declared";
    public const string MergeBlocked = "merge.blocked";
    public const string MergeHeadMoved = "merge.head-moved";
    public const string NotRunBranch = "branch.not-run-branch";
    public const string BranchHeadDiffers = "branch.head-differs";
    public const string ReviewersInvalid = "reviewers.invalid";
    public const string RunInvalid = "verdict.run-invalid";
    public const string DescriptionInvalid = "text.description-invalid";
    public const string ReviewsTooMany = "reviews.too-many";
    public const string MergeRejected = "merge.rejected";
    public const string CommentLookupUnknown = "comment.lookup-unknown";

    /// <summary>The code of an HTTP status that has no code of its own: <c>http.&lt;status&gt;</c>.</summary>
    public static string Http(int status) => "http." + status.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// GitHub through the official CLI (docs/21 D-09 to D-14, ADR-0065 and ADR-0066): only <c>gh api</c>, from the closed catalog of <see cref="GitHubEndpoint"/>, with full paths from <see cref="GitHubApiPath"/>
/// and the data of a write through standard input. Every call goes through <see cref="GhQueue"/> (one at a time, with the state of the limit), outside any hold on the project. The status is the one of
/// the line of the answer; the standard error never reaches a message. What is not declared for the version of <c>gh</c> is not offered: the call answers <see cref="HostingOutcome.Unsupported"/> and
/// launches nothing. The interface has no member that sends a review, and this class has no way to: the verdict goes as a status of the commit and a tagged comment.
/// </summary>
public sealed partial class GitHubHostingProvider : IHostingProvider
{
    /// <summary>The longest title and body of a pull request or a comment (docs/21 D-19).</summary>
    internal const int MaxTitleLength = 200;

    internal const int MaxBodyLength = 20_000;

    internal const int MaxReviewers = 15;

    internal const string VerdictMarkPrefix = "<!-- agent-studio:verdict run=";

    private readonly GhQueue _queue;
    private readonly IDoctorToolRunner _door;
    private HostingCapabilities? _capabilities;

    internal GitHubHostingProvider(GhQueue queue, IDoctorToolRunner door)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(door);
        _queue = queue;
        _door = door;
    }

    public HostingProviderKind Kind => HostingProviderKind.GitHub;

    /// <summary>The wait that GitHub asked for, as the queue holds it (the store of the mailbox keeps it across a restart).</summary>
    public DateTimeOffset? RateLimitedUntil => _queue.RateLimitedUntil;

    public void RestoreRateLimit(DateTimeOffset? until) => _queue.RestoreRateLimit(until);

    public async Task<HostingAvailability> ProbeAsync(CancellationToken ct)
    {
        var reading = await GitHubCli.ReadAsync(_door, null, ct).ConfigureAwait(false);
        var availability = GitHubCli.Availability(reading);

        // Only a reading that worked is kept: a probe that failed (the door timed out, gh was not there yet) must not leave «nothing declared» for good.
        if (reading.State == GhInstallState.Installed)
        {
            _capabilities = availability.Capabilities;
        }

        return availability;
    }

    private async Task<HostingCapabilities> CapabilitiesAsync(CancellationToken ct) =>
        _capabilities ?? (await ProbeAsync(ct).ConfigureAwait(false)).Capabilities;

    /// <summary>The refusal of an operation that is not declared: nothing is launched.</summary>
    private async Task<HostingResult<T>?> RequireAsync<T>(HostingCapability capability, CancellationToken ct)
    {
        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        return capabilities.Has(capability) ? null : new HostingResult<T>(HostingOutcome.Unsupported, RefusalCode: GitHubRefusal.CapabilityMissing);
    }

    // ---- The answer of one call, as an outcome of the contract ----

    private readonly record struct Step(HostingOutcome Outcome, GhHttpResponse? Http = null, string? Code = null, DateTimeOffset? RetryAt = null)
    {
        internal bool IsOk => Outcome == HostingOutcome.Ok;

        /// <summary>A failure that stops a flow whatever it was doing: nothing more should be asked.</summary>
        internal bool IsFatal => Outcome is HostingOutcome.SignInNeeded or HostingOutcome.RateLimited or HostingOutcome.Unreachable or HostingOutcome.InvalidAnswer or HostingOutcome.Unsupported;

        internal HostingResult<T> Fail<T>(T? value = default) => new(Outcome, value, null, RetryAt, Code);
    }

    private async Task<Step> SendAsync(GitHubTarget target, string? body, string? eTag, CancellationToken ct)
    {
        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        var reply = await _queue.SendAsync(target.Call(body, capabilities.Has(HostingCapability.ConditionalRequests) ? eTag : null), ct).ConfigureAwait(false);
        return reply.Kind switch
        {
            GhReplyKind.RateLimited => new Step(HostingOutcome.RateLimited, RetryAt: reply.RetryAt),
            GhReplyKind.NotInstalled => new Step(HostingOutcome.Unsupported, Code: GitHubRefusal.GhNotInstalled),
            GhReplyKind.SignInNeeded => new Step(HostingOutcome.SignInNeeded),
            GhReplyKind.Unreachable => new Step(HostingOutcome.Unreachable),
            GhReplyKind.InvalidAnswer => new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.InvalidAnswer),
            _ => FromStatus(reply.Http!),
        };
    }

    private static Step FromStatus(GhHttpResponse http) => http.Status switch
    {
        >= 200 and <= 299 => new Step(HostingOutcome.Ok, http),
        304 => new Step(HostingOutcome.NotModified, http),
        401 => new Step(HostingOutcome.SignInNeeded, http),
        403 => new Step(HostingOutcome.Forbidden, http),
        404 or 410 => new Step(HostingOutcome.NotFound, http),
        301 or 302 or 307 or 308 => new Step(HostingOutcome.Refused, http, GitHubRefusal.Moved),
        >= 500 and <= 599 => new Step(HostingOutcome.Unreachable, http, GitHubRefusal.ServerError),
        >= 400 and <= 499 => new Step(HostingOutcome.Refused, http, GitHubRefusal.Http(http.Status)),
        _ => new Step(HostingOutcome.InvalidAnswer, http, GitHubRefusal.InvalidAnswer),
    };

    private static HostingResult<T> Invalid<T>() => new(HostingOutcome.InvalidAnswer, RefusalCode: GitHubRefusal.InvalidAnswer);

    private static HostingResult<T> Refuse<T>(string code) => new(HostingOutcome.Refused, RefusalCode: code);

    // ---- Text that goes to GitHub ----

    /// <summary>A lone half of a surrogate pair is not text: the JSON writer cannot encode it, so it is refused before any body is built (no method of the provider throws for a strange text).</summary>
    internal static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text cut at <paramref name="maximum"/> characters without splitting a surrogate pair.</summary>
    internal static string CutAt(string text, int maximum)
    {
        if (text.Length <= maximum)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }

    private static bool IsOneLineOfText(string text) => text.Length > 0 && !string.IsNullOrWhiteSpace(text) && !HasLoneSurrogate(text) && !UntrustedText.HasHidden(text);

    /// <summary>A body may span lines and hold tabs; no other control or format character (a mark of direction would reorder what the reader sees).</summary>
    private static bool IsBodyText(string text)
    {
        if (HasLoneSurrogate(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (UntrustedText.IsHidden(rune) && rune.Value is not ('\n' or '\r' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? TextRefusal(PullRequestText text)
    {
        if (text is null || text.Title is null || text.Body is null || text.Title.Length > MaxTitleLength || !IsOneLineOfText(text.Title))
        {
            return GitHubRefusal.TitleInvalid;
        }

        return text.Body.Length > MaxBodyLength || !IsBodyText(text.Body) ? GitHubRefusal.BodyInvalid : null;
    }

    // ---- Reads of the repository ----

    public async Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (await RequireAsync<RemoteLogin>(HostingCapability.ReadRepository, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var step = await SendAsync(GitHubApiPath.Viewer(), null, null, ct).ConfigureAwait(false);
        if (!step.IsOk)
        {
            return step.Fail<RemoteLogin>();
        }

        return GitHubReaders.Viewer(step.Http!.Body) is { } login
            ? new HostingResult<RemoteLogin>(HostingOutcome.Ok, login)
            : new HostingResult<RemoteLogin>(HostingOutcome.InvalidAnswer, RefusalCode: GitHubRefusal.AccountInvalid);
    }

    public async Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (await RequireAsync<RemoteRepositoryFacts>(HostingCapability.ReadRepository, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var step = await SendAsync(GitHubApiPath.Repository(repository), null, etag, ct).ConfigureAwait(false);
        if (step.Outcome == HostingOutcome.NotModified)
        {
            return new HostingResult<RemoteRepositoryFacts>(HostingOutcome.NotModified, ETag: step.Http!.ETag ?? (GhArguments.IsETag(etag) ? etag : null));
        }

        if (!step.IsOk)
        {
            return step.Fail<RemoteRepositoryFacts>();
        }

        var facts = GitHubReaders.Repository(step.Http!.Body, out var code);
        return facts is null
            ? new HostingResult<RemoteRepositoryFacts>(HostingOutcome.InvalidAnswer, RefusalCode: code ?? GitHubRefusal.InvalidAnswer)
            : new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, facts, step.Http.ETag);
    }

    public async Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(branch);
        if (await RequireAsync<BranchRules>(HostingCapability.ReadBranchRules, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var rules = await SendAsync(GitHubApiPath.BranchRules(repository, branch), null, null, ct).ConfigureAwait(false);
        if (rules.IsFatal && rules.Outcome != HostingOutcome.InvalidAnswer)
        {
            return rules.Fail<BranchRules>();
        }

        if (!rules.IsOk)
        {
            // The platform does not show them to this account (or the branch is not there): that is never «no rules» (D-17).
            return new HostingResult<BranchRules>(HostingOutcome.Ok, BranchRules.Unreadable);
        }

        var branchStep = await SendAsync(GitHubApiPath.Branch(repository, branch), null, null, ct).ConfigureAwait(false);
        if (branchStep.IsFatal && branchStep.Outcome != HostingOutcome.InvalidAnswer)
        {
            return branchStep.Fail<BranchRules>();
        }

        var read = GitHubReaders.Rules(rules.Http!.Body, branchStep.IsOk ? branchStep.Http!.Body : null);
        return read is null ? Invalid<BranchRules>() : new HostingResult<BranchRules>(HostingOutcome.Ok, read);
    }

    // ---- Writes of the pull request ----

    public async Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(head);
        if (await RequireAsync<PullRequestRef?>(HostingCapability.ReadRepository, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var step = await SendAsync(GitHubApiPath.FindPullRequests(repository, head), null, null, ct).ConfigureAwait(false);
        if (!step.IsOk)
        {
            return step.Fail<PullRequestRef?>();
        }

        var (valid, number) = GitHubReaders.OpenPullRequestFor(step.Http!.Body, repository, head);
        if (!valid)
        {
            return Invalid<PullRequestRef?>();
        }

        return new HostingResult<PullRequestRef?>(HostingOutcome.Ok, number is { } n ? new PullRequestRef(repository, n) : null);
    }

    public async Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (await RequireAsync<PullRequestRef>(HostingCapability.OpenPullRequest, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (draft.Draft && await RequireAsync<PullRequestRef>(HostingCapability.DraftPullRequest, ct).ConfigureAwait(false) is { } draftRefusal)
        {
            return draftRefusal;
        }

        if (TextRefusal(draft.Text) is { } code)
        {
            return Refuse<PullRequestRef>(code);
        }

        var step = await SendAsync(GitHubApiPath.OpenPullRequest(draft.Repository), GitHubRequestBodies.OpenPullRequest(draft), null, ct).ConfigureAwait(false);
        if (!step.IsOk)
        {
            return step.Fail<PullRequestRef>();
        }

        return GitHubReaders.OpenedNumber(step.Http!.Body) is { } number
            ? new HostingResult<PullRequestRef>(HostingOutcome.Ok, new PullRequestRef(draft.Repository, number))
            : Invalid<PullRequestRef>();
    }

    public async Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (await RequireAsync<bool>(HostingCapability.UpdatePullRequest, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (TextRefusal(text) is { } code)
        {
            return Refuse<bool>(code);
        }

        var step = await SendAsync(GitHubApiPath.PullRequest(GitHubEndpoint.UpdatePullRequest, pullRequest), GitHubRequestBodies.UpdatePullRequest(text), null, ct).ConfigureAwait(false);
        return step.IsOk ? new HostingResult<bool>(HostingOutcome.Ok, true) : step.Fail<bool>();
    }

    public async Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(reviewers);
        if (await RequireAsync<bool>(HostingCapability.RequestReviewers, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var distinct = reviewers.Where(r => r is not null).DistinctBy(r => r.Value, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0 || distinct.Count != reviewers.Count || distinct.Count > MaxReviewers)
        {
            return Refuse<bool>(GitHubRefusal.ReviewersInvalid);
        }

        var step = await SendAsync(GitHubApiPath.RequestReviewers(pullRequest), GitHubRequestBodies.RequestReviewers(distinct), null, ct).ConfigureAwait(false);
        return step.IsOk ? new HostingResult<bool>(HostingOutcome.Ok, true) : step.Fail<bool>();
    }

    public async Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (await RequireAsync<bool>(HostingCapability.ClosePullRequest, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var step = await SendAsync(GitHubApiPath.PullRequest(GitHubEndpoint.ClosePullRequest, pullRequest), GitHubRequestBodies.ClosePullRequest(), null, ct).ConfigureAwait(false);
        return step.IsOk ? new HostingResult<bool>(HostingOutcome.Ok, true) : step.Fail<bool>();
    }

    /// <summary>
    /// Merges with a method that the adapter declares and the head that was judged: GitHub refuses with 409 when the head is another (docs/21 D-20). It does not check the modes that the repository allows or
    /// the protections of the branch: the gate of the policies does, from <c>ReadRepositoryAsync</c> and <c>ReadBranchRulesAsync</c> (D-17). No title, no message of the merge and no way to ask for an
    /// administrator. A refusal 405 (not mergeable) or 409 (the head does not match) says in what state the pull request is, from the closed vocabulary of GitHub; a 422 is not a block (validation failed)
    /// and has its own code. <b>A timeout in the middle of the request is reported as unreachable although the server may have merged:</b> whoever finds it observes the pull request before it tries again.
    /// </summary>
    public async Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.Has(HostingCapability.Merge))
        {
            return new HostingResult<MergeOutcome>(HostingOutcome.Unsupported, RefusalCode: GitHubRefusal.CapabilityMissing);
        }

        if (!capabilities.MergeMethods.Contains(method))
        {
            return new HostingResult<MergeOutcome>(HostingOutcome.Unsupported, RefusalCode: GitHubRefusal.MergeMethodNotDeclared);
        }

        if (!GitHubApiPath.IsCommitId(expectedHead))
        {
            return Refuse<MergeOutcome>(GitHubRefusal.CommitInvalid);
        }

        var step = await SendAsync(GitHubApiPath.Merge(pullRequest), GitHubRequestBodies.Merge(method, expectedHead), null, ct).ConfigureAwait(false);
        if (step.IsOk)
        {
            return GitHubReaders.MergedSha(step.Http!.Body) is { } sha
                ? new HostingResult<MergeOutcome>(HostingOutcome.Ok, new MergeOutcome(true, sha, null))
                : Invalid<MergeOutcome>();
        }

        if (step.Http is { Status: 422 })
        {
            return new HostingResult<MergeOutcome>(HostingOutcome.Refused, new MergeOutcome(false, null, null), RefusalCode: GitHubRefusal.MergeRejected);
        }

        if (step.Http is { Status: 405 or 409 })
        {
            var state = "unknown";
            var read = await SendAsync(GitHubApiPath.PullRequest(GitHubEndpoint.PullRequest, pullRequest), null, null, ct).ConfigureAwait(false);
            if (read.IsOk && GitHubReaders.PullRequest(read.Http!.Body) is { } facts)
            {
                state = facts.MergeableState;
            }
            else if (read.IsFatal && read.Outcome != HostingOutcome.InvalidAnswer)
            {
                return read.Fail<MergeOutcome>();
            }

            return new HostingResult<MergeOutcome>(
                HostingOutcome.Refused,
                new MergeOutcome(false, null, state),
                RefusalCode: step.Http.Status == 409 ? GitHubRefusal.MergeHeadMoved : GitHubRefusal.MergeBlocked);
        }

        return step.Fail<MergeOutcome>();
    }

    /// <summary>
    /// Deletes the branch of a run, and only if it is one and its head on the remote is the commit that was expected (read just before: the API of deletion takes no condition, so a minimal window remains,
    /// docs/21 D-20). Never any other branch.
    /// </summary>
    public async Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(runBranch);
        if (await RequireAsync<bool>(HostingCapability.DeleteBranch, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (!runBranch.IsRunBranch)
        {
            return Refuse<bool>(GitHubRefusal.NotRunBranch);
        }

        if (!GitHubApiPath.IsCommitId(expectedCommit))
        {
            return Refuse<bool>(GitHubRefusal.CommitInvalid);
        }

        var read = await SendAsync(GitHubApiPath.Branch(repository, runBranch), null, null, ct).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return read.Fail<bool>();
        }

        var head = GitHubReaders.BranchHead(read.Http!.Body);
        if (head is null)
        {
            return Invalid<bool>();
        }

        if (!string.Equals(head, expectedCommit, StringComparison.Ordinal))
        {
            return Refuse<bool>(GitHubRefusal.BranchHeadDiffers);
        }

        var step = await SendAsync(GitHubApiPath.DeleteBranch(repository, runBranch), null, null, ct).ConfigureAwait(false);

        // The documentation of the deletion gives 204, 409 and 422; a 422 is «the reference does not exist».
        if (step.Http is { Status: 422 })
        {
            // «The reference does not exist» or a rejection: the branch is read again. Only a 404 says it is gone; if it is still there, it was refused.
            var again = await SendAsync(GitHubApiPath.Branch(repository, runBranch), null, null, ct).ConfigureAwait(false);
            if (again.Outcome == HostingOutcome.NotFound)
            {
                return new HostingResult<bool>(HostingOutcome.NotFound);
            }

            return again.IsOk || !again.IsFatal ? step.Fail<bool>() : again.Fail<bool>();
        }

        return step.IsOk ? new HostingResult<bool>(HostingOutcome.Ok, true) : step.Fail<bool>();
    }

    // ---- The verdict (docs/21 D-21): a status of the commit and a tagged comment, never a review ----

    internal static string VerdictMark(RunIdText run) => VerdictMarkPrefix + run.Value + " -->";

    public async Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string statusDescription, string commentBody, long? commentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(statusDescription);
        ArgumentNullException.ThrowIfNull(commentBody);
        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        var withStatus = capabilities.Has(HostingCapability.CommitStatus);
        var withComment = capabilities.Has(HostingCapability.Comment);
        if (!withStatus && !withComment)
        {
            return new HostingResult<PublishedVerdict>(HostingOutcome.Unsupported, RefusalCode: GitHubRefusal.CapabilityMissing);
        }

        if (!GitHubApiPath.IsCommitId(notice.JudgedCommit))
        {
            return Refuse<PublishedVerdict>(GitHubRefusal.CommitInvalid);
        }

        if (!RunIdText.TryCreate(notice.Run.Value, out var run))
        {
            return Refuse<PublishedVerdict>(GitHubRefusal.RunInvalid);
        }

        // The description of the status is a text of the system that the caller composed (its templates, D-19): the adapter only takes the control and format characters out and cuts it at 120.
        var description = UntrustedText.Clean(statusDescription).Trim();
        description = CutAt(description, GitHubRequestBodies.MaxStatusDescription).TrimEnd();

        if (description.Length == 0)
        {
            return Refuse<PublishedVerdict>(GitHubRefusal.DescriptionInvalid);
        }

        var mark = VerdictMark(run);
        var body = commentBody.StartsWith(mark, StringComparison.Ordinal) && (commentBody.Length == mark.Length || commentBody[mark.Length] is '\n' or '\r')
            ? commentBody
            : mark + "\n" + commentBody;
        if (body.Length > MaxBodyLength || !IsBodyText(body))
        {
            return Refuse<PublishedVerdict>(GitHubRefusal.BodyInvalid);
        }

        var statusPublished = false;
        if (withStatus)
        {
            var status = await SendAsync(
                GitHubApiPath.PostCommitStatus(pullRequest.Repository, notice.JudgedCommit),
                GitHubRequestBodies.CommitStatus(notice.Standing, description),
                null,
                ct).ConfigureAwait(false);
            if (status.IsOk)
            {
                statusPublished = true;
            }
            else if (status.IsFatal)
            {
                return status.Fail<PublishedVerdict>();
            }

            // Without permission for the status (403, 404) or refused: the comment still says it (D-21).
        }

        if (!withComment)
        {
            return statusPublished
                ? new HostingResult<PublishedVerdict>(HostingOutcome.Ok, new PublishedVerdict(true, null))
                : new HostingResult<PublishedVerdict>(HostingOutcome.Forbidden);
        }

        var written = await WriteCommentAsync(pullRequest, mark, body, commentId, ct).ConfigureAwait(false);
        if (written.Step is { } failure)
        {
            return failure.Fail(statusPublished ? new PublishedVerdict(true, null) : null);
        }

        return new HostingResult<PublishedVerdict>(HostingOutcome.Ok, new PublishedVerdict(statusPublished, written.Id));
    }

    /// <summary>
    /// Writes the comment of the verdict. An id that is given is not trusted: before the comment is edited it is read, and it has to be the comment of this verdict (its first line is the mark of this run), written by
    /// the account that publishes and on this pull request (its <c>issue_url</c>); if it is not (or it is gone) the comment is looked up by its mark, as when there is no id. <b>A timeout in the middle of a write is
    /// reported as unreachable although the server may have applied it:</b> whoever finds it observes before it tries again, so a comment is not written twice (the lookup by mark finds the first).
    /// </summary>
    private async Task<(Step? Step, long? Id)> WriteCommentAsync(PullRequestRef pullRequest, string mark, string body, long? commentId, CancellationToken ct)
    {
        var account = await SendAsync(GitHubApiPath.Viewer(), null, null, ct).ConfigureAwait(false);
        if (!account.IsOk)
        {
            return (account, null);
        }

        if (GitHubReaders.Viewer(account.Http!.Body) is not { } login)
        {
            return (new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.AccountInvalid), null);
        }

        var id = commentId;
        if (id is { } given)
        {
            var read = await SendAsync(GitHubApiPath.Comment(pullRequest.Repository, given), null, null, ct).ConfigureAwait(false);
            if (read.IsOk)
            {
                switch (GitHubReaders.IsOwnVerdictComment(read.Http!.Body, mark, login, pullRequest.Repository, pullRequest.Number))
                {
                    case null:
                        return (new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.InvalidAnswer), null);
                    case false:
                        id = null;
                        break;
                }
            }
            else if (read.IsFatal && read.Outcome != HostingOutcome.InvalidAnswer)
            {
                return (read, null);
            }
            else
            {
                // Gone, or not readable: it is not the one to edit.
                id = null;
            }
        }

        if (id is null)
        {
            var found = await FindOwnCommentAsync(pullRequest, mark, login, ct).ConfigureAwait(false);
            if (found.Step is { } lookupFailure)
            {
                return (lookupFailure, null);
            }

            id = found.Id;
        }

        var json = GitHubRequestBodies.Comment(body);
        if (id is { } edit)
        {
            var step = await SendAsync(GitHubApiPath.EditComment(pullRequest.Repository, edit), json, null, ct).ConfigureAwait(false);
            if (step.IsOk)
            {
                return (null, edit);
            }

            // The comment was deleted between the read and the edit: it is written again, once.
            if (step.Outcome != HostingOutcome.NotFound)
            {
                return (step, null);
            }
        }

        var posted = await SendAsync(GitHubApiPath.PostComment(pullRequest), json, null, ct).ConfigureAwait(false);
        if (!posted.IsOk)
        {
            return (posted, null);
        }

        return GitHubReaders.CommentId(posted.Http!.Body) is { } created ? (null, created) : (new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.InvalidAnswer), null);
    }

    /// <summary>
    /// The comment of the verdict that is ours: its first line is the mark and its author is the account that publishes (D-21). Reads up to <see cref="GitHubApiPath.CommentsPages"/> pages of
    /// <see cref="GitHubApiPath.CommentsPageSize"/> (300 comments): a pull request with more would get a second comment of the verdict, which the screen shows as it is; it is the limit of the lookup and it is documented.
    /// </summary>
    private async Task<(Step? Step, long? Id)> FindOwnCommentAsync(PullRequestRef pullRequest, string mark, RemoteLogin login, CancellationToken ct)
    {
        for (var page = 1; page <= GitHubApiPath.CommentsPages; page++)
        {
            var step = await SendAsync(GitHubApiPath.Comments(pullRequest, page), null, null, ct).ConfigureAwait(false);

            // A page that does not fit in what gh may answer (1 MB): it cannot be known whether the comment of the verdict is in it, and writing another could duplicate it. Nothing is written; the status was.
            if (step is { Outcome: HostingOutcome.InvalidAnswer, Http: null })
            {
                return (new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.CommentLookupUnknown), null);
            }

            if (!step.IsOk)
            {
                return (step, null);
            }

            var (valid, id, items) = GitHubReaders.CommentWithMark(step.Http!.Body, mark, login);
            if (!valid)
            {
                return (new Step(HostingOutcome.InvalidAnswer, Code: GitHubRefusal.InvalidAnswer), null);
            }

            if (id is not null)
            {
                return (null, id);
            }

            if (items < GitHubApiPath.CommentsPageSize)
            {
                break;
            }
        }

        return (null, null);
    }
}
/// <summary>The identifier of a run in the mark of the comment of a verdict: <c>[A-Za-z0-9_-]{1,64}</c>, so the mark cannot close the comment of HTML or carry anything else.</summary>
internal readonly record struct RunIdText
{
    private RunIdText(string value) => Value = value;

    internal string Value { get; }

    internal static bool TryCreate(string? value, out RunIdText run)
    {
        run = default;
        if (value is not { Length: >= 1 and <= 64 } || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            return false;
        }

        run = new RunIdText(value);
        return true;
    }
}
