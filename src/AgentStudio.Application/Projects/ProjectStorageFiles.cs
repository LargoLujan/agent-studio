namespace AgentStudio.Application.Projects;

/// <summary>
/// Writes the files Agent Studio keeps in a project's <c>.agentstudio</c> folder besides its database and evidence: the
/// prompt journal (<c>bitacora/</c>), the canvas layout and the verification profile. Every write goes through the policy
/// that opens the project's storage (ADR-0010, addenda of 2026-10-18): each folder on the way must be a real folder and the
/// file a file with a single name, never a link, and Windows must reach each one at its own place under the final path of
/// the project root. Otherwise the write is refused with an <see cref="IOException"/> that names the path and why, and
/// nothing was written through the link.
/// </summary>
/// <remarks>
/// A path is relative to <c>.agentstudio</c>: a file name, or a folder and a file name with <c>/</c> between them. A folder
/// that is missing is created, checked before and after.
/// </remarks>
public interface IProjectStorageFiles
{
    /// <summary>
    /// Opens <c>.agentstudio/&lt;relativePath&gt;</c> of the project at <paramref name="projectRoot"/> to read and append,
    /// creating it when it is missing. The file is opened without following a link it may be, and that same handle is
    /// checked before the stream is returned. Only reading is shared while it is open: another writer gets a sharing
    /// violation, an <see cref="IOException"/> whose <see cref="Exception.HResult"/> says so, and may try again.
    /// </summary>
    Stream OpenForAppend(string projectRoot, string relativePath);

    /// <summary>
    /// Replaces <c>.agentstudio/&lt;relativePath&gt;</c> of the project at <paramref name="projectRoot"/> with
    /// <paramref name="content"/> in a single move: the new file is written whole in the storage's temporary folder first,
    /// so a reader never sees half of it.
    /// </summary>
    void Replace(string projectRoot, string relativePath, ReadOnlySpan<byte> content);
}
