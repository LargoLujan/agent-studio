namespace AgentStudio.Memory.Documents;

/// <summary>The folder of an export is inside the project and a run is going in it: the files are written when it ends (docs/17 D-60).</summary>
public sealed class ClientDocumentRunBusyException : InvalidOperationException
{
    public ClientDocumentRunBusyException()
        : base("A run is going in the project: the document is exported into its folder when it ends.")
    {
    }

    public ClientDocumentRunBusyException(string message)
        : base(message)
    {
    }

    public ClientDocumentRunBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
