using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/21 D-25 and the review of the P0: an unattended resume (<see cref="ResumeScheduler"/>) may now wait up to 180 s for a remote operation of Agent Studio. Suspending the
/// resumes (the managed exit, the end of the Windows session) ends that wait at once, a resume that got the project afterwards does not go on, and a wait that runs out is
/// «reintentar»: not a failure for the rest of the session, even if the log gained entries meanwhile.
/// </summary>
public sealed class ResumeWaitsForRemoteOperationTests
{
    private static readonly RunOptions Consent = new(AutoResumeConsent: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<RunId> CutWhileRunningAsync(EngineHarness harness)
    {
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var run = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.DisposeAsync();
        await run.CompleteAsync(harness);
        return run.Id;
    }

    /// <summary>The activity of the process, counting how often something asked what holds the project: that is a caller that waits.</summary>
    private sealed class Observed(OrchestrationActivity inner) : IProjectActivity
    {
        private int _polls;

        public int Polls => Volatile.Read(ref _polls);

        public bool IsBusy(string projectId) => inner.IsBusy(projectId);

        public IDisposable? TryEnter(string projectId, ProjectActivityKind kind) => inner.TryEnter(projectId, kind);

        public ProjectActivityKind? Current(string projectId)
        {
            Interlocked.Increment(ref _polls);
            return inner.Current(projectId);
        }
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        for (var spin = 0; !condition(); spin++)
        {
            Assert.True(spin < 5_000_000, "The condition never held.");
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Suspending_during_the_wait_ends_it_at_once_and_resumes_and_writes_nothing_even_when_the_remote_operation_lets_go_later()
    {
        using var harness = new EngineHarness();
        await CutWhileRunningAsync(harness);
        var real = new OrchestrationActivity();
        var activity = new Observed(real);
        var scheduler = new ResumeScheduler(harness.WithActivity(activity), harness.Decisions, harness.Runs);
        var remote = real.TryEnter(harness.Project.ProjectId, ProjectActivityKind.RemoteOperation)!;
        var entries = harness.Entries.Count;

        var pass = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await SpinUntilAsync(() => activity.Polls >= 1);
        Assert.False(pass.IsCompleted, "It waits for the remote operation.");
        using var suspension = scheduler.Suspend();
        await scheduler.WaitForIdleAsync(Ct).WaitAsync(RunTestSupport.Guard, Ct); // no move of the clock: the wait was cut by the suspension, not by the cap

        var stopped = Assert.Single(await pass);
        Assert.Null(stopped.Resumed);
        Assert.Contains("suspendida", stopped.Reason, StringComparison.Ordinal);
        remote.Dispose();
        harness.Time.Advance(ProjectActivityEntry.RemoteWaitCap);
        Assert.Empty(harness.Payloads<RunResumedPayload>());
        Assert.Equal(entries, harness.Entries.Count);
        Assert.Empty(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
    }

    [Fact]
    public async Task A_resume_that_gets_the_project_after_the_app_started_leaving_does_not_resume_anything()
    {
        using var harness = new EngineHarness();
        var runId = await CutWhileRunningAsync(harness);
        var orchestrator = harness.WithActivity(new OrchestrationActivity());
        var entries = harness.Entries.Count;

        var result = await orchestrator.ResumeByConsentAsync(harness.Project, runId, Ct, () => false);

        Assert.Null(result.Run);
        Assert.Contains("suspendida", result.Reason, StringComparison.Ordinal);
        Assert.Equal(entries, harness.Entries.Count);
        Assert.Empty(harness.Payloads<RunResumedPayload>());
    }

    [Fact]
    public async Task A_wait_that_runs_out_is_a_retry_and_not_a_failure_for_the_session_even_if_the_log_gained_entries_meanwhile()
    {
        using var harness = new EngineHarness();
        await CutWhileRunningAsync(harness);
        var real = new OrchestrationActivity();
        var scheduler = new ResumeScheduler(harness.WithActivity(real), harness.Decisions, harness.Runs);
        var remote = real.TryEnter(harness.Project.ProjectId, ProjectActivityKind.RemoteOperation)!;

        var pass = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await harness.Decisions.RecordAsync(
            harness.Project.ProjectId,
            new Actor(ActorKind.System, "agent-studio"),
            DecisionKind.HostingOperationPerformed,
            "Una operación remota anotada durante la espera",
            HostingOperationPerformedPayload.Of(
                HostingOperationKind.PushRunBranch, "20261113-a1b2c3", HostingP0(), null, null, null, null, "g-1", null, HostingOutcome.Ok, null, null, null, null, null, null),
            cancellationToken: Ct);
        await RemoteWaitClock.AdvanceUntilAsync(harness.Time, pass, ProjectActivityEntry.RemoteWaitPoll);

        var gaveUp = Assert.Single(await pass);
        Assert.Null(gaveUp.Resumed);
        Assert.StartsWith("No se pudo reanudar", gaveUp.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ya no se intenta", gaveUp.Reason, StringComparison.Ordinal);

        remote.Dispose();
        var again = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        Assert.DoesNotContain("ya no se intenta", again.Reason, StringComparison.Ordinal);
        await using var resumed = again.Resumed!;
        Assert.Equal(RunResumeCause.ConsentAfterUnrequestedClose, again.Cause);
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    private static RemoteRepository HostingP0() => RemoteRepository.TryCreateGitHub("LargoLujan", "agent-studio")!;
}
