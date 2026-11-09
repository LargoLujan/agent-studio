using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Panels;

/// <summary>
/// F13-19 and D-54: every screen WP8 adds has its ⓘ, with its three texts in Spanish and in English. The sections are read from the XAML
/// of the screens (<c>InfoKey="…"</c>), so a screen with a button and no text, or a text no screen uses, is found.
/// </summary>
public sealed partial class InfoTextsTests
{
    private static readonly string[] Sections = ["Info_Monitor", "Info_Question", "Info_Consumption", "Info_Problems", "Info_Output", "Info_Tests", "Info_Tools"];

    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex("InfoKey=.(Info_[A-Za-z]+).")]
    private static partial Regex InfoKey();

    [Fact]
    public void Each_section_has_its_title_what_it_is_and_its_actions_in_both_languages()
    {
        foreach (var section in Sections)
        {
            foreach (var part in new[] { "_Title", "_What", "_Actions" })
            {
                Assert.True(Spanish.Has(section + part), section + part + " (es)");
                Assert.True(English.Has(section + part), section + part + " (en)");
                Assert.False(Spanish.Get(section + part).StartsWith('['), section + part);
                Assert.NotEqual(Spanish.Get(section + part), English.Get(section + part));
            }
        }
    }

    [Fact]
    public void Each_screen_of_the_package_uses_its_button_and_each_button_has_its_texts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentStudio.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var found = new[] { "Orchestrator/RunMonitorView.xaml", "Orchestrator/Questions/QuestionView.xaml", "Observability/Analysis/ConsumptionAnalysisView.xaml", "Panels/ProblemsPanelView.xaml", "Panels/OutputPanelView.xaml", "Panels/TestsPanelView.xaml", "Tools/ToolsView.xaml" }
            .Select(f => File.ReadAllText(Path.Combine(root.FullName, "src", "AgentStudio.Desktop", "Views", f)))
            .SelectMany(text => InfoKey().Matches(text).Select(m => m.Groups[1].Value))
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(Sections.Order(), found);
    }
}
