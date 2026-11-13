using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The texts of the tray, its panel, the exit question and «Trabajo desatendido» (docs/17 D-47, D-48, D-51 and D-54; F14-1, F14-3,
/// F14-4 and F14-6; ADR-0054): every name an enumeration gives to the screen exists in Spanish of Spain and in English (the test
/// of keys cannot see a key made of a prefix and a value), the plurals have both forms, the English texts are English, the ⓘ has
/// its three texts, and what goes to the decision log does not change with the language of the interface.
/// </summary>
public sealed partial class BackgroundStringsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"InfoKey=""(Info_[A-Za-z]+)""")]
    private static partial Regex InfoKey();

    [Fact]
    public void Every_name_an_enumeration_gives_to_the_tray_and_its_panel_exists_in_both_languages()
    {
        var keys = new List<string>();
        // A remote operation of Agent Studio itself (docs/21 D-25) is not shown as work in the tray: ProjectActivityWork ignores it.
        keys.AddRange(Enum.GetNames<ProjectActivityKind>().Where(n => n != nameof(ProjectActivityKind.RemoteOperation)).Select(n => "Background_Work_Activity_" + n));
        foreach (var status in Enum.GetNames<WorkTaskStatus>())
        {
            keys.Add($"Background_Panel_TaskCount_{status}_One");
            keys.Add($"Background_Panel_TaskCount_{status}_Other");
        }

        keys.AddRange(new[] { "Succeeded", "Failed", "Cancelled", "Blocked", "Interrupted" }.Select(n => "Background_Tray_RunEnd_" + n));

        var missing = keys.Where(k => !Spanish.Has(k) || !English.Has(k) || English.Get(k).StartsWith('[')).ToList();
        Assert.True(missing.Count == 0, "Names without a text:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void A_plural_has_its_one_and_its_many_in_both_languages_with_the_number_in_place()
    {
        var plurals = new[]
        {
            "Background_Exit_IntroWindow", "Background_Exit_IntroExit", "Background_Count_Work", "Background_Count_Attempt", "Background_Count_Run",
            "Background_Count_Debate", "Background_Count_Audit", "Background_Count_Planning", "Background_Count_Paused", "Background_Count_Blocked",
            "Background_Tooltip_ExitAfter", "Background_Tooltip_Person", "Background_Tray_LockedWork", "Background_Panel_Locked_Waiting",
            "Background_Panel_Budget_NoHeader", "Background_Panel_TokensNote",
        };

        foreach (var key in plurals)
        {
            foreach (var provider in new[] { Spanish, English })
            {
                Assert.Contains("1", provider.Format(key + "_One", 1), StringComparison.Ordinal);
                Assert.Contains("3", provider.Format(key + "_Other", 3), StringComparison.Ordinal);
            }
        }

        Assert.Equal("1 intento en curso", Spanish.Format("Background_Count_Attempt_One", 1));
        Assert.Equal("3 attempts in progress", English.Format("Background_Count_Attempt_Other", 3));
    }

    [Fact]
    public void The_english_texts_have_no_spanish_letters_or_signs()
    {
        var untranslated = new List<string>();
        foreach (var baseName in new[] { "BackgroundStrings", "LockStrings" })
        {
            foreach (var (key, text) in Read(ResourceFiles()[baseName].Satellites["en"]))
            {
                if (text.Any(c => "áéíóúüñÁÉÍÓÚÑ¿¡«»".Contains(c, StringComparison.Ordinal)))
                {
                    untranslated.Add($"{baseName}.{key}: {text}");
                }
            }
        }

        Assert.True(untranslated.Count == 0, "English texts with Spanish letters or signs:\n" + string.Join('\n', untranslated));
    }

    /// <summary>The glossary of the guide (ADR-0054, point 9): a text of these areas never says «evidencia», it says «prueba guardada».</summary>
    [Fact]
    public void The_texts_follow_the_glossary()
    {
        foreach (var baseName in new[] { "BackgroundStrings", "LockStrings" })
        {
            foreach (var (key, text) in Read(ResourceFiles()[baseName].Neutral))
            {
                Assert.DoesNotContain("evidencia", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("worktree", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("Info_Unattended", "Views/Background/UnattendedSettingsView.xaml")]
    [InlineData("Info_Lock", "Views/Lock/LockSettingsView.xaml")]
    public void Each_information_button_has_its_title_what_it_is_and_its_actions_in_both_languages(string section, string view)
    {
        Assert.Contains(section, InfoKey().Matches(File.ReadAllText(Path.Combine(DesktopRoot, view))).Select(m => m.Groups[1].Value));

        foreach (var part in new[] { "_Title", "_What", "_Actions" })
        {
            Assert.True(Spanish.Has(section + part), section + part + " (es)");
            Assert.True(English.Has(section + part), section + part + " (en)");
        }

        Assert.NotEqual(Spanish.Get(section + "_What"), English.Get(section + "_What"));
        Assert.True(Spanish.Get(section + "_Actions").Split('\n').Length >= 3, section + " lists its actions, one per line");

        // D-52: no sentence of an explanation has more than 20 words.
        foreach (var provider in new[] { Spanish, English })
        {
            foreach (var part in new[] { "_What", "_Actions" })
            {
                foreach (var sentence in Regex.Split(provider.Get(section + part), @"(?<=[.!?:])\s+|\n"))
                {
                    Assert.True(sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 20, $"{section}{part}: {sentence}");
                }
            }
        }
    }

    /// <summary>
    /// D-48 and F14-4: the summary of the entry of the exit in the decision log is history. It is built with the interface in
    /// Spanish and in English and comes out the same, and it keeps the words the entry was first written with.
    /// </summary>
    [Fact]
    public void The_summary_of_the_exit_in_the_decision_log_is_the_same_in_every_language()
    {
        var expected = new Dictionary<RunExitChoice, string>
        {
            [RunExitChoice.PauseAndExitWhenIdle] = "Pausar y salir cuando terminen los intentos en curso",
            [RunExitChoice.ExitNow] = "Salir ahora y dejarla interrumpida",
            [RunExitChoice.SessionEnded] = "Windows cerró la sesión: cuenta como salir ahora",
        };

        var before = (CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            foreach (var name in new[] { "es-ES", "en" })
            {
                var culture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                foreach (var (choice, text) in expected)
                {
                    Assert.Equal(text, DecisionLogRunExitRecorder.Summary(choice));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = before.CurrentUICulture;
            CultureInfo.DefaultThreadCurrentUICulture = before.DefaultThreadCurrentUICulture;
        }

        // The dialog says it in the words of the guide, in the language in use; the log does not follow it.
        Assert.NotEqual(DecisionLogRunExitRecorder.Summary(RunExitChoice.ExitNow), Spanish.Get("Background_Exit_Now"));
        Assert.NotEqual(DecisionLogRunExitRecorder.Summary(RunExitChoice.ExitNow), English.Get("Background_Exit_Now"));
    }
}
