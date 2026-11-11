using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Help;
using AgentStudio.Desktop.Help;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Desktop.Localization;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Help;

/// <summary>
/// F14-7 (docs/17 D-55, ADR-0056): the local help, free. Every article exists in both languages and follows the rules of the
/// corpus, every ⓘ of the screens has its article and its «Más ayuda», the search is local (FTS5) and tolerant, and <c>F1</c> opens
/// the article of the screen that has the focus.
/// </summary>
public sealed partial class HelpTests
{
    private static readonly HelpCorpus Corpus = HelpCorpus.FromAssembly();

    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"InfoKey=""([A-Za-z_]+)""")]
    private static partial Regex InfoKeyAttribute();

    private static IReadOnlyList<string> InfoKeysInViews() =>
        [.. Directory.GetFiles(Path.Combine(DesktopRoot, "Views"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => InfoKeyAttribute().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    // ---- the corpus ----

    [Fact]
    public void Every_article_exists_in_both_languages_with_the_same_links_and_keeps_the_rules_of_the_corpus()
    {
        var spanish = Corpus.Articles("es-ES");
        var english = Corpus.Articles("en");

        Assert.Equal(HelpScreens.All.Count, spanish.Count);
        Assert.Equal(spanish.Select(a => a.Id), english.Select(a => a.Id));
        foreach (var es in spanish)
        {
            Assert.Equal("es-ES", es.Language);
            var en = Assert.Single(english, a => a.Id == es.Id);
            Assert.Equal("en", en.Language);
            Assert.Empty(HelpArticleRules.Validate(es));
            Assert.Empty(HelpArticleRules.Validate(en));
            Assert.Equal(es.Screens, en.Screens);
            Assert.Equal(es.InfoKeys, en.InfoKeys);
            Assert.NotEqual(es.Title, en.Title);
            Assert.NotEqual(es.Markdown, en.Markdown);

            // Short: a screen of text (ADR-0054, point 9).
            Assert.InRange(es.Markdown.Length, 200, 1800);
            Assert.InRange(en.Markdown.Length, 200, 1800);
        }
    }

    [Fact]
    public void Every_screen_has_an_article_and_a_name_and_no_screen_is_shared()
    {
        foreach (var screen in HelpScreens.All)
        {
            Assert.NotNull(Corpus.ForScreen(screen, "es-ES"));
            Assert.NotNull(Corpus.ForScreen(screen, "en"));
            Assert.True(Spanish.Has("Help_Screen_" + screen), screen);
            Assert.True(English.Has("Help_Screen_" + screen), screen);
        }

        Assert.Equal(HelpScreens.All.Count, Corpus.Articles("es-ES").SelectMany(a => a.Screens).Distinct().Count());
        Assert.Null(Corpus.ForScreen("welcome", "es-ES"));
    }

    [Fact]
    public void Every_info_button_of_the_screens_has_an_article_in_each_language_and_no_key_is_in_two_articles()
    {
        var keys = InfoKeysInViews();
        Assert.True(keys.Count >= 30, "The ⓘ of the screens were not found.");
        foreach (var key in keys)
        {
            Assert.NotNull(Corpus.ForInfoKey(key, "es-ES"));
            Assert.NotNull(Corpus.ForInfoKey(key, "en"));
        }

        var linked = Corpus.Articles("es-ES").SelectMany(a => a.InfoKeys).ToList();
        Assert.Equal(linked.Count, linked.Distinct(StringComparer.Ordinal).Count());
        var all = ResourceFiles().Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);
        Assert.All(linked, key => Assert.Contains(key + "_What", all));
    }

    [Fact]
    public void An_article_that_is_missing_in_a_language_is_read_in_spanish_and_a_region_reads_its_language()
    {
        var only = HelpCorpus.Parse("solo", "es-ES", "---\ntitle: Solo\nscreens: lock\ninfo: Info_Lock\n---\nTexto de la ayuda que solo está en castellano.");
        var corpus = new HelpCorpus([only, Corpus.Articles("en")[0]]);

        Assert.Equal("es-ES", corpus.Find("solo", "en")!.Language);
        Assert.Equal("en", corpus.Resolve("en-GB"));
        Assert.Equal("es-ES", corpus.Resolve("es-MX"));
        Assert.Equal("es-ES", corpus.Resolve("fr"));
        Assert.Equal("es-ES", corpus.Resolve(null));
    }

    [Fact]
    public void A_file_without_a_valid_header_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => HelpCorpus.Parse("x", "es-ES", "# Sin cabecera\nTexto"));
        Assert.Throws<InvalidDataException>(() => HelpCorpus.Parse("x", "es-ES", "---\ntitle: X\ndesconocido: 1\n---\nTexto"));
        Assert.Throws<InvalidDataException>(() => HelpCorpus.Parse("x", "es-ES", "---\ntitle: X\nscreens: lock\nTexto"));
    }

    [Fact]
    public void The_actions_of_the_closed_list_have_a_label_in_both_languages_and_unique_ids()
    {
        var catalog = new HelpActionCatalog();
        Assert.Equal(catalog.Actions.Count, catalog.Actions.Select(a => a.Id).Distinct().Count());
        foreach (var action in catalog.Actions)
        {
            Assert.True(Spanish.Has(action.Label.ResourceKey), action.Id);
            Assert.True(English.Has(action.Label.ResourceKey), action.Id);
            Assert.NotEqual(Spanish.Render(action.Label), English.Render(action.Label));
            Assert.Same(action, catalog.Find(action.Id));
        }

        Assert.Null(catalog.Find("delete.everything"));
    }

    // ---- the search ----

    [Fact]
    public async Task The_search_is_local_and_finds_an_article_by_its_words_without_accents_or_a_whole_word()
    {
        using var search = new HelpSearch(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var byWord = await search.SearchAsync("cuota", "es-ES", 5, ct);
        Assert.Contains(byWord.Select(h => h.ArticleId), id => id == "consumption");
        Assert.All(byWord, hit => Assert.Equal(HelpMatchKind.Lexical, hit.Match));
        Assert.All(byWord, hit => Assert.False(string.IsNullOrWhiteSpace(hit.Excerpt)));

        Assert.Contains((await search.SearchAsync("auditoria", "es-ES", 5, ct)).Select(h => h.ArticleId), id => id == "audit");
        Assert.Contains((await search.SearchAsync("gast", "es-ES", 5, ct)).Select(h => h.ArticleId), id => id == "consumption");
        Assert.Equal("lock", (await search.SearchAsync("¿cómo bloqueo la aplicación?", "es-ES", 3, ct))[0].ArticleId);
        Assert.Equal("debates", (await search.SearchAsync("verdict judge", "en", 3, ct))[0].ArticleId);
        Assert.Single(await search.SearchAsync("cuota", "es-ES", 1, ct));
    }

    [Fact]
    public async Task The_search_never_lets_an_operator_through_and_a_query_that_breaks_the_rules_is_refused()
    {
        using var search = new HelpSearch(Corpus);
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await search.SearchAsync("zzzqqq", "es-ES", 5, ct));
        Assert.Empty(await search.SearchAsync("de la el", "es-ES", 5, ct));
        _ = await search.SearchAsync("\" OR * NEAR( AND \"", "es-ES", 5, ct);
        _ = await search.SearchAsync("title:cuota", "es-ES", 5, ct);

        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("   ", "es-ES", 5, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync(new string('a', 201), "es-ES", 5, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("cuota", "es-ES", 0, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("cuota", "es-ES", 11, ct));
    }

    [Fact]
    public void The_port_of_the_search_has_no_project_and_no_consent_so_it_cannot_spend_quota()
    {
        var parameters = typeof(IHelpSearch).GetMethod(nameof(IHelpSearch.SearchAsync))!.GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(string), typeof(string), typeof(int), typeof(CancellationToken)], parameters);
    }

    // ---- F1, the panel and the shell ----

    private static HelpDocumentViewModel Panel() => new(new HelpBackend(Corpus, new HelpSearch(Corpus), new HelpActionCatalog()), texts: Spanish);

    [Fact]
    public void F1_opens_the_article_of_the_screen_and_a_screen_without_one_shows_the_topics_with_a_note()
    {
        var panel = Panel();

        Assert.False(panel.HasArticle);
        Assert.Equal(HelpScreens.All.Count, panel.Topics.Count);

        panel.ShowFor(HelpScreens.Consumption, null, null, null);
        Assert.Equal("consumption", panel.ArticleId);
        Assert.Equal(Corpus.Find("consumption", "es-ES")!.Markdown, panel.ArticleMarkdown);

        panel.ShowFor(null, null, null, null);
        Assert.False(panel.HasArticle);

        panel.ShowFor("welcome-nueva", null, null, null);
        Assert.False(panel.HasArticle);
        Assert.Equal(Spanish.Get("Help_Article_NoScreen"), panel.ArticleNotice);

        Assert.True(panel.OpenInfoKey("Info_Lock"));
        Assert.Equal("lock", panel.ArticleId);
        Assert.False(panel.OpenInfoKey("Info_Inexistente"));
        Assert.True(panel.HasInfoArticle("Info_Memory"));
        Assert.False(panel.HasInfoArticle(null));
        Assert.Null(HelpScreens.Of(null));
        Assert.Null(HelpScreens.Of("un fichero"));
    }

    [Fact]
    public async Task Typing_in_the_search_box_lists_results_and_a_result_opens_its_article()
    {
        var panel = Panel();
        panel.Query = "memoria";
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.NotEmpty(panel.Results);
        panel.Results[0].OpenCommand.Execute(null);
        Assert.True(panel.HasArticle);

        panel.Query = "zzzqqq";
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(panel.Results);
        Assert.NotNull(panel.SearchNotice);
    }

    [Fact]
    public void Without_the_agent_or_a_project_the_panel_says_why_it_cannot_ask_and_the_local_help_keeps_working()
    {
        var panel = Panel();
        Assert.False(panel.AgentAvailable);
        Assert.Equal(Spanish.Get("Help_Ask_NotComposed"), panel.AgentUnavailableText);
        panel.Question = "¿Dónde veo el gasto?";
        Assert.False(panel.AskCommand.CanExecute(null));
        Assert.True(panel.OpenArticle("memory"));
    }

    [Fact]
    public void The_shell_offers_Ayuda_and_F1_and_the_more_help_of_each_info_button_only_with_the_help_composed()
    {
        using var folder = new TempDirectory();
        MainViewModel Shell(HelpDocumentViewModel? help) => new(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), TimeProvider.System),
            new ExplorerViewModel(),
            new JournalViewModel(TimeProvider.System),
            new QuickOpenViewModel(),
            help: help);

        Assert.DoesNotContain(Shell(null).Activities, a => a.Id == "help");
        Assert.False(Shell(null).OpenInfoHelpCommand.CanExecute("Info_Lock"));

        var panel = Panel();
        var shell = Shell(panel);
        var entry = Assert.Single(shell.Activities, a => a.Id == "help");
        Assert.Equal("F1", entry.Shortcut);
        Assert.True(shell.OpenInfoHelpCommand.CanExecute("Info_Lock"));
        Assert.False(shell.OpenInfoHelpCommand.CanExecute("Info_Inexistente"));

        // The ⓘ of the monitor: the shell opens the help tab on the article of that section.
        shell.OpenInfoHelpCommand.Execute("Info_Debates");
        Assert.Same(panel, shell.ActiveContent);
        Assert.Equal("debates", panel.ArticleId);
        Assert.Contains(panel, shell.Documents);

        // F1 with the help already in front changes nothing; the bar entry runs the same command.
        shell.OpenHelpCommand.Execute(null);
        Assert.Equal("debates", panel.ArticleId);
        Assert.Same(shell.OpenHelpCommand, entry.Command);

        var xaml = File.ReadAllText(Path.Combine(DesktopRoot, "MainWindow.xaml"));
        Assert.Contains("<KeyBinding Key=\"F1\" Command=\"{Binding OpenHelpCommand}\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void In_a_rendered_window_the_more_help_of_an_info_button_shows_only_for_a_section_with_an_article()
    {
        var panel = Panel();
        var (withArticle, without) = WpfHost.Run(() =>
        {
            var shell = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), TimeProvider.System),
                new ExplorerViewModel(),
                new JournalViewModel(TimeProvider.System),
                new QuickOpenViewModel(),
                help: panel);
            var known = new AgentStudio.Desktop.Controls.Info.InfoButton { InfoKey = "Info_Lock" };
            var unknown = new AgentStudio.Desktop.Controls.Info.InfoButton { InfoKey = "Info_Inexistente" };
            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(known);
            content.Children.Add(unknown);
            var window = new System.Windows.Window { DataContext = shell, Content = content, ShowActivated = false, Left = -10000, Top = -10000, Width = 200, Height = 200 };
            try
            {
                window.Show();
                WpfHost.FlushBindings();
                return (((System.Windows.Controls.Button)known.FindName("MoreHelp")).Visibility, ((System.Windows.Controls.Button)unknown.FindName("MoreHelp")).Visibility);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(System.Windows.Visibility.Visible, withArticle);
        Assert.Equal(System.Windows.Visibility.Collapsed, without);
    }

    [Fact]
    public void The_texts_of_the_help_exist_in_both_languages_with_the_same_placeholders()
    {
        var files = ResourceFiles();
        var neutral = Read(files["HelpStrings"].Neutral);
        var english = Read(files["HelpStrings"].Satellites["en"]);
        Assert.Equal(neutral.Keys.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Ayuda", Spanish.Get("Help_Title"));
        Assert.Equal("Help", English.Get("Help_Title"));
        foreach (var kind in Enum.GetValues<HelpContextKind>())
        {
            Assert.True(Spanish.Has("Help_Card_Kind_" + kind) || kind == HelpContextKind.Screen && Spanish.Has("Help_Card_Kind_Screen"), kind.ToString());
            Assert.True(Spanish.Has("Help_Card_Withheld_" + kind), kind.ToString());
        }

        Assert.DoesNotMatch("[áéíóúñ¿¡]", string.Concat(english.Values));
    }
}
