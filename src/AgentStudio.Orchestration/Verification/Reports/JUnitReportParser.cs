using System.Xml;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification.Reports;

/// <summary>
/// Reads a JUnit XML report (the format of <c>--report-xunit-junit</c>, Ant, Maven Surefire, pytest...) into a
/// <see cref="TestRunSummary"/>.
/// </summary>
/// <remarks>
/// The reader is streaming and DTD processing is prohibited. Every <c>testcase</c> counts once, whatever the nesting of
/// the <c>testsuite</c> elements: a <c>failure</c> or <c>error</c> child makes it a failure, a <c>skipped</c> child makes
/// it skipped, otherwise it passed. The <c>tests</c>/<c>failures</c> attributes of the suites are not trusted, so a report
/// without test cases has no passes. A run that executed no test never succeeds, because
/// <see cref="TestRunSummary.Succeeded"/> needs at least one pass.
/// </remarks>
public static class JUnitReportParser
{
    /// <param name="report">The JUnit document. The stream is not closed.</param>
    /// <param name="exitCode">Exit code of the process that produced the report.</param>
    /// <exception cref="TestReportException">The document is not well-formed XML or not a JUnit report.</exception>
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
            throw new TestReportException("The JUnit report is not valid XML: " + ex.Message, ex);
        }
    }

    /// <exception cref="TestReportException">The file cannot be read, or it is not a valid JUnit report.</exception>
    public static TestRunSummary ParseFile(string path, int exitCode)
    {
        using var stream = TestReportXml.OpenFile(path, "JUnit");
        return Parse(stream, exitCode);
    }

    private static TestRunSummary Read(XmlReader reader, int exitCode)
    {
        if (!MoveToRoot(reader) || reader.LocalName is not ("testsuites" or "testsuite"))
        {
            throw new TestReportException("The document is not a JUnit report: its root element is neither <testsuites> nor <testsuite>.");
        }

        int passed = 0, failed = 0, skipped = 0;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "testcase")
            {
                switch (ReadTestCase(reader))
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
            }
            else
            {
                reader.Read();
            }
        }

        return new TestRunSummary(passed, failed, skipped, exitCode);
    }

    // Consumes the whole <testcase> element (its captured output can be large) and looks only at its direct children.
    private static Outcome ReadTestCase(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return Outcome.Passed;
        }

        var depth = reader.Depth;
        var outcome = Outcome.Passed;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                switch (reader.LocalName)
                {
                    case "failure" or "error":
                        outcome = Outcome.Failed;
                        break;
                    case "skipped" when outcome == Outcome.Passed:
                        outcome = Outcome.Skipped;
                        break;
                }

                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }

        // Past </testcase>.
        reader.Read();
        return outcome;
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
}
