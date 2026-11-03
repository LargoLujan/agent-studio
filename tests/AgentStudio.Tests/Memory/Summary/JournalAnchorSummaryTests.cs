using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Memory.Summary;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Summary;

/// <summary>
/// docs/17 D-02 (Phase 12, P0): the result lines of the journal now end with the <c>AgentRunStarted</c> of the launch. The
/// daily summary (docs/14 D-31) must read the entries of before and the new ones the same: the same text, the same prompts
/// and the same unrecognised lines.
/// </summary>
public sealed partial class JournalAnchorSummaryTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");
    private static readonly DateTimeOffset DayAfter = new(2026, 3, 11, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SampleJournal => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Fixtures", "bitacora-ejemplo.md"), Encoding.UTF8);

    private static async Task<DailySummaryResult> SummarizeAsync(string journal)
    {
        using var world = await MemoryWorld.CreateAsync();
        world.Clock.SetLocalTimeZone(PlusTwo);
        world.Clock.SetUtcNow(DayAfter);
        var path = Path.Combine(world.DataDirectory, "bitacora", $"{Day:yyyy-MM-dd}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, journal, new UTF8Encoding(false), Ct);
        return await new DailySummaryService(new ProjectStorageFiles(), world.Memory, world.Redactor, world.Clock).SummarizeAsync(world.Project, Day, Ct);
    }

    [Fact]
    public async Task The_sample_journal_with_anchored_result_lines_gives_the_same_summary()
    {
        var anchors = new Queue<AgentRunAnchor>(
        [
            new AgentRunAnchor("20260310-0a1b2c", null, 1),
            new AgentRunAnchor("20260310-0a1b2c", "T-12", 1),
            new AgentRunAnchor("aux-20260310-0d0e0f", null, 1),
            new AgentRunAnchor("deb-20260310-a1b2c3", "r1-mi agente", 2),
            new AgentRunAnchor("20260310-0a1b2c", "tarea_2", 12),
        ]);
        var before = SampleJournal;
        var after = ResultLine().Replace(before, match => match.Value + " · " + JournalResultAnchor.Format(anchors.Dequeue()));

        Assert.Empty(anchors);
        Assert.NotEqual(before, after);
        Assert.All(after.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("  - Resultado ", StringComparison.Ordinal)), line => Assert.True(JournalResultAnchor.TryParse(line, out _), line));

        var old = await SummarizeAsync(before);
        var anchored = await SummarizeAsync(after);

        Assert.Equal(old.Summary, anchored.Summary);
        Assert.Equal((old.Prompts, old.UnrecognizedLines), (anchored.Prompts, anchored.UnrecognizedLines));
        Assert.Equal((9, 3), (anchored.Prompts, anchored.UnrecognizedLines));
    }

    [Fact]
    public async Task A_day_the_writer_journaled_with_anchors_reads_like_the_same_day_without_them()
    {
        var plain = await WriteDayAsync(anchored: false);
        var anchored = await WriteDayAsync(anchored: true);

        Assert.NotEqual(plain, anchored);
        Assert.Contains("· ejecución `20260310-0a1b2c` · tarea `T-12` · intento 2", anchored, StringComparison.Ordinal);

        var old = await SummarizeAsync(plain);
        var now = await SummarizeAsync(anchored);

        Assert.Equal(old.Summary, now.Summary);
        Assert.Equal(3, now.Prompts);
        Assert.Equal((old.Prompts, old.UnrecognizedLines), (now.Prompts, now.UnrecognizedLines));
        Assert.Contains("- Failed: 1", now.Summary, StringComparison.Ordinal);
        Assert.Contains("- Entrada 3.600 → salida 1.020 (exactos)", now.Summary, StringComparison.Ordinal);
    }

    /// <summary>Three prompts and their results, as the journal of the product writes them, with or without their anchors.</summary>
    private static async Task<string> WriteDayAsync(bool anchored)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentstudio-anchor-day-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var at = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(2));
            var journal = new MarkdownDailyJournal(root, new ProjectStorageFiles(), new FakeClock(at));
            (string Task, int Attempt, AgentRunStatus Status, string? Note)[] runs =
            [
                ("T-12", 1, AgentRunStatus.Succeeded, null),
                ("T-12", 2, AgentRunStatus.Failed, "las pruebas fallan"),
                ("T-13", 1, AgentRunStatus.Succeeded, "(acumulado de la sesión)"),
            ];
            foreach (var (task, attempt, status, note) in runs)
            {
                var id = await journal.RecordPromptAsync(new PromptJournalEntry(at, "Ejecutor", ProviderId.ClaudeCode, "sonnet", $"Implementar {task}", ["#tests"], task), Ct);
                var outcome = new PromptOutcome(at.AddSeconds(42), status, TimeSpan.FromSeconds(42), MetricValue<long>.Exact(1200, "u"), MetricValue<long>.Exact(340, "u"), note)
                {
                    Anchor = anchored ? new AgentRunAnchor("20260310-0a1b2c", task, attempt) : null,
                };
                await journal.RecordOutcomeAsync(id, outcome, Ct);
            }

            return await File.ReadAllTextAsync(Path.Combine(root, ".agentstudio", "bitacora", $"{Day:yyyy-MM-dd}.md"), Ct);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [GeneratedRegex(@"(?m)^  - Resultado [^\r\n]*?(?=\r?$)")]
    private static partial Regex ResultLine();
}
