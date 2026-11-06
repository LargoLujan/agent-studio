using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The registrations of Windows Hello and the lock (<see cref="LockServices.AddWindowsHelloAndLock"/>), resolved with doubles
/// registered first: nothing here asks Windows Hello, locks the session or reads the input of Windows.
/// </summary>
public sealed class LockCompositionTests
{
    private sealed class Activity : IProjectActivity
    {
        public Dictionary<string, ProjectActivityKind> Held { get; } = [];

        public IDisposable? TryEnter(string projectId, ProjectActivityKind kind) => throw new NotSupportedException();

        public ProjectActivityKind? Current(string projectId) => Held.TryGetValue(projectId, out var kind) ? kind : null;

        public bool IsBusy(string projectId) => Held.ContainsKey(projectId);
    }

    private static (ServiceCollection Services, InMemorySettingsStore Store, FakeUserPresenceVerifier Hello) WithDoubles()
    {
        var store = new InMemorySettingsStore();
        var hello = new FakeUserPresenceVerifier();
        var services = new ServiceCollection();
        services.AddSingleton<IAppSettingsStore>(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(T0));
        services.AddSingleton<IUserPresenceVerifier>(hello);
        services.AddSingleton<IWindowsSession>(new FakeWindowsSession());
        services.AddSingleton<IIdleInput>(new ZeroIdle());
        return (services, store, hello);
    }

    private sealed class ZeroIdle : IIdleInput
    {
        public TimeSpan IdleFor() => TimeSpan.Zero;
    }

    [Fact]
    public void The_lock_its_screen_its_settings_and_the_confirmation_are_one_each_and_keep_the_doubles()
    {
        var (services, store, hello) = WithDoubles();
        using var container = services.AddWindowsHelloAndLock().BuildServiceProvider();

        var interfaceLock = container.GetRequiredService<InterfaceLock>();
        var confirmation = container.GetRequiredService<ReinforcedConfirmation>();

        Assert.Same(interfaceLock, container.GetRequiredService<IInterfaceLock>());
        Assert.Same(hello, container.GetRequiredService<IUserPresenceVerifier>());
        Assert.Same(hello, confirmation.Verifier);
        Assert.NotNull(container.GetRequiredService<IdleLockMonitor>());
        Assert.NotNull(container.GetRequiredService<LockScreenViewModel>());
        Assert.NotNull(container.GetRequiredService<LockSettingsViewModel>());

        // The setting is read at each question.
        Assert.True(confirmation.IsOn());
        store.Current = store.Current with { WindowsHelloForImportantApprovals = false };
        Assert.False(confirmation.IsOn());
    }

    [Fact]
    public void Without_doubles_the_app_gets_the_real_windows_session_and_input()
    {
        var services = new ServiceCollection().AddWindowsHelloAndLock();

        Assert.Equal(typeof(WindowsSession), services.Single(d => d.ServiceType == typeof(IWindowsSession)).ImplementationType);
        Assert.Equal(typeof(SessionIdleInput), services.Single(d => d.ServiceType == typeof(IIdleInput)).ImplementationType);
        Assert.Single(services, d => d.ServiceType == typeof(IUserPresenceVerifier));
    }

    [Fact]
    public async Task The_lock_screen_reads_what_goes_on_from_the_activity_of_the_projects_and_names_none()
    {
        var (services, _, _) = WithDoubles();
        var activity = new Activity();
        activity.Held["p-a"] = ProjectActivityKind.Run;
        activity.Held["p-b"] = ProjectActivityKind.Audit;
        services.AddSingleton<IWorkInProgress>(new ProjectsWorkInProgress(activity, () => ["p-a", "p-b", "p-c", "p-a"]));
        using var container = services.AddWindowsHelloAndLock().BuildServiceProvider();
        var screen = container.GetRequiredService<LockScreenViewModel>();

        await container.GetRequiredService<InterfaceLock>().LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);

        // Composed, the screen follows the lock on the UI thread: the dispatcher of the shared WPF test application, when a
        // test before this one created it. It refreshes there, so the text comes a moment later.
        for (var i = 0; i < 1000 && screen.RunningText != "Sigue en marcha: 1 ejecución y 1 auditoría."; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal("Sigue en marcha: 1 ejecución y 1 auditoría.", screen.RunningText);
    }
}
