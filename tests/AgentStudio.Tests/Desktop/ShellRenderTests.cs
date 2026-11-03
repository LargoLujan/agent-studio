using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Tests.Desktop.Orchestrator;
using AvalonDock;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The shell drawn without ever being shown (<see cref="OffscreenRender"/>): the main window with a project open, the welcome
/// without one, and Auto Doctor, compact, with Gemini parked. No window, no HWND, nothing on the user's screen; synthetic data
/// only. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved there.
/// </summary>
public sealed class ShellRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Neutral = Color.FromRgb(0x5F, 0x73, 0x96);

    private static IProviderProbe[] Providers() =>
    [
        new StubProbe("claude-code", InstallationState.Installed, AuthenticationState.Authenticated),
        new StubProbe("codex-cli", InstallationState.Installed, AuthenticationState.Authenticated),
        new StubProbe("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null),
    ];

    [Fact]
    public async Task The_main_window_with_a_project_renders_offscreen_with_the_width_for_the_documents_and_a_green_summary()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        temp.WriteFile("calc/src/Calc.cs", "public static class Calc { }");
        temp.WriteFile("calc/README.md", "# Calc");

        var (drawn, green, orange, documents) = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var vm = ShellFactory.CreateMain(null, null, null, Providers());
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            await vm.InitializeAsync();
            await vm.OpenProjectAsync(project);
            await vm.Diagnostics.RefreshCommand.ExecuteAsync(null);
            vm.ShowWelcomeCommand.Execute(null);
            var dock = (DockingManager)window.FindName("DockManager");
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, dock));

            var bitmap = OffscreenRender.Render((FrameworkElement)window.Content, 1280, 800);
            OffscreenRender.SaveIfRequested(bitmap, "ux-ventana-con-proyecto.png");
            var pane = Descendants<AvalonDock.Controls.LayoutDocumentPaneControl>(dock).Single();
            return (OffscreenRender.CountUnlike(bitmap, Background), OffscreenRender.Count(bitmap, Success), OffscreenRender.Count(bitmap, Warning), pane.ActualWidth);
        });

        Assert.True(drawn > 150_000, $"Only {drawn} pixels differ from the background: the window was not drawn.");
        Assert.True(green > 40, $"Only {green} pixels are green: the provider summary is not (Gemini, parked, does not count).");
        Assert.True(orange < 10, $"{orange} pixels are orange: nothing should warn with Claude Code and Codex ready.");
        Assert.True(documents >= 900, $"The documents get only {documents:F0} of 1280.");
    }

    /// <summary>V6: without a project the welcome offers to open one, and the name and path of a recent project start at the left of its row.</summary>
    [Fact]
    public void The_welcome_without_a_project_renders_offscreen_with_its_recent_projects_aligned_to_the_left()
    {
        var (drawn, offsets, withProject) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var command = new RelayCommand(() => { });
            var recents = new ObservableCollection<RecentProjectItem>
            {
                new(@"C:\proyectos\calc", "calc", @"C:\proyectos\calc"),
                new(@"C:\proyectos\agenda-de-citas", "agenda-de-citas", @"C:\proyectos\agenda-de-citas"),
            };
            var welcome = new WelcomeDocumentViewModel(recents, command, command, [new("Orquestador", "Planifica.", null, command)]);
            var view = new WelcomeView { DataContext = welcome };

            var bitmap = OffscreenRender.Render(view, 900, 600);
            OffscreenRender.SaveIfRequested(bitmap, "ux-bienvenida-sin-proyecto.png");
            var rows = Descendants<Button>((DependencyObject)view.FindName("RecentProjectsList")).ToList();
            var left = rows.Select(row =>
            {
                var name = Descendants<TextBlock>(row).First();
                return name.TranslatePoint(new Point(0, 0), row).X;
            }).ToList();
            return (OffscreenRender.CountUnlike(bitmap, Background), left, ((FrameworkElement)view.FindName("ProjectPanel")).Visibility);
        });

        Assert.True(drawn > 10_000, $"Only {drawn} pixels differ from the background: the welcome was not drawn.");
        Assert.Equal(2, offsets.Count);
        Assert.All(offsets, x => Assert.True(x < 20, $"The name starts {x:F0} from the left of its row: it is not aligned to the left."));
        Assert.Equal(Visibility.Collapsed, withProject);
    }

    /// <summary>V6: with a project the welcome offers the ways into it, each with its shortcut, instead of «Abre la carpeta…».</summary>
    [Fact]
    public void The_welcome_with_a_project_renders_offscreen_with_the_ways_into_it()
    {
        var (start, project, rows, shortcuts, name) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var command = new RelayCommand(() => { });
            var welcome = new WelcomeDocumentViewModel(
                [],
                command,
                command,
                [
                    new("Orquestador", "Planifica una petición con agentes.", null, command),
                    new("Ir al fichero", "Busca un fichero del proyecto.", "Ctrl+P", command),
                    new("Debates", "Contrasta una decisión.", "Ctrl+Mayús+D", command),
                ]);
            welcome.ShowProject("calc", @"C:\proyectos\calc");
            var view = new WelcomeView { DataContext = welcome };

            var bitmap = OffscreenRender.Render(view, 900, 700);
            OffscreenRender.SaveIfRequested(bitmap, "ux-bienvenida-con-proyecto.png");
            var list = (ItemsControl)view.FindName("ProjectActionsList");
            return (
                ((FrameworkElement)view.FindName("StartPanel")).Visibility,
                ((FrameworkElement)view.FindName("ProjectPanel")).Visibility,
                Descendants<Button>(list).Count(),
                Descendants<TextBlock>(list).Select(t => t.Text).Where(t => t.StartsWith("Ctrl", StringComparison.Ordinal)).ToList(),
                System.Windows.Automation.AutomationProperties.GetName((TextBlock)view.FindName("ProjectNameText")));
        });

        Assert.Equal(Visibility.Collapsed, start);
        Assert.Equal(Visibility.Visible, project);
        Assert.Equal(3, rows);
        Assert.Equal(["Ctrl+P", "Ctrl+Mayús+D"], shortcuts);

        // A screen reader says which project is open, not only that one is.
        Assert.Equal("Proyecto abierto: calc", name);
    }

    /// <summary>
    /// The user's request: the providers took too much room. One row per provider; Gemini, parked, in a neutral row (no orange),
    /// with what its check found folded under its details.
    /// </summary>
    [Fact]
    public async Task Auto_Doctor_renders_offscreen_compact_with_Gemini_parked_and_neutral()
    {
        var (drawn, orange, neutral, green, height, summaries) = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var diagnostics = new ProviderDiagnosticsViewModel(new ProviderDiagnostics(Providers()), TimeProvider.System);
            await diagnostics.RefreshCommand.ExecuteAsync(null);
            var view = new AutoDoctorView { DataContext = diagnostics };
            var bitmap = OffscreenRender.Render(view, 340, 400);
            OffscreenRender.SaveIfRequested(bitmap, "ux-auto-doctor.png");
            var list = (ItemsControl)view.FindName("ProviderList");

            // What is laid out: the details of each row are folded, so their texts are not.
            return (
                OffscreenRender.CountUnlike(bitmap, Surface),
                OffscreenRender.Count(bitmap, Warning),
                OffscreenRender.Count(bitmap, Neutral),
                OffscreenRender.Count(bitmap, Success),
                list.ActualHeight,
                Descendants<TextBlock>(list).Where(t => t.ActualWidth > 0).Select(t => t.Text).ToList());
        });

        Assert.True(drawn > 3_000, $"Only {drawn} pixels differ from the panel: Auto Doctor was not drawn.");
        Assert.True(orange < 10, $"{orange} pixels are orange: a parked provider is not a warning.");
        Assert.True(neutral > 40, $"Only {neutral} pixels have the neutral colour: the dot of Gemini is not neutral.");
        Assert.True(green > 80, $"Only {green} pixels are green: the dots of Claude Code and Codex are not.");
        Assert.True(height < 140, $"Three providers take {height:F0} px: the rows are not compact.");
        Assert.Contains("Aparcado", summaries);
        Assert.Contains("1.0 · Sesión iniciada", summaries);
        Assert.DoesNotContain(ProviderStatusItem.GeminiParkedNote, summaries);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
