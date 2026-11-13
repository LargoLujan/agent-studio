using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Hosting.Cli;

/// <summary>
/// The environment of every <c>gh</c> that talks to GitHub (docs/21 D-11): an allow list, not a block list. <c>ProcessSpec.Environment</c> is a change over what the child
/// inherits, so every variable of the process that is not allowed goes in as null, which removes it; the variables that redirect <c>gh</c> or hand it a token
/// (<c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, <c>GH_HOST</c>, <c>GH_REPO</c>, <c>GH_DEBUG</c>, every <c>GIT_*</c>…) are removed whatever the process has, and also named explicitly so that
/// they are removed even when the process does not hold them.
/// </summary>
internal static class GhEnvironment
{
    /// <summary>The variables of the person that cross the list because <c>gh</c> would not find its session or would not reach the network without them (D-11).</summary>
    internal static readonly string[] ConfigurationFolders = ["XDG_CONFIG_HOME", "GH_CONFIG_DIR"];

    internal static readonly string[] Network = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "SSL_CERT_FILE"];

    /// <summary>What is never passed, named so that a test and a reader see the list (it is also removed by not being on the allow list).</summary>
    internal static readonly string[] Removed =
    [
        "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_DEBUG", "DEBUG", "GH_PAGER", "PAGER", "GH_BROWSER", "BROWSER",
        "GH_FORCE_TTY", "GH_PATH", "GH_EDITOR",
    ];

    /// <summary>The variables <c>gh</c> is given so that it asks nothing, reports nothing and prints nothing but the answer.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Fixed { get; } =
    [
        .. DoctorEnvironment.GhOffline,
        new("NO_COLOR", "1"),
        new("GH_SPINNER_DISABLED", "1"),
    ];

    /// <param name="ambient">The variables the process has.</param>
    /// <param name="roots">The projects that were open: a configuration folder inside one of them is not passed (it could hold what the repository planted).</param>
    internal static IReadOnlyDictionary<string, string?> For(GhFolders folders, IReadOnlyDictionary<string, string> ambient, IUntrustedRoots? roots)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(ambient);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ambient.Keys)
        {
            if (!DoctorEnvironment.Allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                environment[name] = null;
            }
        }

        foreach (var name in Removed)
        {
            environment[name] = null;
        }

        var untrusted = UntrustedPaths.Resolve(roots?.Roots);
        foreach (var name in ConfigurationFolders)
        {
            if (ambient.TryGetValue(name, out var value) && IsAbsoluteOutside(value, untrusted))
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = null;
            }
        }

        foreach (var name in Network)
        {
            if (ambient.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = null;
            }
        }

        environment["TEMP"] = folders.TempFolder;
        environment["TMP"] = folders.TempFolder;
        foreach (var (name, value) in Fixed)
        {
            environment[name] = value;
        }

        return environment;
    }

    private static bool IsAbsoluteOutside(string? value, IReadOnlyList<string> untrusted)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(value) && (untrusted.Count == 0 || !UntrustedPaths.IsInside(value, untrusted));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
