using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// The receipt of the journal (addendum of 2026-11-03 to ADR-0047): the journal gives the SHA-256 of the exact text of an
/// anchored entry, its line and its result line; the decorator carries it to the end of the run, so its
/// <c>AgentRunFinished</c> keeps it; and the forms the derivation reads round-trip: the entry with its day in the anchor and
/// the source <c>bitacora:</c>.
/// </summary>
public sealed class JournalReceiptTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly AgentRunAnchor Anchor = new("20260310-a1b2c3", "T-12", 2);

    // A project folder exists before anything journals in it, as an open project's does.
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentstudio-recibo-" + Guid.NewGuid().ToString("N"))).FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string[] Lines() =>
        File.ReadAllText(Path.Combine(_root, ".agentstudio", "bitacora", "2026-03-10.md")).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private static AgentRunRequest Request() =>
        new(
            new AgentDefinition(
                new AgentId("ejecutor"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"),
                "sys",
                [],
                [],
                [],
                new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
                3,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            "Haz T-12",
            "C:/work",
            new PermissionProfile(false, false, false, [], [], []),
            Purpose: "Implementar T-12",
            TaskId: "T-12");

    [Fact]
    public async Task The_journal_gives_the_hash_of_the_two_lines_it_wrote_and_the_decorator_carries_it_to_the_end_of_the_run()
    {
        var provider = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, (_, _) => new ScriptedRun(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, "s1", null)));
        var journal = new MarkdownDailyJournal(_root, new ProjectStorageFiles(), new FakeClock(At));
        var journaled = new JournalingAgentProvider(provider, journal, new FakeClock(At), TimeSpan.FromSeconds(1));

        AgentRunOutcome anchored;
        await using (var run = await journaled.StartAsync(Request() with { Anchor = Anchor }, Ct))
        {
            anchored = await run.Completion;
        }

        AgentRunOutcome unannounced;
        await using (var run = await journaled.StartAsync(Request(), Ct))
        {
            unannounced = await run.Completion;
        }

        var lines = Lines();
        var line = Assert.Single(lines, l => l.StartsWith("- **09:00:00** · P-0001", StringComparison.Ordinal));
        var result = Assert.Single(lines, l => l.StartsWith("  - Resultado P-0001", StringComparison.Ordinal));
        Assert.Equal(new JournalReceipt(new DateOnly(2026, 3, 10), 1, ContentHash.Sha256Hex(line + "\n" + result)), anchored.Journal);
        Assert.Equal(new JournalEntryId(new DateOnly(2026, 3, 10), 1), anchored.Journal!.EntryId());
        Assert.EndsWith("(tarea T-12 · entrada 2026-03-10/P-0001)", line, StringComparison.Ordinal);

        // A launch nobody announced keeps its lines of before, and has no receipt.
        Assert.Null(unannounced.Journal);
        Assert.EndsWith("«Implementar T-12» (tarea T-12)", Assert.Single(lines, l => l.StartsWith("- **09:00:00** · P-0002", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.False(JournalResultAnchor.TryParse(Assert.Single(lines, l => l.StartsWith("  - Resultado P-0002", StringComparison.Ordinal)), out _));
    }

    [Fact]
    public async Task A_journal_reopened_since_the_line_was_written_gives_no_receipt()
    {
        var first = new MarkdownDailyJournal(_root, new ProjectStorageFiles(), new FakeClock(At));
        var id = await first.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "uno", [], "T-12") { Anchor = Anchor }, Ct);
        var outcome = new PromptOutcome(At.AddSeconds(5), AgentRunStatus.Succeeded, TimeSpan.FromSeconds(5), MetricValue<long>.Exact(1, "u"), MetricValue<long>.Exact(2, "u")) { Anchor = Anchor };

        var reopened = new MarkdownDailyJournal(_root, new ProjectStorageFiles(), new FakeClock(At));

        Assert.Null(await reopened.RecordReceiptedOutcomeAsync(id, outcome, Ct));
        Assert.True(JournalResultAnchor.TryParse(Lines()[^1], out var anchor, out var entry));
        Assert.Equal((Anchor, (JournalEntryId?)id), (anchor, entry));
    }

    [Fact]
    public async Task A_result_that_could_not_be_written_lets_go_of_the_line_of_its_entry()
    {
        var journal = new MarkdownDailyJournal(_root, new ProjectStorageFiles(), new FakeClock(At));
        var id = await journal.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "uno", [], "T-12") { Anchor = Anchor }, Ct);
        var outcome = new PromptOutcome(At.AddDays(1), AgentRunStatus.Succeeded, TimeSpan.FromDays(1), MetricValue<long>.Exact(1, "u"), MetricValue<long>.Exact(2, "u")) { Anchor = Anchor };

        // The day the agent ends is a folder, not a file: its result cannot be written.
        var nextDay = Path.Combine(_root, ".agentstudio", "bitacora", "2026-03-11.md");
        Directory.CreateDirectory(nextDay);
        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => journal.RecordReceiptedOutcomeAsync(id, outcome, Ct));

        // Written later, it has no receipt any more: the journal no longer keeps the line it would vouch for.
        Directory.Delete(nextDay);
        Assert.Null(await journal.RecordReceiptedOutcomeAsync(id, outcome, Ct));
        Assert.True(JournalResultAnchor.TryParse(File.ReadAllText(nextDay).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')[^1], out _));
    }

    [Fact]
    public void The_end_of_a_launch_keeps_the_receipt_only_when_there_is_one()
    {
        var receipt = new JournalReceipt(new DateOnly(2026, 3, 10), 7, ContentHash.Sha256Hex("texto"));
        var without = new AgentRunFinishedPayload(Tid("T1"), 1, AttemptOutcome.Succeeded, 0, "s", null, null, null) { RunId = "r" };
        var with = without with { Journal = receipt };

        var plain = DecisionPayloadSerializer.Serialize(without);
        var kept = DecisionPayloadSerializer.Serialize(with);

        Assert.DoesNotContain("journal", plain, StringComparison.Ordinal);
        Assert.Contains($"\"journal\":{{\"day\":\"2026-03-10\",\"entry\":7,\"sha256\":\"{receipt.Sha256}\"}}", kept, StringComparison.Ordinal);
        Assert.Equal(receipt, DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(kept).Journal);
        Assert.Null(DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(plain).Journal);
    }

    [Theory]
    [InlineData("20260310-a1b2c3", "T-12", 1, 2026, 3, 10, 7)]
    [InlineData("aux-20260310-0a1b2c", null, 1, 2026, 1, 1, 1)]
    [InlineData("deb-20260310-a1b2c3", "juez-juez revision", 2, 2025, 12, 31, 12345)]
    public void The_anchor_names_its_entry_with_its_day_and_both_round_trip(string run, string? task, int attempt, int year, int month, int day, int sequence)
    {
        var anchor = new AgentRunAnchor(run, task, attempt);
        var entry = new JournalEntryId(new DateOnly(year, month, day), sequence);
        var line = "  - Resultado P-0001 · 00:00:12 · Failed · 3,0 s · tokens no disponibles · boom · " + JournalResultAnchor.Format(anchor, entry);

        Assert.True(JournalResultAnchor.TryParse(line, out var read, out var named));
        Assert.Equal((anchor, (JournalEntryId?)entry), (read, named));
        Assert.True(JournalEntryId.TryParseQualified(entry.Qualified(), out var back));
        Assert.Equal(entry, back);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}/P-\d{4,}$", entry.Qualified());
    }

    [Theory]
    [InlineData("2026-03-10/P-0000")]
    [InlineData("2026-13-10/P-0001")]
    [InlineData("2026-03-10P-0001")]
    [InlineData("2026-03-10/Q-0001")]
    [InlineData("2026-03-10/P-00a1")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_a_day_and_a_positive_number_name_an_entry(string? text)
    {
        Assert.False(JournalEntryId.TryParseQualified(text, out _));
    }

    [Fact]
    public void The_source_of_a_journal_entry_reads_back_its_day_and_its_hash()
    {
        var hash = ContentHash.Sha256Hex("- **11:42:07** · P-0007 · Backend");
        foreach (var day in new[] { new DateOnly(2026, 10, 30), new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31) })
        {
            Assert.True(MemorySources.TryParseJournal(MemorySources.Journal(day, hash), out var read, out var sha));
            Assert.Equal((day, hash), (read, sha));
        }

        string?[] others =
        [
            null,
            string.Empty,
            "bitacora/2026-10-30.md#sha256:" + hash,
            "bitacora:2026-10-30#sha256:" + hash.ToUpperInvariant(),
            "bitacora:2026-10-30#sha256:" + hash[..63],
            "bitacora:2026-10-30#sha256:" + hash + "0",
            "bitacora:2026-02-30#sha256:" + hash,
            "bitacora:2026-10-30#sha1:" + hash,
            MemorySources.LogEntry("20261015-a1b2c3", 42),
        ];
        Assert.All(others, source => Assert.False(MemorySources.TryParseJournal(source, out _, out _)));
    }
}
