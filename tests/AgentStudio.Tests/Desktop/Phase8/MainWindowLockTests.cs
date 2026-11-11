using System.Windows;
using AgentStudio.Application.Execution;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Desktop.Views.Lock;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Lock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The lock of the interface in the main window (docs/16 D-13, F8-10; ADR-0037, decision 4): the lock screen takes the place of
/// the content, which is hidden and disabled (the editor and the terminal are WebView2 windows no WPF layer covers); the
/// floating panes hide, the list of files closes, no shortcut of the window works, and the tray hides what goes on. Unlocked,
/// all of it comes back. The real window over doubles: it is never shown, and nothing asks Windows Hello.
/// </summary>
public sealed class MainWindowLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Locking_replaces_the_content_hides_the_floating_panes_closes_the_list_of_files_and_takes_every_shortcut_out()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        temp.WriteFile("proyecto/a.txt");
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        var panes = new CountingPanes();
        var presenter = new RecordingPresenter();

        var seen = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))), quickOpenPresenter: presenter, lockScreen: screen, interfaceLock: interfaceLock, floatingPanes: panes);
            WpfHost.FlushBindings();
            var shortcuts = Shortcuts(window);
            var lockButton = (Button)window.FindName("LockButton");
            var boundToLock = ReferenceEquals(lockButton.Command, screen.LockCommand) && lockButton.Visibility == Visibility.Visible;
            await vm.OpenProjectAsync(project);
            await vm.GoToFileCommand.ExecuteAsync(null);

            // «Bloquear», on the UI thread: the content is replaced at once, before Windows is asked whether Hello is there.
            var locking = interfaceLock.LockAsync(LockReason.Manual, Ct);
            var replacedAtOnce = window.ShowsLockScreen;
            await locking;
            WpfHost.FlushBindings();
            var locked = State(window);
            var listOpen = vm.QuickOpen.IsOpen;
            var panesWhileLocked = (panes.Hides, panes.Restores);

            await interfaceLock.UnlockWithHelloAsync(Ct);
            WpfHost.FlushBindings();
            var unlocked = State(window);
            var ctrlP = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.P && b.Modifiers == ModifierKeys.Control);
            return (shortcuts, boundToLock, replacedAtOnce, locked, listOpen, unlocked, ReferenceEquals(ctrlP.Command, vm.GoToFileCommand), Shortcuts(window), panesWhileLocked);
        });

        Assert.Contains((Key.P, ModifierKeys.Control), seen.shortcuts);
        Assert.Contains((Key.L, ModifierKeys.Control | ModifierKeys.Alt), seen.shortcuts);
        Assert.Contains((Key.B, ModifierKeys.Control | ModifierKeys.Shift), seen.shortcuts);
        Assert.True(seen.boundToLock, "«Bloquear» of the top bar runs the command of the lock screen.");
        Assert.True(seen.replacedAtOnce);

        Assert.Equal((Visibility.Hidden, false, Visibility.Visible, 0, 0), seen.locked);
        Assert.False(seen.listOpen);
        Assert.Equal(1, presenter.Closes);
        Assert.Equal((1, 0), seen.panesWhileLocked);

        Assert.Equal((Visibility.Visible, true, Visibility.Collapsed, seen.shortcuts.Count, 0), seen.unlocked);
        Assert.True(seen.Item7, "Ctrl+P runs «Ir al fichero» again once unlocked.");
        Assert.Equal(seen.shortcuts, seen.Item8);
        Assert.Equal((1, 1), (panes.Hides, panes.Restores));
    }

    [Fact]
    public async Task A_lock_from_another_thread_reaches_the_window_and_the_tray_on_the_ui_thread()
    {
        using var temp = new TempDirectory();
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        var tray = new FakeTrayIcon();

        var (whileLocked, panelLocked, trayLocked, afterUnlock) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))), quickOpenPresenter: new RecordingPresenter(), lockScreen: screen, interfaceLock: interfaceLock, floatingPanes: new CountingPanes());
            using var services = BackgroundServices(tray);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());

            // The idle lock locks from a timer thread: the window and the tray follow on the dispatcher.
            await Task.Run(() => interfaceLock.LockAsync(LockReason.Idle, Ct), Ct);
            WpfHost.FlushBindings();
            var locked = (window.ShowsLockScreen, host.Panel.IsLocked, host.Tray.IsLocked);

            await Task.Run(() => interfaceLock.UnlockWithHelloAsync(Ct), Ct);
            WpfHost.FlushBindings();
            return (locked.ShowsLockScreen, locked.Item2, locked.Item3, (window.ShowsLockScreen, host.Panel.IsLocked, host.Tray.IsLocked));
        });

        Assert.True(whileLocked);
        Assert.True(panelLocked, "The panel of the tray hides what goes on while locked.");
        Assert.True(trayLocked, "The tray names no project while locked.");
        Assert.Equal((false, false, false), afterUnlock);
    }

    [Fact]
    public async Task Attached_while_locked_the_tray_starts_locked()
    {
        using var temp = new TempDirectory();
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        await interfaceLock.LockAsync(LockReason.Manual, Ct);

        var (shows, trayLocked) = WpfHost.Run(() =>
        {
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = new MainWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Combine("data"))), quickOpenPresenter: new RecordingPresenter(), lockScreen: screen, interfaceLock: interfaceLock, floatingPanes: new CountingPanes());
            using var services = BackgroundServices(new FakeTrayIcon());
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());
            return (window.ShowsLockScreen, host.Tray.IsLocked);
        });

        Assert.True(shows);
        Assert.True(trayLocked);
    }

    [Fact]
    public void Without_a_lock_composed_the_window_offers_no_lock_and_keeps_its_shortcuts()
    {
        using var temp = new TempDirectory();

        var (visibility, shortcuts) = WpfHost.Run(() =>
        {
            var window = new MainWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            return (((Button)window.FindName("LockButton")).Visibility, Shortcuts(window));
        });

        Assert.Equal(Visibility.Collapsed, visibility);
        Assert.DoesNotContain((Key.L, ModifierKeys.Control | ModifierKeys.Alt), shortcuts);

        // Ctrl+P, «Debates», «Auditar proyecto completo», the terminal (Ctrl+Ñ, Oem3) and «Ayuda» (F1).
        Assert.Equal(5, shortcuts.Count);
        Assert.Contains((Key.F1, ModifierKeys.None), shortcuts);
        Assert.Contains((Key.Oem3, ModifierKeys.Control), shortcuts);
    }

    /// <summary>The background over doubles, with a registry that raises its events inline.</summary>
    internal static ServiceProvider BackgroundServices(FakeTrayIcon tray, IAppSettingsStore? settings = null, IExitPrompt? prompt = null, IShellWindow? shell = null, IAppShutdown? shutdown = null, IRunExitRecorder? recorder = null) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(BackgroundSamples.T0))
            .AddSingleton(recorder ?? new FakeRunExitRecorder())
            .AddSingleton<ITrayIcon>(tray)
            .AddSingleton<IStatusPanelPresenter>(new FakePanelPresenter())
            .AddSingleton(shell ?? new FakeShellWindow())
            .AddSingleton(shutdown ?? new FakeAppShutdown())
            .AddSingleton(prompt ?? new FakeExitPrompt())
            .AddSingleton<IKeepAwake>(new CountingKeepAwake())
            .AddSingleton<IFolderOpener>(new FakeFolderOpener())
            .AddSingleton(settings ?? new InMemorySettingsStore())
            .AddSingleton(BackgroundSamples.Registry())
            .AddAgentStudioBackground()
            .BuildServiceProvider();

    private static List<(Key Key, ModifierKeys Modifiers)> Shortcuts(Window window) =>
        [.. window.InputBindings.OfType<KeyBinding>().Select(b => (b.Key, b.Modifiers))];

    private static (Visibility Content, bool ContentEnabled, Visibility LockScreen, int Shortcuts, int Commands) State(MainWindow window) =>
        (((FrameworkElement)window.FindName("ShellContent")).Visibility,
         ((FrameworkElement)window.FindName("ShellContent")).IsEnabled,
         ((LockScreenView)window.FindName("LockScreen")).Visibility,
         window.InputBindings.Count,
         window.CommandBindings.Count);

    internal sealed class CountingPanes : IFloatingPanes
    {
        public int Hides { get; private set; }

        public int Restores { get; private set; }

        public void HideShown() => Hides++;

        public void RestoreHidden() => Restores++;
    }

    internal sealed class RecordingPresenter : IQuickOpenPresenter
    {
        public int Shows { get; private set; }

        public int Closes { get; private set; }

        public void Show(Window owner, QuickOpenViewModel viewModel) => Shows++;

        public void Close() => Closes++;
    }
}
