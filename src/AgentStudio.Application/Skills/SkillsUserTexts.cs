namespace AgentStudio.Application.Skills;

/// <summary>
/// The texts of the health checks of a skill that the doctor and the local reader give (docs/17 D-47, ADR-0054): a code and its arguments in
/// <see cref="AgentStudio.Domain.Skills.SkillHealthCheck.TextCode"/>, painted by the desktop in the language of the person (<c>Text_</c> + the code, in
/// <c>SkillsStrings</c>). The checks of the frontmatter, the license and the pinning, still sentences of the domain, are listed as pending in docs/18.
/// </summary>
public static class SkillsUserTexts
{
    // ---- the doctor against GitHub (GitHubSkillHealthChecker) ----
    public const string DoctorOff = "skills.doctor-off";
    public const string DoctorInvalidOrigin = "skills.doctor-invalid-origin";
    public const string DoctorNoNetwork = "skills.doctor-no-network";
    public const string DoctorForbidden = "skills.doctor-forbidden";
    public const string DoctorNotFound = "skills.doctor-not-found";
    public const string DoctorUpstreamNotFound = "skills.doctor-upstream-not-found";

    /// <summary>Argument 0: the instant the limit is reset.</summary>
    public const string DoctorRateLimited = "skills.doctor-rate-limited";
    public const string DoctorRateLimitedSoon = "skills.doctor-rate-limited-soon";
    public const string DoctorRedirectRefused = "skills.doctor-redirect-refused";
    public const string DoctorUnreadable = "skills.doctor-unreadable";
    public const string DoctorBadToken = "skills.doctor-bad-token";
    public const string DoctorArchived = "skills.doctor-archived";
    public const string DoctorNotArchived = "skills.doctor-not-archived";
    public const string DoctorCommitGone = "skills.doctor-commit-gone";
    public const string DoctorBranchGone = "skills.doctor-branch-gone";
    public const string DoctorFolderMissing = "skills.doctor-folder-missing";
    public const string DoctorUpToDate = "skills.doctor-up-to-date";
    public const string DoctorUpdateToReview = "skills.doctor-update-to-review";

    // ---- the local reader (SkillHealthReader) ----
    public const string ReaderUpstreamUnchecked = "skills.reader-upstream-unchecked";
    public const string ReaderArchivedUnchecked = "skills.reader-archived-unchecked";
    public const string ReaderScriptsNoCopy = "skills.reader-scripts-no-copy";
    public const string ReaderScriptsNoReader = "skills.reader-scripts-no-reader";
    public const string ReaderScriptsNone = "skills.reader-scripts-none";

    /// <summary>Argument 0: the number of scripts.</summary>
    public const string ReaderScriptsReviewed = "skills.reader-scripts-reviewed";

    /// <summary>Argument 0: the number of scripts.</summary>
    public const string ReaderScriptsUnreviewed = "skills.reader-scripts-unreviewed";

    /// <summary>Argument 0: the number of scripts.</summary>
    public const string ReaderScriptsChanged = "skills.reader-scripts-changed";
    public const string ReaderScriptsUnreadable = "skills.reader-scripts-unreadable";
    public const string ReaderUnreadNoCopy = "skills.reader-unread-no-copy";
    public const string ReaderUnreadNoFolder = "skills.reader-unread-no-folder";
    public const string ReaderUnreadFolderLink = "skills.reader-unread-folder-link";
    public const string ReaderUnreadFileMissing = "skills.reader-unread-file-missing";
    public const string ReaderUnreadFileLink = "skills.reader-unread-file-link";
    public const string ReaderUnreadFailed = "skills.reader-unread-failed";
    public const string ReaderSizeUnread = "skills.reader-size-unread";

    /// <summary>Argument 0: the size in KiB.</summary>
    public const string ReaderTooLargeFrontmatter = "skills.reader-too-large-frontmatter";

    /// <summary>Argument 0: the size in KiB.</summary>
    public const string ReaderTooLargeSize = "skills.reader-too-large-size";

    /// <summary>Every code, for the tests that check each has its text in every language.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DoctorOff, DoctorInvalidOrigin, DoctorNoNetwork, DoctorForbidden, DoctorNotFound, DoctorUpstreamNotFound, DoctorRateLimited, DoctorRateLimitedSoon,
        DoctorRedirectRefused, DoctorUnreadable, DoctorBadToken, DoctorArchived, DoctorNotArchived, DoctorCommitGone, DoctorBranchGone, DoctorFolderMissing,
        DoctorUpToDate, DoctorUpdateToReview, ReaderUpstreamUnchecked, ReaderArchivedUnchecked, ReaderScriptsNoCopy, ReaderScriptsNoReader, ReaderScriptsNone,
        ReaderScriptsReviewed, ReaderScriptsUnreviewed, ReaderScriptsChanged, ReaderScriptsUnreadable, ReaderUnreadNoCopy, ReaderUnreadNoFolder,
        ReaderUnreadFolderLink, ReaderUnreadFileMissing, ReaderUnreadFileLink, ReaderUnreadFailed, ReaderSizeUnread, ReaderTooLargeFrontmatter, ReaderTooLargeSize,
    ];
}
