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

/// <summary>One health check result. <c>Message</c> is shown to the user (Spanish).</summary>
public sealed record SkillHealthCheck(string Code, SkillHealthStatus Status, string Message);

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
            return new(SkillHealthCodes.License, SkillHealthStatus.Warning, "Sin licencia declarada: revisa los términos de uso.");
        }

        var shown = license.Trim();
        var normalized = shown.ToLowerInvariant();
        if (Matches(normalized, Permissive))
        {
            return new(SkillHealthCodes.License, SkillHealthStatus.Healthy, $"Licencia permisiva: {shown}.");
        }

        if (Matches(normalized, Copyleft))
        {
            return new(SkillHealthCodes.License, SkillHealthStatus.Healthy, $"Licencia copyleft: válida para uso personal ({shown}).");
        }

        return new(SkillHealthCodes.License, SkillHealthStatus.Warning,
            $"Licencia no OSI o con restricciones ({shown}): revisa los términos antes de usarla.");
    }

    public static SkillHealthCheck CheckPinned(string? pinnedCommit) =>
        pinnedCommit is not null && Sha1().IsMatch(pinnedCommit)
            ? new(SkillHealthCodes.Pinned, SkillHealthStatus.Healthy, "Fijada a un commit concreto.")
            : new(SkillHealthCodes.Pinned, SkillHealthStatus.Warning, "Sin commit fijado: el contenido upstream puede cambiar sin revisión.");
}
