using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Prompts;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// V44 (docs/17 D-48, F14-4, ADR-0054): the executor and the planner write what the person reads in the language the person chose,
/// which reaches them as a block of data; the culture of the thread changes nothing of their prompts.
/// </summary>
public sealed partial class ResponseLanguageInPromptsTests
{
    [GeneratedRegex(@"\d{8}-[0-9a-f]{6}")]
    private static partial Regex RunIds();

    /// <summary>The id of a run is drawn at random in every run of the engine; it is the one thing that tells two prompts apart.</summary>
    private static string Normalized(string prompt) => RunIds().Replace(prompt, "RUN");

    private sealed class Fixed(string tag) : IResponseLanguage
    {
        public string Tag { get; } = tag;
    }

    private static async Task<string> ExecutorPromptAsync(string? language, string threadCulture)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(threadCulture);
            using var harness = new EngineHarness { ResponseLanguage = language is null ? null : new Fixed(language) };
            await using var run = await harness.StartAsync(Plan(Work("T1")));
            await run.CompleteAsync(harness);
            return Normalized(Assert.Single(harness.Claude.Starts).Request.Prompt);
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    private static async Task<string> PlannerPromptAsync(string? language, string threadCulture)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(threadCulture);
            using var harness = new EngineHarness { ResponseLanguage = language is null ? null : new Fixed(language) };
            harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Planning.PlanJson.Plan(Planning.PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"])).ToJsonString() });
            await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken);
            return Normalized(Assert.Single(harness.Claude.Starts).Request.Prompt);
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en")]
    public async Task The_executor_prompt_carries_the_language_and_the_rule(string language)
    {
        var prompt = await ExecutorPromptAsync(language, "es-ES");

        Assert.Contains($"<response_language>\"{language}\"</response_language>", prompt, StringComparison.Ordinal);
        Assert.Contains(ResponseLanguage.Rule, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en")]
    public async Task The_planner_prompt_carries_the_language_and_the_rule(string language)
    {
        var prompt = await PlannerPromptAsync(language, "es-ES");

        Assert.Contains($"<response_language>\"{language}\"</response_language>", prompt, StringComparison.Ordinal);
        Assert.Contains(ResponseLanguage.Rule, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_language_the_prompts_say_the_default_one()
    {
        Assert.Contains($"\"{ResponseLanguage.Default}\"", await ExecutorPromptAsync(null, "en-US"), StringComparison.Ordinal);
        Assert.Contains($"\"{ResponseLanguage.Default}\"", await PlannerPromptAsync(null, "en-US"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_chosen_language_gives_the_same_bytes_under_two_thread_cultures_for_the_executor_and_the_planner()
    {
        var executorA = await ExecutorPromptAsync("en", "es-ES");
        var executorB = await ExecutorPromptAsync("en", "de-DE");
        var plannerA = await PlannerPromptAsync("en", "es-ES");
        var plannerB = await PlannerPromptAsync("en", "de-DE");

        Assert.Equal(Encoding.UTF8.GetBytes(executorA), Encoding.UTF8.GetBytes(executorB));
        Assert.Equal(Encoding.UTF8.GetBytes(plannerA), Encoding.UTF8.GetBytes(plannerB));
    }

    [Fact]
    public async Task Only_the_block_changes_in_an_executor_prompt_when_the_language_changes()
    {
        var spanish = await ExecutorPromptAsync("es-ES", "es-ES");
        var english = await ExecutorPromptAsync("en", "es-ES");

        Assert.Equal(spanish.Replace("\"es-ES\"", "\"en\"", StringComparison.Ordinal), english);
    }
}
