using System.Globalization;
using System.Windows;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// One language the inventory walks the screens in. Each takes a text provider of its own, injected for the screen being built
/// (<see cref="Texts.Scope"/>), since the language of the app can only be applied once and the culture of the tests is fixed by
/// <c>TestCulture</c>: nothing here depends on the machine.
/// </summary>
internal sealed record UiLanguageCase(string Name, Func<IUiTextProvider> Provider, bool Pseudo = false, bool English = false)
{
    public static UiLanguageCase Spanish { get; } = new("es", () => new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES")));

    public static UiLanguageCase EnglishCase { get; } = new("en", () => new ResourceTextProvider(CultureInfo.GetCultureInfo("en")), English: true);

    /// <summary>The Spanish texts, accented and 40 % longer (ADR-0054, addendum of Phase 14).</summary>
    public static UiLanguageCase Pseudolocalized { get; } = new(PseudoTextProvider.CultureName, () => new PseudoTextProvider(Spanish.Provider()), Pseudo: true);

    public static IReadOnlyList<UiLanguageCase> All { get; } = [Spanish, EnglishCase, Pseudolocalized];

    public override string ToString() => Name;
}

/// <summary>A screen built for the inventory: its root and, for a window, the window (its title is read as well).</summary>
internal sealed record UiBuilt(FrameworkElement Root, Window? Window = null);

/// <summary>
/// A screen the inventory walks: how to build it with synthetic data, and the size of window it is laid out at. A text of the
/// synthetic data that is written in one language (a title of a task, say) is listed in <see cref="DataTexts"/>, so the check of
/// the language does not take it for a text of the interface.
/// </summary>
internal sealed record UiScreen(string Name, Func<Task<UiBuilt>> Create, double Width = 1100, double Height = 760, IReadOnlyList<string>? DataTexts = null)
{
    public override string ToString() => Name;
}

/// <summary>What walking one screen in one language found.</summary>
internal sealed record UiScreenResult(string Screen, string Language, IReadOnlyList<string> Problems, IReadOnlySet<Type> TypesReached);

/// <summary>Walks every screen in every language once and keeps what it found, for every test of the inventory.</summary>
internal static class UiInventoryRun
{
    private static readonly Dictionary<string, Lazy<Task<IReadOnlyList<UiScreenResult>>>> Runs =
        UiLanguageCase.All.ToDictionary(l => l.Name, l => new Lazy<Task<IReadOnlyList<UiScreenResult>>>(() => WalkAsync(l)));

    public static Task<IReadOnlyList<UiScreenResult>> ResultsAsync(UiLanguageCase language) => Runs[language.Name].Value;

    private static async Task<IReadOnlyList<UiScreenResult>> WalkAsync(UiLanguageCase language)
    {
        var results = new List<UiScreenResult>();
        foreach (var screen in UiScreens.All)
        {
            results.Add(await WpfHost.RunAsync(() => WalkAsync(screen, language)));
        }

        return results;
    }

    private static async Task<UiScreenResult> WalkAsync(UiScreen screen, UiLanguageCase language)
    {
        var problems = new List<string>();
        var types = new HashSet<Type>();
        try
        {
            using var scope = Texts.Scope(language.Provider());
            Orchestrator.OrchestratorTheme.EnsureLoaded();
            var built = await screen.Create();
            var root = built.Root;
            UiTree.Lay(root, screen.Width, screen.Height);
            foreach (var element in UiTree.All(root))
            {
                types.Add(element.GetType());
            }

            if (built.Window is { } window)
            {
                types.Add(window.GetType());
                if (string.IsNullOrWhiteSpace(window.Title))
                {
                    problems.Add("window without title");
                }
            }

            var texts = UiChecks.VisibleTexts(root);
            if (built.Window is { Title.Length: > 0 } titled)
            {
                texts = [.. texts, (titled.GetType().Name + ".Title", titled.Title)];
            }

            // The names of the languages are written in their own language, never translated (ADR-0054), and the one of Windows too.
            var ignored = (screen.DataTexts ?? []).Concat(UiLanguages.All.Select(l => l.NativeName)).Append(CultureInfo.CurrentUICulture.NativeName).ToList();
            var language1 = texts.Where(t => !ignored.Any(d => t.Text.Contains(d, StringComparison.Ordinal))).ToList();
            problems.AddRange(UiChecks.MissingKeys(texts));
            problems.AddRange(UiChecks.Names(root, section => Texts.Current.Format("Info_Button_Name", section)));
            problems.AddRange(UiChecks.Keyboard(root));
            problems.AddRange(UiChecks.Cut(root));
            if (!language.Pseudo)
            {
                problems.AddRange(UiChecks.Language(language1, language.English));
            }
        }
        catch (Exception ex)
        {
            problems.Add($"the screen could not be built: {ex.GetType().Name}: {ex.Message}");
        }

        return new UiScreenResult(screen.Name, language.Name, problems.Distinct().ToList(), types);
    }
}
