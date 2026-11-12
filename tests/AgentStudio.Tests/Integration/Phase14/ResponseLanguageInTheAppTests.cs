using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Prompts;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Planning;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase14;

/// <summary>
/// F14-2 and F14-4 (docs/17 D-48 and D-49, ADR-0054; §9.4 «Integración») in the app as it composes itself: with the interface in English, a
/// scripted agent receives <c>&lt;response_language&gt;"en"&lt;/response_language&gt;</c> as data, whoever asks (the executor of a run, the planner),
/// and the prompt is the same bytes under two cultures of the thread: the language is the one of the interface, never the one of the thread
/// that asks. The language of the app is applied once at startup, so the test paints with a scoped provider (<see cref="Texts.Scope"/>), as
/// the renders per language do.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed partial class ResponseLanguageInTheAppTests
{
    [GeneratedRegex(@"\d{8}-[0-9a-f]{6}")]
    private static partial Regex RunIds();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Normalized(string prompt) => RunIds().Replace(prompt, "RUN");

    private static IDisposable English() => Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo("en")));

    /// <summary>The app with a scripted planner: it answers a plan of one task, and any other agent writes its file.</summary>
    private static Task<AppWorld> OpenAsync()
    {
        var clis = new Phase5Clis();
        var executor = Phase9Runs.Executor;
        clis.Executor = (request, attempt) => request.Agent.Role == AgentRole.Orchestrator
            ? new AgentScript { StructuredOutput = PlanJson.Plan(PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"])).ToJsonString() }
            : executor(request, attempt);
        return AppWorld.OpenAsync(clis: clis);
    }

    private static async Task<string> PlannerPromptAsync(AppWorld world, string threadCulture)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var before = world.Clis.Launches.Count;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(threadCulture);
            var roster = await world.Get<AgentStudio.Application.Persistence.IAgentRoster>().ListAsync(world.App.Project.ProjectId, Ct);
            var request = new PlanningRequest(world.App.Project, "Añade Calc.Multiply(a,b) con sus pruebas", Requirements(), roster, new AgentId("orquestador"), Phase5Runs.Profile(), new PlanLimits());
            await world.App.Orchestrator.PlanAsync(request, null, Ct);
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = ui;
        }

        return Normalized(world.Clis.Launches[before].Prompt);
    }

    [Fact]
    public async Task With_the_interface_in_English_the_planner_and_the_executor_of_a_run_receive_the_language_as_data()
    {
        using var scope = English();
        await using var world = await OpenAsync();
        var block = $"<response_language>\"en\"</response_language>";

        var planner = await PlannerPromptAsync(world, "es-ES");
        Assert.Contains(block, planner, StringComparison.Ordinal);
        Assert.Contains(ResponseLanguage.Rule, planner, StringComparison.Ordinal);

        await Phase9Runs.RunAsync(world, Phase9Runs.Unreviewed("T2"), Phase9Runs.NoChecks);
        var executor = Assert.Single(world.Clis.Launches, r => r.Agent.Role == AgentRole.Executor).Prompt;
        Assert.Contains(block, executor, StringComparison.Ordinal);
        Assert.DoesNotContain("<response_language>\"es-ES\"", executor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_interface_language_gives_the_same_bytes_under_two_cultures_of_the_thread_and_only_the_block_changes_with_the_language()
    {
        string spanish;
        string english1;
        string english2;
        await using (var world = await OpenAsync())
        {
            using var scope = English();
            english1 = await PlannerPromptAsync(world, "es-ES");
            english2 = await PlannerPromptAsync(world, "de-DE");
        }

        await using (var world = await OpenAsync())
        {
            spanish = await PlannerPromptAsync(world, "en-US");
        }

        // Byte for byte the same prompt, whatever culture the thread that asked has.
        Assert.Equal(Encoding.UTF8.GetBytes(english1), Encoding.UTF8.GetBytes(english2));

        // And what the interface language changes is the block of data and nothing else of the prompt.
        Assert.Contains("<response_language>\"es-ES\"</response_language>", spanish, StringComparison.Ordinal);
        Assert.Equal(spanish.Replace("\"es-ES\"", "\"en\"", StringComparison.Ordinal), english1);
    }

    [Theory]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("ca-ES", "es-ES")]
    public void The_language_of_the_interface_is_the_one_of_Windows_when_it_is_in_the_list_and_English_when_it_is_not(string windows, string expected)
    {
        Assert.Equal(expected, UiLanguages.Resolve(null, CultureInfo.GetCultureInfo(windows)).CultureName);

        // A choice in «Ajustes» wins over Windows; one that is not in the list is no choice.
        Assert.Equal("en", UiLanguages.Resolve("en", CultureInfo.GetCultureInfo("es-ES")).CultureName);
        Assert.Equal(expected, UiLanguages.Resolve("tlh", CultureInfo.GetCultureInfo(windows)).CultureName);
    }
}
