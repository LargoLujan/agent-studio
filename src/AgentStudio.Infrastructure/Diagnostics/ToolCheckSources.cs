using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// The «Herramientas» section (docs/20 D-03): the .NET SDKs and runtimes, Node, npm, Python, Git and the WebView2 Runtime. Each check is decided
/// by what the door found (<see cref="ToolSnapshot"/>): an answer that could not be understood is «sin comprobar», never «correcto»; a tool found
/// only inside the open project is an aviso that says so (it is not used).
/// </summary>
public sealed class ToolsCheckSource(ToolSnapshotProvider tools) : IDoctorCheckSource
{
    public DoctorSection Section => DoctorSection.Tools;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        var snapshot = await tools.SnapshotAsync(project?.RootPath, ct).ConfigureAwait(false);
        return
        [
            DotNetSdk(snapshot.DotNetSdks),
            DotNetRuntime(snapshot.DotNetRuntimes),
            Version("doctor.node", snapshot.Node, DoctorStatus.Warning, DoctorFixCatalog.InstallNode),
            Version("doctor.npm", snapshot.Npm, DoctorStatus.Warning, DoctorFixCatalog.InstallNode),
            Python(snapshot.Python),
            Version("doctor.git", snapshot.Git, DoctorStatus.Error, DoctorFixCatalog.InstallGit),
            Version("doctor.webview2", snapshot.WebView2, DoctorStatus.Error, DoctorFixCatalog.InstallWebView2),
        ];
    }

    private static DoctorCheck DotNetSdk(ToolReading<IReadOnlyList<InstalledSdk>> reading)
    {
        if (reading.IsFound)
        {
            var sdks = reading.Value!;
            if (sdks.Count == 0)
            {
                return new DoctorCheck("doctor.dotnet.sdk", DoctorSection.Tools, DoctorStatus.Warning, [], DoctorFixCatalog.InstallDotNet);
            }

            var highest = sdks[^1];
            var facts = new List<DoctorFact> { DoctorFact.Of(DoctorFactNames.Version, highest.Version.Text), DoctorFact.Of("count", sdks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            if (highest.Path is not null)
            {
                facts.Add(DoctorFact.Of(DoctorFactNames.Path, highest.Path));
            }

            return new DoctorCheck("doctor.dotnet.sdk", DoctorSection.Tools, DoctorStatus.Ok, facts);
        }

        return NotFound("doctor.dotnet.sdk", reading, DoctorStatus.Warning, DoctorFixCatalog.InstallDotNet);
    }

    private static DoctorCheck DotNetRuntime(ToolReading<IReadOnlyList<InstalledRuntime>> reading)
    {
        if (reading.IsFound)
        {
            var runtimes = reading.Value!;
            if (runtimes.Count == 0)
            {
                return new DoctorCheck("doctor.dotnet.runtime", DoctorSection.Tools, DoctorStatus.Warning, [], DoctorFixCatalog.InstallDotNet);
            }

            var core = runtimes.Where(r => r.Name == "Microsoft.NETCore.App").ToList();
            var facts = new List<DoctorFact> { DoctorFact.Of("count", runtimes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            if (core.Count > 0)
            {
                facts.Insert(0, DoctorFact.Of(DoctorFactNames.Version, core[^1].Version.Text));
            }

            return new DoctorCheck("doctor.dotnet.runtime", DoctorSection.Tools, DoctorStatus.Ok, facts);
        }

        return NotFound("doctor.dotnet.runtime", reading, DoctorStatus.Warning, DoctorFixCatalog.InstallDotNet);
    }

    private static DoctorCheck Python(ToolReading<ToolVersion> reading)
    {
        if (reading.State == ToolState.StoreAlias)
        {
            // The empty alias of the Microsoft Store and a Python of the Store that was never run look the same: it is not started and it is not said to be missing (docs/20 D-03).
            return new DoctorCheck("doctor.python", DoctorSection.Tools, DoctorStatus.Unknown, reading.Path is null ? [] : [DoctorFact.Of("alias", reading.Path)], DoctorFixCatalog.InstallPython);
        }

        return Version("doctor.python", reading, DoctorStatus.Warning, DoctorFixCatalog.InstallPython);
    }

    private static DoctorCheck Version(string code, ToolReading<ToolVersion> reading, DoctorStatus whenMissing, string fixCode)
    {
        if (!reading.IsFound)
        {
            return NotFound(code, reading, whenMissing, fixCode);
        }

        var facts = new List<DoctorFact> { DoctorFact.Of(DoctorFactNames.Version, reading.Value!.Text) };
        if (!string.IsNullOrWhiteSpace(reading.Path))
        {
            facts.Add(DoctorFact.Of(DoctorFactNames.Path, reading.Path));
        }

        return new DoctorCheck(code, DoctorSection.Tools, DoctorStatus.Ok, facts);
    }

    /// <summary>What a tool that did not answer is: missing (with its fix), only inside the project (an aviso, with its path), or not understood (sin comprobar).</summary>
    private static DoctorCheck NotFound<T>(string code, ToolReading<T> reading, DoctorStatus whenMissing, string fixCode)
        where T : class =>
        reading.State switch
        {
            ToolState.InsideProject => new DoctorCheck(code, DoctorSection.Tools, DoctorStatus.Warning, reading.Path is null ? [] : [DoctorFact.Of("inside-project", reading.Path)]),
            ToolState.Unreadable => new DoctorCheck(code, DoctorSection.Tools, DoctorStatus.Unknown, reading.Path is null ? [] : [DoctorFact.Of(DoctorFactNames.Path, reading.Path)]),
            _ => new DoctorCheck(code, DoctorSection.Tools, whenMissing, [], fixCode),
        };
}

/// <summary>
/// The «Contenedores» section (docs/20 D-09): Docker, with its three states, and WSL, with its three. It decides by structure and by the exit code, not
/// by the words of the tool. A problem only counts when the project declares containers (<see cref="IProjectEnvironmentProbe.DeclaresContainers"/>);
/// otherwise a missing Docker or WSL is «no aplica». The fix is always a guide: nothing is started or installed.
/// </summary>
public sealed class ContainersCheckSource(ToolSnapshotProvider tools, IProjectEnvironmentProbe environment) : IDoctorCheckSource
{
    public DoctorSection Section => DoctorSection.Containers;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        var snapshot = await tools.SnapshotAsync(project?.RootPath, ct).ConfigureAwait(false);
        var relevant = project is not null && environment.DeclaresContainers(project.RootPath);
        return [Docker(snapshot.Docker, relevant), Wsl(snapshot.Wsl, relevant)];
    }

    private static DoctorCheck Docker(ToolReading<DockerReading> reading, bool relevant)
    {
        DoctorStatus Problem(DoctorStatus status) => relevant || status == DoctorStatus.Unknown ? status : DoctorStatus.NotApplicable;

        if (reading.IsFound)
        {
            var docker = reading.Value!;
            var facts = new List<DoctorFact> { DoctorFact.Of("client", docker.Client.Text) };
            if (docker.Server is not null)
            {
                facts.Add(DoctorFact.Of("server", docker.Server.Text));
                return new DoctorCheck("doctor.docker", DoctorSection.Containers, DoctorStatus.Ok, facts);
            }

            return new DoctorCheck("doctor.docker", DoctorSection.Containers, Problem(DoctorStatus.Warning), facts, DoctorFixCatalog.StartDocker);
        }

        return reading.State switch
        {
            ToolState.NotInstalled => new DoctorCheck("doctor.docker", DoctorSection.Containers, Problem(DoctorStatus.Error), [], DoctorFixCatalog.InstallDocker),
            ToolState.InsideProject => new DoctorCheck("doctor.docker", DoctorSection.Containers, Problem(DoctorStatus.Warning), reading.Path is null ? [] : [DoctorFact.Of("inside-project", reading.Path)]),
            _ => new DoctorCheck("doctor.docker", DoctorSection.Containers, Problem(DoctorStatus.Unknown), []),
        };
    }

    private static DoctorCheck Wsl(ToolReading<WslReading> reading, bool relevant)
    {
        DoctorStatus Problem(DoctorStatus status) => relevant || status == DoctorStatus.Unknown ? status : DoctorStatus.NotApplicable;

        if (reading.IsFound)
        {
            var wsl = reading.Value!;
            // The default version is the one new distributions get; the version of the default distribution is not in the status.
            var facts = new List<DoctorFact> { DoctorFact.Of("default-version", wsl.DefaultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            if (wsl.DefaultDistribution is not null)
            {
                facts.Insert(0, DoctorFact.Of("distribution", wsl.DefaultDistribution));
            }

            if (wsl.IsReady)
            {
                return new DoctorCheck("doctor.wsl", DoctorSection.Containers, DoctorStatus.Ok, facts);
            }

            // No distribution of its own (none, or only the internal one of Docker Desktop): the guide installs one. A distribution at version 1 is a warning with no fix.
            var noDistribution = wsl.DefaultDistribution is null || wsl.IsDockerInternal;
            return new DoctorCheck("doctor.wsl", DoctorSection.Containers, Problem(DoctorStatus.Warning), facts, noDistribution ? DoctorFixCatalog.InstallWslDistribution : null);
        }

        return reading.State switch
        {
            ToolState.NotInstalled => new DoctorCheck("doctor.wsl", DoctorSection.Containers, Problem(DoctorStatus.Error), [], DoctorFixCatalog.InstallWsl),
            ToolState.InsideProject => new DoctorCheck("doctor.wsl", DoctorSection.Containers, Problem(DoctorStatus.Warning), reading.Path is null ? [] : [DoctorFact.Of("inside-project", reading.Path)]),
            _ => new DoctorCheck("doctor.wsl", DoctorSection.Containers, Problem(DoctorStatus.Unknown), []),
        };
    }
}
