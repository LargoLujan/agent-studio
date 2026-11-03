namespace AgentStudio.Observability.Logging;

/// <summary>One finished JSON line (including its trailing newline) and the local day it belongs to.</summary>
/// <param name="Day">Local calendar day of the entry's timestamp; it decides which day file receives the line.</param>
/// <param name="Bytes">UTF-8 text of the line, without BOM.</param>
internal readonly record struct LogLine(DateOnly Day, byte[] Bytes);

/// <summary>
/// Where finished lines end up. The production implementation is <see cref="RollingFileSink"/>; tests substitute their
/// own sink to hold the writer thread and exercise the queue.
/// </summary>
internal interface ILogLineSink : IDisposable
{
    /// <summary>Deletes the files that are past the retention window. Returns how many were deleted.</summary>
    int DeleteExpired();

    /// <summary>
    /// Appends complete lines, in order. May throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>;
    /// the caller counts the failure and carries on.
    /// </summary>
    void Append(IReadOnlyList<LogLine> lines);
}
