using System.Diagnostics;
using AgentStudio.Application.Decisions;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Desktop.Background;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// Closing the window when the managed exit does not exist (before the tray started, or when it could not start; ADR-0036):
/// the person closed the app, so each piece of work going on gets «Salir ahora» in its log, as the managed exit writes it, and
/// a run with the consent of its header is never read as cut by a close nobody asked for (docs/16 D-39). The real window,
/// never shown, over doubles.
/// </summary>
public sealed class ExitWithoutTrayTests
{
    [Fact]
    public void Closing_the_window_without_the_tray_writes_salir_ahora_for_the_work_going_on_and_closes()
    {
        using var temp = new TempDirectory();
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        work.Track(new FakeWork(BackgroundWorkKind.Audit, ProjectB, workId: null));
        var recorder = new FakeRunExitRecorder();

        var closed = WpfHost.Run(() =>
        {
            var window = new MainWindow(
                ShellFactory.CreateMain(),
                new FileLayoutStore(new AppDataPaths(temp.Combine("data"))),
                quickOpenPresenter: new MainWindowLockTests.RecordingPresenter(),
                floatingPanes: new MainWindowLockTests.CountingPanes(),
                exitWithoutTray: new ExitWithoutTray(work, recorder));
            var closings = 0;
            window.Closed += (_, _) => closings++;
            window.Close();
            return closings;
        });

        // The audit has no id, and so no entry, as in the managed exit.
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)], recorder.Recorded.ToList());
        Assert.Equal(1, closed);
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_holds_the_others_nor_the_close_for_longer_than_its_time()
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectB, "20261021-bbbbbb") { AttemptsInFlight = 1 });
        var recorder = new FakeRunExitRecorder();
        recorder.FailingProjects.Add("p-a");

        var written = new ExitWithoutTray(work, recorder).Record(ExitWithoutTray.Budget);

        Assert.Equal(2, written);
        Assert.Equal([("p-b", "20261021-bbbbbb", RunExitChoice.ExitNow)], recorder.Recorded.ToList());

        // A disk that does not answer: the close waits for it no longer than its time.
        var hanging = new FakeRunExitRecorder { Hang = new TaskCompletionSource() };
        var clock = Stopwatch.StartNew();
        new ExitWithoutTray(work, hanging).Record(TimeSpan.FromMilliseconds(200));
        hanging.Hang.SetResult();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The close waited {clock.Elapsed}.");
    }

    [Fact]
    public void Nothing_going_on_writes_nothing()
    {
        var work = Registry();
        work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { IsActive = false });
        var recorder = new FakeRunExitRecorder();

        Assert.Equal(0, new ExitWithoutTray(work, recorder).Record(ExitWithoutTray.Budget));
        Assert.Empty(recorder.Recorded);
    }
}
