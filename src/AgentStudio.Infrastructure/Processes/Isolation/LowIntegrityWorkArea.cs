using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Git;
using static AgentStudio.Infrastructure.Processes.Isolation.IsolationNativeMethods;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// One Low work area (<see cref="LowIntegrityWorkAreaFactory"/>). Every folder is under <see cref="RootDirectory"/>, which
/// is labelled Low; disposing deletes it with links removed as links, never followed (the pattern of the git layer), and
/// only then lets the lock of the user go.
/// </summary>
/// <param name="finalRoot">
/// The path Windows reached for <paramref name="root"/> when the area was made, every link resolved. A results folder or a
/// report whose final path is not under it was reached through a link (inside the area or above it) and is refused.
/// </param>
/// <param name="held">
/// The lock of the user, held for as long as the area exists: its steps, the reading of their reports and its deletion.
/// </param>
[SupportedOSPlatform("windows")]
internal sealed class LowIntegrityWorkArea(
    string commit,
    string root,
    string finalRoot,
    IsolationLock held,
    string source,
    string temp,
    string cliHome,
    string nuGetPackages,
    string nuGetHttpCache,
    string nuGetPluginsCache) : IIsolatedWorkArea
{
    private int _disposed;

    public string Commit { get; } = commit;

    public string RootDirectory { get; } = root;

    public string SourceDirectory { get; } = source;

    public string TempDirectory { get; } = temp;

    public string CliHomeDirectory { get; } = cliHome;

    public string NuGetPackagesDirectory { get; } = nuGetPackages;

    public string NuGetHttpCacheDirectory { get; } = nuGetHttpCache;

    public string NuGetPluginsCacheDirectory { get; } = nuGetPluginsCache;

    public string CreateResultsDirectory()
    {
        // A name nobody can guess: a process of an earlier step cannot have prepared the folder of the next one.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var name = "r-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var path = Path.Combine(RootDirectory, name);
            try
            {
                IntegrityLabels.CreateDirectory(path, label: null);
            }
            catch (IOException) when (Path.Exists(path))
            {
                continue;
            }

            // New, so empty; but only the folder of the area if no link on the way led the creation elsewhere.
            if (!string.Equals(IntegrityLabels.FinalPath(path), Path.Combine(finalRoot, name), StringComparison.OrdinalIgnoreCase))
            {
                TryRemoveEmpty(path);
                throw new IOException($"The results folder '{path}' is reached through a link, so it is not used.");
            }

            return path;
        }

        throw new IOException($"No new results folder could be created in '{RootDirectory}'.");
    }

    public Stream OpenReport(string path, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{full}' is outside the isolated work area, so it is not read.");
        }

        // Every folder between the area and the file must be a real folder, not a link a process of the area left.
        for (var folder = Path.GetDirectoryName(full); folder is not null && folder.Length > RootDirectory.Length; folder = Path.GetDirectoryName(folder))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
            {
                throw new IOException($"'{folder}' cannot be inspected: {ex.Message}", ex);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"'{full}' is reached through the link '{folder}', so it is not read.");
            }
        }

        var handle = IntegrityLabels.OpenWithoutFollowing(full, out var error)
            ?? throw new IOException($"'{full}' cannot be opened: {new Win32Exception(error).Message}");
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException($"'{full}' cannot be inspected.");
            }

            if ((information.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
            {
                throw new IOException($"'{full}' is not a regular file, so it is not read.");
            }

            var size = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            if (size > maxBytes)
            {
                throw new IOException($"'{full}' has {size} bytes, more than the {maxBytes} a report may have, so it is not read.");
            }

            // What is open must be that file of the area: a link anywhere on the way, also above the area or put there since
            // the checks above, leads somewhere else.
            var expected = Path.Combine(finalRoot, full[(RootDirectory.Length + 1)..]);
            if (!string.Equals(IntegrityLabels.FinalPath(handle), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"'{full}' is reached through a link, so it is not read.");
            }

            return new FileStream(handle, FileAccess.Read, 64 * 1024);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await DeleteAsync(RootDirectory).ConfigureAwait(false);
            }
            finally
            {
                // Only once the area is gone may another verification of the user make its own.
                held.Dispose();
            }
        }
    }

    // A folder this area has just made, empty: removing it never follows a link (RemoveDirectory removes a link itself).
    private static void TryRemoveEmpty(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the deletion of the area.
        }
    }

    /// <summary>Deletes a folder of a work area; a link inside is removed as a link. A failure is only logged.</summary>
    internal static async Task DeleteAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                await WorktreeFileSystem.RetryWhileInUseAsync(() => WorktreeFileSystem.DeleteTree(path), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Agent Studio: the isolated work area '{0}' could not be deleted ({1}).", path, ex.Message);
        }
    }
}
