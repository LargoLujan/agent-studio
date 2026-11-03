using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The pieces of the background as the composition registers them, and the host put together over doubles of what would
/// show anything (the icon, the question, the window): nothing is ever on the screen.
/// </summary>
public sealed class BackgroundCompositionTests
{
    [Fact]
    public void Every_piece_is_registered_once_as_a_singleton_and_an_earlier_registration_wins()
    {
        var tray = new FakeTrayIcon();
        var services = new ServiceCollection();
        services.AddSingleton<ITrayIcon>(tray);

        services.AddAgentStudioBackground();
        services.AddAgentStudioBackground();

        foreach (var type in new[]
        {
            typeof(IPowerEvents), typeof(IExecutionState), typeof(IKeepAwake), typeof(BackgroundWorkRegistry), typeof(IRunExitRecorder),
            typeof(IExitPrompt), typeof(IShellWindow), typeof(IAppShutdown), typeof(StatusPanelViewModel), typeof(IStatusPanelPresenter),
            typeof(ITrayIcon), typeof(IFolderOpener),
        })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        Assert.Same(tray, Assert.Single(services, d => d.ServiceType == typeof(ITrayIcon)).ImplementationInstance);
    }

    [Fact]
    public async Task The_host_puts_the_exit_the_tray_and_keeping_awake_together_over_the_registry()
    {
        var tray = new FakeTrayIcon();
        var panel = new FakePanelPresenter();
        var window = new FakeShellWindow();
        var shutdown = new FakeAppShutdown();
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var keepAwake = new CountingKeepAwake();
        var settings = new InMemorySettingsStore(AppSettings.Default with { PreventSleepWhileWorking = true });
        var store = new InMemoryDecisionLogStore();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(T0))
            .AddSingleton<IDecisionRecorder>(new DecisionRecorder(store, new FakeTimeProvider(T0)))
            .AddSingleton<IAppSettingsStore>(settings)
            .AddSingleton<ITrayIcon>(tray)
            .AddSingleton<IStatusPanelPresenter>(panel)
            .AddSingleton<IShellWindow>(window)
            .AddSingleton<IAppShutdown>(shutdown)
            .AddSingleton<IExitPrompt>(prompt)
            .AddSingleton<IKeepAwake>(keepAwake)
            .AddSingleton<IFolderOpener>(new FakeFolderOpener())
            .AddSingleton(Registry())
            .AddAgentStudioBackground()
            .BuildServiceProvider();
        var confirmed = 0;

        using var host = BackgroundHost.Create(services, new BackgroundShell(_ =>
        {
            confirmed++;
            return Task.FromResult(true);
        }));
        host.Start();
        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };
        host.Work.Track(run);

        Assert.True(tray.Shown);
        Assert.Equal("Agent Studio · 1 ejecución", tray.ToolTip);
        Assert.Equal(1, keepAwake.Holds);

        host.IsLocked = true;
        Assert.True(host.Panel.IsLocked);
        Assert.True(host.Tray.IsLocked);
        host.IsLocked = false;

        await host.Exit.ExitAsync();

        Assert.Equal(1, confirmed);
        Assert.Equal(1, shutdown.Calls);
        var entry = Assert.Single(store.Entries("p-a"));
        Assert.Equal(AgentStudio.Domain.Decisions.DecisionKind.RunExitChosen, entry.Kind);

        settings.Current = settings.Current with { PreventSleepWhileWorking = false };
        host.SettingsChanged();
        Assert.Equal(0, keepAwake.Held);
    }

    [Fact]
    public async Task Opening_the_app_from_the_panel_goes_through_the_managed_exit_and_cancels_a_pending_exit()
    {
        var tray = new FakeTrayIcon();
        var window = new FakeShellWindow();
        var shutdown = new FakeAppShutdown();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(T0))
            .AddSingleton<IRunExitRecorder>(new FakeRunExitRecorder())
            .AddSingleton<ITrayIcon>(tray)
            .AddSingleton<IStatusPanelPresenter>(new FakePanelPresenter())
            .AddSingleton<IShellWindow>(window)
            .AddSingleton<IAppShutdown>(shutdown)
            .AddSingleton<IExitPrompt>(new FakeExitPrompt(ExitChoice.PauseAndExitWhenIdle))
            .AddSingleton<IKeepAwake>(new CountingKeepAwake())
            .AddSingleton<IFolderOpener>(new FakeFolderOpener())
            .AddSingleton(Registry())
            .AddAgentStudioBackground()
            .BuildServiceProvider();
        using var host = BackgroundHost.Create(services, BackgroundShell.None);
        host.Start();
        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };
        host.Work.Track(run);

        Assert.True(host.Panel.OpenAppCommand.CanExecute(null));
        host.Panel.OpenAppCommand.Execute(null);
        Assert.Equal(1, window.Shown);

        await host.Exit.ExitAsync();
        Assert.Equal(ExitState.WaitingForIdle, host.Exit.State);
        host.Panel.OpenAppCommand.Execute(null);

        Assert.Equal(ExitState.None, host.Exit.State);
        Assert.Contains(tray.Notifications, n => n.Text == ManagedExit.PendingExitCancelledNotice);
        run.Move(attempts: 0);
        Assert.Equal(0, shutdown.Calls);

        host.ShowWindow();
        Assert.Equal(3, window.Shown);
    }

    private sealed class CountingKeepAwake : IKeepAwake
    {
        public int Holds { get; private set; }

        public int Held { get; private set; }

        public bool IsHeld => Held > 0;

        public IDisposable Hold(string reason)
        {
            Holds++;
            Held++;
            return new Release(this);
        }

        private sealed class Release(CountingKeepAwake owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (!_done)
                {
                    _done = true;
                    owner.Held--;
                }
            }
        }
    }
}
