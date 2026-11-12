using AgentStudio.Application.Projects;

namespace AgentStudio.Application.Git;

// Phase 6 contract (docs/20 D-19 and section 4.2, ADR-0062). The implementation (WP-G) copies the commit with the same call the verification uses
// (IWorktreeManager.ArchiveCommitAsync) into an isolated work area. No port for reading objects of git (ls-tree, cat-file) is added.

/// <summary>
/// A copy of one commit that can be read without running anything of it (docs/20 D-19): the analysis of Auto Graph and the consistency checks
/// both read the same copy, which is opened once for both and deleted when it is disposed. Everything in it is a file of the repository and
/// therefore data (ADR-0018).
/// </summary>
public interface IRepositorySnapshot : IAsyncDisposable
{
    /// <summary>The commit the copy holds, by its full id.</summary>
    string Commit { get; }

    /// <summary>The root of the copy, inside an isolated work area. Only this folder is ever opened: nothing outside it.</summary>
    string Root { get; }

    /// <summary>
    /// The files of the copy: relative to <see cref="Root"/>, with <c>/</c>, in ordinal order, none of them a link and none leading out of the
    /// copy (<c>Domain.Common.RepositoryPaths.IsSafeRelative</c>). A file whose path is not safe is not listed.
    /// </summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Opens a file of the copy to read it. It refuses a path that is not in <see cref="Paths"/>, a symbolic link or a junction (also in any folder
    /// of the path) and a file longer than <paramref name="maxBytes"/>, and it checks what it opened, not its name again.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not one of the copy.</exception>
    /// <exception cref="IOException">The file is a link, is longer than the limit or could not be opened.</exception>
    Stream OpenRead(string relativePath, long maxBytes);
}

/// <summary>Opens copies of a commit (docs/20 D-19). It shares the lock of «una a la vez» with the verification: with a verification running it waits.</summary>
public interface IRepositorySnapshots
{
    /// <param name="commit">The full id of the commit (40 or 64 lowercase hexadecimal characters).</param>
    Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct);
}
