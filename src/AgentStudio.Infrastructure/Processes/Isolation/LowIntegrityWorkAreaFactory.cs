using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <param name="Root">
/// The folder that holds one folder per run. Default: <c>%USERPROFILE%\AppData\LocalLow\AgentStudio\verify</c>. The
/// folders of the root that do not exist are created labelled Medium, so a Low process cannot create anything at that
/// level; an existing folder keeps its label.
/// </param>
/// <param name="CopyTimeout">How long writing the tree of the commit into the area may take (default ten minutes).</param>
/// <param name="LockFile">
/// The lock that only one isolated verification of the user holds at a time, from before its area is made until after it
/// is deleted. Default: <c>%LOCALAPPDATA%\AgentStudio\verify.lock</c>, the same for every Agent Studio of the user. Only
/// tests give their own, and then a <paramref name="Root"/> of their own as well: the leftovers of a root are only removed
/// under the lock every run in that root takes.
/// </param>
public sealed record LowIntegrityWorkAreaOptions(string? Root = null, TimeSpan? CopyTimeout = null, string? LockFile = null);

/// <summary>
/// Creates the work area of an isolated verification run (ADR-0017; spike in
/// docs/investigacion/2026-10-16-spike-verificacion-aislada.md):
/// <list type="number">
/// <item>the lock of the user, taken before anything is made and given back once the area is deleted: all the Low
/// processes of the user share one identity, so only one isolated verification of the user runs at a time, whichever
/// process runs it. While another holds it, this one waits (until cancelled) and makes nothing;</item>
/// <item>under the lock, the areas that runs which never ended (a crash, a kill) left in the root are removed: only
/// folders named as this factory names areas, without following links;</item>
/// <item>a new folder <c>&lt;root&gt;\v-&lt;random&gt;</c> born labelled Medium, so no Low process can put anything in it
/// while it is filled, and checked to be reached without links;</item>
/// <item>the tree of the commit in its <c>src</c> folder, written by the caller's writer: the hardened git layer
/// (<c>IWorktreeManager.ArchiveCommitAsync</c>), which copies the commit from the project's repository. The factory runs no
/// git and reads no worktree;</item>
/// <item>the folder relabelled Low, which Windows propagates to the copy, and the folders of the run (temporary, CLI home,
/// NuGet packages and caches) created inside it.</item>
/// </list>
/// Anything that fails, the writer included, removes what was created and throws <see cref="ProcessIsolationException"/>.
/// </summary>
public sealed partial class LowIntegrityWorkAreaFactory : IIsolatedWorkAreaFactory
{
    // An area is "v-" and twelve hexadecimal digits; an older version, which took no lock, named them with the digits alone.
    private const string AreaPrefix = "v-";

    private static readonly TimeSpan DefaultCopyTimeout = TimeSpan.FromMinutes(10);
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    private readonly LowIntegrityWorkAreaOptions _options;

    /// <exception cref="ArgumentException">The options give a lock file of their own without a root of their own.</exception>
    public LowIntegrityWorkAreaFactory(LowIntegrityWorkAreaOptions? options = null)
    {
        _options = options ?? new LowIntegrityWorkAreaOptions();
        if (_options.LockFile is not null && _options.Root is null)
        {
            throw new ArgumentException(
                "A lock file of its own needs a root of its own: the leftovers of a root are only removed under the lock every run in that root takes.",
                nameof(options));
        }
    }

    public async Task<IIsolatedWorkArea> CreateAsync(string commit, Func<string, CancellationToken, Task> writeTree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeTree);
        if (commit is null || !ObjectIdPattern().IsMatch(commit))
        {
            throw new ProcessIsolationException($"'{commit}' is not the full object id of a commit; nothing was prepared.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new ProcessIsolationException("Isolated verification needs Windows; nothing was prepared.");
        }

        return await CreateOnWindowsAsync(commit.ToLowerInvariant(), writeTree, cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdPattern();

    [GeneratedRegex(@"\Av-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AreaNamePattern();

    [SupportedOSPlatform("windows")]
    private async Task<IIsolatedWorkArea> CreateOnWindowsAsync(string commit, Func<string, CancellationToken, Task> writeTree, CancellationToken ct)
    {
        // One isolated verification of the user at a time: the area holds the lock until it is deleted.
        var held = await IsolationLock.AcquireAsync(Path.GetFullPath(_options.LockFile ?? DefaultLockFile()), ct).ConfigureAwait(false);
        try
        {
            var root = PrepareRoot();
            await DeleteLeftoversAsync(root).ConfigureAwait(false);
            var (area, areaFinal) = CreateRunFolder(root);
            try
            {
                var source = Path.Combine(area, "src");
                Directory.CreateDirectory(source);
                await WriteTreeAsync(commit, writeTree, source, ct).ConfigureAwait(false);

                IntegrityLabels.Set(area, IntegrityLabels.Low);
                RequireLabel(area, IntegrityLabels.LowRid);
                RequireLabel(source, IntegrityLabels.LowRid);

                return new LowIntegrityWorkArea(
                    commit,
                    area,
                    areaFinal,
                    held,
                    source,
                    temp: CreateInside(area, "tmp"),
                    cliHome: CreateInside(area, "home"),
                    nuGetPackages: CreateInside(area, "nuget"),
                    nuGetHttpCache: CreateInside(area, "nuget-http"),
                    nuGetPluginsCache: CreateInside(area, "nuget-plugins"));
            }
            catch (Exception ex) when (ex is not ProcessIsolationException and not OperationCanceledException
                && (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or InvalidOperationException or ArgumentException))
            {
                await LowIntegrityWorkArea.DeleteAsync(area).ConfigureAwait(false);
                throw new ProcessIsolationException($"The isolated work area could not be prepared: {ex.Message}", ex);
            }
            catch
            {
                await LowIntegrityWorkArea.DeleteAsync(area).ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes, under the lock, the areas that runs which never ended (a crash, a kill) left in the root: no verification of
    /// the user can be using one, and a Low process of another run could reach what they hold. Only folders named as this
    /// factory names areas are touched, without following links; an area of an older version, which took no lock, may be
    /// in use and is left alone. A folder that cannot be removed is only logged.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task DeleteLeftoversAsync(string root)
    {
        foreach (var folder in Directory.GetDirectories(root))
        {
            if (AreaNamePattern().IsMatch(Path.GetFileName(folder)))
            {
                await LowIntegrityWorkArea.DeleteAsync(folder).ConfigureAwait(false);
            }
        }
    }

    private static string DefaultLockFile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? throw new ProcessIsolationException("The local application data folder of the user could not be found, so there is no lock for the isolated verifications; nothing was prepared.")
            : Path.Combine(local, "AgentStudio", "verify.lock");
    }

    /// <summary>The tree of the commit, written while the area is still Medium and within the copy timeout.</summary>
    private async Task WriteTreeAsync(string commit, Func<string, CancellationToken, Task> writeTree, string source, CancellationToken ct)
    {
        var limit = _options.CopyTimeout ?? DefaultCopyTimeout;
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await writeTree(source, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProcessIsolationException($"Copying commit {commit} took longer than {limit}; nothing was prepared.", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private string PrepareRoot()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.Root ?? DefaultRoot()));

        // The missing folders are created labelled Medium; the existing ones keep their label.
        var missing = new Stack<string>();
        for (var current = root; !Directory.Exists(current); current = Path.GetDirectoryName(current)
            ?? throw new ProcessIsolationException($"The root '{root}' of the isolated work areas cannot be created."))
        {
            missing.Push(current);
        }

        while (missing.TryPop(out var folder))
        {
            try
            {
                IntegrityLabels.CreateDirectory(folder, IntegrityLabels.Medium);
                RequireLabel(folder, IntegrityLabels.MediumRid);
            }
            catch (IOException) when (Directory.Exists(folder))
            {
                // Created meanwhile by another run of Agent Studio.
            }
        }

        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ProcessIsolationException($"The root '{root}' of the isolated work areas is a link; nothing was prepared.");
        }

        return root;
    }

    [SupportedOSPlatform("windows")]
    private static string DefaultRoot()
    {
        var result = IsolationNativeMethods.SHGetKnownFolderPath(LocalAppDataLow, 0, 0, out var pointer);
        try
        {
            if (result != 0 || pointer == 0)
            {
                throw new ProcessIsolationException($"The LocalLow folder of the user could not be found (HRESULT 0x{result:X8}).");
            }

            return Path.Combine(Marshal.PtrToStringUni(pointer)!, "AgentStudio", "verify");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>
    /// A new folder with a random name, born Medium, reached without links from the root, and the path Windows reaches for
    /// it (every link above the root resolved), which the area keeps to refuse anything reached through a later link.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string Path, string Final) CreateRunFolder(string root)
    {
        var rootFinal = IntegrityLabels.FinalPath(root)
            ?? throw new ProcessIsolationException($"The root '{root}' of the isolated work areas cannot be opened.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var name = AreaPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            var path = Path.Combine(root, name);
            try
            {
                IntegrityLabels.CreateDirectory(path, IntegrityLabels.Medium);
            }
            catch (IOException) when (Path.Exists(path))
            {
                continue;
            }

            try
            {
                var final = Path.Combine(rootFinal, name);
                if (!string.Equals(IntegrityLabels.FinalPath(path), final, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProcessIsolationException($"The work area '{path}' is reached through a link; it was not used.");
                }

                // Until the copy is complete no Low process may write here.
                RequireLabel(path, IntegrityLabels.MediumRid);
                return (path, final);
            }
            catch
            {
                Directory.Delete(path);
                throw;
            }
        }

        throw new ProcessIsolationException($"No new work area could be created under '{root}'.");
    }

    [SupportedOSPlatform("windows")]
    private static string CreateInside(string area, string name)
    {
        var path = Path.Combine(area, name);
        IntegrityLabels.CreateDirectory(path, label: null);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireLabel(string path, uint expected)
    {
        var rid = IntegrityLabels.ReadRid(path);
        if (rid != expected)
        {
            throw new ProcessIsolationException(
                $"'{path}' did not get the integrity label 0x{expected:X4} (it has {(rid is null ? "none" : $"0x{rid:X4}")}); nothing was prepared.");
        }
    }
}
