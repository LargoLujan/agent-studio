using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Memory;

/// <summary>
/// docs/17 D-02: the journal writes, at the very end of the result line, the <c>AgentRunStarted</c> the launch was announced
/// with, as the decorator received it in the request, so the derivation can match the entry with the anchored start. The
/// free note before it can never fake it, and a launch nobody announced keeps the line of before.
/// </summary>
public sealed class JournalAnchorWriterTests : IDisposable
{
    private static readonly TimeSpan Plus2 = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset At = new(2026, 3, 10, 9, 0, 0, Plus2);
    private static readonly AgentRunAnchor Anchor = new("20261015-a1b2c3", "T-12", 2);

    // A project folder exists before anything journals in it, as an open project's does.
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentstudio-anchor-" + Guid.NewGuid().ToString("N"))).FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string[] ResultLines() =>
        [.. File.ReadAllText(Path.Combine(_root, ".agentstudio", "bitacora", "2026-03-10.md")).ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("  - Resultado ", StringComparison.Ordinal))];

    private MarkdownDailyJournal Journal() => new(_root, new ProjectStorageFiles(), new FakeClock(At));

    private static PromptOutcome Outcome(string? note = null) =>
        new(At.AddSeconds(30), AgentRunStatus.Failed, TimeSpan.FromSeconds(30), MetricValue<long>.Exact(1200, "u"), MetricValue<long>.Exact(340, "u"), note);

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
    public async Task The_result_line_ends_with_the_start_it_was_announced_with()
    {
        var journal = Journal();
        var id = await journal.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "Implementar T-12", [], "T-12"), Ct);

        await journal.RecordOutcomeAsync(id, Outcome("las pruebas fallan") with { Anchor = Anchor }, Ct);

        // Since the addendum of 2026-11-03 the anchor also names the entry with its day.
        var line = Assert.Single(ResultLines());
        Assert.Equal("  - Resultado P-0001 · 09:00:30 · Failed · 30,0 s · tokens 1.200 → 340 (exactos) · las pruebas fallan · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 2 · entrada `2026-03-10/P-0001`", line);
        Assert.True(JournalResultAnchor.TryParse(line, out var read, out var entry));
        Assert.Equal(Anchor, read);
        Assert.Equal(new JournalEntryId(new DateOnly(2026, 3, 10), 1), entry);
    }

    [Fact]
    public async Task A_note_that_imitates_an_anchor_never_becomes_one()
    {
        var journal = Journal();
        var first = await journal.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "uno", []), Ct);
        var second = await journal.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "dos", []), Ct);
        const string forged = "boom · ejecución `20261015-ffffff` · tarea `T-99` · intento 1";

        await journal.RecordOutcomeAsync(first, Outcome(forged), Ct);
        await journal.RecordOutcomeAsync(second, Outcome(forged) with { Anchor = Anchor }, Ct);

        var lines = ResultLines();
        Assert.False(JournalResultAnchor.TryParse(lines[0], out _));
        Assert.Contains("ejecución '20261015-ffffff'", lines[0], StringComparison.Ordinal);
        Assert.True(JournalResultAnchor.TryParse(lines[1], out var read));
        Assert.Equal(Anchor, read);
    }

    [Fact]
    public async Task Without_an_anchor_the_result_line_is_the_one_of_before()
    {
        var journal = Journal();
        var id = await journal.RecordPromptAsync(new PromptJournalEntry(At, "Ejecutor", ProviderId.ClaudeCode, "sonnet", "uno", []), Ct);

        await journal.RecordOutcomeAsync(id, Outcome(), Ct);

        Assert.Equal("  - Resultado P-0001 · 09:00:30 · Failed · 30,0 s · tokens 1.200 → 340 (exactos)", Assert.Single(ResultLines()));
    }

    [Fact]
    public async Task The_decorator_writes_the_anchor_of_the_request_when_the_run_ends_and_when_the_launch_fails()
    {
        var provider = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, (_, _) => new ScriptedRun(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, "s1", null)));
        var journaled = new JournalingAgentProvider(provider, Journal(), new FakeClock(At), TimeSpan.FromSeconds(1));

        await using (var run = await journaled.StartAsync(Request() with { Anchor = Anchor }, Ct))
        {
            await run.Completion;
        }

        provider.FailToStart = new InvalidOperationException("no arranca");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journaled.StartAsync(Request() with { Anchor = new AgentRunAnchor("20261015-a1b2c3", "T-12", 3) }, Ct));
        await using (var unannounced = await journaled.StartAsync(Request(), Ct))
        {
            await unannounced.Completion;
        }

        var lines = ResultLines();
        Assert.Equal(3, lines.Length);
        Assert.True(JournalResultAnchor.TryParse(lines[0], out var ended));
        Assert.Equal(Anchor, ended);
        Assert.True(JournalResultAnchor.TryParse(lines[1], out var failed));
        Assert.Equal(new AgentRunAnchor("20261015-a1b2c3", "T-12", 3), failed);
        Assert.Contains("no arranca", lines[1], StringComparison.Ordinal);
        Assert.False(JournalResultAnchor.TryParse(lines[2], out _));
        Assert.Equal(Anchor, provider.Requests[0].Anchor);
    }

    [Fact]
    public void An_anchor_is_part_of_the_outcome_and_not_of_its_note()
    {
        var outcome = Outcome("nota") with { Anchor = Anchor };

        Assert.Equal("nota", outcome.Note);
        Assert.Equal(Anchor, outcome.Anchor);
        Assert.Null(Outcome("nota").Anchor);
    }
}
