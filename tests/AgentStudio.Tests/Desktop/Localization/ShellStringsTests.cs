using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// The texts of the shell (Phase 14, WP-14A part d1; ADR-0054): the glossary of the guide is applied in the first level, the
/// two languages say the same thing with the same placeholders, and «Idioma» has its ⓘ.
/// </summary>
public sealed partial class ShellStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    /// <summary>Terms the guide keeps out of the first level (ADR-0054, point 9), by language.</summary>
    private static readonly string[] BannedSpanish = ["worktree", "hash", "sha-256", "payload", "proveedor", "cuota", "dossier", "evidencia", "append-only", "commit head"];

    private static readonly string[] BannedEnglish = ["worktree", "hash", "sha-256", "payload", "provider", "quota", "dossier", "append-only", "commit head"];

    private static IReadOnlyDictionary<string, string> ShellKeys(string language = "") =>
        Read(Path.Combine(ResourcesFolder, language.Length == 0 ? "ShellStrings.resx" : $"ShellStrings.{language}.resx"));

    [GeneratedRegex("""\{\d+(?:,[^}:]*)?(?::[^}]*)?\}""")]
    private static partial Regex Placeholder();

    [Fact]
    public void The_shell_has_its_texts_in_both_languages_with_the_same_placeholders()
    {
        var es = ShellKeys();
        var en = ShellKeys("en");

        Assert.True(es.Count > 150);
        Assert.Equal(es.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, text) in es)
        {
            Assert.Equal(
                Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal),
                Placeholder().Matches(en[key]).Select(m => m.Value).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_texts_of_the_first_level_use_the_words_of_the_glossary()
    {
        foreach (var (key, text) in ShellKeys())
        {
            Assert.DoesNotContain(BannedSpanish, term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.False(Regex.IsMatch(text, @"\bjuez\b", RegexOptions.IgnoreCase) && !text.Contains("(juez)", StringComparison.OrdinalIgnoreCase), $"{key}: «juez» alone; the glossary says «revisor independiente (juez)».");
        }

        foreach (var (key, text) in ShellKeys("en"))
        {
            Assert.DoesNotContain(BannedEnglish, term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.False(Regex.IsMatch(text, @"\bjudge\b", RegexOptions.IgnoreCase) && !text.Contains("(judge)", StringComparison.OrdinalIgnoreCase), $"{key}: «judge» alone.");
        }
    }

    [Fact]
    public void A_sentence_of_the_shell_is_at_most_twenty_words_in_spanish()
    {
        foreach (var (key, text) in ShellKeys())
        {
            foreach (var sentence in Regex.Split(text, @"(?<=[.!?:])\s+|\n"))
            {
                var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                Assert.True(words <= 20, $"{key}: {words} words in «{sentence}».");
            }
        }
    }

    [Fact]
    public void The_shortcuts_follow_the_language_and_the_status_texts_follow_it_too()
    {
        Assert.Equal("Ctrl+Mayús+D", Spanish.Get("Main_Shortcut_Debates"));
        Assert.Equal("Ctrl+Shift+D", English.Get("Main_Shortcut_Debates"));
        Assert.Equal("Programas: 1 de 2 listos", Spanish.Format("Providers_Summary_Ready", 1, 2));
        Assert.Equal("Programs: 1 of 2 ready", English.Format("Providers_Summary_Ready", 1, 2));
        Assert.Equal("Last check: 14:05:09", English.Format("Providers_LastChecked", new DateTime(2026, 11, 11, 14, 5, 9)));
    }

    [Fact]
    public void Idioma_in_ajustes_has_its_information_button()
    {
        var xaml = File.ReadAllText(Path.Combine(DesktopRoot, "Views", "Settings", "LanguageSettingsView.xaml"));

        Assert.Contains("InfoKey=\"Info_Language\"", xaml, StringComparison.Ordinal);
        foreach (var provider in new[] { Spanish, English })
        {
            Assert.True(provider.Has("Info_Language_Title"));
            Assert.True(provider.Has("Info_Language_What"));
            Assert.True(provider.Has("Info_Language_Actions"));
        }
    }
}
