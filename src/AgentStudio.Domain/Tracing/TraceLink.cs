using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Tracing;

/// <summary>Who asserts a link (docs/16 D-29): the system, for the ones it knows (a run launched from a milestone), or the person, for the rest and for the suggestions they confirm.</summary>
public enum TraceAssertedBy
{
    System,
    User,
}

/// <summary>
/// An explicit link of the trace (docs/16 D-29, F9-3): from one node to another, many to many, never edited. A link is
/// read in both directions (<see cref="TraceLinkSet.Around"/>); the direction is what the person said: from the commit
/// to the requirement it fulfils, from the milestone to the run it launched. Its id is derived from its content, so the
/// same link recorded twice is one, and retracting names it exactly.
/// </summary>
/// <param name="From">The node the link starts at.</param>
/// <param name="To">The node it ends at.</param>
/// <param name="AssertedBy">The system or the person.</param>
/// <param name="Reason">Why, in a few words: the person's or the system's, never an agent's.</param>
/// <param name="At">When it was recorded.</param>
public sealed record TraceLink(TraceRef From, TraceRef To, TraceAssertedBy AssertedBy, string Reason, DateTimeOffset At)
{
    /// <summary>The longest reason a link or a retraction carries.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The id of the link: the SHA-256 of its two ends and who asserts it. Not the time nor the reason: those can differ and the link is the same.</summary>
    public string Id => TraceLinks.IdOf(From, To, AssertedBy);

    /// <summary>The codes of what is wrong with the link; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (From is null || To is null)
        {
            problems.Add("trace.end-missing");
            return problems;
        }

        if (From == To)
        {
            problems.Add("trace.self-link");
        }

        if (!Enum.IsDefined(AssertedBy))
        {
            problems.Add("trace.asserted-by");
        }

        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > MaxReasonLength || Reason.Any(char.IsControl))
        {
            problems.Add("trace.reason");
        }

        return problems;
    }
}

/// <summary>The id of a link (docs/16 D-29).</summary>
public static class TraceLinks
{
    /// <summary>The SHA-256 of the kind and id of both ends and of who asserts it, each field with a length prefix.</summary>
    public static string IdOf(TraceRef from, TraceRef to, TraceAssertedBy assertedBy)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return ContentHash.Sha256Hex(ContentHash.Canonical("trace-link/1", from.Kind.ToString(), from.Id, to.Kind.ToString(), to.Id, assertedBy.ToString()));
    }
}

/// <summary>Where a suggestion was inferred from (docs/16 D-30). Names are written to the log when a person confirms one, so new sources are appended.</summary>
public enum TraceSuggestionSource
{
    /// <summary>A mention in the message of a commit (<c>T3</c>, the id of a run).</summary>
    CommitMessageMention,

    /// <summary>The trailers the system writes in its own commits (<see cref="CommitTrailers"/>).</summary>
    CommitTrailer,

    /// <summary>The name of a branch of a run (<c>agentstudio/run-…</c>).</summary>
    RunBranchName,

    /// <summary>A pull request that cites a milestone (Phase 10).</summary>
    PullRequestMention,
}

/// <summary>
/// A link the system infers and nobody confirmed (docs/16 D-30, F9-3). It is shown as «sugerido (inferido de …)», it counts
/// for nothing until a person confirms it (<see cref="Confirm"/>), and it never reaches the judge as a fact.
/// </summary>
/// <param name="From">The node it would start at.</param>
/// <param name="To">The node it would end at.</param>
/// <param name="Source">What it was inferred from.</param>
/// <param name="InferredFrom">The text it was inferred from, cut to <see cref="MaxInferredFromLength"/> characters: a piece of a commit message, a branch name.</param>
public sealed record TraceSuggestion(TraceRef From, TraceRef To, TraceSuggestionSource Source, string InferredFrom)
{
    /// <summary>The longest piece of text a suggestion quotes as its origin.</summary>
    public const int MaxInferredFromLength = 200;

    /// <summary>
    /// The link the person confirms: asserted by them, with the origin of the suggestion as its reason. A suggestion never
    /// becomes a link by itself: only this, called with their decision, makes one.
    /// </summary>
    public TraceLink Confirm(DateTimeOffset at)
    {
        var quoted = InferredFrom.Length <= MaxInferredFromLength ? InferredFrom : InferredFrom[..MaxInferredFromLength];
        var line = string.Concat(quoted.Where(c => !char.IsControl(c)));
        return new TraceLink(From, To, TraceAssertedBy.User, $"confirmed: {Source}: {line}".Trim(), at);
    }
}
