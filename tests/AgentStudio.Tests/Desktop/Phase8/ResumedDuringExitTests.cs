using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// A run the scheduler resumes while the app holds the resumes back to leave (docs/16 D-39, ADR-0036): the suspension is taken
/// at once, and the scheduler announces the run of the pass in progress on its own thread before the wait for that pass ends;
/// the shell puts it in the registry of the work at once, without the UI thread. So the question of the exit lists it, whatever
/// the dispatcher runs first, and the end of the session, which blocks the UI thread while it waits for the pass, writes in it
/// too, and nothing waits on that thread. On the WPF test thread, with the shell's real handler; nothing is shown.
/// </summary>
public sealed class ResumedDuringExitTests
{
    private static readonly RunId Run = new("20261021-c4a1f2");
    private static readonly ProjectContext Project = new("p-a", RootA);

    [Fact]
    public async Task A_run_resumed_while_the_exit_waits_for_the_pass_in_progress_is_in_its_question()
    {
        var recorder = new FakeRunExitRecorder();
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);

        var titles = await WpfHost.RunAsync(async () =>
        {
            var (_, work, resumes) = Shell();
            var exit = new ManagedExit(work, prompt, recorder, new FakeShellWindow(), new FakeAppShutdown(), resumes: new PassResumingARun(resumes));

            await exit.ExitAsync();
            return Assert.Single(prompt.Asked).Work.Select(w => w.Title).ToList();
        });

        Assert.Equal([$"Ejecución {Run}"], titles);
        Assert.Equal([("p-a", Run.Value, RunExitChoice.ExitNow)], recorder.Recorded.ToList());
    }

    [Fact]
    public void A_run_resumed_while_the_session_ends_gets_its_entry_and_nothing_waits_for_the_blocked_ui_thread()
    {
        var recorder = new FakeRunExitRecorder();

        // A long budget: the pass gets half of it, so a handler that needed the UI thread would show as a wait that long.
        var budget = TimeSpan.FromSeconds(20);
        var (exiting, tracked, took) = WpfHost.Run(() =>
        {
            var (_, work, resumes) = Shell();
            var exit = new ManagedExit(work, new FakeExitPrompt(), recorder, new FakeShellWindow(), new FakeAppShutdown(), resumes: new PassResumingARun(resumes));

            // On the UI thread, which stays blocked while the pass is waited for, as Application.SessionEnding does.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            exit.RecordSessionEnded(budget);
            return (exit.IsExiting, work.All.Select(w => w.WorkId).ToList(), clock.Elapsed);
        });

        Assert.True(exiting);
        Assert.Contains(Run.Value, tracked);
        Assert.Equal([("p-a", Run.Value, RunExitChoice.SessionEnded)], recorder.Recorded.ToList());
        Assert.True(took < budget / 2, $"The end of the session waited {took} for the pass: something needed the blocked UI thread.");
    }

    /// <summary>
    /// The resumes with a pass in progress that resumes a run on a thread of the pool before the wait for it ends: what the
    /// scheduler does before <c>WaitForIdleAsync</c> completes. The exit awaits the wait; the end of the session blocks on it.
    /// </summary>
    private sealed class PassResumingARun(ScriptedResumes resumes) : ISuspendableResumes
    {
        public IDisposable Suspend() => new NoRelease();

        public Task WaitForIdleAsync(CancellationToken cancellationToken)
        {
            var resumed = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));
            return Task.Run(() => resumes.Raise(Project, resumed), cancellationToken);
        }
    }

    /// <summary>The shell's view model over a registry of the work, on the UI thread, with the consented resumes scripted.</summary>
    private static (MainViewModel Main, BackgroundWorkRegistry Work, ScriptedResumes Resumes) Shell()
    {
        var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
        var resumes = new ScriptedResumes();
        var main = new MainViewModel(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
            new ExplorerViewModel(),
            new JournalViewModel(TimeProvider.System),
            new QuickOpenViewModel(),
            orchestrators: OrchestratorDocumentFactory.WithoutEngine(),
            work: work,
            resumes: resumes);
        return (main, work, resumes);
    }

    private sealed class ScriptedResumes : IConsentedResumes
    {
        public event EventHandler<ScheduledResumeEventArgs>? Resumed;

        public event EventHandler? Available
        {
            add { }
            remove { }
        }

        public Task<ConsentedPass> ResumeAsync(string projectRoot, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsentedPass([], Complete: true));

        public void Raise(ProjectContext project, IOrchestrationRun run) =>
            Resumed?.Invoke(this, new ScheduledResumeEventArgs(project, run, RunResumeCause.ConsentAfterUnrequestedClose));
    }

    private sealed class NoRelease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
