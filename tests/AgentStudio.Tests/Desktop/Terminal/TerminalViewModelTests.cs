using System.ComponentModel;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Controls.Terminal;

namespace AgentStudio.Tests.Desktop.Terminal;

public sealed class TerminalViewModelTests
{
    private sealed class FakeSession : ITerminalSession
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 4242;

        public IAsyncEnumerable<string> Output => _output.Reader.ReadAllAsync();

        public Task<int> Exited => _exited.Task;

        public List<string> Written { get; } = [];

        public List<(short Columns, short Rows)> Sizes { get; } = [];

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>When set, <see cref="DisposeAsync"/> waits for it, to model a slow shutdown.</summary>
        public Task? DisposeGate { get; set; }

        public ValueTask WriteAsync(string input, CancellationToken cancellationToken)
        {
            Written.Add(input);
            return ValueTask.CompletedTask;
        }

        public void Resize(short columns, short rows) => Sizes.Add((columns, rows));

        public Task KillAsync()
        {
            Killed = true;
            End(-1);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            End(-1);
            if (DisposeGate is { } gate)
            {
                await gate;
            }
        }

        public void Emit(string text) => _output.Writer.TryWrite(text);

        public void End(int code)
        {
            _output.Writer.TryComplete();
            _exited.TrySetResult(code);
        }
    }

    private sealed class FakeLauncher : ITerminalLauncher
    {
        public List<TerminalStartInfo> Started { get; } = [];

        public List<FakeSession> Sessions { get; } = [];

        public bool Fail { get; set; }

        public ITerminalSession Start(TerminalStartInfo info)
        {
            if (Fail)
            {
                throw new Win32Exception(2, "boom");
            }

            Started.Add(info);
            var session = new FakeSession();
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeShells(params ShellInfo[] shells) : IShellLocator
    {
        public IReadOnlyList<ShellInfo> GetAvailableShells() => shells;
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private static readonly ShellInfo Pwsh = new("pwsh", "PowerShell 7", @"C:\p\pwsh.exe", ["-NoLogo"]);
    private static readonly ShellInfo Cmd = new("cmd", "Símbolo del sistema", @"C:\w\cmd.exe", []);

    private static TerminalViewModel Create(FakeLauncher launcher, params ShellInfo[] shells) =>
        new(launcher, new FakeShells(shells), new InlineContext());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Selects_the_first_available_shell()
    {
        var vm = Create(new FakeLauncher(), Pwsh, Cmd);

        Assert.Equal(Pwsh, vm.SelectedShell);
        Assert.Equal(2, vm.Shells.Count);
        Assert.Equal(TerminalState.NotStarted, vm.State);
    }

    [Fact]
    public void Without_shells_start_is_disabled_and_the_status_says_why()
    {
        var vm = Create(new FakeLauncher());

        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("ninguna shell", vm.StatusText);
    }

    [Fact]
    public async Task ViewReady_starts_the_selected_shell_with_the_reported_size()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);

        vm.ViewReady(100, 40);
        await WaitUntilAsync(() => vm.State == TerminalState.Running);

        var started = Assert.Single(launcher.Started);
        Assert.Equal(Pwsh.Path, started.Shell);
        Assert.Equal(["-NoLogo"], started.Arguments);
        Assert.Equal((short)100, started.Columns);
        Assert.Equal((short)40, started.Rows);
        Assert.Contains("PID 4242", vm.StatusText);
        Assert.True(vm.IsRunning);
        Assert.Equal("Reiniciar", vm.StartButtonText);
    }

    [Fact]
    public async Task Changing_directory_restarts_a_shell_running_elsewhere_there()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);
        vm.WorkingDirectory = @"C:\Users\yo";
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);

        await vm.ChangeWorkingDirectoryAsync(@"C:\proyectos\uno");

        Assert.Equal([@"C:\Users\yo", @"C:\proyectos\uno"], launcher.Started.Select(s => s.WorkingDirectory));
        Assert.True(launcher.Sessions[0].Disposed);
        Assert.True(vm.IsRunning);
    }

    [Fact]
    public async Task Changing_to_the_same_directory_keeps_the_running_shell()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);
        vm.WorkingDirectory = @"C:\proyectos\uno";
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);

        await vm.ChangeWorkingDirectoryAsync(@"c:\Proyectos\UNO\");

        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task Changing_directory_before_the_view_is_ready_only_sets_where_the_shell_starts()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);

        await vm.ChangeWorkingDirectoryAsync(@"C:\proyectos\uno");
        Assert.Empty(launcher.Started);

        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);
        Assert.Equal(@"C:\proyectos\uno", Assert.Single(launcher.Started).WorkingDirectory);
    }

    [Fact]
    public async Task Output_is_forwarded_input_is_written_and_resize_reaches_the_session()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Cmd);
        var received = new List<string>();
        vm.OutputReceived += text =>
        {
            lock (received)
            {
                received.Add(text);
            }
        };
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);
        var session = launcher.Sessions[0];

        session.Emit("hola");
        await WaitUntilAsync(() => { lock (received) { return received.Count == 1; } });
        await vm.SendInputAsync("dir\r");
        vm.ViewResized(132, 43);

        Assert.Equal(["hola"], received);
        Assert.Equal(["dir\r"], session.Written);
        Assert.Equal([((short)132, (short)43)], session.Sizes);
    }

    [Fact]
    public async Task Exit_code_is_shown_when_the_shell_ends()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Cmd);
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);

        launcher.Sessions[0].End(3);
        await WaitUntilAsync(() => vm.State == TerminalState.Exited);

        Assert.Equal(3, vm.ExitCode);
        Assert.Contains("código 3", vm.StatusText);
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_kills_the_session_and_reports_it_was_stopped()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Cmd);
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);

        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.State == TerminalState.Exited);

        Assert.True(launcher.Sessions[0].Killed);
        Assert.Null(vm.ExitCode);
        Assert.Contains("detenido", vm.StatusText);
    }

    [Fact]
    public async Task Restart_disposes_the_old_session_clears_the_screen_and_starts_another()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Cmd);
        var cleared = 0;
        vm.Cleared += () => cleared++;
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, launcher.Sessions.Count);
        Assert.True(launcher.Sessions[0].Disposed);
        Assert.Equal(2, cleared);
        Assert.Equal(TerminalState.Running, vm.State);
    }

    [Fact]
    public async Task A_launch_failure_is_reported_not_thrown()
    {
        var vm = Create(new FakeLauncher { Fail = true }, Cmd);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(TerminalState.Failed, vm.State);
        Assert.Contains("boom", vm.ErrorMessage);
    }

    [Fact]
    public async Task Input_is_dropped_when_nothing_is_running()
    {
        var vm = Create(new FakeLauncher(), Cmd);

        await vm.SendInputAsync("x");

        Assert.Equal(TerminalState.NotStarted, vm.State);
    }

    [Fact]
    public async Task Dispose_during_a_restart_does_not_start_a_new_shell()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);
        vm.ViewReady(80, 24);
        await WaitUntilAsync(() => vm.IsRunning);
        var gate = new TaskCompletionSource();
        launcher.Sessions[0].DisposeGate = gate.Task;

        // The restart is stuck releasing the old shell when the view model is disposed.
        var restart = vm.StartCommand.ExecuteAsync(null);
        await vm.DisposeAsync();
        gate.SetResult();
        await restart;

        Assert.Single(launcher.Sessions);
        Assert.True(launcher.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Start_after_dispose_does_nothing()
    {
        var launcher = new FakeLauncher();
        var vm = Create(launcher, Pwsh);
        await vm.DisposeAsync();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(launcher.Started);
        Assert.NotEqual(TerminalState.Running, vm.State);
    }
}
