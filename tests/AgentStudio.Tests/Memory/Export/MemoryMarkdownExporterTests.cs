using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Export;
using AgentStudio.Memory.Store;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;
using static AgentStudio.Tests.Domain.TestData;
using AgentStudio.Desktop.Documents;

namespace AgentStudio.Tests.Memory.Export;

/// <summary>
/// The Markdown of the memory (docs/14 D-22; ADR-0022, decision 8): export only, written whole through the checked
/// storage, in a fixed order, with only what is trusted and accepted, the secrets redacted, and never while a run or a
/// planning goes on.
/// </summary>
public sealed class MemoryMarkdownExporterTests
{
    /// <summary>Madrid in winter: the files are in local time, so a test in UTC would hide a wrong conversion.</summary>
    private static readonly TimeZoneInfo PlusOne = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");

    private static readonly string[] GoldenFiles =
    [
        "cronologico-2026-01.md",
        "cronologico-2026-02.md",
        "tema-convenciones.md",
        "tema-decisiones.md",
        "tema-errores-conocidos.md",
        "tema-soluciones.md",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryMarkdownExporter Exporter(MemoryWorld world, IProjectStorageFiles? files = null) =>
        new(world.Memory, files ?? new ProjectStorageFiles(), world.Activity, world.Redactor, world.Clock, new ResourceUserTextRenderer());

    private static string MemoryFolder(MemoryWorld world) => Path.Combine(world.DataDirectory, "memory");

    private static string Read(MemoryWorld world, string name) => File.ReadAllText(Path.Combine(MemoryFolder(world), name), Encoding.UTF8);

    private static string Golden(string name, IReadOnlyDictionary<string, string> values)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Export", "Fixtures", name), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        return values.Aggregate(text, (current, value) => current.Replace("{" + value.Key + "}", value.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Two decisions (the second supersedes the first, a month later), a convention with a secret, a proposal nobody
    /// accepted and a retracted solution; the ids and hashes the memory gave them, as the golden files name them.
    /// </summary>
    private static async Task<Dictionary<string, string>> SeedAsync(MemoryWorld world)
    {
        world.Clock.SetLocalTimeZone(PlusOne);
        var memory = world.Memory;
        var e1 = await memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Decision, "Despliegue", "Se despliega a mano.", MemoryOrigin.System, "registro:20260101-aaaaaa#3"), Ct);
        world.Clock.Advance(TimeSpan.FromHours(1));
        var e2 = await memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Convention, "Commits", "Los mensajes van en castellano.\nCon password=hunter2 nunca.", MemoryOrigin.User, "nota:1"), Ct);
        world.Clock.Advance(TimeSpan.FromHours(1));
        await memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.KnownError, "Compilación", "Falla sin restaurar: propuesta-sin-aceptar.", MemoryOrigin.Agent, "registro:20260101-aaaaaa#9"), Ct);
        world.Clock.Advance(TimeSpan.FromHours(1));
        var e4 = await memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Solution, "Caché", "Borrar obj: solucion-retirada.", MemoryOrigin.User, "nota:2"), Ct);
        await memory.ReviewAsync(world.Project, e4.Id, MemoryReview.Retracted, "manuel", null, Ct);
        world.Clock.SetUtcNow(new DateTimeOffset(2026, 2, 10, 9, 30, 0, TimeSpan.Zero));
        var e5 = await memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Decision, "Despliegue", "Se despliega con blue green; los `tests` van antes y <nunca> en # títulos.", MemoryOrigin.System, "registro:20260210-bbbbbb#5", e1.Id), Ct);
        return new Dictionary<string, string>
        {
            ["e1"] = e1.Id,
            ["e1.sha"] = e1.ContentSha256,
            ["e2"] = e2.Id,
            ["e2.sha"] = e2.ContentSha256,
            ["e5"] = e5.Id,
            ["e5.sha"] = e5.ContentSha256,
        };
    }

    [Fact]
    public async Task The_trusted_memory_is_written_by_month_and_by_category_exactly_as_the_golden_files_say()
    {
        using var world = await MemoryWorld.CreateAsync();
        var values = await SeedAsync(world);

        var result = await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.True(result.Exported);
        Assert.Null(result.Reason);
        Assert.Equal(3, result.Entries);
        Assert.Equal(GoldenFiles.Select(f => "memory/" + f), result.Files);
        Assert.Equal(GoldenFiles, Directory.GetFiles(MemoryFolder(world)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var file in GoldenFiles)
        {
            Assert.Equal(Golden(file, values), Read(world, file));
        }
    }

    [Fact]
    public async Task Exporting_again_without_changes_writes_the_same_bytes()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);
        var first = GoldenFiles.Select(f => File.ReadAllBytes(Path.Combine(MemoryFolder(world), f))).ToList();

        await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.Equal(first, GoldenFiles.Select(f => File.ReadAllBytes(Path.Combine(MemoryFolder(world), f))));
        Assert.Empty(Directory.GetFiles(Path.Combine(world.DataDirectory, "tmp")));
    }

    [Fact]
    public async Task What_is_retracted_after_an_export_leaves_the_files_at_the_next_one()
    {
        using var world = await MemoryWorld.CreateAsync();
        var entry = await world.Memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Convention, "Commits", "Los mensajes van en castellano.", MemoryOrigin.User, "nota:1"), Ct);
        await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);
        Assert.Contains("castellano", Read(world, "tema-convenciones.md"), StringComparison.Ordinal);

        await world.Memory.ReviewAsync(world.Project, entry.Id, MemoryReview.Retracted, "manuel", null, Ct);
        var result = await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.Equal(0, result.Entries);
        Assert.DoesNotContain("castellano", Read(world, "tema-convenciones.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("castellano", Read(world, $"cronologico-{T0:yyyy-MM}.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_that_ends_in_a_line_break_is_quoted_without_an_empty_last_line()
    {
        using var world = await MemoryWorld.CreateAsync();
        await world.Memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.DailySummary, "Resumen del 2026-01-01", "# Resumen\n\n- Prompts: 2\n", MemoryOrigin.System, "bitacora/2026-01-01.md#sha256:x"), Ct);

        await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.Contains("\n> \\# Resumen\n>\n> - Prompts: 2\n\n- Entrada: ", Read(world, "tema-resumenes-diarios.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_that_reached_a_row_is_redacted_in_the_export()
    {
        using var world = await MemoryWorld.CreateAsync();

        // A row recorded before the redactor knew this kind of secret: the export redacts again.
        var unredacted = new ProjectMemory(world.Instance.Recorder, world.Store, world.Activity, new NoRedaction(), world.Clock);
        await unredacted.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Convention, "Acceso", "Clona con token=ghp_abcdefghijklmnopqrstuvwxyz0123456789 y listo.", MemoryOrigin.User, "nota:1"), Ct);

        await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        var exported = Read(world, "tema-convenciones.md") + Read(world, $"cronologico-{T0:yyyy-MM}.md");
        Assert.DoesNotContain("ghp_", exported, StringComparison.Ordinal);
        Assert.Contains("REDACTED", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_a_run_or_a_planning_goes_on_the_export_is_refused_and_writes_nothing()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        world.Activity.Busy = true;

        var result = await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.False(result.Exported);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(MemoryFolder(world)));
    }

    [Fact]
    public async Task A_run_that_starts_during_the_export_stops_it_before_the_next_file()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var files = new BusyAfterFirstWrite(new ProjectStorageFiles(), world.Activity);

        var result = await Exporter(world, files).ExportAsync(world.Project, "es-ES", Ct);

        Assert.False(result.Exported);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(["memory/cronologico-2026-01.md"], result.Files);
        Assert.Equal(["cronologico-2026-01.md"], Directory.GetFiles(MemoryFolder(world)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_junction_planted_as_the_memory_folder_is_refused_and_nothing_is_written_outside()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var outside = Path.Combine(Path.GetDirectoryName(world.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(outside);
        using var links = new PlantedLinks();
        links.Junction(MemoryFolder(world), outside);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => Exporter(world).ExportAsync(world.Project, "es-ES", Ct));

        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task On_a_log_that_does_not_verify_nothing_is_exported()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        foreach (var anchor in Directory.GetFiles(world.AnchorFolder))
        {
            await File.WriteAllTextAsync(anchor, "{}", Ct);
        }

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Exporter(world).ExportAsync(world.Project, "es-ES", Ct));

        Assert.False(Directory.Exists(MemoryFolder(world)));
    }

    [Fact]
    public async Task A_project_without_memory_exports_no_file()
    {
        using var world = await MemoryWorld.CreateAsync();

        var result = await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);

        Assert.True(result.Exported);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.Entries);
        Assert.False(Directory.Exists(MemoryFolder(world)));
    }

    /// <summary>
    /// The frame of the files is in the language the person chose, given as a parameter: titles, introductions, the names of the
    /// fields, the categories, the origins and the month. What each entry holds, and its hash, are as they were anchored.
    /// </summary>
    [Fact]
    public async Task The_frame_of_the_files_is_in_the_language_asked_and_what_the_entries_hold_is_as_anchored()
    {
        using var world = await MemoryWorld.CreateAsync();
        var values = await SeedAsync(world);

        var result = await Exporter(world).ExportAsync(world.Project, "en", Ct);

        Assert.True(result.Exported);
        Assert.Equal(GoldenFiles.Select(f => "memory/" + f), result.Files);
        var january = Read(world, "cronologico-2026-01.md");
        Assert.StartsWith("<!-- Generated by Agent Studio from the memory of the project.", january, StringComparison.Ordinal);
        Assert.Contains("# Project memory · January 2026", january, StringComparison.Ordinal);
        Assert.Contains("## 2026-01-01 11:00 · Decision · Despliegue", january, StringComparison.Ordinal);
        Assert.Contains("> Se despliega a mano.", january, StringComparison.Ordinal);
        Assert.Contains($"- Entry: {values["e1"]} (from the system)", january, StringComparison.Ordinal);
        Assert.Contains($"- Source: registro:20260101-aaaaaa\\#3", january, StringComparison.Ordinal);
        Assert.Contains($"- SHA-256: {values["e1.sha"]}", january, StringComparison.Ordinal);
        Assert.Contains("- Replaced by: ", january, StringComparison.Ordinal);
        Assert.Contains("# Project memory · Decisions", Read(world, "tema-decisiones.md"), StringComparison.Ordinal);
        Assert.Contains("# Project memory · Known errors", Read(world, "tema-errores-conocidos.md"), StringComparison.Ordinal);
        Assert.Contains("_There is no current trusted memory in this category._", Read(world, "tema-errores-conocidos.md"), StringComparison.Ordinal);
        foreach (var file in GoldenFiles)
        {
            Assert.DoesNotContain("Memoria del proyecto", Read(world, file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The month of a chronological file came from a list of Spanish names and now comes from the culture of the language: the twelve
    /// months say what the list said, and in English they are the English ones.
    /// </summary>
    [Fact]
    public void The_twelve_months_of_the_title_say_what_the_old_list_of_names_said()
    {
        string[] names = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
        var renderer = new ResourceUserTextRenderer();
        for (var month = 1; month <= 12; month++)
        {
            var first = new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero);
            var piece = new AgentStudio.Application.Localization.UserText("memory.export.month", [first]);
            Assert.Equal($"{names[month - 1]} de 2026", renderer.Render(piece, "es-ES"));
            Assert.Equal(first.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en")), renderer.Render(piece, "en"));
        }
    }

    /// <summary>The language is a parameter, not the culture of the thread: the same memory and language give the same bytes on any thread.</summary>
    [Fact]
    public async Task The_culture_of_the_thread_does_not_change_the_files()
    {
        using var world = await MemoryWorld.CreateAsync();
        var values = await SeedAsync(world);
        var previous = (Thread.CurrentThread.CurrentCulture, Thread.CurrentThread.CurrentUICulture);
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            await Exporter(world).ExportAsync(world.Project, "es-ES", Ct);
        }
        finally
        {
            (Thread.CurrentThread.CurrentCulture, Thread.CurrentThread.CurrentUICulture) = previous;
        }

        foreach (var file in GoldenFiles)
        {
            Assert.Equal(Golden(file, values), Read(world, file));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a language")]
    [InlineData("C:\\Users\\persona")]
    public async Task A_language_that_is_not_the_name_of_a_culture_is_refused_before_anything_is_written(string language)
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);

        await Assert.ThrowsAsync<ArgumentException>(() => Exporter(world).ExportAsync(world.Project, language, Ct));

        Assert.False(Directory.Exists(MemoryFolder(world)));
    }

    /// <summary>A redactor that keeps everything: what a row looks like when its secret was not known at record time.</summary>
    private sealed class NoRedaction : ISecretRedactor
    {
        public string Redact(string text) => text;
    }

    /// <summary>The checked storage, where a run starts right after the first file is written.</summary>
    private sealed class BusyAfterFirstWrite(IProjectStorageFiles inner, FakeRunActivity activity) : IProjectStorageFiles
    {
        public Stream OpenForAppend(string projectRoot, string relativePath) => inner.OpenForAppend(projectRoot, relativePath);

        public void Replace(string projectRoot, string relativePath, ReadOnlySpan<byte> content)
        {
            inner.Replace(projectRoot, relativePath, content);
            activity.Busy = true;
        }
    }
}
