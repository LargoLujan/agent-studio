using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Lock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// Closing the window while the interface is locked (docs/16 D-13, ADR-0037): the X of the title bar, Alt+F4, Alt+Space →
/// Cerrar and «Cerrar ventana» of the taskbar all raise Closing, outside the content the lock hides. Locked, a close never
/// starts the managed exit nor shows a question: nothing of the work going on, nor the names of the files with unsaved changes,
/// shows over the lock screen, and nothing is written or ended. The real window, never shown, over doubles.
/// </summary>
public sealed class ClosingWhileLockedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Closing_the_window_while_locked_asks_nothing_writes_nothing_and_does_not_exit()
    {
        using var temp = new TempDirectory();
        var interfaceLock = NewLock();
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var recorder = new FakeRunExitRecorder();
        var shutdown = new FakeAppShutdown();

        var (closed, state, suspends) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = Window(vm, temp, screen, interfaceLock);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: prompt, shutdown: shutdown, recorder: recorder);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
            var closings = 0;
            window.Closed += (_, _) => closings++;

            await interfaceLock.LockAsync(LockReason.Manual, Ct);
            WpfHost.FlushBindings();
            window.Close();
            WpfHost.FlushBindings();
            return (closings, host.Exit.State, host.Exit.SuspendsResumes);
        });

        Assert.Empty(prompt.Asked);
        Assert.Empty(recorder.Recorded);
        Assert.Equal(0, shutdown.Calls);
        Assert.Equal(0, closed);
        Assert.Equal(ExitState.None, state);
        Assert.False(suspends);
    }

    [Fact]
    public async Task Without_the_tray_closing_the_window_while_locked_shows_no_unsaved_changes_and_does_not_close()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var file = temp.WriteFile("proyecto/notas.txt", "original");
        var interfaceLock = NewLock();
        var unsaved = new FakeUnsavedChangesPrompt(UnsavedChangesChoice.Discard);

        var closed = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain(unsaved);
            await vm.OpenProjectAsync(project);
            await vm.OpenFileAsync(file);
            Assert.IsType<FileDocumentViewModel>(vm.Documents.Last()).Editor!.SetDirty(true);
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = Window(vm, temp, screen, interfaceLock);
            var closings = 0;
            window.Closed += (_, _) => closings++;

            await interfaceLock.LockAsync(LockReason.Manual, Ct);
            WpfHost.FlushBindings();
            window.Close();
            WpfHost.FlushBindings();
            return closings;
        });

        Assert.Empty(unsaved.Calls);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task The_lock_withdraws_the_exit_question_open_when_it_locks_as_cancelar_and_releases_the_suspension()
    {
        using var temp = new TempDirectory();
        var interfaceLock = NewLock();
        var prompt = new OpenUntilWithdrawn();
        var recorder = new FakeRunExitRecorder();
        var shutdown = new FakeAppShutdown();
        var resumes = new CountingResumes();

        var (outcome, state, suspends) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = Window(vm, temp, screen, interfaceLock);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: prompt, shutdown: shutdown, recorder: recorder);
            using var host = BackgroundHost.Create(services, new BackgroundShell(Resumes: resumes));
            window.AttachBackground(host, new InMemorySettingsStore());
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });

            // «Salir» from the tray: the question is open when the idle lock fires.
            var leaving = host.Exit.ExitAsync();
            await interfaceLock.LockAsync(LockReason.Idle, Ct);
            var left = await leaving;
            return (left, host.Exit.State, host.Exit.SuspendsResumes);
        });

        Assert.Single(prompt.Asked);
        Assert.True(prompt.Token.IsCancellationRequested, "The question was withdrawn.");
        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(ExitState.None, state);
        Assert.Empty(recorder.Recorded);
        Assert.Equal(0, shutdown.Calls);
        Assert.False(suspends);
        Assert.Equal((1, 1), (resumes.Taken, resumes.Released));
    }

    [Fact]
    public async Task The_lock_withdraws_the_unsaved_changes_question_of_the_exit_and_nothing_is_written()
    {
        var work = BackgroundSamples.Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var recorder = new FakeRunExitRecorder();
        var shutdown = new FakeAppShutdown();
        var resumes = new CountingResumes();
        var confirmations = new List<CancellationToken>();

        var (outcome, state) = await WpfHost.RunAsync(async () =>
        {
            var exit = new ManagedExit(work, new FakeExitPrompt(ExitChoice.ExitNow), recorder, new FakeShellWindow(), shutdown, OpenUntilWithdrawnAsync, resumes: resumes);
            var leaving = exit.ExitAsync();

            // «Salir ahora» was answered; the question of the unsaved changes is open when the interface locks. Whatever it
            // answers then is not taken.
            exit.IsLocked = true;
            var left = await leaving;
            return (left, exit.State);
        });

        Assert.True(Assert.Single(confirmations).IsCancellationRequested);
        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(ExitState.None, state);
        Assert.Empty(recorder.Recorded);
        Assert.Equal(0, shutdown.Calls);
        Assert.Equal((1, 1), (resumes.Taken, resumes.Released));

        async Task<bool> OpenUntilWithdrawnAsync(CancellationToken withdrawn)
        {
            confirmations.Add(withdrawn);
            await WaitUntilWithdrawnAsync(withdrawn);
            return true;
        }
    }

    [Fact]
    public async Task Without_the_tray_the_lock_withdraws_the_unsaved_changes_question_of_a_close_and_the_window_stays()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var file = temp.WriteFile("proyecto/notas.txt", "original");
        var interfaceLock = NewLock();
        var unsaved = new LockingPrompt(interfaceLock);

        var (closed, locked) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain(unsaved);
            await vm.OpenProjectAsync(project);
            await vm.OpenFileAsync(file);
            Assert.IsType<FileDocumentViewModel>(vm.Documents.Last()).Editor!.SetDirty(true);
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = Window(vm, temp, screen, interfaceLock);
            var closings = 0;
            window.Closed += (_, _) => closings++;

            // The question opens; the idle lock fires while it is open, and «No guardar» is answered as it locks.
            window.Close();
            WpfHost.FlushBindings();
            return (closings, window.ShowsLockScreen);
        });

        Assert.Equal(1, unsaved.Asked);
        Assert.True(unsaved.Withdrawn, "The question was withdrawn by the lock.");
        Assert.True(locked);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task An_answer_given_as_the_interface_locked_is_not_taken()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var file = temp.WriteFile("proyecto/notas.txt", "original");
        using var withdrawal = new CancellationTokenSource();
        var unsaved = new AnswerAsWithdrawn(withdrawal, UnsavedChangesChoice.Discard);

        var (goOn, dirty) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain(unsaved);
            await vm.OpenProjectAsync(project);
            await vm.OpenFileAsync(file);
            var document = Assert.IsType<FileDocumentViewModel>(vm.Documents.Last());
            document.Editor!.SetDirty(true);
            var confirmed = await vm.ConfirmDiscardOrSaveAsync(vm.Documents.ToList(), withdrawal.Token);
            return (confirmed, document.IsDirty);
        });

        Assert.False(goOn, "A withdrawn question is a cancel, whatever it answered.");
        Assert.True(dirty);
        Assert.Equal(1, unsaved.Asked);
    }

    [Fact]
    public async Task The_end_of_the_session_while_locked_still_writes_session_ended_and_the_window_closes_without_asking()
    {
        using var temp = new TempDirectory();
        var interfaceLock = NewLock();
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var recorder = new FakeRunExitRecorder();

        var (closed, exiting) = await WpfHost.RunAsync(async () =>
        {
            var vm = ShellFactory.CreateMain();
            using var screen = new LockScreenViewModel(interfaceLock, new FakeTimeProvider(T0), SynchronizationContext.Current);
            var window = Window(vm, temp, screen, interfaceLock);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: prompt, recorder: recorder);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
            var closings = 0;
            window.Closed += (_, _) => closings++;
            await interfaceLock.LockAsync(LockReason.Manual, Ct);

            // Windows ends the session: Application.SessionEnding, then the windows close.
            host.Exit.RecordSessionEnded(TimeSpan.FromSeconds(5));
            window.Close();
            WpfHost.FlushBindings();
            return (closings, host.Exit.IsExiting);
        });

        Assert.Equal([("p-a", "20261021-aaaaaa", AgentStudio.Application.Decisions.RunExitChoice.SessionEnded)], recorder.Recorded.ToList());
        Assert.Empty(prompt.Asked);
        Assert.True(exiting);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Salir_while_locked_asks_nothing_and_takes_no_suspension()
    {
        var work = BackgroundSamples.Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, BackgroundSamples.ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var resumes = new CountingResumes();
        var exit = new ManagedExit(work, prompt, new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes) { IsLocked = true };

        var outcome = await exit.ExitAsync();
        var closing = await exit.WindowClosingAsync(CloseWindowAction.Exit);

        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(CloseOutcome.HiddenToTray, closing);
        Assert.Empty(prompt.Asked);
        Assert.Equal(0, resumes.Taken);
    }

    private static InterfaceLock NewLock() => new(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));

    /// <summary>Waits until <paramref name="withdrawn"/> is cancelled; gives up after a while, so a test that fails never hangs.</summary>
    private static async Task WaitUntilWithdrawnAsync(CancellationToken withdrawn)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(withdrawn, TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await Task.Delay(Timeout.Infinite, bound.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The question of the exit, open until it is withdrawn; then it answers «Salir ahora», which must not be taken.</summary>
    private sealed class OpenUntilWithdrawn : IExitPrompt
    {
        public List<ExitQuestion> Asked { get; } = [];

        public CancellationToken Token { get; private set; }

        public async Task<ExitChoice> AskAsync(ExitQuestion question, CancellationToken cancellationToken)
        {
            Asked.Add(question);
            Token = cancellationToken;
            await WaitUntilWithdrawnAsync(cancellationToken);
            return ExitChoice.ExitNow;
        }
    }

    /// <summary>The question of unsaved changes, during which the idle lock fires: it answers «No guardar» as it locks.</summary>
    private sealed class LockingPrompt(InterfaceLock interfaceLock) : IUnsavedChangesPrompt
    {
        public int Asked { get; private set; }

        public bool Withdrawn { get; private set; }

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave) => Ask(titles, canSave, CancellationToken.None);

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave, CancellationToken cancellationToken)
        {
            Asked++;

            // The lock applies at once, on this thread: its window is replaced before Windows is asked about Hello.
            _ = interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);
            Withdrawn = cancellationToken.IsCancellationRequested;
            return UnsavedChangesChoice.Discard;
        }
    }

    /// <summary>The question of unsaved changes, withdrawn by its caller while it is open, and answered all the same.</summary>
    private sealed class AnswerAsWithdrawn(CancellationTokenSource withdrawal, UnsavedChangesChoice answer) : IUnsavedChangesPrompt
    {
        public int Asked { get; private set; }

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave) => Ask(titles, canSave, CancellationToken.None);

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave, CancellationToken cancellationToken)
        {
            Asked++;
            withdrawal.Cancel();
            return answer;
        }
    }

    /// <summary>The consented resumes as the exit holds them: the suspensions taken and released.</summary>
    private sealed class CountingResumes : ISuspendableResumes
    {
        public int Taken { get; private set; }

        public int Released { get; private set; }

        public IDisposable Suspend()
        {
            Taken++;
            return new Release(this);
        }

        public Task WaitForIdleAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class Release(CountingResumes owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (!_done)
                {
                    _done = true;
                    owner.Released++;
                }
            }
        }
    }

    /// <summary>The real main window, never shown, with the lock and doubles of its floating panes and its list of files.</summary>
    private static MainWindow Window(MainViewModel vm, TempDirectory temp, LockScreenViewModel screen, IInterfaceLock interfaceLock) => new(
        vm,
        new FileLayoutStore(new AppDataPaths(temp.Combine("data"))),
        quickOpenPresenter: new MainWindowLockTests.RecordingPresenter(),
        lockScreen: screen,
        interfaceLock: interfaceLock,
        floatingPanes: new MainWindowLockTests.CountingPanes());
}
