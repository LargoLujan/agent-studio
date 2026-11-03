namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Another instance of Agent Studio has the project open. Two instances orchestrating the same project would write
/// two decision logs over one database, so the second one is refused.
/// </summary>
public sealed class ProjectInUseException : IOException
{
    public ProjectInUseException()
    {
    }

    public ProjectInUseException(string message)
        : base(message)
    {
    }

    public ProjectInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
