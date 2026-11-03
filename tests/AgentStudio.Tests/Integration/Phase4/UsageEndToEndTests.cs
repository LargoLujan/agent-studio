using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// F4-1 and F4-2 end to end (docs/14 section 9, «Integración»): a real run of the engine composed as the app composes it
/// (SQLite, a temporary git repository, the provider pipeline with the project's journal and the user's quota record),
/// with scripted CLIs that report usage as the Phase 4 parsers do: tokens with their origin, the model of each step, the
/// session, the context of each step and a compaction. The usage panel's report (<see cref="IUsageReport"/>, the real
/// <see cref="UsageProjector"/> reading the log through <c>IProjectLogReader</c>) projects that run from the verified log:
/// every attempt in version 2, each figure with its origin, the retry that resumed the session counted once (D-02), and the
/// grand total first. The export to CSV and JSON carries figures and identifiers, never a prompt nor agent text (D-07).
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class UsageEndToEndTests
{
    private const string Model = "claude-sonnet-5-5";
    private const long Window = 1_000_000;

    // Text that reaches the prompts, the agents' answers and the log, and that no export may carry.
    private const string RequestText = "Peticion-marcada-USO-7d1: escribe a.txt con Claude y b.txt con Codex.";
    private const string ObjectiveT1 = "Objetivo-marcado-USO-T1";
    private const string InstructionsT1 = "Instrucciones-marcadas-USO-T1";
    private const string ObjectiveT2 = "Objetivo-marcado-USO-T2";
    private const string ErrorT1 = "Error-marcado-USO-T1: el CLI se cayo a mitad de la tarea.";
    private const string ReportT1 = "Informe-marcado-USO-T1";
    private const string FinalT1 = "Texto-final-marcado-USO-T1";
    private const string ReportT2 = "Informe-marcado-USO-T2";

    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContextObserved Step(DateTimeOffset at, long tokens) =>
        new(at, Model, MetricValue<long>.Exact(tokens, "claude assistant.message.usage"), MetricValue<long>.Missing("claude aún no informa de la ventana"));

    /// <summary>What Claude reports at the end of a run: the totals of its session, with the breakdown by model.</summary>
    private static UsageReported ClaudeUsage(DateTimeOffset at, long input, long cacheRead, long cacheWrite, long output, decimal cost, int turns) =>
        new(at, new TokenUsage(
            MetricValue<long>.Exact(input, "claude result.usage"),
            MetricValue<long>.Exact(output, "claude result.usage"),
            MetricValue<long>.Exact(cacheRead, "claude result.usage"),
            MetricValue<long>.Exact(Window, "claude result.modelUsage"),
            MetricValue<decimal>.Estimated(cost, "claude total_cost_usd (precio de lista)"))
        {
            CacheWriteInputTokens = MetricValue<long>.Exact(cacheWrite, "claude result.usage"),
            PerModel =
            [
                new ModelUsage(
                    Model,
                    MetricValue<long>.Exact(input, "claude result.modelUsage"),
                    MetricValue<long>.Exact(output, "claude result.modelUsage"),
                    MetricValue<long>.Exact(cacheRead, "claude result.modelUsage"),
                    MetricValue<long>.Exact(cacheWrite, "claude result.modelUsage"),
                    MetricValue<decimal>.Estimated(cost, "claude result.modelUsage.costUSD (precio de lista)"),
                    MetricValue<long>.Exact(Window, "claude result.modelUsage"),
                    "list"),
            ],
            Turns = turns,
        });

    /// <summary>What <c>codex exec</c> reports: the usage of the thread, with reasoning, and neither window nor cost.</summary>
    private static UsageReported CodexUsage(DateTimeOffset at) =>
        new(at, new TokenUsage(
            MetricValue<long>.Exact(9_000, "codex turn.completed usage"),
            MetricValue<long>.Exact(600, "codex turn.completed usage"),
            MetricValue<long>.Exact(4_000, "codex turn.completed usage"),
            MetricValue<long>.NotExposed("codex exec no expone la ventana de contexto"),
            MetricValue<decimal>.NotExposed("codex no expone el coste"))
        {
            ReasoningOutputTokens = MetricValue<long>.Exact(250, "codex turn.completed usage"),
        });

    /// <summary>The windows Claude announces at the start of every run (<c>rate_limit_event</c>).</summary>
    private static QuotaReported ClaudeQuota(DateTimeOffset at, double fiveHour, double sevenDay) =>
        new(at,
        [
            new QuotaWindow("five_hour", MetricValue<double>.Exact(fiveHour, "claude rate_limit_event"), at.AddHours(3)),
            new QuotaWindow("seven_day", MetricValue<double>.Exact(sevenDay, "claude rate_limit_event"), at.AddDays(4)),
        ]);

    [Fact]
    public async Task A_real_run_is_projected_from_the_log_in_version_2_with_origins_one_count_per_session_and_the_grand_total_and_exported_without_prompts()
    {
        using var repo = TempGitRepo.Create();
        var paths = Phase4Paths.Beside(repo.Root);
        var at = DateTimeOffset.UtcNow;
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var codex = ScriptedCli.Codex(TimeProvider.System);

        // T1 on Claude: the first attempt fails after spending (its session has 21 000 tokens of input); the retry resumes
        // that session, which ends with 52 000 (the CLI reports the total of the session, D-02).
        claude.Script.Script("ejecutor", "T1", 1, AgentScript.Failing(ErrorT1) with
        {
            Events = [ClaudeQuota(at, 0.31, 0.12), Step(at, 8_000), Step(at, 17_500), ClaudeUsage(at, 21_000, 12_000, 3_000, 450, 0.021m, 2)],
        });
        claude.Script.Script("ejecutor", "T1", 2, AgentScript.Completed(("src/T1/a.txt", "a\n")) with
        {
            StructuredOutput = AgentScript.Report(summary: ReportT1, filesChanged: ["src/T1/a.txt"]),
            FinalText = FinalT1,
            Events =
            [
                ClaudeQuota(at.AddSeconds(1), 0.33, 0.14),
                Step(at.AddSeconds(1), 19_000),
                new CompactionObserved(at.AddSeconds(1), "auto", MetricValue<long>.Exact(19_000, "claude compact_boundary")),
                Step(at.AddSeconds(1), 31_000),
                ClaudeUsage(at.AddSeconds(1), 52_000, 30_000, 5_000, 1_150, 0.054m, 5),
            ],
        });

        // T2 on Codex, in parallel: one attempt, no context signals.
        codex.Script.Script("ejecutorCodex", "T2", 1, AgentScript.Completed(("src/T2/b.txt", "b\n")) with
        {
            StructuredOutput = AgentScript.Report(summary: ReportT2, filesChanged: ["src/T2/b.txt"]),
            Events = [CodexUsage(at)],
        });

        var plan = Plan(
            Work("T1", scope: ["src/T1/"]) with { Objective = ObjectiveT1, Instructions = InstructionsT1 },
            Work("T2", agent: "ejecutorCodex", scope: ["src/T2/"]) with { Objective = ObjectiveT2 });
        var services = Phase4Composition.Services(paths, new ScriptedApprovalPrompt(), Phase4ScriptedProviders.Over(claude, codex)).WithoutCliProbes();
        await using var app = await Phase4App.OpenAsync(repo.Root, services, Roster());

        await using var run = await app.Orchestrator.StartAsync(await app.RunRequestAsync(RequestText, Requirements(), plan, new VerificationProfile("sin verificación", [])), Ct);
        var result = await run.Completion.WaitAsync(Guard, Ct);
        var log = await app.LoadLogAsync();
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(log));
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        Assert.Equal(["T1#1", "T1#2"], claude.Script.Starts.Select(s => $"{s.Request.TaskId}#{s.Attempt}"));
        Assert.Equal(["T2#1"], codex.Script.Starts.Select(s => $"{s.Request.TaskId}#{s.Attempt}"));

        // What the log holds and the export must never carry: the prompts and the error the CLI gave.
        Assert.All(claude.Script.Starts, s => Assert.Contains(ObjectiveT1, s.Request.Prompt, StringComparison.Ordinal));
        var ends = await app.PayloadsAsync<AgentRunFinishedPayload>(run.Id.Value);
        var t1 = new TaskId("T1");
        var failed = Assert.Single(ends, e => e.Payload.Task == t1 && e.Payload.Attempt == 1).Payload;
        Assert.Contains(ErrorT1, failed.Error, StringComparison.Ordinal);
        Assert.Equal("session-ejecutor-T1-1", Assert.Single(ends, e => e.Payload.Task == t1 && e.Payload.Attempt == 2).Payload.ResumedSession);

        // ---- The report of the panel, read from the project's log without the lock and verified against its anchor.
        var now = DateTimeOffset.UtcNow;
        var query = new UsageQuery(now.AddDays(-1), now.AddDays(1), TimeZoneInfo.Local, [UsageDimension.Provider, UsageDimension.Model]);
        var report = await app.Get<IUsageReport>().QueryAsync([repo.Root], query, Ct);

        Assert.True(report.Excluded.Count == 0, string.Join("; ", report.Excluded.Select(e => $"{e.Root}: {e.Reason}")));
        Assert.Equal(3, report.Attempts.Count);
        // Since Phase 8 the engine records the end in version 3 (docs/16 D-41), which the usage reads as a version 2.
        Assert.All(report.Attempts, a => Assert.Equal((app.Project.ProjectId, run.Id.Value, AgentRunFinishedPayload.EndClassVersion, AgentRunPurpose.Executor), (a.ProjectId, a.RunId, a.RecordedVersion, a.Purpose)));
        var first = Assert.Single(report.Attempts, a => a is { TaskId: "T1", Attempt: 1 });
        var retry = Assert.Single(report.Attempts, a => a is { TaskId: "T1", Attempt: 2 });
        var other = Assert.Single(report.Attempts, a => a is { TaskId: "T2", Attempt: 1 });

        // Who, where and how it ended. The model is the one of the steps (D-13), never the configured «haiku».
        Assert.Equal((AttemptOutcome.Failed, AttemptOutcome.Succeeded, AttemptOutcome.Succeeded), (first.Outcome, retry.Outcome, other.Outcome));
        Assert.Equal((ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", Model), (first.Provider, first.Vendor, first.ConfiguredModel, first.ReportedModel));
        Assert.Equal((ProviderId.CodexCli, VendorId.OpenAI, (string?)null, (string?)null), (other.Provider, other.Vendor, other.ConfiguredModel, other.ReportedModel));
        Assert.Equal(UsageAttemptFacts.DefaultModel, other.Model());
        Assert.Equal(("session-ejecutor-T1-1", "session-ejecutor-T1-2"), (first.SessionId, retry.SessionId));
        Assert.All(report.Attempts, a => Assert.True(a.LaunchedAt is { } launched && launched >= a.RecordedAt && launched <= a.FinishedAt, $"{a.TaskId}#{a.Attempt} launched at {a.LaunchedAt}"));
        Assert.All(report.Attempts, a => Assert.Equal(MetricOrigin.MeasuredBySystem, a.Duration().Origin));

        // Every figure with its origin (D-03): Claude's tokens reported by the CLI and its cost an estimate of the CLI;
        // what a CLI does not give is «no expuesto», never a zero.
        Assert.Equal((21_000L, MetricOrigin.ReportedByProvider), (first.Own.InputTokens.Value, first.Own.InputTokens.Origin));
        Assert.Equal((0.021m, MetricOrigin.EstimatedByProvider), (first.Own.CostUsd.Value, first.Own.CostUsd.Origin));
        Assert.Equal(MetricOrigin.NotExposed, first.Own.ReasoningOutputTokens.Origin);
        Assert.Equal((9_000L, 4_000L, 600L, 250L), (other.Own.InputTokens.Value!.Value, other.Own.CacheReadInputTokens.Value!.Value, other.Own.OutputTokens.Value!.Value, other.Own.ReasoningOutputTokens.Value!.Value));
        Assert.All(new[] { other.Own.InputTokens, other.Own.ReasoningOutputTokens }, f => Assert.Equal(MetricOrigin.ReportedByProvider, f.Origin));
        Assert.Equal((MetricOrigin.NotExposed, MetricOrigin.NotExposed), (other.Own.CostUsd.Origin, other.Own.CacheWriteInputTokens.Origin));
        Assert.Null(other.Own.CostUsd.Value);

        // One count per session (D-02): the retry is its snapshot minus the first attempt's, still reported by the CLI,
        // and both attempts carry the total of the session. Adding up the attempts gives the session, not 73 000.
        Assert.Equal((31_000L, 18_000L, 2_000L, 700L, 0.033m), (retry.Own.InputTokens.Value!.Value, retry.Own.CacheReadInputTokens.Value!.Value, retry.Own.CacheWriteInputTokens.Value!.Value, retry.Own.OutputTokens.Value!.Value, retry.Own.CostUsd.Value!.Value));
        Assert.Equal((MetricOrigin.ReportedByProvider, MetricOrigin.EstimatedByProvider), (retry.Own.InputTokens.Origin, retry.Own.CostUsd.Origin));
        Assert.Contains("menos el intento anterior de la sesión", retry.Own.InputTokens.Source, StringComparison.Ordinal);
        Assert.All(new[] { first, retry }, a => Assert.Equal((52_000L, 1_150L), (a.SessionTotal!.InputTokens.Value!.Value, a.SessionTotal.OutputTokens.Value!.Value)));
        Assert.Equal(retry.SessionTotal!.InputTokens.Value, first.Own.InputTokens.Value + retry.Own.InputTokens.Value);
        Assert.Null(other.SessionTotal);
        var model = Assert.Single(retry.PerModel);
        Assert.Equal((Model, 31_000L, Window, MetricOrigin.ReportedByProvider, "list"), (model.Model, model.Figures.InputTokens.Value!.Value, model.ContextWindow.Value!.Value, model.ContextWindow.Origin, model.CostBasis));
        Assert.Empty(other.PerModel);

        // The context: the largest step with the window of its model at the end, and the compactions Agent Studio
        // counted; codex exec says neither.
        Assert.Equal((Model, 31_000L, Window), (retry.ContextPeak!.Model, retry.ContextPeak.UsedTokens.Value!.Value, retry.ContextPeak.ContextWindow.Value!.Value));
        Assert.Equal((MetricOrigin.ReportedByProvider, MetricOrigin.ReportedByProvider), (retry.ContextPeak.UsedTokens.Origin, retry.ContextPeak.ContextWindow.Origin));
        Assert.Equal(17_500L, first.ContextPeak!.UsedTokens.Value);
        Assert.Equal(((int?)0, (int?)1, (int?)null), (first.Compactions, retry.Compactions, other.Compactions));
        Assert.Equal(MetricOrigin.NotExposed, other.ContextPeak!.UsedTokens.Origin);
        Assert.Equal(MetricOrigin.NotExposed, other.CompactionCount().Origin);

        // The grand total first, with an empty key: sums that never mix origins and say what they miss («parcial»).
        var total = report.Groups[0];
        Assert.Empty(total.Key);
        Assert.Equal(3, total.Totals.Attempts);
        Assert.Equal((61_000L, 3, 0, MetricOrigin.ReportedByProvider), (total.Totals.InputTokens.Sum!.Value, total.Totals.InputTokens.Reported, total.Totals.InputTokens.Missing, total.Totals.InputTokens.Origin));
        Assert.Equal((34_000L, 1_750L), (total.Totals.CacheReadInputTokens.Sum!.Value, total.Totals.OutputTokens.Sum!.Value));
        Assert.Equal((5_000L, 2, 1, true), (total.Totals.CacheWriteInputTokens.Sum!.Value, total.Totals.CacheWriteInputTokens.Reported, total.Totals.CacheWriteInputTokens.Missing, total.Totals.CacheWriteInputTokens.IsPartial));
        Assert.Equal((250L, 1, 2), (total.Totals.ReasoningOutputTokens.Sum!.Value, total.Totals.ReasoningOutputTokens.Reported, total.Totals.ReasoningOutputTokens.Missing));
        Assert.Equal((0.054m, 2, 1, MetricOrigin.EstimatedByProvider), (total.Totals.CostUsd.Sum!.Value, total.Totals.CostUsd.Reported, total.Totals.CostUsd.Missing, total.Totals.CostUsd.Origin));
        Assert.Equal((3, 0, MetricOrigin.MeasuredBySystem), (total.Totals.DurationMilliseconds.Reported, total.Totals.DurationMilliseconds.Missing, total.Totals.DurationMilliseconds.Origin));
        Assert.Equal((1L, 2, 1), (total.Totals.Compactions.Sum!.Value, total.Totals.Compactions.Reported, total.Totals.Compactions.Missing));
        Assert.Equal((0, 0), (total.Totals.RateLimited, total.Totals.LimitReached));
        Assert.Equal((Model, 31_000L), (total.Totals.ContextPeak!.Model, total.Totals.ContextPeak.UsedTokens.Value!.Value));

        // Then one group per provider and model, in order.
        Assert.Equal(
            [
                ($"{ProviderId.ClaudeCode.Value}|{Model}", 2, 52_000L),
                ($"{ProviderId.CodexCli.Value}|{UsageAttemptFacts.DefaultModel}", 1, 9_000L),
            ],
            report.Groups.Skip(1).Select(g => (string.Join('|', g.Key.Select(k => k.Value)), g.Totals.Attempts, g.Totals.InputTokens.Sum!.Value)));
        Assert.Equal(3, report.Journal.RecordedAttempts);

        // ---- The quota Claude announced in the run reached the user's record (D-14): the last of each window.
        var quota = await app.Get<IQuotaObservations>().LatestAsync(Ct);
        Assert.Equal([("five_hour", 0.33), ("seven_day", 0.14)], quota.Where(o => o.Provider == ProviderId.ClaudeCode).Select(o => (o.Window, o.Utilization.Value!.Value)));
        Assert.True(File.Exists(paths.QuotaLog));

        // ---- The export (D-07): CSV with BOM, one row per attempt and one per model; JSON agentstudio-usage/1.
        var exporter = app.Get<IUsageExporter>();
        using var csvStream = new MemoryStream();
        await exporter.WriteCsvAsync(report, csvStream, Ct);
        var csvBytes = csvStream.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes[..3]);
        var csv = Encoding.UTF8.GetString(csvBytes, 3, csvBytes.Length - 3);
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("row,project_id,run_id,task_id,attempt,", rows[0], StringComparison.Ordinal);
        Assert.Equal(3, rows.Count(r => r.StartsWith("intento,", StringComparison.Ordinal)));
        Assert.Equal(2, rows.Count(r => r.StartsWith("modelo,", StringComparison.Ordinal)));
        Assert.All(rows.Skip(1), r => Assert.Contains(run.Id.Value, r, StringComparison.Ordinal));

        using var jsonStream = new MemoryStream();
        await exporter.WriteJsonAsync(report, jsonStream, Ct);
        var json = Encoding.UTF8.GetString(jsonStream.ToArray());
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            Assert.Equal(UsageExporter.JsonSchema, root.GetProperty("schema").GetString());
            var exported = root.GetProperty("attempts").EnumerateArray().ToList();
            Assert.Equal(3, exported.Count);
            Assert.All(exported, a => Assert.Equal(AgentRunFinishedPayload.EndClassVersion, a.GetProperty("recordedVersion").GetInt32()));
            Assert.Equal(61_000, root.GetProperty("aggregates")[0].GetProperty("totals").GetProperty("inputTokens").GetProperty("value").GetInt64());
        }

        // No prompt, no answer of an agent and no error text of a CLI: only identifiers and figures.
        string[] neverExported = [RequestText, ObjectiveT1, InstructionsT1, ObjectiveT2, ErrorT1, ReportT1, FinalT1, ReportT2, "Trabaja solo dentro", "Instrucciones de sistema de", "Hecho según el objetivo"];
        foreach (var text in neverExported)
        {
            Assert.DoesNotContain(text, csv, StringComparison.Ordinal);
            Assert.DoesNotContain(text, json, StringComparison.Ordinal);
        }
    }
}
