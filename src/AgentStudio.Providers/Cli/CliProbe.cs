using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>
/// Shared flow of the CLI probes: locate the executable, read <c>--version</c>, then ask the subclass about the
/// session. Only commands that send no prompt and spend no quota are run. CLI failures never throw.
/// </summary>
public abstract class CliProbe : IProviderProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _runner;

    protected CliProbe(IExecutableLocator locator, IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        _locator = locator;
        _runner = runner;
    }

    /// <summary>
    /// The folder the probe commands start in: never the open project, whose files (<c>.npmrc</c>, a <c>global.json</c>...) a command could
    /// read. Null means the system folder (<see cref="NeutralWorkingFolder"/>); the composition gives one of the app.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    public abstract ProviderId Provider { get; }

    public abstract VendorId Vendor { get; }

    public abstract string DisplayName { get; }

    /// <summary>Command looked up on the PATH.</summary>
    protected abstract string Command { get; }

    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var path = _locator.Find(Command);
        if (path is null)
        {
            // Found, but only inside the open project, which is not trusted to provide programs: the desktop says so in the language in use.
            var insideProject = _locator.FindInUntrustedRoot(Command);
            return Result(InstallationState.NotInstalled, null, null, AuthenticationState.Unknown, null, [$"No se encontró «{Command}» en el PATH."], insideProject);
        }

        var notes = new List<string>();
        var versionRun = await RunAsync(path, ["--version"], cancellationToken).ConfigureAwait(false);
        if (versionRun.Failure is not null)
        {
            notes.Add($"No se pudo obtener la versión: {versionRun.Failure}");
            return Result(InstallationState.Error, path, null, AuthenticationState.Unknown, null, notes);
        }

        if (versionRun.Result is { ExitCode: not 0 } failed)
        {
            notes.Add($"«--version» terminó con código {failed.ExitCode}.");
            return Result(InstallationState.Error, path, null, AuthenticationState.Unknown, null, notes);
        }

        var version = CliVersionParser.Parse(versionRun.Output);
        if (version is null)
        {
            notes.Add("La salida de --version no contiene un número de versión reconocible.");
            return Result(InstallationState.Error, path, null, AuthenticationState.Unknown, null, notes);
        }

        if (SupportedVersions.UntestedNote(Provider, version) is { } untested)
        {
            notes.Add(untested);
        }

        var auth = await CheckAuthenticationAsync(path, notes, cancellationToken).ConfigureAwait(false);
        return Result(InstallationState.Installed, path, version, auth.State, auth.Method, notes);
    }

    /// <summary>Checks the session with a command that spends no quota, adding explanations to <paramref name="notes"/>.</summary>
    protected abstract Task<CliAuthStatus> CheckAuthenticationAsync(string executablePath, List<string> notes, CancellationToken cancellationToken);

    /// <summary>Runs one probe command; <see cref="CommandRun.Failure"/> is set on timeout or when it cannot start.</summary>
    protected async Task<CommandRun> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            // Never the folder the app inherited, which may be the one of a project: see WorkingDirectory.
            var folder = NeutralWorkingFolder.Prepare(WorkingDirectory);
            var result = await _runner.RunAsync(new ProcessSpec(executablePath, arguments, folder, Timeout: CommandTimeout), cancellationToken).ConfigureAwait(false);
            var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardOutput + "\n" + result.StandardError;
            return result.TimedOut
                ? new CommandRun(result, text, "se agotó el tiempo de espera (20 s).")
                : new CommandRun(result, text, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A CLI that cannot start must be reported, not thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new CommandRun(null, "", $"no se pudo ejecutar ({ex.GetType().Name}).");
        }
    }

    protected sealed record CommandRun(ProcessResult? Result, string Output, string? Failure);

    private ProviderProbeResult Result(
        InstallationState installation,
        string? path,
        string? version,
        AuthenticationState authentication,
        string? method,
        IReadOnlyList<string> notes,
        string? foundInsideProject = null) =>
        new(Provider, DisplayName, installation, path, version, authentication, method, notes, foundInsideProject);
}
