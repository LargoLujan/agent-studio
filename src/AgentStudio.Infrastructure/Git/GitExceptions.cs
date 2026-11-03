namespace AgentStudio.Infrastructure.Git;

/// <summary>git.exe is missing, or older than the commands Agent Studio relies on (<c>merge-tree --write-tree</c>).</summary>
public sealed class GitUnavailableException : InvalidOperationException
{
    public GitUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The repository uses a format whose writes Agent Studio cannot check (P-02): refs kept in the <c>reftable</c> backend
/// rather than in files, where the checks of every ref, reflog and administrative file assume the files backend. Nothing
/// was run against it.
/// </summary>
public sealed class UnsupportedRepositoryException : InvalidOperationException
{
    public UnsupportedRepositoryException(string message)
        : base(message)
    {
    }
}

/// <summary>A git command failed: non-zero exit code, or it timed out (then <see cref="ExitCode"/> is -1).</summary>
public class GitCommandException : InvalidOperationException
{
    public GitCommandException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>
/// git could not take a lock because its <c>*.lock</c> file already exists: a git process was killed while it held it,
/// or one is still working on the repository. Agent Studio never deletes git lock files; <see cref="LockFile"/> says
/// which one to delete by hand once no git process is running.
/// </summary>
public sealed class GitLockFileException : GitCommandException
{
    public GitLockFileException(string message, int exitCode, string standardError, string lockFile)
        : base(message, exitCode, standardError)
    {
        LockFile = lockFile;
    }

    /// <summary>Full path of the lock file.</summary>
    public string LockFile { get; }
}

/// <summary>
/// A mutating operation was refused because the target is not a worktree owned by Agent Studio: the user's main
/// working tree, a worktree outside the configured root, a worktree without the Agent Studio lock reason, or a lease
/// that does not match what git reports.
/// </summary>
public sealed class WorktreeOwnershipException : InvalidOperationException
{
    public WorktreeOwnershipException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Something already exists where a worktree or ref should be created, and it is not what the request describes
/// (another base commit, another lock reason, a foreign directory). The operation fails closed.
/// </summary>
public sealed class WorktreeMismatchException : InvalidOperationException
{
    public WorktreeMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A delivery to the user's branch was asked without the approval it needs: no request or no answer, an answer that is
/// not an explicit yes from someone named, a request below critical risk, an approval asked for another delivery or
/// answering another request, or one already used. Nothing was run.
/// </summary>
public sealed class DeliveryNotApprovedException : InvalidOperationException
{
    public DeliveryNotApprovedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A fast-forward of a ref was refused because the new commit does not descend from the commit the ref was expected at:
/// it would drop commits. Nothing moved.
/// </summary>
public sealed class NotFastForwardException : InvalidOperationException
{
    public NotFastForwardException(string refName, string from, string to)
        : base($"Refusing to move '{refName}' from {from} to {to}: {to} does not descend from {from}, so it is not a fast-forward.")
    {
        RefName = refName;
        From = from;
        To = to;
    }

    public string RefName { get; }

    public string From { get; }

    public string To { get; }
}

/// <summary>
/// An internal git command was not run because the effective configuration names a program that git could run with the
/// user's permissions and that the repository or a worktree controls (ADR-0012): a filter, a merge driver, a textconv or
/// another callback whose command is a relative path, a path inside the repository or the worktree root, or a program
/// that would read a script from there. <see cref="Refusals"/> says which entry and why; values are never repeated whole.
/// </summary>
public sealed class UnsafeGitCallbackException : InvalidOperationException
{
    public UnsafeGitCallbackException(string message, IReadOnlyList<GitCallbackRefusal> refusals)
        : base(message)
    {
        Refusals = refusals;
    }

    public IReadOnlyList<GitCallbackRefusal> Refusals { get; }
}

/// <summary>
/// The agent's worktree holds an embedded git repository (a directory other than the worktree root that contains a
/// <c>.git</c>, staged as a gitlink). Agent Studio refuses to commit it: a later git command could spawn a child git
/// inside it with configuration the agent controls (N-01), and a gitlink the system never verified has no place in an
/// integration. <see cref="Paths"/> lists the offending directories.
/// </summary>
public sealed class EmbeddedRepositoryException : InvalidOperationException
{
    public EmbeddedRepositoryException(string message, IReadOnlyList<string> paths)
        : base(message)
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }
}

/// <summary>A compare-and-swap on a ref failed: the ref no longer holds the value the caller expected.</summary>
public sealed class StaleRefException : InvalidOperationException
{
    public StaleRefException(string refName, string expected, string? actual)
        : base($"Ref '{refName}' was expected at {expected} but is at {actual ?? "(missing)"}.")
    {
        RefName = refName;
        Expected = expected;
        Actual = actual;
    }

    public string RefName { get; }

    public string Expected { get; }

    public string? Actual { get; }
}
