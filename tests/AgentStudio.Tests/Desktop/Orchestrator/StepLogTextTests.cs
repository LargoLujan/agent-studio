using System.Globalization;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Metrics;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// B7 and V18: the main line of the error of a verification step, read from the log the runner writes (its header and footer
/// lines, and each line of the process with its stream), the steps a failure names, and the windows of the quota named in
/// Spanish in the activity of the orchestrator.
/// </summary>
public sealed class StepLogTextTests
{
    private static string Log(params string[] lines) => string.Join("\n", lines);

    private static readonly string[] Header =
    [
        "# Agent Studio · verification step 'test' (Pruebas)",
        "# command: dotnet test --report-trx --results-directory {results}",
        "# isolation: low integrity (S-1-16-4096)",
        "# working directory: C:\\wt\\T1",
    ];

    [Fact]
    public void A_line_that_reads_as_an_error_is_the_key_and_a_count_of_none_is_not()
    {
        var log = Log([
            .. Header,
            "stdout| Restore complete (0,6s)",
            "stdout|     0 Warning(s)",
            "stdout|     0 Error(s)",
            "stdout| Test run summary: failed: 0",
            "stdout| No test projects were found in the solution.",
            "# exit code: 1",
            "# result: failed",
        ]);

        Assert.Equal("No test projects were found in the solution.", StepLogText.KeyLine(log));
    }

    [Fact]
    public void An_error_on_the_error_stream_wins_and_the_project_of_an_msbuild_line_is_left_out()
    {
        var log = Log([
            .. Header,
            "stdout| src\\Calc\\Calc.cs(12,5): error CS1002: ; expected [C:\\wt\\T1\\src\\Calc\\Calc.csproj]",
            "stderr| \u001b[31mC:\\wt\\T1\\src\\Calc\\Calc.cs(12,5): error CS1002: ; expected [C:\\wt\\T1\\src\\Calc\\Calc.csproj]\u001b[0m",
            "# exit code: 1",
        ]);

        Assert.Equal(@"C:\wt\T1\src\Calc\Calc.cs(12,5): error CS1002: ; expected", StepLogText.KeyLine(log));
    }

    [Fact]
    public void A_step_that_could_not_start_says_why_first()
    {
        var log = Log([.. Header, "# start problem: The low integrity work area could not be created.", "# exit code: -1"]);

        Assert.Equal("The low integrity work area could not be created.", StepLogText.KeyLine(log));
    }

    [Fact]
    public void Without_an_error_line_the_report_problem_and_then_the_first_error_stream_line_say_it()
    {
        Assert.Equal(
            "the TRX report was not written",
            StepLogText.KeyLine(Log([.. Header, "stdout| Hecho.", "# report problem: the TRX report was not written", "# exit code: 0"])));
        Assert.Equal("aviso extraño", StepLogText.KeyLine(Log([.. Header, "stderr| aviso extraño", "# exit code: 3"])));
        Assert.Null(StepLogText.KeyLine(Log([.. Header, "stdout| Todo bien.", "# exit code: 0"])));
        Assert.Null(StepLogText.KeyLine(null));
    }

    [Fact]
    public void A_long_line_is_cut_with_an_ellipsis()
    {
        var line = StepLogText.KeyLine(Log("stderr| error: " + new string('x', 400)));

        Assert.Equal(StepLogText.MaxLength, line!.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("La verificación falló en: test", new[] { "test" })]
    [InlineData("La verificación falló en: build, test", new[] { "build", "test" })]
    [InlineData("El paso lint falló.", new[] { "lint" })]
    [InlineData("El agente declaró que no pudo terminar: no sé.", new string[0])]
    public void The_steps_a_failure_names_are_read_from_its_reason(string reason, string[] steps)
    {
        Assert.Equal(steps, StepLogText.FailedSteps(reason));
    }

    [Fact]
    public void The_windows_of_the_quota_are_named_as_the_tray_names_them()
    {
        var quota = new QuotaReported(T0,
        [
            new QuotaWindow("five_hour", MetricValue<double>.Exact(0.02, "claude rate_limit_event"), null),
            new QuotaWindow("seven_day", MetricValue<double>.Exact(0.61, "claude rate_limit_event"), null),
            new QuotaWindow("primary", MetricValue<double>.Exact(0.5, "codex"), null),
        ]);

        static string Percent(double value) => value.ToString("P0", CultureInfo.GetCultureInfo("es-ES"));
        Assert.Equal($"Límite de uso: 5 horas {Percent(0.02)}, 7 días {Percent(0.61)}, primary {Percent(0.5)}", AgentActivityText.Describe(quota, TimeZoneInfo.Utc));
        Assert.Equal("7 días · Opus", QuotaWindowText.Name("seven_day_opus"));
    }
}
