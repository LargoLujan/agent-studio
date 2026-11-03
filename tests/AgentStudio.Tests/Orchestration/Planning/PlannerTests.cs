using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Skills;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Planning;

public sealed class PlannerTests
{
    private const string Marker = "MARCA-UNICA-7731";

    /// <summary>The plan the F3-8 scenario expects: T1 writes and is tested, I1 integrates, R1 is reviewed by the judge.</summary>
    private static string ValidPlan() =>
        Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/", "tests/Calc.Tests/"], steps: ["build", "test"]),
            Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [Dependency("T1")], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("I1")])).ToJsonString();

    /// <summary>Valid but for one thing: the orchestrator is given a task.</summary>
    private static string PlanWithOrchestratorTask() =>
        Plan(Task("T1", agent: "orquestador", covers: ["RQ-01"])).ToJsonString();

    private static AgentScript Answer(string json) => new() { StructuredOutput = json };

    private static async Task<PlanProposal> PlanAsync(EngineHarness harness, string request = "Añade Calc.Multiply(a,b) " + Marker) =>
        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(request), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_valid_plan_comes_back_as_the_proposal_after_one_run()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.Null(proposal.Error);
        Assert.False(proposal.Corrected);
        Assert.Empty(proposal.Violations);
        Assert.Empty(proposal.Warnings!);
        Assert.NotNull(proposal.Plan);
        Assert.Equal(["I1", "R1", "T1"], proposal.Plan.Tasks.Select(t => t.Id.Value));
        Assert.Single(harness.Claude.Starts);
        Assert.Same(proposal.Plan, Assert.Single(harness.Plans.All()));

        var (entry, created) = Assert.Single(harness.Payloads<PlanCreatedPayload>());
        Assert.Equal(new Actor(ActorKind.Orchestrator, "orquestador"), entry.Actor);
        Assert.Equal(proposal.Plan.ContentHash, created.PlanHash);
        Assert.Equal(proposal.Run.Value, created.RunId);
        Assert.Equal(3, created.TaskCount);
        Assert.Equal(proposal.RawPlanEvidence, created.RawPlanEvidence);
        var raw = harness.Evidence.Record(proposal.RawPlanEvidence!.Value);
        Assert.Equal(EvidenceKind.AgentStatement, raw.Kind);
        Assert.Equal(EvidenceOrigin.ReportedByAgent, raw.Origin);
        Assert.Equal(ValidPlan(), harness.Evidence.Text(raw.Id));
        Assert.Empty(harness.Payloads<PlanRevisedPayload>());
    }

    [Fact]
    public async Task The_guard_baseline_is_captured_before_the_base_worktree_is_created()
    {
        // A-02: the _base worktree is created through a checkout; its baseline must be captured first, so a program the
        // creation could run (A-02) that writes into the user's repository is seen as a difference, not as the normal.
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));
        var ensureBeforeFirstCapture = false;
        harness.Worktrees.OnCapture = n =>
        {
            if (n == 1)
            {
                ensureBeforeFirstCapture = harness.Worktrees.Calls.Any(c => c.StartsWith("ensure", StringComparison.Ordinal));
            }
        };

        await PlanAsync(harness);

        Assert.False(ensureBeforeFirstCapture, "the _base worktree was created before the guard captured its baseline.");
    }

    [Fact]
    public async Task The_orchestrator_reads_the_base_worktree_on_HEAD_with_read_only_tools_and_the_plan_schema()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        var request = Assert.Single(harness.Claude.Starts).Request;
        var (_, worktree) = Assert.Single(harness.Payloads<WorktreeCreatedPayload>());
        Assert.Equal("_base", worktree.Name);
        Assert.Null(worktree.Task);
        Assert.Equal(harness.Worktrees.InitialCommit, worktree.BaseCommit);
        Assert.Equal(worktree.Path, request.WorkingDirectory);
        Assert.Equal(proposal.Run.Value, worktree.RunId);

        Assert.False(request.Permissions.AllowFileWrites);
        Assert.False(request.Permissions.AllowShell);
        Assert.False(request.Permissions.AllowNetwork);
        Assert.Equal(["Read", "Grep", "Glob"], request.Permissions.AllowedTools);
        Assert.Null(request.ResumeSessionId);
        Assert.Equal(new AgentId("orquestador"), request.Agent.Id);
        var context = OrchestrationFixtures.Context();
        Assert.Equal(PlanSchema.Build(context), request.OutputJsonSchema);
        Assert.Contains("#intento-1", request.Tags!);
    }

    [Fact]
    public async Task The_prompt_tells_the_planner_the_review_policy_of_each_agent_that_has_one()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));
        var roster = OrchestrationFixtures.Roster()
            .Select(a => a.Id == new AgentId("ejecutor") ? a with { Review = new ReviewPolicy(true, true) } : a)
            .ToList();

        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest() with { Roster = roster }, null, TestContext.Current.CancellationToken);

        var lines = Assert.Single(harness.Claude.Starts).Request.Prompt.Split('\n');
        var executor = Assert.Single(lines, l => l.StartsWith("- ejecutor:", StringComparison.Ordinal));
        var other = Assert.Single(lines, l => l.StartsWith("- ejecutor2:", StringComparison.Ordinal));
        Assert.Contains("verificationSteps", executor, StringComparison.Ordinal);
        Assert.Contains("Review", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("verificationSteps", other, StringComparison.Ordinal);
        Assert.DoesNotContain("Review", other, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_says_which_steps_of_the_profile_run_tests_and_that_a_policy_of_tests_needs_one_of_them()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));
        var roster = OrchestrationFixtures.Roster()
            .Select(a => a.Id == new AgentId("ejecutor") ? a with { Review = new ReviewPolicy(false, true) } : a)
            .ToList();

        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest() with { Roster = roster }, null, TestContext.Current.CancellationToken);

        var lines = Assert.Single(harness.Claude.Starts).Request.Prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("- test: Pruebas (ejecuta pruebas)", lines);
        Assert.Contains("- build: Compilar", lines);
        Assert.Contains("paso de pruebas", Assert.Single(lines, l => l.StartsWith("- ejecutor:", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_asks_for_an_integration_after_the_writers_and_a_review_of_the_judge_after_it()
    {
        // B10: the plan of run 20261029-3f1cbc was A (writes) → B (judge) and could never be delivered.
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        await PlanAsync(harness);

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        var rule = Assert.Single(prompt.Split('\n'), l => l.StartsWith("- Si alguna tarea escribe", StringComparison.Ordinal));
        Assert.Contains("aunque solo haya una tarea que escribe", rule, StringComparison.Ordinal);
        Assert.Contains("una tarea Integration que depende (directa o indirectamente) de todas las tareas que escriben", rule, StringComparison.Ordinal);
        Assert.Contains("una tarea Review de un juez que depende de esa Integration, sin ninguna tarea que escriba entre las dos", rule, StringComparison.Ordinal);
        Assert.Contains("si hay más tareas Review, cada una va antes de alguna Integration o depende también de esa última", rule, StringComparison.Ordinal);
        Assert.Contains("no se podrá entregar", rule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_says_which_steps_require_others_and_that_the_system_runs_those_first()
    {
        // B6: a plan may name only «test»; the system builds first, and the orchestrator knows it.
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));
        var profile = OrchestrationFixtures.Profile() with
        {
            Steps = [.. OrchestrationFixtures.Profile().Steps.Select(s => s.Id == "test" ? s with { Requires = ["build"] } : s)],
        };

        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest() with { Verification = profile }, null, TestContext.Current.CancellationToken);

        var lines = Assert.Single(harness.Claude.Starts).Request.Prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("- test: Pruebas (ejecuta pruebas; requiere build, que el sistema ejecuta antes)", lines);
        Assert.Contains("- build: Compilar", lines);
        Assert.Contains(lines, l => l.StartsWith("Un paso que requiere otros no necesita que los nombres", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_plan_that_could_never_be_delivered_is_not_corrected_and_comes_with_its_warning()
    {
        using var harness = new EngineHarness();
        var undeliverable = Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("T1")])).ToJsonString();
        harness.Claude.Script("orquestador", null, 1, Answer(undeliverable));

        var proposal = await PlanAsync(harness);

        Assert.Single(harness.Claude.Starts);
        Assert.False(proposal.Corrected);
        Assert.Empty(proposal.Violations);
        var warning = Assert.Single(proposal.Warnings!);
        Assert.Equal((PlanWarningCode.NotDeliverable, new TaskId("T1")), (warning.Code, warning.Task));
    }

    [Fact]
    public async Task A_plan_with_violations_is_corrected_for_them_alone_and_its_warnings_are_never_read()
    {
        // The warnings are read of a plan that may run: an invalid answer of the orchestrator may be of any size.
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(PlanWithOrchestratorTask()));
        harness.Claude.Script("orquestador", null, 2, Answer(PlanWithOrchestratorTask()));

        var proposal = await PlanAsync(harness);

        var correction = harness.Claude.Starts[1].Request.Prompt;
        Assert.Contains("[OrchestratorHasTask]", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("[NotDeliverable]", correction, StringComparison.Ordinal);
        Assert.NotEmpty(proposal.Violations);
        Assert.Null(proposal.Warnings);
    }

    [Fact]
    public async Task A_plan_whose_writer_only_builds_is_corrected_when_the_policy_of_its_agent_asks_for_tests()
    {
        using var harness = new EngineHarness();
        var roster = OrchestrationFixtures.Roster()
            .Select(a => a.Id == new AgentId("ejecutor") ? a with { Review = new ReviewPolicy(false, true) } : a)
            .ToList();
        harness.Claude.Script("orquestador", null, 1, Answer(Plan(Task("T1", covers: ["RQ-01"], steps: ["build"])).ToJsonString()));
        harness.Claude.Script("orquestador", null, 2, Answer(ValidPlan()));

        var proposal = await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest() with { Roster = roster }, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Contains("[TestStepRequiredByPolicy]", harness.Claude.Starts[1].Request.Prompt, StringComparison.Ordinal);
        Assert.True(proposal.Corrected);
        Assert.Empty(proposal.Violations);
    }

    [Fact]
    public async Task The_prompt_is_evidence_and_no_payload_carries_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        await PlanAsync(harness);

        var request = Assert.Single(harness.Claude.Starts).Request;
        Assert.Contains(Marker, request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Añadir Calc.Multiply(a,b)", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("<original_request>", request.Prompt, StringComparison.Ordinal);
        var (_, started) = Assert.Single(harness.Payloads<AgentRunStartedPayload>());
        var prompt = harness.Evidence.Record(started.PromptEvidenceId);
        Assert.Equal(EvidenceKind.Document, prompt.Kind);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, prompt.Origin);
        Assert.Equal(request.Prompt, harness.Evidence.Text(prompt.Id));
        Assert.Equal(prompt.ContentSha256, started.PromptSha256);
        Assert.Equal(ContentHash.Sha256Hex(request.Prompt), started.PromptSha256);
        Assert.Null(started.Task);
        Assert.Equal(ProviderId.ClaudeCode, started.Provider);
        Assert.Equal(VendorId.Anthropic, started.Vendor);
        Assert.All(harness.Entries, e => Assert.DoesNotContain(Marker, e.PayloadJson, StringComparison.Ordinal));
        Assert.All(harness.Entries, e => Assert.DoesNotContain(Marker, e.Summary, StringComparison.Ordinal));

        var (_, finished) = Assert.Single(harness.Payloads<AgentRunFinishedPayload>());
        Assert.Equal(AttemptOutcome.Succeeded, finished.Status);
        Assert.Equal("session-orquestador-plan-1", finished.SessionId);
        Assert.NotNull(finished.ReportEvidenceId);
    }

    [Fact]
    public async Task An_invalid_plan_gets_exactly_one_correction_by_resuming_the_session()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(PlanWithOrchestratorTask()));
        harness.Claude.Script("orquestador", null, 2, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        Assert.Equal(2, harness.Claude.Starts.Count);
        var correction = harness.Claude.Starts[1].Request;
        Assert.Equal("session-orquestador-plan-1", correction.ResumeSessionId);
        Assert.Contains("<plan_problems>", correction.Prompt, StringComparison.Ordinal);
        Assert.Contains("[OrchestratorHasTask]", correction.Prompt, StringComparison.Ordinal);
        Assert.Equal(harness.Claude.Starts[0].Request.OutputJsonSchema, correction.OutputJsonSchema);
        Assert.Contains("#intento-2", correction.Tags!);

        Assert.True(proposal.Corrected);
        Assert.Empty(proposal.Violations);
        Assert.Null(proposal.Error);
        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        var first = PlanParser.Parse(PlanWithOrchestratorTask(), new AgentId("orquestador")).Plan!;
        var (_, created) = Assert.Single(harness.Payloads<PlanCreatedPayload>());
        var (revisedEntry, revised) = Assert.Single(harness.Payloads<PlanRevisedPayload>());
        Assert.Equal(first.ContentHash, created.PlanHash);
        Assert.Equal(proposal.Plan!.ContentHash, revised.PlanHash);
        Assert.Equal(first.ContentHash, revised.PreviousPlanHash);
        Assert.Contains("OrchestratorHasTask", revised.Reason, StringComparison.Ordinal);
        Assert.Equal(ActorKind.Orchestrator, revisedEntry.Actor.Kind);
        Assert.Equal(2, harness.Plans.All().Count);
    }

    [Fact]
    public async Task A_plan_still_invalid_after_the_correction_goes_to_the_user_with_its_violations()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(PlanWithOrchestratorTask()));
        harness.Claude.Script("orquestador", null, 2, Answer(Plan(Task("T2", agent: "orquestador", covers: ["RQ-01"])).ToJsonString()));
        harness.Claude.Script("orquestador", null, 3, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.True(proposal.Corrected);
        Assert.NotNull(proposal.Plan);
        Assert.Equal("T2", Assert.Single(proposal.Plan.Tasks).Id.Value);
        Assert.Contains(proposal.Violations, v => v.Code == PlanViolationCode.OrchestratorHasTask);
        Assert.NotNull(proposal.Error);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_plan_is_corrected_with_the_parse_problems()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer("""{"summary":"sin tareas"}"""));
        harness.Claude.Script("orquestador", null, 2, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        var correction = harness.Claude.Starts[1].Request;
        Assert.Contains("tasks", correction.Prompt, StringComparison.Ordinal);
        Assert.Contains("missing", correction.Prompt, StringComparison.Ordinal);
        Assert.True(proposal.Corrected);
        Assert.Empty(proposal.Violations);
        var (_, created) = Assert.Single(harness.Payloads<PlanCreatedPayload>());
        Assert.Equal(proposal.Plan!.ContentHash, created.PlanHash);
        Assert.Empty(harness.Payloads<PlanRevisedPayload>());
    }

    [Fact]
    public async Task A_correction_that_cannot_be_read_leaves_the_first_plan_and_its_violations()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(PlanWithOrchestratorTask()));
        harness.Claude.Script("orquestador", null, 2, Answer("""{"tasks":[]}"""));

        var proposal = await PlanAsync(harness);

        Assert.True(proposal.Corrected);
        Assert.Contains(proposal.Violations, v => v.Code == PlanViolationCode.OrchestratorHasTask);
        Assert.Contains("no se pudo leer", proposal.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_orchestrator_run_is_reported_and_not_corrected()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, AgentScript.Failing("sin sesión"));

        var proposal = await PlanAsync(harness);

        Assert.Single(harness.Claude.Starts);
        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Equal("sin sesión", proposal.Error);
        Assert.Null(proposal.Plan);
        Assert.Empty(harness.Payloads<PlanCreatedPayload>());
        Assert.Equal(AttemptOutcome.Failed, Assert.Single(harness.Payloads<AgentRunFinishedPayload>()).Payload.Status);
    }

    [Fact]
    public async Task An_orchestrator_that_cannot_be_launched_is_reported()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { ThrowOnStart = new InvalidOperationException("claude no está instalado") });

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Contains("claude no está instalado", proposal.Error!, StringComparison.Ordinal);
        var (_, finished) = Assert.Single(harness.Payloads<AgentRunFinishedPayload>());
        Assert.Equal(AttemptOutcome.Failed, finished.Status);
    }

    [Fact]
    public async Task A_change_to_the_user_repository_during_planning_discards_the_plan()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('f', 40) },
        });

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Null(proposal.Plan);
        Assert.Contains("HEAD moved", proposal.Error!, StringComparison.Ordinal);
        var (_, alert) = Assert.Single(harness.Payloads<AuditAlertPayload>());
        Assert.Equal("user-repository.changed", alert.Code);
        Assert.Contains("HEAD moved", alert.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads<PlanCreatedPayload>());
        Assert.Single(harness.Claude.Starts);
    }

    [Fact]
    public async Task What_Agent_Studio_writes_under_agentstudio_during_planning_is_the_known_exception()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with
            {
                PorcelainStatus = [" M .agentstudio/bitacora/2026-10-15.md", "?? .agentstudio/agentstudio.db-wal"],
            },
        });

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.NotNull(proposal.Plan);
        Assert.Empty(harness.Payloads<AuditAlertPayload>());
    }

    [Fact]
    public async Task Any_other_change_under_agentstudio_during_planning_discards_the_plan()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = ["?? .agentstudio/"] },
        });

        var proposal = await PlanAsync(harness);

        Assert.Null(proposal.Plan);
        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Single(harness.Payloads<AuditAlertPayload>(), a => a.Payload.Code == "user-repository.changed");
    }

    [Fact]
    public async Task An_ignored_folder_the_capture_cannot_verify_does_not_discard_the_plan_and_is_alerted_once()
    {
        // The orchestrator only reads (Read, Grep, Glob): it can never write to the user's repository at medium integrity.
        using var harness = new EngineHarness();
        harness.Worktrees.UserState = harness.Worktrees.UserState with
        {
            PorcelainStatus = ["!! bin/"],
            Contents = new Dictionary<string, AgentStudio.Application.Git.FileFingerprint>(StringComparer.Ordinal)
            {
                ["bin/"] = new(null, "folder:5000+ files", "the capture budget ran out inside this folder"),
            },
        };
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.NotNull(proposal.Plan);
        var (_, alert) = Assert.Single(harness.Payloads<AuditAlertPayload>());
        Assert.Equal("user-repository.capture-indeterminate", alert.Code);
        Assert.Equal(proposal.Run.Value, alert.RunId);
        Assert.Contains("'bin/'", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_untracked_file_outside_agentstudio_is_a_repository_change()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = ["?? src/escapado.cs"] },
        });

        var proposal = await PlanAsync(harness);

        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Contains("src/escapado.cs", Assert.Single(harness.Payloads<AuditAlertPayload>()).Payload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ids_are_dated_and_unique()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (_, _) => Answer(ValidPlan());
        var orchestrator = harness.CreateOrchestrator();
        var ct = TestContext.Current.CancellationToken;

        var first = await orchestrator.PlanAsync(harness.PlanningRequest(), null, ct);
        var second = await orchestrator.PlanAsync(harness.PlanningRequest(), null, ct);

        Assert.Matches(new Regex(@"\A20261015-[0-9a-f]{6}\z"), first.Run.Value);
        Assert.Matches(new Regex(@"\A20261015-[0-9a-f]{6}\z"), second.Run.Value);
        Assert.NotEqual(first.Run, second.Run);
    }

    [Fact]
    public async Task Progress_receives_the_events_of_the_orchestrator()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            Events = [AgentScript.Usage(OrchestrationFixtures.Start, 100, 20)],
        });
        var events = new List<AgentEvent>();
        var progress = new SynchronousProgress<AgentEvent>(events.Add);

        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), progress, TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e is SessionStarted);
        Assert.Contains(events, e => e is UsageReported);
        Assert.Contains(events, e => e is RunFinished);
        var usage = Assert.Single(harness.Payloads<AgentRunFinishedPayload>()).Payload.Usage;
        Assert.Equal(100m, usage!.InputTokens.Value);
    }

    [Fact]
    public async Task A_broken_log_stops_planning_before_any_agent_runs()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (_, _) => Answer(ValidPlan());
        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken);
        harness.LogStore.Tamper(harness.Project.ProjectId, 0, e => e with { Summary = "reescrito" });
        var startsBefore = harness.Claude.Starts.Count;

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken));

        Assert.Equal(startsBefore, harness.Claude.Starts.Count);
    }

    [Fact]
    public async Task The_orchestrator_must_be_an_orchestrator_of_the_roster()
    {
        using var harness = new EngineHarness();
        var orchestrator = harness.CreateOrchestrator();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PlanAsync(harness.PlanningRequest() with { Orchestrator = new AgentId("nadie") }, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PlanAsync(harness.PlanningRequest() with { Orchestrator = new AgentId("ejecutor") }, null, ct));
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_schema_that_does_not_fit_inline_fails_before_any_agent_runs()
    {
        using var harness = new EngineHarness();
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(1, 2000).Select(i => new Requirement(new RequirementId($"RQ-{i:D5}"), "statement", RequirementPriority.Desirable, [])),
            OrchestrationFixtures.Start);

        var proposal = await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest() with { Requirements = requirements }, null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Failed, proposal.Status);
        Assert.Contains("16384", proposal.Error!, StringComparison.Ordinal);
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task The_skills_of_the_orchestrator_are_provisioned_for_its_CLI_and_released()
    {
        using var harness = new EngineHarness(
            activations: [new SkillActivation(new SkillId("leer-codigo"), SkillScopeKind.Agent, "orquestador", true)],
            catalog: [FakeSkillCatalog.Vendored("leer-codigo")]);
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlan()));

        await PlanAsync(harness);

        var (cli, skills, _, directory) = Assert.Single(harness.Materializer.Materialized);
        Assert.Equal(ProviderId.ClaudeCode, cli);
        Assert.Equal(["leer-codigo"], skills);
        Assert.Equal(directory, Assert.Single(harness.Claude.Starts).Request.SkillsDirectory);
        Assert.Equal([directory], harness.Materializer.Cleaned);
        Assert.Equal([new SkillId("leer-codigo")], Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Payload.Skills);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
