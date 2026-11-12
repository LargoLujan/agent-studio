using System.Xml;
using AgentStudio.Application.Verification;
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
    public static TestRunSummary Parse(Stream report, int exitCode) => Parse(report, exitCode, null);

    /// <summary>As <see cref="Parse(Stream, int)"/>, and the failed tests (their names and the first message of each, bounded) are left in <paramref name="failures"/>.</summary>
    internal static TestRunSummary Parse(Stream report, int exitCode, FailureCollector? failures)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            using var reader = TestReportXml.CreateReader(report);
            return Read(reader, exitCode, failures);
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

    /// <summary>Reads a TRX the way <see cref="Parse(Stream, int)"/> does and returns the tests that failed too, at most <see cref="FailureCollector.MaxFailures"/>.</summary>
    /// <exception cref="TestReportException">The document is not well-formed XML or not a TRX report.</exception>
    public static (TestRunSummary Summary, IReadOnlyList<FailedTest> Failed) ParseWithFailures(Stream report, int exitCode)
    {
        var failures = new FailureCollector();
        var summary = Parse(report, exitCode, failures);
        return (summary, failures.Failures);
    }

    private static TestRunSummary Read(XmlReader reader, int exitCode, FailureCollector? failures)
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
                var outcome = Classify(reader.GetAttribute("outcome"));
                switch (outcome)
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

                if (outcome == Outcome.Failed && failures is not null)
                {
                    ReadFailure(reader, failures);
                    continue;
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

    // The reader is on a failed <UnitTestResult>: its name is an attribute; its message is the first <Message> below it (ErrorInfo).
    // It is left past the element, like Skip does.
    private static void ReadFailure(XmlReader reader, FailureCollector failures)
    {
        var name = reader.GetAttribute("testName");
        string? message = null;
        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            var depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (message is null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "Message" && reader.NamespaceURI == Namespace)
                {
                    message = FailureCollector.ReadBoundedText(reader);
                }
                else
                {
                    reader.Read();
                }
            }

            reader.Read();
        }

        failures.Add(name, message);
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
