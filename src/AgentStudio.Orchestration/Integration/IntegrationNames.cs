using System.Text.RegularExpressions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Integration;

/// <summary>
/// The names Agent Studio uses in the user's repository (docs/13 section 5, ADR-0012): hidden refs under
/// <c>refs/agentstudio/&lt;run&gt;/</c>, the visible integration branch <c>agentstudio/run-&lt;run&gt;</c> and the
/// <c>_integration</c> worktree. Only refs that pass <see cref="IsAgentStudioRef"/> are ever created, moved or deleted.
/// </summary>
public static partial class IntegrationNames
{
    /// <summary>The worktree where the integration is checked out and verified.</summary>
    public const string IntegrationWorktree = "_integration";

    public const string HiddenRefPrefix = "refs/agentstudio/";

    public const string BranchRefPrefix = "refs/heads/agentstudio/";

    // The part after the prefix: path segments of safe characters. More checks in IsAgentStudioRef.
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._/-]{0,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RefTailPattern();

    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdPattern();

    /// <summary>The visible branch of the run, <c>agentstudio/run-&lt;run&gt;</c>.</summary>
    public static string BranchName(RunId run) => "agentstudio/run-" + Value(run);

    /// <summary>The full ref of the run's branch, <c>refs/heads/agentstudio/run-&lt;run&gt;</c>.</summary>
    public static string BranchRef(RunId run) => "refs/heads/" + BranchName(run);

    /// <summary>The prefix of the hidden refs of the run, <c>refs/agentstudio/&lt;run&gt;/</c>.</summary>
    public static string RunRefPrefix(RunId run) => HiddenRefPrefix + Value(run) + "/";

    /// <summary>
    /// The candidate of an integration task, <c>refs/agentstudio/&lt;run&gt;/_candidate/&lt;task&gt;</c> (ADR-0012): where its
    /// merges go and what is verified before the run's branch moves to it. It also keeps a candidate that failed its
    /// verification, and the partial head of a conflict.
    /// </summary>
    public static string CandidateRef(RunId run, TaskId task) =>
        RunRefPrefix(run) + "_candidate/" + (task.Value ?? throw new ArgumentException("The task id is empty.", nameof(task)));

    /// <summary>True for the run's branch and for every hidden ref of the run.</summary>
    public static bool BelongsToRun(string refName, RunId run) =>
        refName is not null
        && (string.Equals(refName, BranchRef(run), StringComparison.Ordinal) || refName.StartsWith(RunRefPrefix(run), StringComparison.Ordinal))
        && IsAgentStudioRef(refName);

    /// <summary>
    /// True for a well-formed ref under <c>refs/agentstudio/</c> or <c>refs/heads/agentstudio/</c>: never a user's
    /// branch or tag, never anything git could read as an option, a range or a reflog expression.
    /// </summary>
    public static bool IsAgentStudioRef(string refName)
    {
        if (refName is null)
        {
            return false;
        }

        string tail;
        if (refName.StartsWith(HiddenRefPrefix, StringComparison.Ordinal))
        {
            tail = refName[HiddenRefPrefix.Length..];
        }
        else if (refName.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
        {
            tail = refName[BranchRefPrefix.Length..];
        }
        else
        {
            return false;
        }

        return RefTailPattern().IsMatch(tail)
            && !tail.Contains("..", StringComparison.Ordinal)
            && !tail.Contains("//", StringComparison.Ordinal)
            && !tail.Contains("/.", StringComparison.Ordinal)
            && !tail.Contains(".lock/", StringComparison.Ordinal)
            && !tail.EndsWith('/')
            && !tail.EndsWith('.')
            && !tail.EndsWith(".lock", StringComparison.Ordinal);
    }

    /// <summary>A full object id (40 or 64 hexadecimal characters), never an abbreviation or a name.</summary>
    public static bool IsObjectId(string value) => value is not null && ObjectIdPattern().IsMatch(value);

    private static string Value(RunId run) =>
        run.Value ?? throw new ArgumentException("The run id is empty.", nameof(run));
}
