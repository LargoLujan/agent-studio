using System.Windows.Media;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;
using static AgentStudio.Tests.Desktop.Background.PanelSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The status panel and the question of the managed exit drawn without ever being shown (<see cref="OffscreenRender"/>):
/// measured, arranged and rendered into a bitmap on the WPF test thread, with no window, no HWND and no tray icon, over
/// synthetic data only. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set the pictures are saved for docs/evidencias.
/// </summary>
public sealed class BackgroundRenderTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);

    /// <summary>The panel over the synthetic world, built and refreshed on the UI thread, as the app does.</summary>
    private static Task<StatusPanelViewModel> PanelAsync() => WpfHost.RunAsync(async () =>
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock, SynchronizationContext.Current);
        var activity = new FakeProjectActivity { Holding = { ["p-b"] = ProjectActivityKind.Audit } };
        var work = new BackgroundWorkRegistry(clock, activity, SynchronizationContext.Current, Timeout.InfiniteTimeSpan);
        work.TrackRun(RootA, monitor, () => ProjectA);
        work.TrackDebate(RootC, ProjectC, new FakeDebateRun(DebateSamples.Snapshot(turnsInFlight: 2, awaiting: new DebateAwaiting(DebateAwaitReason.ProviderLimit, "Límite de uso.", Until: T0.AddMinutes(25)))));
        work.TrackProject(RootB, () => ProjectB);
        var panel = Panel(work, clock, openApp: () => { });
        await panel.RefreshAsync();
        return panel;
    });

    [Fact]
    public async Task The_panel_renders_offscreen_with_its_projects_bars_tokens_quota_and_what_waits()
    {
        var panel = await PanelAsync();

        var (drawn, raised, success, warning) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();

            // Every project at once for the picture: the panel of the app scrolls past 620 pixels.
            var view = new StatusPanelView { DataContext = panel, ContentMaxHeight = 4000 };
            var bitmap = OffscreenRender.Render(view, 540, 1950);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-panel-bandeja.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Raised), OffscreenRender.Count(bitmap, Success), OffscreenRender.Count(bitmap, Warning));
        });

        Assert.Equal(["monorepo", "docs-internas", "tienda"], panel.Projects.Select(p => p.Name));
        Assert.True(drawn > 40_000, $"Only {drawn} pixels differ from the panel: it was not drawn.");
        Assert.True(raised > 40_000, $"Only {raised} pixels have the card colour: the cards were not drawn.");
        Assert.True(success > 150, $"Only {success} pixels have the success colour (the bar of a healthy context).");
        Assert.True(warning > 400, $"Only {warning} pixels have the warning colour (the estimated bar, the block and the approval).");
    }

    [Fact]
    public async Task The_locked_panel_renders_offscreen_with_nothing_of_the_work_but_how_much_goes_on()
    {
        var panel = await PanelAsync();

        var (drawn, raised) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            panel.IsLocked = true;
            // On the surface, so the picture is the panel at its size and nothing is stretched to fill it; the bindings
            // settle before the first layout, or the cards the lock hides would be measured and drawn once.
            var view = new System.Windows.Controls.Border { Background = new SolidColorBrush(Surface), Child = new StatusPanelView { DataContext = panel } };
            WpfHost.FlushBindings();
            var bitmap = OffscreenRender.Render(view, 540, 240);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-panel-bloqueado.png");
            var result = (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Raised));
            panel.IsLocked = false;
            return result;
        });

        Assert.True(drawn > 2_000, $"Only {drawn} pixels differ from the panel: the locked text was not drawn.");
        Assert.True(raised < 500, $"{raised} pixels have the card colour: a card of the work is on the locked panel.");
    }

    [Fact]
    public void The_question_of_the_exit_renders_offscreen_with_the_work_and_its_four_answers()
    {
        var (drawn, warning, raised) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var question = new ExitQuestion(
                [
                    new ExitWorkLine("monorepo", "Ejecución 20261021-c4a1f2", "En ejecución · 4 intentos en curso", CanPause: true),
                    new ExitWorkLine("docs-internas", "Debate deb-20261019-a1b2c3", "En deliberación · ronda 2 · 2 turnos en curso", CanPause: false),
                    new ExitWorkLine("tienda", "Auditoría del proyecto", "En curso (sin más detalle aquí)", CanPause: false),
                ],
                OffersKeepInTray: true);
            var view = new ExitPromptView { DataContext = new ExitPromptViewModel(question) };
            var bitmap = OffscreenRender.Render(view, 620, 450);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-salida-gestionada.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning), OffscreenRender.Count(bitmap, Raised));
        });

        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the panel: the question was not drawn.");
        Assert.True(warning > 50, $"Only {warning} pixels have the warning colour («Salir ahora y dejarlas interrumpidas»).");
        Assert.True(raised > 10_000, $"Only {raised} pixels have the card colour (the list of the work).");
    }
}
