using AgentStudio.Desktop.Graph;
using AgentStudio.Tests.Desktop.Graph;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of the «Grafo» section of the document Auto Graph (Phase 6, docs/20 section 6.4; WP-G): the graph of a small solution with a partial project, a
/// relation read from the files, one an AI proposed and a gap, with a project chosen (its tasks) and a namespace chosen (its files), and the card of the OK of
/// «Proponer relaciones». The synthetic data carries no words (names of projects and namespaces are identifiers), as the guard of the language asks.
/// </summary>
internal static class UiScreensGraph
{
    private static async Task<GraphDocumentViewModel> LoadedAsync()
    {
        var backend = new GraphBackend(new GraphDesktopFakes.Analysis(), new GraphDesktopFakes.History(), new GraphDesktopFakes.Inference(GraphDesktopFakes.Proposed), new GraphDesktopFakes.Tasks(), new GraphDesktopFakes.Proposer());
        var section = new GraphMapSection(backend, new FakeTimeProvider(T0));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(GraphDesktopFakes.Project, CancellationToken.None);
        return document;
    }

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Graph · projects", async () =>
        {
            var document = await LoadedAsync();
            var section = (GraphMapSection)document.Sections[0];
            section.SelectedNode = section.Nodes.First(n => n.Id == "src/Core/Core.csproj");
            await Task.Delay(100);
            return UiScreens.Document(document);
        }, 1180, 1700),
        UiScreens.ScreenAsync("Graph · namespaces and a namespace chosen", async () =>
        {
            var document = await LoadedAsync();
            var section = (GraphMapSection)document.Sections[0];
            section.SelectedNode = section.Nodes.First(n => n.Id == "src/Core/Core.csproj");
            section.ToggleExpandCommand.Execute(null);
            section.ShowPackages = true;
            section.SelectedNode = section.Nodes.First(n => n.Id == "src/Core/Core.csproj|Calc.Core");
            return UiScreens.Document(document);
        }, 1180, 1700),
        UiScreens.ScreenAsync("Graph · consent of the proposal", async () =>
        {
            var document = await LoadedAsync();
            var section = (GraphMapSection)document.Sections[0];
            await section.ProposeCommand.ExecuteAsync(null);
            return UiScreens.Document(document);
        }, 1180, 1700),
        UiScreens.ScreenAsync("Graph · not available", async () =>
        {
            var section = new GraphMapSection(GraphBackend.None);
            var document = new GraphDocumentViewModel([section]);
            await document.SetProjectAsync(GraphDesktopFakes.Project, CancellationToken.None);
            return UiScreens.Document(document);
        }, 1180, 700),
    ];
}
