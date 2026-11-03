using System.Xml;

namespace AgentStudio.Orchestration.Verification.Reports;

/// <summary>Shared XML plumbing of the report parsers.</summary>
internal static class TestReportXml
{
    // A report of a big suite with captured output can be hundreds of megabytes: the cap only stops an unbounded input.
    private const long MaxCharactersInDocument = 1_000_000_000;

    /// <summary>
    /// A forward-only reader that is safe for untrusted input: a DOCTYPE is rejected outright (no entities, no external
    /// DTD, so no XXE and no entity expansion bombs) and nothing is ever resolved from disk or the network.
    /// </summary>
    internal static XmlReader CreateReader(Stream report)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxCharactersInDocument,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };

        return XmlReader.Create(report, settings);
    }

    /// <summary>Opens a report for sequential reading while another process may still hold it open.</summary>
    internal static FileStream OpenFile(string path, string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new TestReportException($"The {format} report '{path}' cannot be read: {ex.Message}", ex);
        }
    }
}
