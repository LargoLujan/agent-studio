namespace AgentStudio.Orchestration.Verification.Reports;

/// <summary>
/// A test report is missing, unreadable, truncated, not the expected format or not well-formed XML. A report that
/// cannot be read proves nothing, so the caller must treat the verification step as failed rather than guess counts.
/// </summary>
public sealed class TestReportException : Exception
{
    public TestReportException(string message)
        : base(message)
    {
    }

    public TestReportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
