using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Usage;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Observability.Usage.UsageAttempts;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/14 D-07 (F4-2): the report is exported as CSV (RFC 4180, UTF-8 with BOM, invariant numbers, ISO-8601 dates in
/// UTC and local, three columns per figure) and as JSON <c>agentstudio-usage/1</c> (each figure with its value, origin and
/// source). A text field that starts like a formula gets an apostrophe, and no prompt nor agent output is ever written.
/// </summary>
public sealed class UsageExporterTests
{
    private const string ModelUsage = "claude result.modelUsage";
    private const string Codex = "codex turn.completed.usage";

    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static readonly DateTimeOffset GeneratedAt = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly UsageQuery GoldenQuery = new(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), Madrid, [UsageDimension.Provider]);

    /// <summary>
    /// A Claude attempt with a breakdown by model and a shared session (around the end of summer time in Madrid), a Codex
    /// judge without breakdown nor cost, and an interrupted attempt of version 1; one project excluded; a reconciliation.
    /// </summary>
    internal static UsageReport GoldenReport()
    {
        var claude = Make(
            new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero),
            reportedModel: "claude-sonnet-5-5",
            task: "T1, «parte 2»",
            attempt: 2,
            session: "s1",
            own: Tokens(600, 60, read: 300, write: 100, cost: 0.06m),
            sessionTotal: Tokens(1600, 160, read: 500, write: 400, cost: 0.16m),
            perModel:
            [
                new ModelUsageFigures("claude-sonnet-5-5", Tokens(500, 50, read: 300, write: 100, cost: 0.05m, source: ModelUsage), MetricValue<long>.Exact(1_000_000, ModelUsage), "list"),
                new ModelUsageFigures("claude-haiku-4-5", Tokens(100, 10, cost: 0.01m, source: ModelUsage), MetricValue<long>.Exact(200_000, ModelUsage), "list"),
            ],
            peak: Peak("claude-sonnet-5-5", 30_822, 1_000_000),
            compactions: 1,
            duration: TimeSpan.FromSeconds(42.5));
        var judge = Make(
            new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero),
            provider: ProviderId.CodexCli,
            configuredModel: "gpt-6",
            task: null,
            purpose: AgentRunPurpose.Judge,
            agent: "juez",
            session: "th1",
            own: Tokens(900, 90, read: 100, write: 0, reasoning: 30, cost: null, source: Codex));
        var interrupted = Make(
            new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero),
            task: "T2",
            purpose: null,
            session: null,
            own: NoFigures(UsageProjector.NoResultReason),
            outcome: AttemptOutcome.Interrupted,
            version: 1,
            peak: NoPeak(MetricOrigin.Missing, "intento interrumpido: no se sabe"));
        UsageAttempt[] attempts = [claude, judge, interrupted];
        return new UsageReport(
            attempts,
            UsageAggregator.Group(attempts, GoldenQuery),
            [new ExcludedProject("C:\\repos\\beta", "6f1c2b0e-0000-4000-8000-000000000002", "El registro no verifica (entrada 3): The entry content does not match its hash (it was modified).")],
            new UsageReconciliation(4, 3, ["Bitácora: 4 prompts; registro: 3 intentos. Diferencia de 1 prompt, que no se suma (pruebas en vivo, versiones anteriores o intentos que no llegaron al proveedor)."]));
    }

    private static UsageExporter Exporter()
    {
        var clock = new FakeTimeProvider(GeneratedAt);
        clock.SetLocalTimeZone(Madrid);
        return new UsageExporter(clock);
    }

    private static async Task<byte[]> CsvAsync(UsageReport report, UsageQuery? query = null)
    {
        using var stream = new MemoryStream();
        await Exporter().WriteCsvAsync(report, query, stream, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static async Task<byte[]> JsonAsync(UsageReport report, UsageQuery? query = null)
    {
        using var stream = new MemoryStream();
        await Exporter().WriteJsonAsync(report, query, stream, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static string Text(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF');

    /// <summary>
    /// Compares with a golden file, line endings aside: git may check a fixture out with either. On a difference the
    /// output is left in the temporary folder, named in the failure.
    /// </summary>
    private static void AssertGolden(string name, byte[] actual)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Observability", "Usage", "Fixtures", name), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        var text = Text(actual).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(expected, text, StringComparison.Ordinal))
        {
            var dump = Path.Combine(Path.GetTempPath(), "agentstudio-golden-" + name);
            File.WriteAllBytes(dump, actual);
            Assert.Fail($"The export differs from the golden file '{name}'; the output is at '{dump}'.");
        }
    }

    private static List<List<string>> Records(string csv)
    {
        // A minimal RFC 4180 reader, independent of the writer.
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                i++;
            }
            else
            {
                field.Append(c);
            }
        }

        return records;
    }

    private static Dictionary<string, string> Row(byte[] csv, int index)
    {
        var records = Records(Text(csv));
        return records[0].Zip(records[index + 1]).ToDictionary(p => p.First, p => p.Second);
    }

    // ---- CSV ----

    [Fact]
    public async Task The_csv_is_the_golden_one()
    {
        AssertGolden("usage-report.csv", await CsvAsync(GoldenReport(), GoldenQuery));
    }

    [Fact]
    public async Task The_csv_is_utf8_with_a_bom_and_every_record_ends_with_crlf()
    {
        var bytes = await CsvAsync(GoldenReport(), GoldenQuery);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Text(bytes);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(6, Records(text).Count);
    }

    [Fact]
    public async Task A_field_with_a_comma_a_quote_or_a_line_break_is_quoted_as_rfc_4180_says()
    {
        var report = new UsageReport([Make(GeneratedAt, task: "a,b", session: "dice \"hola\"", configuredModel: "línea\r\notra")], [], [], new UsageReconciliation(null, 0, []));

        var bytes = await CsvAsync(report);

        var text = Text(bytes);
        Assert.Contains(",\"a,b\",", text, StringComparison.Ordinal);
        Assert.Contains(",\"dice \"\"hola\"\"\",", text, StringComparison.Ordinal);
        Assert.Contains(",\"línea\r\notra\",", text, StringComparison.Ordinal);
        var row = Row(bytes, 0);
        Assert.Equal(("a,b", "dice \"hola\"", "línea\r\notra"), (row["task_id"], row["session_id"], row["configured_model"]));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1")]
    [InlineData("-1+2")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tdato")]
    [InlineData("\rdato")]
    public async Task A_text_that_starts_like_a_formula_gets_an_apostrophe(string text)
    {
        var report = new UsageReport([Make(GeneratedAt, reportedModel: text, session: text)], [], [], new UsageReconciliation(null, 0, []));

        var row = Row(await CsvAsync(report), 0);

        Assert.Equal("'" + text, row["reported_model"]);
        Assert.Equal("'" + text, row["session_id"]);
        Assert.Equal("'" + text, row["model"]);
    }

    [Fact]
    public async Task A_text_that_does_not_start_like_a_formula_is_left_as_it_is()
    {
        var report = new UsageReport([Make(GeneratedAt, reportedModel: "gpt-6=x", session: "s-1")], [], [], new UsageReconciliation(null, 0, []));

        var row = Row(await CsvAsync(report), 0);

        Assert.Equal(("gpt-6=x", "s-1"), (row["reported_model"], row["session_id"]));
    }

    [Fact]
    public async Task A_figure_that_is_not_available_has_an_empty_value_and_says_why()
    {
        var row = Row(await CsvAsync(GoldenReport(), GoldenQuery), 3);

        Assert.Equal(("", "NotExposed", "codex exec --json does not report cost"), (row["cost_usd"], row["cost_usd_origin"], row["cost_usd_source"]));
        Assert.Equal(("30", "ReportedByProvider"), (row["reasoning_output_tokens"], row["reasoning_output_tokens_origin"]));
    }

    [Fact]
    public async Task Numbers_and_dates_do_not_depend_on_the_culture_of_the_machine()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            var report = new UsageReport([Make(GeneratedAt, own: Tokens(1_234_567, 10, cost: 1234.5m))], [], [], new UsageReconciliation(null, 0, []));

            var csv = Row(await CsvAsync(report), 0);
            var json = Text(await JsonAsync(report));

            Assert.Equal(("1234567", "1234.5"), (csv["input_tokens"], csv["cost_usd"]));
            Assert.Equal("2026-10-26T09:00:00.000Z", csv["finished_at_utc"]);
            Assert.Contains("\"value\": 1234.5,", json, StringComparison.Ordinal);
            Assert.DoesNotContain("1.234.567", json, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ---- JSON ----

    [Fact]
    public async Task The_json_is_the_golden_one()
    {
        AssertGolden("usage-report.json", await JsonAsync(GoldenReport(), GoldenQuery));
    }

    [Fact]
    public async Task The_json_round_trips_every_figure_with_its_origin_and_source()
    {
        var report = GoldenReport();

        using var json = JsonDocument.Parse(await JsonAsync(report, GoldenQuery));

        var attempts = json.RootElement.GetProperty("attempts");
        Assert.Equal(report.Attempts.Count, attempts.GetArrayLength());
        foreach (var (attempt, element) in report.Attempts.Zip(attempts.EnumerateArray()))
        {
            Assert.Equal(attempt.Duration(), Long(element.GetProperty("durationMilliseconds")));
            AssertFigures(attempt.Own, element.GetProperty("own"));
            if (attempt.SessionTotal is { } total)
            {
                AssertFigures(total, element.GetProperty("sessionTotal"));
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, element.GetProperty("sessionTotal").ValueKind);
            }

            var models = element.GetProperty("perModel");
            Assert.Equal(attempt.PerModel.Count, models.GetArrayLength());
            foreach (var (model, item) in attempt.PerModel.Zip(models.EnumerateArray()))
            {
                Assert.Equal((model.Model, model.CostBasis), (item.GetProperty("model").GetString(), item.GetProperty("costBasis").GetString()));
                Assert.Equal(model.ContextWindow, Long(item.GetProperty("contextWindow")));
                AssertFigures(model.Figures, item.GetProperty("figures"));
            }

            var peak = element.GetProperty("contextPeak");
            Assert.Equal(attempt.ContextPeak!.Model, peak.GetProperty("model").GetString());
            Assert.Equal(attempt.ContextPeak.UsedTokens, Long(peak.GetProperty("usedTokens")));
            Assert.Equal(attempt.ContextPeak.ContextWindow, Long(peak.GetProperty("contextWindow")));
            Assert.Equal(attempt.CompactionCount(), Long(element.GetProperty("compactions")));
            Assert.Equal(attempt.FinishedAt, DateTimeOffset.Parse(element.GetProperty("finishedAt").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(attempt.FinishedAt, DateTimeOffset.Parse(element.GetProperty("finishedAtLocal").GetString()!, CultureInfo.InvariantCulture));
        }

        var groups = json.RootElement.GetProperty("aggregates");
        Assert.Equal(report.Groups.Count, groups.GetArrayLength());
        foreach (var (group, element) in report.Groups.Zip(groups.EnumerateArray()))
        {
            Assert.Equal(group.Key.Select(k => (k.Dimension.ToString(), k.Value)), element.GetProperty("key").EnumerateArray().Select(k => (k.GetProperty("dimension").GetString()!, k.GetProperty("value").GetString()!)));
            var totals = element.GetProperty("totals");
            Assert.Equal(group.Totals.Attempts, totals.GetProperty("attempts").GetInt32());
            AssertAggregate(group.Totals.InputTokens, totals.GetProperty("inputTokens"));
            AssertAggregate(group.Totals.ReasoningOutputTokens, totals.GetProperty("reasoningOutputTokens"));
            AssertAggregate(group.Totals.DurationMilliseconds, totals.GetProperty("durationMilliseconds"));
            AssertAggregate(group.Totals.Compactions, totals.GetProperty("compactions"));
            var cost = totals.GetProperty("costUsd");
            Assert.Equal(group.Totals.CostUsd.Sum, cost.GetProperty("value").ValueKind == JsonValueKind.Null ? null : cost.GetProperty("value").GetDecimal());
            Assert.Equal(group.Totals.CostUsd.Origin.ToString(), cost.GetProperty("origin").GetString());
        }

        var journal = json.RootElement.GetProperty("journal");
        Assert.Equal((report.Journal.JournalPrompts, report.Journal.RecordedAttempts, report.Journal.Difference), (journal.GetProperty("journalPrompts").GetInt32(), journal.GetProperty("recordedAttempts").GetInt32(), journal.GetProperty("difference").GetInt32()));
    }

    [Fact]
    public async Task Through_the_port_the_zone_is_the_one_of_the_clock_and_the_filters_are_not_known()
    {
        IUsageExporter exporter = Exporter();
        using var stream = new MemoryStream();

        await exporter.WriteJsonAsync(GoldenReport(), stream, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(stream.ToArray());
        Assert.Equal("Europe/Madrid", json.RootElement.GetProperty("zone").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("filters").ValueKind);
        Assert.Equal("2026-10-26T09:00:00.000Z", json.RootElement.GetProperty("generatedAt").GetString());
    }

    [Fact]
    public async Task The_csv_through_the_port_shows_local_times_in_the_zone_of_the_clock()
    {
        IUsageExporter exporter = Exporter();
        using var stream = new MemoryStream();

        await exporter.WriteCsvAsync(GoldenReport(), stream, TestContext.Current.CancellationToken);

        Assert.Equal("2026-10-25T00:30:00.000+02:00", Row(stream.ToArray(), 0)["finished_at_local"]);
    }

    [Fact]
    public async Task No_prompt_nor_agent_output_reaches_an_export()
    {
        const string Run = "20261025-a1b2c3";
        var log = new UsageLogBuilder(GeneratedAt.AddHours(-1))
            .Start(Run, "T1", 1, workingDirectory: "C:\\carpeta-secreta\\wt")
            .Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Snapshots.Claude(10, 0, 0, 1, 0.001m), error: "TEXTO-DEL-AGENTE: borra el repositorio");
        var attempts = UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries).Attempts;
        var report = new UsageReport(attempts, UsageAggregator.Group(attempts, GoldenQuery), [], new UsageReconciliation(null, 1, []));

        var csv = Text(await CsvAsync(report));
        var json = Text(await JsonAsync(report));

        foreach (var export in new[] { csv, json })
        {
            Assert.DoesNotContain("TEXTO-DEL-AGENTE", export, StringComparison.Ordinal);
            Assert.DoesNotContain("carpeta-secreta", export, StringComparison.Ordinal);
            Assert.DoesNotContain("ev-prompt", export, StringComparison.Ordinal);
            Assert.Contains("s1", export, StringComparison.Ordinal);
        }
    }

    private static MetricValue<long> Long(JsonElement figure)
    {
        var value = figure.GetProperty("value");
        return MetricValue<long>.Of(Enum.Parse<MetricOrigin>(figure.GetProperty("origin").GetString()!), value.ValueKind == JsonValueKind.Null ? null : value.GetInt64(), figure.GetProperty("source").GetString()!);
    }

    private static MetricValue<decimal> Decimal(JsonElement figure)
    {
        var value = figure.GetProperty("value");
        return MetricValue<decimal>.Of(Enum.Parse<MetricOrigin>(figure.GetProperty("origin").GetString()!), value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal(), figure.GetProperty("source").GetString()!);
    }

    private static void AssertFigures(UsageFigures expected, JsonElement figures)
    {
        Assert.Equal(expected.InputTokens, Long(figures.GetProperty("inputTokens")));
        Assert.Equal(expected.CacheReadInputTokens, Long(figures.GetProperty("cacheReadInputTokens")));
        Assert.Equal(expected.CacheWriteInputTokens, Long(figures.GetProperty("cacheWriteInputTokens")));
        Assert.Equal(expected.OutputTokens, Long(figures.GetProperty("outputTokens")));
        Assert.Equal(expected.ReasoningOutputTokens, Long(figures.GetProperty("reasoningOutputTokens")));
        Assert.Equal(expected.CostUsd, Decimal(figures.GetProperty("costUsd")));
    }

    private static void AssertAggregate(MetricAggregate<long> expected, JsonElement aggregate)
    {
        var value = aggregate.GetProperty("value");
        Assert.Equal(expected.Sum, value.ValueKind == JsonValueKind.Null ? null : value.GetInt64());
        Assert.Equal(expected.Origin.ToString(), aggregate.GetProperty("origin").GetString());
        Assert.Equal((expected.Reported, expected.Missing), (aggregate.GetProperty("reported").GetInt32(), aggregate.GetProperty("missing").GetInt32()));
        Assert.Equal(expected.Sources, aggregate.GetProperty("sources").EnumerateArray().Select(s => s.GetString()!));
    }
}
