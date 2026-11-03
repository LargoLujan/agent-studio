using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Decisions;
using AgentStudio.Observability.Usage;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/14 §9 WP2 and ADR-0019: every query projects the whole log, so its cost is measured over a large synthetic log
/// and the data decides whether a cache is needed. The always-on test projects a log of 20 000 entries, checks the
/// deduplication on it (what the attempts of a session add up to is the last total the CLI gave) and keeps a loose
/// ceiling against a projection that stops being linear. The measurement proper (skipped unless
/// <c>AGENTSTUDIO_USAGE_MEASURE=1</c>) goes up to half a million entries; its report goes to the test output and, when
/// <c>AGENTSTUDIO_USAGE_MEASURE_REPORT</c> names a file, is appended to it. It measures the projection, the grouping and
/// the whole query over logs already in memory: reading the log from SQLite is <c>IProjectLogReader</c>'s (WP3).
/// </summary>
public sealed class UsageProjectionMeasurementTests
{
    public const string MeasureVariable = "AGENTSTUDIO_USAGE_MEASURE";

    public const string ReportVariable = "AGENTSTUDIO_USAGE_MEASURE_REPORT";

    private static readonly UsageQuery Year = new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"),
        [UsageDimension.Provider, UsageDimension.Model, UsageDimension.Day]);

    [Fact]
    public async Task A_log_of_twenty_thousand_entries_is_projected_and_deduplicated_well_under_a_ceiling()
    {
        var (entries, attempts, totals) = SyntheticUsageLog.Build(20_000);

        var clock = Stopwatch.StartNew();
        var projection = UsageProjector.Project(UsageLogBuilder.ProjectId, entries);
        var projected = clock.Elapsed;
        var report = await Query(entries);
        var queried = clock.Elapsed - projected;

        Assert.Equal(attempts, projection.Attempts.Count);
        Assert.Empty(projection.Warnings);
        var added = projection.Attempts
            .Where(a => a.SessionId is not null)
            .GroupBy(a => (a.Provider, a.SessionId!))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Own.InputTokens.Value ?? 0));
        Assert.Equal(totals.Count, added.Count);
        Assert.All(totals, total => Assert.Equal(total.Value, added[total.Key]));
        Assert.Equal(attempts, report.Groups[0].Totals.Attempts);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{entries.Count} entries, {attempts} attempts: projection {projected.TotalMilliseconds:F0} ms, query {queried.TotalMilliseconds:F0} ms."));
        Assert.True(projected + queried < TimeSpan.FromSeconds(15), $"Projecting 20 000 entries took {projected + queried}.");
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public async Task The_projection_of_logs_of_up_to_half_a_million_entries()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(MeasureVariable) == "1", $"Set {MeasureVariable}=1 to measure the usage projection.");
        // Wall time is what the panel waits; the processor time of the process (all its threads) is steadier on a busy
        // machine, and the allocations do not depend on it at all.
        var process = Process.GetCurrentProcess();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Usage projection ({DateTimeOffset.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} logical processors, {RuntimeInformation()})")
            .AppendLine("entries | attempts | read the payloads alone, one thread | project: wall, processor (median of 5) | allocated per projection | group by provider, model, day | query: wall (median of 3) | the same log regrouped (kept projection)");

        foreach (var size in new[] { 10_000, 50_000, 100_000, 200_000, 500_000 })
        {
            var (entries, attempts, _) = SyntheticUsageLog.Build(size);

            UsageProjector.Project(UsageLogBuilder.ProjectId, entries); // warm-up
            var reading = Stopwatch.StartNew();
            foreach (var entry in entries.Where(e => e.Kind is DecisionKind.AgentRunStarted or DecisionKind.AgentRunFinished))
            {
                _ = DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
            }

            reading.Stop();
            var walls = new List<TimeSpan>();
            var processors = new List<TimeSpan>();
            UsageProjection? projection = null;
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < 5; i++)
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                projection = UsageProjector.Project(UsageLogBuilder.ProjectId, entries);
                walls.Add(clock.Elapsed);
                process.Refresh();
                processors.Add(process.TotalProcessorTime - cpu);
            }

            var allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / 5;
            var grouping = Stopwatch.StartNew();
            var groups = UsageAggregator.Group(UsageAggregator.Select(projection!.Attempts, Year), Year);
            grouping.Stop();

            var queries = new List<TimeSpan>();
            for (var i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                await Query(entries);
                queries.Add(clock.Elapsed);
            }

            // The same projector again, as the panel does when it changes the grouping: the log did not change.
            var projector = new UsageProjector(new InMemoryLog(entries), new NoJournal());
            await projector.QueryAsync(["C:\\repos\\grande"], Year, TestContext.Current.CancellationToken);
            var again = Stopwatch.StartNew();
            await projector.QueryAsync(["C:\\repos\\grande"], Year with { GroupBy = [UsageDimension.Agent, UsageDimension.Week] }, TestContext.Current.CancellationToken);
            again.Stop();

            Assert.Equal(attempts, projection.Attempts.Count);
            report.AppendLine(CultureInfo.InvariantCulture, $"{entries.Count} | {attempts} | {reading.ElapsedMilliseconds} ms | {Median(walls).TotalMilliseconds:F0} ms, {Median(processors).TotalMilliseconds:F0} ms | {allocated / (1024 * 1024)} MB | {grouping.ElapsedMilliseconds} ms ({groups.Count} groups) | {Median(queries).TotalMilliseconds:F0} ms | {again.ElapsedMilliseconds} ms");
        }

        var text = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } file)
        {
            await File.AppendAllTextAsync(file, text + Environment.NewLine, TestContext.Current.CancellationToken);
        }
    }

    private static Task<UsageReport> Query(IReadOnlyList<DecisionEntry> entries) =>
        new UsageProjector(new InMemoryLog(entries), new NoJournal()).QueryAsync(["C:\\repos\\grande"], Year, TestContext.Current.CancellationToken);

    private static TimeSpan Median(List<TimeSpan> values) => values.Order().ElementAt(values.Count / 2);

    private static string RuntimeInformation() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    private sealed class InMemoryLog(IReadOnlyList<DecisionEntry> entries) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) =>
            Task.FromResult(new ProjectLogSnapshot(UsageLogBuilder.ProjectId, projectRoot, entries, DecisionLogVerification.Valid, AnchorStatus.Anchored));
    }

    private sealed class NoJournal : IProjectStorageReader
    {
        public Stream OpenForRead(string projectRoot, string relativePath) => throw new FileNotFoundException("No journal.", relativePath);
    }
}
