using System.Windows.Threading;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// What the background asks of the shell that only the shell has (the integration fills it over <c>MainViewModel</c> and
/// the lock of WP4): the confirmation before leaving (unsaved changes in the editor), the actions of the tray menu, and the
/// consented resumes, which the managed exit suspends while the app leaves (<c>ResumeScheduler.Suspend</c> and
/// <c>WaitForIdleAsync</c>, docs/16 D-39).
/// </summary>
public sealed record BackgroundShell(
    Func<CancellationToken, Task<bool>>? ConfirmLeaving = null,
    TrayShellActions? Tray = null,
    ISuspendableResumes? Resumes = null)
{
    public static BackgroundShell None { get; } = new();
}

/// <summary>Registers the pieces of the background of the app (docs/16 D-14 to D-16 and D-40); the shell composes the rest.</summary>
public static class BackgroundServiceCollectionExtensions
{
    /// <summary>
    /// The power transitions (<see cref="IPowerEvents"/>, for the engine too) and «mantener despierto» (<see cref="IKeepAwake"/>),
    /// the registry of the work, the writer of the exit in the logs (and of a close without the tray,
    /// <see cref="ExitWithoutTray"/>), the question of the exit, the main window and the end
    /// of the app, the status panel with its window, the tray icon and the opener of the logs. A registration made before
    /// wins. Resolve them on the UI thread: the registry, the panel and the icon belong to it.
    /// </summary>
    public static IServiceCollection AddAgentStudioBackground(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // By its type, so a test can see what the app registers without building it (it registers with Windows when built).
        services.TryAddSingleton<IPowerEvents, WindowsPowerEvents>();
        services.TryAddSingleton<IExecutionState, ExecutionStateThread>();
        services.TryAddSingleton<IKeepAwake>(provider => new KeepAwakeService(provider.GetRequiredService<IExecutionState>(), provider.GetService<ILogger<KeepAwakeService>>()));
        services.TryAddSingleton(provider => new BackgroundWorkRegistry(Time(provider), provider.GetService<IProjectActivity>(), UiContext()));
        services.TryAddSingleton<IRunExitRecorder>(provider => new DecisionLogRunExitRecorder(provider.GetRequiredService<IDecisionRecorder>()));
        services.TryAddSingleton(provider => new ExitWithoutTray(
            provider.GetRequiredService<BackgroundWorkRegistry>(),
            provider.GetRequiredService<IRunExitRecorder>(),
            provider.GetService<ILogger<ExitWithoutTray>>()));
        services.TryAddSingleton<IExitPrompt, WpfExitPrompt>();
        services.TryAddSingleton<IShellWindow, WpfShellWindow>();
        services.TryAddSingleton<IAppShutdown, WpfAppShutdown>();
        // «Abrir Agent Studio» of the panel is given by BackgroundHost.Create: ManagedExit.ShowWindow, never the window itself.
        services.TryAddSingleton(provider => new StatusPanelViewModel(
            provider.GetRequiredService<BackgroundWorkRegistry>(),
            Time(provider),
            new StatusPanelSources(provider.GetService<IUsageReport>(), provider.GetService<IRunRegistry>(), provider.GetService<QuotaViewModel>())));
        services.TryAddSingleton<IStatusPanelPresenter>(provider => new WindowStatusPanelPresenter(provider.GetRequiredService<StatusPanelViewModel>()));
        services.TryAddSingleton<ITrayIcon, NotifyIconTray>();
        services.TryAddSingleton<IFolderOpener, ExplorerFolderOpener>();
        return services;
    }

    private static TimeProvider Time(IServiceProvider provider) => provider.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// The UI thread's context, whatever thread resolves the registry first: its events, and its polls, go there. Without an
    /// application (a test) the registry takes the creating thread's.
    /// </summary>
    private static DispatcherSynchronizationContext? UiContext() =>
        System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new DispatcherSynchronizationContext(dispatcher) : null;
}

/// <summary>
/// The background of the app put together (docs/16 D-14 to D-16 and D-40): the managed exit, the tray with its panel and
/// «mantener despierto» while there is work, over the registry the shell feeds. The shell creates it once on the UI thread,
/// starts it, and disposes it when the app ends.
/// </summary>
public sealed class BackgroundHost : IDisposable
{
    private readonly KeepAwakeWhileWorking _keepAwake;
    private bool _disposed;

    public BackgroundHost(BackgroundWorkRegistry work, ManagedExit exit, TrayController tray, StatusPanelViewModel panel, KeepAwakeWhileWorking keepAwake)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(keepAwake);
        Work = work;
        Exit = exit;
        Tray = tray;
        Panel = panel;
        _keepAwake = keepAwake;
    }

    /// <summary>What goes on: the shell tracks its runs, debates and projects here.</summary>
    public BackgroundWorkRegistry Work { get; }

    /// <summary>The managed exit: closing the window, «Salir» and the end of the session go through it.</summary>
    public ManagedExit Exit { get; }

    public TrayController Tray { get; }

    public StatusPanelViewModel Panel { get; }

    /// <summary>
    /// The interface is locked (D-13, WP4): the tray and its panel hide what goes on, which goes on, and the managed exit asks
    /// nothing (a question of its own open then is withdrawn).
    /// </summary>
    public bool IsLocked
    {
        get => Tray.IsLocked;
        set
        {
            Tray.IsLocked = value;
            Panel.IsLocked = value;
            Exit.IsLocked = value;
        }
    }

    /// <summary>
    /// Builds it from the services of <see cref="BackgroundServiceCollectionExtensions.AddAgentStudioBackground"/>, on the UI
    /// thread. Every way the background opens the window goes through <see cref="ManagedExit.ShowWindow"/> (the tray, its
    /// notifications and «Abrir Agent Studio» of the panel); the shell should open it the same way (<see cref="ShowWindow"/>).
    /// </summary>
    public static BackgroundHost Create(IServiceProvider services, BackgroundShell shell)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(shell);
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var work = services.GetRequiredService<BackgroundWorkRegistry>();
        var exit = new ManagedExit(
            work,
            services.GetRequiredService<IExitPrompt>(),
            services.GetRequiredService<IRunExitRecorder>(),
            services.GetRequiredService<IShellWindow>(),
            services.GetRequiredService<IAppShutdown>(),
            shell.ConfirmLeaving,
            services.GetService<ILogger<ManagedExit>>(),
            shell.Resumes);
        var tray = new TrayController(
            services.GetRequiredService<ITrayIcon>(),
            work,
            exit,
            services.GetRequiredService<IStatusPanelPresenter>(),
            shell.Tray ?? TrayShellActions.None,
            services.GetRequiredService<IFolderOpener>(),
            TrayController.DefaultLogsFolder,
            time,
            services.GetService<ILogger<TrayController>>());
        var panel = services.GetRequiredService<StatusPanelViewModel>();
        panel.OpenApp = exit.ShowWindow;
        var settings = services.GetService<IAppSettingsStore>();
        var keepAwake = new KeepAwakeWhileWorking(work, services.GetRequiredService<IKeepAwake>(), () => settings?.Load().PreventSleepWhileWorking == true);
        return new BackgroundHost(work, exit, tray, panel, keepAwake);
    }

    /// <summary>Puts the icon in the notification area and starts following the work.</summary>
    public void Start() => Tray.Start();

    /// <summary>Shows the main window as the background does: while the app waits to exit, that exit is cancelled.</summary>
    public void ShowWindow() => Exit.ShowWindow();

    /// <summary>The settings were saved: «Impedir la suspensión mientras hay trabajo» is read again.</summary>
    public void SettingsChanged() => _keepAwake.Refresh();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keepAwake.Dispose();

        // The tray takes its icon out of the notification area itself, whatever order the shell disposes things in.
        Tray.Dispose();
        Exit.Dispose();
    }
}
