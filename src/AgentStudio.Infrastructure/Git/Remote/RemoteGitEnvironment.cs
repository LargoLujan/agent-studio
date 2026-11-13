namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The environment of every command of the remote executor of git (docs/21 D-06, ADR-0064 point 7): an allow list, not a block list. A block list goes stale with every variable of every tool; the allow list fails towards
/// safe. <c>ProcessSpec.Environment</c> is a change over what the child inherits, so every variable the process has that is not on the list goes in as null, which removes it: no <c>GIT_*</c>, no <c>GH_*</c>,
/// no <c>GCM_*</c> and no proxy variable gets through, whatever it is called. What the executor sets itself is the rest.
/// </summary>
internal static class RemoteGitEnvironment
{
    /// <summary>
    /// The variables git, and the credential manager it starts, need to find the person's configuration and store, by name and ignoring case. <c>HOME</c>, <c>HOMEDRIVE</c> and <c>HOMEPATH</c> are where git reads the global
    /// configuration. <c>PATH</c> is here because the runner of processes cleans it (relative entries and the projects out) and sets it.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "USERNAME", "USERDOMAIN", "PATH",

        // The folder of the commands of Windows is set by the runner of processes so that CreateProcess skips the current folder: it must survive.
        "NoDefaultCurrentDirectoryInExePath",
    ];

    /// <summary>
    /// The names removed whether or not the snapshot of the environment has them (the snapshot may be an injected one, and the child inherits the real one): the ones that redirect git, choose how it
    /// authenticates or dump what it sends. The allow list already removes every name it does not list; these are a second line, and a list a test reads.
    /// </summary>
    public static IReadOnlyList<string> AlwaysRemoved { get; } =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES",
        "GIT_PREFIX", "GIT_CONFIG", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_EXEC_PATH", "GIT_TEMPLATE_DIR",
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT", "GIT_ASKPASS", "GIT_PROXY_COMMAND", "GIT_EXTERNAL_DIFF", "GIT_EDITOR", "GIT_SSL_NO_VERIFY", "GIT_SSL_CAINFO", "GIT_HTTP_PROXY_AUTHMETHOD",
        "GIT_TRACE", "GIT_TRACE2", "GIT_TRACE_CURL", "GIT_TRACE_PACKET", "GIT_TRACE_SETUP", "GIT_CURL_VERBOSE", "GIT_REDACT_COOKIES",
        "GCM_TRACE", "GCM_GUI_PROMPT", "GCM_CREDENTIAL_STORE", "GCM_PROVIDER", "GCM_AUTHORITY",
        "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_CONFIG_DIR", "GH_DEBUG", "GH_PATH",
        "SSH_ASKPASS", "SSH_AUTH_SOCK", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy", "CURL_CA_BUNDLE", "SSL_CERT_FILE",
    ];

    /// <summary>
    /// The <c>ProcessSpec.Environment</c> of a command: null for everything the process has that is not allowed and for <see cref="AlwaysRemoved"/>; <c>TEMP</c> and <c>TMP</c> in the folder of the executor;
    /// and what the executor sets: <c>GIT_TERMINAL_PROMPT=0</c>, <c>GIT_OPTIONAL_LOCKS=0</c>, <c>GIT_PAGER=cat</c>, <c>GIT_NO_LAZY_FETCH=1</c>, <c>GIT_ALLOW_PROTOCOL</c> (the scheme of the transport: the same constant
    /// that opens <c>protocol.&lt;scheme&gt;.allow</c>), <c>GCM_INTERACTIVE=never</c> (git credential manager fails instead of opening a window; another helper has no window to avoid) and <c>LC_ALL=C</c>, so
    /// the error output has the one language the classifier of the executor reads.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Build(RemoteTransport transport, string tempFolder, IReadOnlyDictionary<string, string> ambient)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFolder);
        ArgumentNullException.ThrowIfNull(ambient);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ambient.Keys)
        {
            if (!Allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                environment[name] = null;
            }
        }

        foreach (var name in AlwaysRemoved)
        {
            environment[name] = null;
        }

        environment["TEMP"] = tempFolder;
        environment["TMP"] = tempFolder;
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_OPTIONAL_LOCKS"] = "0";
        environment["GIT_PAGER"] = "cat";
        environment["GIT_NO_LAZY_FETCH"] = "1";
        environment["GIT_ALLOW_PROTOCOL"] = transport.Scheme;
        environment["GCM_INTERACTIVE"] = "never";
        environment["LC_ALL"] = "C";
        return environment;
    }
}
