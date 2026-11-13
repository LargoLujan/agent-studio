using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Domain.Tracing;

/// <summary>
/// What a node of the trace can be (docs/16 D-29): the things a link joins. The names are written to the log, so new kinds are
/// appended, never inserted (Phase 10 appends the pull request).
/// </summary>
public enum TraceNodeKind
{
    Requirement,
    BacklogItem,
    Milestone,
    Run,
    Task,
    Attempt,

    /// <summary>A commit by its full id: a commit of the system, a commit of the person made outside a run, or the one a delivery or an integration produced.</summary>
    Commit,
    Evidence,
    Verdict,
    Debate,
    Audit,

    /// <summary>A file of the repository at a commit (<c>path@commit</c>): an ADR, a document of <c>docs</c>, a diagram.</summary>
    Document,

    /// <summary>A pull request of the remote (Phase 10, docs/21 D-28): <c>github.com/&lt;owner&gt;/&lt;name&gt;#&lt;number&gt;</c>.</summary>
    PullRequest,
}

/// <summary>
/// A node of the trace by its kind and its id (docs/16 D-29): the stable id the log already has (a run id, a task id of a
/// run, an evidence id, a verdict id, a full commit id…). Ids that are only unique inside something carry what they are
/// unique in, so a link names exactly one thing wherever it is read: a task is <c>&lt;run&gt;/&lt;task&gt;</c>, an attempt
/// <c>&lt;run&gt;/&lt;task&gt;#&lt;n&gt;</c> and a document <c>&lt;path&gt;@&lt;commit&gt;</c>. A commit is always the full id:
/// 40 hexadecimal characters (SHA-1) or 64 (SHA-256), in lowercase, never an abbreviation, which stops being unique.
/// </summary>
public sealed record TraceRef
{
    /// <summary>The longest id a node has: a path inside a repository, and a commit.</summary>
    public const int MaxIdLength = 400;

    public TraceRef(TraceNodeKind kind, string id)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of node of the trace.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > MaxIdLength || id != id.Trim() || id.Any(char.IsControl))
        {
            throw new ArgumentException($"The id of a node of the trace is up to {MaxIdLength} characters on one line, without spaces at its ends.", nameof(id));
        }

        if (kind == TraceNodeKind.Commit && !IsFullCommitId(id))
        {
            throw new ArgumentException($"A commit is named by its full id (40 or 64 lowercase hexadecimal characters), not '{id}'.", nameof(id));
        }

        if (kind == TraceNodeKind.Document)
        {
            RequireDocument(id);
        }

        if (kind == TraceNodeKind.PullRequest && PullRequestRef.TryParseTraceId(id) is null)
        {
            throw new ArgumentException($"A pull request is named 'github.com/<owner>/<name>#<number>', not '{id}'.", nameof(id));
        }

        Kind = kind;
        Id = id;
    }

    public TraceNodeKind Kind { get; }

    public string Id { get; }

    public override string ToString() => $"{Kind}:{Id}";

    public static TraceRef Requirement(string id) => new(TraceNodeKind.Requirement, id);

    public static TraceRef BacklogItem(string id) => new(TraceNodeKind.BacklogItem, id);

    public static TraceRef Milestone(string id) => new(TraceNodeKind.Milestone, id);

    public static TraceRef Run(string run) => new(TraceNodeKind.Run, run);

    /// <summary>A task of a run: <c>&lt;run&gt;/&lt;task&gt;</c>.</summary>
    public static TraceRef Task(string run, string task) => new(TraceNodeKind.Task, Join(run, task, "/"));

    /// <summary>An attempt of a task of a run: <c>&lt;run&gt;/&lt;task&gt;#&lt;attempt&gt;</c>, the attempt from 1.</summary>
    public static TraceRef Attempt(string run, string task, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return new TraceRef(TraceNodeKind.Attempt, Join(run, task, "/") + "#" + attempt.ToString(CultureInfo.InvariantCulture));
    }

    public static TraceRef Commit(string fullId) => new(TraceNodeKind.Commit, fullId);

    public static TraceRef Evidence(string id) => new(TraceNodeKind.Evidence, id);

    public static TraceRef Verdict(string id) => new(TraceNodeKind.Verdict, id);

    public static TraceRef Debate(string id) => new(TraceNodeKind.Debate, id);

    public static TraceRef Audit(string id) => new(TraceNodeKind.Audit, id);

    /// <summary>A file of the repository at a commit: <c>&lt;path&gt;@&lt;commit&gt;</c>, with the path relative, in forward slashes and without <c>..</c>.</summary>
    public static TraceRef Document(string path, string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new TraceRef(TraceNodeKind.Document, path.Replace('\\', '/') + "@" + commit);
    }

    /// <summary>A pull request of the remote: <c>github.com/&lt;owner&gt;/&lt;name&gt;#&lt;number&gt;</c> (Phase 10).</summary>
    public static TraceRef PullRequest(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return new TraceRef(TraceNodeKind.PullRequest, pullRequest.TraceId);
    }

    /// <summary>Whether <paramref name="id"/> is a full commit id: 40 or 64 lowercase hexadecimal characters.</summary>
    public static bool IsFullCommitId(string? id) =>
        id is { Length: 40 or 64 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string Join(string first, string second, string separator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);
        return first + separator + second;
    }

    private static void RequireDocument(string id)
    {
        var at = id.LastIndexOf('@');
        if (at <= 0 || !IsFullCommitId(id[(at + 1)..]))
        {
            throw new ArgumentException($"A document is named '<path>@<full commit id>', not '{id}'.", nameof(id));
        }

        var path = id[..at];
        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"The path of a document is relative to the repository, in forward slashes and without empty, '.' or '..' segments: '{path}'.", nameof(id));
        }
    }
}
