using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>What the adapter keeps of the pull request itself (docs/21 D-23): identifiers, enums, a time, and one text for the screen.</summary>
internal sealed record PullRequestFacts(
    PullRequestState State,
    string Head,
    RemoteBranch Base,
    string? MergeCommit,
    RemoteLogin? MergedBy,
    DateTimeOffset UpdatedAt,
    string? Title,
    string MergeableState);

/// <summary>A commit of a list, with what is needed to tell how a pull request was merged (docs/21 D-27). Never leaves the adapter: nothing of it is written anywhere.</summary>
internal sealed record CommitInfo(string Sha, string? AuthorName, string? AuthorDate, string? Message);

/// <summary>What a list of checks and statuses says of one commit.</summary>
internal sealed record ChecksReading(int Passing, int Failing, int Pending, bool Truncated, IReadOnlyList<string> FailingNames);

/// <summary>The events of a timeline that the adapter reads: <see cref="HeadRefDeleted"/> and its restore, and the force pushes (docs/21 D-27).</summary>
internal enum TimelineEvent
{
    ForcePushed,
    HeadRefDeleted,
    HeadRefRestored,
}

internal sealed record TimelinePage(IReadOnlyList<TimelineEvent> Events, int Items);

internal sealed record DeploymentEntry(long Id, string Environment);

/// <summary>
/// One reader for each response of GitHub (docs/21 D-12). Each one reads only the fields of its list, with their type and their bound, and answers null when the body is not what the endpoint
/// documents: the provider turns that into <see cref="HostingOutcome.InvalidAnswer"/>, never into a success. A value outside a closed vocabulary is «unknown», not an error and not a hit.
/// The text of the remote (a title, the name of a check, an environment) comes out cleaned and cut, for the screen; identifiers are validated by the types of the domain.
/// </summary>
internal static partial class GitHubReaders
{
    /// <summary>The standard line git writes for a revert: the only thing read of the message of a commit (docs/21 D-27).</summary>
    [GeneratedRegex(@"^This reverts commit (?<sha>[0-9a-f]{40}|[0-9a-f]{64})\.\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RevertLine();

    private const int MaxEntries = 300;

    private const int MaxNames = 20;

    internal static RemoteLogin? Viewer(string? body)
    {
        using var document = GitHubJson.Parse(body);
        return document is null ? null : RemoteLogin.TryParse(GitHubJson.Str(document.RootElement, "login", 64));
    }

    /// <param name="refusalCode">When the answer is the shape of a repository but its default branch is outside what the app admits: the code of the branch rule.</param>
    internal static RemoteRepositoryFacts? Repository(string? body, out string? refusalCode)
    {
        refusalCode = null;
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var name = GitHubJson.Str(root, "default_branch", RemoteBranch.MaxLength);
        if (name is null)
        {
            return null;
        }

        var branch = RemoteBranch.TryParse(name, out refusalCode);
        if (branch is null)
        {
            return null;
        }

        var methods = new List<MergeMethod>(3);
        if (GitHubJson.Bool(root, "allow_merge_commit") == true)
        {
            methods.Add(MergeMethod.MergeCommit);
        }

        if (GitHubJson.Bool(root, "allow_squash_merge") == true)
        {
            methods.Add(MergeMethod.Squash);
        }

        if (GitHubJson.Bool(root, "allow_rebase_merge") == true)
        {
            methods.Add(MergeMethod.Rebase);
        }

        var canPush = GitHubJson.Obj(root, "permissions") is { } permissions && GitHubJson.Bool(permissions, "push") == true;
        return new RemoteRepositoryFacts(branch, methods, canPush, GitHubJson.Bool(root, "archived") == true);
    }

    /// <summary>The active rules of a branch from <c>rules/branches/{branch}</c>, with what <c>branches/{branch}</c> adds (the flag of protection). Null when the first is not a list.</summary>
    internal static BranchRules? Rules(string? rulesBody, string? branchBody)
    {
        using var rules = GitHubJson.Parse(rulesBody);
        if (rules is null || rules.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<BranchRule>();
        var unknown = false;
        foreach (var rule in rules.RootElement.EnumerateArray().Take(MaxEntries))
        {
            var parameters = GitHubJson.Obj(rule, "parameters");
            switch (GitHubJson.Str(rule, "type", 64))
            {
                case "pull_request":
                    list.Add(new BranchRule(BranchRuleKind.RequiredReviews, parameters is { } p ? GitHubJson.Int(p, "required_approving_review_count") : null));
                    break;
                case "required_status_checks":
                    list.Add(new BranchRule(BranchRuleKind.RequiredChecks, parameters is { } q && GitHubJson.Arr(q, "required_status_checks") is { } checks ? checks.GetArrayLength() : null));
                    break;
                case "required_signatures":
                    list.Add(new BranchRule(BranchRuleKind.SignedCommits, null));
                    break;
                case "required_linear_history":
                    list.Add(new BranchRule(BranchRuleKind.LinearHistory, null));
                    break;
                case "merge_queue":
                    list.Add(new BranchRule(BranchRuleKind.MergeQueue, null));
                    break;
                default:
                    unknown = true;
                    break;
            }
        }

        using var branch = GitHubJson.Parse(branchBody);
        if (branch is not null && branch.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (GitHubJson.Bool(branch.RootElement, "protected") == true)
            {
                list.Add(new BranchRule(BranchRuleKind.Protected, null));
            }

            if (GitHubJson.Obj(branch.RootElement, "protection") is { } protection
                && GitHubJson.Obj(protection, "required_status_checks") is { } required
                && !list.Any(r => r.Kind == BranchRuleKind.RequiredChecks))
            {
                list.Add(new BranchRule(BranchRuleKind.RequiredChecks, GitHubJson.Arr(required, "contexts") is { } contexts ? contexts.GetArrayLength() : null));
            }
        }

        if (unknown)
        {
            list.Add(new BranchRule(BranchRuleKind.Unknown, null));
        }

        return new BranchRules(true, list);
    }

    /// <summary>The open pull request whose head is <paramref name="head"/> in the same repository, from the list of <c>pulls?head=…</c>; null inside the result when none is open. A list that is not a list is null.</summary>
    internal static (bool Valid, int? Number) OpenPullRequestFor(string? body, RemoteRepository repository, RemoteBranch head)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (false, null);
        }

        var full = repository.Owner + "/" + repository.Name;
        foreach (var item in document.RootElement.EnumerateArray().Take(MaxEntries))
        {
            var number = GitHubJson.Int(item, "number");
            if (GitHubJson.Str(item, "state", 16) != "open" || number is null or 0)
            {
                continue;
            }

            if (GitHubJson.Obj(item, "head") is not { } h || GitHubJson.Str(h, "ref", RemoteBranch.MaxLength) != head.Name)
            {
                continue;
            }

            if (GitHubJson.Obj(h, "repo") is { } repo && string.Equals(GitHubJson.Str(repo, "full_name", 200), full, StringComparison.OrdinalIgnoreCase))
            {
                return (true, number);
            }
        }

        return (true, null);
    }

    /// <summary>The number of a pull request that was just opened.</summary>
    internal static int? OpenedNumber(string? body)
    {
        using var document = GitHubJson.Parse(body);
        return document is not null && GitHubJson.Int(document.RootElement, "number") is { } number and > 0 ? number : null;
    }

    internal static PullRequestFacts? PullRequest(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var merged = GitHubJson.Bool(root, "merged");
        var state = GitHubJson.Str(root, "state", 16);
        PullRequestState kind;
        if (merged == true)
        {
            kind = PullRequestState.Merged;
        }
        else if (state == "closed")
        {
            kind = PullRequestState.Closed;
        }
        else if (state == "open")
        {
            kind = GitHubJson.Bool(root, "draft") == true ? PullRequestState.Draft : PullRequestState.Open;
        }
        else
        {
            return null;
        }

        if (GitHubJson.Obj(root, "head") is not { } head || GitHubJson.CommitId(GitHubJson.Str(head, "sha", 64)) is not { } sha
            || GitHubJson.Obj(root, "base") is not { } baseBranch || RemoteBranch.TryParse(GitHubJson.Str(baseBranch, "ref", RemoteBranch.MaxLength)) is not { } baseRef
            || GitHubJson.Date(root, "updated_at") is not { } updated)
        {
            return null;
        }

        // merge_commit_sha only counts when the pull request is merged: before that it is a test commit (docs/21 §13).
        string? mergeCommit = null;
        RemoteLogin? mergedBy = null;
        if (kind == PullRequestState.Merged)
        {
            mergeCommit = GitHubJson.CommitId(GitHubJson.Str(root, "merge_commit_sha", 64));
            if (mergeCommit is null)
            {
                return null;
            }

            mergedBy = GitHubJson.Obj(root, "merged_by") is { } by ? RemoteLogin.TryParse(GitHubJson.Str(by, "login", 64)) : null;
        }

        return new PullRequestFacts(kind, sha, baseRef, mergeCommit, mergedBy, updated, GitHubJson.Display(GitHubJson.Str(root, "title", 1024)), MergeableState(GitHubJson.Str(root, "mergeable_state", 32)));
    }

    /// <summary>The state a merge was blocked in, as a word of the closed vocabulary of GitHub; anything else is <c>unknown</c>.</summary>
    internal static string MergeableState(string? state) => state switch
    {
        "clean" or "dirty" or "blocked" or "behind" or "unstable" or "has_hooks" or "draft" => state,
        _ => "unknown",
    };

    /// <summary>The commits of a page of <c>pulls/{n}/commits</c> or of a list of commits; null when it is not a list or a commit has no full id.</summary>
    internal static IReadOnlyList<CommitInfo>? Commits(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<CommitInfo>();
        foreach (var item in document.RootElement.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            if (GitHubJson.CommitId(GitHubJson.Str(item, "sha", 64)) is not { } sha)
            {
                return null;
            }

            var commit = GitHubJson.Obj(item, "commit");
            var author = commit is { } c ? GitHubJson.Obj(c, "author") : null;
            list.Add(new CommitInfo(
                sha,
                author is { } a ? GitHubJson.Str(a, "name", 256) : null,
                author is { } d ? GitHubJson.Str(d, "date", 64) : null,
                commit is { } m ? GitHubJson.Str(m, "message", 65536) : null));
        }

        return list;
    }

    /// <summary>A single commit: its parents (full ids) and who wrote it. Null when it is not a commit.</summary>
    internal static (IReadOnlyList<string> Parents, CommitInfo Info)? SingleCommit(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || GitHubJson.CommitId(GitHubJson.Str(document.RootElement, "sha", 64)) is not { } sha
            || GitHubJson.Arr(document.RootElement, "parents") is not { } parentArray)
        {
            return null;
        }

        var parents = new List<string>();
        foreach (var parent in parentArray.EnumerateArray().Take(16))
        {
            if (GitHubJson.CommitId(GitHubJson.Str(parent, "sha", 64)) is not { } id)
            {
                return null;
            }

            parents.Add(id);
        }

        var commit = GitHubJson.Obj(document.RootElement, "commit");
        var author = commit is { } c ? GitHubJson.Obj(c, "author") : null;
        return (parents, new CommitInfo(sha, author is { } a ? GitHubJson.Str(a, "name", 256) : null, author is { } d ? GitHubJson.Str(d, "date", 64) : null, commit is { } m ? GitHubJson.Str(m, "message", 65536) : null));
    }

    /// <summary>The commits of <paramref name="body"/> that revert one of <paramref name="mergeCommits"/> by the standard line of git: «This reverts commit &lt;sha&gt;.» Null when it is not a list.</summary>
    internal static IReadOnlyList<string>? Reverts(string? body, IReadOnlyCollection<string> mergeCommits)
    {
        var commits = Commits(body);
        if (commits is null)
        {
            return null;
        }

        return [.. commits.Where(c => c.Message is { } message && RevertLine().Matches(message).Any(m => mergeCommits.Contains(m.Groups["sha"].Value, StringComparer.Ordinal))).Select(c => c.Sha)];
    }

    /// <summary>The check runs of a commit (<c>check-runs</c>): how many pass, fail or wait, and the names of the failing ones for the screen. Null when it is not that shape.</summary>
    internal static ChecksReading? CheckRuns(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || GitHubJson.Arr(document.RootElement, "check_runs") is not { } runs)
        {
            return null;
        }

        int passing = 0, failing = 0, pending = 0, seen = 0;
        var names = new List<string>();
        foreach (var run in runs.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            seen++;
            var status = GitHubJson.Str(run, "status", 32);
            var conclusion = GitHubJson.Str(run, "conclusion", 32);
            if (status != "completed")
            {
                pending++;
            }
            else if (conclusion is "success" or "neutral" or "skipped")
            {
                passing++;
            }
            else if (conclusion is "failure" or "cancelled" or "timed_out" or "startup_failure")
            {
                failing++;
                AddName(names, GitHubJson.Str(run, "name", 512));
            }
            else
            {
                // action_required, stale and anything outside the vocabulary: not a pass.
                pending++;
            }
        }

        var total = GitHubJson.Int(document.RootElement, "total_count") ?? seen;
        return new ChecksReading(passing, failing, pending, total > seen, names);
    }

    /// <summary>The commit statuses of a commit (<c>status</c>), without the one of the verdict of Agent Studio itself (it is not a check of the project).</summary>
    internal static ChecksReading? CombinedStatus(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || GitHubJson.Arr(document.RootElement, "statuses") is not { } statuses)
        {
            return null;
        }

        int passing = 0, failing = 0, pending = 0, seen = 0;
        var names = new List<string>();
        foreach (var status in statuses.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            seen++;
            if (GitHubJson.Str(status, "context", 512) == GitHubRequestBodies.VerdictContext)
            {
                continue;
            }

            switch (GitHubJson.Str(status, "state", 32))
            {
                case "success":
                    passing++;
                    break;
                case "failure" or "error":
                    failing++;
                    AddName(names, GitHubJson.Str(status, "context", 512));
                    break;
                default:
                    pending++;
                    break;
            }
        }

        var total = GitHubJson.Int(document.RootElement, "total_count") ?? seen;
        return new ChecksReading(passing, failing, pending, total > seen, names);
    }

    private static void AddName(List<string> names, string? name)
    {
        if (names.Count < MaxNames && GitHubJson.Display(name) is { } clean)
        {
            names.Add(clean);
        }
    }

    /// <summary>The events of a page of the timeline of a pull request that the adapter reads, and how many items the page had. Null when it is not a list.</summary>
    internal static TimelinePage? Timeline(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var events = new List<TimelineEvent>();
        var items = 0;
        foreach (var item in document.RootElement.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            items++;
            switch (GitHubJson.Str(item, "event", 64))
            {
                case "head_ref_force_pushed":
                    events.Add(TimelineEvent.ForcePushed);
                    break;
                case "head_ref_deleted":
                    events.Add(TimelineEvent.HeadRefDeleted);
                    break;
                case "head_ref_restored":
                    events.Add(TimelineEvent.HeadRefRestored);
                    break;
            }
        }

        return new TimelinePage(events, items);
    }

    /// <summary>The deployments of a commit (id and environment, the environment for the screen), newest first as the platform gives them. Null when it is not a list.</summary>
    internal static IReadOnlyList<DeploymentEntry>? Deployments(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<DeploymentEntry>();
        foreach (var item in document.RootElement.EnumerateArray().Take(20))
        {
            if (GitHubJson.Long(item, "id") is { } id and > 0)
            {
                list.Add(new DeploymentEntry(id, GitHubJson.Display(GitHubJson.Str(item, "environment", 256)) ?? string.Empty));
            }
        }

        return list;
    }

    /// <summary>The state of the latest status of a deployment (the highest id), in the closed vocabulary. Null when it is not a list; <see cref="DeploymentState.Unknown"/> when there is none or it is not known.</summary>
    internal static DeploymentState? DeploymentStateOf(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        long best = -1;
        string? state = null;
        foreach (var item in document.RootElement.EnumerateArray().Take(10))
        {
            if (GitHubJson.Long(item, "id") is { } id && id > best)
            {
                best = id;
                state = GitHubJson.Str(item, "state", 32);
            }
        }

        return state switch
        {
            "success" => Domain.Hosting.DeploymentState.Succeeded,
            "failure" or "error" => Domain.Hosting.DeploymentState.Failed,
            "queued" or "in_progress" or "pending" => Domain.Hosting.DeploymentState.Pending,
            "inactive" => Domain.Hosting.DeploymentState.Inactive,
            _ => Domain.Hosting.DeploymentState.Unknown,
        };
    }

    /// <summary>The id and the commit of a merge that was done (<c>merged: true</c>), or null.</summary>
    internal static string? MergedSha(string? body)
    {
        using var document = GitHubJson.Parse(body);
        return document is not null && GitHubJson.Bool(document.RootElement, "merged") == true ? GitHubJson.CommitId(GitHubJson.Str(document.RootElement, "sha", 64)) : null;
    }

    /// <summary>The comments of a page that carry <paramref name="mark"/> in their first line and were written by <paramref name="author"/>: the id of the first. Null inside the result when none; Valid false when it is not a list.</summary>
    internal static (bool Valid, long? Id, int Items) CommentWithMark(string? body, string mark, RemoteLogin author)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (false, null, 0);
        }

        var items = 0;
        long? found = null;
        foreach (var item in document.RootElement.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            items++;
            var id = GitHubJson.Long(item, "id");
            if (found is not null || id is null or <= 0 || GitHubJson.Str(item, "body", 70000) is not { } text)
            {
                continue;
            }

            var firstLine = text.AsSpan();
            var end = firstLine.IndexOfAny('\r', '\n');
            if (end >= 0)
            {
                firstLine = firstLine[..end];
            }

            if (firstLine.SequenceEqual(mark) && GitHubJson.Obj(item, "user") is { } user && string.Equals(GitHubJson.Str(user, "login", 64), author.Value, StringComparison.OrdinalIgnoreCase))
            {
                found = id;
            }
        }

        return (true, found, items);
    }

    /// <summary>The commit a branch points to (<c>branches/{branch}</c>), or null when the body is not a branch.</summary>
    internal static string? BranchHead(string? body)
    {
        using var document = GitHubJson.Parse(body);
        return document is not null && GitHubJson.Obj(document.RootElement, "commit") is { } commit ? GitHubJson.CommitId(GitHubJson.Str(commit, "sha", 64)) : null;
    }

    /// <summary>The id of a comment that was just written.</summary>
    internal static long? CommentId(string? body)
    {
        using var document = GitHubJson.Parse(body);
        return document is not null && GitHubJson.Long(document.RootElement, "id") is { } id and > 0 ? id : null;
    }
}
