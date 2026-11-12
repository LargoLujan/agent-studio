namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of the «Grafo» section of the document Auto Graph (Phase 6, docs/20 section 6.4; WP-G). The P0 creates the list empty and adds it
/// to the aggregator once: WP-G adds its <c>Screen(...)</c> here and does not touch <c>UiScreens</c>.
/// </summary>
internal static class UiScreensGraph
{
    public static IReadOnlyList<UiScreen> All { get; } = [];
}
