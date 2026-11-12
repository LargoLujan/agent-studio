using System.IO;
using System.Text;
using AgentStudio.Orchestration.Verification.Reports;

namespace AgentStudio.Tests.Orchestration.Verification.Reports;

/// <summary>The names of the failed tests, read from the TRX and JUnit reports of the isolated step (P50). A report is untrusted data.</summary>
public sealed class FailedTestsReportTests
{
    private static MemoryStream Stream(string xml) => new(Encoding.UTF8.GetBytes(xml));

    private static string Trx(string results) => $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>{results}</Results></TestRun>";

    private static string FailedResult(string name, string? message) =>
        $"<UnitTestResult testName=\"{name}\" outcome=\"Failed\"><Output><ErrorInfo>{(message is null ? string.Empty : $"<Message>{message}</Message>")}<StackTrace>en X.Y() linea 1</StackTrace></ErrorInfo></Output></UnitTestResult>";

    [Fact]
    public void A_TRX_names_its_failed_tests_with_the_first_message_and_never_the_stack()
    {
        var xml = Trx("<UnitTestResult testName=\"Bien\" outcome=\"Passed\" />" + FailedResult("Mal", "esperado 1, fue 2") + "<UnitTestResult testName=\"Salta\" outcome=\"NotExecuted\" />");

        var (summary, failed) = TrxReportParser.ParseWithFailures(Stream(xml), exitCode: 1);

        Assert.Equal(new AgentStudio.Domain.Evidence.TestRunSummary(1, 1, 1, 1), summary);
        var test = Assert.Single(failed);
        Assert.Equal("Mal", test.Name);
        Assert.Equal("esperado 1, fue 2", test.Message);
    }

    [Fact]
    public void A_failed_TRX_result_without_a_message_still_names_the_test()
    {
        var (_, failed) = TrxReportParser.ParseWithFailures(Stream(Trx(FailedResult("SinMensaje", null) + "<UnitTestResult testName=\"Otra\" outcome=\"Error\" />")), exitCode: 1);

        Assert.Equal(["SinMensaje", "Otra"], failed.Select(f => f.Name));
        Assert.All(failed, f => Assert.Null(f.Message));
    }

    [Fact]
    public void The_counters_do_not_change_when_the_failures_are_read()
    {
        var xml = Trx(FailedResult("A", "x") + "<UnitTestResult testName=\"B\" outcome=\"Passed\" />" + FailedResult("C", "y"));

        var plain = TrxReportParser.Parse(Stream(xml), 1);
        var (withNames, _) = TrxReportParser.ParseWithFailures(Stream(xml), 1);

        Assert.Equal(plain, withNames);
    }

    [Fact]
    public void A_huge_message_and_a_huge_name_are_cut_to_one_bounded_line()
    {
        var message = string.Concat(Enumerable.Repeat("linea larga\n", 20_000));
        var xml = Trx(FailedResult(new string('n', 5_000), message));

        var (_, failed) = TrxReportParser.ParseWithFailures(Stream(xml), 1);

        var test = Assert.Single(failed);
        Assert.True(test.Name.Length <= FailureCollectorLimits.Name);
        Assert.True(test.Message!.Length <= FailureCollectorLimits.Message);
        Assert.DoesNotContain('\n', test.Message);
        Assert.EndsWith("…", test.Message);
    }

    [Fact]
    public void Only_the_first_failures_are_listed_while_every_one_is_counted()
    {
        var xml = Trx(string.Concat(Enumerable.Range(0, 450).Select(i => FailedResult("t" + i, "m"))));

        var (summary, failed) = TrxReportParser.ParseWithFailures(Stream(xml), 1);

        Assert.Equal(450, summary.Failed);
        Assert.Equal(FailureCollectorLimits.Failures, failed.Count);
        Assert.Equal("t0", failed[0].Name);
    }

    [Fact]
    public void A_report_with_a_DTD_is_refused_so_no_entity_reaches_a_name_or_a_message()
    {
        const string hostile = "<!DOCTYPE TestRun [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><TestRun xmlns=\"" + TrxReportParser.Namespace + "\"><Results><UnitTestResult testName=\"&x;\" outcome=\"Failed\" /></Results></TestRun>";

        Assert.Throws<TestReportException>(() => TrxReportParser.ParseWithFailures(Stream(hostile), 1));
    }

    [Fact]
    public void A_JUnit_names_its_failed_tests_with_the_class_and_the_message_of_the_attribute()
    {
        const string xml = "<testsuites><testsuite><testcase classname=\"Calc\" name=\"Suma\" /><testcase classname=\"Calc\" name=\"Resta\"><failure message=\"esperado 0\" type=\"AssertionError\">traza larga</failure></testcase></testsuite></testsuites>";

        var (summary, failed) = JUnitReportParser.ParseWithFailures(Stream(xml), 1);

        Assert.Equal(1, summary.Failed);
        var test = Assert.Single(failed);
        Assert.Equal("Calc.Resta", test.Name);
        Assert.Equal("esperado 0", test.Message);
    }

    [Fact]
    public void A_JUnit_failure_without_an_attribute_message_gives_the_beginning_of_its_text_and_an_error_counts_too()
    {
        const string xml = "<testsuite><testcase name=\"a\"><error>fallo\ninterno</error></testcase><testcase name=\"b\"><failure/></testcase><testcase name=\"c\"><skipped/></testcase></testsuite>";

        var (summary, failed) = JUnitReportParser.ParseWithFailures(Stream(xml), 1);

        Assert.Equal(2, summary.Failed);
        Assert.Equal(["a", "b"], failed.Select(f => f.Name));
        Assert.Equal("fallo interno", failed[0].Message);
        Assert.Null(failed[1].Message);
    }

    [Fact]
    public void A_JUnit_with_a_DTD_is_refused()
    {
        const string hostile = "<!DOCTYPE testsuite [<!ENTITY x \"boom\">]><testsuite><testcase name=\"&x;\"><failure/></testcase></testsuite>";

        Assert.Throws<TestReportException>(() => JUnitReportParser.ParseWithFailures(Stream(hostile), 1));
    }

    // The limits of the collector are internal: the tests say them once.
    private static class FailureCollectorLimits
    {
        public const int Failures = 200;
        public const int Name = 300;
        public const int Message = 400;
    }
}
