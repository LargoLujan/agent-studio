using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Panels;

namespace AgentStudio.Tests.Desktop.Panels;

/// <summary>The panel «Pruebas» shows the failed tests the reports name, says what it did not run or list, and says in words why it could not run (P50).</summary>
public sealed class TestsPanelFailuresTests
{
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es"));

    private static TestRunRow Line(bool failed) => new(null, "Pruebas (test)", "3 passed", failed, failed ? "failed" : "passed", null, "abc");

    private sealed class Scripted(HeadTestsResult? result, Exception? failure = null) : IHeadTestsService
    {
        public Task<HeadTestsResult> RunAsync(CancellationToken cancellationToken) =>
            failure is not null ? Task.FromException<HeadTestsResult>(failure) : Task.FromResult(result!);
    }

    [Fact]
    public async Task The_failed_tests_of_the_report_are_listed_with_their_messages_and_a_summary_of_the_failed_steps()
    {
        var failed = Enumerable.Range(0, 300).Select(i => new FailedTestRow("test", "Calc.Prueba" + i, i % 2 == 0 ? "esperado " + i : null)).ToList();
        var panel = new TestsPanelViewModel(English) { HeadTests = new Scripted(new HeadTestsResult([Line(failed: true)], failed, null, 0)) };

        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal(300, panel.HeadFailures.Count);
        Assert.Equal("Calc.Prueba0 · test · esperado 0", panel.HeadFailures[0].AccessibleName);
        Assert.Equal("Calc.Prueba1 · test", panel.HeadFailures[1].AccessibleName);
        Assert.Equal("1 steps of the tests of the latest version fail.", panel.HeadSummary);
        Assert.Empty(panel.HeadNotes);
    }

    [Fact]
    public async Task The_panel_says_what_it_did_not_run_and_how_many_failed_tests_it_does_not_list()
    {
        var result = new HeadTestsResult([Line(failed: true)], [new FailedTestRow("test", "Calc.Resta", null)], ["deploy", "publish"], 7);
        var panel = new TestsPanelViewModel(English) { HeadTests = new Scripted(result) };

        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Not run, because the profile would pass them without isolation: deploy, publish.", "7 more tests fail and are not shown: the list is cut at the first ones."],
            panel.HeadNotes);
    }

    [Fact]
    public async Task A_new_run_replaces_the_failures_of_the_last_one()
    {
        var panel = new TestsPanelViewModel(English) { HeadTests = new Scripted(new HeadTestsResult([Line(failed: true)], [new FailedTestRow("test", "A", null)], ["x"], 1)) };
        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        panel.HeadTests = new Scripted(new HeadTestsResult([Line(failed: false)]));
        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Empty(panel.HeadFailures);
        Assert.Empty(panel.HeadNotes);
        Assert.Equal("The tests of the latest version pass.", panel.HeadSummary);
    }

    [Theory]
    [InlineData("Panels_Tests_HeadBusy", "No se pudieron ejecutar las pruebas de la última versión: el proyecto está ocupado con una ejecución; espera a que termine.")]
    [InlineData("Panels_Tests_HeadNoProfile", "No se pudieron ejecutar las pruebas de la última versión: el proyecto no tiene perfil de verificación.")]
    [InlineData("Panels_Tests_HeadNoIsolatedStep", "No se pudieron ejecutar las pruebas de la última versión: ningún paso del perfil se ejecuta aislado, y aquí nada se ejecuta con tus permisos.")]
    public async Task A_refusal_of_the_service_is_said_in_the_language_of_the_person_not_in_the_english_of_the_log(string key, string expected)
    {
        var arguments = key == "Panels_Tests_HeadBusy" ? new object[] { "una ejecución" } : [];
        var panel = new TestsPanelViewModel(Spanish) { HeadTests = new Scripted(null, new HeadTestsRefusedException(key, "English for the log.", arguments)) };

        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal(expected, panel.ErrorMessage);
        Assert.False(panel.IsRunning);
    }

    [Fact]
    public async Task Any_other_failure_of_the_service_is_said_in_the_panel_and_never_thrown()
    {
        var panel = new TestsPanelViewModel(English) { HeadTests = new Scripted(null, new InvalidOperationException("sin copia aislada")) };

        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal("The tests of the latest version could not be run: sin copia aislada", panel.ErrorMessage);
    }

    [Fact]
    public void The_failed_tests_of_the_panel_do_not_use_the_word_evidence_for_what_the_person_reads()
    {
        // ADR-0054 section 9: «prueba guardada», not «evidencia».
        foreach (var key in new[] { "Panels_Tests_HeadFailuresTitle", "Panels_Tests_HeadNotRun", "Panels_Tests_HeadMore", "Panels_Tests_HeadBusy", "Panels_Tests_HeadNoProfile", "Panels_Tests_HeadNoIsolatedStep", "Panels_Tests_HeadTimeBudget", "Panels_Tests_HeadNoProject" })
        {
            Assert.DoesNotContain("evidencia", Spanish.Get(key), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("evidence", English.Get(key), StringComparison.OrdinalIgnoreCase);
        }
    }
}
