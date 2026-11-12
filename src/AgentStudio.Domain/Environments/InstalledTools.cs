namespace AgentStudio.Domain.Environments;

/// <summary>A .NET SDK installed (a line of <c>dotnet --list-sdks</c>): its version and the folder it lives in.</summary>
public sealed record InstalledSdk
{
    public InstalledSdk(ToolVersion version, string? path)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
        Path = InstalledText.OptionalPath(path, nameof(path));
    }

    public ToolVersion Version { get; }

    public string? Path { get; }
}

/// <summary>A .NET runtime installed (a line of <c>dotnet --list-runtimes</c>): its name (<c>Microsoft.NETCore.App</c>) and its version.</summary>
public sealed record InstalledRuntime
{
    public InstalledRuntime(string name, ToolVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl) || name != name.Trim())
        {
            throw new ArgumentException("The name of a runtime is one line of up to 100 characters.", nameof(name));
        }

        Name = name;
        Version = version;
    }

    public string Name { get; }

    public ToolVersion Version { get; }
}

/// <summary>A tool installed outside the project (Node, npm, Python, Git): its version and where it was found.</summary>
public sealed record InstalledTool
{
    public InstalledTool(ToolVersion version, string? path)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
        Path = InstalledText.OptionalPath(path, nameof(path));
    }

    public ToolVersion Version { get; }

    public string? Path { get; }
}

/// <summary>The three states of Docker (docs/20 D-09), decided by structure and exit code, not by text.</summary>
public enum DockerState
{
    NotInstalled,
    EngineStopped,
    Running,
}

/// <summary>
/// Docker as <c>docker version --format json</c> tells it. Not installed has no versions; stopped has the version of the client only, and
/// running has both: a state that says more or less than its versions is refused.
/// </summary>
public sealed record DockerStatus
{
    public DockerStatus(DockerState state, ToolVersion? clientVersion, ToolVersion? serverVersion)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of Docker.");
        }

        var consistent = state switch
        {
            DockerState.NotInstalled => clientVersion is null && serverVersion is null,
            DockerState.EngineStopped => clientVersion is not null && serverVersion is null,
            _ => clientVersion is not null && serverVersion is not null,
        };
        if (!consistent)
        {
            throw new ArgumentException($"Docker {state} does not go with those versions.");
        }

        State = state;
        ClientVersion = clientVersion;
        ServerVersion = serverVersion;
    }

    public static DockerStatus NotInstalled { get; } = new(DockerState.NotInstalled, null, null);

    public DockerState State { get; }

    public ToolVersion? ClientVersion { get; }

    public ToolVersion? ServerVersion { get; }
}

/// <summary>The three states of WSL (docs/20 D-09).</summary>
public enum WslState
{
    NotInstalled,
    NoDistribution,
    Ready,
}

/// <summary>WSL as <c>wsl --status</c> tells it. Ready names the default distribution; the other two have none.</summary>
public sealed record WslStatus
{
    public WslStatus(WslState state, string? defaultDistribution)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of WSL.");
        }

        if ((state == WslState.Ready) != (defaultDistribution is not null)
            || (defaultDistribution is not null && (defaultDistribution.Length is < 1 or > 100 || defaultDistribution.Any(char.IsControl))))
        {
            throw new ArgumentException("Only WSL ready has a default distribution, of one line of up to 100 characters.", nameof(defaultDistribution));
        }

        State = state;
        DefaultDistribution = defaultDistribution;
    }

    public static WslStatus NotInstalled { get; } = new(WslState.NotInstalled, null);

    public WslState State { get; }

    public string? DefaultDistribution { get; }
}

/// <summary>
/// What is installed on this computer, outside every project (docs/20 D-03 and D-07): the inventory that Auto Doctor reads with its closed door
/// for tools and the comparer sets against what a project declares. A tool that is not there is null. A Python that is only the alias of
/// the Microsoft Store does not count as installed: <see cref="PythonIsStoreAlias"/> says so and <see cref="Python"/> is null.
/// </summary>
public sealed record InstalledTools
{
    public InstalledTools(
        IReadOnlyList<InstalledSdk> dotNetSdks,
        IReadOnlyList<InstalledRuntime> dotNetRuntimes,
        InstalledTool? node,
        InstalledTool? npm,
        InstalledTool? python,
        bool pythonIsStoreAlias,
        InstalledTool? git,
        DockerStatus docker,
        WslStatus wsl,
        ToolVersion? webView2)
    {
        ArgumentNullException.ThrowIfNull(dotNetSdks);
        ArgumentNullException.ThrowIfNull(dotNetRuntimes);
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(wsl);
        if (pythonIsStoreAlias && python is not null)
        {
            throw new ArgumentException("The alias of the Microsoft Store is not a Python: it does not count as installed.", nameof(pythonIsStoreAlias));
        }

        if (dotNetSdks.Count > 200 || dotNetRuntimes.Count > 500 || dotNetSdks.Any(s => s is null) || dotNetRuntimes.Any(r => r is null))
        {
            throw new ArgumentException("An inventory holds up to 200 SDKs and 500 runtimes, none of them null.");
        }

        DotNetSdks = [.. dotNetSdks.OrderBy(s => s.Version)];
        DotNetRuntimes = [.. dotNetRuntimes.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Version)];
        Node = node;
        Npm = npm;
        Python = python;
        PythonIsStoreAlias = pythonIsStoreAlias;
        Git = git;
        Docker = docker;
        Wsl = wsl;
        WebView2 = webView2;
    }

    /// <summary>A computer where nothing is installed: the starting point of the tests of the comparer.</summary>
    public static InstalledTools None { get; } = new([], [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null);

    /// <summary>The SDKs, from the lowest version to the highest.</summary>
    public IReadOnlyList<InstalledSdk> DotNetSdks { get; }

    public IReadOnlyList<InstalledRuntime> DotNetRuntimes { get; }

    public InstalledTool? Node { get; }

    public InstalledTool? Npm { get; }

    public InstalledTool? Python { get; }

    public bool PythonIsStoreAlias { get; }

    public InstalledTool? Git { get; }

    public DockerStatus Docker { get; }

    public WslStatus Wsl { get; }

    public ToolVersion? WebView2 { get; }

    public bool Equals(InstalledTools? other) =>
        other is not null && DotNetSdks.SequenceEqual(other.DotNetSdks) && DotNetRuntimes.SequenceEqual(other.DotNetRuntimes)
        && Node == other.Node && Npm == other.Npm && Python == other.Python && PythonIsStoreAlias == other.PythonIsStoreAlias
        && Git == other.Git && Docker == other.Docker && Wsl == other.Wsl && Equals(WebView2, other.WebView2);

    public override int GetHashCode() => HashCode.Combine(DotNetSdks.Count, DotNetRuntimes.Count, Node, Python, Docker.State, Wsl.State);
}

internal static class InstalledText
{
    /// <summary>The path where a tool was found: what the system told, one line and at most 520 characters; it is shown, never opened.</summary>
    internal static string? OptionalPath(string? path, string parameterName)
    {
        if (path is null)
        {
            return null;
        }

        if (path.Length is < 1 or > 520 || path.Any(char.IsControl))
        {
            throw new ArgumentException("A path is one line of up to 520 characters.", parameterName);
        }

        return path;
    }
}
