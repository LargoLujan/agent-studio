using System.Text.Json;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The reader of the reviews of a pull request (docs/21 D-21 and D-09): the only file of the adapter that names the states of a review of GitHub, because it reads them as a fact of other people.
/// The adapter never sends a review; <c>GET …/reviews</c> is the only entry of the catalog that touches that path. Each person counts once, by their last decisive review: an approval, a request
/// for changes or a dismissal; a comment does not undo either of the first two and is counted apart.
/// </summary>
internal static class GitHubReviewReader
{
    /// <summary>The state of a review as GitHub names it, in the common model; null for a state outside the vocabulary (a pending review, or one the adapter does not know).</summary>
    internal static ReviewVerdict? VerdictOf(string? state) => state switch
    {
        "APPROVED" => ReviewVerdict.Approved,
        "CHANGES_REQUESTED" => ReviewVerdict.ChangesRequested,
        "COMMENTED" => ReviewVerdict.Commented,
        "DISMISSED" => ReviewVerdict.Dismissed,
        _ => null,
    };

    /// <summary>One review that decides or comments, by the raw login of its author (null when the review carries none).</summary>
    internal sealed record ReviewEntry(string? Login, ReviewVerdict Verdict);

    /// <summary>A page of reviews: how many items it had (the ones that are not in the vocabulary count too: they fill the page) and the entries that matter.</summary>
    internal sealed record ReviewPage(int Items, IReadOnlyList<ReviewEntry> Entries);

    /// <summary>The page of reviews, or null when the body is not a list.</summary>
    internal static ReviewPage? ReadPage(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<ReviewEntry>();
        var items = 0;
        foreach (var review in document.RootElement.EnumerateArray().Take(GitHubApiPath.PageSize))
        {
            items++;
            if (VerdictOf(GitHubJson.Str(review, "state", 32)) is not { } verdict)
            {
                continue;
            }

            var login = GitHubJson.Obj(review, "user") is { } user ? GitHubJson.Str(user, "login", 128) : null;
            entries.Add(new ReviewEntry(login, verdict));
        }

        return new ReviewPage(items, entries);
    }

    /// <summary>
    /// The summary of the reviews of every page, in the order the platform gave them. Each person counts once, by the <b>raw</b> login (an account that does not pass the rule of <see cref="RemoteLogin"/>, such as a
    /// bot, still is the same account in every review: its dismissal undoes its approval, and two approvals are one); only the accounts that pass the rule are named as approvers.
    /// </summary>
    internal static ReviewsSummary Summarize(IEnumerable<ReviewEntry> entries)
    {
        var decisive = new Dictionary<string, ReviewVerdict>(StringComparer.OrdinalIgnoreCase);
        var commented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anonymous = 0;
        foreach (var entry in entries)
        {
            var key = string.IsNullOrEmpty(entry.Login) ? "\u0001anonymous" + (++anonymous).ToString(System.Globalization.CultureInfo.InvariantCulture) : entry.Login;
            if (entry.Verdict == ReviewVerdict.Commented)
            {
                commented.Add(key);
            }
            else
            {
                decisive[key] = entry.Verdict;
            }
        }

        var approvers = decisive.Where(p => p.Value == ReviewVerdict.Approved && RemoteLogin.TryParse(p.Key) is not null).Select(p => RemoteLogin.TryParse(p.Key)!).Take(100).ToList();
        return new ReviewsSummary(
            decisive.Count(p => p.Value == ReviewVerdict.Approved),
            decisive.Count(p => p.Value == ReviewVerdict.ChangesRequested),
            commented.Count,
            approvers);
    }
}