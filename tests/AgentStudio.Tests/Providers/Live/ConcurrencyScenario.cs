using AgentStudio.Application.Providers;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>What one run produced: its outcome and every event it emitted.</summary>
internal sealed record ScenarioRun(AgentRunOutcome Outcome, IReadOnlyList<AgentEvent> Events)
{
    public DateTimeOffset SessionStartedAt => Events.OfType<SessionStarted>().Single().At;

    public DateTimeOffset FinishedAt => Events.OfType<RunFinished>().Single().At;
}

/// <summary>Two runs started at the same time, in the order their <c>StartAsync</c> completed.</summary>
/// <param name="Runs">The two runs, first to start first.</param>
/// <param name="SecondStartWasQueued">True when the second <c>StartAsync</c> was still pending while the first run was alive.</param>
/// <param name="Processes">The CLI processes seen by the recording runner.</param>
internal sealed record ConcurrencyResult(IReadOnlyList<ScenarioRun> Runs, bool SecondStartWasQueued, IReadOnlyList<RecordedProcess> Processes);

/// <summary>
/// The concurrency scenario of criterion F2-5, shared by the live test (real Claude Code) and its offline twin (fake CLI):
/// two runs are requested at once from the same composed provider and everything observable about them is recorded.
/// </summary>
internal static class ConcurrencyScenario
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    /// <param name="provider">The composed provider under test (the same instance serves both runs).</param>
    /// <param name="recorder">The runner the adapter behind <paramref name="provider"/> was built with.</param>
    /// <param name="requestFor">Builds the request of the run labelled "A" or "B".</param>
    /// <param name="queuedProbe">How long to wait, once the first run started, before checking that the second one is still queued.</param>
    public static async Task<ConcurrencyResult> RunTwoAtOnceAsync(
        IAgentProvider provider,
        RecordingProcessRunner recorder,
        Func<string, AgentRunRequest> requestFor,
        TimeSpan queuedProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(requestFor);

        // Cancelled on the way out, so a start still waiting in the queue after a failure never spawns a CLI nobody watches.
        using var startToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startA = provider.StartAsync(requestFor("A"), startToken.Token);
        var startB = provider.StartAsync(requestFor("B"), startToken.Token);

        var started = new List<IAgentRun>();
        Task<IAgentRun>? queuedStart = null;
        var runs = new List<ScenarioRun>();
        var queued = false;
        try
        {
            var firstStart = await Task.WhenAny(startA, startB).WaitAsync(RunTimeout, cancellationToken);
            queuedStart = ReferenceEquals(firstStart, startA) ? startB : startA;
            var first = await firstStart;
            started.Add(first);

            // A limiter that does not work shows itself in this window: the other start would complete (journal entry, spawn).
            await Task.Delay(queuedProbe, cancellationToken);
            queued = !queuedStart.IsCompleted;

            runs.Add(await CompleteAsync(first, cancellationToken));
            var second = await queuedStart.WaitAsync(RunTimeout, cancellationToken);
            queuedStart = null;
            started.Add(second);
            runs.Add(await CompleteAsync(second, cancellationToken));
        }
        finally
        {
            await startToken.CancelAsync();
            if (queuedStart is not null)
            {
                _ = DisposeWhenStartedAsync(queuedStart);
            }

            foreach (var run in started)
            {
                await run.DisposeAsync();
            }
        }

        return new ConcurrencyResult(runs, queued, recorder.Processes);
    }

    /// <summary>Asserts the F2-5 property for a limit of one: the two CLI processes were never alive at the same time.</summary>
    public static void AssertNeverOverlapped(ConcurrencyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Assert.True(result.SecondStartWasQueued, "The second StartAsync completed while the first run was still going: nothing queued it.");

        var processes = result.Processes.OrderBy(p => p.RequestedAt).ToList();
        Assert.Equal(2, processes.Count);
        var (earlier, later) = (processes[0], processes[1]);
        Assert.NotEqual(earlier.ProcessId, later.ProcessId);
        Assert.NotNull(earlier.ExitedAt);
        Assert.NotNull(later.ExitedAt);
        Assert.False(
            earlier.OverlapsWith(later),
            $"The CLI processes overlapped: pid {earlier.ProcessId} {Describe(earlier)}; pid {later.ProcessId} {Describe(later)}.");
        Assert.True(
            later.RequestedAt >= earlier.ExitedAt,
            $"The second process was requested at {later.RequestedAt:O}, before the first one exited at {earlier.ExitedAt:O}.");

        // The same property seen through the normalised events of the adapter.
        var (first, second) = (result.Runs[0], result.Runs[1]);
        Assert.True(
            second.SessionStartedAt >= first.FinishedAt,
            $"The second session started at {second.SessionStartedAt:O}, before the first run finished at {first.FinishedAt:O}.");
    }

    /// <summary>
    /// Asserts that both runs left a prompt entry and a "Succeeded" outcome in the project's daily journal, and that the
    /// queued run was journaled only once the first one had really finished (Concurrency wraps Journaling).
    /// </summary>
    public static void AssertJournaledInOrder(string projectRoot, params string[] expectedSummaries)
    {
        var (entries, outcomes) = JournalFiles.Read(projectRoot);

        Assert.Equal(["P-0001", "P-0002"], entries.Select(e => e.Id).ToArray());
        Assert.Equal(expectedSummaries.Order(StringComparer.Ordinal).ToArray(), entries.Select(e => e.Summary).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal("Succeeded", outcome.Status));

        var firstOutcome = outcomes.Single(o => o.Id == entries[0].Id);
        Assert.True(
            entries[1].LocalTime >= firstOutcome.LocalTime,
            $"P-0002 was journaled at {entries[1].LocalTime:HH:mm:ss}, before the outcome of P-0001 ({firstOutcome.LocalTime:HH:mm:ss}).");
    }

    private static string Describe(RecordedProcess process) =>
        $"requested {process.RequestedAt:O}, exited {process.ExitedAt:O}";

    private static async Task<ScenarioRun> CompleteAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        var events = await LiveTestSupport.DrainAsync(run, cancellationToken).WaitAsync(RunTimeout, cancellationToken);
        var outcome = await run.Completion.WaitAsync(RunTimeout, cancellationToken);
        return new ScenarioRun(outcome, events);
    }

    private static async Task DisposeWhenStartedAsync(Task<IAgentRun> start)
    {
        try
        {
            await using var run = await start;
        }
        catch (OperationCanceledException)
        {
            // Still queued when the token was cancelled: nothing was started.
        }
    }
}
