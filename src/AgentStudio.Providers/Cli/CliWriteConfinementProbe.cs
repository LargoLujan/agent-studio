using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>
/// Tells the engine whether the CLI an executor runs on keeps its file writes inside its working directory (ADR-0018,
/// «Medición»): only one whose installed version is listed in <see cref="SupportedVersions.WriteConfinementMeasured(ProviderId)"/>,
/// which lists a version only while its adapter builds the launch it was measured with. Today that is Claude Code
/// 2.1.290 with the launch of <see cref="Claude.ClaudeCodeArguments.WriteConfinementLaunch"/>; a change to that launch
/// empties the list. With a version listed, the installed one is read with <c>--version</c> each time it is asked (no
/// prompt, no quota, once per run started or resumed), so an update between two runs is never missed. A CLI with
/// nothing listed is not even asked; one that is not installed, cannot start, times out, fails or prints no version is
/// not confined (fail closed). A confined answer speaks for the measured permissions only
/// (<see cref="MeasuredWriteConfinement"/>).
/// </summary>
public sealed class CliWriteConfinementProbe : IWriteConfinementProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The command of each CLI a version can be listed for.</summary>
    private static readonly Dictionary<ProviderId, string> Commands = new()
    {
        [ProviderId.ClaudeCode] = "claude",
    };

    private readonly IExecutableLocator _locator;

    /// <summary>The folder the command starts in (never the project); null means the system folder (see <see cref="NeutralWorkingFolder"/>).</summary>
    public string? WorkingDirectory { get; init; }

    private readonly IProcessRunner _runner;
    private readonly Func<ProviderId, IReadOnlyList<string>> _measured;

    public CliWriteConfinementProbe(IExecutableLocator locator, IProcessRunner runner)
        : this(locator, runner, provider => SupportedVersions.WriteConfinementMeasured(provider))
    {
    }

    /// <param name="locator">Finds the CLI on the PATH.</param>
    /// <param name="runner">Runs its <c>--version</c>.</param>
    /// <param name="measured">The versions of each CLI whose confinement was measured with the launch built today (<see cref="SupportedVersions.WriteConfinementMeasured(ProviderId)"/>).</param>
    public CliWriteConfinementProbe(IExecutableLocator locator, IProcessRunner runner, Func<ProviderId, IReadOnlyList<string>> measured)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(measured);
        _locator = locator;
        _runner = runner;
        _measured = measured;
    }

    public async Task<bool> IsWriteConfinedAsync(ProviderId provider, CancellationToken cancellationToken)
    {
        var measured = _measured(provider);
        if (measured.Count == 0 || !Commands.TryGetValue(provider, out var command) || _locator.Find(command) is not { } executable)
        {
            return false;
        }

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(new ProcessSpec(executable, ["--version"], NeutralWorkingFolder.Prepare(WorkingDirectory), Timeout: CommandTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A CLI that cannot start is one whose confinement nobody can vouch for: not confined.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        return !result.TimedOut
            && result.ExitCode == 0
            && CliVersionParser.Parse(result.StandardOutput) is { } version
            && measured.Contains(version, StringComparer.Ordinal);
    }
}
