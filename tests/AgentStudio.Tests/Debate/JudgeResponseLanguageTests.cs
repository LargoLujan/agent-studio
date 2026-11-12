using System.Globalization;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// V44 (docs/17 D-48, F14-4): the judge writes the free text of its verdict in the language the person chose, which reaches it as a
/// block of data after the material; the brief it came from is not touched, so nothing a brief anchors depends on the language.
/// </summary>
public sealed class JudgeResponseLanguageTests
{
    private const string Workspace = "C:\\wt\\_integration";
    private const string Folder = "C:\\dossiers\\p1\\20261015-a1b2c3\\0123456789abcdef";
    private static readonly DossierFiles Files = new(Folder, "p1", "20261015-a1b2c3", [], []);
    private static readonly AgentDefinition CodexJudge = JudgingWorld.Agent(JudgingWorld.Judge, AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private static async Task<string> PromptAsync(string? language, string threadCulture)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(threadCulture);
            var dossier = Dossier();
            var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) => JudgeAnswers.Run(JudgeAnswers.Approval(dossier, Eid("E1"))));
            var judge = new AgentJudge(provider, new JudgeLaunch(CodexJudge, Workspace, Files, null, null, "Revisión de prueba", "T2", Language: language), new FakeTimeProvider(T0.AddHours(1)));

            await judge.EvaluateAsync(dossier, TestContext.Current.CancellationToken);

            return Assert.Single(provider.Requests).Prompt;
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
    public async Task The_judge_prompt_carries_the_chosen_language_as_data(string language)
    {
        var prompt = await PromptAsync(language, "es-ES");

        Assert.Contains($"<response_language>\"{language}\"</response_language>", prompt, StringComparison.Ordinal);
        Assert.Contains(ResponseLanguage.RuleEnglish, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_language_the_judge_says_the_default_one()
    {
        Assert.Contains($"\"{ResponseLanguage.Default}\"", await PromptAsync(null, "en-US"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_chosen_language_gives_the_same_judge_prompt_under_two_thread_cultures_and_only_the_block_changes_with_the_language()
    {
        var spanish = await PromptAsync("es-ES", "es-ES");
        var other = await PromptAsync("es-ES", "de-DE");
        var english = await PromptAsync("en", "es-ES");

        Assert.Equal(spanish, other);
        Assert.Equal(spanish.Replace("\"es-ES\"", "\"en\"", StringComparison.Ordinal), english);
    }
}
