using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// Names, paths and validation rules of the git layer. Everything that reaches a git command line from outside
/// (project ids, run ids, worktree names, object ids, ref names) is validated here first, so it can never be read
/// as an option.
/// </summary>
internal static partial class GitNaming
{
    internal const string LockReasonPrefix = "agent-studio";

    // \A ... \z on purpose: `$` would also accept a trailing newline.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    // Task ids (T1, build-2...) or one of the two system worktrees. A leading underscore is reserved for the system.
    [GeneratedRegex(@"\A(?:[A-Za-z][A-Za-z0-9_-]{0,31}|_base|_integration)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    // A task id as the plan validator accepts it: a letter, then letters, digits, '_' or '-', 32 at most. Never a system name.
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._/@-]{0,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RefTailPattern();

    [GeneratedRegex(@"\A\d{8}-([0-9a-f]{6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();

    internal static void ValidateProjectId(string projectId)
    {
        if (projectId is null || !SegmentPattern().IsMatch(projectId))
        {
            throw new ArgumentException($"Invalid project id '{projectId}': only letters, digits, '_' and '-' are allowed.", nameof(projectId));
        }
    }

    internal static void ValidateRun(RunId run)
    {
        if (run.Value is null || !SegmentPattern().IsMatch(run.Value))
        {
            throw new ArgumentException($"Invalid run id '{run.Value}': only letters, digits, '_' and '-' are allowed.", nameof(run));
        }
    }

    internal static void ValidateName(string name)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException($"Invalid worktree name '{name}': expected a task id, '_base' or '_integration'.", nameof(name));
        }
    }

    /// <summary>The id of a task of the plan (<c>I1</c>, <c>integra-2</c>...); <c>_base</c>, <c>_integration</c> and anything else are refused.</summary>
    internal static void ValidateTask(TaskId task)
    {
        if (task.Value is null || !TaskPattern().IsMatch(task.Value))
        {
            throw new ArgumentException($"Invalid task id '{task.Value}': it starts with a letter and has at most 32 letters, digits, '_' or '-'.", nameof(task));
        }
    }

    internal static bool IsName(string value) => NamePattern().IsMatch(value);

    internal static bool IsSegment(string value) => SegmentPattern().IsMatch(value);

    internal static bool IsObjectId(string value) => value is not null && ObjectIdPattern().IsMatch(value);

    /// <summary>Full object ids only (never abbreviations or names), normalised to lower case.</summary>
    internal static string NormalizeObjectId(string value, string parameterName)
    {
        if (!IsObjectId(value))
        {
            throw new ArgumentException($"'{value}' is not a full object id (40 or 64 hexadecimal characters).", parameterName);
        }

        return value.ToLowerInvariant();
    }

    /// <summary>A commit id or a plain ref name; no ranges, no revision operators, never starts with '-'.</summary>
    internal static string ValidateRevision(string value, string parameterName)
    {
        if (value is null
            || !RevisionPattern().IsMatch(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || value.EndsWith('/')
            || value.EndsWith(".lock", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{value}' is not a valid commit id or ref name.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// What a read accepts as a revision: a full object id (normalised to lower case) or a well-formed ref of the Agent
    /// Studio namespace (expanded as <see cref="NormalizeAgentStudioRef"/> does). Anything else, a user's branch, an
    /// abbreviated id or something git could read as an option, a range or a reflog expression, is an invalid argument.
    /// </summary>
    internal static string NormalizeReadableRevision(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (IsObjectId(value))
        {
            return value.ToLowerInvariant();
        }

        if (!value.StartsWith("refs/agentstudio/", StringComparison.Ordinal)
            && !value.StartsWith("refs/heads/agentstudio/", StringComparison.Ordinal)
            && !value.StartsWith("agentstudio/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{value}' is neither a full commit id nor an Agent Studio ref.", parameterName);
        }

        return NormalizeAgentStudioRef(value, parameterName);
    }

    /// <summary>
    /// The only refs Agent Studio may move: <c>refs/agentstudio/**</c> and the branches <c>agentstudio/**</c>
    /// (<c>refs/heads/agentstudio/**</c>). A short branch name is expanded; anything else is refused, so a user's
    /// branches are never touched.
    /// </summary>
    internal static string NormalizeAgentStudioRef(string refName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refName, parameterName);

        string tail;
        string prefix;
        if (refName.StartsWith("refs/agentstudio/", StringComparison.Ordinal))
        {
            prefix = "refs/agentstudio/";
            tail = refName["refs/agentstudio/".Length..];
        }
        else if (refName.StartsWith("refs/heads/agentstudio/", StringComparison.Ordinal))
        {
            prefix = "refs/heads/agentstudio/";
            tail = refName["refs/heads/agentstudio/".Length..];
        }
        else if (refName.StartsWith("agentstudio/", StringComparison.Ordinal))
        {
            prefix = "refs/heads/agentstudio/";
            tail = refName["agentstudio/".Length..];
        }
        else
        {
            throw new WorktreeOwnershipException($"Refusing to move ref '{refName}': Agent Studio only moves refs/agentstudio/* and agentstudio/* branches.");
        }

        if (!RefTailPattern().IsMatch(tail)
            || tail.Contains("..", StringComparison.Ordinal)
            || tail.Contains("//", StringComparison.Ordinal)
            || tail.EndsWith('/')
            || tail.EndsWith('.')
            || tail.Contains("/.", StringComparison.Ordinal)
            || tail.Contains(".lock/", StringComparison.Ordinal)
            || tail.EndsWith(".lock", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{refName}' is not a valid Agent Studio ref name.", parameterName);
        }

        return prefix + tail;
    }

    /// <summary>Hidden ref that keeps the commits of a worktree alive: <c>refs/agentstudio/&lt;run&gt;/&lt;name&gt;</c>.</summary>
    internal static string TipRef(RunId run, string name) => $"refs/agentstudio/{run.Value}/{name}";

    /// <summary>
    /// Hidden ref that records the commit a worktree was created at. The lock reason cannot carry it (its format is
    /// fixed by the design), and without it <c>ListOwnedAsync</c> and the reuse check of <c>EnsureAsync</c> could not
    /// know the base of an existing worktree. No worktree can be named '_start', so the directory never collides with
    /// a tip ref.
    /// </summary>
    internal static string StartRef(RunId run, string name) => $"refs/agentstudio/{run.Value}/_start/{name}";

    /// <summary>
    /// The candidate of an integration task (ADR-0012), <c>refs/agentstudio/&lt;run&gt;/_candidate/&lt;task&gt;</c>: where its
    /// merges go, and what is verified before the run's branch moves to it. No worktree can be named '_candidate', so the
    /// folder never collides with a tip ref.
    /// </summary>
    internal static string CandidateRef(RunId run, TaskId task) => $"refs/agentstudio/{run.Value}/_candidate/{task.Value}";

    /// <summary>The prefix of every hidden ref of a run, <c>refs/agentstudio/&lt;run&gt;/</c>.</summary>
    internal static string RunRefPrefix(RunId run) => $"refs/agentstudio/{run.Value}/";

    /// <summary>
    /// The visible branch of a run (ADR-0012), <c>refs/heads/agentstudio/run-&lt;run&gt;</c>: the target of the integration
    /// and the source of the delivery.
    /// </summary>
    internal static string RunBranchRef(RunId run) => $"refs/heads/agentstudio/run-{run.Value}";

    internal static string LockReason(string projectId, RunId run, string name) =>
        $"{LockReasonPrefix}:{projectId}:{run.Value}:{name}";

    internal static bool TryParseLockReason(string? reason, out LockReasonParts parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(reason))
        {
            return false;
        }

        var segments = reason.Split(':');
        if (segments.Length != 4
            || !string.Equals(segments[0], LockReasonPrefix, StringComparison.Ordinal)
            || !IsSegment(segments[1])
            || !IsSegment(segments[2])
            || !IsName(segments[3]))
        {
            return false;
        }

        parts = new LockReasonParts(segments[1], segments[2], segments[3]);
        return true;
    }

    /// <summary>First 8 hex characters of the SHA-256 of the repository root, so one root holds many repositories.</summary>
    internal static string RepoKey(string repoRoot) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(repoRoot).ToLowerInvariant())))[..8];

    /// <summary>The 6 hex characters at the end of a run id such as "20261015-a1b2c3"; any other format is hashed.</summary>
    internal static string RunKey(RunId run)
    {
        var match = RunIdPattern().Match(run.Value);
        return match.Success
            ? match.Groups[1].Value
            : Hex(SHA256.HashData(Encoding.UTF8.GetBytes(run.Value)))[..6];
    }

    internal static string WorktreePath(string worktreeRoot, string repoRoot, RunId run, string name) =>
        Path.Combine(worktreeRoot, RepoKey(repoRoot), RunKey(run), name);

    /// <summary>Full path, with Windows separators and without a trailing separator.</summary>
    internal static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    internal static bool IsUnder(string path, string root)
    {
        var fullPath = NormalizePath(path);
        var fullRoot = NormalizePath(root);
        return fullPath.Length > fullRoot.Length
            && fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            && (fullPath[fullRoot.Length] == Path.DirectorySeparatorChar || fullRoot.EndsWith(Path.DirectorySeparatorChar));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
}

/// <summary>The three fields of the lock reason <c>agent-studio:&lt;projectId&gt;:&lt;run&gt;:&lt;name&gt;</c>.</summary>
internal readonly record struct LockReasonParts(string ProjectId, string Run, string Name);
