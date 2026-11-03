using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

public sealed class DecisionPayloadTests
{
    private const string Run = "20261015-a1b2c3";

    private static readonly string HeaderHash = new('e', 64);

    /// <summary>A green test run the system captured on <see cref="CommitA"/>, as the store returns it.</summary>
    private static readonly EvidenceRecord VerifiedTestRun = new(
        Eid("ev-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test (T1)", "agent-studio:verification", T0, HashB, Green, subjectCommit: CommitA);

    /// <summary>One sample of every payload type. A new payload type without a sample here fails the coverage test below.</summary>
    private static readonly DecisionPayload[] Samples =
    [
        new RunStartedPayload(HashA, HashB, "abc123", "main", HashA, [new RosterEntry(Aid("ejecutor"), AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic)], null, HeaderHash)
        {
            RunId = Run,
            V = RunStartedPayload.HeaderVersion,
        },
        new RunFinishedPayload(RunPhase.Succeeded, "Todo bien", "def456", "v1") { RunId = Run },
        new PlanCreatedPayload(HashA, PlanSource.OrchestratorAgent, Aid("orquestador"), 3, Eid("ev-plan")) { RunId = Run },
        new PlanRevisedPayload(HashB, HashA, "El validador pidió una corrección") { RunId = Run },
        new TaskCreatedPayload(Tid("T1"), "Añadir Multiply", WorkTaskKind.AgentWork, JoinPolicy.AllCompleted, 1) { RunId = Run },
        new TaskAssignedPayload(Tid("T1"), Aid("ejecutor")) { RunId = Run },
        new TaskStatusChangedPayload(Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "done", null, [Eid("ev-1"), Eid("ev-2")], 2) { RunId = Run },
        new InstructionsIssuedPayload(Tid("T1"), 1, Eid("ev-prompt"), HashA, [Rid("RQ-01")], ["src/Calc/"], [new SkillId("tdd")]) { RunId = Run },
        new AgentRunStartedPayload(Tid("T1"), 1, Aid("ejecutor"), ProviderId.CodexCli, VendorId.OpenAI, "gpt-x", "C:/wt/T1", Eid("ev-prompt"), HashA, [new SkillId("tdd")]) { RunId = Run },
        new AgentRunFinishedPayload(Tid("T1"), 1, AttemptOutcome.Succeeded, 0, "sess-1", null, UsageOf(1200, 0.0123m), Eid("ev-report")) { RunId = Run },
        EvidenceRegisteredPayload.Anchoring(VerifiedTestRun, Run, Tid("T1"), 1),
        new WorktreeCreatedPayload(Tid("T1"), "T1", "C:/wt/T1", "refs/agentstudio/r/T1", "abc123") { RunId = Run },
        new WorktreeRemovedPayload("T1", "C:/wt/T1", false) { RunId = Run },
        new IntegrationPerformedPayload(Tid("T2"), "agentstudio/run-r", ["refs/agentstudio/r/T1"], "def456", [], false) { RunId = Run },
        new ConditionEvaluatedPayload(Tid("T4"), "TestsPassed(T3)", true, "yes", [new ConditionFact("T3.tests", "10 passed, 0 failed")]) { RunId = Run },
        new ReviewRequestedPayload(Tid("T7"), Aid("juez")) { RunId = Run },
        new VerdictIssuedPayload("v1", Tid("T7"), VerdictDecision.Approved, true, [VerdictViolationCode.JudgeNotIndependent], HashA, HashB, null, "ninguna") { RunId = Run },
        new ApprovalRequestedPayload("merge", "Entregar en main", RiskLevel.Critical, null, ["src/"]) { RunId = Run },
        new ApprovalAnsweredPayload("merge", true, "manuel", "ok", null) { RunId = Run },
        new AuditAlertPayload("Stalled", [Tid("T3")], "Sin actividad desde hace 10 minutos") { RunId = Run },
        MemoryRecordedPayload.Announcing("mem-1", MemoryKind.Decision, MemoryOrigin.System, HashA, "registro:20261015-a1b2c3#12"),
        MemoryReviewedPayload.Deciding("mem-1", MemoryReview.Accepted, "manuel", "de acuerdo"),

        // Phase 12 (docs/17 D-02): the trust of the journal, outside every run.
        JournalTrustChangedPayload.Deciding(JournalTrustScope.Day, new DateOnly(2026, 10, 29), trusted: true, "manuel", "Son mis prompts."),
        .. DebatePayloadTests.Samples,
        .. Phase8PayloadTests.Samples,
    ];

    private static UsageSnapshot UsageOf(long tokens, decimal cost) => UsageSnapshot.From(new TokenUsage(
        MetricValue<long>.Exact(tokens, "claude result.usage"),
        MetricValue<long>.Estimated(40, "estimate: 4 chars/token"),
        MetricValue<long>.Unavailable("not reported"),
        MetricValue<long>.Unavailable("not reported"),
        MetricValue<decimal>.Exact(cost, "claude result.total_cost_usd")));

    // ---- format ----

    [Fact]
    public void A_status_change_is_written_in_one_fixed_compact_camelCase_format()
    {
        var payload = new TaskStatusChangedPayload(Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "start", null, [], 1) { RunId = Run };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"task\":\"T1\",\"from\":\"Pending\",\"to\":\"Running\",\"reason\":\"start\",\"evidence\":[],\"attempt\":1,\"v\":1,\"runId\":\"20261015-a1b2c3\"}",
            json);
    }

    [Fact]
    public void The_start_of_an_agent_run_records_the_prompt_by_reference_only()
    {
        var payload = (AgentRunStartedPayload)Samples.Single(s => s is AgentRunStartedPayload);

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"task\":\"T1\",\"attempt\":1,\"agent\":\"ejecutor\",\"provider\":\"codex-cli\",\"vendor\":\"openai\",\"model\":\"gpt-x\",\"workingDirectory\":\"C:/wt/T1\",\"promptEvidenceId\":\"ev-prompt\",\"promptSha256\":\"" + HashA + "\",\"skills\":[\"tdd\"],\"v\":1,\"runId\":\"20261015-a1b2c3\"}",
            json);
    }

    // ---- version 1 of the agent run payloads, as Phase 3 writes them (characterization: these never change) ----

    /// <summary>The text Phase 3 writes for the end of an attempt. Its hash is in the logs of existing projects.</summary>
    private const string FinishedV1 =
        "{\"task\":\"T1\",\"attempt\":1,\"status\":\"Succeeded\",\"exitCode\":0,\"sessionId\":\"sess-1\","
        + "\"usage\":{\"inputTokens\":{\"value\":1200,\"provenance\":\"Exact\",\"source\":\"claude result.usage\"},"
        + "\"outputTokens\":{\"value\":40,\"provenance\":\"Estimated\",\"source\":\"estimate: 4 chars/token\"},"
        + "\"cachedInputTokens\":{\"provenance\":\"Unavailable\",\"source\":\"not reported\"},"
        + "\"contextWindow\":{\"provenance\":\"Unavailable\",\"source\":\"not reported\"},"
        + "\"costUsd\":{\"value\":0.0123,\"provenance\":\"Exact\",\"source\":\"claude result.total_cost_usd\"}},"
        + "\"reportEvidenceId\":\"ev-report\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}";

    [Fact]
    public void The_end_of_an_attempt_is_still_written_in_version_1_by_the_phase_3_factories()
    {
        var payload = (AgentRunFinishedPayload)Samples.Single(s => s is AgentRunFinishedPayload);

        Assert.Equal(FinishedV1, DecisionPayloadSerializer.Serialize(payload));
    }

    [Fact]
    public void The_usage_snapshot_of_version_1_has_only_the_five_figures_and_no_origin()
    {
        var json = DecisionPayloadSerializer.Serialize(new AgentRunFinishedPayload(null, 1, AttemptOutcome.Succeeded, 0, null, null, UsageOf(7, 0.5m), null) { RunId = Run });

        Assert.DoesNotContain("origin", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cacheWrite", json, StringComparison.Ordinal);
        Assert.DoesNotContain("perModel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_1_end_of_an_attempt_is_read_and_written_back_byte_for_byte_so_its_hash_does_not_change()
    {
        var actor = new Actor(ActorKind.System, "agent-studio");
        var stored = new DecisionLog().Append(T0, actor, DecisionKind.AgentRunFinished, "Termina ejecutor para T1, intento 1: Succeeded", FinishedV1);

        var read = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(stored.PayloadJson);
        var rewritten = DecisionPayloadSerializer.Serialize(read);

        Assert.Equal(1, read.V);
        Assert.Equal(FinishedV1, rewritten);
        Assert.Equal(stored.Hash, DecisionLog.ComputeHash(stored.Sequence, stored.RecordedAt, actor, stored.Kind, stored.Summary, rewritten, null, stored.PreviousHash));
        // Computed with an independent implementation of the canonical form (length-prefixed fields, SHA-256) before Phase 4.
        Assert.Equal("b5900ce512184dac43edbc481906aadfc93e3c20f1f9c012f0b8c00fa946b0f4", stored.Hash);
        Assert.True(DecisionLog.Load([stored]).Verify().IsValid);
    }

    [Fact]
    public void A_version_1_start_of_an_attempt_is_read_and_written_back_byte_for_byte()
    {
        var v1 = DecisionPayloadSerializer.Serialize(Samples.Single(s => s is AgentRunStartedPayload));

        var read = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(v1);

        Assert.Equal(1, read.V);
        Assert.Equal(v1, DecisionPayloadSerializer.Serialize(read));
        Assert.False(read.RecordsPurpose());
        Assert.Null(read.Purpose);
        Assert.Null(read.ContextPack);
    }

    /// <summary>
    /// A version 1 figure carries no origin: every one came from a parser, through the creators whose meaning did not
    /// change (docs/14 D-03), so its origin is read from its provenance.
    /// </summary>
    [Fact]
    public void A_version_1_figure_is_read_with_the_origin_of_the_creator_that_wrote_it()
    {
        var read = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(FinishedV1);

        Assert.False(read.RecordsUsageWithOrigins());
        Assert.Equal(MetricOrigin.ReportedByProvider, read.Usage!.InputTokens.ResolvedOrigin());
        Assert.Equal(MetricOrigin.EstimatedByProvider, read.Usage.OutputTokens.ResolvedOrigin());
        Assert.Equal(MetricOrigin.NotExposed, read.Usage.CachedInputTokens.ResolvedOrigin());
        Assert.Null(read.Usage.InputTokens.Origin);
        Assert.Equal(MetricValue<long>.Exact(1200, "claude result.usage"), read.Usage.InputTokens.ToInt64());
        Assert.Equal(MetricValue<long>.Unavailable("not reported"), read.Usage.CachedInputTokens.ToInt64());
        Assert.Equal(MetricValue<decimal>.Exact(0.0123m, "claude result.total_cost_usd"), read.Usage.CostUsd.ToDecimal());
        Assert.Null(read.Usage.CacheWriteInputTokens);
        Assert.Null(read.Usage.ReasoningOutputTokens);
        Assert.Null(read.Usage.PerModel);
        Assert.Null(read.LaunchedAt);
        Assert.Null(read.Compactions);
    }

    // ---- version 2 of the agent run payloads (docs/14 sections 5.2 and D-02 to D-04, D-23) ----

    /// <summary>The usage of a Claude attempt with everything version 2 records: cache writes, the model breakdown and turns.</summary>
    private static TokenUsage ClaudeUsageOfTwoSteps() => new(
        MetricValue<long>.Exact(30_822, "claude result.usage"),
        MetricValue<long>.Exact(512, "claude result.usage"),
        MetricValue<long>.Exact(30_391, "claude result.usage"),
        MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"),
        MetricValue<decimal>.Estimated(0.0421m, "claude total_cost_usd"))
    {
        CacheWriteInputTokens = MetricValue<long>.Exact(423, "claude result.usage"),
        ReasoningOutputTokens = MetricValue<long>.NotExposed("claude -p does not report reasoning tokens"),
        PerModel =
        [
            new ModelUsage(
                "claude-sonnet-5-5",
                MetricValue<long>.Exact(30_822, "claude result.modelUsage"),
                MetricValue<long>.Exact(512, "claude result.modelUsage"),
                MetricValue<long>.Exact(30_391, "claude result.modelUsage"),
                MetricValue<long>.Exact(423, "claude result.modelUsage"),
                MetricValue<decimal>.Estimated(0.0421m, "claude result.modelUsage"),
                MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"),
                "list"),
        ],
        Turns = 2,
    };

    private static string Figure(long value, string source, string provenance = "Exact", string origin = "ReportedByProvider") =>
        "{\"value\":" + value.ToString(CultureInfo.InvariantCulture) + ",\"provenance\":\"" + provenance + "\",\"source\":\"" + source + "\",\"origin\":\"" + origin + "\"}";

    [Fact]
    public void The_start_of_an_attempt_is_written_in_version_2_with_its_purpose_and_the_memory_it_was_given()
    {
        var payload = new AgentRunStartedPayload(
            Tid("T1"), 1, Aid("ejecutor"), ProviderId.ClaudeCode, VendorId.Anthropic, "claude-sonnet-5-5", "C:/wt/T1", Eid("ev-prompt"), HashA, [],
            AgentRunPurpose.Executor, new ContextPackReference(HashB, ["mem-1", "mem-2"], ["mem-3"], 1234))
        {
            RunId = Run,
            V = AgentRunStartedPayload.PurposeVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"task\":\"T1\",\"attempt\":1,\"agent\":\"ejecutor\",\"provider\":\"claude-code\",\"vendor\":\"anthropic\",\"model\":\"claude-sonnet-5-5\","
            + "\"workingDirectory\":\"C:/wt/T1\",\"promptEvidenceId\":\"ev-prompt\",\"promptSha256\":\"" + HashA + "\",\"skills\":[],\"purpose\":\"Executor\","
            + "\"contextPack\":{\"sha256\":\"" + HashB + "\",\"entries\":[\"mem-1\",\"mem-2\"],\"omitted\":[\"mem-3\"],\"characters\":1234},\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            json);
        var read = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.True(read.RecordsPurpose());
        Assert.Equal(AgentRunPurpose.Executor, read.Purpose);
        Assert.Equal(["mem-1", "mem-2"], read.ContextPack!.Entries);
        Assert.Equal(2, AgentRunStartedPayload.PurposeVersion);
    }

    [Fact]
    public void An_auxiliary_start_names_its_purpose_and_carries_no_memory()
    {
        var payload = new AgentRunStartedPayload(null, 1, Aid("recomendador"), ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5", "C:/runs/aux", Eid("ev-p"), HashA, [], AgentRunPurpose.SkillRecommendation)
        {
            RunId = "aux-20261018-a1b2c3",
            V = AgentRunStartedPayload.PurposeVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.EndsWith(",\"skills\":[],\"purpose\":\"SkillRecommendation\",\"v\":2,\"runId\":\"aux-20261018-a1b2c3\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("contextPack", json, StringComparison.Ordinal);
        Assert.Equal(["Planner", "Executor", "Judge", "SkillRecommendation", "DailySummary", "DebateParticipant", "DebateJudge", "ProjectAudit"], Enum.GetNames<AgentRunPurpose>());
    }

    [Fact]
    public void The_end_of_an_attempt_is_written_in_version_2_with_origins_breakdown_launch_time_and_context_peak()
    {
        var peak = new ContextObserved(T0.AddSeconds(9), "claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.message.usage"),
            MetricValue<long>.EstimatedBySystem(1_000_000, "ventana vista en una ejecución anterior"));
        var payload = new AgentRunFinishedPayload(
            Tid("T1"), 2, AttemptOutcome.Succeeded, 0, "sess-1", null, UsageSnapshot.WithBreakdown(ClaudeUsageOfTwoSteps()), Eid("ev-report"),
            LaunchedAt: T0.AddSeconds(3), ReportedModel: "claude-sonnet-5-5", ResumedSession: "sess-1", ContextPeak: ContextPeakSnapshot.From(peak), Compactions: 0, Turns: 2)
        {
            RunId = Run,
            V = AgentRunFinishedPayload.UsageVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"task\":\"T1\",\"attempt\":2,\"status\":\"Succeeded\",\"exitCode\":0,\"sessionId\":\"sess-1\",\"usage\":{"
            + "\"inputTokens\":" + Figure(30_822, "claude result.usage") + ",\"outputTokens\":" + Figure(512, "claude result.usage")
            + ",\"cachedInputTokens\":" + Figure(30_391, "claude result.usage") + ",\"contextWindow\":" + Figure(1_000_000, "claude result.modelUsage")
            + ",\"costUsd\":{\"value\":0.0421,\"provenance\":\"Estimated\",\"source\":\"claude total_cost_usd\",\"origin\":\"EstimatedByProvider\"}"
            + ",\"cacheWriteInputTokens\":" + Figure(423, "claude result.usage")
            + ",\"reasoningOutputTokens\":{\"provenance\":\"Unavailable\",\"source\":\"claude -p does not report reasoning tokens\",\"origin\":\"NotExposed\"}"
            + ",\"perModel\":[{\"model\":\"claude-sonnet-5-5\",\"inputTokens\":" + Figure(30_822, "claude result.modelUsage") + ",\"outputTokens\":" + Figure(512, "claude result.modelUsage")
            + ",\"cacheReadInputTokens\":" + Figure(30_391, "claude result.modelUsage") + ",\"cacheWriteInputTokens\":" + Figure(423, "claude result.modelUsage")
            + ",\"costUsd\":{\"value\":0.0421,\"provenance\":\"Estimated\",\"source\":\"claude result.modelUsage\",\"origin\":\"EstimatedByProvider\"}"
            + ",\"contextWindow\":" + Figure(1_000_000, "claude result.modelUsage") + ",\"costBasis\":\"list\"}]},"
            + "\"reportEvidenceId\":\"ev-report\",\"launchedAt\":\"2026-01-01T10:00:03+00:00\",\"reportedModel\":\"claude-sonnet-5-5\",\"resumedSession\":\"sess-1\","
            + "\"contextPeak\":{\"model\":\"claude-sonnet-5-5\",\"usedTokens\":" + Figure(30_822, "claude assistant.message.usage")
            + ",\"contextWindow\":" + Figure(1_000_000, "ventana vista en una ejecución anterior", "Estimated", "EstimatedBySystem") + "},"
            + "\"compactions\":0,\"turns\":2,\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            json);
        Assert.Equal(2, AgentRunFinishedPayload.UsageVersion);
    }

    [Fact]
    public void A_version_2_end_of_an_attempt_survives_a_round_trip_with_every_origin()
    {
        var payload = new AgentRunFinishedPayload(Tid("T1"), 1, AttemptOutcome.RateLimited, 1, "sess-9", "límite", UsageSnapshot.WithBreakdown(ClaudeUsageOfTwoSteps()), null,
            LaunchedAt: T0, ReportedModel: null, ResumedSession: null, ContextPeak: null, Compactions: null, Turns: 2)
        {
            RunId = Run,
            V = AgentRunFinishedPayload.UsageVersion,
        };
        var json = DecisionPayloadSerializer.Serialize(payload);

        var read = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(json);

        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.True(read.RecordsUsageWithOrigins());
        Assert.Equal(MetricOrigin.EstimatedByProvider, read.Usage!.CostUsd.Origin);
        Assert.Equal(MetricValue<long>.Exact(423, "claude result.usage"), read.Usage.CacheWriteInputTokens!.ToInt64());
        Assert.Equal(MetricValue<long>.NotExposed("claude -p does not report reasoning tokens"), read.Usage.ReasoningOutputTokens!.ToInt64());
        var model = Assert.Single(read.Usage.PerModel!);
        Assert.Equal(("claude-sonnet-5-5", "list"), (model.Model, model.CostBasis));
        Assert.Equal(MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), model.ContextWindow.ToInt64());
        Assert.Equal(T0, read.LaunchedAt);
        Assert.Null(read.Compactions);
        Assert.Equal(2, read.Turns);
        Assert.DoesNotContain("compactions", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_snapshot_of_version_1_stays_the_same_even_for_a_usage_with_a_breakdown()
    {
        var v1 = UsageSnapshot.From(ClaudeUsageOfTwoSteps());

        Assert.Null(v1.CacheWriteInputTokens);
        Assert.Null(v1.ReasoningOutputTokens);
        Assert.Null(v1.PerModel);
        Assert.Null(v1.InputTokens.Origin);
        Assert.Equal(MetricOrigin.ReportedByProvider, v1.InputTokens.ResolvedOrigin());
    }

    [Fact]
    public void A_figure_read_back_with_an_origin_that_contradicts_its_value_cannot_become_a_metric()
    {
        var forged = new MetricSnapshot(5, MetricProvenance.Unavailable, "x", MetricOrigin.Missing);

        Assert.Throws<ArgumentException>(() => forged.ToInt64());
        Assert.Throws<ArgumentException>(() => new MetricSnapshot(5, MetricProvenance.Exact, "x", MetricOrigin.EstimatedBySystem).ToInt64());
        Assert.Throws<ArgumentException>(() => new MetricSnapshot(2.5m, MetricProvenance.Exact, "x").ToInt64());
    }

    // ---- memory (docs/14 D-19): announced in the log, never part of a run ----

    [Fact]
    public void A_memory_entry_is_announced_without_its_text_and_outside_every_run()
    {
        var payload = MemoryRecordedPayload.Announcing("mem-2", MemoryKind.KnownError, MemoryOrigin.Agent, HashB, "evidencia:ev-7@" + HashA, supersedes: "mem-1");

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"entry\":\"mem-2\",\"kind\":\"KnownError\",\"origin\":\"Agent\",\"sha256\":\"" + HashB + "\",\"source\":\"evidencia:ev-7@" + HashA + "\",\"supersedes\":\"mem-1\",\"v\":1,\"runId\":\"\"}",
            json);
        Assert.Equal(string.Empty, payload.RunId);
        Assert.Equal(DecisionPayload.NoRun, payload.RunId);
        var read = DecisionPayloadSerializer.Deserialize<MemoryRecordedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal((MemoryKind.KnownError, MemoryOrigin.Agent, "mem-1"), (read.Kind, read.Origin, read.Supersedes));
    }

    [Fact]
    public void A_review_of_a_memory_entry_says_who_decided_and_is_outside_every_run()
    {
        var payload = MemoryReviewedPayload.Deciding("mem-2", MemoryReview.Retracted, "manuel", null);

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal("{\"entry\":\"mem-2\",\"decision\":\"Retracted\",\"decidedBy\":\"manuel\",\"v\":1,\"runId\":\"\"}", json);
        Assert.Equal(["Accepted", "Rejected", "Retracted"], Enum.GetNames<MemoryReview>());
        Assert.Equal(["System", "User", "Agent"], Enum.GetNames<MemoryOrigin>());
    }

    [Fact]
    public void Memory_payloads_are_the_ones_of_their_kinds()
    {
        Assert.Equal(typeof(MemoryRecordedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.MemoryRecorded));
        Assert.Equal(typeof(MemoryReviewedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.MemoryReviewed));
    }

    [Fact]
    public void Evidence_the_engine_registers_is_written_in_version_2_with_the_metadata_that_decides_approval()
    {
        var payload = EvidenceRegisteredPayload.Anchoring(VerifiedTestRun, Run, Tid("T1"), 1);

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"evidence\":\"ev-1\",\"kind\":\"TestRun\",\"origin\":\"CapturedBySystem\",\"sha256\":\"" + HashB + "\",\"task\":\"T1\",\"attempt\":1,"
            + "\"title\":\"dotnet test (T1)\",\"producedBy\":\"agent-studio:verification\",\"capturedAt\":\"2026-01-01T10:00:00+00:00\","
            + "\"testRun\":{\"passed\":10,\"failed\":0,\"skipped\":0,\"exitCode\":0},\"subjectCommit\":\"" + CommitA + "\",\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            json);
    }

    [Fact]
    public void The_start_of_a_run_is_written_in_version_2_with_the_hash_of_its_whole_header()
    {
        var payload = (RunStartedPayload)Samples.Single(s => s is RunStartedPayload);

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"planHash\":\"" + HashA + "\",\"requirementSetHash\":\"" + HashB + "\",\"baseCommit\":\"abc123\",\"baseRef\":\"main\",\"verificationProfileHash\":\"" + HashA + "\","
            + "\"roster\":[{\"agent\":\"ejecutor\",\"role\":\"Executor\",\"provider\":\"claude-code\",\"vendor\":\"anthropic\"}],\"headerHash\":\"" + HeaderHash + "\",\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            json);
    }

    /// <summary>
    /// Version 3 (G-02) adds what the user approved about writers at medium integrity: the warning shown next to «Aprobar
    /// plan» and each writer it names, in a form that does not depend on the wording (task, reason by name and, when they
    /// are the reason, agent, CLI, tools or step).
    /// </summary>
    [Fact]
    public void The_start_of_a_run_is_written_in_version_3_with_the_writers_at_medium_integrity_it_was_approved_with()
    {
        var payload = new RunStartedPayload(
            HashA,
            HashB,
            "abc123",
            "main",
            HashA,
            [new RosterEntry(Aid("ejecutor"), AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic)],
            null,
            HeaderHash,
            "Atención: T1 tiene Bash.",
            [
                new MediumIntegrityWriterEntry(Tid("T1"), "Shell", Aid("ejecutorShell")),
                new MediumIntegrityWriterEntry(Tid("T2"), "UnmeasuredCli", Aid("ejecutorGemini"), ProviderId.GeminiCli),
                new MediumIntegrityWriterEntry(Tid("T3"), "ToolsBeyondMeasuredProfile", Aid("ejecutorMas"), Tools: ["Monitor"]),
                new MediumIntegrityWriterEntry(Tid("L1"), "StepWithoutIsolation", Step: "lint"),
            ])
        {
            RunId = Run,
            V = RunStartedPayload.WritersVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            "{\"planHash\":\"" + HashA + "\",\"requirementSetHash\":\"" + HashB + "\",\"baseCommit\":\"abc123\",\"baseRef\":\"main\",\"verificationProfileHash\":\"" + HashA + "\","
            + "\"roster\":[{\"agent\":\"ejecutor\",\"role\":\"Executor\",\"provider\":\"claude-code\",\"vendor\":\"anthropic\"}],\"headerHash\":\"" + HeaderHash + "\","
            + "\"approvedWarning\":\"Atención: T1 tiene Bash.\","
            + "\"approvedWriters\":[{\"task\":\"T1\",\"reason\":\"Shell\",\"agent\":\"ejecutorShell\"},{\"task\":\"T2\",\"reason\":\"UnmeasuredCli\",\"agent\":\"ejecutorGemini\",\"provider\":\"gemini-cli\"},"
            + "{\"task\":\"T3\",\"reason\":\"ToolsBeyondMeasuredProfile\",\"agent\":\"ejecutorMas\",\"tools\":[\"Monitor\"]},{\"task\":\"L1\",\"reason\":\"StepWithoutIsolation\",\"step\":\"lint\"}],"
            + "\"v\":3,\"runId\":\"20261015-a1b2c3\"}",
            json);
        Assert.Equal(3, RunStartedPayload.WritersVersion);
        Assert.True(payload.AnchorsApprovedWriters());

        var read = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal([Tid("T1"), Tid("T2"), Tid("T3"), Tid("L1")], read.ApprovedWriters!.Select(w => w.Task));
        Assert.Equal(["Monitor"], read.ApprovedWriters![2].Tools!);
    }

    /// <summary>A version 3 start of a run without such writers still says so: none were approved, which is not "unknown".</summary>
    [Fact]
    public void A_version_3_start_without_writers_at_medium_integrity_writes_an_empty_list_and_no_warning()
    {
        var payload = new RunStartedPayload(HashA, HashB, "abc123", null, HashA, [], null, HeaderHash, null, []) { RunId = Run, V = RunStartedPayload.WritersVersion };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.EndsWith(",\"headerHash\":\"" + HeaderHash + "\",\"approvedWriters\":[],\"v\":3,\"runId\":\"20261015-a1b2c3\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("approvedWarning", json, StringComparison.Ordinal);
        var read = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(json);
        Assert.True(read.AnchorsApprovedWriters());
        Assert.Empty(read.ApprovedWriters!);
    }

    [Fact]
    public void A_version_2_start_anchors_no_writers_and_is_written_back_exactly_as_it_was()
    {
        var v2 = "{\"planHash\":\"" + HashA + "\",\"requirementSetHash\":\"" + HashB + "\",\"baseCommit\":\"abc123\",\"verificationProfileHash\":\"" + HashA + "\",\"roster\":[],\"headerHash\":\"" + HeaderHash + "\",\"v\":2,\"runId\":\"20261015-a1b2c3\"}";

        var read = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(v2);

        Assert.False(read.AnchorsApprovedWriters());
        Assert.Null(read.ApprovedWarning);
        Assert.Null(read.ApprovedWriters);
        Assert.Equal(HeaderHash, read.HeaderHash);
        Assert.Equal(v2, DecisionPayloadSerializer.Serialize(read));
    }

    [Fact]
    public void A_version_1_start_is_still_read_and_carries_no_header_hash()
    {
        var v1 = "{\"planHash\":\"" + HashA + "\",\"requirementSetHash\":\"" + HashB + "\",\"baseCommit\":\"abc123\",\"verificationProfileHash\":\"" + HashA + "\",\"roster\":[],\"v\":1,\"runId\":\"20261015-a1b2c3\"}";

        var read = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(v1);

        Assert.Equal(1, read.V);
        Assert.Null(read.HeaderHash);
        Assert.Equal(v1, DecisionPayloadSerializer.Serialize(read));
    }

    [Fact]
    public void A_version_1_registration_is_still_read_and_written_exactly_as_it_was_without_metadata()
    {
        const string v1 = "{\"evidence\":\"ev-1\",\"kind\":\"TestRun\",\"origin\":\"CapturedBySystem\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"task\":\"T1\",\"attempt\":1,\"v\":1,\"runId\":\"20261015-a1b2c3\"}";

        var read = DecisionPayloadSerializer.Deserialize<EvidenceRegisteredPayload>(v1);

        Assert.Equal(1, read.V);
        Assert.Equal((Eid("ev-1"), EvidenceKind.TestRun, HashB), (read.Evidence, read.Kind, read.Sha256));
        Assert.Null(read.Title);
        Assert.Null(read.ProducedBy);
        Assert.Null(read.CapturedAt);
        Assert.Null(read.TestRun);
        Assert.Null(read.SubjectCommit);
        Assert.Equal(v1, DecisionPayloadSerializer.Serialize(read));
    }

    [Fact]
    public void A_test_run_snapshot_keeps_the_counters_and_gives_back_the_same_summary()
    {
        var snapshot = TestRunSnapshot.From(Red);

        Assert.Equal(new TestRunSnapshot(8, 2, 0, 1), snapshot);
        Assert.Equal(Red, snapshot!.ToSummary());
        Assert.Null(TestRunSnapshot.From(null));
    }

    [Fact]
    public void Null_members_are_left_out_and_text_is_written_as_is()
    {
        var payload = new AuditAlertPayload("Stalled", [], "Sin actividad: <T3> & 'T4' ñ") { RunId = Run };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Contains("Sin actividad: <T3> & 'T4' ñ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_does_not_depend_on_the_culture_of_the_machine()
    {
        var payload = (AgentRunFinishedPayload)Samples.Single(s => s is AgentRunFinishedPayload);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            var spanish = DecisionPayloadSerializer.Serialize(payload);
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var turkish = DecisionPayloadSerializer.Serialize(payload);

            Assert.Equal(spanish, turkish);
            Assert.Contains("0.0123", spanish, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Serializing_the_same_payload_twice_gives_the_same_text()
    {
        foreach (var sample in Samples)
        {
            Assert.Equal(DecisionPayloadSerializer.Serialize(sample), DecisionPayloadSerializer.Serialize(sample));
        }
    }

    [Fact]
    public void The_version_defaults_to_one_and_is_part_of_the_text()
    {
        var payload = new TaskAssignedPayload(Tid("T1"), Aid("a")) { RunId = Run };
        var next = payload with { V = 2 };

        Assert.Equal(1, payload.V);
        Assert.Contains("\"v\":1", DecisionPayloadSerializer.Serialize(payload), StringComparison.Ordinal);
        Assert.Contains("\"v\":2", DecisionPayloadSerializer.Serialize(next), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_payload_is_serialized_with_its_own_fields_not_only_those_of_the_base()
    {
        // A serializer called with the declared base type would write {"v":1,"runId":...} and drop everything else.
        DecisionPayload payload = new TaskAssignedPayload(Tid("T1"), Aid("ejecutor")) { RunId = Run };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Contains("\"agent\":\"ejecutor\"", json, StringComparison.Ordinal);
    }

    // ---- reading back ----

    [Fact]
    public void Every_payload_type_has_a_sample_here()
    {
        var all = typeof(DecisionPayload).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DecisionPayload)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(all, Samples.Select(s => s.GetType().Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_payload_survives_a_round_trip()
    {
        foreach (var sample in Samples)
        {
            var json = DecisionPayloadSerializer.Serialize(sample);

            var read = DecisionPayloadSerializer.Deserialize(sample.GetType(), json);

            Assert.IsType(sample.GetType(), read);
            Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
            Assert.Equal(sample.RunId, read.RunId);
            Assert.Equal(sample.V, read.V);
        }
    }

    [Fact]
    public void A_payload_read_back_keeps_its_values()
    {
        var finished = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(DecisionPayloadSerializer.Serialize(Samples.Single(s => s is AgentRunFinishedPayload)));

        Assert.Equal(Tid("T1"), finished.Task);
        Assert.Equal(AttemptOutcome.Succeeded, finished.Status);
        Assert.Equal(1200m, finished.Usage!.InputTokens.Value);
        Assert.Equal(MetricProvenance.Exact, finished.Usage.InputTokens.Provenance);
        Assert.Equal(MetricProvenance.Estimated, finished.Usage.OutputTokens.Provenance);
        Assert.Null(finished.Usage.CachedInputTokens.Value);
        Assert.Equal(MetricProvenance.Unavailable, finished.Usage.CachedInputTokens.Provenance);
        Assert.Equal(0.0123m, finished.Usage.CostUsd.Value);
        Assert.Equal(Eid("ev-report"), finished.ReportEvidenceId);
    }

    [Fact]
    public void A_payload_without_a_run_id_cannot_be_read()
    {
        Assert.ThrowsAny<JsonException>(() => DecisionPayloadSerializer.Deserialize<TaskAssignedPayload>("{\"task\":\"T1\",\"agent\":\"a\",\"v\":1}"));
    }

    [Theory]
    [InlineData("{\"task\":\"T1\",\"agent\":\"a\",\"runId\":\"r\"}")]
    [InlineData("{\"task\":\"T1\",\"agent\":\"a\",\"runId\":\"r\",\"somethingNew\":42}")]
    public void A_payload_without_a_version_is_version_one_and_unknown_fields_of_later_versions_are_skipped(string json)
    {
        var read = DecisionPayloadSerializer.Deserialize<TaskAssignedPayload>(json);

        Assert.Equal(1, read.V);
        Assert.Equal(Tid("T1"), read.Task);
    }

    [Fact]
    public void Ids_and_enums_are_read_strictly()
    {
        Assert.ThrowsAny<JsonException>(() => DecisionPayloadSerializer.Deserialize<TaskAssignedPayload>("{\"task\":\"\",\"agent\":\"a\",\"runId\":\"r\"}"));
        Assert.ThrowsAny<JsonException>(() => DecisionPayloadSerializer.Deserialize<TaskStatusChangedPayload>("{\"task\":\"T1\",\"from\":1,\"to\":\"Running\",\"reason\":\"x\",\"evidence\":[],\"attempt\":1,\"runId\":\"r\"}"));
        Assert.ThrowsAny<JsonException>(() => DecisionPayloadSerializer.Deserialize<TaskStatusChangedPayload>("{\"task\":\"T1\",\"from\":\"Nope\",\"to\":\"Running\",\"reason\":\"x\",\"evidence\":[],\"attempt\":1,\"runId\":\"r\"}"));
    }

    [Fact]
    public void A_default_id_is_a_bug_and_is_not_written()
    {
        var payload = new TaskAssignedPayload(default, Aid("a")) { RunId = Run };

        Assert.ThrowsAny<JsonException>(() => DecisionPayloadSerializer.Serialize(payload));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DecisionPayloadSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => DecisionPayloadSerializer.Deserialize<TaskAssignedPayload>(null!));
    }

    // ---- kinds and payload types ----

    private static readonly DecisionKind[] UntypedKinds =
    [
        DecisionKind.ReviewSkipped, DecisionKind.PermissionDenied, DecisionKind.Correction,
    ];

    public static TheoryData<DecisionKind> AllKinds() => [.. Enum.GetValues<DecisionKind>()];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Each_kind_has_a_payload_type_or_is_known_to_be_free_form(DecisionKind kind)
    {
        var type = DecisionPayloadSerializer.PayloadTypeFor(kind);

        if (UntypedKinds.Contains(kind))
        {
            Assert.Null(type);
            Assert.Null(DecisionPayloadSerializer.Deserialize(kind, "{}"));
            return;
        }

        Assert.NotNull(type);
        Assert.True(type.IsSubclassOf(typeof(DecisionPayload)));
        var sample = Samples.First(s => s.GetType() == type);
        var read = DecisionPayloadSerializer.Deserialize(kind, DecisionPayloadSerializer.Serialize(sample));
        Assert.IsType(type, read);
    }

    [Fact]
    public void Granting_and_denying_share_the_answer_payload()
    {
        Assert.Equal(typeof(ApprovalAnsweredPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ApprovalGranted));
        Assert.Equal(typeof(ApprovalAnsweredPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ApprovalDenied));
    }

    // ---- prompts never go in a payload ----

    [Fact]
    public void No_payload_has_a_field_that_could_hold_a_prompt_or_agent_output()
    {
        var forbidden = new[] { "Prompt", "PromptText", "Text", "Content", "Body", "Instructions", "StandardInput", "Stdin", "Output", "Transcript" };
        var types = typeof(DecisionPayload).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DecisionPayload)))
            .Concat([
                typeof(RosterEntry), typeof(UsageSnapshot), typeof(MetricSnapshot), typeof(ConditionFact), typeof(TestRunSnapshot),
                typeof(ModelUsageSnapshot), typeof(ContextPeakSnapshot), typeof(ContextPackReference), typeof(MediumIntegrityWriterEntry),
                typeof(DebateParticipantEntry), typeof(DebateJudgeEntry), typeof(AttemptEndSnapshot),
            ]);

        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(property.Name, forbidden);
                if (property.Name.Contains("Prompt", StringComparison.Ordinal))
                {
                    Assert.True(
                        property.Name.EndsWith("EvidenceId", StringComparison.Ordinal) || property.Name.EndsWith("Sha256", StringComparison.Ordinal),
                        $"{type.Name}.{property.Name} could carry a prompt.");
                }
            }
        }
    }

    // ---- helpers ----

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, AttemptOutcome.Succeeded)]
    [InlineData(AgentRunStatus.Failed, AttemptOutcome.Failed)]
    [InlineData(AgentRunStatus.Cancelled, AttemptOutcome.Cancelled)]
    [InlineData(AgentRunStatus.LimitReached, AttemptOutcome.LimitReached)]
    [InlineData(AgentRunStatus.RateLimited, AttemptOutcome.RateLimited)]
    public void An_agent_run_status_maps_to_the_outcome_of_the_attempt(AgentRunStatus status, AttemptOutcome expected)
    {
        Assert.Equal(expected, AttemptOutcomes.From(status));
    }

    [Fact]
    public void Every_agent_run_status_maps_to_a_different_outcome()
    {
        var outcomes = Enum.GetValues<AgentRunStatus>().Select(AttemptOutcomes.From).ToList();

        Assert.Equal(outcomes.Count, outcomes.Distinct().Count());
        Assert.DoesNotContain(AttemptOutcome.Interrupted, outcomes);
    }

    [Fact]
    public void A_usage_snapshot_keeps_the_provenance_and_never_turns_unavailable_into_zero()
    {
        var usage = new TokenUsage(
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<long>.Exact(5, "x"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<decimal>.Unavailable("n/a"));

        var snapshot = UsageSnapshot.From(usage);

        Assert.Null(snapshot.InputTokens.Value);
        Assert.Equal(MetricProvenance.Unavailable, snapshot.InputTokens.Provenance);
        Assert.Equal(5m, snapshot.OutputTokens.Value);
        Assert.Null(snapshot.CostUsd.Value);
        Assert.Throws<ArgumentNullException>(() => UsageSnapshot.From(null!));
    }

    [Fact]
    public void A_decision_log_accepts_the_serialized_payload_and_its_hash_covers_the_text()
    {
        var first = DecisionPayloadSerializer.Serialize(Samples[6]);
        var second = DecisionPayloadSerializer.Serialize(((TaskStatusChangedPayload)Samples[6]) with { Reason = "otra" });
        var actor = new Actor(ActorKind.System, "agent-studio");

        var a = new DecisionLog().Append(T0, actor, DecisionKind.TaskStatusChanged, "s", first);
        var b = new DecisionLog().Append(T0, actor, DecisionKind.TaskStatusChanged, "s", second);

        Assert.NotEqual(a.Hash, b.Hash);
    }
}
