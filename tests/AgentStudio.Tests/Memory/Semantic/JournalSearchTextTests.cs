using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Semantic;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The clean view of an entry of the journal that the meaning of the index is computed from (ADR-0047, addenda of 2026-11-03
/// and 2026-11-04): what its author asked and what came of it, without the template the two writers of the journal give
/// every entry, and only the template as they write it. The words of the index search the whole anchored text.
/// </summary>
public sealed class JournalSearchTextTests
{
    [Fact]
    public void An_entry_of_the_product_keeps_its_summary_and_the_note_of_its_result_and_nothing_of_its_template()
    {
        const string Entry = "- **09:05:07** · P-0002 · Backend · codex-cli/gpt-5 · #prompt #agente-backend #tests · «Implementar el almacén \\[T-12\\]» (tarea T-12 · entrada `2026-03-10/P-0002`)\n"
            + "  - Resultado P-0002 · 09:09:47 · Failed · 280,4 s · tokens 1.200 → n/d (estimados) · las pruebas fallan · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 2 · entrada `2026-03-10/P-0002`";

        Assert.Equal("Implementar el almacén [T-12]\nlas pruebas fallan", JournalSearchText.Of(Entry));
    }

    [Fact]
    public void An_entry_of_the_hook_keeps_its_request_and_what_its_done_lines_say()
    {
        const string Entry = "- **09:06:00** · sesión `eb1c6494` · #prompt #docs #fase-4 · «Revisa el ADR-0023 y dime si falta algo»\n"
            + "  - Hecho: ADR-0023 revisado; faltaba el ejemplo del fallo cerrado.";

        Assert.Equal("Revisa el ADR-0023 y dime si falta algo\nADR-0023 revisado; faltaba el ejemplo del fallo cerrado.", JournalSearchText.Of(Entry));
    }

    [Fact]
    public void The_notes_under_an_entry_keep_their_text_without_their_labels()
    {
        const string Notes = "  - Hecho (en curso): pruebas del empaquetador escritas.\n"
            + "  - Hecho (00:27, cierre): todo integrado; 4700 pruebas en verde.\n"
            + "  - Nota: esta línea no es de ninguno de los dos escritores.";

        Assert.Equal("pruebas del empaquetador escritas.\ntodo integrado; 4700 pruebas en verde.\nNota: esta línea no es de ninguno de los dos escritores.", JournalSearchText.Of(Notes));
    }

    [Fact]
    public void A_done_line_that_quotes_keeps_its_quotes()
    {
        Assert.Equal("búsqueda por bm25; no lematiza, así que «prueba» no encuentra «pruebas».", JournalSearchText.Of("  - Hecho: búsqueda por bm25; no lematiza, así que «prueba» no encuentra «pruebas»."));
    }

    [Fact]
    public void An_entry_with_nothing_but_its_template_has_its_whole_text_as_its_meaning()
    {
        const string Result = "  - Resultado P-0003 · 09:11:00 · RateLimited · 60,0 s · tokens no disponibles";

        Assert.Equal(Result, JournalSearchText.Of(Result));
    }

    [Fact]
    public void The_view_does_not_depend_on_the_ends_of_line_of_the_file()
    {
        const string Entry = "- **10:30:00** · P-0005 · Orquestador · claude-code/claude-opus-5 · #prompt · «Resumen final»\n  - Hecho: hecho de verdad.";

        Assert.Equal(JournalSearchText.Of(Entry), JournalSearchText.Of(Entry.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Only_the_lines_written_as_the_app_and_the_hook_write_them_lose_their_template()
    {
        const string Lines = "- Resultado final: todo integrado · 4700 pruebas en verde\n"
            + "- **Nota** · el orden de las fases cambia · «antes la 9»\n"
            + "- Resultado P-0004 · pendiente de revisar · Failed\n"
            + "- **9:05** · P-0004 · Backend · «resumen que no es de una entrada»\n"
            + "- Hechos: dos pruebas nuevas";

        Assert.Equal(
            "Resultado final: todo integrado · 4700 pruebas en verde\n"
            + "**Nota** · el orden de las fases cambia · «antes la 9»\n"
            + "Resultado P-0004 · pendiente de revisar · Failed\n"
            + "**9:05** · P-0004 · Backend · «resumen que no es de una entrada»\n"
            + "Hechos: dos pruebas nuevas",
            JournalSearchText.Of(Lines));
    }

    [Fact]
    public void A_journal_entry_keeps_its_whole_text_its_meaning_is_its_view_and_its_fingerprint_follows_the_anchored_entry()
    {
        const string Topic = "Bitácora del 2026-10-15 · P-0102";
        const string Content = "- **11:40:22** · P-0102 · Backend · codex-cli/gpt-5 · #prompt · «Implementar el almacén de evidencias»\n"
            + "  - Resultado P-0102 · 12:05:10 · Succeeded · 1.488,0 s · ejecución `20261015-a1b2c3` · tarea `T2` · intento 1";
        var entry = new MemoryEntry("mem-0123456789abcdef", MemoryKind.JournalEntry, Topic, Content, MemoryOrigin.System, "bitacora:2026-10-15#sha256:" + HashA, T0, MemoryContent.Sha256(MemoryKind.JournalEntry, Topic, Content), MemoryTrust.Trusted, null, null);
        var twin = entry with { Id = "mem-fedcba9876543210" };

        var fragment = Assert.Single(FragmentCutter.Of(entry));

        // The words search the whole text (the date, the number, who, the run); the meaning is computed from the view.
        Assert.Equal($"{Topic}: {Content}", fragment.Text);
        Assert.Equal("Implementar el almacén de evidencias", FragmentCutter.MeaningOf(entry, fragment));
        Assert.Equal(FragmentSource.Memory(entry.Id, entry.ContentSha256), fragment.Source);
        Assert.Equal(fragment, FragmentCutter.Of(entry with { })[0]);
        Assert.NotEqual(fragment.Sha256, Assert.Single(FragmentCutter.Of(twin)).Sha256);
    }

    [Fact]
    public void The_meaning_of_any_other_entry_is_its_text()
    {
        const string Content = "- **11:40:22** · P-0102 · Backend · «no es la bitácora»";
        var entry = new MemoryEntry("mem-0123456789abcdef", MemoryKind.Change, "Cambio", Content, MemoryOrigin.System, "nota:1", T0, MemoryContent.Sha256(MemoryKind.Change, "Cambio", Content), MemoryTrust.Trusted, null, null);

        var fragment = Assert.Single(FragmentCutter.Of(entry));

        Assert.Equal($"Cambio: {Content}", fragment.Text);
        Assert.Equal(fragment.Text, FragmentCutter.MeaningOf(entry, fragment));
    }

    [Fact]
    public void Every_piece_of_a_long_journal_entry_has_the_view_of_its_own_piece_as_its_meaning()
    {
        const string Topic = "Bitácora del 2026-10-15 · P-0103";
        var lines = Enumerable.Range(1, 40).Select(i => $"  - Hecho: paso {i} del plan de migración de la memoria, con sus pruebas en verde y su revisión hecha.");
        var content = "- **10:05:31** · P-0103 · Documentador · gemini-cli/gemini-2.5-pro · #prompt · «Migrar la memoria»\n" + string.Join("\n", lines);
        var entry = new MemoryEntry("mem-0123456789abcdef", MemoryKind.JournalEntry, Topic, content, MemoryOrigin.System, "bitacora:2026-10-15#sha256:" + HashA, T0, MemoryContent.Sha256(MemoryKind.JournalEntry, Topic, content), MemoryTrust.Trusted, null, null);

        var fragments = FragmentCutter.Of(entry);

        Assert.True(fragments.Count > 1, $"{fragments.Count} fragments");
        Assert.All(fragments, f => Assert.StartsWith(Topic + ": ", f.Text, StringComparison.Ordinal));
        Assert.Equal(JournalSearchText.Of(fragments[0].Text[(Topic.Length + 2)..]), FragmentCutter.MeaningOf(entry, fragments[0]));
        Assert.StartsWith("Migrar la memoria\npaso 1 del plan", FragmentCutter.MeaningOf(entry, fragments[0]), StringComparison.Ordinal);
        Assert.All(fragments.Skip(1), f => Assert.StartsWith("paso ", FragmentCutter.MeaningOf(entry, f), StringComparison.Ordinal));
    }
}
