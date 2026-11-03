namespace AgentStudio.Application.Projects;

/// <summary>
/// Reads the files Agent Studio keeps in a project's <c>.agentstudio</c> folder with the same policy as the writes of
/// <see cref="IProjectStorageFiles"/> (docs/14 D-31, ADR-0010 addendum H-01): each folder on the way must be a real
/// folder and the file a file with a single name, never a link, and Windows must reach it at its own place under the final
/// path of the project root. So a link planted as a day of the journal can never copy a file from outside the project into
/// what Agent Studio writes. It is a port of its own, so Phase 4 changes no implementation of the writes.
/// </summary>
public interface IProjectStorageReader
{
    /// <summary>
    /// Opens <c>.agentstudio/&lt;relativePath&gt;</c> of the project at <paramref name="projectRoot"/> to read, without
    /// following a link, and checks that same handle before returning the stream. The path is relative to
    /// <c>.agentstudio</c>: a file name, or a folder and a file name with <c>/</c> between them.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The path, a folder or the file is a link, has a second name or is not at its place; nothing was read.</exception>
    Stream OpenForRead(string projectRoot, string relativePath);
}
