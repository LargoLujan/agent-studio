using System.Reflection;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Lock;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// Doubles of everything of Phase 8 that would touch Windows or the screen, registered before the composition of the app,
/// whose registrations keep what was registered first: the tray icon, its panel, the main window as the background sees it,
/// the end of the app, the question of the exit, the opener of the logs, «mantener despierto», the power events, Windows
/// Hello, the Windows session and the input of the session. Nothing here shows an icon or a window, asks Hello, locks the
/// session, keeps the computer awake or listens to its power transitions.
/// </summary>
internal sealed class Phase8Doubles
{
    public FakeTrayIcon Tray { get; } = new();

    public FakePanelPresenter Panel { get; } = new();

    public FakeShellWindow Window { get; } = new();

    /// <summary>Where the managed exit writes the choice of each piece of work: a record, so no log of a sample project is written.</summary>
    public FakeRunExitRecorder ExitRecorder { get; } = new();

    public FakeAppShutdown Shutdown { get; } = new();

    public FakeExitPrompt Prompt { get; } = new();

    public FakeFolderOpener Folders { get; } = new();

    public CountingKeepAwake KeepAwake { get; } = new();

    public SilentPowerEvents Power { get; } = new();

    public FakeUserPresenceVerifier Hello { get; } = new();

    public FakeWindowsSession Session { get; } = new();

    public FixedIdleInput Idle { get; } = new();

    public IServiceCollection Register(IServiceCollection services) =>
        services
            .AddSingleton<ITrayIcon>(Tray)
            .AddSingleton<IStatusPanelPresenter>(Panel)
            .AddSingleton<IShellWindow>(Window)
            .AddSingleton<IRunExitRecorder>(ExitRecorder)
            .AddSingleton<IAppShutdown>(Shutdown)
            .AddSingleton<IExitPrompt>(Prompt)
            .AddSingleton<IFolderOpener>(Folders)
            .AddSingleton<IKeepAwake>(KeepAwake)
            .AddSingleton<IPowerEvents>(Power)
            .AddSingleton<IUserPresenceVerifier>(Hello)
            .AddSingleton<IWindowsSession>(Session)
            .AddSingleton<IIdleInput>(Idle);

    /// <summary>The app as it composes itself (<see cref="AppComposition.AddAgentStudioApp"/>) over temporary folders, with these doubles first.</summary>
    public ServiceProvider BuildApp(TempDirectory temp, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        Register(services);
        before?.Invoke(services);
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

/// <summary>«Mantener despierto», counted: never <c>SetThreadExecutionState</c>.</summary>
internal sealed class CountingKeepAwake : IKeepAwake
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

/// <summary>The power events, as a source the test never fires: whoever listens is counted.</summary>
internal sealed class SilentPowerEvents : IPowerEvents
{
    private EventHandler<PowerEvent>? _changed;

    public int Listeners => _changed?.GetInvocationList().Length ?? 0;

    public event EventHandler<PowerEvent>? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }
}

/// <summary>The input of the session, fixed: the person used the keyboard <see cref="Idle"/> ago.</summary>
internal sealed class FixedIdleInput : IIdleInput
{
    public TimeSpan Idle { get; set; } = TimeSpan.Zero;

    public TimeSpan IdleFor() => Idle;
}

/// <summary>Reads what the composition handed a type, where that type keeps it private: the wiring is the thing tested.</summary>
internal static class Wiring
{
    public static object? Field(object owner, string name) =>
        (owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name} has no field {name}.")).GetValue(owner);

    public static object? Property(object owner, string name) =>
        (owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name} has no property {name}.")).GetValue(owner);
}
