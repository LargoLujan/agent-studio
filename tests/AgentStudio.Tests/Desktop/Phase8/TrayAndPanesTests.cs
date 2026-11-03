using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// Two details of the tray: the end of a wait the system estimated (<c>RateLimitedUntilEstimated(…)</c>, docs/16 D-37) is read
/// like an announced reset, and the floating panes of the dock hide while the window lives in the tray, as they do while the
/// interface is locked, and come back once neither holds.
/// </summary>
public sealed class TrayAndPanesTests
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 21, 14, 5, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("RateLimitedUntil(2026-10-21T14:05:00.0000000+00:00)")]
    [InlineData("RateLimitedUntilEstimated(2026-10-21T14:05:00.0000000+00:00)")]
    [InlineData(" RateLimitedUntilEstimated(2026-10-21T16:05:00.0000000+02:00) ")]
    public void The_end_of_a_wait_for_quota_is_read_whether_announced_or_estimated(string blockReason)
    {
        Assert.Equal(Reset, WorkStatus.QuotaResetOf(blockReason));
    }

    [Theory]
    [InlineData("RateLimitedUntilEstimated(no es una hora)")]
    [InlineData("RateLimitedUntilSoon(2026-10-21T14:05:00.0000000+00:00)")]
    [InlineData("Interrupted")]
    public void Anything_else_has_no_reset(string blockReason)
    {
        Assert.Null(WorkStatus.QuotaResetOf(blockReason));
    }

    [Fact]
    public async Task A_run_waiting_an_estimated_time_shows_its_end_in_the_tooltip()
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        monitor.Attach(
            new FakeRun(new RunSnapshot(new RunId("20261021-aaaaaa"), RunPhase.WaitingForQuota, [FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "RateLimitedUntilEstimated(2026-10-21T14:05:00.0000000+00:00)")], FakeRun.Healthy, 1)),
            ProjectA);
        await Task.Yield();

        var status = WorkStatus.Of(new RunWork(RootA, monitor, () => ProjectA));

        Assert.Equal(Reset, status.QuotaUntil);
        Assert.True(status.WaitsForQuota);
        Assert.Contains("hasta las 14:05", TrayToolTip.Compose([status], ExitState.None, null, false, TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void The_floating_panes_hide_while_the_window_is_in_the_tray_and_come_back_with_it()
    {
        var panes = new MainWindowLockTests.CountingPanes();
        var visibility = new FloatingPanesVisibility(panes);

        visibility.Update(locked: false, hiddenInTray: true);
        var hidden = (panes.Hides, panes.Restores, visibility.AreHidden);
        visibility.Update(locked: false, hiddenInTray: false);

        Assert.Equal((1, 0, true), hidden);
        Assert.Equal((1, 1, false), (panes.Hides, panes.Restores, visibility.AreHidden));
    }

    [Fact]
    public void Hidden_for_the_tray_and_for_the_lock_they_come_back_once_after_the_last_of_them()
    {
        var panes = new MainWindowLockTests.CountingPanes();
        var visibility = new FloatingPanesVisibility(panes);

        visibility.Update(locked: false, hiddenInTray: true);
        visibility.Update(locked: true, hiddenInTray: true);

        // Shown again while locked: they stay hidden until the unlock.
        visibility.Update(locked: true, hiddenInTray: false);
        var whileLocked = (panes.Hides, panes.Restores);
        visibility.Update(locked: false, hiddenInTray: false);

        Assert.Equal((1, 0), whileLocked);
        Assert.Equal((1, 1), (panes.Hides, panes.Restores));
    }
}
