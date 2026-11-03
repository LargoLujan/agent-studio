using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Summary;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Summary;

/// <summary>
/// The deterministic daily summary (docs/14 D-31; ADR-0025): the journal of the local day, read through the checked
/// storage, understood as its two writers write it, counted and quoted against a hand-written expected summary. A closed
/// day goes to the memory once per content; today's is provisional and goes nowhere.
/// </summary>
public sealed class DailySummaryTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);

    /// <summary>A local zone two hours ahead, so the local day is not the UTC one around midnight.</summary>
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    /// <summary>10:00 local on the day after: the sample day is closed.</summary>
    private static readonly DateTimeOffset DayAfter = new(2026, 3, 11, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SampleJournal => Path.Combine(AppContext.BaseDirectory, "Memory", "Fixtures", "bitacora-ejemplo.md");

    private static string ExpectedSummary =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Summary", "Fixtures", "resumen-ejemplo.md"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<MemoryWorld> WorldAtAsync(DateTimeOffset now)
    {
        var world = await MemoryWorld.CreateAsync();
        world.Clock.SetLocalTimeZone(PlusTwo);
        world.Clock.SetUtcNow(now);
        return world;
    }

    private static DailySummaryService Service(MemoryWorld world) => new(new ProjectStorageFiles(), world.Memory, world.Redactor, world.Clock);

    private static string DayFile(MemoryWorld world, DateOnly day) => Path.Combine(world.DataDirectory, "bitacora", $"{day:yyyy-MM-dd}.md");

    private static void WriteJournal(MemoryWorld world, string text, DateOnly? day = null)
    {
        var path = DayFile(world, day ?? Day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void CopySample(MemoryWorld world)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DayFile(world, Day))!);
        File.Copy(SampleJournal, DayFile(world, Day));
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task The_sample_journal_of_a_closed_day_gives_the_expected_summary_and_it_goes_to_the_memory()
    {
        using var world = await WorldAtAsync(DayAfter);
        CopySample(world);

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.Equal(ExpectedSummary, result.Summary);
        Assert.Equal((Day, false, 9, 3), (result.Day, result.Provisional, result.Prompts, result.UnrecognizedLines));
        Assert.Equal(Sha256Of(DayFile(world, Day)), result.JournalSha256);
        var recorded = Assert.IsType<MemoryEntry>(result.Recorded);
        Assert.Equal((MemoryKind.DailySummary, MemoryOrigin.System, MemoryTrust.Trusted), (recorded.Kind, recorded.Origin, recorded.Trust));
        Assert.Equal("Resumen del 2026-03-10", recorded.Topic);
        Assert.Equal(result.Summary, recorded.Content);
        Assert.Equal($"bitacora/2026-03-10.md#sha256:{result.JournalSha256}", recorded.Source);
    }

    [Fact]
    public async Task Today_is_provisional_and_leaves_nothing_in_the_memory()
    {
        using var world = await WorldAtAsync(new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero));
        CopySample(world);

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.True(result.Provisional);
        Assert.Null(result.Recorded);
        Assert.StartsWith("# Resumen del 2026-03-10 (provisional)\n", result.Summary, StringComparison.Ordinal);
        Assert.Equal(ExpectedSummary[ExpectedSummary.IndexOf('\n', StringComparison.Ordinal)..], result.Summary[result.Summary.IndexOf('\n', StringComparison.Ordinal)..]);
        Assert.Empty(await world.EntriesAsync(DecisionKind.MemoryRecorded));
    }

    [Theory]
    [InlineData("2026-03-10T21:30:00Z", true)] // 23:30 of the 10th, local
    [InlineData("2026-03-10T22:30:00Z", false)] // 00:30 of the 11th, local: the 10th is closed
    public async Task Which_day_is_today_is_decided_in_local_time(string utcNow, bool provisional)
    {
        using var world = await WorldAtAsync(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture));
        CopySample(world);

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.Equal(provisional, result.Provisional);
        Assert.Equal(provisional, result.Recorded is null);
    }

    [Fact]
    public async Task A_closed_day_is_summarized_once()
    {
        using var world = await WorldAtAsync(DayAfter);
        CopySample(world);
        var first = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        var second = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.NotNull(first.Recorded);
        Assert.Null(second.Recorded);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Single(await world.EntriesAsync(DecisionKind.MemoryRecorded));
    }

    [Fact]
    public async Task A_closed_day_whose_journal_grew_is_summarized_again_and_supersedes_its_earlier_summary()
    {
        using var world = await WorldAtAsync(DayAfter);
        CopySample(world);
        var first = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        // Claude adds its «Hecho» line to an entry of the day before, after midnight.
        await File.AppendAllTextAsync(DayFile(world, Day), "  - Hecho: añadido después de medianoche.\n", Ct);
        var second = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        var recorded = Assert.IsType<MemoryEntry>(second.Recorded);
        Assert.Equal(first.Recorded!.Id, recorded.Supersedes);
        Assert.Contains("## Líneas «Hecho»: 5", second.Summary, StringComparison.Ordinal);
        var summaries = await world.Memory.RecallAsync(world.Project, new MemoryQuery(Kinds: [MemoryKind.DailySummary]), Ct);
        Assert.Equal([recorded.Id], summaries.Select(s => s.Id));
    }

    [Fact]
    public async Task A_day_without_a_journal_has_no_prompts_and_leaves_nothing()
    {
        using var world = await WorldAtAsync(DayAfter);

        var result = await Service(world).SummarizeAsync(world.Project, new DateOnly(2026, 3, 9), Ct);

        Assert.Equal(0, result.Prompts);
        Assert.Null(result.Recorded);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), result.JournalSha256);
        Assert.Contains("No hay bitácora de este día.", result.Summary, StringComparison.Ordinal);
        Assert.Empty(await world.LogAsync());
    }

    [Fact]
    public async Task A_day_that_is_a_second_name_of_a_file_outside_the_project_is_refused_and_nothing_is_summarized()
    {
        using var world = await WorldAtAsync(DayAfter);
        var outside = Path.Combine(Path.GetDirectoryName(world.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "ajeno.md");
        File.Copy(SampleJournal, victim);
        Directory.CreateDirectory(Path.GetDirectoryName(DayFile(world, Day))!);
        using var links = new PlantedLinks();
        links.HardLink(DayFile(world, Day), victim);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => Service(world).SummarizeAsync(world.Project, Day, Ct));

        Assert.Empty(await world.LogAsync());
    }

    [Fact]
    public async Task Tokens_add_up_as_exact_only_when_every_result_gives_exact_figures()
    {
        using var world = await WorldAtAsync(DayAfter);
        WriteJournal(world, string.Join('\n',
            "# Bitácora · 2026-03-10",
            "- **09:00:00** · P-0001 · Orquestador · claude-code/opus · #prompt · «Uno»",
            "  - Resultado P-0001 · 09:00:10 · Succeeded · 10,0 s · tokens 1.000 → 300 (exactos)",
            "- **09:01:00** · P-0002 · Backend · codex-cli/gpt-5 · #prompt · «Dos»",
            "  - Resultado P-0002 · 09:01:20 · Succeeded · 20,5 s · tokens 300 → 40 (exactos)",
            string.Empty));

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.Contains("- Entrada 1.300 → salida 340 (exactos)\n", result.Summary, StringComparison.Ordinal);
        Assert.Contains("- Duración total: 30,5 s\n", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Sin resultado", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hecho_lines_that_do_not_fit_are_counted_and_the_summary_stays_within_an_entry()
    {
        using var world = await WorldAtAsync(DayAfter);
        var lines = new List<string> { "# Bitácora · 2026-03-10", "- **09:00:00** · sesión `abc` · #prompt · «hola»" };
        lines.AddRange(Enumerable.Range(1, 30).Select(i => $"  - Hecho ({i}): " + new string('x', 900)));
        WriteJournal(world, string.Join('\n', lines) + "\n");

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.True(result.Summary.Length <= MemoryContent.MaxCharacters, $"{result.Summary.Length} characters");
        Assert.Contains("## Líneas «Hecho»: 30", result.Summary, StringComparison.Ordinal);
        Assert.Matches(@"\n- … y \d+ líneas «Hecho» más en la bitácora\.\n", result.Summary);
        Assert.Equal(result.Summary, result.Recorded!.Content);
    }

    [Fact]
    public async Task A_secret_in_the_journal_never_reaches_the_summary()
    {
        using var world = await WorldAtAsync(DayAfter);
        WriteJournal(world, "- **09:00:00** · sesión `abc` · #prompt · «hola»\n  - Hecho: clonado con token=ghp_abcdefghijklmnopqrstuvwxyz0123456789 sin problema.\n");

        var result = await Service(world).SummarizeAsync(world.Project, Day, Ct);

        Assert.DoesNotContain("ghp_", result.Summary, StringComparison.Ordinal);
        Assert.Contains(@"\[REDACTED\]", result.Summary, StringComparison.Ordinal);
    }
}
