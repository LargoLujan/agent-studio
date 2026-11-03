using System.IO;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification.Reports;
using static AgentStudio.Tests.Orchestration.Verification.Reports.ReportTestSupport;

namespace AgentStudio.Tests.Orchestration.Verification.Reports;

public sealed class JUnitReportParserTests
{
    // ---- real reports of xunit.v3 4.0.2 (see Fixtures/README.md) ---------------------------------------------

    [Fact]
    public void A_real_report_of_this_repository_counts_passed_and_skipped_tests()
    {
        var summary = JUnitReportParser.ParseFile(Fixture("repo-sample.junit.xml"), exitCode: 0);

        Assert.Equal(new TestRunSummary(Passed: 19, Failed: 0, Skipped: 2, ExitCode: 0), summary);
        Assert.True(summary.Succeeded);
    }

    [Fact]
    public void A_real_report_with_a_failure_counts_it_and_is_not_a_success()
    {
        var summary = JUnitReportParser.ParseFile(Fixture("mixed.junit.xml"), exitCode: 2);

        Assert.Equal(new TestRunSummary(Passed: 3, Failed: 1, Skipped: 1, ExitCode: 2), summary);
        Assert.False(summary.Succeeded);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    public void A_real_report_without_tests_is_never_a_success_whatever_the_exit_code(int exitCode)
    {
        var summary = JUnitReportParser.ParseFile(Fixture("zero-tests.junit.xml"), exitCode);

        Assert.Equal(new TestRunSummary(0, 0, 0, exitCode), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void A_real_report_where_everything_was_skipped_is_not_a_success_even_with_exit_code_zero()
    {
        var summary = JUnitReportParser.ParseFile(Fixture("skipped-only.junit.xml"), exitCode: 0);

        Assert.Equal(new TestRunSummary(Passed: 0, Failed: 0, Skipped: 1, ExitCode: 0), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void Parse_reads_a_stream_and_leaves_it_open()
    {
        using var stream = File.OpenRead(Fixture("mixed.junit.xml"));

        var summary = JUnitReportParser.Parse(stream, exitCode: 2);

        Assert.Equal(3, summary.Passed);
        Assert.True(stream.CanRead, "The parser must not close the caller's stream.");
    }

    // ---- other JUnit producers ----------------------------------------------------------------------------

    [Fact]
    public void A_surefire_style_report_with_nested_suites_errors_and_captured_output_is_counted_case_by_case()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuites name="build" tests="7" failures="1" errors="1" skipped="1">
              <testsuite name="com.acme.CalcTest" tests="4" failures="1" errors="0" skipped="1">
                <properties><property name="java.version" value="21" /></properties>
                <testcase classname="com.acme.CalcTest" name="adds" time="0.01" />
                <testcase classname="com.acme.CalcTest" name="subtracts" time="0.02">
                  <failure message="expected 1 but was 2" type="AssertionError">stack trace</failure>
                  <system-out>salida capturada con &lt;failure /&gt; dentro</system-out>
                </testcase>
                <testcase classname="com.acme.CalcTest" name="ignored" time="0"><skipped message="not ready" /></testcase>
                <testcase classname="com.acme.CalcTest" name="multiplies" time="0.01"><system-err>aviso</system-err></testcase>
              </testsuite>
              <testsuite name="com.acme.DivTest" tests="3" failures="0" errors="1" skipped="0">
                <testsuite name="com.acme.DivTest.Nested" tests="2">
                  <testcase classname="com.acme.DivTest$Nested" name="divides" time="0.01" />
                  <testcase classname="com.acme.DivTest$Nested" name="by zero" time="0.01"><error message="boom" type="ArithmeticException">trace</error></testcase>
                </testsuite>
                <testcase classname="com.acme.DivTest" name="remainder" time="0.01" />
              </testsuite>
            </testsuites>
            """;
        using var stream = Utf8(xml);

        var summary = JUnitReportParser.Parse(stream, exitCode: 1);

        Assert.Equal(new TestRunSummary(Passed: 4, Failed: 2, Skipped: 1, ExitCode: 1), summary);
    }

    [Fact]
    public void A_single_testsuite_without_testsuites_is_accepted()
    {
        using var stream = Utf8("""<testsuite name="s" tests="2"><testcase name="a" /><testcase name="b"><failure message="x" /></testcase></testsuite>""");

        Assert.Equal(new TestRunSummary(1, 1, 0, 1), JUnitReportParser.Parse(stream, 1));
    }

    [Fact]
    public void A_case_with_both_a_failure_and_a_skip_counts_as_a_failure()
    {
        using var stream = Utf8("""<testsuite><testcase name="a"><skipped /><failure message="x" /></testcase><testcase name="b"><failure message="x" /><skipped /></testcase></testsuite>""");

        Assert.Equal(new TestRunSummary(0, 2, 0, 0), JUnitReportParser.Parse(stream, 0));
    }

    [Fact]
    public void A_failure_nested_deeper_than_a_direct_child_does_not_count()
    {
        using var stream = Utf8("""<testsuite><testcase name="a"><system-out><failure /></system-out><rerunFailure message="x" /></testcase></testsuite>""");

        Assert.Equal(new TestRunSummary(1, 0, 0, 0), JUnitReportParser.Parse(stream, 0));
    }

    [Fact]
    public void The_attributes_of_the_suites_are_not_trusted()
    {
        using var stream = Utf8("""<testsuites tests="99" failures="0" errors="0"><testsuite tests="99" failures="0"><testcase name="a"><failure message="x" /></testcase></testsuite></testsuites>""");

        Assert.Equal(new TestRunSummary(Passed: 0, Failed: 1, Skipped: 0, ExitCode: 0), JUnitReportParser.Parse(stream, 0));
    }

    [Fact]
    public void A_report_with_suite_counts_but_no_test_cases_has_no_passes()
    {
        using var stream = Utf8("""<testsuites tests="10" failures="0"><testsuite name="s" tests="10" failures="0" skipped="0" /></testsuites>""");

        var summary = JUnitReportParser.Parse(stream, 0);

        Assert.Equal(new TestRunSummary(0, 0, 0, 0), summary);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public void An_empty_root_has_no_tests()
    {
        using var stream = Utf8("<testsuites />");

        Assert.Equal(new TestRunSummary(0, 0, 0, 0), JUnitReportParser.Parse(stream, 0));
    }

    // ---- hostile and broken input -----------------------------------------------------------------------------

    [Fact]
    public void A_document_type_declaration_is_rejected_and_nothing_is_resolved()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE testsuites [<!ENTITY secreto SYSTEM "file:///C:/Windows/win.ini">]>
            <testsuites><testsuite><testcase name="&secreto;" /></testsuite></testsuites>
            """;
        using var stream = Utf8(xxe);

        var failure = Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(stream, 0));

        // The reader stops at the DOCTYPE itself, not later at an entity it could not resolve. (The text of the
        // message is translated, but the acronym is not.)
        var cause = Assert.IsAssignableFrom<System.Xml.XmlException>(failure.InnerException);
        Assert.Contains("DTD", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Even_a_harmless_document_type_declaration_is_rejected()
    {
        using var stream = Utf8("<!DOCTYPE testsuites><testsuites><testsuite><testcase name=\"a\" /></testsuite></testsuites>");

        var failure = Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(stream, 0));

        Assert.Contains("DTD", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_expansion_bomb_is_rejected_without_expanding_it()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE testsuites [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <testsuites>&c;</testsuites>
            """;
        using var stream = Utf8(bomb);

        var failure = Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(stream, 0));

        Assert.Contains("DTD", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_report_is_an_error_not_partial_counts()
    {
        var whole = File.ReadAllText(Fixture("mixed.junit.xml"));
        using var truncated = Utf8(whole[..(whole.Length * 2 / 3)]);

        Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(truncated, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es xml")]
    [InlineData("<testsuites><testsuite></testsuites>")]
    public void Malformed_documents_are_errors(string document)
    {
        using var stream = Utf8(document);

        Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(stream, 0));
    }

    [Theory]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results /></TestRun>")]
    [InlineData("<html><body><testcase /></body></html>")]
    public void Documents_that_are_not_JUnit_are_errors(string document)
    {
        using var stream = Utf8(document);

        var failure = Assert.Throws<TestReportException>(() => JUnitReportParser.Parse(stream, 0));

        Assert.Contains("JUnit", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_unreadable_file_is_an_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".xml");

        Assert.Throws<TestReportException>(() => JUnitReportParser.ParseFile(missing, 0));
        Assert.Throws<TestReportException>(() => JUnitReportParser.ParseFile(Path.GetTempPath(), 0));
        Assert.ThrowsAny<ArgumentException>(() => JUnitReportParser.ParseFile("", 0));
    }

    // ---- size ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_very_large_report_is_read_as_a_stream_with_exact_counts()
    {
        const int total = 400_000;
        using var stream = new GeneratedStream(Generate(total));

        var summary = JUnitReportParser.Parse(stream, exitCode: 1);

        var failed = Enumerable.Range(0, total).Count(i => i % 1000 == 0);
        var skipped = Enumerable.Range(0, total).Count(i => i % 1000 != 0 && i % 7 == 0);
        Assert.Equal(new TestRunSummary(total - failed - skipped, failed, skipped, 1), summary);
    }

    [Fact]
    public void A_case_with_a_huge_captured_output_is_skipped_without_trouble()
    {
        using var stream = new GeneratedStream(HugeOutput());

        Assert.Equal(new TestRunSummary(2, 0, 0, 0), JUnitReportParser.Parse(stream, 0));

        static IEnumerable<string> HugeOutput()
        {
            yield return "<testsuites><testsuite><testcase name=\"ruidosa\"><system-out>";
            var line = new string('x', 1000) + "\n";
            for (var i = 0; i < 50_000; i++)
            {
                yield return line;
            }

            yield return "</system-out></testcase><testcase name=\"otra\" /></testsuite></testsuites>";
        }
    }

    private static IEnumerable<string> Generate(int total)
    {
        yield return "<testsuites><testsuite name=\"generada\">";
        for (var i = 0; i < total; i++)
        {
            var head = $"<testcase name=\"AgentStudio.Tests.Generadas.Clase{i / 100}.Prueba{i}\" classname=\"AgentStudio.Tests.Generadas.Clase{i / 100}\" time=\"0.001000\"";
            yield return i % 1000 == 0
                ? head + "><failure message=\"fallo\">traza</failure></testcase>"
                : i % 7 == 0
                    ? head + "><skipped>omitida</skipped></testcase>"
                    : head + " />";
        }

        yield return "</testsuite></testsuites>";
    }
}
