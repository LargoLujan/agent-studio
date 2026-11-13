using AgentStudio.Application.Hosting;

namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>What the analysis of the configuration found: the keys that refuse the operation and whether a filter of git-lfs is configured (a first sign; <see cref="RemoteLfsDetector"/> asks for the second).</summary>
internal sealed record RemoteConfigAnalysis(IReadOnlyList<RemoteConfigRefusal> Refusals, bool LfsFilterConfigured);

/// <summary>
/// The two lists of docs/21 D-05, applied to the output of <c>git config -z --show-scope --show-origin --get-regexp</c> (read with the public reader of <see cref="GitCallbacks"/>). Pure: nothing is started here.
/// <list type="bullet">
/// <item>What a <c>-c</c> overrides (<c>core.hooksPath</c>, <c>core.fsmonitor</c>, <c>core.sshCommand</c>, <c>core.gitProxy</c>, <c>core.askPass</c>, <c>protocol.*</c>, <c>http.followRedirects</c>, the <c>push.*</c> of one value,
/// <c>submodule.recurse</c>) is never refused: the command line wins, as in the internal layer. A repository with husky or with <c>core.fsmonitor</c> is normal.</item>
/// <item>What a <c>-c</c> cannot override is refused when the repository defines it (scope <c>local</c> or <c>worktree</c>, or a file that lives inside the repository): <c>credential.*</c>, <c>url.*</c>, any <c>http.*</c> but
/// <c>followRedirects</c>, the keys of <c>remote.*</c> that change the destination or the program, <c>remote.pushDefault</c>, <c>branch.*.pushRemote</c>, <c>push.pushOption</c> and <c>core.alternateRefsCommand</c>. The global and system
/// configuration of the person counts: it is where the credential helper lives.</item>
/// </list>
/// The refusal names the key (a subsection is written <c>*</c>: it holds an address that may hold a token), its scope and its file, never its value (docs/21 D-05).
/// </summary>
internal static class RemoteConfigInspection
{
    /// <summary>
    /// The pattern of <c>--get-regexp</c>: the sections that have a key to judge, plus <c>filter</c> for git-lfs. Git prints the section and the variable in lowercase and the subsection as written.
    /// </summary>
    public const string Pattern = @"^(credential|url|http|remote|branch|push|core|include|includeif|filter|extensions)\.";

    private const int MaxFileCharacters = 260;

    public static RemoteConfigAnalysis Analyze(IEnumerable<GitCallbacks.ConfigEntry> entries, string address, Func<string, bool> originInsideRepository)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(originInsideRepository);

        var refusals = new List<RemoteConfigRefusal>();
        var lfs = false;
        foreach (var entry in entries)
        {
            // The pairs this executor puts on the command line itself are not the configuration of anybody.
            if (string.Equals(entry.Scope, "command", StringComparison.Ordinal) || !TryParseKey(entry.Key, out var section, out var subsection, out var variable))
            {
                continue;
            }

            if (section == "filter" && string.Equals(subsection, "lfs", StringComparison.OrdinalIgnoreCase) && (variable is "clean" or "smudge" or "process") && !string.IsNullOrWhiteSpace(entry.Value))
            {
                lfs = true;
            }

            var definedByRepository = !IsTrustedScope(entry.Scope) || originInsideRepository(entry.Origin);
            if (definedByRepository && IsRefusedKey(section, subsection, variable))
            {
                Add(refusals, entry, section, subsection, variable);
            }
            else if (section == "remote" && subsection is not null && string.Equals(subsection, address, StringComparison.Ordinal) && variable is "url" or "pushurl" or "push" or "receivepack" or "uploadpack" or "proxy" or "proxyauthmethod" or "vcs")
            {
                // A remote called exactly like the address, in the person's own files: `ls-remote --get-url` does not see its pushurl (it applies insteadOf and url only), and its keys would divert or launch.
                Add(refusals, entry, section, subsection, variable);
            }
            else if (section == "url" && variable == "pushinsteadof" && address.StartsWith(entry.Value, StringComparison.Ordinal))
            {
                // git ls-remote --get-url applies insteadOf but not pushInsteadOf (measured with git 2.45.2): a rewrite of the push address by the person's own configuration is found here, by prefix.
                Add(refusals, entry, section, subsection, variable);
            }
        }

        return new RemoteConfigAnalysis(refusals, lfs);
    }

    /// <summary>
    /// The refusal of a rewrite of the address that git itself reported (<c>ls-remote --get-url</c> gave another address): it names the entry of <c>url.*.insteadOf</c> whose value is a prefix of the address when there is one,
    /// and a generic key when none is (nothing is guessed).
    /// </summary>
    public static RemoteConfigRefusal NameRewrite(IEnumerable<GitCallbacks.ConfigEntry> entries, string address)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (TryParseKey(entry.Key, out var section, out var subsection, out var variable)
                && section == "url" && variable == "insteadof" && !string.Equals(entry.Scope, "command", StringComparison.Ordinal)
                && address.StartsWith(entry.Value, StringComparison.Ordinal))
            {
                return new RemoteConfigRefusal(Display(section, subsection, variable), entry.Scope, FileOf(entry.Origin));
            }
        }

        return new RemoteConfigRefusal("url.*.insteadof", "unknown", string.Empty);
    }

    /// <summary>Whether a refusal is for a key that makes the repository a partial clone: it carries its own code, <see cref="RemoteGitCodes.PartialCloneUnsupported"/>.</summary>
    public static bool IsPartialClone(RemoteConfigRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Key is "extensions.partialclone" or "remote.*.promisor" or "remote.*.partialclonefilter";
    }

    // Anything that is not one of the three scopes of the person's own files is treated as the repository's (fail closed).
    private static bool IsTrustedScope(string scope) => scope is "system" or "global";

    private static bool IsRefusedKey(string section, string? subsection, string variable) => section switch
    {
        "credential" => true,
        "url" => true,
        "http" => !(subsection is null && variable == "followredirects"),
        "remote" => subsection is null
            ? variable == "pushdefault"
            : IsAddress(subsection) || variable is "pushurl" or "push" or "receivepack" or "uploadpack" or "proxy" or "proxyauthmethod" or "vcs" or "promisor" or "partialclonefilter",
        "branch" => subsection is not null && variable == "pushremote",
        "push" => subsection is null && variable == "pushoption",
        "core" => subsection is null && variable == "alternaterefscommand",

        // A partial clone: git fetches the objects it lacks on demand, from the remote the repository calls promisor (it depends on GIT_NO_LAZY_FETCH, from git 2.44, to be stopped: it is refused instead).
        "extensions" => subsection is null && variable == "partialclone",
        _ => false,
    };

    // git looks the remote up by the name it is given, and the executor gives it the address: a remote of the repository called like the address (it holds a «:», a «/» or a «\», which no name of a remote does) would apply
    // every one of its keys to the push. Measured with git 2.45.2: its pushurl and its url took the push to another repository, its receivepack ran a program, and its mirror stopped the push. So any key of it is refused.
    private static bool IsAddress(string subsection) => subsection.AsSpan().IndexOfAny(':', '/', '\\') >= 0;

    private static void Add(List<RemoteConfigRefusal> refusals, GitCallbacks.ConfigEntry entry, string section, string? subsection, string variable)
    {
        var refusal = new RemoteConfigRefusal(Display(section, subsection, variable), entry.Scope, FileOf(entry.Origin));
        if (!refusals.Contains(refusal))
        {
            refusals.Add(refusal);
        }
    }

    private static string Display(string section, string? subsection, string variable) =>
        section + (subsection is null ? string.Empty : ".*") + "." + variable;

    // The path of the file of an origin, without control characters and capped; «command line:» and the like as they are.
    private static string FileOf(string origin)
    {
        var path = origin.StartsWith("file:", StringComparison.Ordinal) ? origin["file:".Length..] : origin;
        var clean = new string([.. path.Where(c => !char.IsControl(c))]);
        return clean.Length <= MaxFileCharacters ? clean : clean[..MaxFileCharacters];
    }

    private static bool TryParseKey(string key, out string section, out string? subsection, out string variable)
    {
        section = string.Empty;
        subsection = null;
        variable = string.Empty;
        var first = key.IndexOf('.', StringComparison.Ordinal);
        var last = key.LastIndexOf('.');
        if (first <= 0 || last == key.Length - 1)
        {
            return false;
        }

        section = key[..first].ToLowerInvariant();
        variable = key[(last + 1)..].ToLowerInvariant();
        subsection = last > first ? key[(first + 1)..last] : null;
        return true;
    }
}
