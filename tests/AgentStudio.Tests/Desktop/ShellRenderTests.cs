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
        // docs/24 §3.1: the activity bar shows its names (188 px instead of 44): the documents keep at least 800 of 1280.
        Assert.True(documents >= 800, $"The documents get only {documents:F0} of 1280.");
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
            var welcome = new WelcomeDocumentViewModel(recents, command, command);
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

    /// <summary>
    /// docs/24 §3.2: with a project the welcome shows its next step, one card with one button and the sentence that says why,
    /// instead of «Abre la carpeta…» and instead of the list of every screen (the activity bar has them, with their names).
    /// </summary>
    [Fact]
    public void The_welcome_with_a_project_renders_offscreen_with_its_next_step()
    {
        var (start, project, card, buttons, action, reason, name) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var command = new RelayCommand(() => { });
            var welcome = new WelcomeDocumentViewModel([], command, command);
            welcome.ShowProject("calc", @"C:\proyectos\calc");
            welcome.NextStep = new NextStepViewModel(NextStepKind.SetUp, "Aún no has configurado este proyecto.", "Configurar el proyecto", command);
            var view = new WelcomeView { DataContext = welcome };

            var bitmap = OffscreenRender.Render(view, 900, 700);
            OffscreenRender.SaveIfRequested(bitmap, "ux-bienvenida-con-proyecto.png");
            var nextStep = (FrameworkElement)view.FindName("NextStepCard");
            var button = (Button)view.FindName("NextStepButton");
            return (
                ((FrameworkElement)view.FindName("StartPanel")).Visibility,
                ((FrameworkElement)view.FindName("ProjectPanel")).Visibility,
                nextStep.Visibility,
                Descendants<Button>(nextStep).Count(),
                System.Windows.Automation.AutomationProperties.GetName(button),
                ((TextBlock)view.FindName("NextStepReason")).Text,
                System.Windows.Automation.AutomationProperties.GetName((TextBlock)view.FindName("ProjectNameText")));
        });

        Assert.Equal(Visibility.Collapsed, start);
        Assert.Equal(Visibility.Visible, project);
        Assert.Equal(Visibility.Visible, card);
        Assert.Equal(1, buttons);
        Assert.Equal("Configurar el proyecto", action);
        Assert.Equal("Aún no has configurado este proyecto.", reason);

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
            // The panel contains the rows of the providers (docs/20 D-04): with no service of checks it shows them alone, in their section, which opens here.
            var doctor = new AgentStudio.Desktop.Doctor.DoctorViewModel(null, diagnostics, null, null, TimeProvider.System);
            await doctor.RefreshCommand.ExecuteAsync(null);
            doctor.Sections[0].IsExpanded = true;
            var view = new AutoDoctorView { DataContext = doctor };
            var bitmap = OffscreenRender.Render(view, 340, 400);
            OffscreenRender.SaveIfRequested(bitmap, "ux-auto-doctor.png");
            var list = Descendants<ItemsControl>(view).First(i => ReferenceEquals(i.ItemsSource, diagnostics.Providers));

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
