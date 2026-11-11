using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-7 and docs/16 D-14: the icon while the app lives, a tooltip of 127 characters at most, a click for the panel and the
/// menu of the right button (open the window, a project and the logs; pause, resume and stop a run; lock; exit through the
/// managed exit), and notifications that say nothing of the project. The icon is a double: nothing reaches the screen.
/// </summary>
public sealed class TrayControllerTests
{
    private readonly BackgroundWorkRegistry _work = Registry();
    private readonly FakeTrayIcon _tray = new();
    private readonly FakePanelPresenter _panel = new();
    private readonly FakeShellWindow _window = new();
    private readonly FakeFolderOpener _folders = new();
    private readonly FakeExitPrompt _prompt = new();
    private readonly FakeAppShutdown _shutdown = new();
    private readonly List<string> _opened = [];
    private int _folderPicks;
    private int _locks;

    private TrayShellActions Actions => new(
        path =>
        {
            _opened.Add(path);
            return Task.CompletedTask;
        },
        () =>
        {
            _folderPicks++;
            return Task.CompletedTask;
        },
        () =>
        {
            _locks++;
            return Task.CompletedTask;
        },
        () => [RootA, RootB]);

    private (TrayController Tray, ManagedExit Exit) Start(TrayShellActions? actions = null)
    {
        var exit = new ManagedExit(_work, _prompt, new FakeRunExitRecorder(), _window, _shutdown);
        var tray = new TrayController(_tray, _work, exit, _panel, actions ?? Actions, _folders, @"C:\Users\alguien\AppData\Local\AgentStudio\logs", new FakeTimeProvider(T0));
        tray.Start();
        return (tray, exit);
    }

    private (FakeRun Run, RunMonitorViewModel Monitor) FollowRun(string id = "20261021-aaaaaa", RunPhase phase = RunPhase.Running)
    {
        var run = new FakeRun(Snapshot(id, phase, 1, TaskOf("T1", WorkTaskStatus.Running, "ejecutor"), TaskOf("T2", WorkTaskStatus.Pending)));
        var monitor = Follow(run, ProjectA);
        _work.TrackRun(RootA, monitor, () => ProjectA);
        return (run, monitor);
    }

    private static IReadOnlyList<string> Texts(IReadOnlyList<TrayMenuItem> items) =>
        [.. items.Select(i => i.IsSeparator ? "—" : i.IsEnabled ? i.Text : i.Text + " (desactivado)")];

    [Fact]
    public void Starting_puts_the_icon_with_its_tooltip_and_its_menu()
    {
        var (tray, _) = Start();

        Assert.True(_tray.Shown);
        Assert.Equal("Agent Studio · sin trabajo en marcha", _tray.ToolTip);
        Assert.Equal(_tray.ToolTip, tray.ToolTip);
        Assert.NotEmpty(_tray.OpenMenu());
    }

    [Fact]
    public void The_tooltip_follows_the_work_going_on()
    {
        Start();
        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };

        _work.Track(run);
        Assert.Equal("Agent Studio · 1 ejecución", _tray.ToolTip);

        run.Move(active: false);
        Assert.Equal("Agent Studio · sin trabajo en marcha", _tray.ToolTip);
    }

    [Fact]
    public void A_click_shows_or_hides_the_panel_and_a_click_on_a_notification_shows_the_window()
    {
        Start();

        _tray.Click();
        Assert.True(_panel.IsShown);
        _tray.Click();
        Assert.False(_panel.IsShown);

        _tray.ClickNotification();
        Assert.Equal(1, _window.Shown);
    }

    [Fact]
    public void The_menu_offers_the_window_the_panel_a_project_the_logs_each_run_locking_and_exiting()
    {
        FollowRun();
        Start();

        var menu = _tray.OpenMenu();

        Assert.Equal(
            [
                "Abrir Agent Studio",
                "Ver el estado del trabajo",
                "—",
                "Abrir proyecto",
                "Abrir los registros",
                "—",
                "monorepo · Ejecución 20261021-aaaaaa · En ejecución · 1 intento en curso",
                "—",
                "Bloquear",
                "Salir",
            ],
            Texts(menu));
        Assert.Equal(["monorepo", "tienda", "—", "Abrir carpeta…"], Texts(Menus.Find(menu, "Abrir proyecto").Children!));
        Assert.Equal(["Pausar", "Reanudar (desactivado)", "Detener (cancela la ejecución)"], Texts(Menus.Find(menu, "monorepo · ").Children!));
    }

    [Fact]
    public async Task Pausing_and_stopping_from_the_menu_ask_the_monitor_of_the_run()
    {
        var (run, _) = FollowRun();
        Start();

        await Menus.Choose(_tray.OpenMenu(), "Pausar");
        await Menus.Choose(_tray.OpenMenu(), "Detener");

        Assert.Equal(["pause", $"cancel:{RunMonitorViewModel.CancelReason}"], run.Calls);
    }

    [Fact]
    public void A_paused_run_offers_to_resume_it()
    {
        FollowRun(phase: RunPhase.Paused);
        Start();

        Assert.Equal(["Pausar (desactivado)", "Reanudar", "Detener (cancela la ejecución)"], Texts(Menus.Find(_tray.OpenMenu(), "monorepo · ").Children!));
    }

    [Fact]
    public async Task Opening_a_project_shows_the_window_and_asks_the_shell()
    {
        Start();

        await Menus.Choose(_tray.OpenMenu(), "tienda");
        await Menus.Choose(_tray.OpenMenu(), "Abrir carpeta…");

        Assert.Equal([RootB], _opened);
        Assert.Equal(1, _folderPicks);
        Assert.Equal(2, _window.Shown);
    }

    [Fact]
    public async Task The_logs_open_in_their_folder_and_the_window_and_the_panel_from_the_menu()
    {
        Start();

        await Menus.Choose(_tray.OpenMenu(), "Abrir los registros");
        await Menus.Choose(_tray.OpenMenu(), "Abrir Agent Studio");
        await Menus.Choose(_tray.OpenMenu(), "Ver el estado del trabajo");

        Assert.Equal([@"C:\Users\alguien\AppData\Local\AgentStudio\logs"], _folders.Opened);
        Assert.Equal(1, _window.Shown);
        Assert.True(_panel.IsShown);
        Assert.Equal("Ocultar el estado del trabajo", _tray.OpenMenu()[1].Text);
    }

    [Fact]
    public async Task Locking_is_asked_of_the_shell_and_without_a_lock_it_is_not_offered()
    {
        Start();
        await Menus.Choose(_tray.OpenMenu(), "Bloquear");
        Assert.Equal(1, _locks);

        var bare = new FakeTrayIcon();
        var exit = new ManagedExit(_work, _prompt, new FakeRunExitRecorder(), _window, _shutdown);
        new TrayController(bare, _work, exit, _panel, TrayShellActions.None, _folders, @"C:\logs", new FakeTimeProvider(T0)).Start();
        var menu = bare.OpenMenu();
        Assert.False(Menus.Find(menu, "Bloquear").IsEnabled);
        Assert.False(Menus.Find(menu, "Abrir proyecto").IsEnabled);
    }

    [Fact]
    public async Task Exiting_from_the_menu_goes_through_the_managed_exit()
    {
        FollowRun();
        Start();
        _prompt.Answer = ExitChoice.Cancel;

        await Menus.Choose(_tray.OpenMenu(), "Salir");

        Assert.False(Assert.Single(_prompt.Asked).OffersKeepInTray);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task While_waiting_to_exit_the_menu_offers_to_exit_now_or_not_to_exit_and_nothing_on_the_runs()
    {
        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 2 };
        _work.Track(run);
        var (_, exit) = Start();
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        await exit.ExitAsync();

        var menu = _tray.OpenMenu();

        Assert.Equal("Agent Studio · saldrá al terminar 2 intentos en curso · 1 ejecución", _tray.ToolTip);
        Assert.Contains("Salir ahora e interrumpir el trabajo", Texts(menu));
        Assert.Contains("No salir", Texts(menu));
        Assert.DoesNotContain("Salir", Texts(menu));

        await Menus.Choose(menu, "No salir");
        Assert.Equal(ExitState.None, exit.State);
        Assert.Contains("Salir", Texts(_tray.OpenMenu()));
    }

    [Fact]
    public void Locked_the_tray_names_no_project_and_offers_nothing_on_the_work_that_goes_on()
    {
        FollowRun();
        var (tray, _) = Start();

        tray.IsLocked = true;
        var menu = _tray.OpenMenu();

        Assert.Equal("Agent Studio · bloqueado · 1 ejecución", _tray.ToolTip);
        Assert.Equal(
            [
                "Abrir Agent Studio",
                "Ver el estado del trabajo",
                "—",
                "Abrir proyecto (desactivado)",
                "Abrir los registros (desactivado)",
                "—",
                "Bloqueado: 1 trabajo en marcha; desbloquea para verlo (desactivado)",
                "—",
                "Bloqueado (desactivado)",
                "Salir (desactivado)",
            ],
            Texts(menu));
        Assert.DoesNotContain(Menus.Flatten(menu), i => i.Text.Contains("monorepo", StringComparison.Ordinal) || i.Text.Contains("20261021", StringComparison.Ordinal));
    }

    [Fact]
    public void Locked_the_line_of_the_work_agrees_in_number_with_what_goes_on()
    {
        var (tray, _) = Start();
        _work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa"));
        tray.IsLocked = true;

        Assert.Contains("Bloqueado: 1 trabajo en marcha; desbloquea para verlo (desactivado)", Texts(_tray.OpenMenu()));

        _work.Track(new FakeWork(BackgroundWorkKind.Debate, ProjectB, "deb-1", pausable: false));

        Assert.Contains("Bloqueado: 2 trabajos en marcha; desbloquea para verlos (desactivado)", Texts(_tray.OpenMenu()));
    }

    [Fact]
    public async Task The_notifications_say_a_run_asked_for_approval_stopped_waiting_for_quota_and_ended_and_nothing_of_its_project()
    {
        var (run, monitor) = FollowRun();
        Start();
        Assert.Empty(_tray.Notifications);

        run.Emit(new ApprovalPendingEvent(T0, new TaskId("T1"), new ApprovalRequest("delivery.ff", "Entregar a main", RiskLevel.Critical, null, [], "p-a", "20261021-aaaaaa")));
        await WaitUntilAsync(() => _tray.Notifications.Count == 1, "No notification for the approval.");

        run.Emit(new RunPhaseChangedEvent(T0, RunPhase.WaitingForQuota, "Esperando cuota hasta las 14:05"));
        await WaitUntilAsync(() => monitor.Phase == RunPhase.WaitingForQuota, "The run did not wait for quota.");
        run.Emit(new RunPhaseChangedEvent(T0, RunPhase.Running, null));
        await WaitUntilAsync(() => _tray.Notifications.Count == 2, "No notification for the end of the wait.");

        run.Finish(new RunResult(new RunId("20261021-aaaaaa"), RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminada."));
        await WaitUntilAsync(() => _tray.Notifications.Count == 3, "No notification for the end of the run.");

        Assert.Equal(
            [
                new TrayNotification("Agent Studio espera tu aprobación", "Una ejecución necesita tu aprobación para seguir."),
                new TrayNotification("Terminó la espera por el límite de uso", "Una ejecución dejó de esperar el límite de uso de tu plan y sigue."),
                new TrayNotification("Ejecución terminada", "Una ejecución terminó: completada."),
            ],
            _tray.Notifications);
        Assert.DoesNotContain(_tray.Notifications, n => $"{n.Title} {n.Text}".Contains("monorepo", StringComparison.Ordinal) || $"{n.Title} {n.Text}".Contains("20261021", StringComparison.Ordinal) || $"{n.Title} {n.Text}".Contains("delivery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_debate_that_ends_is_said_without_its_question_nor_its_project()
    {
        var debate = new AgentStudio.Tests.Desktop.Debate.FakeDebateRun(AgentStudio.Tests.Desktop.Debate.DebateSamples.Snapshot(turnsInFlight: 1));
        _work.TrackDebate(RootC, ProjectC, debate);
        Start();

        debate.Fail(new OperationCanceledException());
        await WaitUntilAsync(() => _tray.Notifications.Count > 0, "No notification for the end of the debate.");

        Assert.False(_work.HasActiveWork);
        Assert.Equal([new TrayNotification("Debate terminado", "Un debate terminó.")], _tray.Notifications);
    }

    [Fact]
    public void Disposing_stops_following_the_work_and_takes_the_icon_out_of_the_notification_area()
    {
        var (tray, _) = Start();
        tray.Dispose();
        tray.Dispose();

        _work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa"));

        Assert.True(_tray.Disposed);
        Assert.Equal("Agent Studio · sin trabajo en marcha", _tray.ToolTip);
    }

    [Fact]
    public void A_tray_that_never_started_still_takes_its_icon_away_when_disposed()
    {
        var exit = new ManagedExit(_work, _prompt, new FakeRunExitRecorder(), _window, _shutdown);
        var tray = new TrayController(_tray, _work, exit, _panel, Actions, _folders, @"C:\logs", new FakeTimeProvider(T0));

        tray.Dispose();

        Assert.True(_tray.Disposed);
        Assert.False(_tray.Shown);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("notification")]
    [InlineData("project")]
    public async Task Opening_the_window_from_the_tray_while_waiting_to_exit_cancels_the_exit_and_says_so(string route)
    {
        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };
        _work.Track(run);
        var (_, exit) = Start();
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        await exit.ExitAsync();
        Assert.Equal(ExitState.WaitingForIdle, exit.State);
        var hidden = _window.Hidden;

        switch (route)
        {
            case "menu":
                await Menus.Choose(_tray.OpenMenu(), "Abrir Agent Studio");
                break;
            case "notification":
                _tray.ClickNotification();
                break;
            default:
                await Menus.Choose(_tray.OpenMenu(), "tienda");
                Assert.Equal([RootB], _opened);
                break;
        }

        Assert.Equal(ExitState.None, exit.State);
        Assert.True(_window.IsVisible);
        Assert.Equal(hidden, _window.Hidden);
        Assert.Contains(new TrayNotification("Agent Studio ya no sale", ManagedExit.PendingExitCancelledNotice), _tray.Notifications);
        Assert.Equal("Agent Studio · 1 ejecución", _tray.ToolTip);

        // The attempt ends: the exit nobody expects any more does not happen.
        run.Move(attempts: 0);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task Opening_the_window_from_the_tray_when_not_exiting_only_shows_it_and_says_nothing()
    {
        Start();

        await Menus.Choose(_tray.OpenMenu(), "Abrir Agent Studio");

        Assert.Equal(1, _window.Shown);
        Assert.Empty(_tray.Notifications);
    }
}
