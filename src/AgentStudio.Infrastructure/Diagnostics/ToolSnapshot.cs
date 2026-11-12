using System.Collections.Concurrent;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>How a question to a tool ended, in the terms of the checks: it is richer than <c>InstalledTools</c>, which cannot say «I could not tell».</summary>
public enum ToolState
{
    /// <summary>The tool answered and its answer was understood.</summary>
    Found,

    /// <summary>It is not installed (outside the projects).</summary>
    NotInstalled,

    /// <summary>It is only inside the folder of an open project, so it is not used (docs/20 D-02).</summary>
    InsideProject,

    /// <summary>Only the alias of the Microsoft Store: not an interpreter, not started.</summary>
    StoreAlias,

    /// <summary>It ran, or should have, and nothing understandable came out (timeout, failed to start, truncated, an output of another shape): the check says «sin comprobar».</summary>
    Unreadable,
}

/// <param name="Value">What was read; null unless <see cref="State"/> is <see cref="ToolState.Found"/>.</param>
/// <param name="Path">Where the tool was found, or the path that was refused.</param>
public sealed record ToolReading<T>(ToolState State, T? Value = default, string? Path = null)
    where T : class
{
    public bool IsFound => State == ToolState.Found && Value is not null;
}

/// <summary>What the door and the registry told in one run of Auto Doctor, before it is reduced to <see cref="InstalledTools"/>.</summary>
public sealed record ToolSnapshot(
    ToolReading<IReadOnlyList<InstalledSdk>> DotNetSdks,
    ToolReading<IReadOnlyList<InstalledRuntime>> DotNetRuntimes,
    ToolReading<ToolVersion> Node,
    ToolReading<ToolVersion> Npm,
    ToolReading<ToolVersion> Python,
    ToolReading<ToolVersion> Git,
    ToolReading<DockerReading> Docker,
    ToolReading<WslReading> Wsl,
    ToolReading<ToolVersion> WebView2)
{
    /// <summary>
    /// The inventory that WP-E and the verifier know. What could not be read is left as «not installed» (the type has no way to say it):
    /// the checks of Auto Doctor read the snapshot, not this.
    /// </summary>
    public InstalledTools ToInstalledTools() => new(
        DotNetSdks.IsFound ? DotNetSdks.Value! : [],
        DotNetRuntimes.IsFound ? DotNetRuntimes.Value! : [],
        Tool(Node),
        Tool(Npm),
        Tool(Python),
        Python.State == ToolState.StoreAlias,
        Tool(Git),
        Docker.IsFound
            ? new DockerStatus(Docker.Value!.Server is null ? DockerState.EngineStopped : DockerState.Running, Docker.Value.Client, Docker.Value.Server)
            : DockerStatus.NotInstalled,
        Wsl.IsFound ? Wsl.Value!.IsReady ? new WslStatus(WslState.Ready, Wsl.Value.DefaultDistribution) : new WslStatus(WslState.NoDistribution, null) : WslStatus.NotInstalled,
        WebView2.IsFound ? WebView2.Value : null);

    private static InstalledTool? Tool(ToolReading<ToolVersion> reading) => reading.IsFound ? new InstalledTool(reading.Value!, SafePath(reading.Path)) : null;

    private static string? SafePath(string? path) => path is { Length: >= 1 and <= 520 } && !Domain.Common.UntrustedText.HasHidden(path) ? path : null;
}

/// <summary>The version of the WebView2 Runtime, from the registry (docs/20 D-03); null when it is not installed. Nothing is launched.</summary>
public interface IWebView2Probe
{
    ToolVersion? Read();
}

/// <summary>
/// Reads <c>pv</c> of the two keys that Microsoft documents for the Evergreen Runtime (Learn, «Distribute your app and the WebView2 Runtime»):
/// per machine, <c>HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}</c> (64-bit view), and per user,
/// <c>HKCU\Software\Microsoft\EdgeUpdate\Clients\{…}</c>. At least one has to be defined with a version above 0.0.0.0.
/// </summary>
public sealed class WebView2RegistryProbe : IWebView2Probe
{
    private const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public ToolVersion? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        ToolVersion? best = null;
        foreach (var version in new[]
        {
            ReadValue(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientId),
            ReadValue(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Microsoft\EdgeUpdate\Clients\" + ClientId),
        })
        {
            if (version is not null && (best is null || version > best))
            {
                best = version;
            }
        }

        return best;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ToolVersion? ReadValue(Microsoft.Win32.RegistryHive hive, string subKey)
    {
        try
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            if (key?.GetValue("pv") is string text && ToolVersion.TryParse(text, out var version) && version.Numbers.Any(n => n != 0))
            {
                return version;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // A key that cannot be read is the same as a key that is not there.
        }

        return null;
    }
}

/// <summary>
/// Asks the door for every tool once and gathers the answers (docs/20 D-03). It is also the inventory WP-E uses (<see cref="IInstalledToolInventory"/>)
/// and the scope of a run of the doctor (<see cref="IDoctorRunScope"/>): while a run is open, each question is asked once however many checks need
/// it; outside a run, nothing is kept, so a later reading never sees an old answer.
/// </summary>
public sealed class ToolSnapshotProvider(IDoctorToolRunner runner, IWebView2Probe webView2) : IInstalledToolInventory, IDoctorRunScope
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ToolSnapshot>>> _run = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _runOpen;

    public void Begin()
    {
        _run.Clear();
        _runOpen = true;
    }

    public void End()
    {
        _runOpen = false;
        _run.Clear();
    }

    public async Task<InstalledTools> ReadAsync(string? projectRoot, CancellationToken ct) =>
        (await SnapshotAsync(projectRoot, ct).ConfigureAwait(false)).ToInstalledTools();

    /// <summary>The most a whole reading may take: ten tools of 10 seconds each would be 100; past this the rest is «sin comprobar» and the panel answers.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>The snapshot of this computer; the same one for every caller while a run is open.</summary>
    public Task<ToolSnapshot> SnapshotAsync(string? projectRoot, CancellationToken ct)
    {
        if (!_runOpen)
        {
            return ReadAllAsync(projectRoot, ct);
        }

        // Every caller of a run shares its token (the service gives the same one to every source), so the first one's is the run's.
        var lazy = _run.GetOrAdd(projectRoot ?? string.Empty, _ => new Lazy<Task<ToolSnapshot>>(() => ReadAllAsync(projectRoot, ct)));
        return lazy.Value.WaitAsync(ct);
    }

    private sealed record Run(string? Root, CancellationToken Ct, System.Diagnostics.Stopwatch Clock)
    {
        public bool OverBudget => Clock.Elapsed > Budget;
    }

    private async Task<ToolSnapshot> ReadAllAsync(string? projectRoot, CancellationToken ct)
    {
        var run = new Run(projectRoot, ct, System.Diagnostics.Stopwatch.StartNew());

        // The door answers one question at a time, in the order they are asked.
        var sdks = await ListAsync(DoctorTool.DotNetListSdks, ToolOutputParsers.DotNetSdks, run).ConfigureAwait(false);
        var runtimes = await ListAsync(DoctorTool.DotNetListRuntimes, ToolOutputParsers.DotNetRuntimes, run).ConfigureAwait(false);
        var node = await VersionAsync(DoctorTool.NodeVersion, run).ConfigureAwait(false);
        var npm = await VersionAsync(DoctorTool.NpmVersion, run).ConfigureAwait(false);
        var python = await PythonAsync(run).ConfigureAwait(false);
        var git = await VersionAsync(DoctorTool.GitVersion, run).ConfigureAwait(false);
        var docker = await DockerAsync(run).ConfigureAwait(false);
        var wsl = await WslAsync(run).ConfigureAwait(false);
        var webView = webView2.Read();
        return new ToolSnapshot(
            sdks,
            runtimes,
            node,
            npm,
            python,
            git,
            docker,
            wsl,
            webView is null ? new ToolReading<ToolVersion>(ToolState.NotInstalled) : new ToolReading<ToolVersion>(ToolState.Found, webView));
    }

    private async Task<ToolReading<T>> ReadAsync<T>(DoctorTool tool, Run run, Func<DoctorToolResult, T?> parse, Func<DoctorToolResult, bool>? notInstalled = null)
        where T : class
    {
        if (run.OverBudget)
        {
            return new ToolReading<T>(ToolState.Unreadable);
        }

        var result = await runner.RunAsync(tool, run.Root, run.Ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case DoctorToolOutcome.NotInstalled:
                return new ToolReading<T>(ToolState.NotInstalled, null, result.ExecutablePath);
            case DoctorToolOutcome.InsideProject:
                return new ToolReading<T>(ToolState.InsideProject, null, result.ExecutablePath);
            case DoctorToolOutcome.Completed:
                var value = result.Truncated ? null : parse(result);
                if (value is not null)
                {
                    return new ToolReading<T>(ToolState.Found, value, result.ExecutablePath);
                }

                return !result.Truncated && notInstalled?.Invoke(result) == true
                    ? new ToolReading<T>(ToolState.NotInstalled, null, result.ExecutablePath)
                    : new ToolReading<T>(ToolState.Unreadable, null, result.ExecutablePath);
            default:
                return new ToolReading<T>(ToolState.Unreadable, null, result.ExecutablePath);
        }
    }

    private Task<ToolReading<IReadOnlyList<T>>> ListAsync<T>(DoctorTool tool, Func<string?, IReadOnlyList<T>?> parse, Run run) =>
        ReadAsync<IReadOnlyList<T>>(tool, run, r => r.ExitCode == 0 ? parse(r.Output) : null);

    private Task<ToolReading<ToolVersion>> VersionAsync(DoctorTool tool, Run run) =>
        ReadAsync(tool, run, r => r.ExitCode == 0 ? ToolOutputParsers.Version(tool, r.Output) : null);

    private async Task<ToolReading<ToolVersion>> PythonAsync(Run run)
    {
        var python = await VersionAsync(DoctorTool.PythonVersion, run).ConfigureAwait(false);
        if (python.IsFound)
        {
            return python;
        }

        // The Windows launcher is the way most installs of Python are reached when «python» is the alias of the Store or is not on the PATH.
        var launcher = await VersionAsync(DoctorTool.PyVersion, run).ConfigureAwait(false);
        if (launcher.IsFound)
        {
            return launcher;
        }

        return python.State == ToolState.NotInstalled && StoreAlias.Is(python.Path) ? python with { State = ToolState.StoreAlias } : python;
    }

    /// <summary>
    /// Docker by the default context (what an empty <c>DOCKER_CONFIG</c> gives). If that does not show a running engine, the same question goes to the pipe of the Linux engine of Docker Desktop,
    /// which is where its engine may listen only; the first that shows an engine wins. Neither reads the configuration of the person.
    /// </summary>
    private async Task<ToolReading<DockerReading>> DockerAsync(Run run)
    {
        var byDefault = await ReadAsync(DoctorTool.DockerVersionJson, run, r => ToolOutputParsers.Docker(r.Output, r.Truncated)).ConfigureAwait(false);
        if ((byDefault.IsFound && byDefault.Value!.Server is not null) || byDefault.State is ToolState.NotInstalled or ToolState.InsideProject)
        {
            return byDefault;
        }

        var desktop = await ReadAsync(DoctorTool.DockerVersionDesktopPipe, run, r => ToolOutputParsers.Docker(r.Output, r.Truncated)).ConfigureAwait(false);
        return desktop.IsFound && desktop.Value!.Server is not null ? desktop : byDefault;
    }

    private Task<ToolReading<WslReading>> WslAsync(Run run) =>
        ReadAsync(
            DoctorTool.WslStatus,
            run,
            r => r.ExitCode == 0 ? ToolOutputParsers.Wsl(r.Output, r.Truncated) : null,
            r => ToolOutputParsers.WslNotInstalled(r.Output));
}