using System.Windows.Interop;
using System.Windows.Threading;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// What goes on, read from <see cref="IProjectActivity"/> for the projects the shell knows: the kind of activity of each one
/// that has one. Projects are never named.
/// </summary>
/// <param name="activity">The one activity per project of this process.</param>
/// <param name="projects">The ids of the projects that may have an activity (the open ones, the ones with a run going...).</param>
public sealed class ProjectsWorkInProgress(IProjectActivity activity, Func<IEnumerable<string>> projects) : IWorkInProgress
{
    public IReadOnlyList<ProjectActivityKind> Current() =>
        // A remote operation of Agent Studio itself (docs/21 D-25) lasts seconds and has nothing to show or to stop: it is not listed.
        [.. projects().Distinct(StringComparer.Ordinal).Select(activity.Current).OfType<ProjectActivityKind>().Where(kind => kind != ProjectActivityKind.RemoteOperation)];
}

public static class LockServices
{
    /// <summary>
    /// Registers Windows Hello and the lock of the interface (docs/16 D-13, F8-10 and F8-11; ADR-0037): the verifier over
    /// the main window, the Windows session, the input of the session, <see cref="InterfaceLock"/> (also as
    /// <see cref="IInterfaceLock"/>), the verified lock of Windows (<see cref="IWindowsSessionState"/> and
    /// <see cref="IWindowsLockConfirmation"/>, docs/17 D-40 (a)), the <see cref="ReinforcedConfirmation"/> of the approvals (the
    /// fixed barrier of <c>PresencePolicy</c> always; the configurable approvals with
    /// <see cref="AppSettings.WindowsHelloForImportantApprovals"/>, read at each question), the <see cref="IdleLockMonitor"/>
    /// (not started), the <see cref="LockScreenViewModel"/> and the <see cref="LockSettingsViewModel"/>. What is registered
    /// earlier is kept, so tests put doubles first. It needs the <see cref="IAppSettingsStore"/>; an
    /// <see cref="IWorkInProgress"/> registered by the composition tells the lock screen what goes on (without it, «sin dato»).
    /// </summary>
    /// <remarks>
    /// This provides the types and registers them; nothing uses them until the integration wires them. What the integration
    /// has to wire:
    /// <list type="bullet">
    /// <item>the registration of the approval prompt (OrchestrationUiServices), which today builds
    /// <see cref="ApprovalPromptService"/> without them: it has to pass it the <see cref="ReinforcedConfirmation"/> and the
    /// <see cref="IInterfaceLock"/> registered here;</item>
    /// <item>the factory of the «Orquestador» documents (<c>OrchestratorDocumentFactory</c>, in the same place), which has to
    /// get the same <see cref="ReinforcedConfirmation"/> for «Aprobar plan»;</item>
    /// <item>the main window (the <see cref="LockScreenViewModel"/> in place of the content, following
    /// <see cref="IInterfaceLock.Changed"/>), the start of the <see cref="IdleLockMonitor"/> and the
    /// <see cref="LockSettingsViewModel"/> in «Ajustes» (ADR-0037).</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddWindowsHelloAndLock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUserPresenceVerifier>(_ => new WindowsHelloVerifier(UiDispatcher(), MainWindowHandle));
        services.TryAddSingleton<IWindowsSession, WindowsSession>();
        services.TryAddSingleton<IIdleInput, SessionIdleInput>();
        services.TryAddSingleton(provider => new InterfaceLock(
            provider.GetRequiredService<IUserPresenceVerifier>(),
            provider.GetRequiredService<IWindowsSession>(),
            Time(provider)));
        services.TryAddSingleton<IInterfaceLock>(provider => provider.GetRequiredService<InterfaceLock>());
        services.TryAddSingleton<IWindowsSessionState, WindowsSessionState>();
        services.TryAddSingleton<IWindowsLockConfirmation>(provider => new WindowsLockConfirmation(
            provider.GetRequiredService<IWindowsSessionState>(),
            Time(provider)));
        services.TryAddSingleton(provider => new ReinforcedConfirmation(
            provider.GetRequiredService<IUserPresenceVerifier>(),
            () => provider.GetRequiredService<IAppSettingsStore>().Load().WindowsHelloForImportantApprovals,
            Time(provider),
            provider.GetRequiredService<IWindowsLockConfirmation>()));
        services.TryAddSingleton(provider => new IdleLockMonitor(
            provider.GetRequiredService<InterfaceLock>(),
            provider.GetRequiredService<IIdleInput>(),
            () => provider.GetRequiredService<IAppSettingsStore>().Load(),
            Time(provider)));
        services.TryAddSingleton(provider => new LockScreenViewModel(
            provider.GetRequiredService<InterfaceLock>(),
            Time(provider),
            UiContext(),
            provider.GetService<IWorkInProgress>()));
        services.TryAddSingleton(provider => new LockSettingsViewModel(provider.GetRequiredService<IAppSettingsStore>()));
        return services;
    }

    private static TimeProvider Time(IServiceProvider provider) => provider.GetService<TimeProvider>() ?? TimeProvider.System;

    private static Dispatcher UiDispatcher() => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>The handle of the main window, read on the UI thread at each verification; 0 while there is none.</summary>
    private static nint MainWindowHandle() =>
        System.Windows.Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;

    private static DispatcherSynchronizationContext? UiContext() =>
        System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new DispatcherSynchronizationContext(dispatcher) : null;
}
