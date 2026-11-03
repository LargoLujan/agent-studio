using System.Xml;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification.Reports;

/// <summary>
/// Reads a TRX report (Visual Studio test results, the format of <c>--report-xunit-trx</c> and of VSTest's
/// <c>--logger trx</c>) into a <see cref="TestRunSummary"/>. The report is the source of truth, never the console of the
/// test runner, whose text is translated (docs/investigacion/2026-10-15-spikes-fase-3.md, S4).
/// </summary>
/// <remarks>
/// The reader is streaming and DTD processing is prohibited, so a huge or hostile file costs neither memory nor a
/// resolved external entity. Every <c>UnitTestResult</c> counts once, by its <c>outcome</c>: <c>Passed</c> is a pass;
/// <c>NotExecuted</c> and <c>Inconclusive</c> are skipped; every other outcome, including one this parser does not know,
/// is a failure (fail closed). The <c>Counters</c> summary is not trusted: a report with no results has no passes.
/// A run that executed no test never succeeds, because <see cref="TestRunSummary.Succeeded"/> needs at least one pass.
/// </remarks>
public static class TrxReportParser
{
    public const string Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <param name="report">The TRX document. The stream is not closed.</param>
    /// <param name="exitCode">Exit code of the process that produced the report.</param>
    /// <exception cref="TestReportException">The document is not well-formed XML or not a TRX report.</exception>
    public static TestRunSummary Parse(Stream report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            using var reader = TestReportXml.CreateReader(report);
            return Read(reader, exitCode);
        }
        catch (XmlException ex)
        {
            throw new TestReportException("The TRX report is not valid XML: " + ex.Message, ex);
        }
    }

    /// <exception cref="TestReportException">The file cannot be read, or it is not a valid TRX report.</exception>
    public static TestRunSummary ParseFile(string path, int exitCode)
    {
        using var stream = TestReportXml.OpenFile(path, "TRX");
        return Parse(stream, exitCode);
    }

    private static TestRunSummary Read(XmlReader reader, int exitCode)
    {
        if (!MoveToRoot(reader) || reader.LocalName != "TestRun" || reader.NamespaceURI != Namespace)
        {
            throw new TestReportException("The document is not a TRX report: its root element is not <TestRun> in the Visual Studio TeamTest namespace.");
        }

        int passed = 0, failed = 0, skipped = 0;

        // `Skip` leaves the reader on the next node, so `Read` is only called when nothing was skipped.
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "UnitTestResult" && reader.NamespaceURI == Namespace)
            {
                switch (Classify(reader.GetAttribute("outcome")))
                {
                    case Outcome.Passed:
                        passed++;
                        break;
                    case Outcome.Skipped:
                        skipped++;
                        break;
                    default:
                        failed++;
                        break;
                }

                // Nested results (InnerResults of a data-driven test) belong to this one.
                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }

        return new TestRunSummary(passed, failed, skipped, exitCode);
    }

    private static bool MoveToRoot(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    private enum Outcome
    {
        Passed,
        Skipped,
        Failed,
    }

    private static Outcome Classify(string? outcome) => outcome switch
    {
        "Passed" => Outcome.Passed,
        "NotExecuted" or "Inconclusive" => Outcome.Skipped,
        _ => Outcome.Failed,
    };
}
