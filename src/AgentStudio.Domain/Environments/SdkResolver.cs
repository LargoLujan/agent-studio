namespace AgentStudio.Domain.Environments;

/// <summary>The nine policies of <c>sdk.rollForward</c> of a <c>global.json</c> (docs/20 D-07; Microsoft Learn, «global.json overview»).</summary>
public enum GlobalJsonRollForward
{
    /// <summary>The version asked for; if it is not installed, the latest patch of its feature band. This is the default when a version is given.</summary>
    Patch,

    /// <summary>The latest patch of its feature band; else the next higher feature band of the same major and minor.</summary>
    Feature,

    /// <summary>As <see cref="Feature"/>; else the next higher minor of the same major.</summary>
    Minor,

    /// <summary>As <see cref="Minor"/>; else the next higher major.</summary>
    Major,

    /// <summary>The latest installed patch of its feature band that is not lower than the version.</summary>
    LatestPatch,

    /// <summary>The highest installed SDK of the same major and minor that is not lower than the version.</summary>
    LatestFeature,

    /// <summary>The highest installed SDK of the same major that is not lower than the version.</summary>
    LatestMinor,

    /// <summary>The highest installed SDK that is not lower than the version. This is the default when there is no version.</summary>
    LatestMajor,

    /// <summary>Only the exact version.</summary>
    Disable,
}

/// <summary>How the resolution of an SDK ended.</summary>
public enum SdkResolutionKind
{
    /// <summary>An installed SDK would be used: <see cref="SdkResolution.Selected"/>.</summary>
    Selected,

    /// <summary>The rule is understood and none of the installed SDKs satisfies it.</summary>
    NoMatch,

    /// <summary>The rule cannot be evaluated (an invalid version or policy, or <c>sdk.paths</c> that can name SDKs that are not installed): never «satisfied».</summary>
    NotComparable,
}

/// <param name="Selected">The SDK the host would use, when <paramref name="Kind"/> is <see cref="SdkResolutionKind.Selected"/>.</param>
/// <param name="Reason">A stable code when the kind is <see cref="SdkResolutionKind.NotComparable"/>: <c>invalid-version</c>, <c>invalid-roll-forward</c>, <c>version-required</c> or <c>paths</c>.</param>
public sealed record SdkResolution(SdkResolutionKind Kind, ToolVersion? Selected, string? Reason = null);

/// <summary>
/// The rule of <c>global.json</c> to choose a .NET SDK among the installed ones, evaluated by Agent Studio and not by asking <c>dotnet</c> inside the project
/// (docs/20 D-07). The feature band of <c>x.y.znn</c> is <c>z</c>, the hundreds of the patch number. A pre-release SDK is a candidate unless
/// <c>allowPrerelease</c> is false (by default it is true outside Visual Studio). It is total: it never throws.
/// </summary>
public static class SdkResolver
{
    /// <summary>The marker of <c>sdk.paths</c> that stands for the folder of the running <c>dotnet</c>.</summary>
    public const string HostPath = "$host$";

    /// <summary>The policy written in <paramref name="text"/>, case-insensitive (the host reads it so); false when it is none of the nine.</summary>
    public static bool TryParseRollForward(string? text, out GlobalJsonRollForward policy)
    {
        policy = GlobalJsonRollForward.Patch;
        if (text is null)
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<GlobalJsonRollForward>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                policy = candidate;
                return true;
            }
        }

        return false;
    }

    /// <param name="version"><c>sdk.version</c>: three numbers (<c>10.0.303</c>), with an optional pre-release; null when there is none.</param>
    /// <param name="rollForward"><c>sdk.rollForward</c>, or null.</param>
    /// <param name="allowPrerelease"><c>sdk.allowPrerelease</c>, or null for the default (true).</param>
    /// <param name="installed">The versions of the SDKs installed (<c>dotnet --list-sdks</c> from a neutral folder).</param>
    /// <param name="paths">
    /// <c>sdk.paths</c>. Without any, the installed SDKs are all the host can use. With paths, the host searches them in order and takes the first match: it is
    /// understood only when the first (and only) one is <see cref="HostPath"/>, because a folder of the project is never read.
    /// </param>
    public static SdkResolution Resolve(
        string? version, string? rollForward, bool? allowPrerelease, IReadOnlyList<ToolVersion> installed, IReadOnlyList<string>? paths = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        GlobalJsonRollForward? policy = null;
        if (rollForward is not null)
        {
            if (!TryParseRollForward(rollForward, out var parsed))
            {
                return NotComparable("invalid-roll-forward");
            }

            policy = parsed;
        }

        ToolVersion? requested = null;
        if (version is not null)
        {
            if (!ToolVersion.TryParse(version, out requested) || requested.Numbers.Count != 3)
            {
                return NotComparable("invalid-version");
            }
        }
        else if (policy is not null and not GlobalJsonRollForward.LatestMajor)
        {
            return NotComparable("version-required");
        }

        if (paths is { Count: > 0 } && !(paths.Count == 1 && paths[0] == HostPath))
        {
            return NotComparable("paths");
        }

        var candidates = installed.Where(v => v is not null && (allowPrerelease != false || !v.IsPreRelease)).ToList();
        var chosen = requested is null
            ? Highest(candidates)
            : Choose(requested, policy ?? GlobalJsonRollForward.Patch, candidates);
        return chosen is null ? new SdkResolution(SdkResolutionKind.NoMatch, null) : new SdkResolution(SdkResolutionKind.Selected, chosen);
    }

    private static SdkResolution NotComparable(string reason) => new(SdkResolutionKind.NotComparable, null, reason);

    private static ToolVersion? Choose(ToolVersion requested, GlobalJsonRollForward policy, List<ToolVersion> candidates)
    {
        var atLeast = candidates.Where(v => v >= requested).ToList();
        ToolVersion? SameBand(IEnumerable<ToolVersion> from) => Highest(from.Where(v => v.Major == requested.Major && v.Minor == requested.Minor && Band(v) == Band(requested)));

        switch (policy)
        {
            case GlobalJsonRollForward.Disable:
                return candidates.FirstOrDefault(v => v.Equals(requested));
            case GlobalJsonRollForward.Patch:
                return candidates.FirstOrDefault(v => v.Equals(requested)) ?? SameBand(atLeast);
            case GlobalJsonRollForward.LatestPatch:
                return SameBand(atLeast);
            case GlobalJsonRollForward.LatestFeature:
                return Highest(atLeast.Where(v => v.Major == requested.Major && v.Minor == requested.Minor));
            case GlobalJsonRollForward.LatestMinor:
                return Highest(atLeast.Where(v => v.Major == requested.Major));
            case GlobalJsonRollForward.LatestMajor:
                return Highest(atLeast);
            default:
                // Feature, Minor and Major: the same feature band first, then the next one up, and so on outwards. «Next» is the lowest of those above.
                var found = SameBand(atLeast);
                if (found is not null)
                {
                    return found;
                }

                var sameMinor = atLeast.Where(v => v.Major == requested.Major && v.Minor == requested.Minor).ToList();
                found = LowestGroup(sameMinor);
                if (found is not null || policy == GlobalJsonRollForward.Feature)
                {
                    return found;
                }

                found = LowestGroup(atLeast.Where(v => v.Major == requested.Major).ToList());
                if (found is not null || policy == GlobalJsonRollForward.Minor)
                {
                    return found;
                }

                return LowestGroup(atLeast);
        }
    }

    /// <summary>The feature band: the hundreds of the patch number (<c>10.0.303</c> is band 3).</summary>
    private static int Band(ToolVersion version) => version.Patch / 100;

    private static ToolVersion? Highest(IEnumerable<ToolVersion> versions)
    {
        ToolVersion? best = null;
        foreach (var version in versions)
        {
            if (best is null || version > best)
            {
                best = version;
            }
        }

        return best;
    }

    /// <summary>The highest version of the lowest (major, minor, band) group: the «next higher» one with its latest patch.</summary>
    private static ToolVersion? LowestGroup(List<ToolVersion> versions)
    {
        if (versions.Count == 0)
        {
            return null;
        }

        var group = versions.Select(v => (v.Major, v.Minor, Band: Band(v))).Min();
        return Highest(versions.Where(v => (v.Major, v.Minor, Band(v)) == group));
    }
}
