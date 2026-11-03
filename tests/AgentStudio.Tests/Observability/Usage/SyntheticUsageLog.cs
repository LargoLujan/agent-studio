using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// A large decision log shaped like real ones, from a seeded <see cref="Random"/>: runs of executor attempts with the
/// entries around them (task transitions, instructions, evidence), a third of them retried in the same session, a planner
/// with its correction and a judge per run. Claude attempts of version 2 carry a breakdown by two models; some are
/// Codex, some are version 1, some are interrupted. The hashes are not real: the projection reads verified logs and
/// never checks them again.
/// </summary>
internal static class SyntheticUsageLog
{
    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    /// <summary>The log, the number of attempts that ended in it, and the last input snapshot of every session.</summary>
    public static (IReadOnlyList<DecisionEntry> Entries, int Attempts, Dictionary<(ProviderId, string), long> SessionTotals) Build(int targetEntries, int seed = 20261018)
    {
        var random = new Random(seed);
        var entries = new List<DecisionEntry>(targetEntries + 64);
        var totals = new Dictionary<(ProviderId, string), long>();
        var attempts = 0;
        var at = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var noise = DecisionPayloadSerializer.Serialize(new TaskStatusChangedPayload(new TaskId("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "start", null, [], 1) { RunId = "noise" });
        var evidence = DecisionPayloadSerializer.Serialize(new EvidenceRegisteredPayload(new EvidenceId("ev-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, new string('e', 64), new TaskId("T1"), 1) { RunId = "noise" });

        void Add(DecisionKind kind, string payload)
        {
            at = at.AddSeconds(random.Next(1, 30));
            entries.Add(new DecisionEntry(entries.Count + 1, at, SystemActor, kind, kind.ToString(), payload, null, "p", "h"));
        }

        void Attempt(string run, string? task, int attempt, string agent, ProviderId provider, AgentRunPurpose purpose, string session, bool resumed, bool v2, bool interrupted)
        {
            var started = new AgentRunStartedPayload(task is null ? null : new TaskId(task), attempt, new AgentId(agent), provider, provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic, provider == ProviderId.CodexCli ? "gpt-6" : "claude-sonnet-5-5", "C:\\worktrees\\" + run, new EvidenceId("ev-p"), new string('a', 64), [], v2 ? purpose : null)
            {
                RunId = run,
                V = v2 ? 2 : 1,
            };
            Add(DecisionKind.AgentRunStarted, DecisionPayloadSerializer.Serialize(started));
            Add(DecisionKind.TaskStatusChanged, noise);
            UsageSnapshot? usage = null;
            if (!interrupted)
            {
                var key = (provider, session);
                var input = totals.GetValueOrDefault(key) + random.Next(1_000, 60_000);
                totals[key] = input;
                usage = v2 ? V2(provider, input) : V1(input);
            }

            var finished = new AgentRunFinishedPayload(
                task is null ? null : new TaskId(task),
                attempt,
                interrupted ? AttemptOutcome.Interrupted : AttemptOutcome.Succeeded,
                interrupted ? null : 0,
                interrupted ? null : session,
                null,
                usage,
                null,
                v2 && !interrupted ? at : null,
                v2 && !interrupted ? "claude-sonnet-5-5" : null,
                v2 && resumed ? session : null,
                v2 && !interrupted && provider == ProviderId.ClaudeCode ? new ContextPeakSnapshot("claude-sonnet-5-5", Snapshot(random.Next(1_000, 900_000)), Snapshot(1_000_000)) : null,
                v2 && !interrupted && provider == ProviderId.ClaudeCode ? random.Next(0, 2) : null)
            {
                RunId = run,
                V = v2 ? 2 : 1,
            };
            Add(DecisionKind.EvidenceRegistered, evidence);
            Add(DecisionKind.AgentRunFinished, DecisionPayloadSerializer.Serialize(finished));
            attempts++;
        }

        for (var r = 0; entries.Count < targetEntries; r++)
        {
            var run = $"2026{r / 1000 % 12 + 1:D2}{r % 28 + 1:D2}-{r:x6}";
            var v2 = random.NextDouble() < 0.8;
            Attempt(run, null, 1, "orquestador", ProviderId.ClaudeCode, AgentRunPurpose.Planner, run + "-plan", false, v2, false);
            if (random.NextDouble() < 0.2)
            {
                Attempt(run, null, 2, "orquestador", ProviderId.ClaudeCode, AgentRunPurpose.Planner, run + "-plan", true, v2, false);
            }

            for (var t = 1; t <= 8; t++)
            {
                var task = "T" + t;
                var provider = random.NextDouble() < 0.25 ? ProviderId.CodexCli : ProviderId.ClaudeCode;
                var session = $"{run}-{task}";
                var tries = random.NextDouble() < 0.33 ? random.Next(2, 4) : 1;
                for (var a = 1; a <= tries; a++)
                {
                    Attempt(run, task, a, "ejecutor", provider, AgentRunPurpose.Executor, session, a > 1, v2, interrupted: a < tries && random.NextDouble() < 0.3);
                }
            }

            Attempt(run, null, 1, "juez", ProviderId.CodexCli, AgentRunPurpose.Judge, run + "-judge", false, v2, false);
        }

        return (entries, attempts, totals);
    }

    private static MetricSnapshot Snapshot(long value, string source = "claude result.usage") => MetricSnapshot.WithOrigin(MetricValue<long>.Exact(value, source));

    private static UsageSnapshot V2(ProviderId provider, long input)
    {
        if (provider == ProviderId.CodexCli)
        {
            return new UsageSnapshot(Snapshot(input), Snapshot(input / 10), Snapshot(input / 3), Snapshot(0), MetricSnapshot.WithOrigin(MetricValue<decimal>.NotExposed("codex")), Snapshot(0), Snapshot(input / 50), []);
        }

        var cost = MetricSnapshot.WithOrigin(MetricValue<decimal>.Estimated(input / 1_000_000m, "estimación del CLI"));
        var main = input - (input / 10);
        ModelUsageSnapshot Model(string name, long tokens) =>
            new(name, Snapshot(tokens, "claude result.modelUsage"), Snapshot(tokens / 10, "claude result.modelUsage"), Snapshot(tokens / 3, "claude result.modelUsage"), Snapshot(tokens / 5, "claude result.modelUsage"), MetricSnapshot.WithOrigin(MetricValue<decimal>.Estimated(tokens / 1_000_000m, "estimación del CLI")), Snapshot(name.Contains("haiku", StringComparison.Ordinal) ? 200_000 : 1_000_000, "claude result.modelUsage"), "list");
        return new UsageSnapshot(Snapshot(input), Snapshot(input / 10), Snapshot(input / 3), Snapshot(1_000_000), cost, Snapshot(input / 5), MetricSnapshot.WithOrigin(MetricValue<long>.NotExposed("claude")), [Model("claude-sonnet-5-5", main), Model("claude-haiku-4-5", input - main)]);
    }

    private static UsageSnapshot V1(long input) => new(
        MetricSnapshot.From(MetricValue<long>.Exact(input, "claude result.usage")),
        MetricSnapshot.From(MetricValue<long>.Exact(input / 10, "claude result.usage")),
        MetricSnapshot.From(MetricValue<long>.Exact(input / 3, "claude result.usage")),
        MetricSnapshot.From(MetricValue<long>.Exact(200_000, "claude result.modelUsage")),
        MetricSnapshot.From(MetricValue<decimal>.Estimated(input / 1_000_000m, "estimación del CLI")));
}
