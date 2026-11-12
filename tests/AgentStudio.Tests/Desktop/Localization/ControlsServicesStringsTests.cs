using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Markdown;
using AgentStudio.Desktop.Services;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// The texts of the controls and the services (Phase 14, WP-14A part d2; ADR-0054): the two languages say the same with the same
/// placeholders, the guide is applied (sentences of 20 words at most, glossary), the question about unsaved changes and the bar
/// of metrics paint in each language, and what the Markdown viewer shows (the journal as written) does not depend on the language.
/// </summary>
public sealed partial class ControlsServicesStringsTests
{
    private static readonly string[] Areas = ["ControlsStrings", "ServicesStrings"];

    private static readonly string[] BannedSpanish = ["worktree", "hash", "sha-256", "payload", "proveedor", "cuota", "dossier", "evidencia", "append-only"];

    private static readonly string[] BannedEnglish = ["worktree", "hash", "sha-256", "payload", "provider", "quota", "dossier", "append-only"];

    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex("""\{\d+(?:,[^}:]*)?(?::[^}]*)?\}""")]
    private static partial Regex Placeholder();

    private static IReadOnlyDictionary<string, string> Keys(string area, string language = "") =>
        Read(Path.Combine(ResourcesFolder, language.Length == 0 ? $"{area}.resx" : $"{area}.{language}.resx"));

    [Fact]
    public void Both_languages_have_the_same_keys_and_placeholders()
    {
        foreach (var area in Areas)
        {
            var es = Keys(area);
            var en = Keys(area, "en");

            Assert.NotEmpty(es);
            Assert.Equal(es.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, text) in es)
            {
                Assert.Equal(
                    Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal),
                    Placeholder().Matches(en[key]).Select(m => m.Value).Order(StringComparer.Ordinal));
            }
        }
    }

    [Fact]
    public void The_guide_is_applied_sentences_of_twenty_words_at_most_and_the_words_of_the_glossary()
    {
        foreach (var area in Areas)
        {
            foreach (var (language, banned) in new[] { ("", BannedSpanish), ("en", BannedEnglish) })
            {
                foreach (var (key, text) in Keys(area, language))
                {
                    Assert.DoesNotContain(banned, term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
                    foreach (var sentence in Regex.Split(text, @"(?<=[.?!:])\s+"))
                    {
                        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                        Assert.True(words <= 20, $"{area}.{language} {key}: a sentence of {words} words.");
                    }
                }
            }
        }
    }

    [Fact]
    public void The_question_about_unsaved_changes_paints_in_each_language()
    {
        var savable = new UnsavedChangesPromptViewModel(["a.cs"], canSave: true);
        var notSavable = new UnsavedChangesPromptViewModel(["b.cs"], canSave: false);
        Assert.Equal(
            ("Tienes cambios sin guardar en:", "¿Quieres guardarlos?", "Guardar cambios", "Descartar cambios", "Cancelar"),
            (savable.Intro, savable.Question, savable.SaveText, savable.DiscardText, savable.CancelText));
        Assert.Equal(("¿Quieres descartarlos?", "Volver"), (notSavable.Question, notSavable.CancelText));

        Assert.Equal("You have unsaved changes in:", English.Get("Unsaved_IntroSave"));
        Assert.Equal("Do you want to save them?", English.Get("Unsaved_QuestionSave"));
        Assert.Equal("… and 2 more", English.Format("Unsaved_More", 2));
        Assert.Equal("… y 2 más", Spanish.Format("Unsaved_More", 2));
    }

    /// <summary>
    /// The short line of the bars drops the start of the age («vista » / «seen »): it can do it in both languages only if every age
    /// the quota says starts with the prefix of the controls.
    /// </summary>
    [Fact]
    public void Every_age_of_the_quota_starts_with_the_prefix_the_short_bar_drops_in_both_languages()
    {
        foreach (var provider in new[] { Spanish, English })
        {
            var prefix = provider.Get("MetricBar_SeenPrefix");
            Assert.False(string.IsNullOrWhiteSpace(prefix));
            string[] ages =
            [
                provider.Get("Observability_Seen_Now"),
                provider.Format("Observability_Seen_Minutes", 12),
                provider.Format("Observability_Seen_Hours", 5),
                provider.Format("Observability_Seen_Days", 3),
            ];
            foreach (var age in ages)
            {
                Assert.StartsWith(prefix, age, StringComparison.Ordinal);
                Assert.True(age.Length > prefix.Length, age);
            }
        }

        Assert.Equal("seen less than 1 min ago", English.Get("Observability_Seen_Now"));
        Assert.Equal("seen 12 min ago", English.Format("Observability_Seen_Minutes", 12));
        Assert.Equal("vista hace 12 min", Spanish.Format("Observability_Seen_Minutes", 12));
    }

    [Fact]
    public void The_short_origin_of_a_bar_and_the_status_of_the_terminal_come_from_the_resources()
    {
        Assert.Equal("exact", English.Get("MetricBar_ShortExact"));
        Assert.Equal("no data", English.Get("MetricBar_ShortMissing"));
        Assert.Equal("exacto", Spanish.Get("MetricBar_ShortExact"));
        Assert.Equal("seen ", English.Get("MetricBar_SeenPrefix"));

        Assert.Equal("pwsh · exited with code 3", English.Format("Terminal_ExitedWithCode", "pwsh", 3));
        Assert.Equal("pwsh · ha terminado con el código 3", Spanish.Format("Terminal_ExitedWithCode", "pwsh", 3));
        Assert.Equal("Iniciar", Spanish.Get("Terminal_Start"));
        Assert.Equal("Start", English.Get("Terminal_Start"));
    }

    /// <summary>
    /// D-48, F14-4: the Markdown viewer shows the journal as it was written, so what it makes of a text is the same whatever the
    /// language of the interface; nothing it draws is looked up in the resources.
    /// </summary>
    [Fact]
    public void What_the_markdown_viewer_draws_does_not_depend_on_the_language()
    {
        const string journal = "# Bitácora\n\n- 12:30 · Decisión: «aprobar el plan»\n  - Hecho: 3 cambios\n\nTexto con **negrita** y `código`.";
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            var inSpanish = (MarkdownLite.Parse(journal), MarkdownLite.ToPlainText(journal), JournalExcerpt.Of(journal, 20, 2000));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var inEnglish = (MarkdownLite.Parse(journal), MarkdownLite.ToPlainText(journal), JournalExcerpt.Of(journal, 20, 2000));

            Assert.Equal(inSpanish.Item2, inEnglish.Item2);
            Assert.Equal(inSpanish.Item3, inEnglish.Item3);
            Assert.Equal(inSpanish.Item1.Count, inEnglish.Item1.Count);
            Assert.Contains("Decisión: «aprobar el plan»", inEnglish.Item2, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
