using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The managed exit and the consented resumes (docs/16 D-39, ADR-0036). The exit takes the suspension of the resumes at once,
/// before it reads the work going on (<c>Suspend</c>), and then waits for the pass in progress (<c>WaitForIdleAsync</c>): a run
/// that pass resumed is asked about, and none is resumed once the person chose. The suspension is released when the person
/// stays, also when the window is shown again while the exit waits for the pass (cancelling the wait releases nothing by
/// itself, so the exit releases it), and held while the app waits to exit or exits. The end of the Windows session takes one
/// before it writes anything, waits for the pass within its time, and never releases it. With a counting double, and with the
/// engine's scheduler.
/// </summary>
public sealed class ExitSuspendsResumesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_exit_suspends_the_resumes_at_once_then_waits_for_the_pass_in_progress_and_asks_about_the_run_it_resumed()
    {
        var work = Registry();
        var resumes = new CountingResumes();
        var resumed = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };

        // The pass in progress resumes a run before the wait ends.
        resumes.WhileWaiting = () => work.Track(resumed);
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var recorder = new FakeRunExitRecorder();
        var exit = new ManagedExit(work, prompt, recorder, new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes);

        await exit.ExitAsync();

        Assert.Equal(["Suspend", "WaitForIdle"], resumes.Calls);
        Assert.Equal(1, resumes.HeldWhenWaiting);
        Assert.Equal(["Run 20261021-aaaaaa"], Assert.Single(prompt.Asked).Work.Select(w => w.Title));
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)], recorder.Recorded.ToList());
        Assert.Equal((1, 0), (resumes.Taken, resumes.Released));
        Assert.True(exit.SuspendsResumes);
    }

    [Fact]
    public async Task When_the_person_stays_the_suspension_is_released_and_while_the_app_waits_to_exit_it_is_held()
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var resumes = new CountingResumes();
        var prompt = new FakeExitPrompt(ExitChoice.Cancel);
        var window = new FakeShellWindow();
        var exit = new ManagedExit(work, prompt, new FakeRunExitRecorder(), window, new FakeAppShutdown(), resumes: resumes);

        // «Cancelar».
        await exit.ExitAsync();
        var cancelled = (resumes.Taken, resumes.Released);

        // «Seguir en la bandeja».
        prompt.Answer = ExitChoice.KeepInTray;
        await exit.WindowClosingAsync(CloseWindowAction.Ask);
        var keptInTray = (resumes.Taken, resumes.Released);

        // «Pausar y salir…», then «No salir».
        prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        await exit.ExitAsync();
        var waiting = resumes.Held;
        exit.CancelPendingExit();
        var noSalir = resumes.Held;

        // «Pausar y salir…», then the window shown again while it waits.
        await exit.ExitAsync();
        exit.ShowWindow();

        Assert.Equal((1, 1), cancelled);
        Assert.Equal((2, 2), keptInTray);
        Assert.Equal(1, waiting);
        Assert.Equal(0, noSalir);
        Assert.Equal((4, 4), (resumes.Taken, resumes.Released));
        Assert.False(exit.SuspendsResumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_the_window_while_the_exit_waits_for_the_pass_in_progress_stops_the_exit_and_releases_its_suspension(bool closingTheWindow)
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var resumes = new CountingResumes { PassNeverEnds = true };
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var recorder = new FakeRunExitRecorder();
        var window = new FakeShellWindow();
        var shutdown = new FakeAppShutdown();

        // On the UI thread, as in the app: what follows the wait is posted to it, never run inside ShowWindow.
        var (waitingForThePass, heldWhileWaiting, outcome, state, shown, laterQuestions) = await WpfHost.RunAsync(async () =>
        {
            var exit = new ManagedExit(work, prompt, recorder, window, shutdown, resumes: resumes);
            var leaving = closingTheWindow ? exit.WindowClosingAsync(CloseWindowAction.Ask) : exit.ExitAsync();
            var waiting = !leaving.IsCompleted;
            var held = resumes.Held;

            exit.ShowWindow();
            var stopped = await leaving;
            var after = (exit.State, window.Shown);

            // The exit is not left stuck: asked again, it asks.
            resumes.PassNeverEnds = false;
            prompt.Answer = ExitChoice.Cancel;
            await exit.ExitAsync();
            return (waiting, held, stopped, after.State, after.Shown, prompt.Asked.Count);
        });

        Assert.True(waitingForThePass);
        Assert.Equal(1, heldWhileWaiting);
        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(ExitState.None, state);
        Assert.Equal(1, shown);
        Assert.True(resumes.FirstWait!.IsCanceled);
        Assert.Equal(1, laterQuestions);
        Assert.Empty(recorder.Recorded);
        Assert.Equal(0, shutdown.Calls);

        // The first suspension was released when the window came back; the second, when the person cancelled.
        Assert.Equal((2, 2), (resumes.Taken, resumes.Released));
    }

    [Fact]
    public void The_end_of_the_session_suspends_the_resumes_before_it_writes_and_never_releases_them()
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var resumes = new CountingResumes();
        var recorder = new FakeRunExitRecorder();
        var writtenWhenTaken = -1;
        resumes.OnSuspend = () => writtenWhenTaken = recorder.Recorded.Count;
        var exit = new ManagedExit(work, new FakeExitPrompt(), recorder, new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes);

        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));
        exit.CancelPendingExit();
        exit.Dispose();

        Assert.Equal(0, writtenWhenTaken);
        Assert.Equal(["Suspend", "WaitForIdle"], resumes.Calls);
        Assert.Equal(1, resumes.HeldWhenWaiting);
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.SessionEnded)], recorder.Recorded.ToList());
        Assert.Equal((1, 0), (resumes.Taken, resumes.Released));
        Assert.True(exit.SuspendsResumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void When_the_pass_in_progress_does_not_end_the_end_of_the_session_still_writes_within_its_time_and_keeps_the_suspension(bool waitIgnoresItsToken)
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var resumes = new CountingResumes { PassNeverEnds = true, WaitIgnoresItsToken = waitIgnoresItsToken };
        var recorder = new FakeRunExitRecorder();
        var exit = new ManagedExit(work, new FakeExitPrompt(), recorder, new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes);
        var budget = TimeSpan.FromMilliseconds(400);

        var clock = Stopwatch.StartNew();
        exit.RecordSessionEnded(budget);
        clock.Stop();

        // Half the time for the pass, the rest for the writes: never stuck on a pass that does not end.
        Assert.True(clock.Elapsed < budget + TimeSpan.FromSeconds(5), $"The end of the session took {clock.Elapsed}.");
        Assert.Equal(1, resumes.HeldWhenWaiting);
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.SessionEnded)], recorder.Recorded.ToList());
        Assert.Equal((1, 0), (resumes.Taken, resumes.Released));
        Assert.True(exit.SuspendsResumes);
        Assert.True(exit.IsExiting);
    }

    [Fact]
    public async Task The_end_of_the_session_while_the_exit_waits_for_the_pass_keeps_its_suspension_for_good_and_asks_nothing()
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        var resumes = new CountingResumes { PassNeverEnds = true };
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);
        var recorder = new FakeRunExitRecorder();
        var shutdown = new FakeAppShutdown();

        var (outcome, exiting, suspends) = await WpfHost.RunAsync(async () =>
        {
            var exit = new ManagedExit(work, prompt, recorder, new FakeShellWindow(), shutdown, resumes: resumes);
            var leaving = exit.ExitAsync();

            // Windows ends the session while the exit waits for the pass, before its question.
            exit.RecordSessionEnded(TimeSpan.FromMilliseconds(400));
            var stopped = await leaving;
            exit.CancelPendingExit();
            return (stopped, exit.IsExiting, exit.SuspendsResumes);
        });

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.Empty(prompt.Asked);
        Assert.Equal(0, shutdown.Calls);
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.SessionEnded)], recorder.Recorded.ToList());
        Assert.Equal((1, 0), (resumes.Taken, resumes.Released));
        Assert.True(exiting);
        Assert.True(suspends);
    }

    [Fact]
    public async Task With_the_engines_scheduler_no_run_resumes_while_the_app_waits_to_exit_and_it_does_once_the_person_stays()
    {
        using var harness = new EngineHarness();
        var resumes = await CutConsentedRunAsync(harness);
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectB, "20261021-bbbbbb") { AttemptsInFlight = 1 });
        var exit = new ManagedExit(work, new FakeExitPrompt(ExitChoice.PauseAndExitWhenIdle), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes);

        await exit.ExitAsync();
        var whileWaiting = await resumes.ResumeAsync(harness.Project.RootPath, Ct);

        exit.CancelPendingExit();
        var outcome = Assert.Single((await resumes.ResumeAsync(harness.Project.RootPath, Ct)).Outcome);
        await using var resumed = outcome.Resumed!;
        await resumed.CompleteAsync(harness);

        Assert.Empty(whileWaiting.Outcome);
        Assert.False(whileWaiting.Complete);
        Assert.Equal(RunResumeCause.ConsentAfterUnrequestedClose, outcome.Cause);
    }

    [Fact]
    public async Task With_the_engines_scheduler_no_run_resumes_once_the_session_ended()
    {
        using var harness = new EngineHarness();
        var resumes = await CutConsentedRunAsync(harness);
        var exit = new ManagedExit(Registry(), new FakeExitPrompt(), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown(), resumes: resumes);

        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));

        Assert.Empty((await resumes.ResumeAsync(harness.Project.RootPath, Ct)).Outcome);
        Assert.Empty((await resumes.ResumeAsync(harness.Project.RootPath, Ct)).Outcome);
    }

    /// <summary>A run with the consent, cut while T1 runs by a close nobody asked for, and the resumes over the harness's engine.</summary>
    private static async Task<ConsentedResumes> CutConsentedRunAsync(EngineHarness harness)
    {
        Directory.CreateDirectory(Path.Combine(harness.Project.RootPath, ".agentstudio"));
        File.WriteAllBytes(Path.Combine(harness.Project.RootPath, ".agentstudio", "agentstudio.db"), []);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(OrchestrationFixtures.Plan(OrchestrationFixtures.Work("T1")), new RunOptions(AutoResumeConsent: true));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);
        var resumes = new ConsentedResumes(new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs), new Workspace(harness.Project));

        // The managed exit exists: the passes may run.
        resumes.Enable();
        return resumes;
    }

    private sealed class Workspace(ProjectContext project) : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(project);
    }

    /// <summary>
    /// The consented resumes as the exit sees them: the suspensions taken and released, the calls in order, and a pass in
    /// progress that <see cref="WhileWaiting"/> plays before the wait ends, or that never ends (<see cref="PassNeverEnds"/>).
    /// </summary>
    private sealed class CountingResumes : ISuspendableResumes
    {
        public List<string> Calls { get; } = [];

        public int Taken { get; private set; }

        public int Released { get; private set; }

        public int Held => Taken - Released;

        /// <summary>The suspensions held when the last wait began; -1 before any.</summary>
        public int HeldWhenWaiting { get; private set; } = -1;

        /// <summary>The first wait handed out.</summary>
        public Task? FirstWait { get; private set; }

        public Action? OnSuspend { get; set; }

        public Action? WhileWaiting { get; set; }

        public bool PassNeverEnds { get; set; }

        /// <summary>A wait that does not end when its token is cancelled either (the caller's own bound must hold).</summary>
        public bool WaitIgnoresItsToken { get; set; }

        public IDisposable Suspend()
        {
            Calls.Add("Suspend");
            Taken++;
            OnSuspend?.Invoke();
            return new Release(this);
        }

        public Task WaitForIdleAsync(CancellationToken cancellationToken)
        {
            Calls.Add("WaitForIdle");
            HeldWhenWaiting = Held;
            WhileWaiting?.Invoke();
            var never = new TaskCompletionSource().Task;
            var wait = !PassNeverEnds ? Task.CompletedTask : WaitIgnoresItsToken ? never : never.WaitAsync(cancellationToken);
            FirstWait ??= wait;
            return wait;
        }

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
}
