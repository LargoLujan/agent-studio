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

    /// <summary>The summary of a list of reviews, or null when the body is not a list.</summary>
    internal static ReviewsSummary? Read(string? body)
    {
        using var document = GitHubJson.Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var decisive = new Dictionary<string, ReviewVerdict?>(StringComparer.OrdinalIgnoreCase);
        var invalidAccounts = 0;
        var commented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in document.RootElement.EnumerateArray())
        {
            if (VerdictOf(GitHubJson.Str(review, "state", 32)) is not { } verdict)
            {
                continue;
            }

            var login = GitHubJson.Obj(review, "user") is { } user ? GitHubJson.Str(user, "login", 64) : null;
            var key = RemoteLogin.TryParse(login)?.Value;
            if (key is null)
            {
                // An account that does not pass the rule is counted, not named (docs/21 D-12: «no consta»).
                key = "\u0001invalid" + (++invalidAccounts).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (verdict == ReviewVerdict.Commented)
            {
                commented.Add(key);
            }
            else
            {
                decisive[key] = verdict;
            }
        }

        var approvers = decisive.Where(p => p.Value == ReviewVerdict.Approved && !p.Key.StartsWith('\u0001')).Select(p => RemoteLogin.TryParse(p.Key)!).Take(100).ToList();
        var approved = decisive.Count(p => p.Value == ReviewVerdict.Approved);
        var changes = decisive.Count(p => p.Value == ReviewVerdict.ChangesRequested);
        return new ReviewsSummary(approved, changes, commented.Count, approvers);
    }
}
