using System.Reflection;
using AgentStudio.Application.Help;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Providers;
using AgentStudio.Security.Redaction;

namespace AgentStudio.Tests.Application.Help;

/// <summary>
/// docs/17 §5.4 (D-54 to D-57, P0 of Phase 14, ADR-0056): the contracts of the help. The articles of the corpus keep their rules, the
/// search is local, the card says what is sent and withholds the project unless the person marks it, a secret never reaches it, and
/// what the agent answers is cut to what the contract allows. No agent is launched.
/// </summary>
public sealed class HelpContractsTests
{
    private const string Profile = @"C:\Users\ana";

    private static readonly ISecretRedactor Redactor = new SecretRedactor(Profile);

    /// <summary>A token of GitHub in the format the redactor knows: a secret seeded in what the person writes.</summary>
    private const string SeededToken = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    private static HelpArticle Article(string id = "orchestrator.team", string language = "es-ES", string title = "El equipo", string markdown = "# El equipo\nTexto.", string[]? screens = null, string[]? infoKeys = null) =>
        new(id, language, title, markdown, screens ?? ["orchestrator"], infoKeys ?? ["Info_Canvas_Team"]);

    // ---- the articles of the corpus (D-55) ----

    [Fact]
    public void An_article_with_a_code_for_id_a_language_a_title_a_body_and_valid_links_breaks_no_rule()
    {
        Assert.Empty(HelpArticleRules.Validate(Article()));
        Assert.Empty(HelpArticleRules.Validate(Article(screens: [], infoKeys: [])));
    }

    [Fact]
    public void Each_rule_of_an_article_has_its_own_code()
    {
        Assert.Equal(["help.article-id-invalid"], HelpArticleRules.Validate(Article(id: "Equipo Del Orquestador")));
        Assert.Equal(["help.article-language-invalid"], HelpArticleRules.Validate(Article(language: "castellano")));
        Assert.Equal(["help.article-title-required"], HelpArticleRules.Validate(Article(title: " ")));
        Assert.Equal(["help.article-title-too-long"], HelpArticleRules.Validate(Article(title: new string('t', HelpLimits.MaxTitleLength + 1))));
        Assert.Equal(["help.article-body-required"], HelpArticleRules.Validate(Article(markdown: "")));
        Assert.Equal(["help.article-screen-invalid"], HelpArticleRules.Validate(Article(screens: ["Orquestador"])));
        Assert.Equal(["help.article-info-key-invalid"], HelpArticleRules.Validate(Article(infoKeys: ["info canvas"])));
        Assert.Equal(["help.article-link-duplicate"], HelpArticleRules.Validate(Article(screens: ["orchestrator", "orchestrator"])));
        Assert.Equal(["help.article-link-duplicate"], HelpArticleRules.Validate(Article(infoKeys: ["Info_Tools", "Info_Tools"])));
        Assert.Throws<ArgumentNullException>(() => HelpArticleRules.Validate(null!));
    }

    [Theory]
    [InlineData("Info_Canvas_Team", true)]
    [InlineData("Setup_Info", true)]
    [InlineData("Info", true)]
    [InlineData("", false)]
    [InlineData("_Info", false)]
    [InlineData("1Info", false)]
    [InlineData("Info-Canvas", false)]
    [InlineData("Info.Canvas", false)]
    public void The_key_of_an_information_button_is_letters_digits_and_underscores_starting_with_a_letter(string key, bool valid)
    {
        Assert.Equal(valid, HelpArticleRules.IsInfoKey(key));
    }

    [Fact]
    public void The_key_of_every_information_button_the_app_has_is_a_valid_key()
    {
        // The keys of the ⓘ the XAML of Phase 13 already uses: the corpus will name each of them in an article (WP-14C).
        foreach (var key in new[] { "Info_Canvas_Analysis", "Info_Canvas_Brief", "Info_Canvas_Chat", "Info_Canvas_Inspector", "Info_Canvas_Layers", "Info_Canvas_Motion", "Info_Canvas_Strip", "Info_Canvas_Team", "Info_Consumption", "Info_Monitor", "Info_Output", "Info_Problems", "Info_Question", "Info_Tests", "Info_Tools", "Setup_Info" })
        {
            Assert.True(HelpArticleRules.IsInfoKey(key), key);
        }
    }

    // ---- the local search (D-55) ----

    [Fact]
    public void A_search_needs_a_text_that_is_short_and_plain_and_a_limit_within_the_maximum()
    {
        Assert.Empty(HelpSearchRules.Validate("cómo apruebo el plan", 5));
        Assert.Empty(HelpSearchRules.Validate("x", HelpLimits.MaxSearchResults));
        Assert.Equal(["help.search-query-required"], HelpSearchRules.Validate("  ", 5));
        Assert.Equal(["help.search-query-required"], HelpSearchRules.Validate(null, 5));
        Assert.Equal(["help.search-query-too-long"], HelpSearchRules.Validate(new string('q', HelpLimits.MaxQueryLength + 1), 5));
        Assert.Equal(["help.search-query-hidden-character"], HelpSearchRules.Validate("plan\u202E", 5));
        Assert.Equal(["help.search-query-hidden-character"], HelpSearchRules.Validate("dos\nlíneas", 5));
        Assert.Equal(["help.search-limit-invalid"], HelpSearchRules.Validate("plan", 0));
        Assert.Equal(["help.search-limit-invalid"], HelpSearchRules.Validate("plan", HelpLimits.MaxSearchResults + 1));
    }

    [Fact]
    public void The_help_is_found_without_a_project_or_a_consent_because_the_search_and_the_corpus_take_neither()
    {
        // D-55: the first answer to any question is local and free. Nothing in these two ports can launch an agent or spend quota.
        var parameters = typeof(IHelpSearch).GetMethods().Concat(typeof(IHelpCorpus).GetMethods()).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);

        Assert.DoesNotContain(parameters, t => t == typeof(ProjectContext) || t == typeof(QuotaConsent));
        Assert.Equal(["SearchAsync"], MethodNames(typeof(IHelpSearch)));
        Assert.Equal(["Articles", "Find", "ForInfoKey", "ForScreen"], MethodNames(typeof(IHelpCorpus)));
        Assert.Equal(["Lexical", "Semantic", "Both"], Enum.GetNames<HelpMatchKind>());
    }

    // ---- the card of what is sent (D-57) ----

    private static HelpScreenFacts Facts(string screen = "orchestrator", string name = "Orquestador", string[]? state = null, string? notice = "Plan guardado", string? project = "tienda") =>
        new(screen, name, state ?? ["plan sin aprobar", "1 problema"], notice, project);

    [Fact]
    public void The_card_without_the_mark_sends_the_screen_its_state_and_the_notice_and_withholds_the_project()
    {
        var card = HelpContextCard.Build(Facts(), includeProjectData: false, Redactor);

        Assert.Equal("orchestrator", card.Screen);
        Assert.Equal(
            [
                new HelpContextPart(HelpContextKind.Screen, "Orquestador"),
                new HelpContextPart(HelpContextKind.State, "plan sin aprobar"),
                new HelpContextPart(HelpContextKind.State, "1 problema"),
                new HelpContextPart(HelpContextKind.LastNotice, "Plan guardado"),
            ],
            card.Parts);
        Assert.False(card.IncludesProjectData);
        Assert.Equal([HelpContextKind.ProjectName], card.Withheld);
        Assert.Empty(card.Validate(Redactor));
    }

    [Fact]
    public void With_the_mark_the_name_of_the_project_goes_and_nothing_is_withheld()
    {
        var card = HelpContextCard.Build(Facts(), includeProjectData: true, Redactor);

        Assert.Equal(new HelpContextPart(HelpContextKind.ProjectName, "tienda"), card.Parts[^1]);
        Assert.True(card.IncludesProjectData);
        Assert.Empty(card.Withheld);
        Assert.Empty(card.Validate(Redactor));
    }

    [Fact]
    public void A_message_with_a_path_goes_only_with_the_mark()
    {
        var facts = Facts(notice: @"No se pudo abrir C:\Users\ana\tienda\plan.json", state: ["plan sin aprobar"]);

        var without = HelpContextCard.Build(facts, includeProjectData: false, Redactor);
        var with = HelpContextCard.Build(facts, includeProjectData: true, Redactor);

        Assert.DoesNotContain(without.Parts, p => p.Kind == HelpContextKind.LastNotice);
        Assert.Contains(HelpContextKind.LastNotice, without.Withheld);
        Assert.Empty(without.Validate(Redactor));
        var notice = Assert.Single(with.Parts, p => p.Kind == HelpContextKind.LastNotice);
        Assert.Contains("tienda", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana", notice.Text, StringComparison.Ordinal);
        Assert.Empty(with.Validate(Redactor));
    }

    [Fact]
    public void A_secret_in_what_the_screen_says_never_reaches_the_card()
    {
        var facts = Facts(state: [$"clave {SeededToken} pegada", "plan sin aprobar"], notice: $"Falló con token={SeededToken}", project: "tienda");

        foreach (var include in new[] { false, true })
        {
            var card = HelpContextCard.Build(facts, include, Redactor);

            Assert.DoesNotContain(card.Parts, p => p.Text.Contains(SeededToken, StringComparison.Ordinal));
            Assert.Contains(card.Parts, p => p.Text.Contains(SecretRedactor.Placeholder, StringComparison.Ordinal));
            Assert.Empty(card.Validate(Redactor));
        }
    }

    [Fact]
    public void Every_text_is_cleaned_of_hidden_characters_and_cut_and_the_facts_are_limited()
    {
        var facts = Facts(
            name: "Orquestador\u202E",
            state: [.. Enumerable.Range(1, HelpLimits.MaxStateFacts + 3).Select(i => $"hecho {i}")],
            notice: new string('n', HelpLimits.MaxFactLength + 50) + "\u0007");

        var card = HelpContextCard.Build(facts, includeProjectData: false, Redactor);

        Assert.Equal("Orquestador", card.Parts[0].Text);
        Assert.Equal(HelpLimits.MaxStateFacts, card.Parts.Count(p => p.Kind == HelpContextKind.State));
        Assert.Equal(HelpLimits.MaxFactLength, Assert.Single(card.Parts, p => p.Kind == HelpContextKind.LastNotice).Text.Length);
        Assert.Empty(card.Validate(Redactor));
    }

    [Fact]
    public void A_screen_without_a_code_or_a_name_makes_no_card()
    {
        Assert.Throws<ArgumentException>(() => HelpContextCard.Build(Facts(screen: "Orquestador"), false, Redactor));
        Assert.Throws<ArgumentException>(() => HelpContextCard.Build(Facts(name: " "), false, Redactor));
        Assert.Throws<ArgumentException>(() => HelpContextCard.Build(Facts(name: @"C:\Users\ana\pantalla"), false, Redactor));
        Assert.Throws<ArgumentNullException>(() => HelpContextCard.Build(null!, false, Redactor));
        Assert.Throws<ArgumentNullException>(() => HelpContextCard.Build(Facts(), false, null!));
    }

    [Fact]
    public void A_card_made_by_hand_is_checked_again_before_it_is_sent()
    {
        var good = HelpContextCard.Build(Facts(), includeProjectData: true, Redactor);

        Assert.Equal(["help.card-project-data-not-marked"], (good with { IncludesProjectData = false }).Validate(Redactor));
        Assert.Equal(["help.card-screen-invalid"], (good with { Screen = "Orquestador" }).Validate(Redactor));
        Assert.Equal(["help.card-screen-part"], (good with { Parts = [.. good.Parts.Skip(1)] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-not-redacted"],
            (good with { Parts = [.. good.Parts, new HelpContextPart(HelpContextKind.State, $"x {SeededToken}")] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-part-duplicate"],
            (good with { Parts = [.. good.Parts, new HelpContextPart(HelpContextKind.ProjectName, "otro")] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-part-empty"],
            (good with { Parts = [.. good.Parts, new HelpContextPart(HelpContextKind.State, " ")] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-part-too-long"],
            (good with { Parts = [.. good.Parts, new HelpContextPart(HelpContextKind.State, new string('s', HelpLimits.MaxFactLength + 1))] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-hidden-character"],
            (good with { Parts = [.. good.Parts, new HelpContextPart(HelpContextKind.State, "a\u202Eb")] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-part-invalid"],
            (good with { Parts = [.. good.Parts, new HelpContextPart((HelpContextKind)9, "x")] }).Validate(Redactor));
        Assert.Equal(
            ["help.card-too-many-facts"],
            (good with { Parts = [.. good.Parts, .. Enumerable.Range(0, HelpLimits.MaxStateFacts).Select(i => new HelpContextPart(HelpContextKind.State, $"h{i}"))] }).Validate(Redactor));
    }

    [Fact]
    public void A_path_in_a_part_without_the_mark_is_caught_even_when_the_card_was_not_built_by_the_builder()
    {
        var card = new HelpContextCard(
            "orchestrator",
            [new HelpContextPart(HelpContextKind.Screen, "Orquestador"), new HelpContextPart(HelpContextKind.State, "en ~/tienda")],
            IncludesProjectData: false,
            []);

        Assert.Equal(["help.card-project-data-not-marked"], card.Validate(Redactor));
    }

    // ---- the agent and the question (D-56) ----

    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", @"C:\repo");

    private static HelpAskRequest Request(string question = "¿Cómo apruebo el plan?", string language = "es-ES", HelpContextCard? card = null, HelpAgentChoice? agent = null) =>
        new(Project, question, language, card ?? HelpContextCard.Build(Facts(), false, Redactor), agent ?? HelpAgentChoice.Default, new QuotaConsent("user", DateTimeOffset.UnixEpoch, "La ayuda"));

    [Fact]
    public void The_agent_of_the_help_is_claude_with_a_light_model_by_default_and_codex_is_warned_about()
    {
        Assert.Equal(new HelpAgentChoice(ProviderId.ClaudeCode, null), HelpAgentChoice.Default);
        Assert.True(HelpAgentChoice.Default.IsDefault);
        Assert.False(HelpAgentChoice.Default.ReadsWholeDisk);
        var codex = new HelpAgentChoice(ProviderId.CodexCli, null);
        Assert.False(codex.IsDefault);
        Assert.True(codex.ReadsWholeDisk);
        Assert.False(new HelpAgentChoice(ProviderId.ClaudeCode, "claude-haiku-4-5").IsDefault);
        Assert.Equal("ayuda", HelpAgent.Id.Value);
    }

    [Fact]
    public void A_clean_question_with_its_card_the_language_and_an_agent_breaks_no_rule()
    {
        Assert.Empty(HelpAskRules.Validate(Request(), Redactor));
        Assert.Empty(HelpAskRules.Validate(Request(question: "Una\nlínea nueva\ny otra", language: "en"), Redactor));
    }

    [Fact]
    public void Each_rule_of_a_question_has_its_own_code()
    {
        Assert.Equal(["help.question-required"], HelpAskRules.Validate(Request(question: " "), Redactor));
        Assert.Equal(["help.question-too-long"], HelpAskRules.Validate(Request(question: new string('q', HelpLimits.MaxQuestionLength + 1)), Redactor));
        Assert.Equal(["help.question-hidden-character"], HelpAskRules.Validate(Request(question: "plan\u202E"), Redactor));
        Assert.Equal(["help.question-not-redacted"], HelpAskRules.Validate(Request(question: $"mi clave es {SeededToken}"), Redactor));
        Assert.Equal(["help.language-invalid"], HelpAskRules.Validate(Request(language: "castellano"), Redactor));
        Assert.Equal(["help.card-required"], HelpAskRules.Validate(Request() with { Context = null! }, Redactor));
        Assert.Equal(["help.agent-required"], HelpAskRules.Validate(Request() with { Agent = new HelpAgentChoice(default, null) }, Redactor));
        Assert.Equal(["help.card-project-data-not-marked"], HelpAskRules.Validate(Request(card: HelpContextCard.Build(Facts(), true, Redactor) with { IncludesProjectData = false }), Redactor));
    }

    [Fact]
    public void The_question_is_cleaned_of_hidden_characters_and_redacted_so_what_the_person_sees_is_what_travels()
    {
        var clean = HelpAskRules.CleanQuestion($"  ¿Y esto?\u202E clave {SeededToken}\r\nfin ", Redactor);

        Assert.Equal($"¿Y esto? clave {SecretRedactor.Placeholder}\nfin", clean);
        Assert.Empty(HelpAskRules.Validate(Request(question: clean), Redactor));
        Assert.Equal(string.Empty, HelpAskRules.CleanQuestion(null, Redactor));
    }

    [Fact]
    public void Without_a_project_there_is_no_agent_because_the_request_names_one()
    {
        // D-56: without an open project there is only the local help, since there is no log to count the run in.
        Assert.Equal(typeof(ProjectContext), typeof(HelpAskRequest).GetProperty(nameof(HelpAskRequest.Project))!.PropertyType);
        Assert.Equal(["AskAsync"], MethodNames(typeof(IHelpAgent)));
        Assert.Equal(typeof(QuotaConsent), typeof(HelpAskRequest).GetProperty(nameof(HelpAskRequest.Consent))!.PropertyType);
    }

    // ---- the closed list of actions (D-56) ----

    [Fact]
    public void An_action_has_a_code_for_id_and_a_coded_text_for_its_button()
    {
        var action = new HelpAction("open.consumption", UserText.Of("help.action-open-consumption"));

        Assert.Equal("open.consumption", action.Id);
        Assert.Equal("Text_help_action_open_consumption", action.Label.ResourceKey);
        Assert.Throws<ArgumentException>(() => new HelpAction("Abrir Consumo", UserText.Of("help.action-open-consumption")));
        Assert.Throws<ArgumentNullException>(() => new HelpAction("open.consumption", null!));
        Assert.Equal(["get_Actions"], MethodNames(typeof(IHelpActionCatalog)));
    }

    // ---- what the system keeps of the answer (D-56) ----

    private static readonly string[] Sent = ["orchestrator.team", "usage.consumption", "settings.unattended"];

    private static readonly string[] Actions = ["open.consumption", "open.settings-unattended"];

    [Fact]
    public void An_answer_with_cited_articles_among_those_sent_and_actions_of_the_list_is_kept_as_it_is()
    {
        var check = HelpAnswerRules.Check("Abre el plan y pulsa «Aprobar el plan».", ["orchestrator.team", "usage.consumption"], ["open.consumption"], Sent, Actions);

        Assert.Equal("Abre el plan y pulsa «Aprobar el plan».", check.Text);
        Assert.Equal(["orchestrator.team", "usage.consumption"], check.Articles);
        Assert.Equal(["open.consumption"], check.Actions);
        Assert.Empty(check.Discarded);
    }

    [Fact]
    public void An_article_that_was_not_sent_and_an_action_that_is_not_in_the_list_are_left_out_and_said_so()
    {
        var check = HelpAnswerRules.Check("Texto.", ["orchestrator.team", "inventado.por-el-agente"], ["open.consumption", "run.delete-everything"], Sent, Actions);

        Assert.Equal(["orchestrator.team"], check.Articles);
        Assert.Equal(["open.consumption"], check.Actions);
        Assert.Equal(
            ["the article 'inventado.por-el-agente' is not one of the articles sent.", "the action 'run.delete-everything' is not in the closed list."],
            check.Discarded);
    }

    [Fact]
    public void Repeats_and_blanks_are_dropped_and_the_caps_are_kept()
    {
        var check = HelpAnswerRules.Check(
            "Texto.",
            ["orchestrator.team", "orchestrator.team", " ", null, "usage.consumption", "settings.unattended"],
            ["open.consumption", "open.settings-unattended", "open.consumption"],
            [.. Sent, .. Enumerable.Range(0, 6).Select(i => $"extra.articulo-{i}")],
            [.. Actions, "open.third"]);

        Assert.Equal(["orchestrator.team", "usage.consumption", "settings.unattended"], check.Articles);
        Assert.Equal(["open.consumption", "open.settings-unattended"], check.Actions);
        Assert.Empty(check.Discarded);

        var many = HelpAnswerRules.Check("T.", Enumerable.Range(0, 6).Select(i => $"extra.articulo-{i}"), ["open.consumption", "open.settings-unattended", "open.third"], [.. Enumerable.Range(0, 6).Select(i => $"extra.articulo-{i}")], [.. Actions, "open.third"]);
        Assert.Equal(HelpLimits.MaxCitedArticles, many.Articles.Count);
        Assert.Equal(HelpLimits.MaxActions, many.Actions.Count);
        Assert.Equal(3, many.Discarded.Count);
        Assert.All(many.Discarded, d => Assert.Contains("one more than", d, StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_is_cleaned_cut_to_1200_characters_and_an_empty_one_is_said_so()
    {
        var cut = HelpAnswerRules.Check(new string('a', HelpLimits.MaxAnswerLength + 300), null, null, Sent, Actions);
        var hidden = HelpAnswerRules.Check("Hola\u202E, mundo\u0007\r\nfin.", null, null, Sent, Actions);
        var empty = HelpAnswerRules.Check("  ", null, null, Sent, Actions);

        Assert.Equal(HelpLimits.MaxAnswerLength, cut.Text.Length);
        Assert.Equal([$"the text is longer than {HelpLimits.MaxAnswerLength} characters: it was cut."], cut.Discarded);
        Assert.Equal("Hola, mundo\nfin.", hidden.Text);
        Assert.Empty(hidden.Discarded);
        Assert.Equal(string.Empty, empty.Text);
        Assert.Equal(["the answer has no text."], empty.Discarded);
        Assert.Equal(1200, HelpLimits.MaxAnswerLength);
        Assert.Throws<ArgumentNullException>(() => HelpAnswerRules.Check("x", null, null, null!, Actions));
    }

    // ---- the surface of the contracts ----

    [Fact]
    public void The_limits_of_the_help_are_the_documented_ones()
    {
        Assert.Equal(
            [
                ("MaxTitleLength", 80), ("MaxQueryLength", 200), ("MaxSearchResults", 10), ("MaxQuestionLength", 1000), ("MaxAnswerLength", 1200),
                ("MaxSentArticles", 4), ("MaxCitedArticles", 4), ("MaxActions", 2), ("MaxStateFacts", 6), ("MaxFactLength", 200),
            ],
            typeof(HelpLimits).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (f.Name, (int)f.GetRawConstantValue()!)));
        Assert.True(HelpLimits.MaxCitedArticles <= HelpLimits.MaxSentArticles);
        Assert.Equal(["Screen", "State", "LastNotice", "ProjectName"], Enum.GetNames<HelpContextKind>());
    }

    private static string[] MethodNames(Type type) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Order(StringComparer.Ordinal)];
}
