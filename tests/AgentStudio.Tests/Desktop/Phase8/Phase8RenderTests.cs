using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// What the integration of Phase 8 adds to the screens, drawn without ever being shown (<see cref="OffscreenRender"/>): the
/// consent of the next run next to «Ejecutar», «Trabajo desatendido» in «Ajustes» (a section of its own next to «Windows Hello
/// y bloqueo», with the one «Guardar» of the document), and the main window locked, with the lock
/// screen in place of its content. No window, no HWND, nothing on the user's screen; synthetic data only (no path or account of
/// the user). With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved for docs/evidencias.
/// </summary>
public sealed class Phase8RenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_consent_of_the_next_run_renders_offscreen_next_to_ejecutar()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var engine = new FakeOrchestrator();
            var plan = PlanFixtures.Plan();
            engine.Plan = (_, _, _) => Task.FromResult(new PlanProposal(new RunId("20261021-c4a1f2"), plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
            var model = new OrchestratorDocumentViewModel(@"C:\proyectos\calc", new OrchestrationSession(Backends.Complete(engine)), new NoCanvasLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
            await model.OpenAsync();
            model.IsPreparationExpanded = false;
            model.RequestText = "Añadir Calc.Multiply(a, b) con sus pruebas";
            await model.RequestPlanCommand.ExecuteAsync(null);
            model.ApproveCommand.Execute(null);
            model.AutoResumeConsent = true;
            return model;
        });

        var (drawn, checkedBox, laidOut, executes) = WpfHost.Run(() =>
        {
            var view = new OrchestratorView { DataContext = document };
            var bitmap = OffscreenRender.Render(view, 1500, 980);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-consentimiento.png");
            var box = Descendants<CheckBox>(view).Single(c => Equals(c.Content, OrchestratorDocumentViewModel.ResumeConsentLabel));
            return (OffscreenRender.CountUnlike(bitmap, Background), box.IsChecked == true && box.IsEnabled, box.ActualWidth > 100, document.ExecuteCommand.CanExecute(null));
        });

        Assert.True(drawn > 100_000, $"Only {drawn} pixels differ from the background.");
        Assert.True(checkedBox, "The box is marked and can be changed before «Ejecutar».");
        Assert.True(laidOut, "The box was laid out with its label.");
        Assert.True(executes);
    }

    [Fact]
    public void The_unattended_settings_render_offscreen_in_ajustes_next_to_the_lock_with_one_save()
    {
        var (drawn, accent, radios, saves, sections) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var store = new InMemorySettingsStore(AppSettings.Default with { PreventSleepWhileWorking = true, IdleLockEnabled = true });
            var unattended = new UnattendedSettingsViewModel(store, new LockSettingsViewModel(store));
            var document = new AgentStudio.Desktop.Settings.SettingsDocumentViewModel(store, new AgentStudio.Desktop.Observability.ObservabilitySettingsViewModel(store), unattended);
            var view = new AgentStudio.Desktop.Views.Settings.SettingsView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 900, 1240);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-ajustes-trabajo-desatendido.png");
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                OffscreenRender.Count(bitmap, Accent),
                Descendants<RadioButton>(view).Count(r => r.IsChecked == true),
                Descendants<Button>(view).Count(b => Equals(b.Content, "Guardar ajustes")),
                Descendants<UnattendedSettingsView>(view).Count(v => v.ActualHeight > 100) + Descendants<AgentStudio.Desktop.Views.Lock.LockSettingsView>(view).Count(v => v.ActualHeight > 100));
        });

        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the background: the sections were not drawn.");
        Assert.True(accent > 300, $"Only {accent} pixels have the accent colour: «Guardar» was not drawn.");
        Assert.Equal(1, radios);
        Assert.Equal(1, saves);
        Assert.Equal(2, sections);
    }

    [Fact]
    public void The_question_about_unsaved_edits_renders_offscreen_with_its_three_answers()
    {
        var (drawn, accent, buttons) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new AgentStudio.Desktop.Views.UnsavedChangesView
            {
                DataContext = new UnsavedChangesPromptViewModel(["Calc.cs", "CalcTests.cs", "README.md"], canSave: true),
            };

            var bitmap = OffscreenRender.Render(view, 520, 300);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-cambios-sin-guardar.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Accent), Descendants<Button>(view).Count(b => b.Visibility == Visibility.Visible));
        });

        Assert.True(drawn > 5_000, $"Only {drawn} pixels differ from the panel: the question was not drawn.");
        Assert.True(accent > 300, $"Only {accent} pixels have the accent colour: «Cancelar» was not drawn.");
        Assert.Equal(3, buttons);
    }

    [Fact]
    public async Task The_locked_main_window_renders_offscreen_with_the_lock_screen_in_place_of_its_content()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(T0);
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), clock);
        await interfaceLock.LockAsync(LockReason.Manual, Ct);
        _ = interfaceLock.WaitUntilUnlockedAsync(
            new ApprovalRequest("deliver-integration", "Entregar la ejecución en main.", RiskLevel.Critical, null, [@"src\Calc.cs"], "p-1", "20261021-c4a1f2"),
            Ct);

        var (background, accent, content, waiting) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var screen = new LockScreenViewModel(interfaceLock, clock, new InlineSynchronizationContext(), new SampleWork());
            var window = new MainWindow(
                ShellFactory.CreateMain(),
                new FileLayoutStore(new AppDataPaths(temp.Combine("data"))),
                quickOpenPresenter: new MainWindowLockTests.RecordingPresenter(),
                lockScreen: screen,
                interfaceLock: interfaceLock,
                floatingPanes: new MainWindowLockTests.CountingPanes());

            var root = (FrameworkElement)window.Content;
            var bitmap = OffscreenRender.Render(root, 1280, 800);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-ventana-bloqueada.png");
            return (
                OffscreenRender.Count(bitmap, Background),
                OffscreenRender.Count(bitmap, Accent),
                ((FrameworkElement)window.FindName("ShellContent")).Visibility,
                screen.WaitingItems.ToList());
        });

        Assert.Equal(Visibility.Hidden, content);
        Assert.True(background > 600_000, $"Only {background} pixels have the background colour: the lock screen is not in place of the content.");
        Assert.True(accent > 1_500, $"Only {accent} pixels have the accent colour: «Desbloquear con Windows Hello» was not drawn.");
        Assert.Equal(["deliver-integration · riesgo crítico · desde las 10:00"], waiting);
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

    private sealed class SampleWork : IWorkInProgress
    {
        public IReadOnlyList<ProjectActivityKind> Current() => [ProjectActivityKind.Run, ProjectActivityKind.Run, ProjectActivityKind.Debate];
    }

    private sealed class NoCanvasLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }
}
