using System.Windows;
using AgentStudio.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The orchestrator theme in the shared test application, set up as the app does it: the dictionaries App.xaml merges
/// after the tokens, in the same order, and Nodify's colour keys repainted from the tokens (MainWindow,
/// <see cref="NodifyThemeTokens"/>). <see cref="WpfHost"/> only loads Dark.xaml and the document templates, so every test
/// that builds a canvas or reads the canvas colours calls <see cref="EnsureLoaded"/> first, inside <c>WpfHost.Run</c>.
/// </summary>
internal static class OrchestratorTheme
{
    /// <summary>What App.xaml merges after Themes/Dark.xaml, in its order (the order matters: see Themes/Nodify.xaml).</summary>
    public static IReadOnlyList<string> Sources { get; } =
    [
        "pack://application:,,,/Nodify;component/Themes/Dark.xaml",
        "pack://application:,,,/Nodify;component/Themes/FocusVisual.xaml",
        "pack://application:,,,/AgentStudio.Desktop;component/Themes/Nodify.xaml",
    ];

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        var resources = System.Windows.Application.Current.Resources;
        foreach (var source in Sources)
        {
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source) });
        }

        NodifyThemeTokens.Apply(resources, resources);
        _loaded = true;
    }
}
