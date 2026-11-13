using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Tests.Memory.Diagrams;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of the «Diagramas» and «Discrepancias» sections of the document Auto Graph (Phase 6, docs/20 section 6.4; WP-D): the list of diagrams with each state, the text,
/// the question about a file that was changed outside, the discrepancies of every kind with their actions, the result that is not calculated yet and the sections that are not
/// available. The synthetic data carries no words (names of projects, paths and identifiers); the text of a diagram is content, not text of the interface.
/// </summary>
internal static class UiScreensDocs
{
    private static async Task<GraphDocumentViewModel> DiagramsAsync(bool conflict, bool select = true)
    {
        using var world = new DiagramFakes.World();
        var section = new DocsDiagramsSection(new DocsBackend(world.Catalog, world.Catalog, world.Analysis, world.History, new Clipboard()));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(world.Project, CancellationToken.None);

        // The plan is generated and exported, the sequence only generated, and the solution generated and then out of date: the three states and the copy in the repository.
        await world.Catalog.GenerateAsync(world.Project, AgentStudio.Application.Diagrams.DiagramKind.RunPlan, DiagramFixtures.Run, CancellationToken.None);
        await world.Catalog.GenerateAsync(world.Project, AgentStudio.Application.Diagrams.DiagramKind.RunSequence, DiagramFixtures.Run, CancellationToken.None);
        await world.Catalog.GenerateAsync(world.Project, AgentStudio.Application.Diagrams.DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, new DiagramExportRequest([new DiagramRef(AgentStudio.Application.Diagrams.DiagramKind.RunPlan, DiagramFixtures.Run)], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask), CancellationToken.None);
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        await section.RefreshCommand.ExecuteAsync(null);
        if (select)
        {
            section.SelectedRow = section.Rows.First(r => r.Diagram == new DiagramRef(AgentStudio.Application.Diagrams.DiagramKind.RunSequence, DiagramFixtures.Run));
            await section.SourceShown;
        }

        if (conflict)
        {
            section.SelectedRow = section.Rows.First(r => r.Diagram == new DiagramRef(AgentStudio.Application.Diagrams.DiagramKind.RunPlan, DiagramFixtures.Run));
            await section.SourceShown;
            await File.WriteAllTextAsync(Path.Combine(world.Root, "docs", "generado", "diagramas", "plan-run-golden.md"), "changed outside\n");
            await section.ExportCommand.ExecuteAsync(null);
        }

        return document;
    }

    private static async Task<GraphDocumentViewModel> DiscrepanciesAsync(bool saved)
    {
        using var world = new DiagramFakes.World();
        world.Analysis.Cached = saved;
        world.Analysis.Findings =
        [
            new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/a.md", 3, [DoctorFact.Of("target", "b/c.md"), DoctorFact.Of("reason", "missing")]),
            new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/a.md", 9, [DoctorFact.Of("target", "../x.md"), DoctorFact.Of("reason", "outside")]),
            new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, "docs/adr/0001-a.md", 7, [DoctorFact.Of("cited", "src/x/Y.cs")]),
            new ConsistencyFinding(
                ConsistencyCodes.RequirementUncovered,
                null,
                null,
                [DoctorFact.Of("milestone", "M1"), DoctorFact.Of("milestone-id", "mil-1"), DoctorFact.Of("requirement", "R1"), DoctorFact.Of("title", "R1 title")]),
            new ConsistencyFinding(ConsistencyCodes.CheckFailed, null, null, [DoctorFact.Of("check", ConsistencyCodes.RequirementUncovered)]),
        ];
        await world.Catalog.GenerateAsync(world.Project, AgentStudio.Application.Diagrams.DiagramKind.Solution, string.Empty, CancellationToken.None);
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        var section = new DocsDiscrepanciesSection(new DocsBackend(world.Catalog, world.Catalog, world.Analysis, world.History));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(world.Project, CancellationToken.None);
        return document;
    }

    private sealed class Clipboard : IDocsClipboard
    {
        public void Copy(string text)
        {
        }
    }

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Docs · diagrams with the three states and the text", async () => UiScreens.Document(await DiagramsAsync(conflict: false)), 1180, 1500),
        UiScreens.ScreenAsync("Docs · export that asks about a file changed outside", async () => UiScreens.Document(await DiagramsAsync(conflict: true)), 1180, 1700),
        UiScreens.ScreenAsync("Docs · discrepancies of every kind", async () => UiScreens.Document(await DiscrepanciesAsync(saved: true)), 1180, 1500),
        UiScreens.ScreenAsync("Docs · discrepancies not calculated yet", async () => UiScreens.Document(await DiscrepanciesAsync(saved: false)), 1180, 700),
        UiScreens.ScreenAsync("Docs · diagrams not available", async () =>
        {
            var document = new GraphDocumentViewModel([new DocsDiagramsSection(DocsBackend.None)]);
            await document.SetProjectAsync(DiagramFakes.Project(@"C:\work\none"), CancellationToken.None);
            return UiScreens.Document(document);
        }, 1180, 600),
        UiScreens.ScreenAsync("Docs · discrepancies not available", async () =>
        {
            var document = new GraphDocumentViewModel([new DocsDiscrepanciesSection(DocsBackend.None)]);
            await document.SetProjectAsync(DiagramFakes.Project(@"C:\work\none"), CancellationToken.None);
            return UiScreens.Document(document);
        }, 1180, 600),
    ];
}
