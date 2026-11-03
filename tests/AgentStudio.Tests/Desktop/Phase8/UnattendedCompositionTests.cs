using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure.Clocks;
using Engine = AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.TestSupport;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The wiring of Phase 8 in the composition of the app (docs/16 §4.5, «Integración»): every piece of WP1, WP3 and WP4 resolves
/// and reaches what uses it, only the engine runs on the clock that stops while the machine sleeps, and the scheduler of the
/// consented resume shares the one orchestrator of the app. Over temporary folders and the doubles of
/// <see cref="Phase8Doubles"/>: nothing is shown and nothing touches Windows.
/// </summary>
public sealed class UnattendedCompositionTests
{
    [Fact]
    public void Every_piece_of_phase_8_resolves_from_the_composition_of_the_app()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        using var provider = doubles.BuildApp(temp);

        var interfaceLock = provider.GetRequiredService<InterfaceLock>();
        Assert.Same(interfaceLock, provider.GetRequiredService<IInterfaceLock>());
        Assert.NotNull(provider.GetRequiredService<LockScreenViewModel>());
        Assert.NotNull(provider.GetRequiredService<IdleLockMonitor>());
        Assert.NotNull(provider.GetRequiredService<ReinforcedConfirmation>());
        Assert.IsType<BackgroundWorkInProgress>(provider.GetRequiredService<IWorkInProgress>());
        Assert.NotNull(provider.GetRequiredService<BackgroundWorkRegistry>());
        Assert.NotNull(provider.GetRequiredService<StatusPanelViewModel>());
        Assert.NotNull(provider.GetRequiredService<IRunExitRecorder>());
        Assert.Same(doubles.Power, provider.GetRequiredService<IPowerEvents>());

        // «Trabajo desatendido» holds the settings of the lock, and «Ajustes» shows both, each in its section, next to
        // «Contexto y cuota», which is the one «Consumo» judges its report with.
        var unattended = provider.GetRequiredService<UnattendedSettingsViewModel>();
        Assert.Same(provider.GetRequiredService<LockSettingsViewModel>(), unattended.Lock);
        var settings = provider.GetRequiredService<SettingsDocumentViewModel>();
        Assert.Same(unattended, settings.Unattended);
        Assert.Same(unattended.Lock, settings.Lock);
        Assert.Same(provider.GetRequiredService<UsageDashboardViewModel>().Preferences, settings.Context);

        // The consented resume, over the engine and the workspace of the app.
        var resumes = Assert.IsType<ConsentedResumes>(provider.GetRequiredService<IConsentedResumes>());
        Assert.True(resumes.IsAvailable);
        Assert.Same(resumes, provider.GetRequiredService<ConsentedResumes>());

        // The shell resolves with them: its view model tracks the work and asks the scheduler.
        var main = provider.GetRequiredService<MainViewModel>();
        Assert.Same(provider.GetRequiredService<BackgroundWorkRegistry>(), Wiring.Field(main, "_work"));
        Assert.Same(resumes, Wiring.Field(main, "_resumes"));
    }

    [Fact]
    public async Task The_main_window_of_the_composition_follows_the_lock_and_closes_without_the_tray_through_its_writer()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        await using var provider = doubles.BuildApp(temp);

        // The production window, resolved as App resolves it (never shown): the lock reaches it through the composition.
        var (button, locked, unlocked, exitWithoutTray) = await WpfHost.RunAsync(async () =>
        {
            var window = provider.GetRequiredService<AgentStudio.Desktop.MainWindow>();
            var lockButton = (System.Windows.Controls.Button)window.FindName("LockButton");
            var interfaceLock = provider.GetRequiredService<InterfaceLock>();
            await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);
            WpfHost.FlushBindings();
            var shown = window.ShowsLockScreen;
            await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
            WpfHost.FlushBindings();
            return (lockButton.Visibility, shown, !window.ShowsLockScreen, Wiring.Field(window, "_exitWithoutTray"));
        });

        Assert.Equal(System.Windows.Visibility.Visible, button);
        Assert.True(locked, "Locked, the lock screen takes the place of the content of the production window.");
        Assert.True(unlocked);
        Assert.Same(provider.GetRequiredService<ExitWithoutTray>(), exitWithoutTray);
    }

    [Fact]
    public void The_app_registers_the_real_windows_session_and_power_events_and_a_test_composition_builds_neither()
    {
        using var temp = new TempDirectory();
        var folders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);

        // What the app registers, read without building anything (building them hooks into Windows).
        var production = new ServiceCollection().AddAgentStudioApp(folders);
        Assert.Equal(typeof(WindowsSession), Assert.Single(production, d => d.ServiceType == typeof(IWindowsSession)).ImplementationType);
        Assert.Equal(typeof(WindowsPowerEvents), Assert.Single(production, d => d.ServiceType == typeof(IPowerEvents)).ImplementationType);

        // A test puts its doubles first, and the composition keeps them.
        var tested = new ServiceCollection().AddWindowsDoubles().AddAgentStudioApp(folders);
        Assert.IsType<FakeWindowsSession>(Assert.Single(tested, d => d.ServiceType == typeof(IWindowsSession)).ImplementationInstance);
        Assert.IsType<SilentPowerEvents>(Assert.Single(tested, d => d.ServiceType == typeof(IPowerEvents)).ImplementationInstance);
    }

    [Fact]
    public void The_approvals_and_the_approval_of_the_plan_get_windows_hello_and_the_questions_wait_on_the_lock()
    {
        using var temp = new TempDirectory();
        using var provider = new Phase8Doubles().BuildApp(temp);

        var confirmation = provider.GetRequiredService<ReinforcedConfirmation>();
        var prompt = Assert.IsType<ApprovalPromptService>(provider.GetRequiredService<IApprovalPrompt>());
        var factory = Assert.IsType<OrchestratorDocumentFactory>(provider.GetRequiredService<IOrchestratorDocumentFactory>());

        Assert.Same(confirmation, Wiring.Field(prompt, "_confirmation"));
        Assert.Same(provider.GetRequiredService<IInterfaceLock>(), Wiring.Field(prompt, "_lock"));
        Assert.Same(confirmation, Wiring.Field(factory, "_confirmation"));
        Assert.Same(provider.GetRequiredService<IApprovalPrompt>(), provider.GetRequiredService<OrchestrationBackend>().Prompt);
    }

    [Fact]
    public void Only_the_engine_runs_on_the_awake_clock_and_the_lock_and_the_idle_lock_count_a_sleep()
    {
        using var temp = new TempDirectory();
        using var provider = new Phase8Doubles().BuildApp(temp);

        // The clock of the app is the system's: the idle lock counts a machine that slept as time without input, the tray and
        // the logs keep their timestamps.
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Same(TimeProvider.System, Wiring.Field(provider.GetRequiredService<InterfaceLock>(), "_time"));
        Assert.Same(TimeProvider.System, Wiring.Field(provider.GetRequiredService<IdleLockMonitor>(), "_time"));

        // The engine's is the awake clock: its runs leave the time asleep out of the silence of their attempts.
        var engineClock = provider.GetRequiredService<EngineClock>();
        Assert.IsType<AwakeTimeProvider>(engineClock.Time);
        Assert.Same(engineClock.Time, EngineTime(provider));
    }

    [Fact]
    public void A_clock_registered_before_the_composition_drives_the_engine_too()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(T0);
        using var provider = new Phase8Doubles().BuildApp(temp, services => services.AddSingleton<TimeProvider>(clock));

        Assert.Same(clock, EngineTime(provider));
        Assert.Same(clock, Wiring.Field(provider.GetRequiredService<InterfaceLock>(), "_time"));
    }

    [Fact]
    public void The_scheduler_resumes_with_the_one_orchestrator_the_screen_resumes_with_and_the_engine_hears_the_power_events()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        using var provider = doubles.BuildApp(temp);

        var orchestrator = Assert.IsType<Engine.Orchestrator>(provider.GetRequiredService<IOrchestrator>());
        var scheduler = provider.GetRequiredService<ConsentedResumes>().Scheduler;

        Assert.NotNull(scheduler);
        Assert.Same(orchestrator, Wiring.Field(scheduler, "_orchestrator"));
        Assert.Same(orchestrator, provider.GetRequiredService<OrchestrationBackend>().Orchestrator);
        Assert.Same(doubles.Power, Wiring.Property(Wiring.Field(orchestrator, "_ports")!, "Power"));
    }

    [Fact]
    public void The_lock_screen_counts_what_the_registry_of_the_tray_follows_and_names_no_project()
    {
        var work = BackgroundSamples.Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa"));
        work.Track(new FakeWork(BackgroundWorkKind.Debate, BackgroundSamples.ProjectB, "deb-1", pausable: false));
        work.Track(new FakeWork(BackgroundWorkKind.Audit, BackgroundSamples.ProjectC, null, pausable: false));
        work.Track(new FakeWork(BackgroundWorkKind.Planning, BackgroundSamples.ProjectA, null, pausable: false) { IsActive = false });

        var current = new BackgroundWorkInProgress(work).Current();

        Assert.Equal([ProjectActivityKind.Run, ProjectActivityKind.Debate, ProjectActivityKind.Audit], current);
        Assert.Equal("Sigue en marcha: 1 ejecución, 1 debate y 1 auditoría.", LockScreenViewModel.Running(current.ToList()));
    }

    /// <summary>The clock the orchestrator of the composition was built with (its ports are the engine's own).</summary>
    private static object? EngineTime(IServiceProvider provider) =>
        Wiring.Property(Wiring.Field(provider.GetRequiredService<IOrchestrator>(), "_ports")!, "Time");
}
