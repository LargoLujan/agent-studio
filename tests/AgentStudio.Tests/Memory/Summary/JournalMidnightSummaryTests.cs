using System.IO;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Summary;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Summary;

/// <summary>
/// A result written after midnight goes to the file of the day its agent ended, whose <c>P-NNNN</c> are other entries. Since
/// the addendum of 2026-11-03 to ADR-0047 it names its entry with its day, so the daily summary counts it as a result of the
/// day it is written in that answers none of that day's prompts.
/// </summary>
public sealed class JournalMidnightSummaryTests
{
    private static readonly DateOnly Day = new(2026, 3, 11);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<DailySummaryResult> SummarizeAsync(string journal)
    {
        using var world = await MemoryWorld.CreateAsync();
        world.Clock.SetUtcNow(new DateTimeOffset(2026, 3, 12, 8, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(world.DataDirectory, "bitacora", "2026-03-11.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, journal, new UTF8Encoding(false), Ct);
        return await new DailySummaryService(new ProjectStorageFiles(), world.Memory, world.Redactor, world.Clock).SummarizeAsync(world.Project, Day, Ct);
    }

    [Fact]
    public async Task A_result_of_an_entry_of_the_day_before_answers_no_prompt_of_this_day()
    {
        const string Line = "- **09:00:00** · P-0001 · Backend · codex-cli/gpt-5 · #prompt #tests · «Otra tarea» (tarea T-2 · entrada 2026-03-11/P-0001)\n";
        const string Late = "  - Resultado P-0001 · 10:00:00 · Failed · 3,0 s · tokens no disponibles · boom · ejecución `20260310-a1b2c3` · tarea `T-1` · intento 1 · entrada `2026-03-10/P-0001`\n";
        const string Own = "  - Resultado P-0001 · 10:05:00 · Succeeded · 3,0 s · tokens no disponibles · ejecución `20260311-d4e5f6` · tarea `T-2` · intento 1 · entrada `2026-03-11/P-0001`\n";

        var late = await SummarizeAsync("# Bitácora · 2026-03-11\n\n" + Line + Late);
        var both = await SummarizeAsync("# Bitácora · 2026-03-11\n\n" + Line + Late + Own);

        // The late result is a result of the day, of no prompt of it: the day's own P-0001 still has none, and its failure
        // is no failure of Backend.
        Assert.Contains("- Sin resultado en la bitácora: 1", late.Summary, StringComparison.Ordinal);
        Assert.Contains("- Failed: 1", late.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed · Backend", late.Summary, StringComparison.Ordinal);

        // Its own result, which names it, answers it as before.
        Assert.DoesNotContain("Sin resultado en la bitácora", both.Summary, StringComparison.Ordinal);
        Assert.Equal(0, both.UnrecognizedLines);
    }
}
