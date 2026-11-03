namespace AgentStudio.Application.Execution;

/// <summary>
/// Creates the work area of one isolated verification run (ADR-0017): new folders labelled Low integrity, holding a copy
/// of the tree of one commit. The factory only prepares the folders and their labels; the copy is written by the hardened
/// git layer, from the project's repository, so no worktree is ever read to make it and none is the folder the isolated
/// processes run in.
/// </summary>
public interface IIsolatedWorkAreaFactory
{
    /// <param name="commit">The full object id of the commit the area holds (<see cref="IIsolatedWorkArea.Commit"/>).</param>
    /// <param name="writeTree">
    /// Writes the tree of <paramref name="commit"/> into the empty folder it is given, the copy of the area
    /// (<see cref="IIsolatedWorkArea.SourceDirectory"/>), before the area is labelled Low: no Low process can write there
    /// while it runs. The verification runner gives <c>IWorktreeManager.ArchiveCommitAsync</c> on the project's repository.
    /// </param>
    /// <exception cref="ProcessIsolationException">The area could not be prepared, also when <paramref name="writeTree"/> failed; nothing of it is left behind.</exception>
    Task<IIsolatedWorkArea> CreateAsync(string commit, Func<string, CancellationToken, Task> writeTree, CancellationToken cancellationToken);
}

/// <summary>
/// The folders of one isolated run. They are labelled Low, so a process started with
/// <see cref="ProcessIsolation.LowIntegrity"/> can write in them, and the folders above them are not. Disposing removes
/// the whole area without following links; it is only done once every process that used the area has ended.
/// </summary>
public interface IIsolatedWorkArea : IAsyncDisposable
{
    /// <summary>The full object id of the commit whose tree <see cref="SourceDirectory"/> holds.</summary>
    string Commit { get; }

    /// <summary>The folder that holds every other folder of the area.</summary>
    string RootDirectory { get; }

    /// <summary>The copy of the tree of <see cref="Commit"/>, without <c>.git</c>: where the steps run.</summary>
    string SourceDirectory { get; }

    /// <summary>The temporary folder of the run (<c>TEMP</c> and <c>TMP</c>).</summary>
    string TempDirectory { get; }

    /// <summary>The home of the .NET CLI for the run (<c>DOTNET_CLI_HOME</c>).</summary>
    string CliHomeDirectory { get; }

    /// <summary>The NuGet global packages folder of this run alone (<c>NUGET_PACKAGES</c>), empty at the start.</summary>
    string NuGetPackagesDirectory { get; }

    /// <summary>The NuGet HTTP cache of this run alone (<c>NUGET_HTTP_CACHE_PATH</c>).</summary>
    string NuGetHttpCacheDirectory { get; }

    /// <summary>The NuGet plugins cache of this run alone (<c>NUGET_PLUGINS_CACHE_PATH</c>).</summary>
    string NuGetPluginsCacheDirectory { get; }

    /// <summary>Creates a new, empty folder with an unpredictable name inside the area, for what one step writes.</summary>
    string CreateResultsDirectory();

    /// <summary>
    /// Opens for reading a file a process of the area wrote, such as a test report: only a regular file inside the area,
    /// reached without links, and of at most <paramref name="maxBytes"/> bytes.
    /// </summary>
    /// <exception cref="IOException">The file is missing, is a link or is reached through one, is outside the area or is too large.</exception>
    Stream OpenReport(string path, long maxBytes);
}
