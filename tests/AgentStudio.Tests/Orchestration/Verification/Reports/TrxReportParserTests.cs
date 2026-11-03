using System.IO;
using System.Text;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification.Reports;
using static AgentStudio.Tests.Orchestration.Verification.Reports.ReportTestSupport;

namespace AgentStudio.Tests.Orchestration.Verification.Reports;

public sealed class TrxReportParserTests
{
    private static string Trx(params string[] outcomes) =>
        $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>"
        + string.Concat(outcomes.Select((outcome, index) => $"<UnitTestResult testName=\"prueba{index}\" outcome=\"{outcome}\" />"))
        + "</Results></TestRun>";

    // ---- real reports of xunit.v3 4.0.2 (see Fixtures/README.md) ---------------------------------------------

    [Fact]
    public void A_real_report_of_this_repository_counts_passed_and_skipped_tests()
    {
        // 21 tests: EvidenceRecordTests plus the two live Codex tests, which skip without AGENTSTUDIO_PROVIDER_TESTS=1.
        var summary = TrxReportParser.ParseFile(Fixture("repo-sample.trx"), exitCode: 0);

        Assert.Equal(new TestRunSummary(Passed: 19, Failed: 0, Skipped: 2, ExitCode: 0), summary);
        Assert.True(summary.Succeeded);
    }

    [Fact]
    public void A_real_report_with_a_failure_counts_it_and_is_not_a_success()
    {
        var summary = TrxReportParser.ParseFile(Fixture("mixed.trx"), exitCode: 2);

        Assert.Equal(new TestRunSummary(Passed: 3, Failed: 1, Skipped: 1, ExitCode: 2), summary);
        Assert.False(summary.Succeeded);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    public void A_real_report_without_tests_is_never_a_success_whatever_the_exit_code(int exitCode)
    {
        var summary = TrxReportParser.ParseFile(Fixture("zero-tests.trx"), exitCode);

        Assert.Equal(new TestRunSummary(0, 0, 0, exitCode), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void A_real_report_where_everything_was_skipped_is_not_a_success_even_with_exit_code_zero()
    {
        // `dotnet test` exits with 0 here: only the report shows that nothing was verified.
        var summary = TrxReportParser.ParseFile(Fixture("skipped-only.trx"), exitCode: 0);

        Assert.Equal(new TestRunSummary(Passed: 0, Failed: 0, Skipped: 1, ExitCode: 0), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void Parse_reads_a_stream_and_leaves_it_open()
    {
        using var stream = File.OpenRead(Fixture("mixed.trx"));

        var summary = TrxReportParser.Parse(stream, exitCode: 2);

        Assert.Equal(3, summary.Passed);
        Assert.True(stream.CanRead, "The parser must not close the caller's stream.");
    }

    // ---- outcomes -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Passed", 1, 0, 0)]
    [InlineData("NotExecuted", 0, 0, 1)]
    [InlineData("Inconclusive", 0, 0, 1)]
    [InlineData("Failed", 0, 1, 0)]
    [InlineData("Error", 0, 1, 0)]
    [InlineData("Timeout", 0, 1, 0)]
    [InlineData("Aborted", 0, 1, 0)]
    [InlineData("Disconnected", 0, 1, 0)]
    [InlineData("NotRunnable", 0, 1, 0)]
    [InlineData("PassedButRunAborted", 0, 1, 0)]
    [InlineData("Warning", 0, 1, 0)]
    [InlineData("Completed", 0, 1, 0)]
    [InlineData("InProgress", 0, 1, 0)]
    [InlineData("Pending", 0, 1, 0)]
    [InlineData("OutcomeFromTheFuture", 0, 1, 0)]
    [InlineData("passed", 0, 1, 0)]
    [InlineData("", 0, 1, 0)]
    public void Only_Passed_is_a_pass_and_an_unknown_outcome_fails_closed(string outcome, int passed, int failed, int skipped)
    {
        using var stream = Utf8(Trx(outcome));

        var summary = TrxReportParser.Parse(stream, exitCode: 0);

        Assert.Equal(new TestRunSummary(passed, failed, skipped, 0), summary);
    }

    [Fact]
    public void A_result_without_an_outcome_attribute_fails_closed()
    {
        using var stream = Utf8($"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results><UnitTestResult testName=\"x\" /></Results></TestRun>");

        Assert.Equal(new TestRunSummary(0, 1, 0, 0), TrxReportParser.Parse(stream, 0));
    }

    [Fact]
    public void The_counters_summary_is_not_trusted()
    {
        using var stream = Utf8(
            $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results><UnitTestResult testName=\"a\" outcome=\"Failed\" /></Results>"
            + "<ResultSummary outcome=\"Completed\"><Counters total=\"50\" executed=\"50\" passed=\"50\" failed=\"0\" /></ResultSummary></TestRun>");

        var summary = TrxReportParser.Parse(stream, exitCode: 0);

        Assert.Equal(new TestRunSummary(Passed: 0, Failed: 1, Skipped: 0, ExitCode: 0), summary);
    }

    [Fact]
    public void A_report_with_counters_but_no_results_has_no_passes()
    {
        using var stream = Utf8(
            $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results /><ResultSummary outcome=\"Completed\"><Counters total=\"10\" passed=\"10\" /></ResultSummary></TestRun>");

        var summary = TrxReportParser.Parse(stream, exitCode: 0);

        Assert.Equal(new TestRunSummary(0, 0, 0, 0), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void Inner_results_of_a_data_driven_test_belong_to_their_parent()
    {
        using var stream = Utf8(
            $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>"
            + "<UnitTestResult testName=\"datos\" outcome=\"Failed\" resultType=\"DataDrivenTest\"><InnerResults>"
            + "<UnitTestResult testName=\"fila1\" outcome=\"Passed\" /><UnitTestResult testName=\"fila2\" outcome=\"Failed\" />"
            + "</InnerResults></UnitTestResult>"
            + "<UnitTestResult testName=\"otra\" outcome=\"Passed\" />"
            + "</Results></TestRun>");

        var summary = TrxReportParser.Parse(stream, exitCode: 1);

        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 1, Skipped: 0, ExitCode: 1), summary);
    }

    [Fact]
    public void Output_of_a_test_with_markup_like_text_is_not_confused_with_results()
    {
        using var stream = Utf8(
            $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>"
            + "<UnitTestResult testName=\"a\" outcome=\"Passed\"><Output><StdOut>&lt;UnitTestResult outcome=\"Failed\" /&gt;</StdOut></Output></UnitTestResult>"
            + "</Results></TestRun>");

        Assert.Equal(new TestRunSummary(1, 0, 0, 0), TrxReportParser.Parse(stream, 0));
    }

    // ---- hostile and broken input -----------------------------------------------------------------------------

    [Fact]
    public void A_document_type_declaration_is_rejected_and_nothing_is_resolved()
    {
        // An XXE payload: the entity points at a file that exists on every Windows machine.
        const string xxe =
            "<?xml version=\"1.0\"?><!DOCTYPE TestRun [<!ENTITY secreto SYSTEM \"file:///C:/Windows/win.ini\">]>"
            + $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results><UnitTestResult testName=\"&secreto;\" outcome=\"Passed\" /></Results></TestRun>";
        using var stream = Utf8(xxe);

        var failure = Assert.Throws<TestReportException>(() => TrxReportParser.Parse(stream, 0));

        // The reader stops at the DOCTYPE itself, not later at an entity it could not resolve. (The text of the
        // message is translated, but the acronym is not.)
        var cause = Assert.IsAssignableFrom<System.Xml.XmlException>(failure.InnerException);
        Assert.Contains("DTD", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_expansion_bomb_is_rejected_without_expanding_it()
    {
        const string bomb =
            "<?xml version=\"1.0\"?><!DOCTYPE TestRun [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
            + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>"
            + $"<TestRun xmlns=\"{TrxReportParser.Namespace}\">&d;</TestRun>";
        using var stream = Utf8(bomb);

        var failure = Assert.Throws<TestReportException>(() => TrxReportParser.Parse(stream, 0));

        Assert.Contains("DTD", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Even_a_harmless_document_type_declaration_is_rejected()
    {
        using var stream = Utf8($"<!DOCTYPE TestRun><TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results /></TestRun>");

        var failure = Assert.Throws<TestReportException>(() => TrxReportParser.Parse(stream, 0));

        Assert.Contains("DTD", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_report_is_an_error_not_partial_counts()
    {
        var whole = File.ReadAllText(Fixture("mixed.trx"));
        using var truncated = Utf8(whole[..(whole.Length / 2)]);

        Assert.Throws<TestReportException>(() => TrxReportParser.Parse(truncated, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es xml")]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results></TestRun>")]
    public void Malformed_documents_are_errors(string document)
    {
        using var stream = Utf8(document);

        Assert.Throws<TestReportException>(() => TrxReportParser.Parse(stream, 0));
    }

    [Theory]
    [InlineData("<testsuites><testsuite tests=\"1\"><testcase name=\"a\" /></testsuite></testsuites>")]
    [InlineData("<TestRun><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>")]
    [InlineData("<TestRun xmlns=\"urn:otro\"><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>")]
    [InlineData("<Otra xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\" />")]
    public void Documents_that_are_not_TRX_are_errors(string document)
    {
        using var stream = Utf8(document);

        var failure = Assert.Throws<TestReportException>(() => TrxReportParser.Parse(stream, 0));

        Assert.Contains("TRX", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_unreadable_file_is_an_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".trx");

        Assert.Throws<TestReportException>(() => TrxReportParser.ParseFile(missing, 0));
        Assert.Throws<TestReportException>(() => TrxReportParser.ParseFile(Path.GetTempPath(), 0));
        Assert.ThrowsAny<ArgumentException>(() => TrxReportParser.ParseFile(" ", 0));
    }

    [Fact]
    public void A_file_that_another_process_still_has_open_can_be_read()
    {
        var path = Path.Combine(Path.GetTempPath(), "as-trx-" + Guid.NewGuid().ToString("N") + ".trx");
        try
        {
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(Trx("Passed", "Passed", "Failed"));
            writer.Write(bytes);
            writer.Flush();

            Assert.Equal(new TestRunSummary(2, 1, 0, 1), TrxReportParser.ParseFile(path, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- size ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_very_large_report_is_read_as_a_stream_with_exact_counts()
    {
        // 400,000 results (about 100 MB of XML) produced on the fly by a stream that cannot seek: only a streaming
        // parser can read this without holding the document.
        const int total = 400_000;
        using var stream = new GeneratedStream(Generate(total));

        var summary = TrxReportParser.Parse(stream, exitCode: 1);

        var failed = Enumerable.Range(0, total).Count(i => i % 1000 == 0);
        var skipped = Enumerable.Range(0, total).Count(i => i % 1000 != 0 && i % 7 == 0);
        Assert.Equal(new TestRunSummary(total - failed - skipped, failed, skipped, 1), summary);
    }

    [Fact]
    public void A_result_with_a_huge_captured_output_is_skipped_without_trouble()
    {
        using var stream = new GeneratedStream(HugeOutput());

        var summary = TrxReportParser.Parse(stream, exitCode: 0);

        Assert.Equal(new TestRunSummary(2, 0, 0, 0), summary);

        static IEnumerable<string> HugeOutput()
        {
            yield return $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results><UnitTestResult testName=\"ruidosa\" outcome=\"Passed\"><Output><StdOut>";
            var line = new string('x', 1000) + "\n";
            for (var i = 0; i < 50_000; i++)
            {
                yield return line;
            }

            yield return "</StdOut></Output></UnitTestResult><UnitTestResult testName=\"otra\" outcome=\"Passed\" /></Results></TestRun>";
        }
    }

    private static IEnumerable<string> Generate(int total)
    {
        yield return $"<TestRun id=\"x\" xmlns=\"{TrxReportParser.Namespace}\"><Results>";
        for (var i = 0; i < total; i++)
        {
            var outcome = i % 1000 == 0 ? "Failed" : i % 7 == 0 ? "NotExecuted" : "Passed";
            yield return $"<UnitTestResult testName=\"AgentStudio.Tests.Generadas.Clase{i / 100}.Prueba{i}\" outcome=\"{outcome}\" "
                + $"testType=\"13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b\" testId=\"{i}\" executionId=\"{i}\" computerName=\"EQUIPO\" duration=\"00:00:00.0010000\" />";
        }

        yield return "</Results></TestRun>";
    }
}
