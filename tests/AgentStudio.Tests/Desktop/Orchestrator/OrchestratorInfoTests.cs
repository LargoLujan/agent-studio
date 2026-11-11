using System.Globalization;
using System.IO;
using AgentStudio.Desktop.Localization;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// F14-6 and D-54 for the screens of the orchestrator (WP-14A (a1)): each section of the list that falls on them has its ⓘ in the
/// XAML of its screen, and the three texts of its key (title, what it is and what can be done) in Spanish of Spain and in
/// English, the English one not a copy of the Spanish one and with the same number of actions.
/// </summary>
public sealed class OrchestratorInfoTests
{
    /// <summary>The sections of D-54 on these screens: the screen that shows the button and its key.</summary>
    private static readonly (string Screen, string Key)[] Buttons =
    [
        ("PreparationView.xaml", "Info_Preparation"),
        ("PreparationView.xaml", "Info_Requirements"),
        ("PreparationView.xaml", "Info_Roster"),
        ("PreparationView.xaml", "Info_VerificationProfile"),
        ("CanvasView.xaml", "Info_Canvas_Plan"),
        ("OrchestratorView.xaml", "Info_ResumeConsent"),
        ("RunMonitorView.xaml", "Info_Monitor_Context"),
        ("ApprovalView.xaml", "Info_Approval"),
    ];

    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void Each_section_of_the_list_that_falls_on_the_orchestrator_has_its_button_on_its_screen()
    {
        foreach (var (screen, key) in Buttons)
        {
            var xaml = File.ReadAllText(Path.Combine(DesktopRoot, "Views", "Orchestrator", screen));
            Assert.Contains($"InfoKey=\"{key}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_button_has_its_three_texts_in_both_languages_with_the_same_actions()
    {
        foreach (var (_, key) in Buttons)
        {
            foreach (var part in new[] { "_Title", "_What", "_Actions" })
            {
                Assert.True(Spanish.Has(key + part), key + part);
                Assert.False(string.IsNullOrWhiteSpace(Spanish.Get(key + part)), key + part + " (es)");
                Assert.False(string.IsNullOrWhiteSpace(English.Get(key + part)), key + part + " (en)");
                Assert.NotEqual(Spanish.Get(key + part), English.Get(key + part));
            }

            var spanishActions = Spanish.Get(key + "_Actions").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var englishActions = English.Get(key + "_Actions").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(spanishActions.Length >= 2, key);
            Assert.Equal(spanishActions.Length, englishActions.Length);
        }
    }

    /// <summary>The text of the screens is the one of the area files, in both languages, with the same keys.</summary>
    [Fact]
    public void The_orchestrator_texts_exist_in_both_languages_and_the_language_of_the_interface_changes_what_the_screen_says()
    {
        Assert.Equal("Aprobar el plan", Spanish.Get("Orchestrator_Review_Approve"));
        Assert.Equal("Approve the plan", English.Get("Orchestrator_Review_Approve"));
        Assert.Equal("Una acción necesita tu aprobación", Spanish.Get("Orchestrator_ApprovalTitle_Unknown"));
        Assert.Equal("An action needs your approval", English.Get("Orchestrator_ApprovalTitle_Unknown"));
        Assert.Equal("La tarea T2 no tiene título.", Spanish.Format("Orchestrator_Violation_TitleRequired", "T2"));
        Assert.Equal("Task T2 has no title.", English.Format("Orchestrator_Violation_TitleRequired", "T2"));
    }
}
