using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Setup;

/// <summary>
/// F13-19 for the configurator (docs/17 D-47 and D-51): the area file of its texts has every key in both languages, and every
/// word the layers below say as a code or as an enum has its text, so a new step, size, outcome or code cannot come without it.
/// </summary>
public sealed partial class SetupStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex("\"(setup\\.[a-z0-9\\-.]+)\"")]
    private static partial Regex CodeLiteral();

    private static IEnumerable<string> EnumKeys() =>
        Enum.GetValues<SetupStep>().Select(v => "Setup_Step_" + v)
            .Concat(Enum.GetValues<SetupSize>().Select(v => "Setup_Size_" + v))
            .Concat(Enum.GetValues<AgentRole>().Select(v => "Setup_Role_" + v))
            .Concat(Enum.GetValues<SetupPartOutcome>().Select(v => "Setup_Outcome_" + v));

    [Fact]
    public void The_area_file_exists_with_its_english_satellite_and_the_same_keys_and_placeholders()
    {
        var files = ResourceFiles();

        Assert.True(files.ContainsKey("SetupStrings"));
        var (neutral, satellites) = files["SetupStrings"];
        var source = Read(neutral);
        var english = Read(satellites["en"]);
        Assert.Equal(source.Keys.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        Assert.All(source.Keys, key =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.Equal(Placeholders(source[key]), Placeholders(english[key]));
        });

        // Every key is the configurator's: Setup_ for what the screen names, Text_setup_ and Text_model_ for the coded texts.
        Assert.All(source.Keys, key => Assert.True(key.StartsWith("Setup_", StringComparison.Ordinal) || key.StartsWith("Text_setup_", StringComparison.Ordinal), key));
    }

    [Fact]
    public void Every_enum_the_screen_says_in_words_has_its_text_in_both_languages()
    {
        foreach (var key in EnumKeys())
        {
            Assert.True(Spanish.Has(key), key);
            Assert.True(English.Has(key), key);
        }

        Assert.NotEqual(Spanish.Get("Setup_Step_Project"), English.Get("Setup_Step_Project"));
    }

    [Fact]
    public void Every_code_the_application_layer_emits_has_its_coded_text_in_both_languages()
    {
        var folder = Path.Combine(RepositoryRoot, "src", "AgentStudio.Application", "Setup");
        var codes = Directory.GetFiles(folder, "*.cs")
            .SelectMany(f => CodeLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(UserText.IsCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(codes.Count >= 15, "The scan found too few codes: " + codes.Count);
        foreach (var code in codes)
        {
            // The sections of the skeleton are built from the stack: setup.brief.section.stack-<stack>.
            var key = UserText.KeyOf(code);
            Assert.True(Spanish.Has(key), key);
            Assert.True(English.Has(key), key);
        }

        foreach (var stack in new[] { SetupStacks.DotNet, SetupStacks.Node, SetupStacks.Python })
        {
            Assert.All(SetupProposer.BriefSections(stack), code => Assert.True(Spanish.Has(UserText.KeyOf(code)), code));
        }
    }

    [Fact]
    public void The_text_that_names_the_level_and_the_effort_of_a_model_is_in_the_models_file_and_not_here()
    {
        var (neutral, _) = ResourceFiles()["SetupStrings"];

        Assert.DoesNotContain(Read(neutral).Keys, k => k.StartsWith("Models_", StringComparison.Ordinal));
        Assert.True(Spanish.Has("Models_Effort_CliDefault"));
        Assert.True(Spanish.Has("Models_Tier_Maximum"));
    }

    [Fact]
    public void The_screen_has_its_information_button_with_its_three_texts_in_both_languages()
    {
        foreach (var provider in new[] { Spanish, English })
        {
            Assert.True(provider.Has("Setup_Info_Title"));
            Assert.True(provider.Has("Setup_Info_What"));
            Assert.True(provider.Has("Setup_Info_Actions"));
        }

        Assert.Contains("InfoKey=\"Setup_Info\"", File.ReadAllText(Path.Combine(DesktopRoot, "Views", "Setup", "SetupView.xaml")), StringComparison.Ordinal);
        Assert.True(Spanish.Get("Setup_Info_Actions").Split('\n').Length >= 3);
    }

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));
}
