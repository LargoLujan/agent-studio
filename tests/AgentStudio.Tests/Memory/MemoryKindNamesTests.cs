using System.IO;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Export;
using AgentStudio.Memory.Semantic;
using AgentStudio.Memory.Store;
using AgentStudio.Tests.Memory.Store;
using AgentStudio.Desktop.Documents;
using AgentStudio.Application.Localization;

namespace AgentStudio.Tests.Memory;

/// <summary>
/// Every category of memory has its name where the memory writes it: the summary of its entry in the log, the label of a
/// result of the semantic index and, when it is exported, the title of its file. A category appended without them would make
/// recording an entry of it throw (Phase 13 added <see cref="MemoryKind.ProjectBrief"/>, docs/17 D-10).
/// </summary>
public sealed class MemoryKindNamesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<MemoryKind> Kinds() => [.. Enum.GetValues<MemoryKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_category_has_its_name_in_the_log_in_the_index_and_in_its_exported_file(MemoryKind kind)
    {
        Assert.False(string.IsNullOrWhiteSpace(MemoryLabels.Kind(kind)));
        Assert.False(string.IsNullOrWhiteSpace(SemanticLabels.Of(kind)));
        if (MemoryKinds.IsExported(kind))
        {
            var renderer = new ResourceUserTextRenderer();
            foreach (var language in new[] { "es-ES", "en" })
            {
                Assert.DoesNotMatch(@"^\[", renderer.Render(MemoryMarkdownExporter.TitleOf(kind), language));
                Assert.DoesNotMatch(@"^\[", renderer.Render(new UserText(MemoryMarkdownExporter.KindLabel(kind)), language));
            }

            Assert.StartsWith("tema-", AgentStudioFiles.ThematicMemoryFile(kind), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_brief_of_a_project_is_recorded_and_exported_with_its_name()
    {
        using var world = await MemoryWorld.CreateAsync();

        var brief = await world.Memory.RecordAsync(
            world.Project,
            new MemoryDraft(MemoryKind.ProjectBrief, ProjectBriefs.Topic, "Calculadora de consola en .NET.", MemoryOrigin.User, ProjectBriefs.Source),
            Ct);
        var result = await new MemoryMarkdownExporter(world.Memory, new ProjectStorageFiles(), world.Activity, world.Redactor, world.Clock, new ResourceUserTextRenderer()).ExportAsync(world.Project, "es-ES", Ct);

        Assert.Equal(MemoryKind.ProjectBrief, brief.Kind);
        Assert.True(result.Exported);
        var file = File.ReadAllText(Path.Combine(world.DataDirectory, "memory", "tema-ficha-del-proyecto.md"), Encoding.UTF8);
        Assert.Contains("# Memoria del proyecto · Ficha del proyecto", file, StringComparison.Ordinal);
        Assert.Contains("Calculadora de consola en .NET.", file, StringComparison.Ordinal);
        Assert.Equal("ficha del proyecto", SemanticLabels.Of(MemoryKind.ProjectBrief));
    }
}
