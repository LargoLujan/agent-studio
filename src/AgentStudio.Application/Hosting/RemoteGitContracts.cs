using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Hosting;

// The remote executor of git (Phase 10, docs/21 D-03 to D-08, ADR-0064). Three members and no overload with an argument: the
// transport of the executor gives the address, the code writes the refspec, and nothing here can force.

/// <summary>A key of the configuration of the repository that stops a publication, with its scope and the file it comes from. Never its value (docs/21 D-05): it could be a secret.</summary>
public sealed record RemoteConfigRefusal(string Key, string Scope, string OriginFile);

/// <summary>
/// What the inspection of the repository found before a push or an <c>ls-remote</c> (docs/21 D-05 and D-08): whether it is accepted, the keys that refuse it, whether
/// the project uses Git LFS, and a stable code when the inspection itself failed (closed: an inspection that fails does not publish).
/// </summary>
public sealed record RemoteGitInspection(bool Accepted, IReadOnlyList<RemoteConfigRefusal> Refusals, bool UsesLfs, string? FailureCode);

/// <summary>How a push ended. Appended, never reordered.</summary>
public enum RemotePushOutcome
{
    Pushed,
    AlreadyUpToDate,
    RejectedNotFastForward,
    CredentialsNeeded,
    Unreachable,
    Redirected,
    ConfigurationRefused,
    LfsRefused,
    NotConfirmed,
    Failed,
}

/// <summary>
/// What to push: the repository it goes to, the run and the commit. No force, no refspec, no branch name and no address: the transport of the executor
/// gives the address and the code writes <c>&lt;commit&gt;:refs/heads/agentstudio/run-&lt;id&gt;</c>.
/// </summary>
public sealed record RemotePush(ProjectContext Project, RemoteRepository Destination, RunId Run, string Commit);

/// <param name="FailureCode">A stable code of why it did not push, when the outcome alone does not say it (<see cref="RemotePushOutcome.Failed"/> above all); never text of git or of the remote.</param>
public sealed record RemotePushResult(RemotePushOutcome Outcome, string? RemoteCommit, RemoteGitInspection Inspection, string? FailureCode = null);

/// <summary>What <c>ls-remote</c> said of a branch: whether the remote was reached, the commit the branch points at (null when it does not exist) and a stable code of the failure.</summary>
public sealed record RemoteBranchRead(bool Reached, string? Commit, string? FailureCode);

/// <summary>
/// The remote executor of git (docs/21 D-03): three members and no overload with arguments. It is separate from the internal git layer, which never opens a
/// transport. Only the policy gate reaches it (an architecture test checks it).
/// </summary>
public interface IRemoteGitRunner
{
    Task<RemoteGitInspection> InspectAsync(ProjectContext project, RemoteRepository destination, CancellationToken ct);

    Task<RemoteBranchRead> ReadBranchAsync(ProjectContext project, RemoteRepository destination, RemoteBranch branch, CancellationToken ct);

    Task<RemotePushResult> PushRunBranchAsync(RemotePush push, CancellationToken ct);
}
