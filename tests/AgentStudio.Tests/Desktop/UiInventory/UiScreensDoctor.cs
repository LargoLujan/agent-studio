namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of the panel of Auto Doctor (Phase 6, docs/20 section 6.1; WP-A). The P0 creates the list empty and adds it to the aggregator
/// once: WP-A adds its <c>Screen(...)</c> here and does not touch <c>UiScreens</c>, so the guard <c>Every_view_of_the_app_is_walked</c> keeps
/// passing as its views appear.
/// </summary>
internal static class UiScreensDoctor
{
    public static IReadOnlyList<UiScreen> All { get; } = [];
}
