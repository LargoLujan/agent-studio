using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;

namespace AgentStudio.Tests.Application;

/// <summary>
/// V21: the files Agent Studio keeps in a project's <c>.agentstudio</c> folder, by the exact names its writers give them,
/// tell its own uncommitted files from the user's (the notices of a run, a debate and an audit read the same answer).
/// </summary>
public sealed class AgentStudioFilesTests
{
    [Fact]
    public void The_memory_export_has_one_file_per_category_and_one_per_month_and_nothing_else()
    {
        Assert.Equal(Enum.GetValues<MemoryKind>().Count(MemoryKinds.IsExported), AgentStudioFiles.ThematicMemoryFiles.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(11, AgentStudioFiles.ThematicMemoryFiles.Count);
        Assert.Equal("tema-errores-conocidos.md", AgentStudioFiles.ThematicMemoryFile(MemoryKind.KnownError));
        Assert.Equal("tema-ficha-del-proyecto.md", AgentStudioFiles.ThematicMemoryFile(MemoryKind.ProjectBrief));
        Assert.Equal("cronologico-2026-10.md", AgentStudioFiles.ChronologicalMemoryFile(new DateOnly(2026, 10, 1)));
        Assert.All(AgentStudioFiles.ThematicMemoryFiles, name => Assert.True(AgentStudioFiles.IsMemoryExportFile(name), name));

        Assert.True(AgentStudioFiles.IsMemoryExportFile("cronologico-2026-10.md"));
        Assert.False(AgentStudioFiles.IsMemoryExportFile("tema-notas.md"));
        Assert.False(AgentStudioFiles.IsMemoryExportFile("cronologico-2026-13.md"));
        Assert.False(AgentStudioFiles.IsMemoryExportFile("Tema-veredictos.md"));
        Assert.False(AgentStudioFiles.IsMemoryExportFile("tema-veredictos.md.bak"));
    }

    /// <summary>
    /// Phase 12 (docs/17 D-02): a journal entry anchored as memory is not exported, since its source already is the journal
    /// in the repository; it has no thematic file, and nothing under the memory folder is taken for one.
    /// </summary>
    [Fact]
    public void The_journal_entries_have_no_file_in_the_memory_export()
    {
        Assert.False(MemoryKinds.IsExported(MemoryKind.JournalEntry));
        Assert.All(Enum.GetValues<MemoryKind>().Where(k => k != MemoryKind.JournalEntry), kind => Assert.True(MemoryKinds.IsExported(kind), kind.ToString()));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentStudioFiles.ThematicMemoryFile(MemoryKind.JournalEntry));
        Assert.False(AgentStudioFiles.IsMemoryExportFile("tema-bitacora.md"));
    }

    [Theory]
    [InlineData("?? .agentstudio/bitacora/2026-10-29.md")]
    [InlineData(" M .agentstudio/bitacora/2026-10-30.md")]
    [InlineData("?? .agentstudio/memory/cronologico-2026-10.md")]
    [InlineData("?? .agentstudio/memory/tema-errores-conocidos.md")]
    [InlineData(" M .agentstudio/memory/tema-veredictos.md")]
    [InlineData("A  .agentstudio/memory/tema-cambios.md")]
    [InlineData("?? .agentstudio/agentstudio.db-wal")]
    [InlineData("?? .agentstudio/evidence/ab/cd.bin")]
    [InlineData("?? .agentstudio/tmp/x.tmp")]
    [InlineData("?? .agentstudio/verification.json")]
    [InlineData("?? .agentstudio/canvas-layout.json")]
    public void What_Agent_Studio_keeps_in_its_folder_is_its_own(string line)
    {
        Assert.True(AgentStudioFiles.IsAgentStudioStatusLine(line), line);
    }

    [Theory]
    [InlineData(" M src/Calc/Calc.cs")]
    [InlineData("?? notas.txt")]
    [InlineData("?? .agentstudio/memory/tema-notas.md")]
    [InlineData("?? .agentstudio/memory/mis-notas.md")]
    [InlineData("?? .agentstudio/bitacora/notas.md")]
    [InlineData("?? .agentstudio/otra-cosa.json")]
    [InlineData(" D .agentstudio/memory/tema-veredictos.md")]
    [InlineData(" D .agentstudio/bitacora/2026-10-29.md")]
    [InlineData("R  .agentstudio/bitacora/2026-10-29.md -> .agentstudio/bitacora/2026-10-30.md")]
    [InlineData("?? .agentstudio/memory/sub/tema-veredictos.md")]
    [InlineData("?? apps/calc/.agentstudio/bitacora/2026-10-29.md")]
    [InlineData("??")]
    public void Anything_else_is_a_change_of_the_user(string line)
    {
        Assert.False(AgentStudioFiles.IsAgentStudioStatusLine(line), line);
    }

    [Fact]
    public void A_project_in_a_folder_of_the_repository_keeps_its_files_in_its_own_folder()
    {
        Assert.True(AgentStudioFiles.IsAgentStudioStatusLine("?? apps/calc/.agentstudio/bitacora/2026-10-29.md", "apps/calc/"));
        Assert.False(AgentStudioFiles.IsAgentStudioStatusLine("?? .agentstudio/bitacora/2026-10-29.md", "apps/calc/"));
    }

    [Fact]
    public void A_day_of_the_journal_is_a_real_date()
    {
        Assert.Equal("2026-10-30.md", AgentStudioFiles.JournalDayFile(new DateOnly(2026, 10, 30)));
        Assert.True(AgentStudioFiles.IsJournalDay("2026-10-30.md"));
        Assert.False(AgentStudioFiles.IsJournalDay("2026-02-30.md"));
        Assert.False(AgentStudioFiles.IsJournalDay("notas.md"));
    }
}
