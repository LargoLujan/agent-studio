using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

// What happens to the answers of the ports while the plan changes, the document is read again or it is closed (P49): the answers come
// in any order and each one is kept for the question it answers, a late one after a reset is dropped, closing or reading again cancels
// what is being asked, and the ports are asked off the thread that reads the draft.
public sealed partial class MontageFactsCacheTests
{
    /// <summary>
    /// A port that never answers by itself: the test answers each call, or the token the call was given cancels it when the port
    /// <paramref name="honoursToken"/>. It keeps what each call was given, so the test can tell the calls apart.
    /// </summary>
    private sealed class Gate<T>(bool honoursToken)
    {
        private readonly object _lock = new();
        private readonly List<(CancellationToken Token, object? What, TaskCompletionSource<T> Answer)> _calls = [];

        public int Calls
        {
            get
            {
                lock (_lock)
                {
                    return _calls.Count;
                }
            }
        }

        public CancellationToken TokenOf(int call)
        {
            lock (_lock)
            {
                return _calls[call].Token;
            }
        }

        /// <summary>The index of the call that was given <paramref name="what"/>.</summary>
        public int IndexOf(object what)
        {
            lock (_lock)
            {
                return _calls.FindIndex(c => Equals(c.What, what));
            }
        }

        public Task<T> Ask(CancellationToken ct, object? what = null)
        {
            var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _calls.Add((ct, what, answer));
            }

            if (honoursToken)
            {
                ct.Register(() => answer.TrySetCanceled(ct));
            }

            return answer.Task;
        }

        public void Answer(int call, T value)
        {
            TaskCompletionSource<T> answer;
            lock (_lock)
            {
                answer = _calls[call].Answer;
            }

            answer.TrySetResult(value);
        }

        public async Task WaitForCallsAsync(int count)
        {
            for (var i = 0; i < 1_000 && Calls < count; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(Calls >= count, $"The port was asked {Calls} times, not {count}.");
        }
    }

    private sealed class GatedEstimator(Gate<PlanEstimate> gate) : IUsageEstimator
    {
        public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>The call says how many tasks the plan it was asked about has.</summary>
        public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct) => gate.Ask(ct, request.Plan.Tasks.Count);
    }

    private sealed class GatedContextPacker(Gate<ContextPack> gate) : IContextPacker
    {
        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct) => gate.Ask(ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1_000 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "What the test waited for did not happen.");
    }

    /// <summary>The plan of <see cref="TwoTasks"/> and one more task: another shape, with the estimate of another number of tasks.</summary>
    private static PlanDefinition ThreeTasks() => Plan([.. TwoTasks().Tasks, Writing("T3", "ejecutor", "src/Third/")]);

    // ---- answers that come in any order ----

    [Fact]
    public async Task Answers_that_come_in_any_order_are_each_kept_for_the_plan_they_answer()
    {
        foreach (var answerThreeTasksFirst in new[] { false, true })
        {
            var gate = new Gate<PlanEstimate>(honoursToken: false);
            var changed = 0;
            var cache = new MontageFactsCache(new BuilderServices(Estimator: new GatedEstimator(gate)), () => Interlocked.Increment(ref changed));
            var two = Subject(TwoTasks());
            var three = Subject(ThreeTasks());

            // The plan goes from two tasks to three and back to two (an undo) while the estimator is still reading the log.
            Assert.Null(cache.Read(two).EstimatedAgentRuns);
            Assert.Null(cache.Read(three).EstimatedAgentRuns);
            Assert.Null(cache.Read(two).EstimatedAgentRuns);
            await gate.WaitForCallsAsync(2);
            var (forTwo, forThree) = (gate.IndexOf(2), gate.IndexOf(3));
            Assert.True(forTwo >= 0 && forThree >= 0);

            // The first answer to arrive is the one of the plan that is not on the screen, or the one that is.
            var (first, second) = answerThreeTasksFirst ? (forThree, forTwo) : (forTwo, forThree);
            gate.Answer(first, Runs(low: 1, high: first == forTwo ? 20 : 30));
            await WaitUntilAsync(() => Volatile.Read(ref changed) == 1);
            Assert.Equal(first == forTwo ? (long?)20 : null, cache.Read(two).EstimatedAgentRuns);
            Assert.Equal(first == forThree ? (long?)30 : null, cache.Read(three).EstimatedAgentRuns);

            gate.Answer(second, Runs(low: 1, high: second == forTwo ? 20 : 30));
            await cache.Pending;

            // Each plan has its own answer, and the plan that went back is not asked about a third time.
            Assert.Equal(2, Volatile.Read(ref changed));
            Assert.Equal(20, cache.Read(two).EstimatedAgentRuns);
            Assert.Equal(30, cache.Read(three).EstimatedAgentRuns);
            Assert.Equal(2, gate.Calls);
        }
    }

    // ---- a reset ----

    [Fact]
    public async Task A_late_answer_of_the_estimator_after_a_reset_is_dropped_and_the_plan_is_asked_again()
    {
        var gate = new Gate<PlanEstimate>(honoursToken: false);
        var changed = 0;
        var cache = new MontageFactsCache(new BuilderServices(Estimator: new GatedEstimator(gate)), () => Interlocked.Increment(ref changed));
        var subject = Subject(TwoTasks());
        cache.Read(subject);
        await gate.WaitForCallsAsync(1);

        // The estimator ignores the token and answers when the project is already read again.
        cache.Reset();
        gate.Answer(0, Runs(low: 1, high: 20));
        await cache.Pending;

        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.Null(cache.Read(subject).EstimatedAgentRuns);
        await gate.WaitForCallsAsync(2);
        gate.Answer(1, Runs(low: 1, high: 25));
        await cache.Pending;
        Assert.Equal(25, cache.Read(subject).EstimatedAgentRuns);
    }

    [Fact]
    public async Task A_late_answer_of_the_skills_after_a_reset_is_dropped()
    {
        var gate = new Gate<int>(honoursToken: false);
        var changed = 0;
        var cache = new MontageFactsCache(new BuilderServices(SkillCharacters: (_, agent, ct) => gate.Ask(ct, agent.Id.Value)), () => Interlocked.Increment(ref changed));
        var before = cache.Read(Subject(TwoTasks())).TaskContexts;
        await gate.WaitForCallsAsync(1);

        cache.Reset();
        gate.Answer(0, 4_000);
        await cache.Pending;

        // Nothing was kept and nothing told the analysis to run again; the second agent was not even asked for.
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.Equal(1, gate.Calls);
        Assert.Equal(before.Select(f => f.PreambleTokens), cache.Read(Subject(TwoTasks())).TaskContexts.Select(f => f.PreambleTokens));
    }

    // ---- cancelling ----

    [Fact]
    public async Task Reading_the_project_again_cancels_what_is_being_asked_and_asks_it_again_with_a_token_that_is_alive()
    {
        var (packs, runs, skills) = (new Gate<ContextPack>(honoursToken: true), new Gate<PlanEstimate>(honoursToken: true), new Gate<int>(honoursToken: true));
        var changed = 0;
        var cache = new MontageFactsCache(
            new BuilderServices(ContextPacker: new GatedContextPacker(packs), Estimator: new GatedEstimator(runs), SkillCharacters: (_, agent, ct) => skills.Ask(ct, agent.Id.Value)),
            () => Interlocked.Increment(ref changed));
        var subject = Subject(TwoTasks());
        cache.Read(subject);
        await Task.WhenAll(packs.WaitForCallsAsync(1), runs.WaitForCallsAsync(1), skills.WaitForCallsAsync(1));
        var tokens = new[] { packs.TokenOf(0), runs.TokenOf(0), skills.TokenOf(0) };
        Assert.All(tokens, token => Assert.False(token.IsCancellationRequested));

        cache.Reset();
        await cache.Pending;

        // Cancelled, and finished without an error and without telling the analysis anything.
        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.Equal(0, Volatile.Read(ref changed));
        cache.Read(subject);
        await Task.WhenAll(packs.WaitForCallsAsync(2), runs.WaitForCallsAsync(2), skills.WaitForCallsAsync(2));
        Assert.False(packs.TokenOf(1).IsCancellationRequested);
        Assert.False(runs.TokenOf(1).IsCancellationRequested);
        Assert.False(skills.TokenOf(1).IsCancellationRequested);
    }

    [Fact]
    public async Task Closing_the_cache_cancels_what_is_being_asked_and_nothing_is_asked_after()
    {
        var (packs, runs, skills) = (new Gate<ContextPack>(honoursToken: true), new Gate<PlanEstimate>(honoursToken: true), new Gate<int>(honoursToken: true));
        var changed = 0;
        var cache = new MontageFactsCache(
            new BuilderServices(ContextPacker: new GatedContextPacker(packs), Estimator: new GatedEstimator(runs), SkillCharacters: (_, agent, ct) => skills.Ask(ct, agent.Id.Value)),
            () => Interlocked.Increment(ref changed));
        var subject = Subject(TwoTasks());
        cache.Read(subject);
        await Task.WhenAll(packs.WaitForCallsAsync(1), runs.WaitForCallsAsync(1), skills.WaitForCallsAsync(1));
        var tokens = new[] { packs.TokenOf(0), runs.TokenOf(0), skills.TokenOf(0) };

        cache.Dispose();
        await cache.Pending;

        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.Equal(0, Volatile.Read(ref changed));

        // A cache that is closed gives nothing, asks nothing and can be reset or closed again without a word.
        var facts = cache.Read(subject);
        cache.Reset();
        cache.Dispose();
        await cache.Pending;
        Assert.Empty(facts.TaskContexts);
        Assert.Null(facts.EstimatedAgentRuns);
        Assert.Equal((1, 1, 1), (packs.Calls, runs.Calls, skills.Calls));
    }

    // ---- off the UI thread ----

    [Fact]
    public async Task The_ports_are_asked_off_the_context_that_reads_the_draft_and_the_answers_come_back_to_it()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<SynchronizationContext?>();
        var packer = new RecordingPacker(seen);
        var estimator = new FakeEstimator(_ =>
        {
            seen.Add(SynchronizationContext.Current);
            return Runs(1, 10);
        });
        var services = new BuilderServices(
            ContextPacker: packer,
            Estimator: estimator,
            SkillCharacters: (_, _, _) =>
            {
                seen.Add(SynchronizationContext.Current);
                return Task.FromResult(100);
            });
        var cache = new MontageFactsCache(services, () => { });

        // The draft is read where the UI would be: with a context. A port that is called there would see it.
        var previous = SynchronizationContext.Current;
        var ui = new InlineSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            cache.Read(Subject(TwoTasks()));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await cache.Pending;

        // One pack (the executor that reads memory), one estimate and one count of skills for each of the two agents.
        Assert.Equal(4, seen.Count);
        Assert.All(seen, context => Assert.Null(context));
    }

    private sealed class RecordingPacker(System.Collections.Concurrent.ConcurrentBag<SynchronizationContext?> seen) : IContextPacker
    {
        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            seen.Add(SynchronizationContext.Current);
            return Task.FromResult(Pack(100));
        }
    }
}
