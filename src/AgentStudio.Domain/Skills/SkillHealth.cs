using System.Text.RegularExpressions;

namespace AgentStudio.Domain.Skills;

public enum SkillHealthStatus
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

/// <summary>Stable codes of the health checks.</summary>
public static class SkillHealthCodes
{
    /// <summary>SKILL.md frontmatter against the Agent Skills specification.</summary>
    public const string Frontmatter = "frontmatter";

    /// <summary>License is present and permissive/OSI.</summary>
    public const string License = "license";

    /// <summary>The upstream commit is pinned (40 hex).</summary>
    public const string Pinned = "pinned";

    /// <summary>Upstream still exists and has no unreviewed changes (needs network; produced by ISkillHealthChecker).</summary>
    public const string Upstream = "upstream";

    /// <summary>Upstream repository is archived or deleted (needs network).</summary>
    public const string Archived = "archived";

    /// <summary>The skill bundles executable scripts.</summary>
    public const string Scripts = "scripts";

    /// <summary>Body size recommendation (under 500 lines).</summary>
    public const string Size = "size";
}

/// <summary>
/// The codes of the texts the pure checks of the domain give (ADR-0054, P65): each is painted by the desktop from <c>Text_</c> + the code (<c>.</c> and <c>-</c> as <c>_</c>)
/// in <c>SkillsStrings</c>. They live here because Domain cannot see Application (ADR-0002); <c>SkillsUserTexts.All</c> lists them with the rest.
/// </summary>
public static class SkillHealthTextCodes
{
    // ---- the frontmatter (SkillFrontmatter.Validate) ----
    public const string FrontmatterNoHeader = "skills.frontmatter-no-header";
    public const string FrontmatterNameMissing = "skills.frontmatter-name-missing";

    /// <summary>Argument 0: the longest name allowed.</summary>
    public const string FrontmatterNameTooLong = "skills.frontmatter-name-too-long";
    public const string FrontmatterNameInvalid = "skills.frontmatter-name-invalid";

    /// <summary>Argument 0: the name; argument 1: the folder.</summary>
    public const string FrontmatterNameFolderMismatch = "skills.frontmatter-name-folder-mismatch";
    public const string FrontmatterDescriptionMissing = "skills.frontmatter-description-missing";

    /// <summary>Argument 0: the longest description allowed.</summary>
    public const string FrontmatterDescriptionTooLong = "skills.frontmatter-description-too-long";

    /// <summary>Argument 0: the key.</summary>
    public const string FrontmatterDuplicateKey = "skills.frontmatter-duplicate-key";
    public const string FrontmatterOk = "skills.frontmatter-ok";

    // ---- the size of the body (SkillFrontmatter.Validate): with the cost in context when the caller knows it ----

    /// <summary>Argument 0: the lines of the body; argument 1: the recommended maximum.</summary>
    public const string SizeLong = "skills.size-long";

    /// <summary>Argument 0: the lines of the body; argument 1: the recommended maximum; argument 2: the estimated tokens of the whole SKILL.md.</summary>
    public const string SizeLongCost = "skills.size-long-cost";

    /// <summary>Argument 0: the lines of the body.</summary>
    public const string SizeOk = "skills.size-ok";

    /// <summary>Argument 0: the lines of the body; argument 1: the estimated tokens of the whole SKILL.md.</summary>
    public const string SizeOkCost = "skills.size-ok-cost";

    // ---- the license and the pinning (SkillHealthChecks) ----
    public const string LicenseMissing = "skills.license-missing";

    /// <summary>Argument 0: the license as declared.</summary>
    public const string LicensePermissive = "skills.license-permissive";

    /// <summary>Argument 0: the license as declared.</summary>
    public const string LicenseCopyleft = "skills.license-copyleft";

    /// <summary>Argument 0: the license as declared.</summary>
    public const string LicenseRestricted = "skills.license-restricted";
    public const string PinnedOk = "skills.pinned-ok";
    public const string PinnedMissing = "skills.pinned-missing";

    /// <summary>Every code, for the tests that check each has its text in every language.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        FrontmatterNoHeader, FrontmatterNameMissing, FrontmatterNameTooLong, FrontmatterNameInvalid, FrontmatterNameFolderMismatch, FrontmatterDescriptionMissing,
        FrontmatterDescriptionTooLong, FrontmatterDuplicateKey, FrontmatterOk, SizeLong, SizeLongCost, SizeOk, SizeOkCost, LicenseMissing, LicensePermissive,
        LicenseCopyleft, LicenseRestricted, PinnedOk, PinnedMissing,
    ];
}
/// <summary>
/// One health check result. When <see cref="TextCode"/> is set, the text the person reads is that coded text with <see cref="TextArguments"/> (docs/17 D-47, ADR-0054): the
/// desktop paints it in the language of the person from its resources, and <see cref="Message"/> is only a technical fallback (the code). Without a code,
/// <c>Message</c> is shown as it is (a few messages of the desktop itself).
/// The arguments are text, numbers or an instant, never a sentence (<see cref="AgentStudio.Domain.Common.TextArguments"/>).
/// </summary>
public sealed record SkillHealthCheck(string Code, SkillHealthStatus Status, string Message, string? TextCode = null, IReadOnlyList<object>? TextArguments = null)
{
    /// <summary>A check whose text is a code: the fallback message is the code itself.</summary>
    /// <exception cref="ArgumentException">An argument of a type a coded text does not carry.</exception>
    public static SkillHealthCheck Coded(string code, SkillHealthStatus status, string textCode, params object[] arguments) =>
        new(code, status, textCode, textCode, AgentStudio.Domain.Common.TextArguments.Checked(arguments, nameof(arguments)));
}

/// <summary>
/// A set of checks. The global status is the worst one by severity Healthy &lt; Unknown &lt; Warning &lt; Critical
/// (an unverified check weighs more than a passed one); with no checks it is Unknown.
/// </summary>
public sealed record SkillHealthReport(IReadOnlyList<SkillHealthCheck> Checks)
{
    public SkillHealthStatus Status =>
        Checks.Count == 0 ? SkillHealthStatus.Unknown : Checks.MaxBy(c => Severity(c.Status))!.Status;

    private static int Severity(SkillHealthStatus status) => status switch
    {
        SkillHealthStatus.Healthy => 0,
        SkillHealthStatus.Unknown => 1,
        SkillHealthStatus.Warning => 2,
        _ => 3,
    };
}

/// <summary>Pure health checks that need no network.</summary>
public static partial class SkillHealthChecks
{
    [GeneratedRegex(@"\A[0-9a-fA-F]{40}\z")]
    private static partial Regex Sha1();

    // SPDX identifiers (lower-case). A license matches an id exactly or as "<id>-<suffix>" (e.g. bsd-3-clause).
    private static readonly string[] Permissive = ["mit", "apache-2.0", "bsd", "isc", "0bsd", "unlicense", "zlib"];

    private static readonly string[] Copyleft = ["gpl", "agpl", "lgpl", "mpl-2.0", "epl"];

    private static bool Matches(string normalized, string[] ids) =>
        ids.Any(id => normalized == id || normalized.StartsWith(id + "-", StringComparison.Ordinal));

    public static SkillHealthCheck CheckLicense(string? license)
    {
        if (string.IsNullOrWhiteSpace(license))
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.License, SkillHealthStatus.Warning, SkillHealthTextCodes.LicenseMissing);
        }

        var shown = license.Trim();
        var normalized = shown.ToLowerInvariant();
        if (Matches(normalized, Permissive))
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.License, SkillHealthStatus.Healthy, SkillHealthTextCodes.LicensePermissive, shown);
        }

        if (Matches(normalized, Copyleft))
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.License, SkillHealthStatus.Healthy, SkillHealthTextCodes.LicenseCopyleft, shown);
        }

        return SkillHealthCheck.Coded(SkillHealthCodes.License, SkillHealthStatus.Warning, SkillHealthTextCodes.LicenseRestricted, shown);
    }

    public static SkillHealthCheck CheckPinned(string? pinnedCommit) =>
        pinnedCommit is not null && Sha1().IsMatch(pinnedCommit)
            ? SkillHealthCheck.Coded(SkillHealthCodes.Pinned, SkillHealthStatus.Healthy, SkillHealthTextCodes.PinnedOk)
            : SkillHealthCheck.Coded(SkillHealthCodes.Pinned, SkillHealthStatus.Warning, SkillHealthTextCodes.PinnedMissing);
}
