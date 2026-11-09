using System.IO;
using System.Text.Json.Nodes;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// F13-10 (docs/17 D-20): «Revisar el orden con el orquestador» runs only with the person's OK, once per OK; it is an auxiliary
/// read-only run recorded under its own run with the purpose <c>OrderReview</c> and its answer as evidence; and every
/// suggestion is checked again (the tasks exist, <c>TaskGraph</c> admits the dependency) and offered as a fix, never applied.
/// With a scripted orchestrator: no CLI is launched.
/// </summary>
public sealed class OrderReviewServiceTests
{
    private static readonly RunId PlanningRun = new("20261015-a1b2c3");

    /// <summary>T1 and T2 write apart; I1 integrates both; R1 reviews.</summary>
    private static PlanDefinition EditedPlan() =>
        Plan(
            Work("T1").Covering("RQ-01").Verified("build", "test"),
            Work("T2", "ejecutor2", WorkspaceMode.Write, "src/Other/"),
            Node("I1", AgentStudio.Domain.Work.WorkTaskKind.Integration).After(On("T1"), On("T2")),
            Node("R1", AgentStudio.Domain.Work.WorkTaskKind.Review).After(On("I1")));

    private static JsonObject Note(string left, string leftName, string right, string rightName, string reason) =>
        new() { [leftName] = left, [rightName] = right, ["reason"] = reason };

    private static string Answer(JsonArray? dependencies = null, JsonArray? parallel = null, JsonArray? harmful = null) =>
        new JsonObject { ["dependencies"] = dependencies ?? [], ["parallelOk"] = parallel ?? [], ["harmful"] = harmful ?? [] }.ToJsonString();

    private sealed class World : IDisposable
    {
        public World()
        {
            Service = new OrderReviewService(new AuxiliaryAgentRunner(Harness.Decisions, Harness.Evidence, Harness.Providers, Harness.Time), Harness.Worktrees, Consents, Harness.Time);
        }

        public QuotaConsentLedger Consents { get; } = new();

        public EngineHarness Harness { get; } = new();

        public OrderReviewService Service { get; }

        public QuotaConsent Consent(TimeSpan? ago = null) => new("miveritas", Harness.Time.GetUtcNow() - (ago ?? TimeSpan.Zero), "Revisar el orden: ~3.000 tokens (estimado)");

        public OrderReviewRequest Request(QuotaConsent? consent = null, PlanDefinition? plan = null) =>
            new(Harness.Project, PlanningRun, plan ?? EditedPlan(), Roster(), new AgentId("orquestador"), consent ?? Consent());

        public Task<OrderReviewResult> ReviewAsync(OrderReviewRequest request) => Service.ReviewAsync(request, null, TestContext.Current.CancellationToken);

        public void Dispose() => Harness.Dispose();
    }

    [Fact]
    public async Task It_reads_the_base_of_the_planning_with_read_only_tools_and_records_an_auxiliary_run_with_its_answer()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Answer() });

        var result = await world.ReviewAsync(world.Request());

        var request = Assert.Single(world.Harness.Claude.Starts).Request;
        Assert.Equal(["Read", "Grep", "Glob"], request.Permissions.AllowedTools);
        Assert.False(request.Permissions.AllowFileWrites || request.Permissions.AllowShell || request.Permissions.AllowNetwork);
        Assert.EndsWith(Path.Combine(PlanningRun.Value, "_base"), request.WorkingDirectory, StringComparison.Ordinal);
        Assert.Equal(PlanSchema.OrderReviewSchema(["I1", "R1", "T1", "T2"]), request.OutputJsonSchema);
        Assert.Contains("<plan>", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"Id\":\"T2\"", request.Prompt, StringComparison.Ordinal);

        Assert.StartsWith(AuxiliaryAgentRunner.RunPrefix, result.Run.Value, StringComparison.Ordinal);
        var (_, started) = Assert.Single(world.Harness.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunPurpose.OrderReview, result.Run.Value), (started.Purpose, started.RunId));
        Assert.NotNull(result.AnswerEvidence);
        Assert.Equal(EvidenceOrigin.ReportedByAgent, world.Harness.Evidence.Record(result.AnswerEvidence!.Value).Origin);
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Each_note_is_checked_again_and_offered_as_a_fix_and_nothing_is_applied()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = Answer(
                [
                    Note("T1", "from", "T2", "to", "T2 usa la API que crea T1"),
                    Note("T1", "from", "I1", "to", "I1 integra T1"),
                    Note("R1", "from", "T1", "to", "Esto haría un ciclo"),
                    Note("T9", "from", "T1", "to", "T9 no está en el plan"),
                ],
                [Note("T1", "a", "T2", "b", "Carpetas distintas")],
                [Note("T2", "first", "T1", "then", "T2 antes que T1 rompería la compilación")]),
        });
        var plan = EditedPlan();

        var result = await world.ReviewAsync(world.Request(plan: plan));

        Assert.Equal(
            [
                new OrderSuggestion(OrderSuggestionKind.Dependency, new TaskId("T1"), new TaskId("T2"), "T2 usa la API que crea T1", false, new AddDependencyFix(new TaskId("T2"), new TaskId("T1"))),
                new OrderSuggestion(OrderSuggestionKind.Dependency, new TaskId("T1"), new TaskId("I1"), "I1 integra T1", true, null),
                new OrderSuggestion(OrderSuggestionKind.Dependency, new TaskId("R1"), new TaskId("T1"), "Esto haría un ciclo", false, null),
                new OrderSuggestion(OrderSuggestionKind.ParallelOk, new TaskId("T1"), new TaskId("T2"), "Carpetas distintas", true, null),
                new OrderSuggestion(OrderSuggestionKind.Harmful, new TaskId("T2"), new TaskId("T1"), "T2 antes que T1 rompería la compilación", false, new AddDependencyFix(new TaskId("T2"), new TaskId("T1"))),
            ],
            result.Suggestions);
        Assert.Single(result.Discarded);
        Assert.Equal(plan.ContentHash, EditedPlan().ContentHash);
        Assert.Empty(world.Harness.Plans.All());
        Assert.DoesNotContain(world.Harness.Entries, e => e.Kind is DecisionKind.PlanCreated or DecisionKind.PlanRevised);
    }

    [Fact]
    public async Task Without_a_fresh_OK_for_this_use_nothing_is_launched()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Answer() });
        var consent = world.Consent();
        await world.ReviewAsync(world.Request(consent));

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.ReviewAsync(world.Request(consent)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.ReviewAsync(world.Request(world.Consent(TimeSpan.FromMinutes(11)))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.ReviewAsync(world.Request(world.Consent(TimeSpan.FromMinutes(-5)))));
        await Assert.ThrowsAsync<ArgumentNullException>(() => world.ReviewAsync(world.Request() with { Consent = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => world.ReviewAsync(world.Request() with { Orchestrator = new AgentId("ejecutor") }));

        Assert.Single(world.Harness.Claude.Starts);
    }

    [Fact]
    public async Task A_run_that_fails_or_an_answer_out_of_the_schema_gives_no_suggestion()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, AgentScript.Failing("Límite de uso alcanzado."));
        world.Harness.Claude.Script("orquestador", null, 2, new AgentScript { StructuredOutput = "{\"dependencies\":[]}" });

        var failed = await world.ReviewAsync(world.Request());
        var broken = await world.ReviewAsync(world.Request(world.Consent(TimeSpan.FromSeconds(1)))); // a second OK, for a second use

        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Empty(failed.Suggestions);
        Assert.NotNull(failed.Error);
        Assert.Empty(broken.Suggestions);
        Assert.NotEmpty(broken.Discarded);
        Assert.NotNull(broken.Error);
    }
}
