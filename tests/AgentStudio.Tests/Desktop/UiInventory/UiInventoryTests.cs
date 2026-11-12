using System.IO;
using System.Text.RegularExpressions;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The inventory walks every screen under the language scope of each case (<c>Texts.Scope</c>), on the one dispatcher every WPF
/// test shares. WPF batches work of bindings and layout of several callers into pending operations that can run in the execution
/// context of whoever queued them: a screen of another test built meanwhile was painted in the inventory's English (seen with
/// <c>MainWindowQuickOpenTests</c>). So the inventory runs alone, after every parallel test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiInventoryCollection
{
    public const string Name = "UI inventory";
}

/// <summary>
/// F14-10 (docs/17 D-62): the automatic inventory of the interface. A walk of every screen of the app with synthetic data, off
/// screen, in Spanish, in English and in pseudolocalization (<see cref="UiLanguageCase"/>), that checks with the tree of layout
/// and UI Automation that:
/// <list type="bullet">
/// <item>every control that can be used has an accessible name, not empty and not the name of its type, and is reached with Tab;</item>
/// <item>every ⓘ is called «Información sobre …» (in its language) and explains its section; no text comes out as <c>[Key]</c>;</item>
/// <item>no text is cut: one that cannot wrap has <c>TextWrapping</c> or <c>TextTrimming</c> set or fits its room, read from the
/// layout and never from pixels, which would not be the same on the runner of the CI;</item>
/// <item>in English no text is in Spanish, and the other way round.</item>
/// </list>
/// The guard <see cref="Every_view_of_the_app_is_walked"/> makes a view that appears in <c>Views/**</c> or <c>Controls/**</c> and
/// is not reached by any screen fail the inventory.
/// </summary>
[Collection(UiInventoryCollection.Name)]
[Trait("Category", "UiInventory")]
public sealed partial class UiInventoryTests
{
    [GeneratedRegex(@"x:Class=""([^""]+)""")]
    private static partial Regex XamlClass();

    /// <summary>
    /// Views that no screen reaches, by full name, each with why. Nothing else may be left out of the walk, and a name that is
    /// reached after all fails too, so the list cannot go stale.
    /// </summary>
    private static readonly Dictionary<string, string> NotWalked = new(StringComparer.Ordinal);

    [Fact]
    public Task The_spanish_interface_passes_the_inventory() => AssertCleanAsync(UiLanguageCase.Spanish);

    [Fact]
    public Task The_english_interface_passes_the_inventory() => AssertCleanAsync(UiLanguageCase.EnglishCase);

    [Fact]
    public Task The_pseudolocalized_interface_passes_the_inventory() => AssertCleanAsync(UiLanguageCase.Pseudolocalized);

    [Fact]
    public async Task Every_view_of_the_app_is_walked()
    {
        var reached = (await UiInventoryRun.ResultsAsync(UiLanguageCase.Spanish)).SelectMany(r => r.TypesReached).Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);
        var views = ViewClasses();

        var missing = views.Where(v => !reached.Contains(v) && !NotWalked.ContainsKey(v)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "No screen of the inventory reaches these views; add a screen in UiScreens that shows each, or list it in NotWalked with why: " + string.Join(", ", missing));

        var stale = NotWalked.Keys.Where(reached.Contains).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "These views are walked now, take them out of NotWalked: " + string.Join(", ", stale));
        Assert.Subset(views.ToHashSet(StringComparer.Ordinal), NotWalked.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_screen_is_built_in_the_three_languages()
    {
        foreach (var language in UiLanguageCase.All)
        {
            var results = await UiInventoryRun.ResultsAsync(language);
            Assert.Equal(UiScreens.All.Select(s => s.Name), results.Select(r => r.Screen));
            var broken = results.Where(r => r.Problems.Any(p => p.StartsWith("the screen could not be built", StringComparison.Ordinal))).Select(r => r.Screen).ToList();
            Assert.True(broken.Count == 0, $"[{language}] could not be built: " + string.Join(", ", broken));
        }
    }

    private static async Task AssertCleanAsync(UiLanguageCase language)
    {
        var results = await UiInventoryRun.ResultsAsync(language);
        var lines = results.SelectMany(r => r.Problems.Select(p => $"[{language}] {r.Screen}: {p}")).ToList();
        Assert.True(lines.Count == 0, $"{lines.Count} problems:{Environment.NewLine}" + string.Join(Environment.NewLine, lines));
    }

    /// <summary>The classes of the XAML of <c>Views/**</c>, <c>Controls/**</c> and the main window.</summary>
    private static List<string> ViewClasses() =>
    [
        .. new[] { "Views", "Controls" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(DesktopRoot, folder), "*.xaml", SearchOption.AllDirectories))
            .Append(Path.Combine(DesktopRoot, "MainWindow.xaml"))
            .Select(f => XamlClass().Match(File.ReadAllText(f)))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal),
    ];
}
