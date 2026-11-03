using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;

namespace AgentStudio.Tests.Memory;

public sealed class MarkdownDailyJournalTests : IDisposable
{
    private static readonly TimeSpan Plus2 = TimeSpan.FromHours(2);

    // A project folder exists before anything journals in it, as an open project's does.
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentstudio-journal-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DayFile(string day) => Path.Combine(_root, ".agentstudio", "bitacora", day + ".md");

    private string[] Lines(string day) => File.ReadAllText(DayFile(day)).ReplaceLineEndings("\n").Split('\n');

    private MarkdownDailyJournal NewJournal(DateTimeOffset? now = null) =>
        new(_root, new ProjectStorageFiles(), new FakeClock(now ?? new DateTimeOffset(2026, 3, 10, 9, 0, 0, Plus2)));

    private static PromptJournalEntry Entry(DateTimeOffset at, string summary = "Resumen", IReadOnlyList<string>? tags = null) =>
        new(at, "Orquestador", ProviderId.ClaudeCode, "opus", summary, tags ?? ["Fase 2", "tests"], "T-12");

    private static DateTimeOffset At(int h, int m, int s) => new(2026, 3, 10, h, m, s, Plus2);

    /// <summary>
    /// The title of the hook, and under it what the app writes (V31): its requests and their results, not the «Hecho» lines
    /// Claude adds in the repository of Agent Studio, which mean nothing in the project of a user.
    /// </summary>
    [Fact]
    public async Task First_record_creates_the_file_with_the_title_and_says_what_the_app_writes()
    {
        var journal = NewJournal();

        await journal.RecordPromptAsync(Entry(At(9, 5, 7)), TestContext.Current.CancellationToken);

        var lines = Lines("2026-03-10");
        Assert.Equal("# Bitácora · 2026-03-10", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("Registro automático de Agent Studio: una línea por cada petición a un agente (hora · número · quién · proveedor/modelo · etiquetas · resumen).", lines[2]);
        Assert.Equal("Cuando el agente termina, se añade una línea «Resultado» con el estado, la duración y los tokens.", lines[3]);
        Assert.Equal(string.Empty, lines[4]);
        Assert.DoesNotContain(lines, l => l.Contains("Hecho", StringComparison.Ordinal) || l.Contains("hook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entry_line_has_the_exact_format()
    {
        var journal = NewJournal();

        var id = await journal.RecordPromptAsync(Entry(At(9, 5, 7)), TestContext.Current.CancellationToken);

        Assert.Equal("P-0001", id.ToString());
        Assert.Equal(new DateOnly(2026, 3, 10), id.Day);
        Assert.Equal(
            "- **09:05:07** · P-0001 · Orquestador · claude-code/opus · #prompt #fase-2 #tests · «Resumen» (tarea T-12)",
            Lines("2026-03-10")[5]);
    }

    [Fact]
    public async Task Outcome_line_has_the_exact_format_with_exact_tokens()
    {
        var journal = NewJournal();
        var id = await journal.RecordPromptAsync(Entry(At(9, 5, 7)), TestContext.Current.CancellationToken);

        await journal.RecordOutcomeAsync(
            id,
            new PromptOutcome(
                At(9, 5, 19),
                AgentRunStatus.Succeeded,
                TimeSpan.FromSeconds(12.3),
                MetricValue<long>.Exact(1200, "usage"),
                MetricValue<long>.Exact(340, "usage"),
                "todo bien"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "  - Resultado P-0001 · 09:05:19 · Succeeded · 12,3 s · tokens 1.200 → 340 (exactos) · todo bien",
            Lines("2026-03-10")[6]);
    }

    [Fact]
    public async Task Outcome_line_marks_unavailable_and_estimated_tokens()
    {
        var journal = NewJournal();
        var ct = TestContext.Current.CancellationToken;
        var id = await journal.RecordPromptAsync(Entry(At(9, 5, 7)), ct);
        var unavailable = MetricValue<long>.Unavailable("no reportado");

        await journal.RecordOutcomeAsync(id, new PromptOutcome(At(9, 5, 8), AgentRunStatus.Failed, TimeSpan.FromSeconds(1), unavailable, unavailable), ct);
        await journal.RecordOutcomeAsync(
            id,
            new PromptOutcome(At(9, 5, 9), AgentRunStatus.Cancelled, TimeSpan.FromSeconds(2), MetricValue<long>.Exact(10, "a"), MetricValue<long>.Estimated(20, "b")),
            ct);

        var lines = Lines("2026-03-10");
        Assert.Equal("  - Resultado P-0001 · 09:05:08 · Failed · 1,0 s · tokens no disponibles", lines[6]);
        Assert.Equal("  - Resultado P-0001 · 09:05:09 · Cancelled · 2,0 s · tokens 10 → 20 (estimados)", lines[7]);
    }

    [Fact]
    public async Task Outcome_after_midnight_goes_to_the_next_day_file_and_cites_the_entry()
    {
        var journal = NewJournal();
        var ct = TestContext.Current.CancellationToken;
        var id = await journal.RecordPromptAsync(Entry(At(23, 59, 59)), ct);

        var unavailable = MetricValue<long>.Unavailable("no reportado");
        await journal.RecordOutcomeAsync(
            id,
            new PromptOutcome(new DateTimeOffset(2026, 3, 11, 0, 0, 1, Plus2), AgentRunStatus.Succeeded, TimeSpan.FromSeconds(2), unavailable, unavailable),
            ct);

        // The lines under the title name the «Resultado» lines; none is in the first day.
        Assert.DoesNotContain(Lines("2026-03-10"), l => l.TrimStart().StartsWith("- Resultado", StringComparison.Ordinal));
        var next = Lines("2026-03-11");
        Assert.Equal("# Bitácora · 2026-03-11", next[0]);
        Assert.Contains(next, l => l.StartsWith("  - Resultado P-0001 · 00:00:01 · Succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sequence_continues_after_a_new_instance_over_the_same_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewJournal();
        await first.RecordPromptAsync(Entry(At(9, 0, 1)), ct);
        await first.RecordPromptAsync(Entry(At(9, 0, 2)), ct);

        var second = NewJournal();
        var id = await second.RecordPromptAsync(Entry(At(9, 0, 3)), ct);

        Assert.Equal(3, id.Sequence);
        Assert.Contains(Lines("2026-03-10"), l => l.Contains("· P-0003 ·", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_and_tags_are_sanitised_to_a_single_line()
    {
        var journal = NewJournal();
        var longTail = new string('x', 300);
        var entry = Entry(At(9, 0, 0), "Línea uno\r\nlínea «dos» con `código` " + longTail, ["#Fase 2", "TESTS", "tests", "ñandú!", "  ", "#"]);

        await journal.RecordPromptAsync(entry, TestContext.Current.CancellationToken);

        var entryLines = Lines("2026-03-10").Where(l => l.StartsWith("- **", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(entryLines);
        Assert.Contains("#prompt #fase-2 #tests #nandu ·", line, StringComparison.Ordinal);
        Assert.Contains("«Línea uno línea ‹dos› con 'código' xxx", line, StringComparison.Ordinal);
        var summary = line[(line.IndexOf('«', StringComparison.Ordinal) + 1)..line.LastIndexOf('»')];
        Assert.True(summary.Length <= 200, $"summary was {summary.Length} chars");
        Assert.DoesNotContain('`', line);
    }

    [Fact]
    public async Task Fifty_concurrent_records_produce_fifty_lines_with_unique_sequences()
    {
        var journal = NewJournal();
        var ct = TestContext.Current.CancellationToken;

        var ids = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => journal.RecordPromptAsync(Entry(At(9, 0, i)), ct), ct)));

        Assert.Equal(50, ids.Select(i => i.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 50), ids.Select(i => i.Sequence).Order());
        var entryLines = Lines("2026-03-10").Where(l => l.StartsWith("- **", StringComparison.Ordinal)).ToArray();
        Assert.Equal(50, entryLines.Length);
        Assert.Equal(1, Lines("2026-03-10").Count(l => l.StartsWith("# Bitácora", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Coexists_with_a_line_written_by_the_hook()
    {
        var dir = Path.GetDirectoryName(DayFile("2026-03-10"))!;
        Directory.CreateDirectory(dir);
        var hookContent =
            "# Bitácora · 2026-03-10\n\nRegistro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.\n"
            + "Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.\n\n"
            + "- **08:00:00** · sesión `abc12345` · #prompt #general · «hola»\n";
        await File.WriteAllTextAsync(DayFile("2026-03-10"), hookContent, TestContext.Current.CancellationToken);

        var id = await NewJournal().RecordPromptAsync(Entry(At(9, 0, 0)), TestContext.Current.CancellationToken);

        Assert.Equal(1, id.Sequence);
        var text = File.ReadAllText(DayFile("2026-03-10")).ReplaceLineEndings("\n");
        Assert.StartsWith(hookContent, text, StringComparison.Ordinal);
        Assert.Equal(1, Lines("2026-03-10").Count(l => l.StartsWith("# Bitácora", StringComparison.Ordinal)));
        Assert.Contains("· P-0001 · Orquestador", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_is_utf8_without_bom()
    {
        await NewJournal().RecordPromptAsync(Entry(At(9, 0, 0)), TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(DayFile("2026-03-10"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task Independent_instances_and_an_external_writer_keep_ids_unique_and_consecutive()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DayFile("2026-03-10");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# Bitácora · 2026-03-10\n\n", ct);

        // Four instances share no semaphore, like four processes would not.
        var journals = Enumerable.Range(0, 4).Select(_ => NewJournal()).ToArray();
        var hook = Task.Run(
            () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            File.AppendAllText(path, "- **08:00:00** · sesión `abc` · #prompt #general · «hook»\n");
                            break;
                        }
                        catch (IOException) when (attempt < 200)
                        {
                            Thread.Sleep(5);
                        }
                    }
                }
            },
            ct);
        var writers = journals.Select(j => Task.Run(
            async () =>
            {
                var ids = new List<JournalEntryId>();
                for (var i = 0; i < 50; i++)
                {
                    ids.Add(await j.RecordPromptAsync(Entry(At(9, 0, i)), ct));
                }

                return ids;
            },
            ct)).ToArray();

        var all = (await Task.WhenAll(writers)).SelectMany(x => x).ToArray();
        await hook;

        Assert.Equal(Enumerable.Range(1, 200), all.Select(i => i.Sequence).Order());
        var text = Lines("2026-03-10");
        Assert.Equal(200, text.Count(l => l.Contains("· P-", StringComparison.Ordinal)));
        Assert.Equal(20, text.Count(l => l.Contains("«hook»", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Entry_is_not_glued_to_a_last_line_without_trailing_newline()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DayFile("2026-03-10");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# Bitácora · 2026-03-10\n\n- **08:00:00** · sesión `abc` · #prompt · «manual»", ct);

        await NewJournal().RecordPromptAsync(Entry(At(9, 0, 0)), ct);

        var lines = Lines("2026-03-10");
        Assert.Contains("- **08:00:00** · sesión `abc` · #prompt · «manual»", lines);
        Assert.Contains(lines, l => l.StartsWith("- **09:00:00** · P-0001 ·", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# H", @"«\# H»")]
    [InlineData("- **01:00:00** · P-9999", @"«- \*\*01:00:00\*\* · P-9999»")]
    [InlineData("#secret [x](y) a_b <b> a|b", @"«\#secret \[x\]\(y\) a\_b \<b\> a\|b»")]
    public async Task Summary_markdown_is_escaped(string summary, string expected)
    {
        await NewJournal().RecordPromptAsync(Entry(At(9, 0, 0), summary), TestContext.Current.CancellationToken);

        var line = Lines("2026-03-10").Single(l => l.StartsWith("- **", StringComparison.Ordinal));
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outcome_note_markdown_is_escaped()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = NewJournal();
        var id = await journal.RecordPromptAsync(Entry(At(9, 0, 0)), ct);
        var unavailable = MetricValue<long>.Unavailable("x");

        await journal.RecordOutcomeAsync(id, new PromptOutcome(At(9, 0, 1), AgentRunStatus.Failed, TimeSpan.FromSeconds(1), unavailable, unavailable, "#x *y*"), ct);

        Assert.EndsWith(@" · \#x \*y\*", Lines("2026-03-10")[6], StringComparison.Ordinal);
    }
}
