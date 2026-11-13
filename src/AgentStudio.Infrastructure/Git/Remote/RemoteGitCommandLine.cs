using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The exact command lines of the remote executor of git (docs/21 D-06 and D-07): a closed list. The arguments are constants; the only data in them is the address (written by the transport from a validated
/// <see cref="RemoteRepository"/>), the refspec (written by <see cref="TryRefspec"/>, the only function that writes one) and the name of a branch, which is validated and goes behind <c>refs/heads/</c>. There is no
/// <c>--force</c>, no <c>+</c> refspec, no deletion, no <c>--mirror</c>, <c>--all</c> or <c>--tags</c>, and no <c>fetch</c>. The five commands are <c>config</c>, <c>grep</c> (local), <c>ls-remote</c> (three times) and <c>push</c>.
/// </summary>
internal static class RemoteGitCommandLine
{
    /// <summary>How long a push may take (docs/21 D-06).</summary>
    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long <c>ls-remote</c>, the query of the configuration and the question of the address may take.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The characters of the output of a command that are read; past it the tree is killed and the operation fails closed.</summary>
    public const int MaxOutputCharacters = 64_000;

    /// <summary>The length of a commit: 40 lowercase hexadecimal characters.</summary>
    public const int CommitLength = 40;

    // https first, as in D-06. The scheme of the transport is the one that is "always"; every other name is "never".
    private static readonly string[] ProtocolNames = ["https", "file", "git", "ssh", "http", "ftp", "ftps", "ext"];

    /// <summary>
    /// The <c>-c</c> pairs every command carries (D-06). They override what a single value can be overridden with; what a <c>-c</c> cannot override is refused by the inspection (D-05). <c>credential.helper</c> is NOT emptied:
    /// it is what the person's helper needs. No <c>http.*</c> is set but <c>http.followRedirects</c>: a <c>-c</c> beats the global configuration too, and would take the person's proxy away.
    /// </summary>
    public static IReadOnlyList<string> Configuration(RemoteTransport transport, string hooksFile)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksFile);

        var arguments = new List<string>(80);
        void Set(string setting)
        {
            arguments.Add("-c");
            arguments.Add(setting);
        }

        Set("core.hooksPath=" + hooksFile);
        Set("core.fsmonitor=false");
        Set("gc.auto=0");
        Set("maintenance.auto=false");
        Set("core.sshCommand=");
        Set("core.gitProxy=");
        Set("core.askPass=");
        Set("protocol.allow=never");
        foreach (var name in ProtocolNames)
        {
            Set($"protocol.{name}.allow=" + (string.Equals(name, transport.Scheme, StringComparison.Ordinal) ? "always" : "never"));
        }

        if (!ProtocolNames.Contains(transport.Scheme, StringComparer.Ordinal))
        {
            Set($"protocol.{transport.Scheme}.allow=always");
        }

        Set("http.followRedirects=false");
        Set("submodule.recurse=false");
        Set("push.default=nothing");
        Set("push.followTags=false");
        Set("push.recurseSubmodules=no");
        Set("push.gpgSign=false");
        Set("push.autoSetupRemote=false");
        return arguments;
    }

    /// <summary>The query of the configuration by scope and by origin (docs/21 D-05): it opens no transport. The pattern is the keys that are judged.</summary>
    public static IReadOnlyList<string> ConfigQuery() =>
        ["config", "-z", "--show-scope", "--show-origin", "--get-regexp", "--end-of-options", RemoteConfigInspection.Pattern];

    /// <summary>The question of the address git would use after rewriting it with <c>url.&lt;base&gt;.insteadOf</c>; it opens no transport.</summary>
    public static IReadOnlyList<string> GetUrl(string address) => ["ls-remote", "--get-url", "--", address];

    /// <summary>
    /// Whether the tree of a commit has a <c>.gitattributes</c> (at any depth) with a line that says <c>filter=lfs</c> and is not a comment: a local command, read-only and with no network. <c>git grep</c> of a tree reads the objects and runs
    /// no filter or conversion; it exits with 0 when there is a match, 1 when there is none and more for a failure. <paramref name="treeish"/> is a commit (40 hexadecimal characters) or <c>HEAD</c>, never other text.
    /// </summary>
    public static IReadOnlyList<string> LfsAttributes(string treeish)
    {
        if (treeish != "HEAD" && !IsCommit(treeish))
        {
            throw new ArgumentException("A commit of 40 lowercase hexadecimal characters, or HEAD.", nameof(treeish));
        }

        return ["grep", "-l", "-i", "-E", "--no-color", "-e", "^[^#]*filter=lfs", treeish, "--", ":(glob)**/.gitattributes"];
    }

    /// <summary><c>ls-remote --refs</c> of one branch of the repository.</summary>
    public static IReadOnlyList<string> LsRemote(string address, RemoteBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return ["ls-remote", "--refs", "--", address, "refs/heads/" + branch.Name];
    }

    /// <summary>The push of one refspec. The address and the refspec come last, behind <c>--</c>.</summary>
    public static IReadOnlyList<string> Push(string address, string refspec) =>
        ["push", "--porcelain", "--no-verify", "--no-follow-tags", "--recurse-submodules=no", "--no-signed", "--", address, refspec];

    /// <summary>
    /// The one function that writes a refspec: <c>&lt;commit&gt;:refs/heads/agentstudio/run-&lt;id&gt;</c>. The commit is 40 lowercase hexadecimal characters (anything else, a <c>+</c>, a <c>:</c>, an option, a name,
    /// is null) and the run has an identifier. It never starts with <c>+</c> or <c>:</c> and it never deletes.
    /// </summary>
    public static string? TryRefspec(string? commit, RunId run)
    {
        if (!IsCommit(commit) || run.Value is null)
        {
            return null;
        }

        var branch = RemoteBranch.ForRun(run);
        return branch.IsRunBranch ? $"{commit}:refs/heads/{branch.Name}" : null;
    }

    /// <summary>Whether <paramref name="value"/> is 40 lowercase hexadecimal characters.</summary>
    public static bool IsCommit(string? value) =>
        value is { Length: CommitLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
