namespace AgentStudio.Application.Hosting;

/// <summary>
/// The stable codes of the remote executor of git (docs/21 D-06, ADR-0054): what <see cref="RemoteGitInspection.FailureCode"/>, <see cref="RemoteBranchRead.FailureCode"/> and <see cref="RemotePushResult.FailureCode"/> carry. They are codes,
/// never a sentence, and never a piece of what the process wrote: the output of git is text that is not trusted, and no message cites it. Appended, never renamed: the screens paint each one from its own resource.
/// </summary>
public static class RemoteGitCodes
{
    /// <summary>The inspection of the configuration refused the repository: the keys are in the inspection.</summary>
    public const string ConfigurationRefused = "configuration-refused";

    /// <summary>The query of the configuration failed, or gave something that is not understood: closed, nothing is published.</summary>
    public const string InspectionFailed = "inspection-failed";

    /// <summary>The query of the configuration wrote more than the cap: closed.</summary>
    public const string InspectionOutputTooLarge = "inspection-output-too-large";

    /// <summary>No <c>git.exe</c> outside the open projects was found.</summary>
    public const string GitUnavailable = "git-unavailable";

    /// <summary>The empty file that every command receives as <c>core.hooksPath</c> is a link, a folder or has content.</summary>
    public const string UnsafeHooksFile = "hooks-file-unsafe";

    /// <summary>A folder of the executor (the temporary one, or the one that holds it and the hooks file) is a link: closed.</summary>
    public const string UnsafeFolder = "work-folder-unsafe";

    /// <summary>The folder of the project is not a folder that exists, or is not a git repository.</summary>
    public const string ProjectUnreadable = "project-unreadable";

    /// <summary>The address of the transport is empty, has a control character or starts with <c>-</c>.</summary>
    public const string InvalidAddress = "invalid-address";

    /// <summary>The commit is not 40 lowercase hexadecimal characters, or the run has no identifier.</summary>
    public const string InvalidCommit = "invalid-commit";

    /// <summary>The commit that goes up has a <c>.gitattributes</c> with <c>filter=lfs</c> and the filter is configured: the objects would not go up (docs/21 D-08).</summary>
    public const string LfsDetected = "lfs-detected";

    /// <summary>Whether the commit that goes up uses Git LFS could not be read (the command failed, ran out of time or wrote too much): closed, nothing is published.</summary>
    public const string LfsCheckFailed = "lfs-check-failed";

    /// <summary>The repository is a partial clone (<c>extensions.partialClone</c>, or a remote with <c>promisor</c> or <c>partialclonefilter</c>): git would fetch missing objects on demand from a remote the repository chooses, so nothing is published.</summary>
    public const string PartialCloneUnsupported = "partial-clone-unsupported";

    public const string TimedOut = "timed-out";

    public const string OutputTooLarge = "output-too-large";

    public const string CredentialsNeeded = "credentials-needed";

    public const string RejectedNotFastForward = "rejected-not-fast-forward";

    public const string RemoteUnreachable = "remote-unreachable";

    public const string Redirected = "redirected";

    public const string ProtocolRefused = "protocol-refused";

    /// <summary>The remote declined the update for a reason of its own (a hook, a protected branch), or the push ended in a way this executor does not know.</summary>
    public const string PushRejected = "push-rejected";

    public const string GitFailed = "git-failed";
}
