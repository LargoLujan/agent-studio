using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/14 section 5: the contracts of Phase 4 that the packages WP1 to WP5 build against in parallel. These tests fail
/// if one of them drifts from what docs/14 and docs/04 say.
/// </summary>
public sealed class Phase4ContractTests
{
    private static readonly AgentDefinition Executor = new(
        Aid("ejecutor"),
        "Ejecutor",
        AgentRole.Executor,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic),
        "instructions",
        [],
        [],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    private static IEnumerable<string> MethodNames(Type type) => type.GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);

    private static IEnumerable<Type> Parameters(Type type, string method) => type.GetMethod(method)!.GetParameters().Select(p => p.ParameterType);

    // ---- providers (section 5.2) ----

    [Fact]
    public void A_usage_built_as_phase_2_did_reports_no_cache_writes_reasoning_nor_breakdown_and_never_a_default_figure()
    {
        var usage = new TokenUsage(
            MetricValue<long>.Exact(10, "x"),
            MetricValue<long>.Exact(2, "x"),
            MetricValue<long>.Exact(0, "x"),
            MetricValue<long>.Unavailable("x"),
            MetricValue<decimal>.Unavailable("x"));

        Assert.Equal(MetricOrigin.NotExposed, usage.CacheWriteInputTokens.Origin);
        Assert.Equal(MetricOrigin.NotExposed, usage.ReasoningOutputTokens.Origin);
        Assert.False(string.IsNullOrWhiteSpace(usage.CacheWriteInputTokens.Source));
        Assert.False(string.IsNullOrWhiteSpace(usage.ReasoningOutputTokens.Source));
        Assert.Empty(usage.PerModel);
        Assert.Null(usage.Turns);
    }

    [Fact]
    public void A_provider_reports_context_occupancy_only_when_its_adapter_says_so()
    {
        var capabilities = new ProviderCapabilities(true, true, true, false, true, true, 1);

        Assert.False(capabilities.ReportsContextOccupancy);
        Assert.True((capabilities with { ReportsContextOccupancy = true }).ReportsContextOccupancy);
    }

    [Fact]
    public void A_run_request_has_no_mcp_server_unless_one_is_asked_for()
    {
        var request = new AgentRunRequest(Executor, "p", "C:\\wd", new PermissionProfile(false, false, false, [], [], []));
        var server = new McpServerSpec("agentstudio", "C:\\app\\AgentStudio.SkillsMcp.exe", ["--snapshot", "C:\\runs\\aux\\snapshot.json"], ["mcp__agentstudio__search_skills"]);

        Assert.Null(request.McpServers);
        Assert.Equal([server], (request with { McpServers = [server] }).McpServers!);
    }

    [Fact]
    public void Each_step_and_each_compaction_reach_the_caller_as_events()
    {
        var step = new ContextObserved(T0, "claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.message.usage"), MetricValue<long>.Missing("window not known yet"));
        var compaction = new CompactionObserved(T0, "auto", MetricValue<long>.Exact(180_000, "claude compact_boundary"));

        Assert.IsAssignableFrom<AgentEvent>(step);
        Assert.IsAssignableFrom<AgentEvent>(compaction);
        Assert.Equal(("claude-sonnet-5-5", 30_822L), (step.Model, step.PromptTokens.Value!.Value));
        Assert.Equal("auto", compaction.Trigger);
    }

    // ---- memory (section 5.4) ----

    [Fact]
    public void The_hash_announced_for_a_memory_entry_covers_its_category_topic_and_content_canonically()
    {
        var hash = MemoryContent.Sha256(MemoryKind.Decision, "Despliegue", "Se usa blue green.");

        Assert.Equal(ContentHash.Sha256Hex(ContentHash.Canonical("Decision", "Despliegue", "Se usa blue green.")), hash);
        Assert.NotEqual(hash, MemoryContent.Sha256(MemoryKind.Convention, "Despliegue", "Se usa blue green."));
        Assert.NotEqual(MemoryContent.Sha256(MemoryKind.Decision, "ab", "c"), MemoryContent.Sha256(MemoryKind.Decision, "a", "bc"));
        Assert.Equal(4000, MemoryContent.MaxCharacters);
    }

    [Fact]
    public void The_project_memory_records_reviews_and_recalls_and_the_store_only_appends_and_searches()
    {
        Assert.Equal(["RecallAsync", "RecordAsync", "ReviewAsync"], MethodNames(typeof(IProjectMemory)));
        Assert.Equal(["AppendAsync", "SearchAsync"], MethodNames(typeof(IMemoryStore)));
        Assert.Equal(["ExportAsync"], MethodNames(typeof(IMemoryExporter)));
        Assert.Equal(["IsBusy"], MethodNames(typeof(IRunActivity)));
        Assert.Equal(["Trusted", "Proposed", "Quarantined", "Rejected", "Retracted"], Enum.GetNames<MemoryTrust>());
        Assert.Equal(typeof(Task<MemoryEntry>), typeof(IProjectMemory).GetMethod(nameof(IProjectMemory.RecordAsync))!.ReturnType);
    }

    [Fact]
    public void A_memory_query_returns_only_trusted_entries_unless_asked_otherwise()
    {
        var query = new MemoryQuery();

        Assert.True(query.TrustedOnly);
        Assert.Equal(20, query.MaxEntries);
        Assert.Null(query.MaxCharacters);
    }

    [Fact]
    public void An_export_refused_while_a_run_goes_on_says_why_and_writes_nothing()
    {
        var refused = MemoryExportResult.Refused("Hay una ejecución en curso.");

        Assert.False(refused.Exported);
        Assert.Empty(refused.Files);
        Assert.Equal(0, refused.Entries);
        Assert.Equal("Hay una ejecución en curso.", refused.Reason);
    }

    [Fact]
    public void The_packer_packs_by_tiers_and_its_pack_is_recorded_by_reference()
    {
        Assert.Equal(["PackAsync"], MethodNames(typeof(IContextPacker)));
        Assert.Equal(["MandatoryRequirement", "Decision", "PendingTask", "DesirableRequirement", "KnownIssue", "Other"], Enum.GetNames<ContextTier>());
        var kept = new ContextItem("RQ-01", ContextTier.MandatoryRequirement, "requirement", "User", "Añadir Multiply", HashA);
        var left = new ContextItem("mem-9", ContextTier.Other, "Change", "System", "Un cambio antiguo", HashB);
        var pack = new ContextPack([kept], [left], 15, MetricValue<long>.EstimatedBySystem(4, "estimate: 4 chars/token"), new string('c', 64));

        var reference = pack.ToReference();
        Assert.Equal((new string('c', 64), 15), (reference.Sha256, reference.Characters));
        Assert.Equal(["RQ-01"], reference.Entries);
        Assert.Equal(["mem-9"], reference.Omitted);
        Assert.Empty(new ContextPackRequest(Executor, "Implementar Multiply", [kept], 4000).Desirable);
    }

    [Fact]
    public void Requirements_that_do_not_fit_stop_the_launch_with_how_much_was_needed()
    {
        var failure = new ContextBudgetTooSmallException(5000, 4000);

        Assert.IsAssignableFrom<InvalidOperationException>(failure);
        Assert.Equal((5000, 4000), (failure.Required, failure.Budget));
        Assert.Contains("5000", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_daily_summary_is_asked_for_by_day()
    {
        Assert.Equal(["SummarizeAsync"], MethodNames(typeof(IDailySummaryService)));
        Assert.Equal([typeof(ProjectContext), typeof(DateOnly), typeof(CancellationToken)], Parameters(typeof(IDailySummaryService), nameof(IDailySummaryService.SummarizeAsync)));
    }

    [Fact]
    public void The_usage_ledger_is_gone_because_the_log_is_the_ledger()
    {
        var assembly = typeof(IProjectMemory).Assembly;

        Assert.Null(assembly.GetType("AgentStudio.Application.Knowledge.IUsageLedger"));
        Assert.Null(assembly.GetType("AgentStudio.Application.Knowledge.UsageRecord"));
    }

    // ---- usage (section 5.3) ----

    [Fact]
    public void The_usage_report_is_queried_over_project_roots_and_exported_as_csv_or_json()
    {
        Assert.Equal(["QueryAsync"], MethodNames(typeof(IUsageReport)));
        Assert.Equal([typeof(IReadOnlyList<string>), typeof(UsageQuery), typeof(CancellationToken)], Parameters(typeof(IUsageReport), nameof(IUsageReport.QueryAsync)));
        Assert.Equal(["WriteCsvAsync", "WriteJsonAsync"], MethodNames(typeof(IUsageExporter)));
        Assert.Equal(
            ["Provider", "Vendor", "Model", "Session", "Run", "Task", "Agent", "Purpose", "Project", "Day", "Week", "Month"],
            Enum.GetNames<UsageDimension>());
    }

    [Fact]
    public void A_reconciliation_counts_the_difference_between_the_journal_and_the_log_without_adding_it()
    {
        Assert.Equal(3, new UsageReconciliation(10, 7, []).Difference);
        Assert.Null(new UsageReconciliation(null, 7, ["Sin bitácora del periodo."]).Difference);
    }

    [Fact]
    public void Another_projects_log_is_read_only_and_counted_only_when_it_verifies_against_a_recognised_anchor()
    {
        Assert.Equal(["ReadAsync"], MethodNames(typeof(IProjectLogReader)));
        Assert.Equal(["Anchored", "NoHistory", "Unanchored", "AnotherProject", "Unreadable"], Enum.GetNames<AnchorStatus>());

        ProjectLogSnapshot Snapshot(DecisionLogVerification verification, AnchorStatus anchor) => new("p1", "C:\\repo", [], verification, anchor);
        Assert.True(Snapshot(DecisionLogVerification.Valid, AnchorStatus.Anchored).IsTrusted);
        Assert.True(Snapshot(DecisionLogVerification.Valid, AnchorStatus.NoHistory).IsTrusted);
        Assert.False(Snapshot(DecisionLogVerification.Valid, AnchorStatus.Unanchored).IsTrusted);
        Assert.False(Snapshot(DecisionLogVerification.Valid, AnchorStatus.AnotherProject).IsTrusted);
        Assert.False(Snapshot(DecisionLogVerification.Valid, AnchorStatus.Unreadable).IsTrusted);
        Assert.False(Snapshot(new DecisionLogVerification(false, 3, "rewritten"), AnchorStatus.Anchored).IsTrusted);
    }

    [Fact]
    public void The_checked_read_of_the_project_storage_is_a_new_port_so_no_writer_changes()
    {
        Assert.Equal(["OpenForRead"], MethodNames(typeof(IProjectStorageReader)));
        Assert.Equal(["OpenForAppend", "Replace"], MethodNames(typeof(IProjectStorageFiles)));
    }

    // ---- quota (section 5.2, D-14 and D-15) ----

    [Fact]
    public void Quota_observations_are_appended_and_the_latest_of_each_window_is_read_back()
    {
        Assert.Equal(["AppendAsync", "LatestAsync"], MethodNames(typeof(IQuotaObservations)));
        Assert.Equal(["ReadAsync", "get_Provider"], MethodNames(typeof(IProviderQuotaReader)));
        Assert.Equal(["Read", "NotOptedIn", "NotExposed", "Missing"], Enum.GetNames<QuotaReadStatus>());

        var read = new QuotaReadResult(QuotaReadStatus.NotOptedIn, []);
        Assert.Null(read.Reason);
        Assert.Empty(read.Warnings);
        Assert.Null(read.PlanType);
    }

    // ---- skills (section 5.5) ----

    [Fact]
    public void The_recommendation_service_is_deterministic_for_free_and_asks_an_agent_only_with_consent()
    {
        Assert.Equal(["AskAgentAsync", "Recommend"], MethodNames(typeof(ISkillRecommendationService)));
        Assert.Contains(typeof(QuotaConsent), Parameters(typeof(ISkillRecommendationService), nameof(ISkillRecommendationService.AskAgentAsync)));
        Assert.Equal(["ReadLocalAsync"], MethodNames(typeof(ISkillHealthReader)));
    }

    /// <summary>What the MCP server reads holds catalog metadata only: no member could carry a SKILL.md body or a path.</summary>
    [Fact]
    public void The_catalog_snapshot_for_the_mcp_server_carries_no_body_nor_path()
    {
        var names = typeof(SnapshotSkill).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Path", StringComparison.Ordinal) || n.Contains("Body", StringComparison.Ordinal) || n.Contains("Content", StringComparison.Ordinal));
        Assert.Equal(["At", "Skills"], typeof(SkillCatalogSnapshot).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_usage_of_one_model_keeps_every_figure_with_its_origin_and_the_cost_basis()
    {
        var model = new ModelUsage(
            "claude-haiku-4-5",
            MetricValue<long>.Exact(1, "m"),
            MetricValue<long>.Exact(2, "m"),
            MetricValue<long>.Exact(3, "m"),
            MetricValue<long>.Exact(4, "m"),
            MetricValue<decimal>.Estimated(0.01m, "m"),
            MetricValue<long>.Exact(200_000, "m"),
            null);

        Assert.Equal(MetricOrigin.EstimatedByProvider, model.CostUsd.Origin);
        Assert.Null(model.CostBasis);
    }
}
