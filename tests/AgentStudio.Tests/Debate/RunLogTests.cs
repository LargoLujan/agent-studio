using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

public sealed class RunLogTests
{
    private const string Run = "20261015-a1b2c3";
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    /// <summary>A hand-made log, for the cases the prepared world does not have.</summary>
    private sealed class Log
    {
        private readonly DecisionLog _log = new();

        public IReadOnlyList<DecisionEntry> Entries => _log.Entries;

        public DecisionEntry Add(DecisionKind kind, DecisionPayload payload) =>
            _log.Append(T0, System, kind, kind.ToString(), DecisionPayloadSerializer.Serialize(payload));

        public DecisionEntry AddRaw(DecisionKind kind, string payloadJson, long? corrects = null) =>
            _log.Append(T0, System, kind, kind.ToString(), payloadJson, corrects);
    }

    private static RosterEntry Entry(string agent, AgentRole role, VendorId vendor) => new(Aid(agent), role, new ProviderId("p-" + vendor.Value), vendor);

    private static RunStartedPayload Started(params RosterEntry[] roster) => new(HashA, HashB, "abc", null, HashA, roster, null) { RunId = Run };

    private static async Task<IReadOnlyList<DecisionEntry>> EntriesOfAsync(JudgingWorld world) =>
        RunLog.EntriesOf((await world.LoadLogAsync()).Entries, JudgingWorld.Run.Value);

    [Fact]
    public async Task The_entries_of_a_run_are_those_whose_payload_names_it()
    {
        using var world = await JudgingWorld.CreateAsync();
        var log = (await world.LoadLogAsync()).Entries;

        var entries = RunLog.EntriesOf(log, JudgingWorld.Run.Value);

        Assert.Equal(log.Count - 2, entries.Count);
        Assert.Single(entries, e => e.Kind == DecisionKind.RunStarted);
        Assert.DoesNotContain(entries, e => e.PayloadJson.Contains(JudgingWorld.OtherRun.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void A_correction_belongs_to_the_run_of_the_entry_it_corrects()
    {
        var log = new Log();
        var ours = log.Add(DecisionKind.TaskAssigned, new TaskAssignedPayload(Tid("T1"), Aid("ejecutor")) { RunId = Run });
        var theirs = log.Add(DecisionKind.TaskAssigned, new TaskAssignedPayload(Tid("T1"), Aid("ejecutor")) { RunId = "otra" });
        var fixesOurs = log.AddRaw(DecisionKind.Correction, "{}", ours.Sequence);
        log.AddRaw(DecisionKind.Correction, "{}", theirs.Sequence);
        log.AddRaw(DecisionKind.ReviewSkipped, "no es JSON");

        var entries = RunLog.EntriesOf(log.Entries, Run);

        Assert.Equal([ours.Sequence, fixesOurs.Sequence], entries.Select(e => e.Sequence));
    }

    [Fact]
    public async Task The_orchestrator_and_the_executors_produced_the_work_and_the_judge_did_not()
    {
        using var world = await JudgingWorld.CreateAsync();

        Assert.Equal([VendorId.Anthropic], RunLog.ProducerVendors(await EntriesOfAsync(world)));
    }

    [Fact]
    public async Task An_executor_on_another_vendor_is_a_producer_too()
    {
        using var world = await JudgingWorld.CreateAsync(new JudgingWorldOptions { ExecutorModel = new ModelBinding(ProviderId.GeminiCli, VendorId.Google) });

        Assert.Equal([VendorId.Anthropic, VendorId.Google], RunLog.ProducerVendors(await EntriesOfAsync(world)).OrderBy(v => v.Value));
    }

    [Fact]
    public async Task An_agent_of_the_roster_that_never_worked_did_not_produce_anything()
    {
        using var world = await JudgingWorld.CreateAsync(new JudgingWorldOptions { IdleExecutorModel = new ModelBinding(ProviderId.GeminiCli, VendorId.Google) });

        Assert.Equal([VendorId.Anthropic], RunLog.ProducerVendors(await EntriesOfAsync(world)));
    }

    [Fact]
    public async Task An_earlier_run_of_the_judge_does_not_make_the_judge_exclude_itself()
    {
        using var world = await JudgingWorld.CreateAsync();
        await world.RecordAsync(JudgingWorld.System, DecisionKind.AgentRunStarted, "Juez lanzado",
            new AgentRunStartedPayload(null, 1, JudgingWorld.Judge, ProviderId.CodexCli, VendorId.OpenAI, null, world.Workspace, world.TestEvidence.Id, HashA, []) { RunId = JudgingWorld.Run.Value });

        Assert.Equal([VendorId.Anthropic], RunLog.ProducerVendors(await EntriesOfAsync(world)));
    }

    [Fact]
    public void An_agent_that_ran_counts_with_the_vendor_it_ran_on_even_outside_the_roster()
    {
        var log = new Log();
        log.Add(DecisionKind.RunStarted, Started(Entry("orquestador", AgentRole.Orchestrator, VendorId.Anthropic), Entry("juez", AgentRole.Judge, VendorId.OpenAI)));
        log.Add(DecisionKind.AgentRunStarted, new AgentRunStartedPayload(Tid("T1"), 1, Aid("intruso"), ProviderId.GeminiCli, VendorId.Google, null, "C:\\wt", Eid("p"), HashA, []) { RunId = Run });

        Assert.Equal([VendorId.Anthropic, VendorId.Google], RunLog.ProducerVendors(log.Entries).OrderBy(v => v.Value));
    }

    [Fact]
    public void An_agent_assigned_to_work_is_a_producer_before_it_runs_but_a_reviewer_is_not()
    {
        var log = new Log();
        log.Add(DecisionKind.RunStarted, Started(
            Entry("orquestador", AgentRole.Orchestrator, VendorId.Anthropic),
            Entry("ejecutor", AgentRole.Executor, VendorId.Google),
            Entry("juez", AgentRole.Judge, VendorId.OpenAI)));
        log.Add(DecisionKind.TaskCreated, new TaskCreatedPayload(Tid("T1"), "Trabajo", WorkTaskKind.AgentWork, JoinPolicy.AllCompleted, 1) { RunId = Run });
        log.Add(DecisionKind.TaskCreated, new TaskCreatedPayload(Tid("T2"), "Revisión", WorkTaskKind.Review, JoinPolicy.AllCompleted, 0) { RunId = Run });
        log.Add(DecisionKind.TaskAssigned, new TaskAssignedPayload(Tid("T1"), Aid("ejecutor")) { RunId = Run });
        log.Add(DecisionKind.TaskAssigned, new TaskAssignedPayload(Tid("T2"), Aid("juez")) { RunId = Run });

        Assert.Equal([VendorId.Anthropic, VendorId.Google], RunLog.ProducerVendors(log.Entries).OrderBy(v => v.Value));
    }

    [Fact]
    public void Whoever_wrote_the_plan_is_a_producer_even_without_the_orchestrator_role()
    {
        var log = new Log();
        log.Add(DecisionKind.RunStarted, Started(Entry("arquitecto", AgentRole.Architect, VendorId.Google), Entry("juez", AgentRole.Judge, VendorId.OpenAI)));
        log.Add(DecisionKind.PlanCreated, new PlanCreatedPayload(HashA, PlanSource.OrchestratorAgent, Aid("arquitecto"), 2, null) { RunId = Run });

        Assert.Equal([VendorId.Google], RunLog.ProducerVendors(log.Entries));
    }

    [Fact]
    public async Task The_events_replay_needs_are_the_transitions_of_the_run_in_log_order()
    {
        using var world = await JudgingWorld.CreateAsync();
        var problems = new List<string>();

        var events = RunLog.ReplayEvents(await EntriesOfAsync(world), problems);

        Assert.Empty(problems);
        var transitions = events.Cast<TaskTransitioned>().ToList();
        Assert.Equal(
            [(JudgingWorld.Work, WorkTaskStatus.Pending, WorkTaskStatus.Running), (JudgingWorld.Work, WorkTaskStatus.Running, WorkTaskStatus.Completed), (JudgingWorld.Review, WorkTaskStatus.Pending, WorkTaskStatus.Running)],
            transitions.Select(t => (t.Task, t.From, t.To)));
        Assert.Equal([world.DiffEvidence.Id, world.TestEvidence.Id], transitions[1].Evidence);
    }

    [Fact]
    public void A_condition_contributes_the_branch_it_chose()
    {
        var log = new Log();
        log.Add(DecisionKind.ConditionEvaluated, new ConditionEvaluatedPayload(Tid("C1"), "TestsPassed(T1)", true, "yes", []) { RunId = Run });

        var events = RunLog.ReplayEvents(log.Entries, new List<string>());

        Assert.Equal(new ConditionResolved(T0, Tid("C1"), "yes"), Assert.Single(events));
    }

    [Fact]
    public void An_unreadable_transition_ends_the_events_with_a_problem()
    {
        var log = new Log();
        log.Add(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "go", null, [], 1) { RunId = Run });
        var broken = log.AddRaw(DecisionKind.TaskStatusChanged, $$"""{"v":1,"runId":"{{Run}}","task":"T1","from":"Nowhere"}""");
        log.Add(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Failed, "boom", null, [], 1) { RunId = Run });
        var problems = new List<string>();

        var events = RunLog.ReplayEvents(log.Entries, problems);

        Assert.Single(events);
        Assert.Contains(problems, p => p.Contains($"entry {broken.Sequence}", StringComparison.OrdinalIgnoreCase));
    }
}
