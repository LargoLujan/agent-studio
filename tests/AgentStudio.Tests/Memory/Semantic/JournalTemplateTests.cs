using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Memory.Semantic;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The fixed words of the template of the journal (ADR-0047, addenda of 2026-11-04): one list for the clean view and for the
/// lexical list of a search, and the writer of the product keeps to it.
/// </summary>
public sealed class JournalTemplateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ejecución", true)]
    [InlineData("Ejecución,", true)]
    [InlineData("#prompt", true)]
    [InlineData("tarea", true)]
    [InlineData("Resultado", true)]
    [InlineData("intento?", true)]
    [InlineData("codex-cli", true)]
    [InlineData("RateLimited", false)]
    [InlineData("Failed", false)]
    [InlineData("Bitácora", true)]
    [InlineData("tareas", false)]
    [InlineData("intentos", false)]
    [InlineData("Claude", false)]
    [InlineData("codex", false)]
    [InlineData("Backend", false)]
    [InlineData("gpt-5", false)]
    [InlineData("P-0102", false)]
    public void A_term_is_a_word_of_the_template_only_when_it_is_one_whole(string term, bool expected)
    {
        Assert.Equal(expected, JournalTemplate.IsTemplateWord(term));
    }

    [Fact]
    public void The_list_a_search_leaves_out_is_the_keywords_and_the_CLIs_folded_and_not_the_states()
    {
        Assert.Equal(JournalTemplate.FoldAll([.. JournalTemplate.Keywords, .. JournalTemplate.Providers]), JournalTemplate.Words);
        Assert.DoesNotContain("failed", JournalTemplate.Words);
        Assert.Equal(Enum.GetNames<AgentRunStatus>(), JournalTemplate.Statuses);
        Assert.Contains("ejecucion", JournalTemplate.Words);
        Assert.Contains("claudecode", JournalTemplate.Words);
        Assert.Equal(string.Empty, JournalTemplate.Fold("\ud800 · ¿?"));
    }

    [Fact]
    public async Task What_the_writer_of_the_product_writes_is_spelled_with_the_words_of_the_list()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentstudio-template-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var at = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
            var journal = new MarkdownDailyJournal(root, new ProjectStorageFiles(), new FakeClock(at));
            var first = await journal.RecordPromptAsync(new PromptJournalEntry(at, "Backend", ProviderId.CodexCli, "gpt-5", "Implementar el almacén", ["#tests"], "T2"), Ct);
            await journal.RecordOutcomeAsync(first, new PromptOutcome(at.AddSeconds(42), AgentRunStatus.Failed, TimeSpan.FromSeconds(42), MetricValue<long>.Missing("n/d"), MetricValue<long>.Missing("n/d"), "las pruebas fallan")
            {
                Anchor = new AgentRunAnchor("20260310-0a1b2c", "T2", 2),
            }, Ct);
            var second = await journal.RecordPromptAsync(new PromptJournalEntry(at, "Revisor", ProviderId.ClaudeCode, "sonnet", "Revisar el almacén", []), Ct);
            await journal.RecordOutcomeAsync(second, new PromptOutcome(at.AddSeconds(50), AgentRunStatus.Succeeded, TimeSpan.FromSeconds(8), MetricValue<long>.Estimated(1200, "u"), MetricValue<long>.Missing("n/d")), Ct);

            var day = await File.ReadAllTextAsync(Path.Combine(root, ".agentstudio", "bitacora", "2026-03-10.md"), Ct);

            Assert.StartsWith("# " + JournalTemplate.Journal + " · 2026-03-10", day, StringComparison.Ordinal);
            Assert.Contains(" · codex-cli/gpt-5 · " + JournalTemplate.PromptTag + " #tests · «Implementar el almacén» (" + JournalTemplate.Task + " T2)", day, StringComparison.Ordinal);
            Assert.Contains("  - " + JournalTemplate.Result + " P-0001 · ", day, StringComparison.Ordinal);
            Assert.Contains(" · Failed · 42,0 s · " + JournalTemplate.Tokens + " no disponibles · las pruebas fallan · " + JournalTemplate.Run + " `20260310-0a1b2c` · " + JournalTemplate.Task + " `T2` · " + JournalTemplate.Attempt + " 2", day, StringComparison.Ordinal);
            Assert.Contains(" · Succeeded · 8,0 s · " + JournalTemplate.Tokens + " 1.200 → " + JournalTemplate.NotAvailable + " (" + JournalTemplate.EstimatedTokens + ")", day, StringComparison.Ordinal);
            Assert.Contains(JournalTemplate.Run + " `20260310-0a1b2c` · " + JournalTemplate.Task + " `T2` · " + JournalTemplate.Attempt + " 2", JournalResultAnchor.Format(new AgentRunAnchor("20260310-0a1b2c", "T2", 2)), StringComparison.Ordinal);

            // The view of the entries the writer wrote keeps their summaries and notes, and none of the words of the list.
            var entries = day.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("- **", StringComparison.Ordinal) || l.StartsWith("  - ", StringComparison.Ordinal));
            var view = JournalSearchText.Of(string.Join('\n', entries));
            Assert.Contains("Implementar el almacén", view, StringComparison.Ordinal);
            Assert.Contains("las pruebas fallan", view, StringComparison.Ordinal);
            Assert.DoesNotContain(JournalTemplate.Run, view, StringComparison.Ordinal);
            Assert.DoesNotContain(JournalTemplate.Tokens, view, StringComparison.Ordinal);
            Assert.DoesNotContain("Failed", view, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
