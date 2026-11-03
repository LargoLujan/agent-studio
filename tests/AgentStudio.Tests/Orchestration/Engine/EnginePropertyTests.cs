using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The properties of docs/13 section 8 over random worlds (about 200 plans per seed, seeded <see cref="Random"/>, no
/// FsCheck): each run is driven to its end, a person answering when the run waits for one, and checked against:
/// <list type="number">
/// <item>no task starts before its edges are satisfied, in log order;</item>
/// <item>the run always ends, and every task that did not finish is explained by the health analysis or its block reason;</item>
/// <item>no provider ever runs more agents than its limit;</item>
/// <item>the decision log verifies after every run;</item>
/// <item>replaying the log gives the live snapshot (also at every point the run waited for a person);</item>
/// <item>every completed task that must prove its result has evidence, its attempt record first;</item>
/// <item>a failure at entry k, recovery and resume never repeat an attempt and never redo completed work; nor do they ask
/// the judge again for a review whose verdict is in the log, and every launch the log shows (the judge's included, which
/// carry no task) ends in the log.</item>
/// </list>
/// Each property also checks that the generator reached the cases that matter.
/// </summary>
public sealed class EnginePropertyTests
{
    private const int Worlds = 200;

    private sealed class Coverage
    {
        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int Cancelled { get; set; }

        public int Rests { get; set; }

        public int Retries { get; set; }

        public int Skips { get; set; }

        public int Conditions { get; set; }

        public int Reviews { get; set; }

        public int Integrations { get; set; }

        public int Approvals { get; set; }

        public int Budgets { get; set; }

        public int Crashes { get; set; }

        public int CompletedBeforeCrash { get; set; }

        public int InterruptedAttempts { get; set; }

        public int VerdictsBeforeCrash { get; set; }

        public int JudgeLaunchesInterrupted { get; set; }

        public int MaxConcurrency { get; set; }

        public override string ToString() =>
            $"succeeded {Succeeded}, failed {Failed}, cancelled {Cancelled}, rests {Rests}, retries {Retries}, skips {Skips}, conditions {Conditions}, reviews {Reviews}, integrations {Integrations}, approvals {Approvals}, budgets {Budgets}, crashes {Crashes}, completed before crash {CompletedBeforeCrash}, interrupted {InterruptedAttempts}, verdicts before crash {VerdictsBeforeCrash}, judge launches interrupted {JudgeLaunchesInterrupted}, max concurrency {MaxConcurrency}";
    }

    [Fact]
    public async Task Properties_1_to_6_hold_for_every_random_world()
    {
        var random = new Random(26101601);
        var coverage = new Coverage();
        for (var i = 0; i < Worlds; i++)
        {
            var world = RandomWorld.Generate(random);
            using var harness = new EngineHarness(world.ClaudeLimit, world.CodexLimit);
            world.Wire(harness);
            var context = $"world {i}`n{world.Describe()}`n";
            await using var run = await harness.StartAsync(world.Plan, world.Options);
            var result = await DriveAsync(harness, world, run, random, coverage, context);
            await CheckAsync(harness, world, run, result, coverage, context);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(coverage.ToString());
        Assert.True(coverage.Succeeded > 20 && coverage.Failed > 20 && coverage.Cancelled > 3, coverage.ToString());
        Assert.True(coverage.Rests > 20 && coverage.Retries > 40 && coverage.Skips > 20, coverage.ToString());
        Assert.True(coverage.Conditions > 30 && coverage.Reviews > 20 && coverage.Integrations > 20 && coverage.Approvals > 20, coverage.ToString());
        Assert.True(coverage.Budgets > 2 && coverage.MaxConcurrency >= 2, coverage.ToString());
    }

    [Fact]
    public async Task Property_7_a_failure_at_entry_k_then_recovery_and_resume_never_repeat_an_attempt()
    {
        var random = new Random(26101602);
        var coverage = new Coverage();
        for (var i = 0; i < Worlds; i++)
        {
            var world = RandomWorld.Generate(random);
            var context = $"world {i}`n{world.Describe()}`n";

            // A reference run tells how long the log of this world gets; the crash falls somewhere after the start.
            int total;
            using (var reference = new EngineHarness(world.ClaudeLimit, world.CodexLimit))
            {
                world.Wire(reference);
                await using var referenceRun = await reference.StartAsync(world.Plan, world.Options);
                await DriveAsync(reference, world, referenceRun, new Random(i), new Coverage(), context + " (reference)");
                total = reference.Entries.Count;
            }

            using var harness = new EngineHarness(world.ClaudeLimit, world.CodexLimit);
            world.Wire(harness);
            var startEntries = 5 + world.Plan.Tasks.Count + world.Plan.Tasks.Count(t => t.Agent is not null);
            var crashAt = random.Next(startEntries + 1, Math.Max(startEntries + 2, total + 1));
            harness.LogStore.FailFromSequence = crashAt;
            var first = await harness.StartAsync(world.Plan, world.Options);
            var interrupted = await DriveAsync(harness, world, first, random, coverage, context + $" (failing at entry {crashAt})", checkAtRest: false);
            await first.DisposeAsync();
            var stored = harness.LogStore.Entries(harness.Project.ProjectId).Count;
            harness.LogStore.FailFromSequence = null;
            if (interrupted.Phase != RunPhase.Interrupted)
            {
                // Only a run that never needed entry k may end normally (this one interleaved differently from the reference).
                Assert.True(stored < crashAt, $"{context}: the store refused entry {crashAt} but the run ended {interrupted.Phase}");
                continue;
            }

            Assert.Equal(crashAt - 1, stored);

            coverage.Crashes++;
            var before = harness.Entries.Count;
            var completedBefore = CompletedTasks(harness, first.Id);
            var startsBefore = Starts(harness, first.Id);
            var judgedBefore = Judged(harness, first.Id);
            var reviewsBefore = harness.Review.Requests.Count;
            coverage.CompletedBeforeCrash += completedBefore.Count;
            coverage.VerdictsBeforeCrash += judgedBefore.Count;

            await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken);
            var result = await DriveAsync(harness, world, resumed, random, coverage, context + $" (resumed after a failure at entry {crashAt})");

            var startsAfter = Starts(harness, first.Id).Skip(startsBefore.Count).ToList();
            var all = Starts(harness, first.Id);
            Assert.True(all.Distinct().Count() == all.Count, $"{context}: an attempt was launched twice: {string.Join(", ", all)}\n{harness.Describe()}");
            Assert.True(
                startsAfter.All(s => !completedBefore.Contains(s.Task)),
                $"{context}: completed work was redone after resuming: {string.Join(", ", startsAfter.Where(s => completedBefore.Contains(s.Task)))}");

            // The judge's launches too: a review whose verdict was already in the log is never asked again, and every
            // launch the log shows (executors', planner's and judge's, which carry no task) has its end in the log.
            var askedAgain = harness.Review.Requests.Skip(reviewsBefore).Select(r => r.ReviewTask).Where(judgedBefore.Contains).ToList();
            Assert.True(askedAgain.Count == 0, $"{context}: the judge was asked again for {string.Join(", ", askedAgain)}, whose verdict was in the log\n{harness.Describe()}");
            var open = OpenLaunches(harness, first.Id);
            Assert.True(open.Count == 0, $"{context}: launches without an end in the log: {string.Join(", ", open)}\n{harness.Describe()}");
            coverage.InterruptedAttempts += harness.Payloads<AgentRunFinishedPayload>().Count(p => p.Entry.Sequence > before && p.Payload.Status == AttemptOutcome.Interrupted);
            coverage.JudgeLaunchesInterrupted += harness.Payloads<AgentRunFinishedPayload>().Count(p => p.Entry.Sequence > before && p.Payload is { Task: null, Status: AttemptOutcome.Interrupted });
            await CheckAsync(harness, world, resumed, result, coverage, context + " (resumed)");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(coverage.ToString());
        Assert.True(coverage.Crashes > 100, coverage.ToString());
        Assert.True(coverage.CompletedBeforeCrash > 100 && coverage.InterruptedAttempts > 10, coverage.ToString());
        Assert.True(coverage.VerdictsBeforeCrash > 10, coverage.ToString());
    }

    // ---- driving a run ----

    private static bool Resting(RunSnapshot snapshot) =>
        snapshot.Phase is RunPhase.Blocked or RunPhase.AwaitingHuman
        && snapshot.Tasks.Any(t => t.Status == WorkTaskStatus.Blocked)
        && !snapshot.Tasks.Any(t => t.Status is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval);

    /// <summary>Runs to the end; when the run waits for a person, checks it there and then retries what is blocked (or cancels).</summary>
    private static async Task<RunResult> DriveAsync(EngineHarness harness, RandomWorld world, IOrchestrationRun run, Random random, Coverage coverage, string context, bool checkAtRest = true)
    {
        for (var interventions = 0; ; interventions++)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!run.Completion.IsCompleted && !Resting(run.Snapshot()))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"{context}: the run neither ended nor rested.\n{RunTestSupport.Describe(run.Snapshot())}\n{harness.Describe()}");
                }

                await Task.Delay(2);
            }

            if (run.Completion.IsCompleted)
            {
                return await run.Completion;
            }

            var snapshot = run.Snapshot();
            coverage.Rests++;
            if (checkAtRest)
            {
                await CheckRestingAsync(harness, world, run, snapshot, context);
            }

            if (interventions >= 10 || random.Next(100) < 25)
            {
                await run.CancelAsync("fin de la prueba");
                return await run.CompleteAsync(harness);
            }

            var blocked = snapshot.Tasks.Where(t => t.Status == WorkTaskStatus.Blocked).Select(t => t.Id).ToList();
            try
            {
                foreach (var task in blocked)
                {
                    try
                    {
                        await run.RetryBlockedAsync(task);
                    }
                    catch (InvalidOperationException) when (run.Completion.IsCompleted || run.Snapshot().View(task.Value).Status != WorkTaskStatus.Blocked)
                    {
                        // An earlier retry already moved the run on: it ended (a task failed for good) or skipped this
                        // task. The engine rightly refuses to retry it; the driver goes on with what is left.
                    }
                }
            }
            catch (Exception) when (!checkAtRest)
            {
                // The store died while the loop applied the retry: the run stops, and that is what is under test.
                return await run.CompleteAsync(harness);
            }

            if (run.Completion.IsCompleted)
            {
                return await run.Completion;
            }

            await harness.WaitUntilAsync(run, s => run.Completion.IsCompleted || s.Tasks.Where(t => blocked.Contains(t.Id)).All(t => t.Status != WorkTaskStatus.Blocked), $"{context}: unblocked");
        }
    }

    // ---- the properties ----

    private static async Task CheckRestingAsync(EngineHarness harness, RandomWorld world, IOrchestrationRun run, RunSnapshot snapshot, string context)
    {
        var unexplained = Unexplained(snapshot, world, budgetSpent: harness.Alerts("run.budget-exhausted").Count > 0, cancelled: false);
        Assert.True(unexplained.Count == 0, $"{context}: resting with unexplained tasks {string.Join(", ", unexplained)}\n{RunTestSupport.Describe(snapshot)}\n{harness.Describe()}");
        await CheckReplayAsync(harness, run.Id, snapshot, context + " (at rest)");
    }

    private static async Task CheckAsync(EngineHarness harness, RandomWorld world, IOrchestrationRun run, RunResult result, Coverage coverage, string context)
    {
        var snapshot = run.Snapshot();
        Assert.True(result.Phase is RunPhase.Succeeded or RunPhase.Failed or RunPhase.Cancelled, $"{context}: ended {result.Phase}: {result.Summary}\n{harness.Describe()}");
        switch (result.Phase)
        {
            case RunPhase.Succeeded:
                coverage.Succeeded++;
                Assert.All(snapshot.Tasks, t => Assert.True(t.Status is WorkTaskStatus.Completed or WorkTaskStatus.Skipped, $"{context}: {t.Id} is {t.Status} in a run that succeeded"));
                break;
            case RunPhase.Failed:
                coverage.Failed++;
                break;
            default:
                coverage.Cancelled++;
                break;
        }

        // 1. No start before the edges are satisfied, in log order.
        var events = harness.RunEvents(run.Id);
        var record = (await harness.Runs.FindAsync(harness.Project.ProjectId, run.Id, TestContext.Current.CancellationToken))!;
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index] is TaskTransitioned { From: WorkTaskStatus.Pending, To: WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked } start)
            {
                var before = RunStateReplayer.Replay(world.Plan, record.CreatedAt, events.Take(index));
                Assert.True(
                    before.Graph.ReadyTasks().Any(t => t.Id == start.Task),
                    $"{context}: {start.Task} went {start.From}>{start.To} at event {index + 1} before its prerequisites were satisfied\n{harness.Describe()}");
            }
        }

        // 2. Every task that did not finish is explained.
        var budgetSpent = harness.Alerts("run.budget-exhausted").Count > 0;
        coverage.Budgets += budgetSpent ? 1 : 0;
        var unexplained = Unexplained(snapshot, world, budgetSpent, cancelled: result.Phase == RunPhase.Cancelled);
        Assert.True(unexplained.Count == 0, $"{context}: unexplained tasks {string.Join(", ", unexplained)}\n{RunTestSupport.Describe(snapshot)}\n{harness.Describe()}");
        Assert.True(harness.Alerts("graph.deadlock").Count == 0, $"{context}: a false deadlock alert\n{harness.Describe()}");

        // 3. Never above the limit of a provider.
        Assert.True(harness.Claude.MaxObservedConcurrency <= world.ClaudeLimit, $"{context}: Claude ran {harness.Claude.MaxObservedConcurrency} with limit {world.ClaudeLimit}");
        Assert.True(harness.Codex.MaxObservedConcurrency <= world.CodexLimit, $"{context}: Codex ran {harness.Codex.MaxObservedConcurrency} with limit {world.CodexLimit}");
        coverage.MaxConcurrency = Math.Max(coverage.MaxConcurrency, Math.Max(harness.Claude.MaxObservedConcurrency, harness.Codex.MaxObservedConcurrency));

        // 4. The log verifies.
        var log = await harness.Decisions.LoadAsync(harness.Project.ProjectId, TestContext.Current.CancellationToken);
        Assert.True(log.Verification.IsValid, $"{context}: {log.Verification.Problem} at {log.Verification.FirstInvalidSequence}");

        // 5. Replay gives the live snapshot.
        await CheckReplayAsync(harness, run.Id, snapshot, context);

        // 6. Completed work that must prove itself has evidence, its attempt record first.
        foreach (var task in snapshot.Tasks.Where(t => t.Status == WorkTaskStatus.Completed && t.Kind is WorkTaskKind.AgentWork or WorkTaskKind.ToolRun or WorkTaskKind.Integration))
        {
            Assert.True(task.Evidence.Count > 0, $"{context}: {task.Id} completed without evidence");
            Assert.All(task.Evidence, e => Assert.True(harness.Evidence.Contains(e), $"{context}: {task.Id} cites missing evidence {e}"));
            var first = harness.Evidence.Record(task.Evidence[0]);
            Assert.True(first.Kind == EvidenceKind.Document && first.Origin == EvidenceOrigin.CapturedBySystem && first.Title.StartsWith("Registro del intento", StringComparison.Ordinal), $"{context}: the first evidence of {task.Id} is not its attempt record");
        }

        var transitions = harness.Payloads<TaskStatusChangedPayload>().Select(p => p.Payload).Where(p => p.RunId == run.Id.Value).ToList();
        coverage.Retries += transitions.Count(t => t is { From: WorkTaskStatus.Failed, To: WorkTaskStatus.Pending });
        coverage.Skips += transitions.Count(t => t.To == WorkTaskStatus.Skipped);
        coverage.Conditions += harness.Payloads<ConditionEvaluatedPayload>().Count(p => p.Payload.RunId == run.Id.Value);
        coverage.Reviews += harness.Review.Requests.Count;
        coverage.Integrations += harness.Integrator.Requests.Count;
        coverage.Approvals += harness.Approvals.Requests.Count;
    }

    private static async Task CheckReplayAsync(EngineHarness harness, RunId run, RunSnapshot snapshot, string context)
    {
        var replay = await harness.ReplayAsync(run);
        Assert.True(replay.IsResumable, $"{context}: replay problems {string.Join("; ", replay.Problems)}\n{harness.Describe()}");
        Assert.Equal(RunTestSupport.StateOf(snapshot, replay.Graph), RunTestSupport.StatusOf(replay.Graph));

        // The attempts the snapshot shows are the ones the log tells.
        var payloads = harness.Payloads<TaskStatusChangedPayload>().Where(p => p.Payload.RunId == run.Value).Select(p => p.Payload).ToList();
        foreach (var task in snapshot.Tasks)
        {
            var logged = payloads.Where(p => p.Task == task.Id).Select(p => p.Attempt).DefaultIfEmpty(0).Max();
            Assert.True(task.Attempt == logged, $"{context}: {task.Id} shows attempt {task.Attempt} but the log has {logged}");
        }
    }

    /// <summary>
    /// The tasks that did not finish and that nothing explains: not unreachable, out of retries, blocked with a structured
    /// reason, waiting for an approval, failed because the budget or the cancellation stopped its retries, or pending
    /// behind a prerequisite that is itself explained.
    /// </summary>
    private static List<string> Unexplained(RunSnapshot snapshot, RandomWorld world, bool budgetSpent, bool cancelled)
    {
        var views = snapshot.Tasks.ToDictionary(t => t.Id);
        var memo = new Dictionary<TaskId, bool>();
        bool Explained(TaskId id)
        {
            if (memo.TryGetValue(id, out var known))
            {
                return known;
            }

            var view = views[id];
            var explained = WorkTaskTransitions.IsTerminal(view.Status)
                || snapshot.Health.UnreachableTasks.Contains(id)
                || snapshot.Health.RetryExhaustedTasks.Contains(id)
                || view.Status switch
                {
                    WorkTaskStatus.Blocked => view.BlockReason is "Interrupted" or "ProviderUnavailable" or "AwaitingHuman" || (view.BlockReason?.StartsWith("RateLimitedUntil(", StringComparison.Ordinal) ?? false),
                    WorkTaskStatus.AwaitingApproval => true,
                    WorkTaskStatus.Failed => budgetSpent || cancelled,
                    WorkTaskStatus.Pending => budgetSpent || world.Specs[id].DependsOn.Any(d => !WorkTaskTransitions.IsTerminal(views[d.Prerequisite].Status) && Explained(d.Prerequisite)),
                    _ => false,
                };
            memo[id] = explained;
            return explained;
        }

        return [.. snapshot.Tasks.Where(t => !Explained(t.Id)).Select(t => $"{t.Id} {t.Status}")];
    }

    private static HashSet<TaskId> CompletedTasks(EngineHarness harness, RunId run) =>
        [.. harness.Payloads<TaskStatusChangedPayload>().Where(p => p.Payload.RunId == run.Value && p.Payload.To == WorkTaskStatus.Completed).Select(p => p.Payload.Task)];

    private static List<(TaskId Task, int Attempt)> Starts(EngineHarness harness, RunId run) =>
        [.. harness.Payloads<AgentRunStartedPayload>().Where(p => p.Payload.RunId == run.Value && p.Payload.Task is not null).Select(p => (p.Payload.Task!.Value, p.Payload.Attempt))];

    /// <summary>The review tasks with a verdict in the log.</summary>
    private static HashSet<TaskId> Judged(EngineHarness harness, RunId run) =>
        [.. harness.Payloads<VerdictIssuedPayload>().Where(p => p.Payload.RunId == run.Value && p.Payload.Task is not null).Select(p => p.Payload.Task!.Value)];

    /// <summary>The launches of the run whose end is not in the log, pairing starts and ends in log order by task (or none) and attempt.</summary>
    private static List<string> OpenLaunches(EngineHarness harness, RunId run)
    {
        var open = new List<AgentRunStartedPayload>();
        foreach (var entry in harness.Entries)
        {
            switch (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson))
            {
                case AgentRunStartedPayload started when started.RunId == run.Value:
                    open.Add(started);
                    break;
                case AgentRunFinishedPayload finished when finished.RunId == run.Value:
                    var index = open.FindIndex(o => o.Task == finished.Task && o.Attempt == finished.Attempt);
                    if (index >= 0)
                    {
                        open.RemoveAt(index);
                    }

                    break;
            }
        }

        return [.. open.Select(o => $"{o.Agent} {o.Task?.Value ?? "(sin tarea)"} #{o.Attempt}")];
    }
}
