namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Something Agent Studio keeps under a project's <c>.agentstudio</c> folder is not what it must be: a link (junction or
/// symbolic link) where a folder or a file should be, a file with a second name elsewhere, or a path that leads outside
/// the project. The storage is not used, so nothing is written through it (ADR-0010, addendum of 2026-10-18).
/// </summary>
public sealed class ProjectStorageIntegrityException : IOException
{
    public ProjectStorageIntegrityException()
    {
    }

    public ProjectStorageIntegrityException(string message)
        : base(message)
    {
    }

    public ProjectStorageIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
