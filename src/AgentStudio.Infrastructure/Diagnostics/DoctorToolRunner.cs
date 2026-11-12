using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// The one door for launching system tools from Auto Doctor (docs/20 D-02, ADR-0061). What makes F6-7 true by construction and not by care:
/// <list type="bullet">
/// <item>the caller names an entry of <see cref="DoctorTool"/>; the program and its arguments come from <see cref="DoctorToolCatalog"/>;</item>
/// <item>the executable is resolved by the hardened locator (outside every open project, links resolved), and a result that lies inside the
/// project of the caller is refused again here: two checks for the same thing;</item>
/// <item>the working folder is always the neutral one of Agent Studio, never a folder of a project;</item>
/// <item>the environment is the allow list of <see cref="DoctorEnvironment"/>;</item>
/// <item>10 seconds and 64 000 characters per question, one question at a time, no standard input.</item>
/// </list>
/// </summary>
public sealed class DoctorToolRunner : IDoctorToolRunner, IAsyncDisposable
{
    /// <summary>How long a tool has to answer (docs/20 D-02).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly DoctorFolders _folders;
    private readonly Func<IReadOnlyDictionary<string, string>> _ambient;
    private readonly SerialQueue _queue = new();

    /// <param name="ambient">The environment variables the process has, to remove the ones that are not allowed; the ones of the current process by default.</param>
    public DoctorToolRunner(IExecutableLocator locator, IProcessRunner runner, DoctorFolders folders, Func<IReadOnlyDictionary<string, string>>? ambient = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(folders);
        _locator = locator;
        _runner = runner;
        _folders = folders;
        _ambient = ambient ?? DoctorEnvironment.CurrentProcess;
    }

    public Task<DoctorToolResult> RunAsync(DoctorTool tool, string? projectRoot, CancellationToken ct)
    {
        var entry = DoctorToolCatalog.Of(tool);
        return _queue.RunAsync(token => AskAsync(entry, projectRoot, token), ct);
    }

    private async Task<DoctorToolResult> AskAsync(DoctorToolEntry entry, string? projectRoot, CancellationToken ct)
    {
        var path = _locator.Find(entry.Command);
        if (path is null)
        {
            var inside = _locator.FindInUntrustedRoot(entry.Command);
            return DoctorToolResult.Without(inside is null ? DoctorToolOutcome.NotInstalled : DoctorToolOutcome.InsideProject, inside);
        }

        if (_locator.IsUntrusted(path) || LiesInside(path, projectRoot))
        {
            return DoctorToolResult.Without(DoctorToolOutcome.InsideProject, path);
        }

        if (entry.RefusesStoreAlias && StoreAlias.Is(path))
        {
            // Not started: the alias may open the Microsoft Store, and it is not an interpreter. The inventory reads the path.
            return DoctorToolResult.Without(DoctorToolOutcome.NotInstalled, path);
        }

        var spec = new ProcessSpec(
            path,
            entry.Arguments,
            _folders.WorkingFolder,
            DoctorEnvironment.For(entry.Tool, _folders, _ambient()),
            Timeout,
            StandardInput: null,
            MaxOutputCharacters: DoctorToolResult.MaxOutputLength);
        try
        {
            _folders.Ensure();
            var result = await _runner.RunAsync(spec, ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return DoctorToolResult.Without(DoctorToolOutcome.TimedOut, path);
            }

            var output = entry.StderrWhenBlank && string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
            var truncated = result.OutputLimitExceeded || output.Length > DoctorToolResult.MaxOutputLength;
            return new DoctorToolResult(
                DoctorToolOutcome.Completed,
                result.ExitCode,
                output.Length > DoctorToolResult.MaxOutputLength ? output[..DoctorToolResult.MaxOutputLength] : output,
                truncated,
                path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A tool that cannot start is an answer (FailedToStart), never a failure of the panel.
        catch (Exception)
#pragma warning restore CA1031
        {
            return DoctorToolResult.Without(DoctorToolOutcome.FailedToStart, path);
        }
    }

    private static bool LiesInside(string path, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }

        var roots = UntrustedPaths.Resolve([projectRoot]);
        return UntrustedPaths.IsInside(path, roots);
    }

    public ValueTask DisposeAsync() => _queue.DisposeAsync();
}

/// <summary>The alias of the Microsoft Store (<c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>) that stands in for a program that is not installed.</summary>
public static class StoreAlias
{
    public static bool Is(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Replace('/', '\\').Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);
}
